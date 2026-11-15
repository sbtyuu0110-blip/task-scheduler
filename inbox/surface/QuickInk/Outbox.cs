using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickInk;

// 1回の送信の結果。画面側（Feedback）が「届きました」や「まだ届いていません」を出すのに使う
public enum SendResult
{
    Delivered,      // 受け口に届いた（OK）
    Retrying,       // 通信できない・受け口の不調。控えを残して、あとで自動で送り直す
    WrongToken,     // 合言葉が違う（NG）。設定を直すまで届かない
    Rejected,       // 受け口が受け付けない（BADREQ・TOOBIG・ファイルの欠け）。「送れなかった」へ移した
    NotConfigured,  // 送り先が設定されていない
}

public readonly record struct SendOutcome(SendResult Result, string Detail, int Pending);

// 「送信待ち」フォルダ。1メモ＝ メモID.json（送る内容の情報）＋ メモID.png（書いた部分の画像）＋ メモID.isf（筆跡の元データ）。
// 受け口が「OK」で始まる返事をしたら「送信済み」へ移し、30日後に消す。届くまで何度でも送り直す。
// 何度送っても受け付けられないもの（BADREQ・TOOBIG・ファイルの欠け）は「送れなかった」へ移し、後ろのメモを先に送る。
public sealed class Outbox
{
    const double MaxImageEdge = 1600;
    const double Margin = 24;
    static readonly TimeSpan KeepSent = TimeSpan.FromDays(30);

    readonly AutoResetEvent wake = new(false);
    public event Action<int>? PendingChanged;
    // 送信のスレッドから呼ばれる。画面を触るときは画面のスレッドに戻すこと
    public event Action<SendOutcome>? Outcome;

    public static string PendingDir => Paths.Sub("送信待ち");
    public static string SentDir => Paths.Sub("送信済み");
    public static string FailedDir => Paths.Sub("送れなかった");

    public int PendingCount() => Directory.GetFiles(PendingDir, "*.json").Length;

    public void Wake() => wake.Set();

    public string SaveInk(StrokeCollection strokes)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string id = now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(1000, 10000) + "-SF";

        WriteAtomically(Path.Combine(PendingDir, id + ".png"), stream => RenderPng(strokes).Save(stream));
        WriteAtomically(Path.Combine(PendingDir, id + ".isf"), stream => strokes.Save(stream));
        var meta = new JsonObject
        {
            ["id"] = id,
            ["createdAt"] = now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["type"] = "ink",
        };
        // json を最後に書く。送信は json があるものだけを対象にするので、書きかけを送ることはない
        WriteAtomically(Path.Combine(PendingDir, id + ".json"),
            stream => stream.Write(Encoding.UTF8.GetBytes(meta.ToJsonString())));

