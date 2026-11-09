using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuickInk;

// 全画面の白紙。ペン＝書く（頭側で消す）、指＝上にフリックで送る、マウス＝書く（ペンのないPCでの確認用）。
// 閉じずに隠すだけなので、次に出すときは起動の待ち時間がない。
public sealed class InkWindow : Window
{
    const double BarHeight = 64;

    readonly Outbox outbox;
    readonly InkCanvas ink = new();
    readonly Border bar;
    readonly TextBlock status = new();

    long showRequestedAt;
    string showSource = "";
    int framesUntilLogged;
    Point? touchStart;
    DateTime touchStartTime;
    Point? barStart;

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    public InkWindow(Outbox outbox)
    {
        this.outbox = outbox;
        Title = "QuickInk";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.White;

        ink.Background = Brushes.White;
        ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black,
            Width = 2.5,
            Height = 2.5,
            FitToCurve = true,
            IgnorePressure = false,
        };
        ink.PreviewStylusDown += OnInkStylusDown;
        ink.PreviewStylusUp += OnInkStylusUp;
        ink.PreviewMouseDown += (_, e) =>
        {
            if (e.StylusDevice == null) ink.EditingMode = InkCanvasEditingMode.Ink;  // 本物のマウス
        };

        bar = new Border
        {
            Height = BarHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            Child = new TextBlock
            {
                Text = "↑ ここを上へ引き上げるか、指で上にフリックして送る　（Esc：閉じる / Ctrl+Z：1画消す）",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                FontSize = 14,
            },
        };
        bar.PreviewStylusDown += (_, e) => { barStart = e.GetPosition(this); e.Handled = true; };
        bar.PreviewStylusUp += (_, e) => { EndBarDrag(e.GetPosition(this)); e.Handled = true; };
        bar.PreviewMouseDown += (_, e) => { if (e.StylusDevice == null) barStart = e.GetPosition(this); };
        bar.PreviewMouseUp += (_, e) => { if (e.StylusDevice == null) EndBarDrag(e.GetPosition(this)); };

        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.VerticalAlignment = VerticalAlignment.Top;
        status.Margin = new Thickness(0, 8, 16, 0);
        status.Foreground = Brushes.Gray;
        status.FontSize = 13;
        status.IsHitTestVisible = false;

        var grid = new Grid();
        grid.Children.Add(ink);
        grid.Children.Add(bar);
        grid.Children.Add(status);
        Content = grid;

        KeyDown += OnKeyDown;
        Deactivated += (_, _) => Hide();  // 別のアプリに移ったら隠す（書きかけは残る）
        outbox.PendingChanged += count => Dispatcher.BeginInvoke(() => UpdateStatus(count));
    }

    // 起動時に一度だけ。ウィンドウを作って描画の準備まで済ませ、隠しておく
    public void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Opacity = 0;
        Show();
        Hide();
        Opacity = 1;
        UpdateStatus(outbox.PendingCount());
    }

    public void ShowForInk(string source, long requestedAt)
    {
        if (IsVisible)
        {
            Activate();
            return;
        }
        showSource = source;
        showRequestedAt = requestedAt;
        framesUntilLogged = 2;
        CompositionTarget.Rendering += OnRendering;
        Show();
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        ink.Focus();
    }

    // 表示してから2コマ描画した時点を「書ける状態」とみなして記録する
    void OnRendering(object? sender, EventArgs e)
    {
        if (--framesUntilLogged > 0) return;
        CompositionTarget.Rendering -= OnRendering;
        Log.Timing(showSource, Stopwatch.GetElapsedTime(showRequestedAt).TotalMilliseconds);
    }

    void OnInkStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch)
        {
            ink.EditingMode = InkCanvasEditingMode.None;  // 指では書かない
            touchStart = e.GetPosition(this);
            touchStartTime = DateTime.UtcNow;
        }
        else
        {
            ink.EditingMode = e.StylusDevice.Inverted ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
        }
    }

    void OnInkStylusUp(object sender, StylusEventArgs e)
    {
        if (touchStart is not Point start || e.StylusDevice.TabletDevice?.Type != TabletDeviceType.Touch) return;
        touchStart = null;
        Point end = e.GetPosition(this);
        double up = start.Y - end.Y;
        double sideways = Math.Abs(end.X - start.X);
        bool quick = (DateTime.UtcNow - touchStartTime).TotalMilliseconds <= 700;
        if (quick && up >= ActualHeight * 0.18 && sideways <= up) Send("指で上フリック");
    }

    void EndBarDrag(Point end)
    {
        if (barStart is not Point start) return;
        barStart = null;
        if (start.Y - end.Y >= ActualHeight * 0.15) Send("下のつまみを引き上げ");
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape) Hide();
        else if (ctrl && e.Key == Key.Enter) Send("Ctrl+Enter");
        else if (ctrl && e.Key == Key.Z && ink.Strokes.Count > 0) ink.Strokes.RemoveAt(ink.Strokes.Count - 1);
    }

    void Send(string how)
    {
        if (ink.Strokes.Count == 0)
        {
            Hide();
            return;
        }
        long started = Stopwatch.GetTimestamp();
        try
        {
            outbox.SaveInk(ink.Strokes);
            ink.Strokes.Clear();
            Hide();
            Log.Timing("保存（" + how + "）", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Diag("保存に失敗: " + ex);
            MessageBox.Show(this, "保存に失敗しました。書いた内容はまだ画面に残っています。\n" + ex.Message, "QuickInk");
        }
    }

    void UpdateStatus(int pending)
    {
        status.Text = pending > 0 ? "未送信 " + pending + " 件" : "";
    }
}
