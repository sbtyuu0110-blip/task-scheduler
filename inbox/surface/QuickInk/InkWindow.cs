using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace QuickInk;

public enum StatusTone { Quiet, Info, Warning, Error }

// 全画面の白紙。ペン＝書く（頭側で消す）、指＝上へなぞって送る、マウス＝書く（ペンのないPCでの確認用）。
// 閉じずに隠すだけなので、次に出すときは起動の待ち時間がない。
//
// 送るときの動き：紙（白紙と書いた内容）が指について上へ動き、少しずつ薄くなる。離したときに十分動いていたか
// 勢いがあれば、そのまま上へ滑って消える。足りなければ元の位置へ戻る（送らない）。
// 窓そのものは透明にしない（透明な窓はインクの描画が重くなるため）。代わりに、動かすときだけ窓の中に
// 「この窓の後ろの画面」の写しを敷き、その上で紙を動かす。動きの間は紙を一枚の絵として扱い（BitmapCache）、軽く動かす。
public sealed class InkWindow : Window
{
    enum PaperState { Hidden, Shown, Dragging, Returning, Departing, Fading }

    const double BarHeight = 64;
    const double ShadowHeight = 28;
    const double DragStartDistance = 8;    // これだけ上へ動いたら、紙が指につき始める
    const double SendDistanceRatio = 0.22; // 画面の高さのこの割合まで上へ動かしたら送る
    const double FlickVelocity = 0.8;      // 1ミリ秒あたりの移動量。これより速く上へはらったら送る
    const double FlickMinDistance = 48;
    const double DepartMs = 250, ReturnMs = 220, FadeMs = 180;
    const int BackdropWaitMs = 80;          // 画面の写しを待つ上限。間に合わなければ灰色で動かし、届いたら差し替える

    static readonly Brush FallbackBackdrop = Frozen(Color.FromRgb(0xDA, 0xDA, 0xDA));

    readonly Outbox outbox;
    readonly Grid root = new();
    readonly Grid paper = new() { Background = Brushes.White };
    readonly TranslateTransform paperShift = new();
    readonly InkCanvas ink = new();
    readonly InkPresenter departing = new() { IsHitTestVisible = false };
    readonly Border bar;
    readonly Border statusBox = new();
    readonly TextBlock status = new();
    readonly Border draftNotice;
    readonly DispatcherTimer statusTimer = new();
    readonly DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    PaperState state = PaperState.Hidden;
    int motion;            // 動きを始めるたび・打ち切るたびに増やす。古い動きの続きが紛れ込まないように
    int scene;             // 紙を元に戻すたびに増やす。古い写しを敷かないように
    Task<Brush?>? backdropTask;
    bool backdropApplied;
    Tween? tween;
    bool tweenHooked;
    bool suppressAutoHide;
    bool backdropRetried;

    Gesture? gesture;

    long showRequestedAt;
    string showSource = "";
    int framesUntilLogged;
    bool timingHooked;

    int pendingCount;
    bool sendStuck;
    string? flashText;
    StatusTone flashTone;

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    // 指を離した瞬間（保存した直後）
    public event Action? Saved;
    // 送った紙が画面から消えたとき。true は、動きの途中でペンのボタンなどにより新しい白紙を出したとき
    public event Action<bool>? Departed;

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
        // 指で触れたときの丸い表示や長押しの右クリックを出さない（なぞる動きをすぐ紙に伝えるため）
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

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
        ink.PreviewStylusMove += OnInkStylusMove;
        ink.PreviewStylusUp += OnInkStylusUp;
        ink.LostStylusCapture += (_, e) => CancelGesture(e.StylusDevice.Id);
        ink.PreviewMouseDown += (_, e) =>
        {
            if (e.StylusDevice == null) ink.EditingMode = InkCanvasEditingMode.Ink;  // 本物のマウス
        };

