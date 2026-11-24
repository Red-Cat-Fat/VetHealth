namespace BpCore;

/// <summary>Настройки анализа. Значения по умолчанию совпадают с прежним скриптом на Python.</summary>
public sealed class AnalysisSettings
{
    /// <summary>Пауза в минутах, после которой начинается новая серия.</summary>
    public int GapMinutes { get; set; } = 10;

    // Сбой аппарата: SYS/DIA слишком низкие или разница SYS-DIA неправдоподобна
    public int SysMin { get; set; } = 85;
    public int DiaMin { get; set; } = 50;
    public int PulsePressureMin { get; set; } = 20;
    public int PulsePressureMax { get; set; } = 80;

    // Выброс: отличие от медианы серии больше чем на столько
    public int OutlierSys { get; set; } = 20;
    public int OutlierDia { get; set; } = 15;

    // Подсветка повышенного давления
    public int HighSys { get; set; } = 140;
    public int HighDia { get; set; } = 90;
}

/// <summary>Серия замеров подряд и её средние.</summary>
public sealed class Series
{
    public int No { get; init; }
    public List<Measurement> Items { get; init; } = new();
    public List<Measurement> Used => Items.Where(m => m.Excluded == Exclusion.None).ToList();

    public DateTime Start => Items[0].Time;
    public DateTime End => Items[^1].Time;
    public DateTime Middle => Start + TimeSpan.FromTicks((End - Start).Ticks / 2);
    public DateTime Date => Start.Date;

    public double SysMean { get; set; }
    public double DiaMean { get; set; }
    public double PulseMean { get; set; }
    public double MapMean { get; set; }
    public double SysClean { get; set; }
    public double DiaClean { get; set; }
    public double PulseClean { get; set; }
    public double SysMedian { get; set; }
    public double DiaMedian { get; set; }
    public bool Unreliable { get; set; }
    public List<string> Notes { get; } = new();
    public string NotesText => string.Join("; ", Notes);

    public string SysRange => $"{Items.Min(m => m.Sys)}–{Items.Max(m => m.Sys)}";
    public string DiaRange => $"{Items.Min(m => m.Dia)}–{Items.Max(m => m.Dia)}";
}

/// <summary>Итог за день: среднее из «чистых» средних серий.</summary>
public sealed record DaySummary(DateTime Date, int SeriesCount, int MeasurementCount,
    double Sys, double Dia, double Pulse, double SysMinSeries, double SysMaxSeries);

public static class Analysis
{
    public static bool IsGlitch(Measurement m, AnalysisSettings s)
    {
        int pp = m.Sys - m.Dia;
        return m.Sys < s.SysMin || m.Dia < s.DiaMin || pp <= s.PulsePressureMin || pp >= s.PulsePressureMax;
    }

    /// <summary>Сортирует замеры, режет на серии, помечает сбои и выбросы, считает средние.</summary>
    public static List<Series> Analyze(IEnumerable<Measurement> measurements, AnalysisSettings s)
    {
        var list = measurements.OrderBy(m => m.Time).ToList();
        var result = new List<Series>();
        var cur = new List<Measurement>();
        foreach (var m in list)
        {
            if (cur.Count > 0 && m.Time - cur[^1].Time > TimeSpan.FromMinutes(s.GapMinutes))
            {
                result.Add(Summarize(result.Count + 1, cur, s));
                cur = new List<Measurement>();
            }
            cur.Add(m);
        }
        if (cur.Count > 0) result.Add(Summarize(result.Count + 1, cur, s));
        return result;
    }

    static Series Summarize(int no, List<Measurement> items, AnalysisSettings s)
    {
        var ser = new Series { No = no, Items = items };
        foreach (var m in items)
        {
            m.SeriesNo = no;
            m.Excluded = IsGlitch(m, s) ? Exclusion.Glitch : Exclusion.None;
        }

        var ok = items.Where(m => m.Excluded == Exclusion.None).ToList();
        if (ok.Count == 0)
        {
            // Все замеры выглядят как сбой: считать всё равно не из чего, берём все
            foreach (var m in items) m.Excluded = Exclusion.None;
            ok = items.ToList();
        }

        List<Measurement> clean;
        if (ok.Count >= 3)
        {
            double ms = Median(ok.Select(m => (double)m.Sys));
            double md = Median(ok.Select(m => (double)m.Dia));
            clean = ok.Where(m => Math.Abs(m.Sys - ms) <= s.OutlierSys && Math.Abs(m.Dia - md) <= s.OutlierDia).ToList();
            if (clean.Count * 2 < ok.Count)
                clean = ok; // «выбросов» больше половины: разброс сплошной, отбрасывать нечего
            foreach (var m in ok)
                if (!clean.Contains(m)) m.Excluded = Exclusion.Outlier;
        }
        else clean = ok;

        ser.SysMean = Avg(items, m => m.Sys);
        ser.DiaMean = Avg(items, m => m.Dia);
        ser.PulseMean = Avg(items, m => m.Pulse);
        ser.MapMean = Avg(items, m => m.Map);
        ser.SysClean = Avg(clean, m => m.Sys);
        ser.DiaClean = Avg(clean, m => m.Dia);
        ser.PulseClean = Avg(clean, m => m.Pulse);
        ser.SysMedian = Median(items.Select(m => (double)m.Sys));
        ser.DiaMedian = Median(items.Select(m => (double)m.Dia));

        int n = items.Count;
        if (n < 3) ser.Notes.Add("мало замеров");
        int dropped = n - clean.Count;
        if (dropped > 0) ser.Notes.Add($"не учтено замеров: {dropped}");
        if (n >= 3 && (clean.Count * 2 < n || clean.Max(m => m.Sys) - clean.Min(m => m.Sys) > 25))
        {
            ser.Unreliable = true;
            ser.Notes.Insert(0, "серия ненадёжная");
        }
        if (items.Max(m => m.Sys) - items.Min(m => m.Sys) > 25) ser.Notes.Add("большой разброс SYS");
        return ser;
    }

    public static List<DaySummary> ByDay(IEnumerable<Series> series) =>
        series.GroupBy(x => x.Date).OrderBy(g => g.Key).Select(g => new DaySummary(
            g.Key, g.Count(), g.Sum(x => x.Items.Count),
            Math.Round(g.Average(x => x.SysClean), 1), Math.Round(g.Average(x => x.DiaClean), 1),
            Math.Round(g.Average(x => x.PulseClean), 1),
            g.Min(x => x.SysClean), g.Max(x => x.SysClean))).ToList();

    static double Avg(List<Measurement> l, Func<Measurement, int> f) => Math.Round(l.Average(m => (double)f(m)), 1);

    public static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        int k = a.Length / 2;
        return a.Length % 2 == 1 ? a[k] : (a[k - 1] + a[k]) / 2;
    }
}
