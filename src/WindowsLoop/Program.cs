using System.Diagnostics;
using System.IO.Pipes;

namespace WindowsLoop;

internal static class Program
{
    const string MutexName = "WindowsLoop.SingleInstance.v1";
    const string PipeName = "WindowsLoop.OpenWindow.v1";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--tile-defaults-selftest")
        {
            TileDefaultsSelfTest.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--event-selftest")
        {
            EventSelfTest.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--push-selftest")
        {
            PushSelfTest.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--audio-selftest")
        {
            AudioSelfTest.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "--audio-mix-test")
        {
            AudioSelfTest.RunMixTest();
            return;
        }

        if (args.Length > 0 && args[0] == "--audio-walk-test")
        {
            AudioSelfTest.RunWalkTest();
            return;
        }

        if (args.Length > 0 && args[0] == "--audio-land-test")
        {
            AudioSelfTest.RunLandTest();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash((Exception)e.ExceptionObject);

        var game = new Game { LevelName = PickLevel(args) };
        game.Init();
        game.Audio.Start();
        StartPipeServer(game);
        // 1ms 计时器精度：默认 15.6ms 会让每次等待都超时，整圈被拉到 20ms（50fps）
        Native.TimeBeginPeriod(1);
        try { RunLoop(game); }
        finally
        {
            Native.TimeEndPeriod(1);
            game.Audio.Dispose();
        }
    }

    /// <summary>关卡用 --level &lt;名字&gt; 或环境变量 WL_LEVEL 指定，默认进 main（关卡选择地图）。</summary>
    static string PickLevel(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] is "--level" or "-l")
                return args[i + 1];

        var env = Environment.GetEnvironmentVariable("WL_LEVEL");
        return string.IsNullOrWhiteSpace(env) ? "main" : env.Trim();
    }

    static void SignalExistingInstance()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(250);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine("open");
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(80);
            }
        }
    }

    static void StartPipeServer(Game game)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var command = reader.ReadLine();
                    if (Environment.GetEnvironmentVariable("WL_DEBUG_LAYOUT") == "1")
                        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "windowsloop-debug.log"), $"pipe: {command}\n"); } catch { }
                    if (command == "open")
                        game.Enqueue(game.SpawnWindow);   // 再次双击程序也要演一遍"吐出窗口"
                }
                catch (Exception)
                {
                    Thread.Sleep(200);
                }
            }
        })
        { IsBackground = true, Name = "pipe-server" };
        thread.Start();
    }

    /// <summary>把崩溃追加到日志再退出：静默死掉的循环是最难查的问题。</summary>
    static void Crash(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "windowsloop-crash.log"),
                $"\r\n[{DateTime.Now:HH:mm:ss.fff}] {ex}\r\n");
        }
        catch { }
    }

    /// <summary>WL_TRACE_FPS=1 时把整圈/重绘/物理的耗时写进临时目录，用来定位掉帧在哪一段。</summary>
    static readonly bool TraceFps = Environment.GetEnvironmentVariable("WL_TRACE_FPS") == "1";

    static void RunLoop(Game game)
    {
        var clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds;
        double accumulator = 0;

        // 统计用
        int frames = 0, stepsTotal = 0;
        double paintMs = 0, stepMs = 0, msgMs = 0, loopMs = 0, statFrom = 0;

        while (game.Running)
        {
            try
            {
                double loopStart = clock.Elapsed.TotalSeconds;

                DrainMessages(game);
                if (!game.Running) break;
                game.PumpActions();

                double now = clock.Elapsed.TotalSeconds;
                double frameDt = Math.Min(now - last, 0.05);
                accumulator += Math.Min(now - last, 0.25);
                last = now;
                msgMs += (now - loopStart) * 1000.0;

                int steps = 0;
                double stepT0 = clock.Elapsed.TotalSeconds;
                while (accumulator >= Game.FixedDt && steps < 5)
                {
                    game.Step(Game.FixedDt);
                    accumulator -= Game.FixedDt;
                    steps++;
                }
                stepMs += (clock.Elapsed.TotalSeconds - stepT0) * 1000.0;
                stepsTotal += steps;

                // 抖动/飞行只改位置，不跟物理步走
                game.UpdateWindowAnimations((float)frameDt);

                double paintT0 = clock.Elapsed.TotalSeconds;
                // 窗口动画只改位置、不改内容，所以动画期间只在物理步推进时重绘（60Hz），
                // 余下的每一圈只更新位置 —— 一帧重绘要 5ms 上下，不这样限就没法提高位移频率
                bool needPaint = !game.WindowAnimBusy || steps > 0;
                if (needPaint)
                {
                    foreach (var window in game.Windows.ToArray())
                    {
                        if (window.IsDisposed) continue;
                        window.Invalidate();
                        window.Update();
                    }
                }
                paintMs += (clock.Elapsed.TotalSeconds - paintT0) * 1000.0;

                Heartbeat(game);

                // 窗口动画期间把等待压到 2ms（≈300Hz 位移更新）；平时按物理步对齐，省 CPU
                int waitMs = game.WindowAnimBusy
                    ? 2
                    : (int)Math.Clamp((Game.FixedDt - accumulator) * 1000.0 - 1.0, 0, 16);
                Native.MsgWaitForMultipleObjects(0, null, false, (uint)waitMs, Native.QS_ALLINPUT);

                frames++;
                loopMs += (clock.Elapsed.TotalSeconds - loopStart) * 1000.0;

                if (TraceFps && clock.Elapsed.TotalSeconds - statFrom >= 2.0)
                {
                    double span = clock.Elapsed.TotalSeconds - statFrom;
                    statFrom = clock.Elapsed.TotalSeconds;
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "windowsloop-fps.log"),
                            $"[{DateTime.Now:HH:mm:ss}] loop={frames / span:F1}fps  " +
                            $"整圈={loopMs / frames:F1}ms 消息={msgMs / frames:F1}ms " +
                            $"物理={stepMs / frames:F1}ms 重绘={paintMs / frames:F1}ms 窗口={game.Windows.Count} " +
                            $"物理步/圈={(double)stepsTotal / frames:F2}\r\n");
                    }
                    catch { }
                    frames = 0; stepsTotal = 0;
                    paintMs = stepMs = msgMs = loopMs = 0;
                }
            }
            catch (Exception ex)
            {
                Crash(ex);
                game.Running = false;
                break;
            }
        }
    }

    /// <summary>
    /// 每几秒写一行"还活着"。进程安静地消失而日志里没有异常时，
    /// 就能靠最后的Alive时间判断它是异常退出还是被外部杀掉。发布前应移除。
    /// </summary>
    static void Heartbeat(Game game)
    {
        if (game.Time - _lastBeat < 5f) return;
        _lastBeat = game.Time;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "windowsloop-alive.log"),
                $"[{DateTime.Now:HH:mm:ss}] t={game.Time:0.0} windows={game.Windows.Count}\r\n");
        }
        catch { }
    }

    static float _lastBeat = -99f;

    static void DrainMessages(Game game)
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.message == Native.WM_QUIT)
            {
                game.Running = false;
                return;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }
}
