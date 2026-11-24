using HidSharp;

namespace BpCore;

/// <summary>
/// Обмен с тонометром CONTEC08A по USB HID (VID 0483, PID 5750).
/// Протокол восстановлен по записи обмена программы BPMeter с аппаратом (2026-11-23):
///   ПК → аппарат: 40 8F FF FE FD FC   приветствие
///                 42 8F FF FE FD FC   информация об аппарате
///                 43 41 01 04 02      выдать замеры
///   аппарат → ПК: 4A 43 01            подтверждение
///                 46 41 ...           заголовок, в нём число замеров
///                 по одному замеру в отчёте, 14 байт:
///                 [флаги] [0] SYS DIA пульс MAP год-2000 месяц [0] день час минута [0] [0x31]
/// У всех байтов данных установлен старший бит; настоящий старший бит байта N
/// хранится в бите (N-1) байта флагов (так передаются значения больше 127).
/// </summary>
public static class ContecProtocol
{
    public const int Vid = 0x0483, Pid = 0x5750;
    public static readonly byte[] CmdHello = { 0x40, 0x8F, 0xFF, 0xFE, 0xFD, 0xFC };
    public static readonly byte[] CmdInfo = { 0x42, 0x8F, 0xFF, 0xFE, 0xFD, 0xFC };
    public static readonly byte[] CmdRead = { 0x43, 0x41, 0x01, 0x04, 0x02 };

    /// <summary>Снимает кодировку «байт флагов + 7-битные данные»; p[start] — байт флагов.</summary>
    public static int[] Unpack(byte[] p, int start = 0, int n = 14)
    {
        var v = new int[n];
        int flags = p[start] & 0x7F;
        v[0] = p[start];
        for (int k = 1; k < n && start + k < p.Length; k++)
            v[k] = (p[start + k] & 0x7F) | (((flags >> (k - 1)) & 1) != 0 ? 0x80 : 0);
        return v;
    }

    public static Measurement DecodeRecord(byte[] p)
    {
        var v = Unpack(p);
        int sys = v[2], dia = v[3], pr = v[4], map = v[5];
        int yy = v[6], mo = v[7], dd = v[9], hh = v[10], mi = v[11];
        if (sys == 0 || dia == 0) return null;
        try
        {
            return new Measurement { Time = new DateTime(2000 + yy, mo, dd, hh, mi, 0), Sys = sys, Dia = dia, Map = map, Pulse = pr, Source = "аппарат" };
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static int HeaderCount(byte[] p)
    {
        var v = Unpack(p, 2, 12);
        return v[1] * 256 + v[2];
    }

    /// <summary>Разбирает поток отчётов от аппарата после команды CmdRead.</summary>
    public static (int? count, List<Measurement> items) ParseStream(IEnumerable<byte[]> packets)
    {
        int? count = null;
        var items = new List<Measurement>();
        bool started = false;
        foreach (var p in packets)
        {
            if (!started)
            {
                if (p.Length > 1 && p[0] == 0x46 && p[1] == 0x41) { count = HeaderCount(p); started = true; }
                continue;
            }
            if ((p[0] & 0x80) != 0)
            {
                var m = DecodeRecord(p);
                if (m != null) items.Add(m);
            }
            if (count.HasValue && items.Count >= count) break;
        }
        return (count, items);
    }
}

public sealed class DeviceException : Exception
{
    public DeviceException(string message) : base(message) { }
}

/// <summary>Загрузка замеров с аппарата.</summary>
public static class ContecDevice
{
    public static bool IsConnected() => DeviceList.Local.GetHidDevices(ContecProtocol.Vid, ContecProtocol.Pid).Any();

    public static (int? count, List<Measurement> items) Download(Action<string> log = null, int idleTimeoutMs = 3000)
    {
        var dev = DeviceList.Local.GetHidDevices(ContecProtocol.Vid, ContecProtocol.Pid).FirstOrDefault()
                  ?? throw new DeviceException("Аппарат не найден. Подключите его по USB и включите.");
        log?.Invoke($"Найден аппарат: {SafeName(dev)}");
        if (!dev.TryOpen(out HidStream stream))
            throw new DeviceException("Не удалось открыть аппарат. Закройте BPMeter и попробуйте снова.");

        using (stream)
        {
            int outLen = Math.Max(dev.GetMaxOutputReportLength(), 65);
            int inLen = Math.Max(dev.GetMaxInputReportLength(), 65);

            void Send(byte[] cmd)
            {
                var buf = new byte[outLen]; // buf[0] = 0: номер отчёта
                Array.Copy(cmd, 0, buf, 1, cmd.Length);
                stream.Write(buf);
            }

            byte[] Recv(int ms)
            {
                stream.ReadTimeout = ms;
                var buf = new byte[inLen];
                try
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 1) return null;
                    return buf[1..n]; // без номера отчёта
                }
                catch (TimeoutException) { return null; }
            }

            void Drain() { while (Recv(50) != null) { } }

            foreach (var cmd in new[] { ContecProtocol.CmdHello, ContecProtocol.CmdInfo })
            {
                Send(cmd);
                Recv(1000);
                Drain();
            }

            log?.Invoke("Скачиваю замеры…");
            Send(ContecProtocol.CmdRead);
            var packets = new List<byte[]>();
            var last = DateTime.UtcNow;
            while ((DateTime.UtcNow - last).TotalMilliseconds < idleTimeoutMs)
            {
                var p = Recv(200);
                if (p == null) continue;
                packets.Add(p);
                last = DateTime.UtcNow;
                var (cnt, items) = ContecProtocol.ParseStream(packets);
                if (cnt.HasValue && items.Count >= cnt) break;
            }
            return ContecProtocol.ParseStream(packets);
        }
    }

    static string SafeName(HidDevice d)
    {
        try { return d.GetProductName(); } catch { return "CONTEC"; }
    }
}
