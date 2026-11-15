using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace QuickInk;

public enum ToastKind { Queued, Delivered, Warning, Error }

public static class Motion
{
    // Windows の「アニメーション効果」（設定 → アクセシビリティ → 視覚効果）。オフなら動きを省く
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
}

// 画面上部の中央に出る小さな札。白紙が閉じたあとも見えるよう、白紙とは別の小さな窓にする。
// 触っても下の画面に素通りし、選択中のアプリを切り替えない。
public sealed class Toast : Window
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const double QueuedMinimumMs = 900;  // 「送信待ちに入れました」は、届いた知らせが来ても最低これだけ出しておく

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    readonly TextBlock text = new()
    {
        FontSize = 16,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 560,
    };
    readonly Border box;
    readonly DispatcherTimer holdTimer = new();
    ToastKind? current;
    long shownAt;
    (ToastKind Kind, string Message)? next;
    int fadeGeneration;

    public Toast()
    {
        Title = "QuickInk";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;  // 札は小さく、書く場所でもないので透明にしてよい
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        IsHitTestVisible = false;
        box = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 10, 20, 11),
            Child = text,
        };
        Content = box;
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            ScreenBackdrop.Exclude(hwnd);  // 白紙の後ろの写しに札が写り込まないように
        };
        SizeChanged += (_, _) => Place();
        holdTimer.Tick += (_, _) => OnHoldElapsed();
    }

    public bool IsShowing => current != null;

    // 起動時に一度だけ。窓を作っておき、初めて札を出すときの待ち時間をなくす
    public void Prepare()
    {
        Opacity = 0;
        Show();
        Hide();
    }

    public void Present(ToastKind kind, string message)
    {
        if (current is ToastKind showing)
        {
            if (Priority(kind) < Priority(showing)) return;  // 警告を出している間は、軽い知らせで上書きしない
            if (kind == ToastKind.Queued && showing == ToastKind.Delivered) return;  // 先に「届きました」が出ていれば、それを残す
            double elapsed = Elapsed();
            if (showing == ToastKind.Queued && kind == ToastKind.Delivered && elapsed < QueuedMinimumMs)
            {
                // 「送信待ちに入れました」を読み終えるくらい出してから、「届きました」に替える
                next = (kind, message);
                holdTimer.Stop();
                holdTimer.Interval = TimeSpan.FromMilliseconds(QueuedMinimumMs - elapsed);
                holdTimer.Start();
                return;
            }
        }
        ShowNow(kind, message);
    }

    public void HideNow()
    {
        fadeGeneration++;
        holdTimer.Stop();
        next = null;
        current = null;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (IsVisible) Hide();
    }

    void ShowNow(ToastKind kind, string message)
    {
        fadeGeneration++;
        next = null;
        current = kind;
        shownAt = Environment.TickCount64;
        text.Text = message;
        box.Background = Fill(kind);
        // 出している最中に中身を替えるときは、今の濃さから続ける（一瞬消えてちらつかないように）
        double opacityNow = IsVisible ? Opacity : (Motion.Enabled ? 0 : 1);
        BeginAnimation(OpacityProperty, null);
        Opacity = opacityNow;
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
        if (Motion.Enabled) BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        else Opacity = 1;
        holdTimer.Stop();
        holdTimer.Interval = Hold(kind);
        holdTimer.Start();
    }

    void OnHoldElapsed()
    {
        holdTimer.Stop();
        if (next is { } n)
        {
            ShowNow(n.Kind, n.Message);
            return;
        }
        if (!Motion.Enabled)
        {
            HideNow();
            return;
        }
        int generation = ++fadeGeneration;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) =>
        {
            if (generation == fadeGeneration) HideNow();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    double Elapsed() => Environment.TickCount64 - shownAt;

    void Place()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 24;
    }

    static int Priority(ToastKind kind) => kind is ToastKind.Warning or ToastKind.Error ? 2 : 1;

    static TimeSpan Hold(ToastKind kind) => TimeSpan.FromMilliseconds(kind switch
    {
        ToastKind.Queued => 1000,
        ToastKind.Delivered => 1200,
        ToastKind.Warning => 5000,
        _ => 7000,
    });

    static Brush Fill(ToastKind kind) => kind switch
    {
        ToastKind.Queued => Frozen(0xE6, 0x26, 0x26, 0x26),
        ToastKind.Delivered => Frozen(0xE6, 0x1F, 0x51, 0x30),
        ToastKind.Warning => Frozen(0xF2, 0xB4, 0x53, 0x09),
        _ => Frozen(0xF2, 0xB9, 0x1C, 0x1C),
    };

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

