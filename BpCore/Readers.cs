using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace BpCore;

/// <summary>Чтение замеров из файлов программы BPMeter (*.awp).</summary>
public static class AwpReader
{
    static readonly Regex Row = new(@"^(\d+)=([0-9A-Fa-f]{32,})\s*$", RegexOptions.Multiline);

    public const string DefaultDataDir = @"C:\BPMeter\data";

    /// <summary>
    /// Строка замера: N=07EA0B0C010D0000 7100 4F00 5C00 6E00 ...
    /// год (2 байта, старший первым), месяц, день, час, минута, 2 служебных байта,
    /// затем SYS, DIA, MAP, пульс по 2 байта (младший первым).
    /// </summary>
    public static List<Measurement> Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);
        var result = new List<Measurement>();
        foreach (Match m in Row.Matches(text))
        {
            var b = Convert.FromHexString(m.Groups[2].Value[..32]);
            int year = b[0] << 8 | b[1];
            if (year < 2000 || year > 2100) continue; // другой (старый) формат записи
            DateTime dt;
            try { dt = new DateTime(year, b[2], b[3], b[4], b[5], 0); }
            catch (ArgumentOutOfRangeException) { continue; }
            int U16(int i) => b[i] | b[i + 1] << 8;
            int sys = U16(8), dia = U16(10), map = U16(12), pr = U16(14);
            if (sys == 0 || dia == 0) continue; // неудачный замер
            result.Add(new Measurement { Time = dt, Sys = sys, Dia = dia, Map = map, Pulse = pr, Source = Path.GetFileName(path) });
        }
        return result;
    }
}

/// <summary>Чтение таблицы замеров из PDF-отчёта BPMeter.</summary>
public static class PdfReportReader
{
    static readonly Regex Row = new(
        @"^\s*(\d+)\s+(\d{4}/\d{1,2}/\d{1,2})\s+(\d{1,2}:\d{2})\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)");

    public static List<Measurement> Read(string path)
    {
        var result = new List<Measurement>();
        using var doc = PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            // Собираем слова в строки по вертикальной координате
            var lines = page.GetWords()
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            foreach (var line in lines)
            {
                var m = Row.Match(line);
                if (!m.Success) continue;
                var dt = DateTime.ParseExact($"{m.Groups[2].Value} {m.Groups[3].Value}", "yyyy/M/d H:mm", CultureInfo.InvariantCulture);
                int G(int i) => int.Parse(m.Groups[i].Value);
                // Порядок столбцов в отчёте: SYS MAP DIA PP PR
                result.Add(new Measurement { Time = dt, Sys = G(4), Map = G(5), Dia = G(6), Pulse = G(8), Source = Path.GetFileName(path) });
            }
        }
        return result;
    }
}

/// <summary>Накопительный архив всех замеров (CSV, тот же формат, что у прежнего скрипта).</summary>
public static class Archive
{
    const string Header = "дата;время;SYS;DIA;MAP;пульс";

    public static List<Measurement> Load(string path)
    {
        var result = new List<Measurement>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Encoding.UTF8).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 6) continue;
            if (!DateTime.TryParseExact($"{p[0]} {p[1]}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) continue;
            result.Add(new Measurement
            {
                Time = dt, Sys = int.Parse(p[2]), Dia = int.Parse(p[3]), Map = int.Parse(p[4]), Pulse = int.Parse(p[5]),
                Source = "архив",
            });
        }
        return result;
    }

    public static void Save(string path, IEnumerable<Measurement> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var m in items.OrderBy(m => m.Time))
            sb.AppendLine($"{m.Time:yyyy-MM-dd};{m.Time:HH:mm};{m.Sys};{m.Dia};{m.Map};{m.Pulse}");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true)); // с BOM: Excel открывает без «кракозябр»
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Добавляет новые замеры без повторов. Возвращает число добавленных.</summary>
    public static int Merge(List<Measurement> target, IEnumerable<Measurement> incoming)
    {
        var known = target.Select(m => m.Key).ToHashSet();
        int added = 0;
        foreach (var m in incoming)
            if (known.Add(m.Key)) { target.Add(m); added++; }
        return added;
    }
}
