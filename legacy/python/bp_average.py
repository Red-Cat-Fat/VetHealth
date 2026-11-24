#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Усреднение замеров давления из PDF-отчётов программы тонометра.

Программа читает таблицу замеров (Пим / Дата / Время / SYS / MAP / DIA / PP / PR)
из PDF-отчётов или прямо из файлов программы (*.awp в C:\\BPMeter\\data), объединяет замеры, сделанные подряд (с паузой
не больше --gap минут), в одну «серию» и считает по каждой серии среднее,
медиану и среднее без явных выбросов. Результат: Excel-файл с листами
«Серии», «По дням» и «Все замеры».

Установка (один раз):
    pip install pypdf openpyxl

Запуск:
    python bp_average.py                      (все замеры из C:\\BPMeter\\data)
    python bp_average.py отчёт1.pdf отчёт2.pdf
    python bp_average.py *.pdf -o давление.xlsx --gap 10
Двойной щелчок по bp_average.bat берёт данные из C:\\BPMeter\\data;
можно также перетащить на него PDF-отчёты мышкой.
"""
import argparse
import re
import statistics as st
import sys
from datetime import datetime, timedelta
from pathlib import Path

try:
    from pypdf import PdfReader
    from openpyxl import Workbook
    from openpyxl.styles import Alignment, Font, PatternFill
    from openpyxl.utils import get_column_letter
except ImportError:
    sys.exit("Не хватает библиотек. Выполните: pip install pypdf openpyxl")

ROW_RE = re.compile(
    r"(?m)^\s*(\d+)\s+(\d{4}/\d{1,2}/\d{1,2})\s+(\d{1,2}:\d{2})"
    r"\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)"
)
FIELDS = ("sys", "map", "dia", "pp", "pr")

# Пороги для подсветки (домашние замеры, ммHg)
HIGH_SYS, HIGH_DIA = 140, 90
# Замер считается выбросом, если отличается от медианы серии больше чем на столько
OUTLIER_SYS, OUTLIER_DIA = 20, 15
# Замер считается сбоем аппарата, если SYS/DIA слишком низкие или разница SYS-DIA неправдоподобна
SYS_MIN, DIA_MIN, PP_MIN, PP_MAX = 85, 50, 20, 80


AWP_RE = re.compile(r"(?m)^(\d+)=([0-9A-Fa-f]{32,})\s*$")
DEFAULT_DATA_DIR = Path(r"C:\BPMeter\data")


def read_awp(path):
    """Файл замеров программы тонометра (*.awp в папке C:\\BPMeter\\data).

    Каждый замер хранится строкой вида N=07EA0B0C010D0000 7100 4F00 5C00 6E00 ...:
    год (2 байта), месяц, день, час, минута, 2 служебных байта,
    затем SYS, DIA, MAP, пульс по 2 байта (младший байт первым).
    """
    text = Path(path).read_text(encoding="cp1251", errors="replace")
    out = []
    for num, h in AWP_RE.findall(text):
        b = bytes.fromhex(h[:32])
        year = int.from_bytes(b[0:2], "big")
        if not 2000 <= year <= 2100:
            continue  # другой (старый) формат записи
        try:
            dt = datetime(year, b[2], b[3], b[4], b[5])
        except ValueError:
            continue
        sys_, dia, map_, pr = (int.from_bytes(b[i:i + 2], "little") for i in (8, 10, 12, 14))
        if sys_ == 0 or dia == 0:
            continue  # неудачный замер
        out.append({"dt": dt, "sys": sys_, "dia": dia, "map": map_, "pp": sys_ - dia, "pr": pr,
                    "src": Path(path).name, "num": int(num)})
    return out


def read_any(path):
    return read_awp(path) if Path(path).suffix.lower() == ".awp" else read_pdf(path)


def read_pdf(path):
    text = "\n".join((p.extract_text() or "") for p in PdfReader(str(path)).pages)
    out = []
    for num, d, t, *vals in ROW_RE.findall(text):
        rec = dict(zip(FIELDS, map(int, vals)))
        rec["dt"] = datetime.strptime(f"{d} {t}", "%Y/%m/%d %H:%M")
        rec["src"] = Path(path).name
        rec["num"] = int(num)
        out.append(rec)
    return out


def group_series(meas, gap_min):
    series, cur = [], []
    for m in meas:
        if cur and m["dt"] - cur[-1]["dt"] > timedelta(minutes=gap_min):
            series.append(cur)
            cur = []
        cur.append(m)
    if cur:
        series.append(cur)
    return series


def is_glitch(m):
    """Физиологически сомнительный замер: скорее сбой аппарата, чем давление."""
    pp = m["sys"] - m["dia"]
    return m["sys"] < SYS_MIN or m["dia"] < DIA_MIN or pp <= PP_MIN or pp >= PP_MAX


def summarize(s):
    sys_v = [m["sys"] for m in s]
    dia_v = [m["dia"] for m in s]
    med_s, med_d = st.median(sys_v), st.median(dia_v)
    notes = []
    for m in s:
        m["why"] = "сбой" if is_glitch(m) else ""
    ok = [m for m in s if not m["why"]] or list(s)
    if len(ok) >= 3:
        ms = st.median(m["sys"] for m in ok)
        md = st.median(m["dia"] for m in ok)
        clean = [m for m in ok
                 if abs(m["sys"] - ms) <= OUTLIER_SYS and abs(m["dia"] - md) <= OUTLIER_DIA]
        if len(clean) * 2 < len(ok):  # «выбросов» больше половины: значит, разброс сплошной
            clean = ok
        for m in ok:
            if m not in clean:
                m["why"] = "выброс"
    else:
        clean = ok
    for m in s:
        m["outlier"] = bool(m["why"])
    if len(s) < 3:
        notes.append("мало замеров")
    dropped = len(s) - len(clean)
    if dropped:
        notes.append(f"не учтено замеров: {dropped}")
    if len(s) >= 3 and (len(clean) * 2 < len(s) or max(m["sys"] for m in clean) - min(m["sys"] for m in clean) > 25):
        notes.insert(0, "серия ненадёжная")
    if max(sys_v) - min(sys_v) > 25:
        notes.append("большой разброс SYS")
    avg = lambda key, data: round(st.mean(m[key] for m in data), 1)
    return {
        "date": s[0]["dt"].date(), "start": s[0]["dt"].strftime("%H:%M"),
        "end": s[-1]["dt"].strftime("%H:%M"), "n": len(s),
        "sys": avg("sys", s), "dia": avg("dia", s), "pr": avg("pr", s), "map": avg("map", s),
        "sys_med": med_s, "dia_med": med_d,
        "sys_clean": avg("sys", clean), "dia_clean": avg("dia", clean), "pr_clean": avg("pr", clean),
        "n_clean": len(clean),
        "sys_range": f"{min(sys_v)}–{max(sys_v)}", "dia_range": f"{min(dia_v)}–{max(dia_v)}",
        "notes": "; ".join(notes),
    }


HEAD_FONT = Font(bold=True, color="FFFFFF")
HEAD_FILL = PatternFill("solid", fgColor="4F6D8F")
RED = PatternFill("solid", fgColor="F8CBAD")
GREY = Font(color="999999", strike=True)


def write_sheet(ws, headers, rows, widths):
    ws.append(headers)
    for c in ws[1]:
        c.font, c.fill = HEAD_FONT, HEAD_FILL
        c.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
    ws.row_dimensions[1].height = 32
    for r in rows:
        ws.append(r)
    for i, w in enumerate(widths, 1):
        ws.column_dimensions[get_column_letter(i)].width = w
    ws.freeze_panes = "A2"


def highlight(ws, sys_col, dia_col):
    for row in ws.iter_rows(min_row=2):
        s, d = row[sys_col - 1], row[dia_col - 1]
        if isinstance(s.value, (int, float)) and s.value >= HIGH_SYS:
            s.fill = RED
        if isinstance(d.value, (int, float)) and d.value >= HIGH_DIA:
            d.fill = RED


def main():
    ap = argparse.ArgumentParser(description="Усреднение замеров давления из PDF-отчётов")
    ap.add_argument("pdfs", nargs="*",
                    help="PDF-отчёты или файлы *.awp; без аргументов берутся все *.awp из C:\\BPMeter\\data")
    ap.add_argument("-o", "--out", help="куда сохранить xlsx (по умолчанию рядом с первым PDF)")
    ap.add_argument("--gap", type=int, default=10,
                    help="пауза в минутах, после которой начинается новая серия (по умолчанию 10)")
    a = ap.parse_args()

    from_default = not a.pdfs
    if from_default:
        a.pdfs = sorted(str(p) for p in DEFAULT_DATA_DIR.glob("*.awp"))
        if not a.pdfs:
            sys.exit(f"Не указаны файлы, и в {DEFAULT_DATA_DIR} нет *.awp")
    meas, seen = [], set()
    for p in a.pdfs:
        rows = read_any(p)
        print(f"{Path(p).name}: найдено замеров {len(rows)}")
        for m in rows:
            key = (m["dt"], m["sys"], m["dia"], m["pr"])
            if key not in seen:  # одинаковые замеры из пересекающихся отчётов берём один раз
                seen.add(key)
                meas.append(m)
    if not meas:
        sys.exit("В PDF не нашлось таблицы замеров.")

    if a.out:
        out = Path(a.out)
    elif from_default:
        out = Path.cwd() / "давление_средние.xlsx"
    else:
        out = Path(a.pdfs[0]).with_name(Path(a.pdfs[0]).stem + "_средние.xlsx")
    build_workbook(meas, a.gap, out)


def build_workbook(meas, gap, out):
    """Группирует замеры в серии, считает средние и сохраняет Excel-файл out."""
    meas.sort(key=lambda m: m["dt"])
    series = group_series(meas, gap)
    summ = [summarize(s) for s in series]
    for i, s in enumerate(series, 1):
        for m in s:
            m["series"] = i

    wb = Workbook()
    ws = wb.active
    ws.title = "Серии"
    write_sheet(ws,
        ["№", "Дата", "Начало", "Конец", "Замеров",
         "SYS средн.", "DIA средн.", "Пульс средн.",
         "SYS без выбросов", "DIA без выбросов", "Пульс без выбросов", "Учтено замеров",
         "SYS медиана", "DIA медиана", "SYS мин–макс", "DIA мин–макс", "Пометки"],
        [[i, s["date"], s["start"], s["end"], s["n"], s["sys"], s["dia"], s["pr"],
          s["sys_clean"], s["dia_clean"], s["pr_clean"], s["n_clean"],
          s["sys_med"], s["dia_med"], s["sys_range"], s["dia_range"], s["notes"]]
         for i, s in enumerate(summ, 1)],
        [5, 11, 8, 8, 8, 9, 9, 9, 10, 10, 10, 9, 9, 9, 11, 11, 34])
    for c in ws["B"][1:]:
        c.number_format = "DD.MM.YYYY"
    highlight(ws, 6, 7)
    highlight(ws, 9, 10)

    # По дням: среднее из «чистых» средних серий за день
    days = {}
    for s in summ:
        days.setdefault(s["date"], []).append(s)
    ws2 = wb.create_sheet("По дням")
    write_sheet(ws2, ["Дата", "Серий", "Замеров", "SYS", "DIA", "Пульс", "SYS мин серия", "SYS макс серия"],
        [[d, len(ss), sum(x["n"] for x in ss),
          round(st.mean(x["sys_clean"] for x in ss), 1),
          round(st.mean(x["dia_clean"] for x in ss), 1),
          round(st.mean(x["pr_clean"] for x in ss), 1),
          min(x["sys_clean"] for x in ss), max(x["sys_clean"] for x in ss)]
         for d, ss in sorted(days.items())],
        [11, 7, 9, 8, 8, 8, 11, 11])
    for c in ws2["A"][1:]:
        c.number_format = "DD.MM.YYYY"
    highlight(ws2, 4, 5)

    ws3 = wb.create_sheet("Все замеры")
    write_sheet(ws3, ["Серия", "Дата", "Время", "SYS", "MAP", "DIA", "PP", "Пульс", "Не учтён", "Файл", "№ в отчёте"],
        [[m["series"], m["dt"].date(), m["dt"].strftime("%H:%M"), m["sys"], m["map"], m["dia"],
          m["pp"], m["pr"], m["why"], m["src"], m["num"]] for m in meas],
        [7, 11, 7, 6, 6, 6, 6, 7, 8, 22, 10])
    for c in ws3["B"][1:]:
        c.number_format = "DD.MM.YYYY"
    for row in ws3.iter_rows(min_row=2):
        if row[8].value:
            for c in row[:9]:
                c.font = GREY

    total = [s["sys_clean"] for s in summ], [s["dia_clean"] for s in summ]
    print(f"Серий: {len(summ)}, замеров: {len(meas)}")
    print(f"Среднее по сериям (без сбоев и выбросов): {st.mean(total[0]):.1f}/{st.mean(total[1]):.1f} ммHg")

    wb.save(out)
    print(f"Готово: {out}")


if __name__ == "__main__":
    main()
