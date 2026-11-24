using ClosedXML.Excel;

namespace BpCore;

/// <summary>Excel-файл с листами «Серии», «По дням», «Все замеры» (как у прежнего скрипта).</summary>
public static class ExcelExport
{
    static readonly XLColor Head = XLColor.FromHtml("#4F6D8F");
    static readonly XLColor High = XLColor.FromHtml("#F8CBAD");

    public static void Save(string path, List<Series> series, AnalysisSettings s, byte[] chartPng = null)
    {
        using var wb = new XLWorkbook();

        var ws = wb.AddWorksheet("Серии");
        WriteHeader(ws, "№", "Дата", "Начало", "Конец", "Замеров", "SYS средн.", "DIA средн.", "Пульс средн.",
            "SYS без выбросов", "DIA без выбросов", "Пульс без выбросов", "Учтено замеров",
            "SYS медиана", "DIA медиана", "SYS мин–макс", "DIA мин–макс", "Пометки");
        int r = 2;
        foreach (var x in series)
        {
            object[] row = { x.No, x.Date, x.Start.ToString("HH:mm"), x.End.ToString("HH:mm"), x.Items.Count,
                x.SysMean, x.DiaMean, x.PulseMean, x.SysClean, x.DiaClean, x.PulseClean, x.Used.Count,
                x.SysMedian, x.DiaMedian, x.SysRange, x.DiaRange, x.NotesText };
            Fill(ws, r, row);
            ws.Cell(r, 2).Style.DateFormat.Format = "dd.mm.yyyy";
            Mark(ws.Cell(r, 6), x.SysMean >= s.HighSys); Mark(ws.Cell(r, 7), x.DiaMean >= s.HighDia);
            Mark(ws.Cell(r, 9), x.SysClean >= s.HighSys); Mark(ws.Cell(r, 10), x.DiaClean >= s.HighDia);
            r++;
        }
        Widths(ws, 5, 11, 8, 8, 8, 9, 9, 9, 10, 10, 10, 9, 9, 9, 11, 11, 34);

        var wd = wb.AddWorksheet("По дням");
        WriteHeader(wd, "Дата", "Серий", "Замеров", "SYS", "DIA", "Пульс", "SYS мин серия", "SYS макс серия");
        r = 2;
        foreach (var d in Analysis.ByDay(series))
        {
            Fill(wd, r, new object[] { d.Date, d.SeriesCount, d.MeasurementCount, d.Sys, d.Dia, d.Pulse, d.SysMinSeries, d.SysMaxSeries });
            wd.Cell(r, 1).Style.DateFormat.Format = "dd.mm.yyyy";
            Mark(wd.Cell(r, 4), d.Sys >= s.HighSys); Mark(wd.Cell(r, 5), d.Dia >= s.HighDia);
            r++;
        }
        Widths(wd, 11, 7, 9, 8, 8, 8, 11, 11);

        var wa = wb.AddWorksheet("Все замеры");
        WriteHeader(wa, "Серия", "Дата", "Время", "SYS", "MAP", "DIA", "PP", "Пульс", "Не учтён", "Источник");
        r = 2;
        foreach (var m in series.SelectMany(x => x.Items))
        {
            Fill(wa, r, new object[] { m.SeriesNo, m.Time.Date, m.Time.ToString("HH:mm"), m.Sys, m.Map, m.Dia, m.PulsePressure, m.Pulse, m.ExcludedText, m.Source });
            wa.Cell(r, 2).Style.DateFormat.Format = "dd.mm.yyyy";
            if (m.Excluded != Exclusion.None)
                wa.Range(r, 1, r, 9).Style.Font.SetFontColor(XLColor.Gray).Font.SetStrikethrough(true);
            r++;
        }
        Widths(wa, 7, 11, 7, 6, 6, 6, 6, 7, 9, 20);

        if (chartPng != null)
        {
            var wc = wb.AddWorksheet("График");
            using var ms = new MemoryStream(chartPng);
            wc.AddPicture(ms).MoveTo(wc.Cell(1, 1));
        }

        wb.SaveAs(path);
    }

    static void WriteHeader(IXLWorksheet ws, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = names[i];
            c.Style.Font.Bold = true;
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Fill.BackgroundColor = Head;
            c.Style.Alignment.WrapText = true;
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        ws.Row(1).Height = 32;
        ws.SheetView.FreezeRows(1);
    }

    static void Fill(IXLWorksheet ws, int r, object[] values)
    {
        for (int i = 0; i < values.Length; i++)
            ws.Cell(r, i + 1).Value = values[i] switch
            {
                null => Blank.Value,
                int v => v,
                double v => v,
                DateTime v => v,
                var v => v.ToString(),
            };
    }

    static void Mark(IXLCell c, bool high) { if (high) c.Style.Fill.BackgroundColor = High; }

    static void Widths(IXLWorksheet ws, params double[] w)
    {
        for (int i = 0; i < w.Length; i++) ws.Column(i + 1).Width = w[i];
    }
}
