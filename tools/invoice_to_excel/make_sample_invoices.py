"""検証用のダミー請求書PDFを生成する。

顧客の実データを扱う前に、この生成物だけで動作確認を完結させるための道具。
テキスト層を持つPDF（システムが発行した請求書と同じ性質）を作る。
"""
from __future__ import annotations

import argparse
from pathlib import Path

from reportlab.lib.pagesizes import A4
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.cidfonts import UnicodeCIDFont
from reportlab.pdfgen import canvas

FONT = "HeiseiKakuGo-W5"

SAMPLES = [
    {
        "no": "INV-2026-0801",
        "date": "2026年8月5日",
        "to": "株式会社サンプル商事",
        "items": [
            ("配送手数料（7月分）", 12, 1500),
            ("梱包資材 Aタイプ", 200, 48),
            ("保管料", 1, 22000),
        ],
    },
    {
        "no": "INV-2026-0802",
        "date": "2026年8月5日",
        "to": "株式会社サンプル商事",
        "items": [
            ("配送手数料（7月分）", 8, 1500),
            ("梱包資材 Bタイプ", 150, 62),
        ],
    },
    {
        "no": "INV-2026-0803",
        "date": "2026年8月6日",
        "to": "株式会社サンプル商事",
        "items": [
            ("システム利用料", 1, 38000),
            ("追加アカウント", 3, 1200),
            ("初期設定サポート", 1, 15000),
            ("配送手数料（7月分）", 4, 1500),
        ],
    },
]


def yen(n: int) -> str:
    return f"{n:,}"


def draw_invoice(path: Path, data: dict) -> None:
    c = canvas.Canvas(str(path), pagesize=A4)
    w, h = A4
    c.setFont(FONT, 20)
    c.drawString(40, h - 60, "請求書")

    c.setFont(FONT, 10)
    c.drawString(40, h - 95, f"請求書番号: {data['no']}")
    c.drawString(40, h - 112, f"発行日: {data['date']}")
    c.drawString(40, h - 129, f"{data['to']} 御中")

    c.drawRightString(w - 40, h - 95, "サンプル物流株式会社")
    c.drawRightString(w - 40, h - 112, "東京都千代田区0-0-0")
    c.drawRightString(w - 40, h - 129, "登録番号: T0000000000000")

    y = h - 180
    c.setFont(FONT, 10)
    for label, x in (("品名", 40), ("数量", 330), ("単価", 400), ("金額", 500)):
        c.drawString(x, y, label)
    c.line(40, y - 5, w - 40, y - 5)

    y -= 24
    subtotal = 0
    for name, qty, unit in data["items"]:
        amount = qty * unit
        subtotal += amount
        c.drawString(40, y, name)
        c.drawRightString(370, y, yen(qty))
        c.drawRightString(455, y, yen(unit))
        c.drawRightString(w - 40, y, yen(amount))
        y -= 20

    tax = int(subtotal * 0.1)
    total = subtotal + tax

    y -= 12
    c.line(330, y, w - 40, y)
    y -= 20
    for label, value in (("小計", subtotal), ("消費税(10%)", tax), ("合計金額", total)):
        c.drawString(400, y, label)
        c.drawRightString(w - 40, y, f"¥{yen(value)}")
        y -= 20

    c.setFont(FONT, 9)
    c.drawString(40, 80, "お振込期限: 2026年8月31日")
    c.save()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", default="samples", help="出力ディレクトリ")
    args = ap.parse_args()

    pdfmetrics.registerFont(UnicodeCIDFont(FONT))
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    for data in SAMPLES:
        path = out / f"{data['no']}.pdf"
        draw_invoice(path, data)
        print(f"created: {path}")


if __name__ == "__main__":
    main()
