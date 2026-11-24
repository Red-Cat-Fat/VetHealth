#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Загрузка замеров давления прямо с тонометра CONTEC08A по USB, без программы BPMeter.

Что делает:
  1. Подключается к аппарату (USB HID, VID 0483, PID 5750) и скачивает все замеры из его памяти.
  2. Дописывает новые замеры в архив «замеры_архив.csv» (повторы не дублируются),
     так что история сохраняется, даже если память аппарата очистится.
  3. Строит «давление_средние.xlsx» по всему архиву (те же листы, что у bp_average.py).

Запуск: двойной щелчок по «Собрать_данные.bat». При первом запуске он сам
подготовит Python и нужные библиотеки в папке .venv рядом с собой. Вручную:
    python bp_device.py [--gap 10] [--archive путь.csv] [-o путь.xlsx]

Перед запуском закройте BPMeter: два приложения не могут одновременно работать с аппаратом.

Протокол восстановлен по записи USB-обмена BPMeter с аппаратом (2026-11-23):
  ПК -> аппарат: 40 8F FF FE FD FC   (приветствие)
                 42 8F FF FE FD FC   (информация об аппарате)
                 43 41 01 04 02      (выдать замеры)
  аппарат -> ПК: 4A 43 01            (подтверждение)
                 46 41 ...           (заголовок, в нём число замеров)
                 по одному замеру в отчёте, 14 байт:
                 [флаги] [0] SYS DIA пульс MAP год-2000 месяц [0] день час минута [0] [0x31]
  У всех байтов данных установлен старший бит; настоящий старший бит байта N
  хранится в бите (N-1) байта флагов (так кодируют значения больше 127).
