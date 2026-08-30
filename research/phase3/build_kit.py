"""coconala-fields.json から出品用のコピーページを生成する。

ココナラ側の字数上限は出品画面でしか分からず、これまで2度変更している
（サービス内容1000字／購入にあたってのお願い500字）。
上限が変わったら LIMITS を直して再実行すれば、字数表示ごと作り直せる。
"""
from __future__ import annotations

import html
import json
from pathlib import Path

HERE = Path(__file__).parent
OUT = HERE.parent.parent / "coconala-kit.html"

# 出品画面で確認できた上限。変わったらここを直す。
LIMITS = {"service": 1000, "please": 500}

FIELDS = [
    ("タイトル", "ココナラは「〜します」で終わる形式", "請求書PDFのExcel転記を自動化します"),
    ("キャッチコピー", "タイトルの下に出る一文", "合わない数字は転記せず停止／同じ様式を毎月処理する方へ"),
    ("価格", "「1フォーマット」だと1枚あたりと誤読されるため表記を変更済み", "9800"),
    ("お届け日数", "実際は3日で出せても、約束は7日にしておく", "7"),
    ("タグ", "「請求書」「PDF」「転記」は必ず入れる。ここが薄い領域",
     "Excel 請求書 PDF 転記 自動化 業務効率化 VBA GAS スプレッドシート 経理 データ入力 Python"),
]

CSS = """
:root{--paper:#FBFBF9;--panel:#F4F5F1;--ink:#1A1D1A;--mid:#5C625B;--faint:#8A8F88;
--rule:#DDE0DA;--rule2:#BFC4BC;--ok:#1F5A3D;--okbg:#E7F0E9;--ng:#A32B21;--ngbg:#F7E8E6;--accent:#2C4A7C;
--serif:"Zen Old Mincho",serif;--sans:"BIZ UDPGothic","Hiragino Sans","Noto Sans JP",sans-serif;
--mono:"JetBrains Mono",ui-monospace,Menlo,monospace;}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--paper:#141614;--panel:#1C1F1C;
--ink:#E8EAE6;--mid:#A2A89F;--faint:#767C74;--rule:#2C302C;--rule2:#3E443E;--ok:#6FBF8F;--okbg:#18291F;
--ng:#E8776B;--ngbg:#2B1917;--accent:#8FAFD9;}}
:root[data-theme="dark"]{--paper:#141614;--panel:#1C1F1C;--ink:#E8EAE6;--mid:#A2A89F;--faint:#767C74;
--rule:#2C302C;--rule2:#3E443E;--ok:#6FBF8F;--okbg:#18291F;--ng:#E8776B;--ngbg:#2B1917;--accent:#8FAFD9;}
*{box-sizing:border-box}
body{margin:0;background:var(--paper);color:var(--ink);font-family:var(--sans);font-size:16px;
line-height:1.85;-webkit-font-smoothing:antialiased}
.wrap{max-width:880px;margin:0 auto;padding:0 20px 100px}
header{padding:56px 0 26px;border-bottom:3px double var(--rule2);margin-bottom:34px}
.eyebrow{font-family:var(--mono);font-size:11px;letter-spacing:.2em;text-transform:uppercase;color:var(--faint);margin:0 0 16px}
h1{font-family:var(--serif);font-weight:900;font-size:clamp(28px,5vw,40px);line-height:1.3;margin:0 0 16px;text-wrap:balance}
.lede{color:var(--mid);margin:0;max-width:60ch;font-size:15.5px}
h2{font-family:var(--serif);font-weight:700;font-size:21px;margin:44px 0 6px}
.hint{color:var(--faint);font-size:13.5px;margin:0 0 18px}
.field{border:1px solid var(--rule);border-radius:3px;margin-bottom:14px;background:var(--panel)}
.fmeta{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap;padding:11px 14px 0}
.flabel{font-weight:700;font-size:13.5px;letter-spacing:.04em}
.fnote{color:var(--faint);font-size:12.5px}
.frow{display:flex;align-items:flex-start;gap:12px;padding:8px 14px 14px}
.ftext{flex:1;margin:0;font-family:var(--sans);font-size:15px;line-height:1.75;white-space:pre-wrap;word-break:break-word;min-width:0}
.block{border:1px solid var(--rule);border-radius:3px;background:var(--panel);overflow:hidden;margin-bottom:14px}
.bhead{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px 14px;border-bottom:1px solid var(--rule)}
.bhead b{font-size:13.5px;letter-spacing:.04em;line-height:1.6}
.count{font-family:var(--mono);font-size:11.5px;color:var(--ok);background:var(--okbg);
border:1px solid var(--ok);border-radius:2px;padding:2px 8px;white-space:nowrap;margin-left:auto;margin-right:10px}
.btext{margin:0;padding:18px 20px;font-family:var(--sans);font-size:14.5px;line-height:1.95;
white-space:pre-wrap;word-break:break-word;max-height:420px;overflow-y:auto}
.qa .btext{max-height:none;padding:16px 20px}
button.copy{font-family:var(--sans);font-size:13px;font-weight:700;white-space:nowrap;padding:7px 16px;
border:1px solid var(--ok);border-radius:3px;background:var(--okbg);color:var(--ok);cursor:pointer}
button.copy:hover{background:var(--ok);color:var(--paper)}
button.copy:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
button.copy.done{background:var(--ok);color:var(--paper)}
.warn{border-left:3px solid var(--ng);background:var(--ngbg);padding:14px 18px;margin:26px 0;font-size:14.5px;line-height:1.8}
.warn b{color:var(--ng)}
ol.steps{margin:0;padding-left:1.3em;max-width:62ch}
ol.steps li{margin-bottom:11px;line-height:1.8}
ol.steps li::marker{color:var(--faint);font-family:var(--mono)}
footer{margin-top:56px;padding-top:22px;border-top:3px double var(--rule2);font-family:var(--mono);
font-size:11.5px;color:var(--faint);line-height:1.9}
@media (max-width:560px){.frow{flex-direction:column;align-items:stretch}
button.copy{width:100%}.bhead{flex-wrap:wrap}.count{margin-left:0}}
@media (prefers-reduced-motion:reduce){*{transition:none!important}}
"""