        bar = new Border
        {
            Height = BarHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Frozen(Color.FromArgb(20, 0, 0, 0)),
            Child = new TextBlock
            {
                Text = "↑ 指で上へなぞるか、ここを上へ引き上げて送る　（Ctrl+Enter：送る / Esc：送らずに閉じる / Ctrl+Z：1画消す）",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                FontSize = 14,
            },
        };
        bar.PreviewStylusDown += (_, e) =>
        {
            if (BeginGesture(e.StylusDevice.Id, e.GetPosition(this), "下の帯を引き上げ")) e.StylusDevice.Capture(bar);
            e.Handled = true;
        };
        bar.PreviewStylusMove += (_, e) => { MoveGesture(e.StylusDevice.Id, e.GetPosition(this)); e.Handled = true; };
        bar.PreviewStylusUp += (_, e) =>
        {
            EndGesture(e.StylusDevice.Id, e.GetPosition(this));
            if (e.StylusDevice.Captured == bar) e.StylusDevice.Capture(null);
            e.Handled = true;
        };
        bar.LostStylusCapture += (_, e) => CancelGesture(e.StylusDevice.Id);
        const int MouseId = -1;
        bar.PreviewMouseDown += (_, e) =>
        {
            if (e.StylusDevice == null && BeginGesture(MouseId, e.GetPosition(this), "下の帯を引き上げ（マウス）")) bar.CaptureMouse();
        };
        bar.PreviewMouseMove += (_, e) => { if (e.StylusDevice == null) MoveGesture(MouseId, e.GetPosition(this)); };
        bar.PreviewMouseUp += (_, e) =>
        {
            if (e.StylusDevice != null) return;
            EndGesture(MouseId, e.GetPosition(this));
            bar.ReleaseMouseCapture();
        };
        bar.LostMouseCapture += (_, _) => CancelGesture(MouseId);

        status.Foreground = Brushes.Gray;
        status.FontSize = 13;
        status.TextWrapping = TextWrapping.Wrap;
        statusBox.Child = status;
        statusBox.HorizontalAlignment = HorizontalAlignment.Right;
        statusBox.VerticalAlignment = VerticalAlignment.Top;
        statusBox.Margin = new Thickness(0, 8, 16, 0);
        statusBox.Padding = new Thickness(10, 4, 10, 5);
        statusBox.CornerRadius = new CornerRadius(8);
        statusBox.MaxWidth = 640;
        statusBox.IsHitTestVisible = false;
        statusTimer.Tick += (_, _) =>
        {
            statusTimer.Stop();
            flashText = null;
            RenderStatus();
        };

