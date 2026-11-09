/**
 * メモ受け箱 — AI振り分け（段階2）
 *
 * 「メモ」シートで状態が「未整理」の行を Claude API に渡し、次を書き戻す。
 *   C 分類 / D 状態（整理済み・要確認）/ H AIメモ / 手書きなら B に読み取った文章
 *   「一覧」シートに、1メモを1〜複数の項目に分けて追加
 * AIは「未整理」の行しか触らない。手で直した行が上書きされることはない。
 *
 * スクリプト プロパティ:
 *   ANTHROPIC_API_KEY  必須。Anthropic のコンソールで発行したAPIキー
 *   AI_MODEL           任意。省略時は claude-opus-5-5
 */

const CATEGORIES = ['タスク', 'アイデア', '改善メモ', '情報・記録', 'その他'];
const AREAS = ['仕事', '私用'];
const LIST_SHEET_NAME = '一覧';
const LIST_HEADERS = ['元メモID', '分類', '領域', '内容', '期限', '状態', '作成日時'];
const AI_LOG_SHEET_NAME = 'AIログ';
const AI_LOG_HEADERS = ['実行日時', 'モデル', '件数', '入力トークン', '出力トークン', '概算費用（円）', '備考'];
const COMPARE_SHEET_NAME = 'モデル比較';
const DEFAULT_MODEL = 'claude-opus-5-5';
const COMPARE_MODELS = ['claude-opus-5-5', 'claude-sonnet-5-5', 'claude-haiku-5-5'];
// 100万トークンあたりのドル（入力, 出力）。概算費用の表示にだけ使う
// 下の3つは、AIが処理を断ったときに自動でやり直す先のモデル（返事の model に入る）
const PRICES = {
  'claude-opus-5-5': [4, 20],
  'claude-sonnet-5-5': [2, 10],
  'claude-haiku-5-5': [0.10, 0.50],
  'claude-opus-5': [5, 25],
  'claude-opus-4-8': [5, 25],
  'claude-sonnet-5': [2, 10],
};
const YEN_PER_DOLLAR = 150;
const BATCH_SIZE = 8;                    // 1回の問い合わせを約60秒の制限内に収めるため
const TIME_BUDGET_MS = 4 * 60 * 1000;    // 1回の実行は6分まで。余裕を残して打ち切る
const API_URL = 'https://api.anthropic.com/v1/messages';

const SYSTEM_PROMPT = [
  'あなたは、ある1人の個人が思いつきを書き溜めた「メモ受け箱」の整理係です。',
  'メモは入力の速さを優先して書かれているので、短く、略語や書きかけが多いことを前提に読んでください。',
  '',
  '各メモについて次を返してください。',
  '- id: 渡されたメモIDをそのまま',
  '- reading: 手書き画像のメモなら、書かれている文章をそのまま文字に起こしたもの。矢印や囲みは「→」「【】」などで表す。文字のメモなら空文字',
  '- items: メモに含まれる用件を1つずつに分けたもの。「牛乳、A社に電話」なら2項目。中身のないメモやテストなら空の配列',
  '  - category: ' + CATEGORIES.join('／'),
  '    タスク＝やること。アイデア＝思いつき・企画。改善メモ＝仕事や生活のやり方を良くする気づき。情報・記録＝覚えておきたい事実や記録',
  '  - area: ' + AREAS.join('／') + '。判断できなければ文脈から近いほう',
  '  - content: 後で読んで分かる短い文。タスクは「〜する」の形にする。書かれていないことを付け足さない',
  '  - due: 期限が読み取れれば YYYY-MM-DD。「明日」「金曜」などはメモの入力日時を基準に日付にする。期限がなければ空文字',
  '- summary: メモ全体のひとこと要約（30字以内）',
  '- status: 整理できたら「整理済み」。読めない・意味が分からない・判断に迷う場合は「要確認」',
  '- reason: 「要確認」の理由（何が分からないか）。「整理済み」なら空文字',
  '',
  '推測で中身を作らないでください。迷ったら「要確認」にして、理由を書いてください。',
].join('\n');

