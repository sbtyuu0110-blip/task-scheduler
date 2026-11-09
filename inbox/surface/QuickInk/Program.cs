using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace QuickInk;

// QuickInk（試作）：常駐して、ペンのボタンなどで白紙を即表示する手書きメモ。
// 書いて指で上にフリックすると、端末内の「送信待ち」に保存し、裏で受け口に送る。
public static class Program
{
    const string MutexName = "QuickInk.SingleInstance";
    public const string PipeName = "QuickInk.Show";
    const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        bool startHidden = Array.IndexOf(args, "--background") >= 0;
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // すでに常駐しているので「表示して」と伝えて終わる（ペンのボタンにこのexeを割り当てた場合の経路）
            AllowSetForegroundWindow(ASFW_ANY);
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.Write("show");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Diag("常駐中のQuickInkへの表示要求に失敗: " + ex.Message);
                return 1;
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var core = new AppCore(app);
        app.Startup += (_, _) => core.Start(startHidden);
        return app.Run();
    }
}

public sealed class AppCore
{
    readonly Application app;
    Outbox outbox = null!;
    InkWindow window = null!;
    Tray tray = null!;
    Triggers triggers = null!;

    public AppCore(Application app) => this.app = app;

    public void Start(bool startHidden)
    {
        Log.Diag("起動: " + Diagnostics.Summary());
        outbox = new Outbox();
        window = new InkWindow(outbox);
        window.Prepare();
        tray = new Tray(this, outbox);
        triggers = new Triggers(window, RequestShow);
        triggers.Start();
        StartPipeServer();
        outbox.StartSender();
        if (!startHidden) RequestShow("起動直後");
    }

    // 合図を受けた瞬間の時刻を記録してから表示する（白紙が出るまでの時間を測るため）
    public void RequestShow(string source)
    {
        long receivedAt = Stopwatch.GetTimestamp();
        window.Dispatcher.BeginInvoke(() => window.ShowForInk(source, receivedAt));
    }

    public void Exit()
    {
        tray.Dispose();
        app.Shutdown();
    }

    void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    if (reader.ReadToEnd() == "show") RequestShow("exeの起動（ペンの設定など）");
                }
                catch (Exception ex)
                {
                    Log.Diag("表示要求の受け取りに失敗: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true, Name = "QuickInk.Pipe" };
        thread.Start();
    }
}
