using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace WindowsLoop;

internal sealed class Game
{
    public const float FixedDt = 1f / 60f;

    public readonly List<GameWindow> Windows = new();
    public readonly Player Player = new();
    public Level Level = null!;
    public Bitmap TileSheet = null!, PlayerSheet = null!;

    public bool Running = true;
    public float Time;

    readonly ConcurrentQueue<Action> _actions = new();
    bool _spaceDown;
    float _traceAt;
    static readonly bool DebugTrace = Environment.GetEnvironmentVariable("WL_DEBUG_TRACE") == "1";
    GameWindow? _lastWindow;

    /// <summary>单实例音频：BGM / 脚步循环 / 一次性音效。</summary>
    public readonly AudioManager Audio = new();
    bool _mDown;
    bool _wasOnGround;

    /// <summary>本次离地已经持续多久；落地音只在真正滞空之后才响，避免一帧抖动就重复触发。</summary>
    float _airTime;

    const float LandMinAirTime = 0.03f;   // ≈2 帧

    /// <summary>相机跟随：位置反馈时间常数 T 与速度惯性时间常数 τ。
    /// T=0.22 / τ=0.12 时，跨一格约 0.48s 走完，位移曲线是 S 形（起步/收尾速度都为 0）；
    /// 角色以 10 格/秒连续跑动时稳态滞后约 2 格。已用 60fps 固定步长模拟验证。</summary>
    const float CamFollowTime = 0.22f;
    const float CamVelSmooth = 0.12f;
    float _camVelX, _camVelY;
    GameWindow? _camWindow;


    public static string AssetPath(string rel) => Path.Combine(AppContext.BaseDirectory, "assets", rel);

    /// <summary>关卡文件名（可以带 .json，也可以只是一个名字）。</summary>
    public string LevelName = "main";

    public void Init()
    {
        if (!TryLoadLevel(LevelName)) TryLoadLevel("main");
        TileSheet = new Bitmap(AssetPath("tiles.png"));
        PlayerSheet = new Bitmap(AssetPath("player.png"));
        OpenWindow();
    }