const RESULT_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['memos'],
  properties: {
    memos: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['id', 'reading', 'items', 'summary', 'status', 'reason'],
        properties: {
          id: { type: 'string' },
          reading: { type: 'string' },
          items: {
            type: 'array',
            items: {
              type: 'object',
              additionalProperties: false,
              required: ['category', 'area', 'content', 'due'],
              properties: {
                category: { type: 'string', enum: CATEGORIES },
                area: { type: 'string', enum: AREAS },
                content: { type: 'string' },
                due: { type: 'string' },
              },
            },
          },
          summary: { type: 'string' },
          status: { type: 'string', enum: ['整理済み', '要確認'] },
          reason: { type: 'string' },
        },
      },
    },
  },
};

/* ===== メニュー ===== */

function onOpen() {
  SpreadsheetApp.getUi().createMenu('受け箱')
    .addItem('未整理を今すぐ整理', 'organizeInbox')
    .addSeparator()
    .addItem('自動整理をオン（1時間ごと）', 'enableHourlyOrganize')
    .addItem('自動整理をオン（朝7時と夕方18時）', 'enableTwiceDailyOrganize')
    .addItem('自動整理をオフ', 'disableAutoOrganize')
    .addToUi();
}

function enableHourlyOrganize() {
  disableAutoOrganize();
  ScriptApp.newTrigger('organizeInbox').timeBased().everyHours(1).create();
  notify('自動整理をオンにしました（1時間ごと）');
}

function enableTwiceDailyOrganize() {
  disableAutoOrganize();
  [7, 18].forEach(function (hour) {
    ScriptApp.newTrigger('organizeInbox').timeBased().atHour(hour).everyDays(1).create();
  });
  notify('自動整理をオンにしました（毎日7時と18時ごろ）');
}

function disableAutoOrganize() {
  ScriptApp.getProjectTriggers()
    .filter(function (t) { return t.getHandlerFunction() === 'organizeInbox'; })
    .forEach(function (t) { ScriptApp.deleteTrigger(t); });
}

/* ===== 整理の本体 ===== */

function organizeInbox() {
  // 受け口（Code.gs の doPost）が使うスクリプトロックとは別のロックを使う。
  // 同じロックだと、整理に数分かかる間、届いたメモが10秒待たされて ERROR になってしまう。
  // 受け口は行を末尾に足すだけで、整理は既存の行を書き換えるだけなので、同時に動いてもぶつからない。
  const lock = LockService.getDocumentLock() || LockService.getUserLock();
  if (!lock.tryLock(1000)) return;  // 別の整理が動いている
  const state = { started: Date.now(), done: 0, stopped: false };
  try {
    const sheet = getSheet();
    const model = aiModel();
    const pending = readMemos(sheet).filter(function (m) { return m.status === '未整理'; });
    if (pending.length === 0) return;  // 未整理が0件ならAIを呼ばない（費用ゼロ）

    for (let i = 0; i < pending.length && !state.stopped; i += BATCH_SIZE) {
      if (outOfTime(state)) break;  // 残りは次回
      organizeBatch(sheet, model, pending.slice(i, i + BATCH_SIZE), state);
    }
  } finally {
    lock.releaseLock();
  }
  notify(state.done + '件を整理しました');
}

function outOfTime(state) {
  return Date.now() - state.started > TIME_BUDGET_MS;
}

