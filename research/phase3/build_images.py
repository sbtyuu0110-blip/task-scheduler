"""出品用の画像を生成する。

価格を画像に焼き込んでいるため、価格が変わると画像も作り直しになる。
2026/8/31、¥9,800 で出品が弾かれた（ココナラは500円区切り）ときに、
画像を手で作り直す羽目になったので、価格を定数1つにまとめた。

    PRICE を直して実行するだけで、両方の画像が揃う。
"""
from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
ROOT = HERE.parent.parent
JP = "/usr/share/fonts/truetype/fonts-japanese-gothic.ttf"

# ココナラの価格は500円区切り。ここを直したら build_kit.py も再実行すること。
PRICE = 9500
assert PRICE % 500 == 0, f"ココナラの価格は500円区切り: {PRICE}"
YEN = f"¥{PRICE:,}"

PAPER, INK, MID, FAINT = "#FBFBF9", "#1A1D1A", "#5C625B", "#8A8F88"
RULE, RULE2 = "#DDE0DA", "#BFC4BC"
OK, OK_BG, NG, NG_BG, PANEL = "#1F5A3D", "#E7F0E9", "#A32B21", "#F7E8E6", "#F4F5F1"

f = lambda s: ImageFont.truetype(JP, s)


def thumbnail() -> None:
    """一覧に出る1枚目。1200×900。"""
    W, H, M = 1200, 900, 58
    img = Image.new("RGB", (W, H), PAPER)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, 14, H], fill=OK)

    d.text((M, 46), "毎月とどく請求書のPDFを", font=f(32), fill=MID)
    d.text((M, 94), "Excelに自動転記します", font=f(74), fill=INK)
    d.text((M, 200), "初回設定費", font=f(23), fill=FAINT)
    x = M + 142
    d.text((x, 192), YEN, font=f(34), fill=INK)
    x += d.textlength(YEN, font=f(34)) + 24
    d.text((x, 200), "／  同じ様式なら 毎月・何枚でも 追加費用なし", font=f(23), fill=MID)
    d.line([(M, 252), (W - M, 252)], fill=RULE, width=2)

    y = 280
    d.rectangle([M, y, W - M, y + 150], fill=NG_BG, outline=NG, width=2)
    d.rectangle([M, y, M + 6, y + 150], fill=NG)
    d.text((M + 32, y + 22), "合わない数字は、書き込まずに止めます。", font=f(38), fill=NG)
    d.text((M + 32, y + 80), "検算不一致 — 明細合計=31,140 だが 小計=30,000（差額 +1,140）", font=f(22), fill=INK)
    d.text((M + 32, y + 112), "読み取れなかった行があります： 前月調整額 -1,140", font=f(22), fill=INK)

    y = 466
    d.text((M, y), "検算を通った請求書だけ、Excelに追記します", font=f(24), fill=OK)
    y += 38
    xs = [M + 14, M + 230, M + 560, M + 690, M + 850]
    d.rectangle([M, y, W - M, y + 38], fill=PANEL, outline=RULE)
    for c, cx in zip(["請求書番号", "品名", "数量", "単価", "金額"], xs):
        d.text((cx, y + 9), c, font=f(21), fill=MID)
    y += 38
    for r in [("INV-2026-0801", "配送手数料（7月分）", "12", "1,500", "18,000"),
              ("INV-2026-0801", "梱包資材 Aタイプ", "200", "48", "9,600"),
              ("INV-2026-0801", "保管料", "1", "22,000", "22,000")]:
        for v, cx in zip(r, xs):
            d.text((cx, y + 9), v, font=f(22), fill=INK)
        d.line([(M, y + 40), (W - M, y + 40)], fill=RULE)
        y += 41

    y += 26
    d.rectangle([M, y, W - M, y + 92], fill=NG_BG, outline=NG, width=2)
    d.rectangle([M, y, M + 6, y + 92], fill=NG)
    d.text((M + 32, y + 16), "検算が合わなかった請求書は、1行も書き込みません", font=f(26), fill=NG)
    d.text((M + 32, y + 54), "差額まで表示しますので、その月だけご自身で確認していただけます。", font=f(21), fill=INK)
    y += 118
    d.text((M, y), "対象：テキスト形式のPDF（システム発行）／同じ発行元・同じ書式　　"
                   "対象外：スキャン・写真・手書き", font=f(21), fill=FAINT)
    img.save(ROOT / "thumbnail-coconala.png")