        PendingChanged?.Invoke(PendingCount());
        wake.Set();
        return id;
    }

    // 書いた部分だけを白地に切り抜く（AIの読み取りが安く・正確になる）
    static PngBitmapEncoder RenderPng(StrokeCollection strokes)
    {
        Rect bounds = strokes.GetBounds();
        bounds.Inflate(Margin, Margin);
        double scale = Math.Min(2.0, MaxImageEdge / Math.Max(bounds.Width, bounds.Height));
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            strokes.Draw(dc);
            dc.Pop();
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }

    // 一時ファイルに書いてから名前を変える（途中で電源が切れても壊れたファイルが残らない）
    static void WriteAtomically(string path, Action<Stream> write)
    {
        string temp = path + ".tmp";
        using (var stream = File.Create(temp)) write(stream);
        File.Move(temp, path, overwrite: true);
    }

    public void StartSender()
    {
        var thread = new Thread(SendLoop) { IsBackground = true, Name = "QuickInk.Sender" };
        thread.Start();
    }

    void SendLoop()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        int failures = 0;
        while (true)
        {
            DeleteOldSent();
            bool ok = SendAll(http);
            failures = ok ? 0 : Math.Min(failures + 1, 6);
            // 成功時は1分ごとに確認。失敗が続くと 15秒→30秒→…→8分 と間隔を空ける。新しいメモが来たらすぐ起きる
            TimeSpan wait = ok ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15 * Math.Pow(2, failures - 1));
            wake.WaitOne(wait);
        }
    }

    bool SendAll(HttpClient http)
    {
        Config? config = Config.Load();
        if (config == null)
        {
            // 未設定。設定されるまで送らずに貯めておく（貯まっているときは画面で知らせる）
            int waiting = PendingCount();
            if (waiting > 0) Report(SendResult.NotConfigured, "", waiting);
            return true;
        }

        foreach (string metaPath in Directory.GetFiles(PendingDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(metaPath);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                string pngPath = Path.Combine(PendingDir, id + ".png");
                JsonObject? meta = null;
                try
                {
                    meta = JsonNode.Parse(File.ReadAllText(metaPath))?.AsObject();
                }
                catch (System.Text.Json.JsonException)
                {
                    // 下で「送れなかった」へ移す
                }
                if (meta == null || !File.Exists(pngPath))
                {
                    GiveUp(id, meta == null ? "情報ファイルが壊れています" : "画像ファイルがありません");
                    continue;
                }
                var payload = new JsonObject
                {
                    ["token"] = config.Token,
                    ["id"] = id,
                    ["createdAt"] = (string?)meta["createdAt"],
                    ["type"] = "ink",
                    ["text"] = "",
                    ["image"] = Convert.ToBase64String(File.ReadAllBytes(pngPath)),
                };
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = http.PostAsync(config.Url, content).GetAwaiter().GetResult();
                string reply = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Log.Send(id, reply.Length > 40 ? reply[..40] : reply, ms);
                if (reply.StartsWith("OK", StringComparison.Ordinal))
                {
                    MoveAll(id, SentDir);
                    int left = PendingCount();
                    PendingChanged?.Invoke(left);
                    Report(SendResult.Delivered, id, left);
                    continue;
                }
                if (reply == "BADREQ" || reply == "TOOBIG")
                {
                    GiveUp(id, "受け口が受け付けませんでした（" + reply + "）");
                    continue;
                }
                // 合言葉違い（NG）や受け口の不調（ERROR など）は、直るまで残しておく
                Report(reply == "NG" ? SendResult.WrongToken : SendResult.Retrying, reply.Length > 40 ? reply[..40] : reply, PendingCount());
                return false;
            }
            catch (Exception ex)
            {
                Log.Send(id, "通信エラー: " + ex.GetType().Name, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Report(SendResult.Retrying, ex.GetType().Name, PendingCount());
                return false;
            }
        }
        return true;
    }

    static void MoveAll(string id, string toDir)
    {
        foreach (string ext in new[] { ".png", ".isf", ".json" })
        {
            string from = Path.Combine(PendingDir, id + ext);
            if (File.Exists(from)) File.Move(from, Path.Combine(toDir, id + ext), overwrite: true);
        }
    }

    // 書いた内容は消さずに「送れなかった」に残す（筆跡の元データ .isf も一緒に移す）
    void GiveUp(string id, string reason)
    {
        MoveAll(id, FailedDir);
        Log.Diag($"{id} を「送れなかった」に移しました: {reason}");
        int left = PendingCount();
        PendingChanged?.Invoke(left);
        Report(SendResult.Rejected, reason, left);
    }

    void Report(SendResult result, string detail, int pending)
    {
        try
        {
            Outcome?.Invoke(new SendOutcome(result, detail, pending));
        }
        catch (Exception ex)
        {
            Log.Diag("送信結果の表示に失敗: " + ex.Message);
        }
    }

    static void DeleteOldSent()
    {
        foreach (string path in Directory.GetFiles(SentDir))
        {
            if (DateTime.Now - File.GetLastWriteTime(path) > KeepSent) File.Delete(path);
        }
    }
}