// 送った結果をどこにどう知らせるかを決める。
// 白紙を使っている間は白紙の右上に、白紙が閉じているときは札で知らせる。
// 届かないときは必ず知らせる（同じ理由で何度も届かないときは、新しいメモを書くまで繰り返さない）。
public sealed class Feedback
{
    readonly InkWindow window;
    readonly Toast toast;
    readonly Tray tray;
    bool stuck;                 // 送信待ちが届かずに残っている
    bool newSinceNotice = true; // 最後に知らせてから、新しいメモを書いたか
    int deliveredBurst;
    long lastDeliveredAt;

    public Feedback(InkWindow window, Toast toast, Tray tray, Outbox outbox)
    {
        this.window = window;
        this.toast = toast;
        this.tray = tray;
        window.Saved += () => newSinceNotice = true;
        window.Departed += interrupted =>
        {
            const string queued = "✓ 送信待ちに入れました";
            if (interrupted) window.FlashStatus(queued, StatusTone.Info, TimeSpan.FromSeconds(1.5));
            else toast.Present(ToastKind.Queued, queued);
        };
        Dispatcher dispatcher = window.Dispatcher;
        outbox.Outcome += outcome => dispatcher.BeginInvoke(() => OnOutcome(outcome));
    }

    void OnOutcome(SendOutcome o)
    {
        switch (o.Result)
        {
            case SendResult.Delivered:
            {
                bool recovered = stuck;
                long now = Environment.TickCount64;
                deliveredBurst = now - lastDeliveredAt < 4000 ? deliveredBurst + 1 : 1;
                lastDeliveredAt = now;
                string message = recovered ? "✓ 未送信だったメモが届きました" : "✓ 届きました";
                if (deliveredBurst > 1) message += $"（{deliveredBurst}件）";
                if (o.Pending == 0) stuck = false;
                Tell(ToastKind.Delivered, message);
                break;
            }
            case SendResult.Retrying:
                Alert(ToastKind.Warning, $"⚠ まだ届いていません。つながったら自動で送ります（未送信 {o.Pending} 件）", balloon: false);
                break;
            case SendResult.WrongToken:
                Alert(ToastKind.Error, "⚠ 合言葉が違うため届きません。右下のアイコン →「設定ファイルを開く」で token を確かめてください", balloon: true);
                break;
            case SendResult.NotConfigured:
                Alert(ToastKind.Error, "⚠ 送り先が設定されていないため送れません。右下のアイコン →「設定ファイルを開く」", balloon: true);
                break;
            case SendResult.Rejected:
                newSinceNotice = true;  // 1件ごとに別の問題なので、毎回知らせる
                Alert(ToastKind.Error, "⚠ 受け口が受け付けなかったメモを「送れなかった」フォルダに移しました（右下のアイコンから開けます）", balloon: true, keepsQueue: true);
                break;
        }
        if (o.Pending == 0) stuck = false;
        window.SetSendState(o.Pending, stuck);
        tray.SetAlert(stuck);
    }

    void Alert(ToastKind kind, string message, bool balloon, bool keepsQueue = false)
    {
        bool notify = !stuck || newSinceNotice;
        if (!keepsQueue) stuck = true;
        if (!notify) return;
        newSinceNotice = false;
        Tell(kind, message);
        // Windows の通知にも出す（見逃しても通知センターに残る）
        if (balloon) tray.Balloon(message.TrimStart('⚠', ' '));
    }

    void Tell(ToastKind kind, string message)
    {
        if (window.IsInUse)
        {
            StatusTone tone = kind switch
            {
                ToastKind.Error => StatusTone.Error,
                ToastKind.Warning => StatusTone.Warning,
                _ => StatusTone.Info,
            };
            window.FlashStatus(message, tone, TimeSpan.FromSeconds(kind is ToastKind.Error or ToastKind.Warning ? 8 : 2));
        }
        else
        {
            toast.Present(kind, message);
        }
    }
}
