using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using Microsoft.Win32;
using Windows.Devices.Input;

namespace QuickInk;

public static class Paths
{
    public static string Root
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickInk");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Sub(string name)
    {
        string dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

// 設定ファイル（%LOCALAPPDATA%\QuickInk\設定.json）。受け口のURLと Surface の合言葉を書く
public sealed record Config(string Url, string Token)
{
    public static string FilePath => Path.Combine(Paths.Root, "設定.json");

    public static Config? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) CreateTemplate();
            var json = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
            string url = ((string?)json["url"] ?? "").Trim();
            string token = ((string?)json["token"] ?? "").Trim();
            if (!url.StartsWith("https://", StringComparison.Ordinal) || token.Length < 8) return null;
            return new Config(url, token);
        }
        catch (Exception ex)
        {
            Log.Diag("設定ファイルを読めません: " + ex.Message);
            return null;
        }
    }

    public static void CreateTemplate()
    {
        if (File.Exists(FilePath)) return;
        var template = new JsonObject
        {
            ["url"] = "ここに受け口のURL（https://script.google.com/… で始まる）",
            ["token"] = "ここにSurfaceの合言葉",
        };
        File.WriteAllText(FilePath, template.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

// 記録フォルダ（%LOCALAPPDATA%\QuickInk\記録）に CSV とテキストで残す
public static class Log
{
    static readonly object Gate = new();

    static string FilePath(string name) => Path.Combine(Paths.Sub("記録"), name);

    static void Append(string name, string header, string line)
    {
        lock (Gate)
        {
            try
            {
                string path = FilePath(name);
                if (!File.Exists(path)) File.WriteAllText(path, header + Environment.NewLine, new System.Text.UTF8Encoding(true));
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // 記録に失敗しても本体の動作は止めない
            }
        }
    }

    static string Now => DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff");

    // 白紙が出るまでの時間など
    public static void Timing(string source, double ms) =>
        Append("速度.csv", "日時,きっかけ,ミリ秒", $"{Now},{source},{ms:F0}");

    public static void Send(string id, string result, double ms) =>
        Append("送信.csv", "日時,メモID,結果,ミリ秒", $"{Now},{id},{result.Replace(',', ' ')},{ms:F0}");

    public static void Diag(string message) =>
        Append("診断.txt", "QuickInk の診断記録", $"{Now} {message}");

    public static string Folder => Paths.Sub("記録");
}

public static class Diagnostics
{
    public static string Summary() =>
        $"アプリの種類={RuntimeInformation.ProcessArchitecture}（ARM64ならSnapdragon向けに直接動作）, " +
        $"OS={RuntimeInformation.OSArchitecture} {Environment.OSVersion.Version}, " +
        $"場所={Environment.ProcessPath}";
}

// Windows の起動時に裏で起動する設定（ユーザーごとの「スタートアップ」）
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "QuickInk";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) != null;
    }

    public static void Set(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

// 白紙を出すきっかけ。どれが Surface のペンで使えるかを確かめるため、全部を同時に待ち受けて記録する
public sealed class Triggers
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    const uint VK_M = 0x4D, VK_F18 = 0x81, VK_F19 = 0x82, VK_F20 = 0x83;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    readonly System.Windows.Window window;
    readonly Action<string> show;
    readonly string[] hotkeyNames =
    {
        "",
        "キーボード（Ctrl+Alt+M）",
        "ペンボタン1回？（Win+F20）",
        "ペンボタン2回？（Win+F19）",
        "ペンボタン長押し？（Win+F18）",
    };
    PenButtonListener? penListener;  // 参照を持ち続けないとイベントが止まる

    public Triggers(System.Windows.Window window, Action<string> show)
    {
        this.window = window;
        this.show = show;
    }

    public void Start()
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        (uint mods, uint vk)[] keys =
        {
            (0, 0),
            (MOD_CONTROL | MOD_ALT, VK_M),
            (MOD_WIN, VK_F20),
            (MOD_WIN, VK_F19),
            (MOD_WIN, VK_F18),
        };
        for (int id = 1; id < keys.Length; id++)
        {
            bool ok = RegisterHotKey(hwnd, id, keys[id].mods | MOD_NOREPEAT, keys[id].vk);
            Log.Diag($"{hotkeyNames[id]} の待ち受け: {(ok ? "OK" : "失敗（他のアプリが使用中の可能性）")}");
        }

        try
        {
            penListener = PenButtonListener.GetDefault();
            Log.Diag("PenButtonListener（ペンボタンを直接受け取る機能）: " + (penListener.IsSupported() ? "対応" : "非対応"));
            penListener.IsSupportedChanged += (_, _) =>
                Log.Diag("PenButtonListener の対応状況が変わりました: " + penListener.IsSupported());
            penListener.TailButtonClicked += (_, _) => show("ペンボタン1回（PenButtonListener）");
            penListener.TailButtonDoubleClicked += (_, _) => show("ペンボタン2回（PenButtonListener）");
            penListener.TailButtonLongPressed += (_, _) => show("ペンボタン長押し（PenButtonListener）");
        }
        catch (Exception ex)
        {
            Log.Diag("PenButtonListener を使えません: " + ex.Message);
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id > 0 && id < hotkeyNames.Length)
            {
                show(hotkeyNames[id]);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }
}

// タスクバー右下のアイコン
public sealed class Tray : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon icon;

    public Tray(AppCore core, Outbox outbox)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("メモを開く", null, (_, _) => core.RequestShow("右下のアイコン"));
        menu.Items.Add("今すぐ送信", null, (_, _) => outbox.Wake());
        var startup = new System.Windows.Forms.ToolStripMenuItem("Windowsの起動時に裏で起動") { Checked = Startup.IsEnabled() };
        startup.Click += (_, _) =>
        {
            Startup.Set(!startup.Checked);
            startup.Checked = Startup.IsEnabled();
        };
        menu.Items.Add(startup);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("設定ファイルを開く", null, (_, _) =>
        {
            Config.CreateTemplate();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.FilePath}\"") { UseShellExecute = true });
        });
        menu.Items.Add("記録フォルダを開く", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Folder}\"") { UseShellExecute = true }));
        menu.Items.Add("送信待ちフォルダを開く", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Outbox.PendingDir}\"") { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => core.Exit());

        icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "QuickInk",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => core.RequestShow("右下のアイコン");
        // 送信は裏のスレッドで動くので、アイコンの表示更新は画面のスレッドに戻してから行う
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        outbox.PendingChanged += count => dispatcher.BeginInvoke(() => UpdateText(count));
        UpdateText(outbox.PendingCount());
    }

    void UpdateText(int pending)
    {
        string configured = Config.Load() == null ? "（未設定）" : "";
        icon.Text = pending > 0 ? $"QuickInk 未送信 {pending}件{configured}" : $"QuickInk{configured}";
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