// 失敗したときは原因によって「今回はやめて次回に再試行」か「半分に分けてやり直す」。
// 1件だけでも失敗するメモは「要確認」にして、そのメモのせいでほかのメモの整理が止まり続けないようにする。
function organizeBatch(sheet, model, batch, state) {
  if (state.stopped || outOfTime(state)) return;
  let res;
  try {
    res = callClaude(model, batch);
  } catch (err) {
    const msg = String((err && err.message) || err);
    const kind = errorKind(msg);
    if (kind === 'stop' || (kind === 'split' && batch.length === 1)) {
      writeAiLog(model, batch.length, null, 'エラー（次回に再試行）: ' + msg.slice(0, 300));
      state.stopped = true;
      return;
    }
    if (batch.length === 1) {
      markNeedsReview(sheet, batch[0], 'AIで処理できませんでした（' + msg.slice(0, 120) + '）');
      writeAiLog(model, 1, null, '1件を「要確認」にしました: ' + msg.slice(0, 300));
      return;
    }
    writeAiLog(model, batch.length, null, '分けてやり直します: ' + msg.slice(0, 300));
    splitAndRetry(sheet, model, batch, state);
    return;
  }
  if (res.refusal) {
    writeAiLog(res.model, batch.length, res.usage, 'AIが処理を断りました' + (batch.length > 1 ? '（分けてやり直します）' : '（「要確認」にしました）'));
    if (batch.length > 1) {
      splitAndRetry(sheet, model, batch, state);  // 断られる原因のメモだけを「要確認」にするため
    } else {
      markNeedsReview(sheet, batch[0], 'AIがこのメモの処理を断りました');
    }
    return;
  }
  state.done += applyResults(sheet, batch, res.memos);
  writeAiLog(res.model, batch.length, res.usage, '');
}

function splitAndRetry(sheet, model, batch, state) {
  const half = Math.ceil(batch.length / 2);
  organizeBatch(sheet, model, batch.slice(0, half), state);
  organizeBatch(sheet, model, batch.slice(half), state);
}

// stop  : APIキー・残高・混雑など、メモの中身と関係ない問題。今回はやめて次回に再試行する
// memo  : 特定のメモが原因かもしれない問題。分けて試し、1件でも失敗するメモは「要確認」にする
// split : 時間切れや通信の問題。小さく分けて試し、1件でも失敗するなら次回に再試行する
function errorKind(msg) {
  if (/ANTHROPIC_API_KEY/.test(msg) || /^HTTP (401|403|404|429|5\d\d)/.test(msg) || /credit|balance|billing/i.test(msg)) {
    return 'stop';
  }
  if (/^HTTP (400|413)/.test(msg) || /^画像を開けません|途切れ|本文がありません|JSON/.test(msg)) return 'memo';
  return 'split';
}

function readMemos(sheet) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return [];
  const range = sheet.getRange(2, 1, lastRow - 1, 9);
  const values = range.getValues();
  const formulas = range.getFormulas();
  return values.map(function (v, i) {
    const match = /\/d\/([A-Za-z0-9_-]+)/.exec(formulas[i][6] || '');
    return {
      row: i + 2,
      createdAt: v[0],
      text: String(v[1]),
      status: String(v[3]),
      id: String(v[4]),
      imageFileId: match ? match[1] : '',
    };
  }).filter(function (m) { return m.id; });
}

function callClaude(model, memos) {
  const apiKey = PropertiesService.getScriptProperties().getProperty('ANTHROPIC_API_KEY');
  if (!apiKey) throw new Error('スクリプト プロパティに ANTHROPIC_API_KEY がありません');

  const content = [{ type: 'text', text: '次の' + memos.length + '件のメモを整理してください。' }];
  memos.forEach(function (m) {
    const when = m.createdAt instanceof Date
      ? Utilities.formatDate(m.createdAt, Session.getScriptTimeZone(), 'yyyy-MM-dd（E）HH:mm')
      : String(m.createdAt);
    if (m.imageFileId) {
      content.push({ type: 'text', text: 'メモID: ' + m.id + '\n入力日時: ' + when + '\n種類: 手書き（次の画像）' });
      let bytes;
      try {
        bytes = DriveApp.getFileById(m.imageFileId).getBlob().getBytes();
      } catch (err) {
        throw new Error('画像を開けません（削除された可能性。メモID ' + m.id + '）: ' + ((err && err.message) || err));
      }
      content.push({ type: 'image', source: { type: 'base64', media_type: 'image/png', data: Utilities.base64Encode(bytes) } });
    } else {
      content.push({ type: 'text', text: 'メモID: ' + m.id + '\n入力日時: ' + when + '\n本文:\n' + m.text });
    }
  });

  const body = {
    model: model,
    max_tokens: 16000,
    system: SYSTEM_PROMPT,
    output_config: { effort: 'low', format: { type: 'json_schema', schema: RESULT_SCHEMA } },
    messages: [{ role: 'user', content: content }],
  };
  const headers = { 'x-api-key': apiKey, 'anthropic-version': '2026-10-21' };
  // 安全上の理由でAIが処理を断ったとき、別のモデルで自動的にやり直す（Haikuには無い機能）
  if (model.indexOf('haiku') === -1) {
    body.fallbacks = 'default';
    headers['anthropic-beta'] = 'server-side-fallback-2026-07-01';
  }

  const resp = UrlFetchApp.fetch(API_URL, {
    method: 'post',
    contentType: 'application/json',
    headers: headers,
    payload: JSON.stringify(body),
    muteHttpExceptions: true,
  });
  const code = resp.getResponseCode();
  const text = resp.getContentText();
  if (code !== 200) throw new Error('HTTP ' + code + ': ' + text.slice(0, 300));

  const data = JSON.parse(text);
  if (data.stop_reason === 'refusal') return { refusal: true, usage: data.usage, model: data.model };
  if (data.stop_reason === 'max_tokens') throw new Error('出力が上限で途切れました');
  const textBlocks = (data.content || []).filter(function (b) { return b.type === 'text'; });
  if (textBlocks.length === 0) throw new Error('AIの返事に本文がありません');
  const parsed = JSON.parse(textBlocks[textBlocks.length - 1].text);
  return { memos: parsed.memos || [], usage: data.usage, model: data.model || model };
}

