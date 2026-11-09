/**
 * メモ受け箱 — 受け口 v2（Google Apps Script）
 *
 * iPhone のショートカットや Surface のアプリから届いたメモを「メモ」シートに 1 行ずつ追記する。
 * 書き込み専用。メモの中身を外へ返す機能は持たせないこと（合言葉が漏れても読まれないように）。
 *
 * 受け取る JSON:
 *   token      合言葉（スクリプト プロパティの TOKEN_<端末名> と一致すること）
 *   id         メモID（例 20261103-153012-4821-iP）。同じIDは二重に登録しない
 *   createdAt  入力した時刻（ISO 8601。例 2026-11-03T15:30:12+09:00）
 *   type       'text'（省略時）、'ink'（手書き）、'ping'（速度測定。何も書き込まない）
 *   text       本文
 *   image      手書き画像（PNG を Base64 にしたもの。type が 'ink' のときだけ）
 *
 * 返事（文字だけ）:
 *   OK / OK-DUP（受け取り済み）/ OK-EMPTY（空なので何もしない）/ OK-PING（測定）
 *     → 「OK」で始まれば、端末は控えを消してよい
 *   NG（合言葉が違う）/ BADREQ（形式が不正）/ TOOBIG（大きすぎる）/ ERROR（その他）
 *     → 端末は控えを残す
 */

const SHEET_NAME = 'メモ';
const LOG_SHEET_NAME = 'ログ';
const HEADERS = ['日時', 'メモ', '分類', '状態', 'メモID', '端末', '画像', 'AIメモ', '受信日時'];
const LOG_HEADERS = ['受信日時', '結果', '端末', 'メモID', '処理ミリ秒', '備考'];
const COL_ID = 5;
const TOKEN_PREFIX = 'TOKEN_';
const MIN_TOKEN_LENGTH = 8;
const MAX_TEXT_LENGTH = 45000;            // セルの上限（5万文字）に余裕を持たせる
const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
const IMAGE_ROOT = ['受け箱', '手書き'];   // マイドライブ配下。この下に月別フォルダを作る
const INK_PLACEHOLDER = '（手書き・未読）';
const DATE_FORMAT = 'yyyy/MM/dd HH:mm:ss';

function doPost(e) {
  const started = Date.now();
  const log = { result: 'ERROR', device: '', id: '', note: '' };
  try {
    log.result = handle(e, log);
  } catch (err) {
    log.result = 'ERROR';
    log.note = String((err && err.message) || err);
  }
  writeLog(log, Date.now() - started);
  return ContentService.createTextOutput(log.result);
}

// ブラウザでURLを開いたときの動作確認用。メモの中身は返さない。
function doGet() {
  return ContentService.createTextOutput('受け口は動いています（v2）');
}

function handle(e, log) {
  let data;
  try {
    data = JSON.parse(e.postData.contents);
  } catch (err) {
    return 'BADREQ';
  }
  if (!data || typeof data !== 'object') return 'BADREQ';

  const device = deviceForToken(data.token);
  if (!device) return 'NG';
  log.device = device;

  // 速度測定用。シートには何も書かない
  if (data.type === 'ping') return 'OK-PING';

  // 旧ショートカット（IDなし）からの送信も受け付ける
  const id = data.id ? String(data.id) : 'SV-' + Utilities.getUuid();
  if (!/^[A-Za-z0-9_-]{1,80}$/.test(id)) return 'BADREQ';
  log.id = id;

  const isInk = data.type === 'ink';
  const text = String(data.text || '').trim();
  if (text.length > MAX_TEXT_LENGTH) return 'TOOBIG';
  if (!isInk && !text) return 'OK-EMPTY';

  let imageBytes = null;
  if (isInk) {
    try {
      imageBytes = Utilities.base64Decode(String(data.image || ''));
    } catch (err) {
      return 'BADREQ';
    }
    if (imageBytes.length > MAX_IMAGE_BYTES) return 'TOOBIG';
    if (!isPng(imageBytes)) return 'BADREQ';
  }

  const receivedAt = new Date();
  const createdAt = parseCreatedAt(data.createdAt, receivedAt, log);

  const lock = LockService.getScriptLock();
  lock.waitLock(10000);
  try {
    const sheet = getSheet();
    if (findRowById(sheet, id)) return 'OK-DUP';

    let imageCell = '';
    if (imageBytes) {
      const file = saveImage(imageBytes, id, createdAt);
      imageCell = '=HYPERLINK("' + file.getUrl() + '","画像")';
    }
    const row = [
      createdAt,
      "'" + (text || INK_PLACEHOLDER),  // 先頭の「'」で、= や - で始まるメモが数式・数値・日付に化けるのを防ぐ
      '',
      '未整理',
      id,
      device,
      imageCell,
      '',
      receivedAt,
    ];
    sheet.getRange(sheet.getLastRow() + 1, 1, 1, row.length).setValues([row]);
    return 'OK';
  } finally {
    lock.releaseLock();
  }
}