    /// <summary>
    /// 读一关进来（只替换 Level，不动窗口）。关卡文件不存在或读坏了返回 false 并保持原关卡，
    /// 免得一个填错的跳转目标把整个游戏带崩。
    /// </summary>
    public bool TryLoadLevel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var file = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";
        var path = Path.IsPathRooted(file) ? file : AssetPath(Path.Combine("levels", file));
        try
        {
            var level = Level.Load(path);
            if (level.Pieces.Count == 0)
            {
                Log($"load level '{name}' failed: no pieces");
                return false;
            }
            Level = level;
            LevelName = name;
            return true;
        }
        catch (Exception ex)
        {
            Log($"load level '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Enqueue(Action action) => _actions.Enqueue(action);

    public void PumpActions()
    {
        while (_actions.TryDequeue(out var action))
            action();
    }

    public Piece PieceForSlot(int slot) => Level.Pieces[slot % Level.Pieces.Count];

    public void OpenWindow()
    {
        if (DebugLayout) Log($"open: windows={Windows.Count} max={Level.MaxWindows}");
        if (Windows.Count >= Level.MaxWindows) return;

        int slot = Windows.Count;
        var piece = PieceForSlot(slot);

        Point clientOrigin;
        if (Windows.Count == 0)
        {
            clientOrigin = new Point(240, 160);
        }
        else
        {
            // 贴着上一块往右下摆；放不下就退回从工作区左上角依次排开，保证整块都在屏幕里
            var size = new Size(GameWindow.ViewTilesX * GameWindow.TileSize, GameWindow.ViewTilesY * GameWindow.TileSize);
            var last = Windows[^1].ClientScreen;
            var next = new Point(last.Right + 80, last.Top + 80);
            var work = Screen.FromPoint(new Point(next.X + size.Width / 2, next.Y + size.Height / 2)).WorkingArea;

            if (next.X + size.Width > work.Right || next.Y + size.Height > work.Bottom)
                next = new Point(work.Left + 60 + Windows.Count * 48, work.Top + 60 + Windows.Count * 48);

            clientOrigin = next;
        }

        clientOrigin = GameWindow.ClampToScreen(new Point(
            GameWindow.GridRound(clientOrigin.X),
            GameWindow.GridRound(clientOrigin.Y)));

        var window = new GameWindow(this, piece, clientOrigin);
        var spawnPos = new Vector2(piece.Spawn.X + 0.5f, piece.Spawn.Y + 0.5f);
        var cam = CameraTarget(spawnPos);
        window.SetCamera(cam.X, cam.Y);

        bool first = Windows.Count == 0;
        Windows.Add(window);
        window.Show();

        if (Windows.Count == 1)
            PlaceAtSpawn(window);

        RefreshLayout();
        if (!first && !_suppressSfx) Audio.Play(Sfx.WindowOpen);
    }

    /// <summary>把角色放到某块地图的出生点（注意取景框可能已经滚动过）。</summary>
    void PlaceAtSpawn(GameWindow w)
    {
        Player.Piece = w.Piece;
        Player.Pos = new Vector2(w.Piece.Spawn.X + 0.5f, w.Piece.Spawn.Y + 0.5f);
        _camVelX = _camVelY = 0f;   // 出生/重生时相机从静止开始，别带着上一次的滚动惯性
    }

    /// <summary>角色所在的那块地图目前由哪个窗口显示。</summary>
    public GameWindow? WindowFor(Piece piece)
    {
        foreach (var w in Windows)
            if (w.Piece == piece && !w.IsDisposed) return w;
        return null;
    }

    /// <summary>角色在取景框里的位置（格），左上角是 (0,0)。地图坐标只在这里减去镜头。</summary>
    public static Vector2 ViewLocal(GameWindow w, Vector2 mapPos) =>
        new(mapPos.X - w.CamX, mapPos.Y - w.CamY);

    /// <summary>
    /// 两块取景框之间的相对位移（格）。只比较这两个窗口，与屏幕绝对坐标无关。
    /// 用浮点：拖动时两块窗口可以差非整格像素接在一起。
    /// </summary>
    public static Vector2 ViewOffset(GameWindow from, GameWindow to)
    {
        var a = from.ClientScreen;
        var b = to.ClientScreen;
        return new Vector2(
            (b.Left - a.Left) / (float)GameWindow.TileSize,
            (b.Top - a.Top) / (float)GameWindow.TileSize);
    }

    public static bool InView(Vector2 u) =>
        u.X >= 0 && u.X < GameWindow.ViewTilesX && u.Y >= 0 && u.Y < GameWindow.ViewTilesY;

    /// <summary>把地图坐标差折算进 [0, 一个周期)。</summary>
    public static float WrapInto(float v, int period)
    {
        float m = v % period;
        return m < 0 ? m + period : m;
    }

    /// <summary>环形地图上的最短坐标差：差 15 格等于差 0 格。</summary>
    public static float ShortDelta(float v, int period)
    {
        float m = v % period;
        if (m > period / 2f) m -= period;
        else if (m < -period / 2f) m += period;
        return m;
    }

    /// <summary>
    /// 角色走出这块窗口的取景框，就交接给相邻那块窗口的地图。
    /// 全程只用"各自的画面内坐标 + 两块窗口之间的相对位移"，不经过任何屏幕坐标，
    /// 所以角色在画面上的位置天然连续，镜头也不需要跟着调整。
    /// </summary>
    void TransferIfCrossed()
    {
        var w = WindowFor(Player.Piece);
        if (w == null) return;

        var u = ViewLocal(w, Player.Pos);
        if (InView(u)) return;

        foreach (var target in w.Neighbors)
        {
            if (target.IsDisposed) continue;

            var tu = u - ViewOffset(w, target);
            if (!InView(tu)) continue;

            // 对方那张地图在这一格是墙就过不去，否则角色会被塞进墙里卡死
            float mx = target.CamX + tu.X;
            float my = target.CamY + tu.Y;
            if (target.Piece.SolidAt((int)MathF.Floor(mx), (int)MathF.Floor(my))) continue;

            Player.Piece = target.Piece;
            Player.Pos = new Vector2(mx, my);
            _lastWindow = target;
            Audio.Play(Sfx.Transfer);
            return;
        }
    }

    /// <summary>是不是正在整体退出（用来区分"用户关掉某一个窗口"和"程序自己在收摊"）。</summary>
    public bool Quitting { get; private set; }

    static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "windowsloop-debug.log"), line + Environment.NewLine); }
        catch { }
    }

    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        Running = false;
        Audio.Dispose();
        foreach (var window in Windows.ToArray())
        {
            if (!window.IsDisposed) window.Close();
        }
        Windows.Clear();
    }

    public void OnWindowClosed(GameWindow window)
    {
        Windows.Remove(window);

        // 换关卡时是程序自己在拆窗口，别把它当成"玩家把窗全关了"
        if (_swapping) return;

        if (Windows.Count == 0)
            Running = false;
        else
            RefreshLayout();
    }

    public void RefreshLayout()
    {
        foreach (var w in Windows)
            w.NeighborLeft = w.NeighborRight = w.NeighborTop = w.NeighborBottom = null;

        SyncGroups();

        for (int i = 0; i < Windows.Count; i++)
        {
            for (int j = i + 1; j < Windows.Count; j++)
            {
                var a = Windows[i];
                var b = Windows[j];
                var ra = a.ClientScreen;
                var rb = b.ClientScreen;

                bool vOverlap = ra.Top < rb.Bottom && ra.Bottom > rb.Top;
                bool hOverlap = ra.Left < rb.Right && ra.Right > rb.Left;

                if (vOverlap && ra.Right == rb.Left) { a.NeighborRight = b; b.NeighborLeft = a; }
                else if (vOverlap && ra.Left == rb.Right) { a.NeighborLeft = b; b.NeighborRight = a; }

                if (hOverlap && ra.Bottom == rb.Top) { a.NeighborBottom = b; b.NeighborTop = a; }
                else if (hOverlap && ra.Top == rb.Bottom) { a.NeighborTop = b; b.NeighborBottom = a; }
            }
        }

        if (DebugLayout) DumpLayout();
    }

    // ── 窗口组 ────────────────────────────────────────────────────────────

    public IReadOnlyList<WindowGroup> Groups => _groups;
    readonly List<WindowGroup> _groups = new();
    string _groupSignature = "";

    /// <summary>
    /// 按窗口当前位置重建窗口组。拖动过程中也要每帧调用，光效才会跟着实时变形；
    /// 但"相邻关系"属于玩法状态，只在松手时由 <see cref="RefreshLayout"/> 提交。
    /// </summary>
    public void SyncGroups()
    {
        var sb = new System.Text.StringBuilder();
        int live = 0;
        foreach (var w in Windows)
        {
            if (w.IsDisposed) continue;
            var r = w.ClientScreen;
            sb.Append(r.Left).Append(',').Append(r.Top).Append(';');
            live++;
        }

        var signature = live + "|" + sb;
        if (signature == _groupSignature) return;
        _groupSignature = signature;

        _groups.Clear();
        foreach (var w in Windows) w.Group = null;

        var seen = new HashSet<GameWindow>();
        foreach (var seed in Windows)
        {
            if (seed.IsDisposed || !seen.Add(seed)) continue;

            var group = new WindowGroup();
            var queue = new Queue<GameWindow>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var w = queue.Dequeue();
                group.Windows.Add(w);
                w.Group = group;

                foreach (var other in Windows)
                {
                    if (other.IsDisposed || seen.Contains(other)) continue;
                    if (!Touches(w.ClientScreen, other.ClientScreen)) continue;
                    seen.Add(other);
                    queue.Enqueue(other);
                }
            }

            var rects = new List<Rectangle>(group.Windows.Count);
            foreach (var w in group.Windows) rects.Add(w.ClientScreen);
            group.Boundary = Outline.Union(rects);

            _groups.Add(group);
        }

    }

    static readonly bool DebugLayout = Environment.GetEnvironmentVariable("WL_DEBUG_LAYOUT") == "1";

    void DumpLayout()
    {
        var sb = new System.Text.StringBuilder();
        if (Player.Piece == null)
            sb.AppendLine("player <未出生>");
        else
        {
            var holder = WindowFor(Player.Piece);
            sb.AppendLine($"player pos={Player.Pos.X:0.00},{Player.Pos.Y:0.00} piece={Player.Piece.Id} " +
                          $"cam={holder?.CamX:0.00},{holder?.CamY:0.00}");
        }
        foreach (var g in _groups)
        {
            sb.Append("group");
            foreach (var w in g.Windows)
            {
                var r = w.ClientScreen;
                sb.Append($" [{r.Left},{r.Top} {r.Width}x{r.Height} nb={w.Neighbors.Count()} cam={w.CamX:0.##},{w.CamY:0.##}]");
            }
            sb.AppendLine();
            foreach (var loop in g.Boundary)
            {
                sb.Append("  loop:");
                foreach (var p in loop)
                    sb.Append($" ({p.X:0},{p.Y:0})");
                sb.AppendLine();
            }
        }
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "windowsloop-groups.log"), sb.ToString()); }
        catch { }
    }

    /// <summary>角色所在的那一块能走到哪些窗口：自己 + 直接相邻的。</summary>
    public IEnumerable<GameWindow> ReachableFrom(GameWindow window)
    {
        yield return window;
        if (window.NeighborLeft != null) yield return window.NeighborLeft;
        if (window.NeighborRight != null) yield return window.NeighborRight;
        if (window.NeighborTop != null) yield return window.NeighborTop;
        if (window.NeighborBottom != null) yield return window.NeighborBottom;
    }

    /// <summary>取景框内第 (cx,cy) 格对应地图上的哪一格（已绕回一个周期）。</summary>
    public (Piece piece, int mx, int my) CellAt(GameWindow w, int cx, int cy)
    {
        var piece = w.Piece;
        int mx = w.View.X + cx;
        int my = w.View.Y + cy;
        return (piece,
            ((mx % piece.Width) + piece.Width) % piece.Width,
            ((my % piece.Height) + piece.Height) % piece.Height);
    }

    public void Step(float dt)
    {
        if (Windows.Count == 0) return;

        Time += dt;

        if (DebugTrace)
        {
            _traceAt += dt;
            if (_traceAt >= 0.4f)
            {
                _traceAt = 0;
                var holder = Player.Piece == null ? null : WindowFor(Player.Piece);
                Log($"trace t={Time:0.0} pos={Player.Pos.X:0.00},{Player.Pos.Y:0.00} " +
                    $"cam={holder?.CamX:0.00},{holder?.CamY:0.00} piece={Player.Piece?.Id}");
            }
        }

        // 窗口组每帧跟着实际位置走（拖动预览也靠它实时变形），
        // 但"相邻关系"是玩法状态，只在松手时由 RefreshLayout 提交。
        SyncGroups();


        for (int i = SeamFlashes.Count - 1; i >= 0; i--)
        {
            var flash = SeamFlashes[i];
            if (Time - flash.Start > flash.Duration || flash.Window.IsDisposed)
                SeamFlashes.RemoveAt(i);
        }

        // 换关卡的过场期间整局挂起：只推进闪白进度，物理和输入都不动
        if (Fading)
        {
            UpdateFade(dt);
            _spaceDown = Key(Native.VK_SPACE);
            Audio.SetWalking(false);
            Audio.Update(dt);
            return;
        }

        if (Paused)
        {
            // 拖动窗口期间整局挂起：物理、相机复位、窗口连接全部延后到松手
            _spaceDown = Key(Native.VK_SPACE);
            Audio.SetWalking(false);   // 挂起时脚步淡出，BGM 照常
            Audio.Update(dt);
            return;
        }

        RefreshLayout();

        UpdateInput(dt);
        MoveAndCollide(dt);
        var here = WindowFor(Player.Piece);
        if (here == null) Respawn();
        else
        {
            _lastWindow = here;
            TransferIfCrossed();
        }

        UpdateCameras(dt);
        UpdateEvents();
        Audio.Update(dt);
    }

    public GameWindow? DraggingWindow { get; private set; }
    public bool Paused => DraggingWindow != null;

    public void BeginDrag(GameWindow window)
    {
        // 拖动一开始就把所有窗口的相机钉到整格：Step 会暂停，
        // 冻结期间画面相位必须一致，否则接缝会错半个瓦片
        foreach (var w in Windows)
            if (!w.IsDisposed) w.SnapCameraIfNeeded();
        _camVelX = _camVelY = 0f;   // 暂停期间冻结惯性，松手后从静止重新跟随

        DraggingWindow = window;
        window.Dragging = true;
        window.PreviewOrigin = window.ClientScreen.Location;
        Player.Buffer = 0;
        _spaceDown = Key(Native.VK_SPACE);

        // 记下拖之前已经贴好的那些缝，松手时只让"新接上"的闪
        _linksBeforeDrag.Clear();
        foreach (var a in Windows)
        {
            if (a.IsDisposed) continue;
            foreach (var b in Windows)
            {
                if (b == a || b.IsDisposed) continue;
                if (Touches(a.ClientScreen, b.ClientScreen)) _linksBeforeDrag.Add(Link(a, b));
            }
        }
    }

    readonly HashSet<(GameWindow, GameWindow)> _linksBeforeDrag = new();

    (GameWindow, GameWindow) Link(GameWindow a, GameWindow b) =>
        Windows.IndexOf(a) <= Windows.IndexOf(b) ? (a, b) : (b, a);

    /// <summary>窗口动完之后，角色如果被留在了取景框外（也进不了任何相邻窗口），就拉回框内。</summary>
    void EnsurePlayerOnScreen()
    {
        if (Player.Piece == null) return;

        var w = WindowFor(Player.Piece);
        if (w == null) return;

        var u = ViewLocal(w, Player.Pos);
        if (InView(u) || InsideSomeWindow(w, Player.Pos.X, Player.Pos.Y)) return;

        // 落到和相机硬约束一致的安全区间里：拉回来的位置如果贴着框边，
        // 下一帧相机会被硬约束猛推一下，看着像闪了一下
        float mx = Player.Width / 2f, my = Player.Height / 2f;
        float cx = Math.Clamp(u.X, GameWindow.ViewTilesX * CamSafeMin + mx, GameWindow.ViewTilesX * CamSafeMax - mx);
        float cy = Math.Clamp(u.Y, GameWindow.ViewTilesY * CamSafeMin + my, GameWindow.ViewTilesY * CamSafeMax - my);
        Player.Pos = new Vector2(w.CamX + cx, w.CamY + cy);
        Player.Vel = Vector2.Zero;
    }

    public void EndDrag(GameWindow window)
    {
        window.Dragging = false;
        DraggingWindow = null;
        int flashesBefore = SeamFlashes.Count;
        RefreshLayout();   // 这时候才真正确认这次移动的结果
        EnsurePlayerOnScreen();

        // 刚刚连上的接缝闪一下白光，然后淡出（原本就贴着的缝不动）
        foreach (var other in Windows)
        {
            if (other == window || other.IsDisposed) continue;
            if (!Touches(window.ClientScreen, other.ClientScreen)) continue;
            if (_linksBeforeDrag.Contains(Link(window, other))) continue;

            var spans = new List<(int side, float s, float e)>();
            CollectSeams(window.ClientScreen, other.ClientScreen, spans);
            foreach (var (side, s, e) in spans)
                SeamFlashes.Add(new SeamFlash { Window = window, Side = side, S = s, E = e, Start = Time });

            spans.Clear();
            CollectSeams(other.ClientScreen, window.ClientScreen, spans);
            foreach (var (side, s, e) in spans)
                SeamFlashes.Add(new SeamFlash { Window = other, Side = side, S = s, E = e, Start = Time });
        }

        // 这次松手接上了新缝 → 吸附音；只是挪了挪 → 轻一点的松手音
        Audio.Play(SeamFlashes.Count > flashesBefore ? Sfx.Snap : Sfx.Release);
    }

    /// <summary>刚连上的接缝上闪的一层白光。</summary>
    public sealed class SeamFlash
    {
        public GameWindow Window = null!;
        public int Side;
        public float S, E;
        public float Start;
        public float Duration = 0.55f;
    }

    public readonly List<SeamFlash> SeamFlashes = new();

    static Rectangle ContentRect(Point origin) =>
        new(origin, new Size(GameWindow.ViewTilesX * GameWindow.TileSize, GameWindow.ViewTilesY * GameWindow.TileSize));

    static bool Touches(Rectangle a, Rectangle b)
    {
        bool vOverlap = a.Top < b.Bottom && a.Bottom > b.Top;
        bool hOverlap = a.Left < b.Right && a.Right > b.Left;
        return (vOverlap && (a.Right == b.Left || a.Left == b.Right))
            || (hOverlap && (a.Bottom == b.Top || a.Top == b.Bottom));
    }

    /// <summary>松手后这个窗口会不会贴到别人身上。</summary>
    public bool PreviewConnects(GameWindow window)
    {
        var rect = ContentRect(window.PreviewOrigin);
        foreach (var other in Windows)
        {
            if (other == window || other.IsDisposed) continue;
            if (Touches(rect, other.ClientScreen)) return true;
        }
        return false;
    }

    /// <summary>预览：别的窗口上“即将被接上”的那一段（返回 边序号 + 沿边的区间）。</summary>
    /// <summary>把两块矩形的接缝（属于 me 的那一侧）加进结果：边序号 + 沿边的区间。</summary>
    static void CollectSeams(Rectangle me, Rectangle r, List<(int side, float s, float e)> into)
    {
        if (r.Right == me.Left)
        {
            float s = Math.Max(me.Top, r.Top) - me.Top, e = Math.Min(me.Bottom, r.Bottom) - me.Top;
            if (e > s) into.Add((0, s, e));
        }
        if (r.Left == me.Right)
        {
            float s = Math.Max(me.Top, r.Top) - me.Top, e = Math.Min(me.Bottom, r.Bottom) - me.Top;
            if (e > s) into.Add((1, s, e));
        }
        if (r.Bottom == me.Top)
        {
            float s = Math.Max(me.Left, r.Left) - me.Left, e = Math.Min(me.Right, r.Right) - me.Left;
            if (e > s) into.Add((2, s, e));
        }
        if (r.Top == me.Bottom)
        {
            float s = Math.Max(me.Left, r.Left) - me.Left, e = Math.Min(me.Right, r.Right) - me.Left;
            if (e > s) into.Add((3, s, e));
        }
    }

    public List<(int side, float s, float e)> PreviewSeams(GameWindow window)
    {
        var result = new List<(int, float, float)>();
        var dragged = DraggingWindow;
        if (dragged == null) return result;

        // 被拖的窗口用"松手后会落到的位置"，其他窗口用当前位置
        var me = ContentRect(window == dragged ? dragged.PreviewOrigin : window.ClientScreen.Location);

        foreach (var other in Windows)
        {
            if (other == window || other.IsDisposed) continue;

            // 只有这次拖动可能新接上的缝才预览：缝的一侧必须是被拖的那块。
            // 两块都没被拖、本来就贴在一起的缝不属于这次操作，不该亮。
            if (window != dragged && other != dragged) continue;

            var r = ContentRect(other == dragged ? dragged.PreviewOrigin : other.ClientScreen.Location);
            CollectSeams(me, r, result);
        }
        return result;
    }

    bool OurProcessForeground()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    static bool Key(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    void UpdateInput(float dt)
    {
        bool active = OurProcessForeground();

        float axis = 0;
        float climb = 0;
        bool spaceNow = false;
        if (active)
        {
            if (Key(Native.VK_A)) axis -= 1;
            if (Key(Native.VK_D)) axis += 1;
            if (Key(Native.VK_W) || Key(Native.VK_UP)) climb -= 1;
            if (Key(Native.VK_S) || Key(Native.VK_DOWN)) climb += 1;
            spaceNow = Key(Native.VK_SPACE);
            if (Key(Native.VK_R) && !_spaceDown) Restart();
            if (Key(Native.VK_ESCAPE)) Quit();
        }

        // M 静音：按下沿触发，和右键菜单共用同一个开关
        bool mNow = active && Key(Native.VK_M);
        if (mNow && !_mDown) Audio.ToggleMute();
        _mDown = mNow;

        if (spaceNow && !_spaceDown) Player.Buffer = Player.JumpBuffer;
        _spaceDown = spaceNow;

        Player.ClimbAxis = climb;
        Player.Buffer = MathF.Max(0, Player.Buffer - dt);
        Player.Vel.X = axis * Player.Speed;
        if (axis != 0) Player.FacingRight = axis > 0;
        Audio.SetWalking(active && axis != 0 && Player.OnGround);

        if (Player.OnGround) Player.Coyote = Player.CoyoteTime;
        else Player.Coyote = MathF.Max(0, Player.Coyote - dt);

        if (Player.Buffer > 0 && Player.Coyote > 0)
        {
            Player.Vel.Y = -Player.JumpVelocity(dt);
            Player.Buffer = 0;
            Player.Coyote = 0;
            Player.OnGround = false;
            Audio.Play(Sfx.Jump);
        }
    }

    bool Collides(Vector2 pos)
    {
        float l = pos.X - Player.Width / 2f, r = pos.X + Player.Width / 2f;
        float t = pos.Y - Player.Height / 2f, b = pos.Y + Player.Height / 2f;

        int x0 = (int)MathF.Floor(l), x1 = (int)MathF.Floor(r - 0.0001f);
        int y0 = (int)MathF.Floor(t), y1 = (int)MathF.Floor(b - 0.0001f);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Player.Piece.SolidAt(x, y)) return true;

        // 取景框之外：那块有邻窗就放行（跨过去后交接），没有就是看不见的墙
        var w = WindowFor(Player.Piece);
        if (w == null) return true;

        if (!InsideSomeWindow(w, l, t) || !InsideSomeWindow(w, r - 0.0001f, t) ||
            !InsideSomeWindow(w, l, b - 0.0001f) || !InsideSomeWindow(w, r - 0.0001f, b - 0.0001f))
            return true;

        return false;
    }

    /// <summary>脚底落在平台顶面时的容差（格）。浮点误差别让它卡在"差一点"上。</summary>
    const float PlatformEps = 0.0001f;

    /// <summary>
    /// 下落时的碰撞：实心照旧；单向平台只在「这一小步之前脚底还在平台顶面（含）之上」时接住。
    ///
    /// 这样三种情况都对：
    ///   · 站在平台上 —— 脚底正好等于顶面，接住，不会掉下去；
    ///   · 从下面跳上来 —— 上升那一步走的是 <see cref="Collides"/>，平台完全不拦，直接穿过去；
    ///     只要跳得够高、脚底越过顶面，落回来就踩在上面（跳不够高则会从平台里掉回去，符合预期）；
    ///   · 横向走过 —— 横向也用 <see cref="Collides"/>，侧面不挡。
    ///
    /// 每一步位移被钳在 0.2 格以内，所以一次只会跨过至多一格，不会漏检穿透。
    /// </summary>
    bool CollidesFalling(Vector2 pos, float feetBefore)
    {
        if (Collides(pos)) return true;

        float l = pos.X - Player.Width / 2f, r = pos.X + Player.Width / 2f;
        float feet = pos.Y + Player.Height / 2f;

        int x0 = (int)MathF.Floor(l), x1 = (int)MathF.Floor(r - 0.0001f);
        int y0 = (int)MathF.Floor(feetBefore - PlatformEps);
        int y1 = (int)MathF.Floor(feet - PlatformEps);

        for (int y = y0; y <= y1; y++)
        {
            // 脚底本来就在这块平台的顶面之下 → 让它穿过去，别把人从平台里顶出来
            if (feetBefore > y + PlatformEps) continue;
            for (int x = x0; x <= x1; x++)
                if (Player.Piece.PlatformAt(x, y)) return true;
        }

        return false;
    }

    bool InsideSomeWindow(GameWindow owner, float mapX, float mapY)
    {
        // 只认可"走得到"的窗口：自己 + 直接相邻的。
        // 重叠但不相邻的窗口不算——否则角色会从接缝以外的地方飘到别的房间。
        var u = ViewLocal(owner, new Vector2(mapX, mapY));
        if (InView(u)) return true;   // 还在自己的取景框里（自己那块地图的实心在 Collides 里查过了）

        foreach (var w in owner.Neighbors)
        {
            if (w.IsDisposed) continue;

            var tu = u - ViewOffset(owner, w);
            if (!InView(tu)) continue;

            // 跨到隔壁时落到对方那张地图上：那一格是墙就走不过去，
            // 否则角色会被塞进墙里，之后所有方向都被挡住，看着就像卡死了。
            float mx = w.CamX + tu.X, my = w.CamY + tu.Y;
            return !w.Piece.SolidAt((int)MathF.Floor(mx), (int)MathF.Floor(my));
        }

        return false;
    }

    void MoveAndCollide(float dt)
    {
        Player.LadderLock = MathF.Max(0, Player.LadderLock - dt);

        // 抓住梯子：不落、上下走；在梯子上也能起跳（跳开后短暂不再被抓回去）
        bool onLadder = Player.LadderLock <= 0 && Player.Piece.ClimbAt(Player.Pos.X, Player.Pos.Y);

        if (onLadder && Player.Buffer > 0)
        {
            Player.Vel.Y = -Player.VelocityFor(Player.JumpHeight, dt);
            Player.Buffer = 0;
            Player.Coyote = 0;
            Player.LadderLock = Player.LadderJumpLock;
            onLadder = false;
        }

        if (onLadder)
        {
            Player.Vel.Y = Player.ClimbAxis * Player.ClimbSpeed;
            Player.OnGround = false;
        }
        else
        {
            float g = Player.Vel.Y > 0 ? Player.Gravity(dt) * Player.FallGravityScale : Player.Gravity(dt);
            Player.Vel.Y += g * dt;
            if (Player.Vel.Y > Player.MaxFallSpeed) Player.Vel.Y = Player.MaxFallSpeed;
        }

        // horizontal
        float remain = Player.Vel.X * dt;
        while (MathF.Abs(remain) > 1e-5f)
        {
            float step = Math.Clamp(remain, -0.2f, 0.2f);
            var next = Player.Pos + new Vector2(step, 0);
            if (!Collides(next)) { Player.Pos = next; remain -= step; }
            else { Player.Vel.X = 0; break; }
        }

        // vertical
        Player.OnGround = false;
        remain = Player.Vel.Y * dt;
        while (MathF.Abs(remain) > 1e-5f)
        {
            float step = Math.Clamp(remain, -0.2f, 0.2f);
            var next = Player.Pos + new Vector2(0, step);

            // 往上走无视单向平台（直接顶穿），往下走才可能被平台顶面接住
            float feet = Player.Pos.Y + Player.Height / 2f;
            bool blocked = step > 0 ? CollidesFalling(next, feet) : Collides(next);

            if (!blocked) { Player.Pos = next; remain -= step; }
            else
            {
                if (step > 0)
                {
                    Player.OnGround = true;

                    // 贴地：把脚底精确对到落到的方块上表面。
                    // 不贴的话会停在离地最多 0.2 格的位置，下一帧判定"离地"、
                    // 再下一帧又"落地"，落地音就会连着响好几遍（移动时尤其明显）。
                    // 实心是"撞进去再抬回来"，单向平台是"还没碰到就被接住"，
                    // 所以上下两个方向都要贴，否则平台上方会留一层看不出来的悬浮。
                    float landedRow = MathF.Floor(next.Y + Player.Height / 2f);
                    float snappedY = landedRow - Player.Height / 2f;
                    if (snappedY != Player.Pos.Y &&
                        MathF.Abs(snappedY - Player.Pos.Y) <= 0.25f &&
                        !Collides(new Vector2(Player.Pos.X, snappedY)))
                        Player.Pos = new Vector2(Player.Pos.X, snappedY);

                    // 踩在弹簧上：直接给一个能弹起 SpringHeight 的初速度
                    if (Player.Piece.SpringAt(Player.Pos.X, Player.Pos.Y + Player.Height / 2f + 0.1f))
                    {
                        Player.Vel.Y = -Player.VelocityFor(Player.SpringHeight, dt);
                        Player.OnGround = false;
                        break;
                    }
                }

                Player.Vel.Y = 0;
                break;
            }
        }

        // 落地只在「真正滞空过 → 触地」的边沿响一次；
        // 更细的 0.3s 去抖交给 AudioManager（落地音的事件级 MinInterval），
        // 那里是"忽略重复播放"的唯一出口，别在两处各写一套。
        if (Player.OnGround)
        {
            if (!_wasOnGround && _airTime >= LandMinAirTime) Audio.Play(Sfx.Land);
            _airTime = 0f;
        }
        else
        {
            _airTime += dt;
        }
        _wasOnGround = Player.OnGround;
    }

    void UpdateCameras(float dt)
    {
        var w = WindowFor(Player.Piece);
        if (w == null) return;

        // 换了窗口就把惯性清掉，别把上一个窗口的滚动速度带过来
        if (_camWindow != w)
        {
            _camVelX = _camVelY = 0f;
            _camWindow = w;
        }

        if (w.HasNeighbors)
        {
            // 相连时取景框锁死，并对齐整格，免得接缝错半个瓦片
            w.SnapCameraIfNeeded();
            _camVelX = _camVelY = 0f;
            return;
        }

        // 焦点取「角色所在格的上边缘中点」：X 是格中心、Y 是格上边缘。
        // 取景框 15x10，算出来的相机两轴都落在整数格上，窗口之间始终对齐；
        // 代价是角色跨格时目标跳一格，由下面的 S 形跟随消化。
        var target = CameraTarget(Player.Pos);

        // 「速度惯性 + 位置反馈」：目标跳变时速度从 0 平滑升起、接近目标时平滑收住，
        // 位移走 S 形（起步/收尾都慢），不再像指数缓动那样第一帧就猛迈一大步。
        // 连续跨格时速度会稳定追上角色，不会因为逐次重置过渡而被越甩越远。
        float a = 1f - MathF.Exp(-dt / CamVelSmooth);
        _camVelX += ((target.X - w.CamX) / CamFollowTime - _camVelX) * a;
        _camVelY += ((target.Y - w.CamY) / CamFollowTime - _camVelY) * a;

        float nx = w.CamX + _camVelX * dt;
        float ny = w.CamY + _camVelY * dt;

        // 二阶系统会有一点点超调，越过去就钳在整数目标上，免得在目标附近来回晃
        if ((target.X - w.CamX) * (target.X - nx) < 0f)
        {
            nx = target.X;
            _camVelX = 0f;
        }
        if ((target.Y - w.CamY) * (target.Y - ny) < 0f)
        {
            ny = target.Y;
            _camVelY = 0f;
        }

        // 停稳后精确落在整数目标上，别留 3.9997 这种残差
        if (MathF.Abs(target.X - w.CamX) < 0.01f && MathF.Abs(_camVelX) < 0.05f)
        {
            nx = target.X;
            _camVelX = 0f;
        }
        if (MathF.Abs(target.Y - w.CamY) < 0.01f && MathF.Abs(_camVelY) < 0.05f)
        {
            ny = target.Y;
            _camVelY = 0f;
        }

        // 兜底：平滑跟随追不上时（长距离快速坠落、弹簧弹射），把相机钳进安全区间，
        // 保证角色不会先跑出取景框（框外会被当成实心地面 → 假落地）
        (nx, ny) = ClampCameraToBand(w, nx, ny, dt);

        if (nx != w.CamX || ny != w.CamY) w.SetCamera(nx, ny);
    }

    /// <summary>取景框里"角色必须待在"的那段区间：25% ~ 75%。</summary>
    public const float CamSafeMin = 0.25f, CamSafeMax = 0.75f;

    /// <summary>约束兜底时最低的追赶速度（格/秒）。角色站着不动也按这个速度滑过去。</summary>
    const float CamClampMinRate = 8f;

    /// <summary>
    /// 相机硬约束：把相机钳到「角色落在取景框 25%~75% 区间内」。
    /// 上面的跟随参数一概不动，这层只在「追不上」时兜底。
    ///
    /// 为什么必须有：<see cref="Collides"/> 把"取景框之外"当成实心，
    /// 但判定用的是当前帧的相机，而相机是滞后跟随的。长距离快速坠落时
    /// 相机按惯性落后 3~4 格，角色会先掉出框底 —— 空气被当成地面，
    /// 于是「假落地」：OnGround 变真、下落速度被清零。
    /// 钳住之后遇到同样的情况就是相机会和角色 1:1 移动，角色永远跑不出框。
    /// </summary>
    (float X, float Y) ClampCameraToBand(GameWindow w, float cx, float cy, float dt)
    {
        float l = Player.Pos.X - Player.Width / 2f, r = Player.Pos.X + Player.Width / 2f;
        float t = Player.Pos.Y - Player.Height / 2f, b = Player.Pos.Y + Player.Height / 2f;

        // 左上角不能越过 25%，右下角不能越过 75%
        float minX = r - GameWindow.ViewTilesX * CamSafeMax;
        float maxX = l - GameWindow.ViewTilesX * CamSafeMin;

        // 约束是硬的，但每帧拉多少要限速：跟得上时完全用不到这层；
        // 追不上时按角色自己的速度（+6 格/秒余量）1:1 追上去；
        // 而「相机离得很远、角色却站着不动」（比如刚松手重排窗口）则平滑滑过去，
        // 不会因为硬钳而一帧跳好几格。
        float rate = MathF.Max(CamClampMinRate,
            MathF.Max(MathF.Abs(Player.Vel.X), MathF.Abs(Player.Vel.Y)) + 6f);
        float step = rate * dt;

        float nx = ConstrainRate(cx, minX, maxX, step);

        // 纵向同理，两轴一律钳：地图再矮也是一整圈，角色同样不能掉出框底
        float minY = b - GameWindow.ViewTilesY * CamSafeMax;
        float maxY = t - GameWindow.ViewTilesY * CamSafeMin;
        float ny = ConstrainRate(cy, minY, maxY, step);

        return (nx, ny);
    }

    /// <summary>把 v 往 [lo, hi] 里收，但每帧最多移动 step。</summary>
    static float ConstrainRate(float v, float lo, float hi, float step)
    {
        if (v < lo) return MathF.Min(lo, v + step);
        if (v > hi) return MathF.Max(hi, v - step);
        return v;
    }

    /// <summary>
    /// 自由滚动时的相机目标。焦点是角色所在格的「上边缘中点」：
    /// X 取格中心 (+0.5)、Y 取格上边缘（整数）。配合 15x10 取景框，
    /// CamX = floor(x) - 7、CamY = floor(y) - 5，两轴都是整数，
    /// 拖动窗口时画面相位和别的窗口完全一致。
    ///
    /// 两轴都无条件跟随：地图是环形（torus），尺寸和取景框一样大甚至更小时，
    /// 滚动就是绕着同一圈转，画面照样连续，不会出现"走到边上被挡住"。
    /// </summary>
    static Vector2 CameraTarget(Vector2 pos) => new(
        MathF.Floor(pos.X) + 0.5f - GameWindow.ViewTilesX / 2f,
        MathF.Floor(pos.Y) - GameWindow.ViewTilesY / 2f);

    // ── 网格事件 ──────────────────────────────────────────────────────────

    /// <summary>本关已经触发过的事件下标。每次读关卡 / 按 R 重置。</summary>
    readonly HashSet<int> _firedEvents = new();

    /// <summary>
    /// 每帧检查角色所在那一格挂着的事件串。
    ///
    /// 触发口径：角色身体中心落在哪一格就算"碰到"那一格（和以前拿宝石一致）。
    /// 一格可以挂多条事件，**按顺序逐条判定**：条件满足就执行，不满足就跳过接着判下一条。
    /// 前置条件不满足的事件不会被标记成已触发 —— 玩家在格子里站着不动、
    /// 把摄像机滚到指定范围，事件照样会亮，这正是解谜要的手感。
    /// 同一条事件一次进入关卡最多触发一次。
    ///
    /// 关键：某条事件把地图换掉了（跳关），这一格剩下的序列立刻全部取消 ——
    /// 地图、角色、格子都已经不是刚才那个了，继续拿旧状态往下判定只会出鬼。
    /// </summary>
    void UpdateEvents()
    {
        if (Level.Events.Count == 0) return;

        var piece = Player.Piece;
        if (piece == null) return;

        int seqIndex = piece.EvtSeqAt((int)MathF.Floor(Player.Pos.X), (int)MathF.Floor(Player.Pos.Y));
        if (seqIndex < 0 || seqIndex >= Level.EvtSeqs.Count) return;

        var seq = Level.EvtSeqs[seqIndex];
        for (int k = 0; k < seq.Count; k++)
        {
            int idx = seq[k];
            if (idx < 0 || idx >= Level.Events.Count) continue;
            if (_firedEvents.Contains(idx)) continue;

            var ev = Level.Events[idx];
            if (ev.Trigger != EventTrigger.Touch) continue;
            if (!ConditionsMet(ev)) continue;

            _firedEvents.Add(idx);
            Fire(ev);

            // 地图切换已经开始：剩下的取消，等新关卡把一切重置干净
            if (Fading) return;
        }
    }

    /// <summary>
    /// 前置条件全过才算成立。
    /// 条件里的地图没开着窗口时按"不成立"处理 —— 看不见的取景框谈不上滚动位置，
    /// 与其蒙一个值，不如让玩家先把那个窗口开出来。
    /// </summary>
    bool ConditionsMet(GridEvent ev)
    {
        foreach (var cond in ev.Conditions)
        {
            int matched = 0;
            foreach (var w in Windows)
            {
                if (w.IsDisposed) continue;
                if (cond.Piece != "*" && w.Piece.Id != cond.Piece) continue;

                matched++;
                if (cond.CamX is { Length: >= 2 } cx &&
                    !InScrollRange(w.CamX, cx[0], cx[1], w.Piece.Width)) return false;
                if (cond.CamY is { Length: >= 2 } cy &&
                    !InScrollRange(w.CamY, cy[0], cy[1], w.Piece.Height)) return false;
            }
            if (matched == 0) return false;
        }
        return true;
    }

    /// <summary>
    /// 环形地图上的区间判定：把 value 相对 min 的差折算进 [0, 一个周期)，
    /// 不超过 span 就算在区间里。span 覆盖整圈时恒成立。
    /// 单位是格——就是 <see cref="GameWindow.CamX"/> / <see cref="GameWindow.CamY"/> 用的那一套。
    /// </summary>
    public static bool InScrollRange(float value, float min, float max, int period)
    {
        if (period <= 0) return true;
        float span = max - min;
        if (span < 0) { (min, max) = (max, min); span = -span; }
        if (span >= period) return true;
        return WrapInto(value - min, period) <= span + 0.001f;
    }

    /// <summary>
    /// 执行一条事件的动作串。按顺序来，但只要有一条把地图切走了，后面的一律不再执行 ——
    /// 地图都没了，再开程序 / 再跳关都是在旧状态上瞎跑。
    /// </summary>
    void Fire(GridEvent ev)
    {
        foreach (var act in ev.Actions)
        {
            if (Fading) return;
            Execute(act);
        }
    }

    void Execute(EventActionDef act)
    {
        switch (act.Type)
        {
            case "goto":
                if (string.IsNullOrWhiteSpace(act.Level)) return;   // 目标还没填：当它不存在
                BeginFadeTo(act.Level);
                Audio.Play(Sfx.Gem);
                break;

            case "run":
                Launch(act.Target, act.Args);
                break;
        }
    }

    /// <summary>
    /// 在电脑上打开一个程序 / 网址 / 文件（走 ShellExecute，等于双击它）。
    /// 不阻塞、不接管进程；打不开就记一行日志，绝不让游戏崩在这里。
    /// </summary>
    void Launch(string target, string args)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"run '{target}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── 跳转关卡的过场 ────────────────────────────────────────────────────

    enum FadePhase { None, Out, In }

    FadePhase _fadePhase = FadePhase.None;

    /// <summary>白场强度 0..1：Out 时 0→1，换关后 In 时 1→0。</summary>
    float _fade;

    string _pendingLevel = "";

    const float FadeOutTime = 0.20f;
    const float FadeInTime = 0.34f;

    /// <summary>正在过场（切关卡的闪白）。这期间物理和输入全部挂起。</summary>
    public bool Fading => _fadePhase != FadePhase.None;

    /// <summary>过场白场的当前强度，GameWindow 画遮罩用。</summary>
    public float FadeAlpha => Math.Clamp(_fade, 0f, 1f);

    /// <summary>换关卡时程序在拆/建窗口，窗口的关闭事件不该当成"退出游戏"。</summary>
    bool _swapping;

    public bool Swapping => _swapping;

    /// <summary>程序自己在开窗（换关卡），不响"新窗口"音效。</summary>
    bool _suppressSfx;

    void BeginFadeTo(string level)
    {
        if (Fading) return;
        _pendingLevel = level;
        _fadePhase = FadePhase.Out;
        _fade = 0f;
        Player.Vel = Vector2.Zero;
        Audio.SetWalking(false);
    }

    void UpdateFade(float dt)
    {
        if (_fadePhase == FadePhase.Out)
        {
            _fade += dt / FadeOutTime;
            if (_fade < 1f) return;

            _fade = 1f;
            SwitchLevel(_pendingLevel);
            _pendingLevel = "";
            _fadePhase = FadePhase.In;
        }
        else if (_fadePhase == FadePhase.In)
        {
            _fade -= dt / FadeInTime;
            if (_fade > 0f) return;
            _fade = 0f;
            _fadePhase = FadePhase.None;
        }
    }

    /// <summary>
    /// 真正换关：先读新关卡，读成功了才拆旧窗口（读失败就留在原关卡，白场照样淡出）。
    /// 拆干净之后按新关卡重开一个窗口、角色放回它的出生点。
    /// </summary>
    void SwitchLevel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!TryLoadLevel(name)) return;

        _swapping = true;
        try
        {
            foreach (var w in Windows.ToArray())
                if (!w.IsDisposed) w.Close();
            Windows.Clear();
        }
        finally { _swapping = false; }

        _groups.Clear();
        _groupSignature = "";
        SeamFlashes.Clear();
        _firedEvents.Clear();
        _camWindow = null;
        _camVelX = _camVelY = 0f;
        _lastWindow = null;
        DraggingWindow = null;
        _wasOnGround = false;
        _airTime = 0f;

        // 角色状态清干净：旧关卡的地图块马上会被新窗口的出生点顶替
        Player.Pos = Vector2.Zero;
        Player.Vel = Vector2.Zero;
        Player.Buffer = 0;
        Player.OnGround = false;
        Player.Coyote = 0;
        Player.LadderLock = 0;

        _suppressSfx = true;
        try { OpenWindow(); }
        finally { _suppressSfx = false; }
    }

    void Respawn()
    {
        var w = _lastWindow;
        if (w == null || w.IsDisposed) w = Windows.Count > 0 ? Windows[0] : null;
        if (w == null) return;
        PlaceAtSpawn(w);
        Player.Vel = Vector2.Zero;
    }

    void Restart()
    {
        _firedEvents.Clear();
        if (Windows.Count == 0) return;
        var w = Windows[0];
        var spawnPos = new Vector2(w.Piece.Spawn.X + 0.5f, w.Piece.Spawn.Y + 0.5f);
        var cam = CameraTarget(spawnPos);
        w.SetCamera(cam.X, cam.Y);
        PlaceAtSpawn(w);
        Player.Vel = Vector2.Zero;
    }

    /// <summary>换关卡的闪白遮罩。画在所有窗口内容与光效之上。</summary>
    public void DrawOverlay(Graphics g, GameWindow w)
    {
        float a = FadeAlpha;
        if (a <= 0.002f) return;

        if (a >= 1f)
        {
            g.Clear(Color.White);
            return;
        }

        int alpha = (int)(a * 255);
        using var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        g.FillRectangle(brush, 0, 0, w.ClientSize.Width, w.ClientSize.Height);
    }
}