function applyResults(sheet, batch, results) {
  const byId = {};
  results.forEach(function (r) { byId[r.id] = r; });
  const now = new Date();
  const listRows = [];
  let count = 0;

  batch.forEach(function (m) {
    const r = byId[m.id];
    if (!r) return;  // 返ってこなかったメモは「未整理」のまま次回に回す
    if (!stillPending(sheet, m)) return;

    const items = (r.items || []).filter(function (it) { return it && String(it.content || '').trim(); });
    const categories = items.map(function (it) { return it.category; })
      .filter(function (c, i, a) { return a.indexOf(c) === i; });
    const category = categories.length === 0 ? 'その他' : (categories.length === 1 ? categories[0] : '複数');
    const status = r.status === '要確認' ? '要確認' : '整理済み';
    const note = status === '要確認' ? ('要確認: ' + (r.reason || '')) : (r.summary || '');

    if (m.imageFileId && String(r.reading || '').trim()) {
      sheet.getRange(m.row, 2).setValue("'" + String(r.reading).trim());
    }
    sheet.getRange(m.row, 3, 1, 2).setValues([[category, status]]);
    sheet.getRange(m.row, 8).setValue("'" + note);

    if (status === '整理済み') {
      items.forEach(function (it) {
        const due = /^\d{4}-\d{2}-\d{2}$/.test(it.due || '') ? it.due : '';
        listRows.push([m.id, it.category, it.area, "'" + String(it.content).trim(), due, '未着手', now]);
      });
    }
    count++;
  });

  if (listRows.length) {
    const list = getListSheet();
    list.getRange(list.getLastRow() + 1, 1, listRows.length, LIST_HEADERS.length).setValues(listRows);
  }
  return count;
}

// 整理中に行が削除されて位置がずれた、または手で状態を変えられた場合は触らない
function stillPending(sheet, m) {
  const v = sheet.getRange(m.row, 4, 1, 2).getValues()[0];
  return String(v[0]) === '未整理' && String(v[1]) === m.id;
}

function markNeedsReview(sheet, m, reason) {
  if (!stillPending(sheet, m)) return;
  sheet.getRange(m.row, 4).setValue('要確認');
  sheet.getRange(m.row, 8).setValue("'要確認: " + reason);
}

/* ===== モデル比較（どのモデルにするか決めるためのテスト。メモシートは変更しない） ===== */
// 直近20件のメモを1つのモデルで整理し、「モデル比較」シートに結果を並べる。
// 3つを順に実行して、手書きの読み取りと分類を見比べる。

function compareOpus() { compareModel(COMPARE_MODELS[0]); }
function compareSonnet() { compareModel(COMPARE_MODELS[1]); }
function compareHaiku() { compareModel(COMPARE_MODELS[2]); }