function deviceForToken(token) {
  if (typeof token !== 'string' || token.length < MIN_TOKEN_LENGTH) return '';
  const props = PropertiesService.getScriptProperties().getProperties();
  for (const key in props) {
    if (key.indexOf(TOKEN_PREFIX) === 0 && props[key] === token) {
      return key.slice(TOKEN_PREFIX.length) || '不明';
    }
  }
  return '';
}

// 入力時刻が読めない・未来すぎる・古すぎる場合は受信時刻で代用する
function parseCreatedAt(value, receivedAt, log) {
  if (!value) return receivedAt;
  const d = new Date(String(value));
  const oneDay = 24 * 60 * 60 * 1000;
  if (isNaN(d.getTime()) || d.getTime() > receivedAt.getTime() + oneDay || d.getFullYear() < 2020) {
    log.note = '入力時刻を読めないため受信時刻で代用: ' + String(value).slice(0, 40);
    return receivedAt;
  }
  return d;
}

function isPng(bytes) {
  return bytes.length > 8 &&
    (bytes[0] & 0xff) === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47;
}

function findRowById(sheet, id) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return 0;
  const cell = sheet.getRange(2, COL_ID, lastRow - 1, 1)
    .createTextFinder(id).matchEntireCell(true).findNext();
  return cell ? cell.getRow() : 0;
}

function saveImage(bytes, id, date) {
  const month = Utilities.formatDate(date, Session.getScriptTimeZone(), 'yyyy-MM');
  let folder = DriveApp.getRootFolder();
  IMAGE_ROOT.concat([month]).forEach(function (name) {
    const found = folder.getFoldersByName(name);
    folder = found.hasNext() ? found.next() : folder.createFolder(name);
  });
  return folder.createFile(Utilities.newBlob(bytes, 'image/png', id + '.png'));
}

function getSheet() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  let sheet = ss.getSheetByName(SHEET_NAME);
  if (!sheet) {
    sheet = ss.insertSheet(SHEET_NAME);
    prepareSheet(sheet);
  }
  return sheet;
}

function prepareSheet(sheet) {
  sheet.getRange(1, 1, 1, HEADERS.length).setValues([HEADERS]);
  sheet.setFrozenRows(1);
  sheet.getRange('A:A').setNumberFormat(DATE_FORMAT);
  sheet.getRange('I:I').setNumberFormat(DATE_FORMAT);
  sheet.getRange('E:E').setNumberFormat('@');
}

function getLogSheet() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  let sheet = ss.getSheetByName(LOG_SHEET_NAME);
  if (!sheet) {
    sheet = ss.insertSheet(LOG_SHEET_NAME);
    sheet.appendRow(LOG_HEADERS);
    sheet.setFrozenRows(1);
    sheet.getRange('A:A').setNumberFormat(DATE_FORMAT);
  }
  return sheet;
}

// 記録に失敗しても、メモの受け取り自体は失敗扱いにしない
function writeLog(log, elapsedMs) {
  try {
    getLogSheet().appendRow([new Date(), log.result, log.device, log.id, elapsedMs, "'" + log.note]);
  } catch (err) {
    console.error('ログの書き込みに失敗: ' + err);
  }
}

/* ===== ここから下は、エディタから手で実行する関数 ===== */

// 初回に1回実行する。見出しの追加・書式の設定・ログシート作成・端末ごとの合言葉の発行を行う。
// 何度実行しても、既にある合言葉は作り直さない。
function setup() {
  prepareSheet(getSheet());
  getLogSheet();
  const props = PropertiesService.getScriptProperties();
  ['iPhone', 'Surface'].forEach(function (device) {
    const key = TOKEN_PREFIX + device;
    if (!props.getProperty(key)) props.setProperty(key, newToken());
  });
  listTokens();
}

// 登録済みの合言葉を実行ログに表示する
function listTokens() {
  const props = PropertiesService.getScriptProperties().getProperties();
  Object.keys(props).filter(function (key) { return key.indexOf(TOKEN_PREFIX) === 0; })
    .forEach(function (key) { console.log(key.slice(TOKEN_PREFIX.length) + ' の合言葉: ' + props[key]); });
}

// 合言葉を作り直す（漏れたとき用）。DEVICE を書き換えてから実行する。古い合言葉はその場で無効になる。
function renewToken() {
  const DEVICE = 'iPhone';
  PropertiesService.getScriptProperties().setProperty(TOKEN_PREFIX + DEVICE, newToken());
  listTokens();
}

function newToken() {
  return (Utilities.getUuid() + Utilities.getUuid()).replace(/-/g, '');
}

// 動作確認。テスト用の行が1行追加される（確認後に消してよい）。
// 「結果: OK」「2回目: OK-DUP」と表示されれば正常。
function testPost() {
  const props = PropertiesService.getScriptProperties().getProperties();
  const key = Object.keys(props).filter(function (k) { return k.indexOf(TOKEN_PREFIX) === 0; })[0];
  if (!key) throw new Error('先に setup を実行してください');
  const body = JSON.stringify({
    token: props[key],
    id: 'TEST-' + Date.now(),
    createdAt: new Date().toISOString(),
    text: '- テスト送信（この行は消してOK）',
  });
  const e = { postData: { contents: body } };
  console.log('結果: ' + doPost(e).getContent());
  console.log('2回目: ' + doPost(e).getContent());
}
