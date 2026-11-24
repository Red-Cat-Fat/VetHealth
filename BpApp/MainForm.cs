using System.Diagnostics;
using System.Text;
using BpCore;
using ScottPlot;
using ScottPlot.WinForms;
using Color = ScottPlot.Color;
using Colors = ScottPlot.Colors;
using Label = System.Windows.Forms.Label;
using Orientation = System.Windows.Forms.Orientation;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;

namespace BpApp;

public sealed class MainForm : Form
{
    readonly AnalysisSettings settings = new();
    readonly string archivePath;
    List<Measurement> archive = new();
    List<Series> series = new();
    Series selected;

    // Элементы окна
    readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4) };
    readonly ToolStripButton btnDevice = new("Скачать с аппарата");
    readonly ToolStripButton btnAwp = new("Импорт из BPMeter");
    readonly ToolStripButton btnPdf = new("Импорт PDF…");
    readonly ToolStripButton btnExcel = new("Сохранить Excel…");
    readonly ToolStripButton btnPng = new("Сохранить график…");
    readonly ToolStripButton btnGoogle = new("Для Google Таблиц…");
    readonly ToolStripComboBox cbView = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 220 };
    readonly NumericUpDown numGap = new() { Minimum = 1, Maximum = 240, Value = 10, Width = 55 };
    readonly FormsPlot plotView = new() { Dock = DockStyle.Fill };
    readonly DataGridView gridSeries = MakeGrid();
    readonly DataGridView gridItems = MakeGrid();
    readonly Label lblSeries = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 4, 6, 4), Font = new Font("Segoe UI", 9.5f) };
    readonly StatusStrip status = new();
    readonly ToolStripStatusLabel statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolTip tip = new() { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 30000 };
    int hoverSeries = -1;


    public MainForm()
    {
        Text = "Давление: усреднение замеров";
        Font = new Font("Segoe UI", 9f);
        Width = 1400; Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        archivePath = ChooseArchivePath();

        cbView.Items.AddRange(new object[] { "Средние и замеры", "Только средние", "Только замеры" });
        cbView.SelectedIndex = 0;
        cbView.SelectedIndexChanged += (_, _) => DrawPlot();
        numGap.ValueChanged += (_, _) => { settings.GapMinutes = (int)numGap.Value; Recalculate(); };

        toolbar.Items.AddRange(new ToolStripItem[]
        {
            btnDevice, btnAwp, btnPdf, new ToolStripSeparator(),
            btnExcel, btnPng, btnGoogle, new ToolStripSeparator(),
            new ToolStripLabel("График:"), cbView, new ToolStripSeparator(),
            new ToolStripLabel("Новая серия после паузы, мин:"), new ToolStripControlHost(numGap),
        });
        btnDevice.Font = new Font(Font, FontStyle.Bold);
        btnDevice.Click += async (_, _) => await DownloadFromDevice();
        btnAwp.Click += (_, _) => ImportAwp(askFiles: ModifierKeys.HasFlag(Keys.Shift));
        btnPdf.Click += (_, _) => ImportPdf();
        btnExcel.Click += (_, _) => SaveExcel();
        btnPng.Click += (_, _) => SavePng();
        btnGoogle.Click += (_, _) => ExportForGoogle();

        // Левая часть: список серий. Правая: график сверху, состав серии снизу.
        gridSeries.SelectionChanged += (_, _) =>
        {
            if (gridSeries.SelectedRows.Count == 0) return;
            var no = (int)gridSeries.SelectedRows[0].Cells[0].Value;
            SelectSeries(series.FirstOrDefault(s => s.No == no), zoom: gridSeries.Focused);
        };

        var bottom = new Panel { Dock = DockStyle.Fill };
        bottom.Controls.Add(gridItems);
        bottom.Controls.Add(lblSeries);

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 520 };
        right.Panel1.Controls.Add(plotView);
        right.Panel2.Controls.Add(bottom);

        var main = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
        main.Panel1.Controls.Add(gridSeries);
        main.Panel2.Controls.Add(right);

        status.Items.Add(statusText);
        Controls.Add(main);
        Controls.Add(toolbar);
        Controls.Add(status);

        plotView.MouseMove += PlotMouseMove;
        plotView.MouseLeave += (_, _) => { tip.Hide(plotView); hoverSeries = -1; };
        plotView.MouseUp += PlotMouseUp;

        Shown += (_, _) => Startup();
    }

    static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        BackgroundColor = System.Drawing.Color.White, BorderStyle = BorderStyle.None,
    };

    // ---------- Данные ----------

    static string ChooseArchivePath()
    {
        // Архив лежит рядом с программой; если туда писать нельзя, то в профиле пользователя
        var exeDir = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(exeDir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(exeDir, "замеры_архив.csv");
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BPAverage");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "замеры_архив.csv");
        }
    }

    void Startup()
    {
        try { archive = Archive.Load(archivePath); }
        catch (Exception e) { Error("Не удалось прочитать архив", e); }

        if (archive.Count == 0 && Directory.Exists(AwpReader.DefaultDataDir))
            ImportAwp(askFiles: false, quiet: true);
        else
            Recalculate();
        SetStatus(archive.Count == 0
            ? "Архив пуст. Подключите аппарат и нажмите «Скачать с аппарата» или импортируйте файлы."
            : $"Архив: {archive.Count} замеров ({archivePath})");
    }

    void AddMeasurements(IEnumerable<Measurement> items, string from)
    {
        var list = items.ToList();
        int added = Archive.Merge(archive, list);
        if (added > 0)
        {
            try { Archive.Save(archivePath, archive); }
            catch (Exception e) { Error("Не удалось сохранить архив", e); }
        }
        Recalculate();
        if (added > 0 && series.Count > 0)
        {
            // Показать последнюю новую серию
            var lastNew = list.Max(m => m.Time);
            ZoomTo(lastNew.AddDays(-7), lastNew.AddHours(6));
        }
        SetStatus($"{from}: получено {list.Count}, новых {added}. Всего в архиве {archive.Count}.");
    }

    void Recalculate()
    {
        series = Analysis.Analyze(archive, settings);
        FillSeriesGrid();
        DrawPlot(resetView: selected == null);
        if (selected != null)
            SelectSeries(series.FirstOrDefault(s => s.Start == selected.Start) ?? series.LastOrDefault(), zoom: false);
    }

    // ---------- Источники ----------

    async Task DownloadFromDevice()
    {
        SetBusy(true, "Подключаюсь к аппарату…");
        try
        {
            var (count, items) = await Task.Run(() => ContecDevice.Download(msg => BeginInvoke(() => SetStatus(msg))));
            if (count.HasValue && items.Count < count)
                MessageBox.Show(this, $"Получено {items.Count} из {count} замеров. Попробуйте скачать ещё раз.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (items.Count == 0)
                MessageBox.Show(this, "Аппарат не передал ни одного замера.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            AddMeasurements(items, "С аппарата");
        }
        catch (DeviceException e)
        {
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus(e.Message);
        }
        catch (Exception e) { Error("Ошибка при обмене с аппаратом", e); }
        finally { SetBusy(false); }
    }

    void ImportAwp(bool askFiles, bool quiet = false)
    {
        string[] files;
        if (!askFiles && Directory.Exists(AwpReader.DefaultDataDir))
            files = Directory.GetFiles(AwpReader.DefaultDataDir, "*.awp", SearchOption.AllDirectories);
        else
        {
            using var dlg = new OpenFileDialog { Filter = "Файлы BPMeter (*.awp)|*.awp", Multiselect = true, Title = "Файлы замеров BPMeter" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            files = dlg.FileNames;
        }
        var items = new List<Measurement>();
        foreach (var f in files)
        {
            try { items.AddRange(AwpReader.Read(f)); }
            catch (Exception e) { if (!quiet) Error($"Не удалось прочитать {Path.GetFileName(f)}", e); }
        }
        if (files.Length == 0 && !quiet)
            MessageBox.Show(this, $"В {AwpReader.DefaultDataDir} нет файлов *.awp.", Text);
        AddMeasurements(items, $"Из BPMeter ({files.Length} файлов)");
    }

    void ImportPdf()
    {
        using var dlg = new OpenFileDialog { Filter = "PDF-отчёты (*.pdf)|*.pdf", Multiselect = true, Title = "PDF-отчёты BPMeter" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var items = new List<Measurement>();
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var r = PdfReportReader.Read(f);
                if (r.Count == 0) MessageBox.Show(this, $"В {Path.GetFileName(f)} не нашлось таблицы замеров.", Text);
                items.AddRange(r);
            }
            catch (Exception e) { Error($"Не удалось прочитать {Path.GetFileName(f)}", e); }
        }
        AddMeasurements(items, "Из PDF");
    }

    // ---------- Таблицы ----------

    void FillSeriesGrid()
    {
        gridSeries.SuspendLayout();
        gridSeries.Columns.Clear();
        gridSeries.Rows.Clear();
        foreach (var h in new[] { "№", "Дата", "Время", "Замеров", "Среднее", "Учтено", "Пульс", "Пометки" })
            gridSeries.Columns.Add(h, h);
        gridSeries.Columns[0].Visible = false;
        gridSeries.Columns[7].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (var s in series.AsEnumerable().Reverse()) // сверху самые новые
        {
            int r = gridSeries.Rows.Add(s.No, s.Date.ToString("dd.MM.yyyy"), $"{s.Start:HH:mm}–{s.End:HH:mm}", s.Items.Count,
                $"{s.SysClean:0.#}/{s.DiaClean:0.#}", $"{s.Used.Count} из {s.Items.Count}", $"{s.PulseClean:0}", s.NotesText);
            var row = gridSeries.Rows[r];
            if (s.SysClean >= settings.HighSys || s.DiaClean >= settings.HighDia)
                row.Cells[4].Style.BackColor = System.Drawing.Color.FromArgb(0xF8, 0xCB, 0xAD);
            if (s.Unreliable) row.Cells[7].Style.ForeColor = System.Drawing.Color.FromArgb(0xB0, 0x3A, 0x2E);
        }
        gridSeries.ResumeLayout();
    }

    void FillItemsGrid(Series s)
    {
        gridItems.Columns.Clear();
        gridItems.Rows.Clear();
        if (s == null) { lblSeries.Text = ""; return; }
        foreach (var h in new[] { "Время", "SYS", "DIA", "Пульс", "MAP", "В среднем", "Источник" })
            gridItems.Columns.Add(h, h);
        foreach (var m in s.Items)
        {
            int r = gridItems.Rows.Add(m.Time.ToString("HH:mm"), m.Sys, m.Dia, m.Pulse, m.Map,
                m.Excluded == Exclusion.None ? "учтён" : $"нет: {m.ExcludedText}", m.Source);
            if (m.Excluded != Exclusion.None)
            {
                gridItems.Rows[r].DefaultCellStyle.ForeColor = System.Drawing.Color.Gray;
                gridItems.Rows[r].DefaultCellStyle.Font = new Font(gridItems.Font, FontStyle.Strikeout);
            }
        }
        lblSeries.Text = $"Серия {s.Date:dd.MM.yyyy} {s.Start:HH:mm}–{s.End:HH:mm}: среднее {s.SysClean:0.#}/{s.DiaClean:0.#}, " +
                         $"пульс {s.PulseClean:0} (учтено {s.Used.Count} из {s.Items.Count}).  Простое среднее всех: {s.SysMean:0.#}/{s.DiaMean:0.#}." +
                         (s.Notes.Count > 0 ? $"\n{s.NotesText}" : "");
    }

    void SelectSeries(Series s, bool zoom)
    {
        selected = s;
        FillItemsGrid(s);
        if (s == null) { DrawPlot(); return; }
        if (zoom)
        {
            var half = TimeSpan.FromHours(36);
            ZoomTo(s.Middle - half, s.Middle + half, redraw: false);
        }
        DrawPlot();
        // Синхронизировать выделение в списке, не вызывая повторного зума
        foreach (DataGridViewRow row in gridSeries.Rows)
            if ((int)row.Cells[0].Value == s.No && !row.Selected)
            {
                row.Selected = true;
                gridSeries.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 3);
                break;
            }
    }

    // ---------- График ----------

    void DrawPlot(bool resetView = false)
    {
        var plot = plotView.Plot;
        var limits = plot.Axes.GetLimits();
        bool hadData = plot.GetPlottables().Any();
        ChartBuilder.Build(plot, series, settings, (ChartMode)cbView.SelectedIndex, selected);
        if (series.Count > 0)
        {
            if (resetView || !hadData)
            {
                var last = series[^1].End;
                ChartBuilder.Zoom(plot, series, settings, last.AddDays(-14), last.AddHours(6));
            }
            else plot.Axes.SetLimits(limits);
        }
        plotView.Refresh();
    }

    void ZoomTo(DateTime from, DateTime to, bool redraw = true)
    {
        ChartBuilder.Zoom(plotView.Plot, series, settings, from, to);
        if (redraw) plotView.Refresh();
    }

    Series SeriesAt(int px, int py)
    {
        if (series.Count == 0) return null;
        var plot = plotView.Plot;
        float scale = plotView.DisplayScale;
        var c = plot.GetCoordinates(new Pixel(px / scale, py / scale));
        Series best = null;
        double bestDist = double.MaxValue;
        foreach (var s in series)
        {
            // Расстояние в пикселях до отрезка времени серии
            double x0 = plot.GetPixel(new Coordinates(s.Start.ToOADate(), c.Y)).X;
            double x1 = plot.GetPixel(new Coordinates(s.End.ToOADate(), c.Y)).X;
            double d = px / scale < x0 ? x0 - px / scale : px / scale > x1 ? px / scale - x1 : 0;
            if (d < bestDist) { bestDist = d; best = s; }
        }
        return bestDist <= 12 ? best : null;
    }

    void PlotMouseMove(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.None) { tip.Hide(plotView); return; }
        var s = SeriesAt(e.X, e.Y);
        if (s == null) { if (hoverSeries != -1) { tip.Hide(plotView); hoverSeries = -1; } return; }
        if (s.No == hoverSeries) return;
        hoverSeries = s.No;
        tip.Show(Describe(s), plotView, e.X + 16, e.Y + 16);
    }

    void PlotMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var s = SeriesAt(e.X, e.Y);
        if (s != null) SelectSeries(s, zoom: false);
    }

    static string Describe(Series s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.Date:dd.MM.yyyy} {s.Start:HH:mm}–{s.End:HH:mm}");
        sb.AppendLine($"Среднее: {s.SysClean:0.#}/{s.DiaClean:0.#}, пульс {s.PulseClean:0} (учтено {s.Used.Count} из {s.Items.Count})");
        foreach (var m in s.Items)
            sb.AppendLine($"  {m.Time:HH:mm}  {m.Sys}/{m.Dia}  пульс {m.Pulse}" + (m.Excluded == Exclusion.None ? "" : $"   — не учтён ({m.ExcludedText})"));
        if (s.Notes.Count > 0) sb.Append(s.NotesText);
        return sb.ToString().TrimEnd();
    }

    // ---------- Выгрузка ----------

    byte[] ChartPng(int w = 1600, int h = 800) => plotView.Plot.GetImageBytes(w, h, ImageFormat.Png);

    void SaveExcel()
    {
        if (!HasData()) return;
        using var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"давление_{DateTime.Now:yyyy-MM-dd}.xlsx" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ExcelExport.Save(dlg.FileName, series, settings, ChartPng());
            SetStatus("Сохранено: " + dlg.FileName);
            OpenFile(dlg.FileName);
        }
        catch (Exception e) { Error("Не удалось сохранить Excel", e); }
    }

    void SavePng()
    {
        if (!HasData()) return;
        using var dlg = new SaveFileDialog { Filter = "Картинка PNG (*.png)|*.png", FileName = $"давление_{DateTime.Now:yyyy-MM-dd}.png" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllBytes(dlg.FileName, ChartPng()); SetStatus("Сохранено: " + dlg.FileName); }
        catch (Exception e) { Error("Не удалось сохранить картинку", e); }
    }

    void ExportForGoogle()
    {
        if (!HasData()) return;
        using var dlg = new FolderBrowserDialog { Description = "Куда сохранить файлы для Google Диска", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd");
            var xlsx = Path.Combine(dlg.SelectedPath, $"давление_{stamp}.xlsx");
            var png = Path.Combine(dlg.SelectedPath, $"давление_график_{stamp}.png");
            var bytes = ChartPng();
            ExcelExport.Save(xlsx, series, settings, bytes);
            File.WriteAllBytes(png, bytes);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{xlsx}\"") { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("https://drive.google.com/drive/my-drive") { UseShellExecute = true });
            MessageBox.Show(this,
                "Файлы сохранены, открыты папка с ними и Google Диск.\n\n" +
                "1. Перетащите файл .xlsx в окно Google Диска.\n" +
                "2. Дважды щёлкните его на Диске и выберите «Открыть в Google Таблицах».\n" +
                "Листы «Серии», «По дням», «Все замеры» и «График» сохранятся.\n\n" +
                "Картинку графика (.png) можно вставить в Google Документ через «Вставка → Изображение».",
                "Для Google Таблиц", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) { Error("Не удалось подготовить файлы", e); }
    }

    // ---------- Мелочи ----------

    bool HasData()
    {
        if (series.Count > 0) return true;
        MessageBox.Show(this, "Пока нет замеров.", Text);
        return false;
    }

    void SetBusy(bool busy, string text = null)
    {
        foreach (ToolStripItem i in toolbar.Items) i.Enabled = !busy;
        UseWaitCursor = busy;
        if (text != null) SetStatus(text);
    }

    void SetStatus(string text) => statusText.Text = text;

    void Error(string what, Exception e)
    {
        SetStatus($"{what}: {e.Message}");
        MessageBox.Show(this, $"{what}.\n\n{e.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