function compareModel(model) {
  const memos = readMemos(getSheet()).slice(-20);
  if (memos.length === 0) throw new Error('メモがありません');
  const sheet = getCompareSheet(memos);
  const col = 3 + COMPARE_MODELS.indexOf(model) * 2;
  const resultById = {};
  let input = 0, output = 0;
  for (let i = 0; i < memos.length; i += BATCH_SIZE) {
    const res = callClaude(model, memos.slice(i, i + BATCH_SIZE));
    if (res.usage) { input += res.usage.input_tokens || 0; output += res.usage.output_tokens || 0; }
    (res.memos || []).forEach(function (r) { resultById[r.id] = r; });
  }
  const cells = memos.map(function (m) {
    const r = resultById[m.id];
    if (!r) return ['（結果なし）', ''];
    const items = (r.items || []).map(function (it) {
      const due = /^\d{4}-\d{2}-\d{2}$/.test(it.due || '') ? '（' + it.due + '）' : '';
      return '[' + it.category + '/' + it.area + '] ' + it.content + due;
    }).join('\n');
    return ["'" + (r.reading || ''), "'" + r.status + (r.reason ? '：' + r.reason : '') + '\n' + items];
  });
  sheet.getRange(1, col, 1, 2).setValues([[model + ' 読み取り', model + ' 整理結果']]);
  sheet.getRange(2, col, cells.length, 2).setValues(cells);
  const yen = estimateYen(model, { input_tokens: input, output_tokens: output });
  sheet.getRange(memos.length + 3, col, 1, 2)
    .setValues([['費用（20件）: 約' + yen + '円', '入力 ' + input + ' / 出力 ' + output + ' トークン']]);
  writeAiLog(model, memos.length, { input_tokens: input, output_tokens: output }, 'モデル比較');
}

function getCompareSheet(memos) {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  let sheet = ss.getSheetByName(COMPARE_SHEET_NAME);
  if (!sheet) sheet = ss.insertSheet(COMPARE_SHEET_NAME);
  sheet.getRange(1, 1, 1, 2).setValues([['メモID', '元のメモ']]);
  sheet.getRange(2, 1, memos.length, 2).setValues(memos.map(function (m) {
    return [m.id, "'" + (m.imageFileId ? '（手書き画像）' : m.text)];
  }));
  sheet.setFrozenRows(1);
  return sheet;
}

/* ===== 補助 ===== */

function aiModel() {
  return PropertiesService.getScriptProperties().getProperty('AI_MODEL') || DEFAULT_MODEL;
}

function getListSheet() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  let sheet = ss.getSheetByName(LIST_SHEET_NAME);
  if (!sheet) {
    sheet = ss.insertSheet(LIST_SHEET_NAME);
    sheet.appendRow(LIST_HEADERS);
    sheet.setFrozenRows(1);
    sheet.getRange('E:E').setNumberFormat('yyyy/MM/dd');
    sheet.getRange('G:G').setNumberFormat(DATE_FORMAT);
  }
  return sheet;
}

function estimateYen(model, usage) {
  const p = PRICES[model];
  if (!p || !usage) return '';
  const dollars = ((usage.input_tokens || 0) * p[0] + (usage.output_tokens || 0) * p[1]) / 1e6;
  return Math.round(dollars * YEN_PER_DOLLAR * 100) / 100;
}

function writeAiLog(model, count, usage, note) {
  try {
    const ss = SpreadsheetApp.getActiveSpreadsheet();
    let sheet = ss.getSheetByName(AI_LOG_SHEET_NAME);
    if (!sheet) {
      sheet = ss.insertSheet(AI_LOG_SHEET_NAME);
      sheet.appendRow(AI_LOG_HEADERS);
      sheet.setFrozenRows(1);
    }
    sheet.appendRow([new Date(), model, count,
      usage ? usage.input_tokens : '', usage ? usage.output_tokens : '',
      estimateYen(model, usage), "'" + note]);
  } catch (err) {
    console.error('AIログの書き込みに失敗: ' + err);
  }
}

// メニューから実行したときだけ画面に知らせる（自動実行のときは何もしない）
function notify(message) {
  try {
    SpreadsheetApp.getActiveSpreadsheet().toast(message, '受け箱');
  } catch (err) {
    console.log(message);
  }
}
