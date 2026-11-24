// Проверка ядра на реальных данных: запуск `dotnet run --project BpTests -- <папка с образцами>`.
// В папке: 012611060558.awp, 012611231210.awp, kotovsky1.pdf, kotovsky2.pdf,
// device_packets.txt (hex-строки отчётов аппарата из записи USB), python_series.json (эталон прежнего скрипта).
using System.Text.Json;
using BpCore;

var dir = args.Length > 0 ? args[0] : ".";
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}"); if (!ok) failures++; }
bool Same(Measurement a, Measurement b) => a.Time == b.Time && a.Sys == b.Sys && a.Dia == b.Dia && a.Map == b.Map && a.Pulse == b.Pulse;

var awp1 = AwpReader.Read(Path.Combine(dir, "012611060558.awp"));
var awp2 = AwpReader.Read(Path.Combine(dir, "012611231210.awp"));
Check(awp1.Count == 92 && awp2.Count == 100, $"awp: {awp1.Count} и {awp2.Count} замеров");
Check(awp2[0].ToString() == "2026-11-12 01:13 113/79 пульс 110", "awp: первый замер " + awp2[0]);

var pdf1 = PdfReportReader.Read(Path.Combine(dir, "kotovsky1.pdf"));
var pdf2 = PdfReportReader.Read(Path.Combine(dir, "kotovsky2.pdf"));
Check(pdf1.Count == 92 && pdf1.Zip(awp1).All(p => Same(p.First, p.Second)), $"pdf1 совпадает с awp ({pdf1.Count})");
Check(pdf2.Count == 100 && pdf2.Zip(awp2).All(p => Same(p.First, p.Second)), $"pdf2 совпадает с awp ({pdf2.Count})");

var packets = File.ReadAllLines(Path.Combine(dir, "device_packets.txt")).Select(Convert.FromHexString);
var (count, dev) = ContecProtocol.ParseStream(packets);
Check(count == 100 && dev.Count == 100 && dev.Zip(awp2).All(p => Same(p.First, p.Second)), $"аппарат: {dev.Count} из {count}, совпадает с awp");

var all = new List<Measurement>();
Archive.Merge(all, awp1); Archive.Merge(all, awp2);
int dup = Archive.Merge(all, pdf2);
Check(all.Count == 192 && dup == 0, "архив без повторов");
var tmp = Path.Combine(Path.GetTempPath(), "bp_test_archive.csv");
Archive.Save(tmp, all);
var back = Archive.Load(tmp);
Check(back.Count == 192 && back.OrderBy(m => m.Time).Zip(all.OrderBy(m => m.Time)).All(p => Same(p.First, p.Second)), "архив: запись и чтение");

var s = new AnalysisSettings();
var series = Analysis.Analyze(all, s);
var py = JsonSerializer.Deserialize<JsonElement[][]>(File.ReadAllText(Path.Combine(dir, "python_series.json")));
Check(series.Count == py.Length, $"серий: {series.Count} (python: {py.Length})");
int diff = 0;
for (int i = 0; i < Math.Min(series.Count, py.Length); i++)
{
    var x = series[i]; var p = py[i];
    var mine = $"{x.Items.Count} {x.SysMean} {x.DiaMean} {x.SysClean} {x.DiaClean} {x.PulseClean} {x.Used.Count} {x.NotesText}";
    var theirs = $"{p[0]} {p[1].GetDouble()} {p[2].GetDouble()} {p[3].GetDouble()} {p[4].GetDouble()} {p[5].GetDouble()} {p[6]} {p[7]}";
    if (mine != theirs) { diff++; Console.WriteLine($"   серия {x.No}: C# [{mine}] python [{theirs}]"); }
}
Check(diff == 0, "средние и пометки совпадают с прежним скриптом");

var xlsx = Path.Combine(Path.GetTempPath(), "bp_test.xlsx");
ExcelExport.Save(xlsx, series, s);
Check(new FileInfo(xlsx).Length > 5000, "Excel сохранён: " + xlsx);

Console.WriteLine(failures == 0 ? "Все проверки пройдены." : $"Ошибок: {failures}");
return failures == 0 ? 0 : 1;
