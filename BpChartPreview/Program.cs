using BpApp;
using BpCore;
using ScottPlot;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = new("ru-RU");
var dir = args[0]; var outDir = args[1];
var all = new List<Measurement>();
foreach (var f in Directory.GetFiles(dir, "*.awp")) Archive.Merge(all, AwpReader.Read(f));
var s = new AnalysisSettings();
var series = Analysis.Analyze(all, s);
var last = series[^1].End;

void Render(string name, ChartMode mode, IReadOnlyCollection<Series> sel, DateTime from, DateTime to)
{
    var plot = new Plot();
    ChartBuilder.Build(plot, series, s, mode, sel);
    ChartBuilder.Zoom(plot, series, s, from, to);
    plot.SavePng(Path.Combine(outDir, name), 1400, 700);
    Console.WriteLine(name);
}
Render("1_all_2weeks.png", ChartMode.All, null, last.AddDays(-14), last.AddHours(6));
Render("2_averages_all.png", ChartMode.AveragesOnly, null, series[0].Start.AddDays(-1), last.AddDays(1));
var sel = series.First(x => x.Unreliable);
Render("3_selected_zoom.png", ChartMode.All, new[] { sel }, sel.Start.AddHours(-1), sel.End.AddHours(1));
Render("4_measurements.png", ChartMode.MeasurementsOnly, null, last.AddDays(-3), last.AddHours(6));
var multi = new[] { series[^1], series[^4], series[^7] };
var mp = new Plot();
ChartBuilder.Build(mp, series, s, ChartMode.All, multi);
ChartBuilder.ZoomToSelection(mp, series, s, multi);
mp.SavePng(Path.Combine(outDir, "5_multi_select.png"), 1400, 700);
Console.WriteLine("5_multi_select.png");