SCRIPT = """
document.querySelectorAll("button.copy").forEach(function (b) {
  b.addEventListener("click", function () {
    var text = document.getElementById(b.dataset.target).textContent;
    function done() {
      var old = b.textContent;
      b.textContent = "コピーしました";
      b.classList.add("done");
      setTimeout(function () { b.textContent = old; b.classList.remove("done"); }, 1600);
    }
    function fallback() {
      var ta = document.createElement("textarea");
      ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
      document.body.appendChild(ta); ta.select();
      try { document.execCommand("copy"); done(); }
      catch (e) { b.textContent = "選択してコピーしてください"; }
      document.body.removeChild(ta);
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, fallback);
    } else { fallback(); }
  });
});
"""


def e(s: str) -> str:
    return html.escape(s)


def main() -> None:
    d = json.loads((HERE / "coconala-fields.json").read_text(encoding="utf-8"))
    service, please, faq = d["service"], d["please"], d["faq"]

    for key, txt in (("service", service), ("please", please)):
        if len(txt) > LIMITS[key]:
            raise SystemExit(f"{key} が上限超過: {len(txt)}字 > {LIMITS[key]}字")

    cards = "\n".join(
        f'<div class="field"><div class="fmeta"><span class="flabel">{e(l)}</span>'
        f'<span class="fnote">{e(n)}</span></div><div class="frow">'
        f'<pre class="ftext" id="f{i}">{e(v)}</pre>'
        f'<button class="copy" data-target="f{i}">コピー</button></div></div>'
        for i, (l, n, v) in enumerate(FIELDS))

    faq_html = "\n".join(
        f'<div class="block qa"><div class="bhead"><b>Q{i}. {e(q)}</b>'
        f'<button class="copy" data-target="a{i}">回答をコピー</button></div>'
        f'<pre class="btext" id="a{i}">{e(a)}</pre></div>'
        for i, (q, a) in enumerate(faq, 1))

    def block(key: str, label: str, txt: str) -> str:
        n, lim = len(txt), LIMITS[key]
        return (f'<div class="block"><div class="bhead"><b>{e(label)}</b>'
                f'<span class="count">{n} / {lim}字</span>'
                f'<button class="copy" data-target="{key}">全文をコピー</button></div>'
                f'<pre class="btext" id="{key}">{e(txt)}</pre></div>')

    OUT.write_text(f'''<title>請求書転記 出品キット</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Zen+Old+Mincho:wght@700;900&family=BIZ+UDPGothic:wght@400;700&family=JetBrains+Mono:wght@400;700&display=swap">
<style>{CSS}</style>

<div class="wrap">
<header>
  <p class="eyebrow">ココナラ 出品用 ／ 2026年8月30日</p>
  <h1>請求書PDFのExcel転記を自動化します</h1>
  <p class="lede">各欄の「コピー」を押して、出品画面にそのまま貼ってください。
  字数上限（サービス内容1000字・購入にあたってのお願い500字）に収まるよう振り分けてあります。
  記号や装飾は入っていないので、貼ったまま公開できます。</p>
</header>

<h2>基本の入力欄</h2>
<p class="hint">上から順に埋めていけば終わります。</p>
{cards}

<h2>サービス内容</h2>
<p class="hint">上限{LIMITS["service"]}字。余白{LIMITS["service"] - len(service)}字。</p>
{block("service", "サービス内容", service)}

<h2>購入にあたってのお願い</h2>
<p class="hint">上限{LIMITS["please"]}字。余白{LIMITS["please"] - len(please)}字。
対象外の条件・進め方・データの扱いだけに絞ってあります。</p>
{block("please", "購入にあたってのお願い", please)}

<h2>よくある質問</h2>
<p class="hint">1件ずつ登録します。反論を先に潰す欄です。特にQ1（枚数）とQ6（不安）は、
書いておかないと問い合わせすら来ずに離脱されます。</p>
{faq_html}

<h2>画像</h2>
<p class="hint">別途お渡しした2枚を使います。どちらもダミーデータなので、そのまま公開できます。</p>
<ol class="steps">
  <li><b>サムネイル</b>（1200×900）— 一覧に出る1枚目</li>
  <li><b>実行結果</b>（縦長）— サービス内容の下に追加</li>
</ol>

<div class="warn">
  <b>入らない欄があれば、欄名と上限字数を教えてください。</b>
  こちらからはココナラの画面が見えないため、上限は貼ってみて分かります。
  数字さえもらえれば、その場で詰め直します。<br><br>
  ほかに確認したいこと<br>
  ① 購入前メッセージでのファイル受け渡しが規約上どこまで可能か（「無料で1枚試す」の前提）<br>
  ② サムネイルの推奨比率（1200×900で作成）<br>
  ③ カテゴリは「Excelマクロ・VBA作成」で適切か
</div>

<h2>出品後</h2>
<ol class="steps">
  <li><b>1週間後</b> — 「請求書 Excel 転記」で検索して、自分の出品が出るか確認する。これは動作確認であって需要の判定ではありません</li>
  <li><b>3〜4週間後</b> — サービス分析で閲覧数を見る。<b>30未満なら判定不能</b>。タイトルとタグを変えて計測し直します</li>
  <li><b>閲覧が伸びなくても商品を否定しない</b> — これは9月中の3経路のうち1本です</li>
</ol>

<footer>
AI副業探索プロジェクト ／ Phase 3<br>
生成: research/phase3/build_kit.py ／ 原稿: coconala-fields.json
</footer>
</div>

<script>{SCRIPT}</script>
''', encoding="utf-8")
    print(f"{OUT.name} 生成 / サービス内容 {len(service)}字 ・ お願い {len(please)}字 ・ FAQ {len(faq)}件")


if __name__ == "__main__":
    main()
