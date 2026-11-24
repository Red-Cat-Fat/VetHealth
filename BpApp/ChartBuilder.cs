using BpCore;
using ScottPlot;
using Color = ScottPlot.Color;
using Colors = ScottPlot.Colors;

namespace BpApp;

public enum ChartMode { All = 0, AveragesOnly = 1, MeasurementsOnly = 2 }

/// <summary>
/// Рисует график «давление по времени». Отдельно от окна, чтобы тот же код
/// рисовал картинку для Excel/PNG и проверялся без Windows.
/// </summary>
public static class ChartBuilder
{
    public static readonly Color SysColor = Color.FromHex("#C0392B");
    public static readonly Color DiaColor = Color.FromHex("#2E6DB4");
    public static readonly Color PulseColor = Color.FromHex("#7F8C8D");
    public static readonly Color ExcludedColor = Color.FromHex("#E67E22");

    public static void Build(Plot plot, List<Series> series, AnalysisSettings settings, ChartMode mode, IReadOnlyCollection<Series> selected)
    {
        plot.Clear();
        plot.Font.Automatic();
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Left.Label.Text = mode == ChartMode.MeasurementsOnly ? "давление, мм рт. ст.; пульс, уд/мин" : "давление, мм рт. ст.";
        plot.Legend.Alignment = Alignment.UpperLeft;
        plot.Title(series.Count == 0 ? "Нет данных" : "");
        if (series.Count == 0) return;

        var hs = plot.Add.HorizontalLine(settings.HighSys);
        hs.LinePattern = LinePattern.Dashed; hs.Color = SysColor.WithAlpha(0.35); hs.LineWidth = 1;
        var hd = plot.Add.HorizontalLine(settings.HighDia);
        hd.LinePattern = LinePattern.Dashed; hd.Color = DiaColor.WithAlpha(0.35); hd.LineWidth = 1;

        foreach (var sel in selected ?? Array.Empty<Series>())
        {
            var pad = TimeSpan.FromMinutes(Math.Max(3, (sel.End - sel.Start).TotalMinutes * 0.3));
            var span = plot.Add.HorizontalSpan((sel.Start - pad).ToOADate(), (sel.End + pad).ToOADate());
            span.FillStyle.Color = Color.FromHex("#F1C40F").WithAlpha(0.25);
            span.LineStyle.Width = 0;
            // Полоса постоянной ширины в пикселях: видна при любом масштабе
            var mark = plot.Add.VerticalLine(sel.Middle.ToOADate());
            mark.Color = Color.FromHex("#F1C40F").WithAlpha(0.45);
            mark.LineWidth = 12;
        }

        var all = series.SelectMany(s => s.Items).ToList();
        if (mode != ChartMode.AveragesOnly)
        {
            var used = all.Where(m => m.Excluded == Exclusion.None).ToList();
            var excl = all.Where(m => m.Excluded != Exclusion.None).ToList();
            double a = mode == ChartMode.MeasurementsOnly ? 0.9 : 0.45;
            AddMarkers(plot, used, m => m.Sys, SysColor.WithAlpha(a), MarkerShape.FilledCircle, 5,
                mode == ChartMode.MeasurementsOnly ? "SYS, замер" : "учтённые замеры");
            AddMarkers(plot, used, m => m.Dia, DiaColor.WithAlpha(a), MarkerShape.FilledCircle, 5,
                mode == ChartMode.MeasurementsOnly ? "DIA, замер" : null);
            AddMarkers(plot, excl, m => m.Sys, ExcludedColor, MarkerShape.Eks, 8, "не учтены (сбой/выброс)");
            AddMarkers(plot, excl, m => m.Dia, ExcludedColor, MarkerShape.Eks, 8, null);
        }
        if (mode != ChartMode.MeasurementsOnly)
        {
            var xs = series.Select(s => s.Middle.ToOADate()).ToArray();
            var sys = plot.Add.Scatter(xs, series.Select(s => s.SysClean).ToArray());
            sys.Color = SysColor; sys.LineWidth = 2; sys.MarkerSize = 9; sys.MarkerShape = MarkerShape.FilledDiamond;
            sys.LegendText = "SYS, среднее серии";
            var dia = plot.Add.Scatter(xs, series.Select(s => s.DiaClean).ToArray());
            dia.Color = DiaColor; dia.LineWidth = 2; dia.MarkerSize = 9; dia.MarkerShape = MarkerShape.FilledDiamond;
            dia.LegendText = "DIA, среднее серии";

            var bad = series.Where(s => s.Unreliable).ToList();
            if (bad.Count > 0)
            {
                var bx = bad.Select(s => s.Middle.ToOADate()).ToArray();
                var m1 = plot.Add.Markers(bx, bad.Select(s => s.SysClean).ToArray(), MarkerShape.OpenCircle, 18, Colors.Black);
                m1.LegendText = "ненадёжная серия";
                plot.Add.Markers(bx, bad.Select(s => s.DiaClean).ToArray(), MarkerShape.OpenCircle, 18, Colors.Black);
            }
        }
        if (mode == ChartMode.MeasurementsOnly)
            AddMarkers(plot, all, m => m.Pulse, PulseColor.WithAlpha(0.6), MarkerShape.FilledSquare, 4, "пульс");
        plot.ShowLegend();
    }

    static void AddMarkers(Plot plot, List<Measurement> items, Func<Measurement, int> y, Color color, MarkerShape shape, float size, string legend)
    {
        if (items.Count == 0) return;
        var m = plot.Add.Markers(items.Select(i => i.Time.ToOADate()).ToArray(), items.Select(i => (double)y(i)).ToArray(), shape, size, color);
        if (legend != null) m.LegendText = legend;
    }

    /// <summary>
    /// Подбирает видимую область так, чтобы все выбранные серии со всеми их замерами
    /// поместились на график. Одна серия показывается как раньше: ±36 часов вокруг неё.
    /// </summary>
    public static void ZoomToSelection(Plot plot, List<Series> series, AnalysisSettings settings, IReadOnlyCollection<Series> selected)
    {
        if (selected == null || selected.Count == 0) return;
        if (selected.Count == 1)
        {
            var s = selected.First();
            var half = TimeSpan.FromHours(36);
            Zoom(plot, series, settings, s.Middle - half, s.Middle + half);
            return;
        }
        var from = selected.Min(s => s.Start);
        var to = selected.Max(s => s.End);
        var pad = TimeSpan.FromMinutes(Math.Max(30, (to - from).TotalMinutes * 0.05));
        Zoom(plot, series, settings, from - pad, to + pad);
    }

    /// <summary>Показывает отрезок времени и подбирает шкалу давления по видимым замерам.</summary>
    public static void Zoom(Plot plot, List<Series> series, AnalysisSettings settings, DateTime from, DateTime to)
    {
        var items = series.SelectMany(s => s.Items).ToList();
        var visible = items.Where(m => m.Time >= from && m.Time <= to).ToList();
        if (visible.Count == 0) visible = items;
        double lo = visible.Count > 0 ? Math.Min(visible.Min(m => m.Dia), settings.HighDia) - 10 : 40;
        double hi = visible.Count > 0 ? Math.Max(visible.Max(m => m.Sys), settings.HighSys) + 10 : 200;
        plot.Axes.SetLimits(from.ToOADate(), to.ToOADate(), lo, hi);
    }
}