"""
import argparse
import csv
import os
import sys
import time
from datetime import datetime
from pathlib import Path

VID, PID = 0x0483, 0x5750
REPORT = 64
CMD_HELLO = bytes([0x40, 0x8F, 0xFF, 0xFE, 0xFD, 0xFC])
CMD_INFO = bytes([0x42, 0x8F, 0xFF, 0xFE, 0xFD, 0xFC])
CMD_READ = bytes([0x43, 0x41, 0x01, 0x04, 0x02])

ARCHIVE_FIELDS = ["дата", "время", "SYS", "DIA", "MAP", "пульс"]


def unpack(pkt, start=0, n=14):
    """Снимает кодировку «байт флагов + 7-битные данные»: pkt[start] — флаги."""
    p = list(pkt[start:start + n])
    flags = p[0] & 0x7F
    return [p[0]] + [(b & 0x7F) | (0x80 if (flags >> (k - 1)) & 1 else 0)
                     for k, b in enumerate(p[1:], 1)]


def decode_record(pkt):
    v = unpack(pkt)
    sys_, dia, pr, map_, yy, mo, _, dd, hh, mi = v[2:12]
    try:
        dt = datetime(2000 + yy, mo, dd, hh, mi)
    except ValueError:
        return None
    if not sys_ or not dia:
        return None
    return {"dt": dt, "sys": sys_, "dia": dia, "map": map_, "pp": sys_ - dia, "pr": pr}


def header_count(pkt):
    """Число замеров из заголовка 46 41 ... (байты 3-4 после снятия кодировки)."""
    v = unpack(pkt, start=2, n=12)
    return v[1] * 256 + v[2]


def parse_stream(packets):
    """packets: последовательность 64-байтных отчётов от аппарата после команды CMD_READ."""
    count, recs, started = None, [], False
    for p in packets:
        if not started:
            if p[0] == 0x46 and p[1] == 0x41:
                count, started = header_count(p), True
            continue
        if p[0] & 0x80:
            r = decode_record(p)
            if r:
                recs.append(r)
        if count is not None and len(recs) >= count:
            break
    return count, recs


def download(timeout_s=3.0):
    try:
        import hid
    except ImportError:
        sys.exit("Не хватает библиотеки. Выполните: pip install hidapi")
    dev = hid.device()
    try:
        dev.open(VID, PID)
    except (OSError, IOError):
        sys.exit("Аппарат не найден. Подключите его по USB, включите и закройте BPMeter.")
    try:
        print("Подключено:", dev.get_product_string() or "аппарат")

        def send(cmd):
            dev.write([0x00] + list(cmd) + [0] * (REPORT - len(cmd)))  # 0x00 — номер отчёта для Windows

        def recv(ms):
            d = dev.read(REPORT, ms)
            return bytes(d) if d else None

        def drain():
            while recv(50):
                pass

        for cmd in (CMD_HELLO, CMD_INFO):
            send(cmd)
            recv(1000)
            drain()

        send(CMD_READ)
        packets, last = [], time.time()
        while time.time() - last < timeout_s:
            p = recv(200)
            if p:
                packets.append(p)
                last = time.time()
                cnt, recs = parse_stream(packets)
                if cnt is not None and len(recs) >= cnt:
                    break
        return parse_stream(packets)
    finally:
        dev.close()


def load_archive(path):
    rows = []
    if path.exists():
        with path.open(encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f, delimiter=";"):
                dt = datetime.strptime(f'{r["дата"]} {r["время"]}', "%Y-%m-%d %H:%M")
                s, d = int(r["SYS"]), int(r["DIA"])
                rows.append({"dt": dt, "sys": s, "dia": d, "map": int(r["MAP"]),
                             "pp": s - d, "pr": int(r["пульс"])})
    return rows


def save_archive(path, rows):
    with path.open("w", encoding="utf-8-sig", newline="") as f:  # utf-8-sig: Excel открывает без «кракозябр»
        w = csv.writer(f, delimiter=";")
        w.writerow(ARCHIVE_FIELDS)
        for m in sorted(rows, key=lambda m: m["dt"]):
            w.writerow([m["dt"].strftime("%Y-%m-%d"), m["dt"].strftime("%H:%M"),
                        m["sys"], m["dia"], m["map"], m["pr"]])


def main():
    ap = argparse.ArgumentParser(description="Загрузка замеров с тонометра CONTEC08A и усреднение")
    ap.add_argument("--archive", default="замеры_архив.csv", help="CSV-архив всех замеров")
    ap.add_argument("-o", "--out", default="давление_средние.xlsx", help="куда сохранить xlsx")
    ap.add_argument("--gap", type=int, default=10, help="пауза в минутах между сериями (по умолчанию 10)")
    ap.add_argument("--no-open", action="store_true", help="не открывать Excel-файл после сборки")
    a = ap.parse_args()

    count, recs = download()
    print(f"Скачано замеров: {len(recs)}" + (f" из {count}" if count is not None else ""))
    if count is not None and len(recs) < count:
        print("Внимание: получены не все замеры. Попробуйте ещё раз.")

    archive = Path(a.archive)
    rows = load_archive(archive)
    if not rows:  # первый запуск: забираем и старую историю из файлов BPMeter, если они есть
        from bp_average import DEFAULT_DATA_DIR, read_awp
        for f in sorted(DEFAULT_DATA_DIR.glob("*.awp")):
            rows += read_awp(f)
        if rows:
            print(f"Добавлено из {DEFAULT_DATA_DIR}: {len(rows)} замеров")
            uniq = {}
            for m in rows:
                uniq.setdefault((m["dt"], m["sys"], m["dia"], m["pr"]), m)
            rows = list(uniq.values())
    key = lambda m: (m["dt"], m["sys"], m["dia"], m["pr"])
    known = {key(m) for m in rows}
    new = [m for m in recs if key(m) not in known]
    rows += new
    save_archive(archive, rows)
    print(f"Новых замеров: {len(new)}, всего в архиве: {len(rows)} ({archive})")
    if not rows:
        sys.exit("Замеров нет.")

    from bp_average import build_workbook
    for i, m in enumerate(sorted(rows, key=lambda m: m["dt"]), 1):
        m["src"], m["num"] = "аппарат", i
    build_workbook(rows, a.gap, Path(a.out))
    if not a.no_open and hasattr(os, "startfile"):
        try:
            os.startfile(Path(a.out).resolve())  # откроется в Excel (или другой программе для .xlsx)
        except OSError as e:
            print("Не удалось открыть файл автоматически:", e)


if __name__ == "__main__":
    main()