        draftNotice = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(14, 5, 14, 6),
            CornerRadius = new CornerRadius(14),
            Background = Frozen(Color.FromRgb(0xF1, 0xF3, 0xF5)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "書きかけがあります（続けて書けます）",
                Foreground = Frozen(Color.FromRgb(0x4B, 0x55, 0x63)),
                FontSize = 14,
            },
        };
        draftTimer.Tick += (_, _) => HideDraftNotice();

        // 紙の下の縁に落ちる影。紙が上へ動いたときにだけ見える
        var shadow = new Rectangle
        {
            Height = ShadowHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(0, ShadowHeight),
            Fill = new LinearGradientBrush(Color.FromArgb(0x38, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90),
        };

        paper.RenderTransform = paperShift;
        paper.Children.Add(shadow);
        paper.Children.Add(ink);
        paper.Children.Add(departing);
        paper.Children.Add(bar);
        paper.Children.Add(statusBox);
        paper.Children.Add(draftNotice);
        root.Children.Add(paper);
        Content = root;

        KeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            // 別のアプリに移ったら、送らずに隠す（書きかけは残る）
            if (!suppressAutoHide && state is PaperState.Shown or PaperState.Dragging or PaperState.Returning) FadeAway();
        };
        outbox.PendingChanged += count => Dispatcher.BeginInvoke(() =>
        {
            pendingCount = count;
            if (count == 0) sendStuck = false;
            RenderStatus();
        });
    }

    // 白紙で書いている最中か（送った結果を白紙の中で知らせるか、札で知らせるかの判断に使う）
    public bool IsInUse => state is PaperState.Shown or PaperState.Dragging or PaperState.Returning;

    IntPtr Handle => new WindowInteropHelper(this).Handle;

    // 起動時に一度だけ。ウィンドウを作って描画の準備まで済ませ、隠しておく
    public void Prepare()
    {
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        ScreenBackdrop.EnableFor(hwnd);
        Log.Diag("送るときの動き: " + (ScreenBackdrop.Available
            ? "紙の後ろに画面を透かして見せる（この窓はスクリーンショットに写りません）"
            : "画面の写しが使えないため、紙の後ろは灰色"));
        Opacity = 0;
        Show();
        Hide();
        Opacity = 1;
        state = PaperState.Hidden;
        pendingCount = outbox.PendingCount();
        RenderStatus();
    }

    public void ShowForInk(string source, long requestedAt)
    {
        if (state is PaperState.Departing or PaperState.Fading)
        {
            // 動きの途中でペンのボタンなどが押された：動きを打ち切り、その場で新しい白紙にする
            bool wasDeparting = state == PaperState.Departing;
            motion++;
            ResetPaper();
            state = PaperState.Shown;
            StartShowTiming(source, requestedAt);
            Activate();
            SetForegroundWindow(Handle);
            ink.Focus();
            if (wasDeparting) Departed?.Invoke(true);
            UpdateDraftNotice();
            return;
        }
        if (IsVisible)
        {
            Activate();
            return;
        }
        StartShowTiming(source, requestedAt);
        state = PaperState.Shown;
        UpdateDraftNotice();
        Show();
        Activate();
        SetForegroundWindow(Handle);
        ink.Focus();
    }

    /* ===== 白紙が出るまでの時間の記録 ===== */

    void StartShowTiming(string source, long requestedAt)
    {
        showSource = source;
        showRequestedAt = requestedAt;
        framesUntilLogged = 2;
        if (!timingHooked)
        {
            CompositionTarget.Rendering += OnTimingFrame;
            timingHooked = true;
        }
    }

    // 表示してから2コマ描画した時点を「書ける状態」とみなして記録する
    void OnTimingFrame(object? sender, EventArgs e)
    {
        if (--framesUntilLogged > 0) return;
        CompositionTarget.Rendering -= OnTimingFrame;
        timingHooked = false;
        Log.Timing(showSource, Stopwatch.GetElapsedTime(showRequestedAt).TotalMilliseconds);
    }

    /* ===== 入力 ===== */

    void OnInkStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch)
        {
            ink.EditingMode = InkCanvasEditingMode.None;  // 指では書かない
            if (BeginGesture(e.StylusDevice.Id, e.GetPosition(this), "指で上へ")) e.StylusDevice.Capture(ink);
        }
        else
        {
            ink.EditingMode = e.StylusDevice.Inverted ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
            HideDraftNotice();
        }
    }

    void OnInkStylusMove(object sender, StylusEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch) MoveGesture(e.StylusDevice.Id, e.GetPosition(this));
    }

    void OnInkStylusUp(object sender, StylusEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type != TabletDeviceType.Touch) return;
        EndGesture(e.StylusDevice.Id, e.GetPosition(this));
        if (e.StylusDevice.Captured == ink) e.StylusDevice.Capture(null);
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            if (state is PaperState.Shown or PaperState.Dragging or PaperState.Returning) FadeAway();
        }
        else if (ctrl && e.Key == Key.Enter)
        {
            if (state == PaperState.Shown) Commit("Ctrl+Enter", 0);
        }
        else if (ctrl && e.Key == Key.Z && state == PaperState.Shown && ink.Strokes.Count > 0)
        {
            ink.Strokes.RemoveAt(ink.Strokes.Count - 1);
        }
    }

    /* ===== 上へなぞる動き（指で紙のどこでも、またはペン・マウスで下の帯を） ===== */

    bool BeginGesture(int device, Point at, string how)
    {
        if (gesture is { } old && old.IdleMs > 1000) CancelGesture(old.Device);  // 指を離した知らせが来なかった古いなぞり
        if (state != PaperState.Shown || gesture != null) return false;
        gesture = new Gesture(device, at, how);
        if (Motion.Enabled) backdropTask ??= CaptureBackdrop();  // 動き出す前に撮り始める
        return true;
    }

    void MoveGesture(int device, Point at)
    {
        if (gesture is not { } g || g.Device != device) return;
        g.Add(at);
        double up = g.Start.Y - at.Y;
        if (!g.Following)
        {
            if (!Motion.Enabled || up < DragStartDistance || Math.Abs(at.X - g.Start.X) > up) return;
            g.Following = true;
            state = PaperState.Dragging;
            HideDraftNotice();
            ApplyBackdrop();
            paper.CacheMode = new BitmapCache();
        }
        double offset = Math.Min(0, -up);
        paperShift.Y = offset;
        paper.Opacity = DragOpacity(offset);
    }

    void EndGesture(int device, Point at)
    {
        if (gesture is not { } g || g.Device != device) return;
        gesture = null;
        g.Add(at);
        double up = g.Start.Y - at.Y;
        double sideways = Math.Abs(at.X - g.Start.X);
        double velocity = g.UpVelocity();
        bool send = sideways <= Math.Max(up, 1)
            && (up >= ActualHeight * SendDistanceRatio || (velocity >= FlickVelocity && up >= FlickMinDistance));
        if (send)
        {
            Commit(g.How, velocity);
        }
        else if (g.Following)
        {
            SpringBack();
        }
        else
        {
            backdropTask = null;  // 触っただけ。撮った写しは使わない
        }
    }

    void CancelGesture(int device)
    {
        if (gesture is not { } g || g.Device != device) return;
        gesture = null;
        if (g.Following && state == PaperState.Dragging) SpringBack();
    }

    double DragOpacity(double offset) => 1 - 0.55 * Math.Min(1, -offset / Math.Max(1, ActualHeight * 0.5));

    /* ===== 送る ===== */

    void Commit(string how, double velocity)
    {
        gesture = null;
        if (ink.Strokes.Count == 0)
        {
            FadeAway();  // 何も書いていない：送らずに閉じる
            return;
        }
        long started = Stopwatch.GetTimestamp();
        StrokeCollection strokes = ink.Strokes;
        try
        {
            outbox.SaveInk(strokes);  // 保存は離した瞬間に行い、動きの終わりを待たない
        }
        catch (Exception ex)
        {
            Log.Diag("保存に失敗: " + ex);
            suppressAutoHide = true;
            MessageBox.Show(this, "保存に失敗しました。書いた内容はまだ画面に残っています。\n" + ex.Message, "QuickInk");
            suppressAutoHide = false;
            SpringBack();
            return;
        }
        // 書いた内容は「飛んでいく紙」に移し、白紙そのものはすぐ次に書ける状態にしておく
        ink.Strokes = new StrokeCollection();
        departing.Strokes = strokes;
        Log.Timing("保存（" + how + "）", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Saved?.Invoke();

        if (!Motion.Enabled)
        {
            HideNow();
            Departed?.Invoke(false);
            return;
        }
        Depart(velocity);
    }

    async void Depart(double velocity)
    {
        int id = ++motion;
        state = PaperState.Departing;
        paper.IsHitTestVisible = false;
        await PrepareBackdrop(id);
        if (id != motion) return;
        paper.CacheMode ??= new BitmapCache();

        double y0 = paperShift.Y, opacity0 = paper.Opacity;
        double distance = y0 + ActualHeight + ShadowHeight + 8;  // 画面の上の外まで
        // 指の勢いを引き継ぎ、そこからだんだん速くする
        double carried = Math.Min(Math.Max(velocity, 0) * DepartMs, distance);
        StartTween(new Tween(DepartMs, p =>
        {
            paperShift.Y = y0 - (carried * p + (distance - carried) * p * p);
            paper.Opacity = opacity0 * (1 - p);
        }, () =>
        {
            if (id != motion) return;
            HideNow();
            Departed?.Invoke(false);
        }));
    }

    void SpringBack()
    {
        int id = ++motion;
        if (!Motion.Enabled || !IsVisible)
        {
            ResetPaper();
            state = IsVisible ? PaperState.Shown : PaperState.Hidden;
            return;
        }
        state = PaperState.Returning;
        double y0 = paperShift.Y, opacity0 = paper.Opacity;
        StartTween(new Tween(ReturnMs, p =>
        {
            double e = 1 - Math.Pow(1 - p, 3);  // ふわっと止まる
            paperShift.Y = y0 * (1 - e);
            paper.Opacity = opacity0 + (1 - opacity0) * e;
        }, () =>
        {
            if (id != motion) return;
            ResetPaper();
            state = PaperState.Shown;
        }));
    }

    /* ===== 送らずに隠す（その場で薄くなるだけ。札は出さない） ===== */

    async void FadeAway()
    {
        int id = ++motion;
        gesture = null;
        StopTween();
        if (!Motion.Enabled || !IsVisible)
        {
            HideNow();
            return;
        }
        state = PaperState.Fading;
        paper.IsHitTestVisible = false;
        HideDraftNotice();
        await PrepareBackdrop(id);
        if (id != motion) return;
        paper.CacheMode ??= new BitmapCache();
        double y0 = paperShift.Y, opacity0 = paper.Opacity;
        StartTween(new Tween(FadeMs, p =>
        {
            paperShift.Y = y0;
            paper.Opacity = opacity0 * (1 - p) * (1 - p);  // はじめに速く薄くなる
        }, () =>
        {
            if (id == motion) HideNow();
        }));
    }

    void HideNow()
    {
        state = PaperState.Hidden;  // 先に変える（Hide で起きる Deactivated を無視するため）
        gesture = null;
        Hide();
        ResetPaper();
    }

    // 紙を元の位置・濃さに戻し、動きのために用意したものを片付ける（書きかけはそのまま残す）
    void ResetPaper()
    {
        StopTween();
        scene++;
        paperShift.Y = 0;
        paper.Opacity = 1;
        paper.CacheMode = null;
        paper.IsHitTestVisible = true;
        departing.Strokes = new StrokeCollection();
        root.Background = null;
        backdropTask = null;
        backdropApplied = false;
    }

    /* ===== 紙の後ろに敷く画面の写し ===== */

    async Task PrepareBackdrop(int id)
    {
        if (backdropApplied) return;
        backdropTask ??= CaptureBackdrop();
        if (!backdropTask.IsCompleted) await Task.WhenAny(backdropTask, Task.Delay(BackdropWaitMs));
        if (id != motion) return;
        ApplyBackdrop();
    }

    void ApplyBackdrop()
    {
        if (backdropApplied) return;
        backdropApplied = true;
        Task<Brush?> task = backdropTask ??= CaptureBackdrop();
        if (task.IsCompleted)
        {
            root.Background = task.Result ?? FallbackBackdrop;
            return;
        }
        root.Background = FallbackBackdrop;
        int forScene = scene;
        task.ContinueWith(t =>
        {
            if (forScene == scene && state != PaperState.Hidden && t.Result is { } backdrop) root.Background = backdrop;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    Task<Brush?> CaptureBackdrop()
    {
        IntPtr hwnd = Handle;
        if (!ScreenBackdrop.Available && !backdropRetried)
        {
            backdropRetried = true;  // 起動時に外せなかった場合、表示中の今もう一度だけ試す
            ScreenBackdrop.EnableFor(hwnd);
        }
        return ScreenBackdrop.CaptureBehindAsync(hwnd, BarHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
    }

    /* ===== 動きの土台（毎コマ、経過時間から位置と濃さを決める） ===== */

    sealed record Tween(double DurationMs, Action<double> Apply, Action Done)
    {
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
    }

    void StartTween(Tween t)
    {
        tween = t;
        t.Apply(0);
        if (!tweenHooked)
        {
            CompositionTarget.Rendering += OnTweenFrame;
            tweenHooked = true;
        }
    }

    void StopTween()
    {
        tween = null;
        if (tweenHooked)
        {
            CompositionTarget.Rendering -= OnTweenFrame;
            tweenHooked = false;
        }
    }

    void OnTweenFrame(object? sender, EventArgs e)
    {
        if (tween is not { } t)
        {
            StopTween();
            return;
        }
        double p = Math.Min(1, Stopwatch.GetElapsedTime(t.StartedAt).TotalMilliseconds / t.DurationMs);
        t.Apply(p);
        if (p < 1) return;
        StopTween();
        t.Done();
    }

    /* ===== 右上の知らせと「書きかけがあります」 ===== */

    public void SetSendState(int pending, bool stuck)
    {
        pendingCount = pending;
        sendStuck = stuck && pending > 0;
        RenderStatus();
    }

    public void FlashStatus(string text, StatusTone tone, TimeSpan duration)
    {
        flashText = text;
        flashTone = tone;
        statusTimer.Stop();
        statusTimer.Interval = duration;
        statusTimer.Start();
        RenderStatus();
    }

    void RenderStatus()
    {
        string text;
        StatusTone tone;
        if (flashText != null)
        {
            text = flashText;
            tone = flashTone;
        }
        else if (pendingCount > 0 && sendStuck)
        {
            text = $"⚠ まだ届いていません（未送信 {pendingCount} 件）。つながったら自動で送ります";
            tone = StatusTone.Warning;
        }
        else if (pendingCount > 0)
        {
            text = $"送信待ち {pendingCount} 件";
            tone = StatusTone.Quiet;
        }
        else
        {
            text = "";
            tone = StatusTone.Quiet;
        }
        status.Text = text;
        statusBox.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        (Brush fore, Brush? back) = tone switch
        {
            StatusTone.Info => (Frozen(Color.FromRgb(0x16, 0x65, 0x34)), Frozen(Color.FromRgb(0xEC, 0xFD, 0xF3))),
            StatusTone.Warning => (Frozen(Color.FromRgb(0x9A, 0x34, 0x12)), Frozen(Color.FromRgb(0xFF, 0xF4, 0xE5))),
            StatusTone.Error => (Frozen(Color.FromRgb(0x99, 0x1B, 0x1B)), Frozen(Color.FromRgb(0xFE, 0xEC, 0xEC))),
            _ => ((Brush)Brushes.Gray, null),
        };
        status.Foreground = fore;
        status.FontSize = tone is StatusTone.Warning or StatusTone.Error ? 15 : 13;
        statusBox.Background = back;
    }

    void UpdateDraftNotice()
    {
        if (ink.Strokes.Count == 0)
        {
            HideDraftNotice();
            return;
        }
        draftNotice.Visibility = Visibility.Visible;
        draftTimer.Stop();
        draftTimer.Start();
    }

    void HideDraftNotice()
    {
        draftTimer.Stop();
        draftNotice.Visibility = Visibility.Collapsed;
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // 1回のなぞり。離したときの速さを出すため、最近の位置を覚えておく
    sealed class Gesture
    {
        readonly List<(double Ms, double Y)> samples = new();
        readonly long startedAt = Stopwatch.GetTimestamp();

        public Gesture(int device, Point start, string how)
        {
            Device = device;
            Start = start;
            How = how;
            samples.Add((0, start.Y));
        }

        public int Device { get; }
        public Point Start { get; }
        public string How { get; }
        public bool Following { get; set; }

        public double IdleMs => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds - samples[^1].Ms;

        public void Add(Point at)
        {
            double now = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            samples.Add((now, at.Y));
            while (samples.Count > 2 && now - samples[0].Ms > 150) samples.RemoveAt(0);
        }

        // 離す直前（約80ミリ秒）の、上向きの速さ
        public double UpVelocity()
        {
            if (samples.Count < 2) return 0;
            (double lastMs, double lastY) = samples[^1];
            (double fromMs, double fromY) = samples[0];
            foreach (var s in samples)
            {
                if (lastMs - s.Ms <= 80)
                {
                    (fromMs, fromY) = s;
                    break;
                }
            }
            double dt = lastMs - fromMs;
            return dt < 8 ? 0 : (fromY - lastY) / dt;
        }
    }
}