def portfolio() -> None:
    """出品ページ内に追加する実行結果。すべてダミーデータ。"""
    W, M = 1240, 56
    img = Image.new("RGB", (W, 1620), PAPER)
    d = ImageDraw.Draw(img)
    y = 52

    d.text((M, y), "請求書PDF → Excel／スプレッドシート 転記", font=f(38), fill=INK); y += 54
    d.text((M, y), "毎月くり返す手作業を自動化します。合わない数字は、書き込まずに止めます。",
           font=f(19), fill=MID); y += 34
    d.text((M, y), "※ 以下はすべてダミーデータでの実行結果です（実際のお客様データは使用していません）",
           font=f(16), fill=FAINT); y += 40
    d.line([(M, y), (W - M, y)], fill=RULE2, width=2); y += 40

    def card(tag, tagfg, accent, heading, lines, extra):
        nonlocal y
        h = 96 + len(lines) * 30 + (len(extra) * 28 + 22 if extra else 0)
        d.rectangle([M, y, W - M, y + h], fill=PANEL, outline=RULE)
        d.rectangle([M, y, M + 5, y + h], fill=accent)
        yy = y + 22
        tw = d.textlength(tag, font=f(16))
        d.rectangle([M + 24, yy, M + 24 + tw + 22, yy + 28],
                    fill=OK_BG if accent == OK else NG_BG, outline=accent)
        d.text((M + 35, yy + 4), tag, font=f(16), fill=tagfg)
        d.text((M + 24 + tw + 40, yy + 2), heading, font=f(24), fill=INK)
        yy += 48
        for ln, col in lines:
            d.text((M + 30, yy), ln, font=f(17), fill=col); yy += 30
        yy += 10
        for ln, col in extra:
            d.text((M + 30, yy), ln, font=f(19), fill=col); yy += 28
        y += h + 26

    card("正常", OK, OK, "3件を読み取り、Excelに転記",
         [("OK   INV-2026-0801.pdf : 合計 ¥54,560（明細3行）", INK),
          ("OK   INV-2026-0802.pdf : 合計 ¥23,430（明細2行）", INK),
          ("OK   INV-2026-0803.pdf : 合計 ¥68,860（明細4行）", INK),
          ("", INK),
          ("3件を 台帳.xlsx に追記しました", OK)],
         [("すべての行で 数量×単価=金額 / 明細合計=小計 / 小計+税=合計 を確認済み", MID)])

    card("停止", NG, NG, "書式が変わった月を検知して、書き込まない",
         [("要確認 INV-2026-0902.pdf:", INK),
          ("  検算不一致 — 明細合計=31,140 だが 小計=30,000（差額 +1,140）", NG),
          ("  読み取れなかった行があります: 前月調整額 -1,140", NG),
          ("", INK),
          ("転記できた請求書はありません。出力は変更していません。", MID)],
         [("先月まで無かった「前月調整額」を検知し、行の名前と差額を表示。", MID),
          ("黙って1行落ちた表より、止まった方が安全だと考えています。", INK)])

    card("対応範囲", NG, NG, "いま対応しているのは、テキスト形式のPDFです",
         [("要確認 SCANNED-sample.pdf:", INK),
          ("  テキスト層が見つかりません。スキャン・写真・手書きの請求書は", NG),
          ("  このツールの対象外です（誤認識を検算できないため）。", NG)],
         [("スキャン画像・写真・手書きは、誤読を検算できないため対象外です。", MID),
          ("発行元からデータ形式のPDFを受け取れる場合は、ご相談ください。", INK)])

    d.text((M, y), "転記結果（そのままスプレッドシートに貼り付けられます）", font=f(24), fill=INK); y += 42
    xs = [M + 16, M + 215, M + 375, M + 660, M + 750, M + 860, M + 990]
    d.rectangle([M, y, W - M, y + 38], fill=PANEL, outline=RULE)
    for c, cx in zip(["請求書番号", "発行日", "品名", "数量", "単価", "金額", "合計金額"], xs):
        d.text((cx, y + 9), c, font=f(16), fill=MID)
    y += 38
    for r in [["INV-2026-0801", "2026年8月5日", "配送手数料（7月分）", "12", "1,500", "18,000", "54,560"],
              ["INV-2026-0801", "", "梱包資材 Aタイプ", "200", "48", "9,600", ""],
              ["INV-2026-0801", "", "保管料", "1", "22,000", "22,000", ""],
              ["INV-2026-0802", "2026年8月5日", "配送手数料（7月分）", "8", "1,500", "12,000", "23,430"]]:
        d.line([(M, y + 34), (W - M, y + 34)], fill=RULE)
        for v, cx in zip(r, xs):
            d.text((cx, y + 8), v, font=f(19), fill=INK)
        y += 35

    y += 34
    d.line([(M, y), (W - M, y)], fill=RULE2, width=2); y += 24
    d.text((M, y), f"初回設定費 {YEN}  ・  同じ様式なら毎月・何枚でも追加費用なし  ・  納期7日",
           font=f(16), fill=MID); y += 26
    d.text((M, y), "お預かりした書類は作業完了後に削除します。取引先名・金額を第三者に開示することはありません。",
           font=f(16), fill=FAINT); y += 30
    img.crop((0, 0, W, y + 24)).save(ROOT / "portfolio-invoice-tool.png")


if __name__ == "__main__":
    thumbnail()
    portfolio()
    print(f"画像を生成: 価格 {YEN}（500円区切り: OK）")
