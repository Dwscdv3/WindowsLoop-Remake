using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using WindowsLoop.Tileset;

namespace WindowsLoop;

internal sealed partial class Game
{
    public const float FixedDt = 1f / 60f;

    public readonly List<GameWindow> Windows = new();
    public readonly Player Player = new();
    public Level Level = null!;
    public Bitmap TileSheet = null!, PlayerSheet = null!;

    /// <summary>箱子贴图（16x16，一格）。素材缺失时为 null，箱子会退化成画一个色块。</summary>
    public Bitmap? CrateSheet;

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
    bool _nDown;   // N 新开窗口
    bool _rDown;   // R 重开的边沿检测，别借用跳跃键的状态
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


    /// <summary>关卡文件名（可以带 .json，也可以只是一个名字）。</summary>
    public string LevelName = "main";

    public void Init()
    {
        // 瓦片默认值要先装：读关卡时会按主贴图查这张表，缺了会让所有碰撞退回 false。
        Assets.LoadEmbeddedTileDefaults();
        if (!TryLoadLevel(LevelName)) TryLoadLevel("main");
        TileSheet = Assets.LoadBitmap("tiles.png");
        PlayerSheet = Assets.LoadBitmap("player.png");
        try { CrateSheet = Assets.LoadBitmap("crate.png"); }
        catch { CrateSheet = null; }   // 素材没打进包也别把整局拖死，箱子退化成色块
        OpenWindow();
    }

    /// <summary>
    /// 读一关进来（只替换 Level，不动窗口）。关卡文件不存在或读坏了返回 false 并保持原关卡，
    /// 免得一个填错的跳转目标把整个游戏带崩。
    /// 关卡直接编在程序集里（asset/levels/*.json）；额外允许传一个磁盘绝对路径用来临时试关卡。
    /// </summary>
    public bool TryLoadLevel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var file = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";

        string? json = Assets.ReadText("levels/" + file);
        if (json == null && Path.IsPathRooted(file) && File.Exists(file)) json = File.ReadAllText(file);
        if (json == null)
        {
            Log($"load level '{name}' failed: not embedded (asset/levels/{file})");
            return false;
        }

        try
        {
            var level = Level.Parse(json);
            if (level.Pieces.Count == 0)
            {
                Log($"load level '{name}' failed: no pieces");
                return false;
            }
            Level = level;
            LevelName = name;
            BuildEventRuntime();   // 隐藏格 / 压力板引用的预表，跟着关卡一起换
            return true;
        }
        catch (Exception ex)
        {
            Log($"load level '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Enqueue(Action action) => _actions.Enqueue(action);

    /// <summary>
    /// 第一块窗口的默认客户区原点：启动进关时把窗口摆在这里。
    /// 重开不回到这个点 —— 那个位置属于玩家，留用窗口原位不动。
    /// </summary>
    public static Point DefaultClientOrigin => GameWindow.ClampToScreen(new Point(240, 160));

    public void PumpActions()
    {
        while (_actions.TryDequeue(out var action))
            action();
    }

    public Piece PieceForSlot(int slot) => Level.Pieces[slot % Level.Pieces.Count];

    /// <summary>
    /// 直接开一个窗口（不带动画）。开局进关、以及没有母体可当"吐出方"时的兜底走这里。
    /// 玩家手动开窗请用 <see cref="SpawnWindow"/>。
    /// </summary>
    public void OpenWindow()
    {
        if (DebugLayout) Log($"open: windows={Windows.Count} max={Level.MaxWindows}");
        if (Windows.Count >= Level.MaxWindows) return;

        Point clientOrigin;
        if (Windows.Count == 0)
        {
            clientOrigin = DefaultClientOrigin;
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

        bool first = Windows.Count == 0;
        if (CreateWindowAt(clientOrigin) == null) return;

        RefreshLayout();
        if (!first && !_suppressSfx) Audio.Play(Sfx.WindowOpen);
    }

    /// <summary>
    /// 造一块窗口并按给定客户区原点显示出来。位置规则、动效、音效都归调用方管。
    /// 返回 null 表示已经到关卡的最大窗口数。
    /// </summary>
    /// <param name="spawning">
    /// true = 这块窗口是"新生动画"里飞出来的：**显示之前**就打上 <see cref="GameWindow.Spawning"/>，
    /// 免得它在 Show 触发的第一次布局里就被算成一个正常窗口。
    /// </param>
    GameWindow? CreateWindowAt(Point clientOrigin, bool spawning = false)
    {
        if (Windows.Count >= Level.MaxWindows) return null;

        var piece = PieceForSlot(Windows.Count);
        var window = new GameWindow(this, piece, clientOrigin) { Spawning = spawning };

        // 镜头先对准这块地图的出生点：窗口一显示就是"刚进这一块"的画面
        var spawnPos = new Vector2(piece.Spawn.X + 0.5f, piece.Spawn.Y + 0.5f);
        var cam = CameraTarget(spawnPos);
        window.SetCamera(cam.X, cam.Y);

        Windows.Add(window);
        window.Show();

        if (Windows.Count == 1) PlaceAtSpawn(window);
        return window;
    }

    // ── 新生窗口的动画：抖动 → 吐出 → 飞出弹停 ──────────────────────────

    enum SpawnPhase { Shake, Fly, Settle }

    /// <summary>一次"吐出一个窗口"的演出状态。同一时间只演一个。</summary>
    sealed class SpawnAnim
    {
        public SpawnPhase Phase = SpawnPhase.Shake;
        public float T;

        /// <summary>母体组的每一块，以及它抖动前的位置 —— 抖完必须分毫不差地放回去。</summary>
        public readonly List<(GameWindow Window, Point Origin)> Group = new();

        /// <summary>母体里最靠近吐出方向的那块，新窗口从它身上冒出来。</summary>
        public GameWindow Edge = null!;

        /// <summary>正在飞的那块。</summary>
        public GameWindow Flying = null!;

        public Vector2 Dir;

        /// <summary>飞行中的浮点客户区原点（窗口坐标只能是整数，这里保留小数，末帧再取整）。</summary>
        public Vector2 Pos, Vel;

        /// <summary>收尾：从停下的位置收到哪个整格点。</summary>
        public Vector2 SettleFrom, SettleTo;

        /// <summary>撞边时已经按"整格落点"反算过回弹初速，收尾不用再吸。</summary>
        public bool Retargeted;

        /// <summary>
        /// 蓄力音效的音量包络（0..1）。抖动幅度直接取"这一帧声音有多响"，
        /// 所以素材里有几下起伏，窗口就抖几下 —— 不用手工对节拍。
        /// </summary>
        public float[]? Env;

        /// <summary>蓄力要抖多久 = 蓄力音效的时长（读不到时用 <see cref="DefaultShakeTime"/>）。</summary>
        public float ShakeTime = DefaultShakeTime;

        /// <summary>蓄力音效只起播一次。</summary>
        public bool Sounded;

        /// <summary>上一帧的真实步长：撞边反算落点速度要用它。</summary>
        public float LastDt = FixedDt;
    }

    SpawnAnim? _spawn;

    /// <summary>演出途中又按了一次：等这次演完接着演下一次，别把玩家的输入吞掉。</summary>
    bool _spawnQueued;

    /// <summary>正在演"新生窗口"。这期间不提交相邻关系、窗口也不接受拖动。</summary>
    public bool SpawnAnimating => _spawn != null;

    const float DefaultShakeTime = 1.0f;   // 读不到蓄力音效时长时的兜底
    const float ShakeAmplitude = 8f;       // 抖到最大时偏移几像素
    const float ShakeHz = 15f;             // 抖多快（取 15 = 60fps 的四分之一周期，画面不打架）

    /// <summary>
    /// 空气阻尼系数（就是"摩擦力"）。速度按 v *= exp(-k·dt) 等比衰减，自由行程 ≈ v0/k。
    /// 调小 → 衰减更慢、尾巴更长、飞得更久；调大 → 很快刹住。
    /// </summary>
    const float FlyDrag = 1.95f;

    /// <summary>撞屏幕边后保留的速度比例 —— "反弹一下"就是它。</summary>
    const float FlyBounce = 0.2f;

    /// <summary>
    /// 飞出时的"行程余量"：初速按 (到边距离 + 余量) 给，于是撞边那一刻还剩
    /// FlyDrag × 余量 的速度，而**回弹距离 = FlyBounce × 余量**（和摩擦系数无关）。
    /// 现在 720 → 回弹约 144px（三格半），总飞行时间也一起拉到 1.2s 上下。
    /// </summary>
    const float FlyRunOut = 720f;

    /// <summary>
    /// 撞边后**看得见**的回弹距离：按恢复系数接过来的速度，衰减到停速为止走过的路程。
    /// 用来给"整格落点"定一个参照位置（见 <see cref="RetargetBounce"/>）。
    /// </summary>
    static float NominalBounceRun =>
        (FlyBounce * FlyDrag * FlyRunOut - FlyStopSpeed) / FlyDrag;

    /// <summary>
    /// 选方向时垂直方向（上/下）的权重。窗口是 3:2 —— 同样多的像素余量，
    /// 竖直方向能容下的"窗口数"是水平方向的 600/400 = 1.5 倍。
    /// </summary>
    const float VerticalWeight = 1.5f;

    const float FlyStopSpeed = 70f;      // px/s，低于这个算停稳
    const float FlyMaxTime = 3f;         // 兜底：万一一直慢不下来
    const float SettleTime = 0.13f;      // 停稳后把位置收进整格用的时间

    /// <summary>
    /// 玩家开一个新窗口（N 键 / 右键菜单 / 再次双击程序都走这里）。
    ///
    /// 演出：先让一个窗口组快速颤抖约 1 秒（幅度越来越大），再从它里面最靠近吐出方向的
    /// 那块窗口身上，把新窗口朝那个方向吐出去；新窗口以指数阻尼减速飞行，
    /// 撞到屏幕边上按恢复系数弹一下，停稳后收进整格。
    ///
    /// 选母体和方向**都不是随机**：全局取"到工作区边界余量最大"的那一个方向
    /// （见 <see cref="PickSpawnSite"/>），由拥有它的窗口组当母体。
    ///
    /// 窗口数已经到顶时不建窗口，改成演一次"憋喷嚏"（见 <see cref="BeginStifle"/>）。
    /// </summary>
    public void SpawnWindow()
    {
        if (Fading || Paused) return;                    // 换关过场 / 拖窗口时不演
        if (Windows.Count == 0) { OpenWindow(); return; }

        // 到上限了：不建窗口，让本来该吐的那个组憋一个喷嚏。
        // 这时候不能再排进队列 —— 队列是"等这一场演完接着吐"，而这里已经吐不出东西了。
        if (Windows.Count >= Level.MaxWindows)
        {
            if (_spawn == null && _stifle == null) BeginStifle();
            return;
        }

        // 上一次还没演完（一次只演一个）：记一笔，等它落地后再接着演，
        // 这样连按 N 不会只出来一个窗口，也不会两场动画抢同一批窗口
        if (_spawn != null) { _spawnQueued = true; return; }

        SyncGroups();   // 母体候选必须是这一帧真实的窗口组
        if (!PickSpawnSite(out var mother, out var edge, out var dir))
        {
            OpenWindow();   // 没窗口组可当母体，退回直接开窗
            return;
        }

        // 新窗口在抖动阶段就建好，全程和"最靠近吐出方向的那块"完全重合。
        // 它被压在母体组最下面（PutBelowGroup），所以这一段完全看不见；
        // 等真被吐出去那一刻才开始移动 —— 也就不会出现"建窗口那一帧闪一下"。
        var window = CreateWindowAt(edge.ClientScreen.Location, spawning: true);
        if (window == null) return;

        PutBelowGroup(window, mother);

        var anim = new SpawnAnim { Flying = window, Edge = edge, Dir = dir };
        foreach (var w in mother)
            if (!w.IsDisposed) anim.Group.Add((w, w.ClientScreen.Location));

        // 蓄力音效决定两件事：抖多久、每一帧抖多厉害
        float len = Audio.SfxLength(Sfx.Ahhh);
        var env = Audio.SfxEnvelope(Sfx.Ahhh);
        if (len > 0.05f) anim.ShakeTime = Math.Clamp(len, 0.3f, 5f);
        if (env.Length > 0) anim.Env = env;

        _spawn = anim;
    }

    /// <summary>
    /// 挑母体窗口组和吐出方向。
    ///
    /// 口径：把每个窗口组 × 四个方向都算一遍"这条通道还能飞多远"，
    /// **全局取最大的那一个** —— 挑的不是"某个组"，而是"当前屏幕上最开阔的那一处"，
    /// 由拥有这个方向的组来吐。
    ///
    /// "还能飞多远"= 从组里最靠外的这块窗口出发，沿这个方向到**最近的挡路物**的距离，
    /// 挡路物既包括屏幕边界，**也包括别的窗口**（见 <see cref="Runway"/>）——
    /// 只按屏幕边界算的话，会挑出"前面横着一块窗口"的方向，让新窗口从人家身上飞过去。
    /// 注意这只管"选哪个方向"：真飞起来仍然只在屏幕边界反弹，不会撞窗口。
    ///
    /// 所以这里没有任何随机成分：同一套布局，按多少次 N 都是同一个方向。
    /// 垂直方向另有 1.5 倍权重，见 <see cref="VerticalWeight"/>。
    /// </summary>
    bool PickSpawnSite(out List<GameWindow> mother, out GameWindow edge, out Vector2 dir)
    {
        mother = new List<GameWindow>();
        dir = Vector2.Zero;
        edge = null!;

        List<GameWindow>? bestMembers = null;
        GameWindow? bestEdge = null;
        int bestDir = -1;
        float bestScore = float.NegativeInfinity;

        foreach (var group in _groups)
        {
            var live = new List<GameWindow>();
            foreach (var w in group.Windows)
                if (!w.IsDisposed && !w.Spawning) live.Add(w);
            if (live.Count == 0) continue;

            for (int i = 0; i < 4; i++)
            {
                // 方向定了才知道从哪块出发：那块决定了这条通道有多宽、谁算挡路
                var from = EdgeToward(live, i);
                if (from == null) continue;

                // 垂直方向 ×1.5：窗口是 3:2（600×400），同样多的像素余量，
                // 竖直方向相当于能容下 1.5 倍的"窗口宽度"那么多空间；
                // 按窗口尺寸归一化就是 余量/400 对 余量/600，正好差这个系数。
                float score = Runway(from, i) * (i >= 2 ? VerticalWeight : 1f);
                if (score <= bestScore) continue;

                bestScore = score;
                bestDir = i;
                bestEdge = from;
                bestMembers = live;
            }
        }

        if (bestMembers == null || bestEdge == null || bestDir < 0) return false;

        mother = bestMembers;
        edge = bestEdge;
        dir = DirOf(bestDir);
        return true;
    }

    static Vector2 DirOf(int dirIndex) => dirIndex switch
    {
        0 => new Vector2(-1, 0),
        1 => new Vector2(1, 0),
        2 => new Vector2(0, -1),
        _ => new Vector2(0, 1),
    };

    /// <summary>组里"最靠近这个方向"的那块：往右取最右、往下取最下，往左/上取最左/最上。</summary>
    static GameWindow? EdgeToward(List<GameWindow> windows, int dirIndex)
    {
        GameWindow? best = null;
        float at = float.NegativeInfinity;
        foreach (var w in windows)
        {
            var r = w.ClientScreen;
            float along = dirIndex switch { 0 => -r.Left, 1 => r.Right, 2 => -r.Top, _ => r.Bottom };
            if (best == null || along > at) { at = along; best = w; }
        }
        return best;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 这块窗口出发朝 dirIndex 方向，**到最近的挡路物还有多远**。
    /// 挡路物取屏幕工作区边界与所有别的窗口里最近的那个；只算"同一条通道里"的窗口
    /// （沿飞行方向在前方、且垂直/水平跨度有交集），侧边的窗口不算挡路。
    ///
    /// 母体组自己的成员不算挡路 —— 新窗口本来就是从它们身下钻出来的，Z 序也压在它们下面。
    /// </summary>
    float Runway(GameWindow from, int dirIndex)
    {
        var r = from.ClientScreen;
        var work = Screen.FromRectangle(r).WorkingArea;

        float run = dirIndex switch
        {
            0 => r.Left - work.Left,
            1 => work.Right - r.Right,
            2 => r.Top - work.Top,
            _ => work.Bottom - r.Bottom,
        };

        foreach (var other in Windows)
        {
            if (other == from || other.IsDisposed || other.Spawning) continue;
            if (other.Group == from.Group) continue;   // 母体组自己人不算挡路

            var o = other.ClientScreen;
            switch (dirIndex)
            {
                case 0:   // 左：横跨在自己左边的，且纵向有交集
                    if (o.Right > r.Left || o.Top >= r.Bottom || o.Bottom <= r.Top) continue;
                    run = MathF.Min(run, r.Left - o.Right);
                    break;
                case 1:   // 右
                    if (o.Left < r.Right || o.Top >= r.Bottom || o.Bottom <= r.Top) continue;
                    run = MathF.Min(run, o.Left - r.Right);
                    break;
                case 2:   // 上：纵向在自己上面，且横向有交集
                    if (o.Bottom > r.Top || o.Left >= r.Right || o.Right <= r.Left) continue;
                    run = MathF.Min(run, r.Top - o.Bottom);
                    break;
                default:  // 下
                    if (o.Top < r.Bottom || o.Left >= r.Right || o.Right <= r.Left) continue;
                    run = MathF.Min(run, o.Top - r.Bottom);
                    break;
            }
        }

        return MathF.Max(run, 0f);
    }

    /// <summary>
    /// 把新窗口插到母体组的最下面，让整组在 Z 序上压着它。
    ///
    /// 顶层窗口的 Z 序就是从 GetTopWindow 起顺着 GW_HWNDNEXT 一路往下走，
    /// 走一遍记下最后一个属于这组的句柄 —— 那就是组里最靠下的那块，插到它下面即可。
    /// </summary>
    static void PutBelowGroup(GameWindow window, List<GameWindow> group)
    {
        var members = new HashSet<IntPtr>();
        foreach (var w in group)
            if (!w.IsDisposed) members.Add(w.Handle);

        IntPtr lowest = IntPtr.Zero;
        for (IntPtr h = Native.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDNEXT))
            if (members.Contains(h)) lowest = h;

        if (lowest == IntPtr.Zero) return;

        Native.SetWindowPos(window.Handle, lowest, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void UpdateSpawn(float dt)
    {
        var s = _spawn;
        if (s == null) return;

        // 演出途中被换关/重开拆掉了窗口，直接放弃
        if (s.Flying.IsDisposed || s.Edge.IsDisposed) { _spawn = null; return; }

        switch (s.Phase)
        {
            case SpawnPhase.Shake: UpdateShake(s, dt); break;
            case SpawnPhase.Fly: UpdateFly(s, dt); break;
            case SpawnPhase.Settle: UpdateSettle(s, dt); break;
        }
    }

    void UpdateShake(SpawnAnim s, float dt)
    {
        s.T += dt;

        // 蓄力音效和抖动同一帧起，后面全靠包络对拍
        if (!s.Sounded)
        {
            s.Sounded = true;
            Audio.Play(Sfx.Ahhh);
        }

        // 抖多厉害 = 这一帧蓄力音效有多响。读不到包络才退回原来的 k² 曲线：
        // 前半段只是细微的嗡，最后几帧明显在抖。
        float amp = s.Env != null
            ? ShakeAmplitude * EnvelopeAt(s.Env, s.T)
            : ShakeAmplitude * MathF.Pow(Math.Clamp(s.T / s.ShakeTime, 0f, 1f), 2f);

        float phase = s.T * ShakeHz * MathF.Tau;

        // 整组共用同一个偏移，不是各自抖：拼在一起的两块地图必须一起动，
        // 各抖各的会把接缝抖开，看着像两张纸互相摩擦。
        // 两轴差 90° 相位 = 绕小圈地抖，比单纯左右晃更像"憋着一股劲"。
        var off = new Point(
            (int)MathF.Round(amp * MathF.Sin(phase)),
            (int)MathF.Round(amp * 0.72f * MathF.Cos(phase)));

        foreach (var (w, origin) in s.Group)
            if (!w.IsDisposed) w.SetClientOrigin(new Point(origin.X + off.X, origin.Y + off.Y));

        // 新窗口全程跟着"冒出来的那一块"，被压在它下面所以看不见
        s.Flying.SetClientOrigin(s.Edge.ClientScreen.Location);

        if (s.T >= s.ShakeTime) BeginFly(s);
    }

    void BeginFly(SpawnAnim s)
    {
        // 母体组先归位：抖动留下的偏移必须在吐出这一帧清干净，
        // 否则组内相邻关系和整格对齐都会带着几像素的误差
        foreach (var (w, origin) in s.Group)
            if (!w.IsDisposed) w.SetClientOrigin(origin);

        // 起飞点就取归位后那块窗口的位置：新窗口这一帧正好严丝合缝地贴在它后面，
        // 下一帧开始往外滑，看起来才是"从它身上冒出来"
        s.Flying.SetClientOrigin(s.Edge.ClientScreen.Location);

        s.Phase = SpawnPhase.Fly;
        s.T = 0f;

        var start = s.Flying.ClientScreen.Location;
        s.Pos = new Vector2(start.X, start.Y);

        var size = new Size(GameWindow.ViewTilesX * GameWindow.TileSize, GameWindow.ViewTilesY * GameWindow.TileSize);
        var work = Screen.FromPoint(new Point(start.X + size.Width / 2, start.Y + size.Height / 2)).WorkingArea;

        // 起飞点到该方向屏幕边的距离
        float dist = s.Dir.X > 0 ? work.Right - size.Width - s.Pos.X
                   : s.Dir.X < 0 ? s.Pos.X - work.Left
                   : s.Dir.Y > 0 ? work.Bottom - size.Height - s.Pos.Y
                                 : s.Pos.Y - work.Top;
        dist = MathF.Max(dist, 0f);

        // 指数阻尼下 x(∞) = v0 / FlyDrag，也就是"这点初速最多能飞多远"。
        // 要让它的行程比"到边的距离"多出 FlyRunOut：
        //
        //     v0 / FlyDrag = dist + FlyRunOut   ⇒   v0 = FlyDrag · (dist + FlyRunOut)
        //
        // 多出来的那截行程就是撞边时还剩的动能：速度 = FlyDrag · FlyRunOut，
        // 乘上恢复系数后回弹 FlyBounce · FlyRunOut —— 撞一下、往回弹一小段，然后收住。
        float v0 = MathF.Max((dist + FlyRunOut) * FlyDrag, 600f);
        s.Vel = s.Dir * v0;

        Audio.Play(Sfx.Choo);   // 窗口真出来的这一声，和画面同一帧
    }

    /// <summary>包络查表（线性插值；越界取两端）。</summary>
    static float EnvelopeAt(float[] env, float t)
    {
        if (env.Length == 0) return 0f;
        if (t <= 0f) return env[0];
        float x = t * EnvRate;
        int i = (int)x;
        if (i >= env.Length - 1) return env[^1];
        float f = x - i;
        return env[i] * (1f - f) + env[i + 1] * f;
    }

    /// <summary>包络采样率，必须和 <see cref="AudioManager.SfxEnvelope"/> 的默认值一致。</summary>
    const int EnvRate = 100;

    void UpdateFly(SpawnAnim s, float dt)
    {
        s.T += dt;
        s.LastDt = dt;

        s.Pos += s.Vel * dt;
        s.Vel *= MathF.Exp(-FlyDrag * dt);   // 空气阻尼：速度指数衰减，位移自然收尾

        var size = new Size(GameWindow.ViewTilesX * GameWindow.TileSize, GameWindow.ViewTilesY * GameWindow.TileSize);
        var work = Screen.FromPoint(new Point(
            (int)(s.Pos.X + size.Width / 2f), (int)(s.Pos.Y + size.Height / 2f))).WorkingArea;
        float maxX = MathF.Max(work.Left, work.Right - size.Width);
        float maxY = MathF.Max(work.Top, work.Bottom - size.Height);

        // 撞屏幕边：位置钉在边界上，速度换成"正好停在整格落点上"的那个值（见 RetargetBounce）。
        // 飞行严格沿一个轴，所以只看飞行轴就够了。
        bool hit = false;
        if (s.Dir.X > 0 && s.Pos.X >= maxX) { s.Pos.X = maxX; hit = true; }
        else if (s.Dir.X < 0 && s.Pos.X <= work.Left) { s.Pos.X = work.Left; hit = true; }
        if (s.Dir.Y > 0 && s.Pos.Y >= maxY) { s.Pos.Y = maxY; hit = true; }
        else if (s.Dir.Y < 0 && s.Pos.Y <= work.Top) { s.Pos.Y = work.Top; hit = true; }

        if (hit && !s.Retargeted) RetargetBounce(s);

        s.Flying.SetClientOrigin(new Point((int)MathF.Round(s.Pos.X), (int)MathF.Round(s.Pos.Y)));

        if (s.Vel.Length() < FlyStopSpeed || s.T > FlyMaxTime) BeginSettle(s);
    }

    /// <summary>
    /// 撞上屏幕边之后，把回弹初速**反算**成"正好停在最近的整格点上"。
    ///
    /// 原来按恢复系数自由回弹，落点是个任意像素值，收尾时还得再吸一次整格 ——
    /// 那一下十几像素的位移就是"停住之后又被拽了一下"。
    /// 现在先定落点（沿飞行轴退一个名义回弹距离再吸整格），再定初速
    /// （衰减到停速时正好走完这段距离），于是收尾的吸附距离≈0。
    /// </summary>
    void RetargetBounce(SpawnAnim s)
    {
        s.Retargeted = true;

        Audio.Play(Sfx.Bump);   // "咚" —— 撞上屏幕边的那一下

        bool horizontal = s.Dir.X != 0;
        float posAxis = horizontal ? s.Pos.X : s.Pos.Y;

        // 撞的是正向边就往回退，垂直方向本来就在整格上，不用动
        float back = s.Dir.X > 0 || s.Dir.Y > 0 ? -1f : 1f;
        float restAxis = GameWindow.GridRound((int)MathF.Round(posAxis + back * NominalBounceRun));

        // 离散积分下，速度从 v 衰减到 FlyStopSpeed 期间走过的距离是
        //   dt · (v - FlyStopSpeed) / (1 - r)，  r = exp(-k·dt)
        // 反解出 v：这样"最后一帧速度掉到停速"的那一刻，位置正好压在 restAxis 上。
        // 撞边时的反算要用**当前这一帧的真实步长**：动画现在按真实帧时长推进，
        // 步长不固定了，写死 FixedDt 会让落点算偏
        float dt = MathF.Max(s.LastDt, 1f / 1000f);
        float r = MathF.Exp(-FlyDrag * dt);
        float run = MathF.Abs(restAxis - posAxis);
        float v = run * (1f - r) / dt + FlyStopSpeed;

        s.Vel = horizontal ? new Vector2(back * v, 0f) : new Vector2(0f, back * v);
        s.SettleTo = horizontal
            ? new Vector2(restAxis, s.Pos.Y)
            : new Vector2(s.Pos.X, restAxis);
    }

    void BeginSettle(SpawnAnim s)
    {
        s.Phase = SpawnPhase.Settle;
        s.T = 0f;
        s.SettleFrom = s.Pos;

        // 撞过边的话落点已经在 RetargetBounce 里定成整格了，这里不用再动；
        // 只有"一直没撞到边"的兜底路径才就地吸整格。
        if (s.Retargeted) return;

        var snapped = GameWindow.ClampToScreen(new Point(
            GameWindow.GridRound((int)MathF.Round(s.Pos.X)),
            GameWindow.GridRound((int)MathF.Round(s.Pos.Y))));
        s.SettleTo = new Vector2(snapped.X, snapped.Y);
    }

    void UpdateSettle(SpawnAnim s, float dt)
    {
        s.T += dt;
        float k = Math.Clamp(s.T / SettleTime, 0f, 1f);
        float e = 1f - MathF.Pow(1f - k, 3f);   // ease-out：一上来就走，最后轻轻贴住
        var p = Vector2.Lerp(s.SettleFrom, s.SettleTo, e);

        s.Flying.SetClientOrigin(new Point((int)MathF.Round(p.X), (int)MathF.Round(p.Y)));
        if (k >= 1f) FinishSpawn(s);
    }

    void FinishSpawn(SpawnAnim s)
    {
        var w = s.Flying;
        w.SetClientOrigin(new Point((int)MathF.Round(s.SettleTo.X), (int)MathF.Round(s.SettleTo.Y)));
        w.Spawning = false;
        _spawn = null;

        // 现在才把这块并进相邻关系/窗口组 —— 飞行途中一路都不提交，
        // 所以撞过谁、擦过谁都不会被误判成"连上了"
        RefreshLayout();

        // 演出期间被按下的那些 N，攒到这一刻接着演下一个
        if (_spawnQueued)
        {
            _spawnQueued = false;
            SpawnWindow();
        }
    }

    // ── 到上限时的"憋喷嚏" ────────────────────────────────────────────────

    /// <summary>窗口数到顶时，母体组想吐但吐不出来。</summary>
    sealed class StifleAnim
    {
        public float T;

        /// <summary>母体组的每一块，以及它抖动前的位置。</summary>
        public readonly List<(GameWindow Window, Point Origin)> Group = new();

        /// <summary>本来要吐的方向：抖得越狠越往这边顶。</summary>
        public Vector2 Dir;

        /// <summary>素材只起播一次。</summary>
        public bool Sounded;

        /// <summary>连续"啊"的音量包络：有几声"啊"，窗口就抖几下。</summary>
        public float[]? Env;

        /// <summary>素材时长；0 表示读不出来，退回固定节奏。</summary>
        public float AudioLength;

        /// <summary>整段演出时长（素材 + 泄气尾巴）。</summary>
        public float Total;
    }

    StifleAnim? _stifle;

    // 下面这组只在"读不到素材"时兜底用
    const float StifleBuild = 0.60f;
    const float StifleHold = 0.14f;
    const float StifleFall = 0.32f;

    const float StifleAmplitude = 10f;  // 抖到最狠时的像素数
    const float StifleStrain = 9f;      // 朝出口顶出去的最大像素数
    const float StifleDeflate = 0.30f;  // 声音停了之后泄气的时长

    /// <summary>窗口正在演动画（新生 / 憋喷嚏）。这期间不拖动、不提交相邻关系。</summary>
    public bool WindowAnimBusy => _spawn != null || _stifle != null;

    /// <summary>
    /// 窗口数已经到顶时按 N（或菜单、双击程序）：让本来该吐的那个组**想吐但吐不出来**。
    ///
    /// 抖动完全跟着音效走：素材里有几声"啊"，整组就一下一下往上顶几次，
    /// 声音停了才泄气松下来。玩家看到的不是"按键没反应"，是"它想吐，吐不出来"。
    ///
    /// 全程只挪位置，不建窗口。
    /// </summary>
    void BeginStifle()
    {
        SyncGroups();   // 母体和方向都按这一帧的真实布局挑，和真吐的时候同一套规则
        if (!PickSpawnSite(out var mother, out _, out var dir)) return;

        var anim = new StifleAnim { Dir = dir };

        float len = Audio.SfxLength(Sfx.CantOpen);
        var env = Audio.SfxEnvelope(Sfx.CantOpen);
        if (len > 0.05f && env.Length > 0)
        {
            anim.Env = env;
            anim.AudioLength = len;
            anim.Total = len + StifleDeflate;
        }
        else
        {
            anim.Total = StifleBuild + StifleHold + StifleFall;
        }

        foreach (var w in mother)
            if (!w.IsDisposed) anim.Group.Add((w, w.ClientScreen.Location));

        _stifle = anim;
    }

    void UpdateStifle(float dt)
    {
        var s = _stifle;
        if (s == null) return;

        s.T += dt;

        if (!s.Sounded)
        {
            s.Sounded = true;
            Audio.Play(Sfx.CantOpen);   // 和抖动同一帧起
        }

        float amp;
        float toward = 0f;   // 朝吐出方向"顶"出去多少像素
        if (s.Env != null)
        {
            float e = EnvelopeAt(s.Env, s.T);
            if (s.T > s.AudioLength)
            {
                // 声音停了：整组泄气松回原位
                float c = Math.Clamp((s.T - s.AudioLength) / StifleDeflate, 0f, 1f);
                e *= (1f - c) * (1f - c);
            }
            amp = StifleAmplitude * e;
            toward = StifleStrain * e;
        }
        else
        {
            // 兜底：读不到素材时用固定节奏（爬升 → 僵住 → 泄气）
            float hold = StifleBuild + StifleHold;
            if (s.T < StifleBuild)
            {
                float k = s.T / StifleBuild;
                float wave = 0.62f + 0.38f * MathF.Sin(s.T * 2f * MathF.PI * 2.6f - MathF.PI / 2f);
                amp = StifleAmplitude * k * k * wave;
            }
            else if (s.T < hold)
            {
                amp = StifleAmplitude;
                toward = StifleStrain;
            }
            else
            {
                float c = Math.Clamp((s.T - hold) / StifleFall, 0f, 1f);
                amp = StifleAmplitude * (1f - c) * (1f - c);
                toward = StifleStrain * (1f - c);
            }
        }

        float phase = s.T * ShakeHz * MathF.Tau;
        var off = new Point(
            (int)MathF.Round(amp * MathF.Sin(phase) + s.Dir.X * toward),
            (int)MathF.Round(amp * 0.72f * MathF.Cos(phase) + s.Dir.Y * toward));

        foreach (var (w, origin) in s.Group)
            if (!w.IsDisposed) w.SetClientOrigin(new Point(origin.X + off.X, origin.Y + off.Y));

        if (s.T < s.Total) return;

        // 收尾必须分毫不差地放回原处：差几像素，拼缝的两块地图就错开了
        foreach (var (w, origin) in s.Group)
            if (!w.IsDisposed) w.SetClientOrigin(origin);

        _stifle = null;
        RefreshLayout();
    }

    /// <summary>
    /// 窗口动画（抖动 / 飞行 / 收尾）单独按**真实帧时长**推进，不跟着 60Hz 的物理步走。
    ///
    /// 这些动画只改变窗口位置、不改变窗口内容，所以可以跑得比物理快得多：
    /// 主循环每转一圈调一次（配合 <see cref="Program"/> 里动画期间缩短的等待时间），
    /// 而重绘仍然只在物理步推进时做。位移本身只是一次 SetWindowPos，很便宜。
    /// </summary>
    public void UpdateWindowAnimations(float dt)
    {
        if (dt <= 0f) return;
        UpdateSpawn(dt);
        UpdateStifle(dt);

        // 窗口组的光效跟着实际位置走，否则高刷位移时光晕会滞后半帧
        if (WindowAnimBusy) SyncGroups();
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

            // 对方那张地图在这一格是墙（或者站着箱子）就过不去，否则角色会被塞进墙里卡死
            float mx = target.CamX + tu.X;
            float my = target.CamY + tu.Y;
            int cx = (int)MathF.Floor(mx), cy = (int)MathF.Floor(my);
            if (target.Piece.SolidAt(cx, cy) && !IsHidden(target.Piece, cx, cy)) continue;
            if (CrateAtCell(target.Piece, cx, cy) != null) continue;

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
        // 窗口动画还在演（新生抖动/飞行、憋喷嚏）：这时候每帧都提交相邻关系，
        // 飞到谁身上就当场"连上"了，光效和玩法都会炸。演出结束才提交一次。
        if (WindowAnimBusy) return;

        foreach (var w in Windows)
            w.NeighborLeft = w.NeighborRight = w.NeighborTop = w.NeighborBottom = null;

        SyncGroups();

        for (int i = 0; i < Windows.Count; i++)
        {
            for (int j = i + 1; j < Windows.Count; j++)
            {
                var a = Windows[i];
                var b = Windows[j];
                if (a.Spawning || b.Spawning) continue;   // 飞行中的窗口不参与相邻判定
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
            if (w.IsDisposed || w.Spawning) continue;   // 飞行中的窗口不算进任何一组
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
            if (seed.IsDisposed || seed.Spawning || !seen.Add(seed)) continue;

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
                    if (other.IsDisposed || other.Spawning || seen.Contains(other)) continue;
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
            TrackHotkeys();
            Audio.SetWalking(false);
            Audio.Update(dt);
            return;
        }

        if (Paused)
        {
            // 拖动窗口期间整局挂起：物理、相机复位、窗口连接全部延后到松手
            TrackHotkeys();
            Audio.SetWalking(false);   // 挂起时脚步淡出，BGM 照常
            Audio.Update(dt);
            return;
        }

        // 窗口动画（抖动/飞行）不在这里推进 —— 它们由 UpdateWindowAnimations 按
        // 真实帧时长独立驱动，可以跑得比 60Hz 的物理步更密。这里只负责提交布局。
        RefreshLayout();

        UpdateInput(dt);
        MoveAndCollide(dt);
        UpdateCrates(dt);   // 推箱蓄力 / 箱子补间 / 下落，都在角色位置定下来之后
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

    /// <summary>
    /// 物理和输入挂起时（过场、拖动窗口）也要跟住这几个键的按下状态：
    /// 不刷新的话，挂起期间按过的键会在恢复的第一帧被当成"刚按下"，白执行一次。
    /// </summary>
    void TrackHotkeys()
    {
        _spaceDown = Key(Native.VK_SPACE);
        _rDown = Key(Native.VK_R);
        _nDown = Key(Native.VK_N);
        _mDown = Key(Native.VK_M);
    }

    void UpdateInput(float dt)
    {
        bool active = OurProcessForeground();

        float axis = 0;
        float climb = 0;
        bool spaceNow = false;
        bool rNow = false;
        bool nNow = false;
        bool mNow = false;
        if (active)
        {
            if (Key(Native.VK_A)) axis -= 1;
            if (Key(Native.VK_D)) axis += 1;
            if (Key(Native.VK_W) || Key(Native.VK_UP)) climb -= 1;
            if (Key(Native.VK_S) || Key(Native.VK_DOWN)) climb += 1;
            spaceNow = Key(Native.VK_SPACE);
            rNow = Key(Native.VK_R);
            nNow = Key(Native.VK_N);
            mNow = Key(Native.VK_M);
            if (Key(Native.VK_ESCAPE)) Quit();
        }

        // R 重开整关：按住只在按下沿触发一次（以前这里是拿 _spaceDown 做边沿，
        // 跳跃键的状态一变 R 就会跟着误触）
        if (rNow && !_rDown) RestartLevel();
        _rDown = rNow;

        // N 新开窗口：和右键菜单那条是同一件事，同样丢给主循环延后执行
        // （建窗会走 Form.Show，不在更新中途动窗口列表更省心）。
        // 上限由 SpawnWindow / CreateWindowAt 按关卡的最大窗口数挡掉。
        if (nNow && !_nDown) Enqueue(SpawnWindow);
        _nDown = nNow;

        // M 静音：按下沿触发，和右键菜单共用同一个开关
        if (mNow && !_mDown) Audio.ToggleMute();
        _mDown = mNow;

        if (spaceNow && !_spaceDown) Player.Buffer = Player.JumpBuffer;
        _spaceDown = spaceNow;


        Player.ClimbAxis = climb;
        Player.Buffer = MathF.Max(0, Player.Buffer - dt);
        Player.Vel.X = axis * Player.Speed;
        _moveAxis = axis;   // 推箱子看的是"手按着哪个方向"，不是速度（顶住箱子时速度会被清 0）

        // 推箱补间期间水平位移交给箱子（StepSlide 里同速带着人走）。
        // 这里必须把速度清掉：玩家 10 格/秒比箱子的推速快得多，
        // 留着输入他会一头扎进箱子已经"占住"的目标格，看起来就是人被落下、箱子自己飞走。
        if (CarryingCrate) Player.Vel.X = 0f;

        if (axis != 0) Player.FacingRight = axis > 0;
        // 被箱子带着走的时候人确实在位移，脚步循环就照常在响（别一会儿停一会儿响，听着更碎）
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

    // ── 碰撞：一律按「拼接之后的整体」判定 ────────────────────────────────
    //
    // 口径只有一条：角色身上每一格，先问"这一格在拼接后的整体里由哪块窗口盖着"，
    // 再查那块窗口自己的地图。整组窗口都没盖到的地方 —— 也就是拼接之后的最外边界之外 ——
    // 才算看不见的墙。
    //
    // 以前的写法是两段并行：先拿 Player.Piece 查整个 AABB，再拿 AABB 的四个角去问邻窗。
    // 前者在跨缝时会把越界的那半格按环形**绕回自己地图的另一头**（一块 15x10 的地图，
    // x=15 就是 x=0），于是"窗口外那圈看不见的区域"照样在挡人：明明接缝对面是空地，
    // 却因为自己地图另一头是墙而被挡住；反过来对面是墙、自己另一头是空地时又会穿墙进去。

    /// <summary>
    /// 把地图坐标上的一个点，解析成「拼接后的整体」里的那一格：属于哪张地图、地图上的哪一格。
    /// 这是整套碰撞判定的唯一口径。
    /// </summary>
    /// <returns>false = 这个点落在所有窗口之外（拼接后的外边界之外）。</returns>
    bool TryResolvePoint(GameWindow owner, float mapX, float mapY, out Piece piece, out int mx, out int my)
    {
        float vx = mapX - owner.CamX, vy = mapY - owner.CamY;

        // 1) 自己这块窗口盖着：直接就是自己地图上的那一格
        if (InView(new Vector2(vx, vy)))
        {
            piece = owner.Piece;
            mx = (int)MathF.Floor(mapX);
            my = (int)MathF.Floor(mapY);
            return true;
        }

        // 2) 同组里别的窗口盖着：换算到对方的画面坐标，再取对方地图上的那一格。
        //
        // 认"整组"而不是"上下左右那四块"：四块窗口交汇的那个点上，角色的 AABB
        // 会顺带压到斜对角那块窗口的地盘，只认直接相邻的话那一格会被当成墙 ——
        // 又在交汇处凭空多出一小块看不见的阻挡。
        // 组内成员按定义互不重叠（重叠的窗口不算贴合，不会同组），所以不会有歧义。
        var group = owner.Group?.Windows;
        if (group != null)
        {
            for (int i = 0; i < group.Count; i++)
            {
                var other = group[i];
                if (other == owner || other.IsDisposed) continue;
                if (TryMapTo(other, owner, vx, vy, out piece, out mx, out my)) return true;
            }
        }
        else
        {
            // 组还没建出来时的兜底（窗口组在 Step 开头才重建）：退回直接相邻的那几块
            foreach (var other in owner.Neighbors)
            {
                if (other.IsDisposed) continue;
                if (TryMapTo(other, owner, vx, vy, out piece, out mx, out my)) return true;
            }
        }

        piece = null!;
        mx = my = 0;
        return false;
    }

    /// <summary>把 owner 画面上的点换算到 other 的画面里；落进对方框内就换成对方地图上的一格。</summary>
    static bool TryMapTo(GameWindow other, GameWindow owner, float vx, float vy,
        out Piece piece, out int mx, out int my)
    {
        var off = ViewOffset(owner, other);
        float tx = vx - off.X, ty = vy - off.Y;

        if (!InView(new Vector2(tx, ty)))
        {
            piece = null!;
            mx = my = 0;
            return false;
        }

        piece = other.Piece;
        mx = (int)MathF.Floor(other.CamX + tx);
        my = (int)MathF.Floor(other.CamY + ty);
        return true;
    }

    /// <summary>
    /// 拼接后的整体里，这一**格地形**挡不挡人。
    ///
    /// 箱子不在这里判：它的碰撞按**实际矩形**另算（见 <see cref="CrateHitsAabb"/>），
    /// 因为下落中的箱子视觉位置会在格子里上下滑，跟它所属的格子能差半格。
    ///
    /// 取景框外的处理分两种，这条很关键：
    ///   · 跟别的窗口拼在一起 → 那就是拼接后的外边界，算墙；
    ///   · 单独一块自由滚动的窗口 → 根本没有"外面"，那只是镜头没跟上（快速下落时相机会滞后，
    ///     角色脚下那一格经常暂时落到框外）。地图是首尾相接的，按自己这张地图绕回去接着算。
    ///     以前这里一律当墙，结果快速下落时会凭空冒出看不见的地板/墙，箱子也因此判不到。
    /// </summary>
    bool CellBlocks(GameWindow owner, int mx, int my)
    {
        // 拿格中心去问：中心落在哪块窗口里，就查那块窗口地图上的那一格
        if (!TryResolvePoint(owner, mx + 0.5f, my + 0.5f, out var piece, out int px, out int py))
        {
            if (owner.HasNeighbors) return true;
            piece = owner.Piece;
            px = mx;
            py = my;
        }

        if (IsHidden(piece, px, py)) return false;   // "隐藏图块并且不再阻挡"
        return piece.SolidAt(px, py);
    }

    /// <summary>
    /// 拼接后的整体里，这一格是不是单向平台。藏起来的格子不算（和实心同一条口径）；
    /// 取景框外的处理同 <see cref="CellBlocks"/>（单块自由滚动窗口按环形绕回，拼接的才当边界）。
    /// </summary>
    bool PlatformInSplice(GameWindow owner, int mx, int my)
    {
        if (!TryResolvePoint(owner, mx + 0.5f, my + 0.5f, out var piece, out int px, out int py))
        {
            if (owner.HasNeighbors) return false;
            piece = owner.Piece;
            px = mx;
            py = my;
        }

        return !IsHidden(piece, px, py) && piece.PlatformAt(px, py);
    }

    /// <summary>
    /// 角色所在（或脚下 <paramref name="dy"/> 格处）那一格的瓦片定义。
    /// 梯子、弹簧这类"这一格是什么地形"的判定都走这里 —— 和碰撞同一个口径，
    /// 接缝上不会再按环形绕回自己地图的另一头。
    /// </summary>
    TileDef? CellDefAtPlayer(float dy = 0f)
    {
        var w = WindowFor(Player.Piece);
        if (w == null) return null;
        if (!TryResolvePoint(w, Player.Pos.X, Player.Pos.Y + dy, out var piece, out int mx, out int my))
            return null;
        return TileSet.Defs[piece.At(mx, my)];
    }

    /// <summary>
    /// 只有**地形**挡不挡这个位置：实心格子 + 被事件藏起来的格子不算。
    /// 箱子不在这里判 —— 它按实际矩形另算（<see cref="CrateHitsAabb"/>）。
    ///
    /// 拆成两个函数是为了贴齐箱子时用得上："人贴到箱面上"这件事本身会让 AABB 和箱子边界相接，
    /// 拿合并口径（<see cref="Collides"/>）去验证就会一直报"撞着"，于是永远贴不上去。
    /// </summary>
    bool TerrainCollides(Vector2 pos)
    {
        var w = WindowFor(Player.Piece);
        if (w == null) return true;   // 这张地图没有窗口显示：整块都不算存在

        float l = pos.X - Player.Width / 2f, r = pos.X + Player.Width / 2f;
        float t = pos.Y - Player.Height / 2f, b = pos.Y + Player.Height / 2f;

        int x0 = (int)MathF.Floor(l), x1 = (int)MathF.Floor(r - 0.0001f);
        int y0 = (int)MathF.Floor(t), y1 = (int)MathF.Floor(b - 0.0001f);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (CellBlocks(w, x, y)) return true;

        return false;
    }

    /// <summary>这个位置能不能站人：地形 + 箱子。</summary>
    bool Collides(Vector2 pos) => TerrainCollides(pos) || CrateHitsAabb(pos);

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
    /// 平台同样按拼接后的整体查（<see cref="PlatformInSplice"/>）：接缝对面的平台照样接得住人。
    /// </summary>
    bool CollidesFalling(Vector2 pos, float feetBefore)
    {
        if (Collides(pos)) return true;

        var w = WindowFor(Player.Piece);
        if (w == null) return true;

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
                if (PlatformInSplice(w, x, y)) return true;
        }

        return false;
    }

    /// <summary>
    /// 这个地图坐标点是不是还落在"走得到"的那片区域里（自己 + 直接相邻的窗口）。
    ///
    /// 只用于拖完窗口之后判断"角色有没有被丢在走不到的地方"（见 <see cref="EnsurePlayerOnScreen"/>）——
    /// 碰撞本身走 <see cref="TryResolvePoint"/>，那边的口径是整组窗口。
    /// 这里故意只认直接相邻：真被丢到两块窗口以外，就该拉回框内，而不是让角色飘在别的房间里。
    /// </summary>
    bool InsideSomeWindow(GameWindow owner, float mapX, float mapY)
    {
        var u = ViewLocal(owner, new Vector2(mapX, mapY));
        if (InView(u)) return true;   // 还在自己的取景框里

        foreach (var w in owner.Neighbors)
        {
            if (w.IsDisposed) continue;

            var tu = u - ViewOffset(owner, w);
            if (!InView(tu)) continue;

            // 跨到隔壁时落到对方那张地图上：那一格是墙（或者站着箱子）就走不过去，
            // 否则角色会被塞进墙里，之后所有方向都被挡住，看着就像卡死了。
            float mx = w.CamX + tu.X, my = w.CamY + tu.Y;
            int cx = (int)MathF.Floor(mx), cy = (int)MathF.Floor(my);
            bool wall = w.Piece.SolidAt(cx, cy) && !IsHidden(w.Piece, cx, cy);
            return !wall && CrateAtCell(w.Piece, cx, cy) == null;
        }

        return false;
    }

    void MoveAndCollide(float dt)
    {
        Player.LadderLock = MathF.Max(0, Player.LadderLock - dt);

        // 抓住梯子：不落、上下走；在梯子上也能起跳（跳开后短暂不再被抓回去）
        bool onLadder = Player.LadderLock <= 0 && CellDefAtPlayer()?.Climb == true;

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
        else if (_rideCrate is { } ride && CanRide(ride))
        {
            // 踩在箱子上：箱子就是"活动地面" —— 不施加重力，竖直位置由 UpdateCrates 贴到箱顶。
            // 起跳时 UpdateInput 已经把 Vel.Y 设成负的，CanRide 会返回 false，照常走跳跃。
            GlueToRide(ride);
        }
        else
        {
            _rideCrate = null;
            float g = Player.Vel.Y > 0 ? Player.Gravity(dt) * Player.FallGravityScale : Player.Gravity(dt);
            Player.Vel.Y += g * dt;
            if (Player.Vel.Y > Player.MaxFallSpeed) Player.Vel.Y = Player.MaxFallSpeed;
        }

        // horizontal：走不动时如果是箱子挡的，先贴齐箱面再掐掉速度 ——
        // 推箱判定靠的是"身体边缘离箱面多远"，贴齐之后就不会出现"差 0.1 格所以够不着"。
        float remain = Player.Vel.X * dt;
        while (MathF.Abs(remain) > 1e-5f)
        {
            float step = Math.Clamp(remain, -0.2f, 0.2f);
            var next = Player.Pos + new Vector2(step, 0);
            if (!Collides(next)) { Player.Pos = next; remain -= step; }
            else
            {
                SnapFlushToCrate(next, step > 0 ? 1 : -1);
                Player.Vel.X = 0;
                break;
            }
        }

        // vertical：踩着箱子时整段跳过（位置由 UpdateCrates 贴到箱顶）
        if (_rideCrate == null)
        {
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

                        // 踩在箱子上：箱子的顶面是连续坐标（下落时在格子里滑），直接贴到它顶面，
                        // 并把这箱子记成"正踩着的那只"，之后每帧由 UpdateCrates 贴住。
                        if (TryCrateTopUnder(next, feet, out var standingCrate, out float crateTop))
                        {
                            Player.Pos = new Vector2(Player.Pos.X, crateTop - Player.Height / 2f);
                            Player.Vel.Y = 0;
                            _rideCrate = standingCrate;
                            break;
                        }

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
                        if (CellDefAtPlayer(Player.Height / 2f + 0.1f)?.Spring == true)
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
    /// 为什么必须有：<see cref="Collides"/> 把"取景框之外"当成实心
    /// （相连时以整组窗口拼出来的外边界为准，单块时就是它自己的框边），
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

    /// <summary>本关已经触发过的**触碰**事件下标。每次读关卡 / 按 R 重置。</summary>
    readonly HashSet<int> _firedEvents = new();

    /// <summary>
    /// 当前处于"激活状态"的事件：触碰事件触发后就一直激活，压力板事件跟着板子的按压状态来。
    /// 「隐藏绑定格」这条操作看的就是这里 —— 一格上挂着多条事件时，只要有一条激活，格子就藏。
    /// </summary>
    readonly HashSet<int> _activeEvents = new();

    /// <summary>上一帧还按着的压力板事件。用来抓"刚踩下去"那一帧跑一次 goto / run。</summary>
    readonly HashSet<int> _plateDown = new();

    /// <summary>
    /// 挂着"隐藏本格"操作的格子（关卡读进来时预扫一遍）。
    /// 每帧按激活状态重算 <see cref="_hidden"/>，别在绘制 / 碰撞时临时判。
    /// </summary>
    readonly List<(Piece Piece, int X, int Y, List<int> Seq)> _hideCells = new();

    /// <summary>压力板事件 → 它盯的那块板（地图块 + 格）。板子不存在的事件不会进来。</summary>
    readonly List<(int Event, Piece Piece)> _plateRefs = new();

    /// <summary>
    /// 已经被藏起来的格子。按地图块分组，key = y * Width + x。
    /// 只有 <see cref="_hideCells"/> 里的格子会进来，通常是个位数。
    /// </summary>
    readonly Dictionary<Piece, HashSet<int>> _hidden = new();

    /// <summary>关卡读完 / 换关时重建预表：哪些格子会因为事件被藏、哪些事件盯哪块板。</summary>
    void BuildEventRuntime()
    {
        _hideCells.Clear();
        _plateRefs.Clear();
        _hidden.Clear();
        if (Level == null) return;

        foreach (var p in Level.Pieces)
        {
            if (p.Evt == null) continue;
            for (int y = 0; y < p.Height; y++)
                for (int x = 0; x < p.Width; x++)
                {
                    int si = p.EvtSeqAt(x, y);
                    if (si < 0 || si >= Level.EvtSeqs.Count) continue;

                    var seq = Level.EvtSeqs[si];
                    bool hides = false;
                    foreach (int idx in seq)
                        if (Has(index: idx) && Level.Events[idx].Hides) { hides = true; break; }

                    if (hides) _hideCells.Add((p, x, y, seq));
                }
        }

        for (int i = 0; i < Level.Events.Count; i++)
        {
            var ev = Level.Events[i];
            if (ev.Trigger != EventTrigger.Plate) continue;
            if (ev.PlateX < 0 || ev.PlateY < 0) continue;   // 没填坐标 = 这块板不存在

            Piece? piece = string.IsNullOrEmpty(ev.PlatePiece) ? BoundPieceOf(i) : PieceById(ev.PlatePiece);
            if (piece != null) _plateRefs.Add((i, piece));
        }

        bool Has(int index) => index >= 0 && index < Level.Events.Count;
    }

    Piece? PieceById(string id)
    {
        foreach (var p in Level.Pieces)
            if (p.Id == id) return p;
        return null;
    }

    /// <summary>这条事件挂在哪些格子上，就取第一块地图（压力板没填地图块时用它兜底）。</summary>
    Piece? BoundPieceOf(int eventIndex)
    {
        foreach (var p in Level.Pieces)
        {
            if (p.Evt == null) continue;
            for (int y = 0; y < p.Height; y++)
                for (int x = 0; x < p.Width; x++)
                {
                    int si = p.EvtSeqAt(x, y);
                    if (si >= 0 && si < Level.EvtSeqs.Count && Level.EvtSeqs[si].Contains(eventIndex))
                        return p;
                }
        }
        return null;
    }

    /// <summary>重开 / 换关时把事件的运行状态清空（预表本身不动）。</summary>
    void ResetEventRuntime()
    {
        _activeEvents.Clear();
        _plateDown.Clear();
        foreach (var set in _hidden.Values) set.Clear();
    }

    /// <summary>这一格是不是被事件藏起来了：藏起来就不画、也不挡人。</summary>
    public bool IsHidden(Piece piece, int x, int y)
    {
        if (_hidden.Count == 0) return false;
        if (!_hidden.TryGetValue(piece, out var set) || set.Count == 0) return false;

        int w = piece.Width, h = piece.Height;
        if (w <= 0 || h <= 0) return false;
        int wx = ((x % w) + w) % w;
        int wy = ((y % h) + h) % h;
        return set.Contains(wy * w + wx);
    }

    /// <summary>
    /// 这一格的压力板是不是被压着。玩家和箱子都算，而且**身体所在格和脚底那一格**都认：
    /// 板子画在地板那一格（脚下）就按脚底算，画在站立的空格里就按身体算，
    /// 两种摆法都能用，做关卡时不用去猜引擎认哪一格。
    /// </summary>
    public bool PlatePressed(Piece piece, int x, int y)
    {
        int w = piece.Width, h = piece.Height;
        if (w <= 0 || h <= 0) return false;
        int wx = ((x % w) + w) % w;
        int wy = ((y % h) + h) % h;
        if (!piece.PlateAt(wx, wy)) return false;

        if (Player.Piece == piece)
        {
            // 身体所在格（中心）和脚底那一格都算；左右边缘各探一次，
            // 因为角色 0.62 格宽，站在两格之间时会同时压着两列。
            int by = (int)MathF.Floor(Player.Pos.Y);
            int fy = (int)MathF.Floor(Player.Pos.Y + Player.Height / 2f + 0.02f);
            if (by == wy || fy == wy)
            {
                int lx = (int)MathF.Floor(Player.Pos.X - Player.Width / 2f + 0.02f);
                int rx = (int)MathF.Floor(Player.Pos.X + Player.Width / 2f - 0.02f);
                int cx = (int)MathF.Floor(Player.Pos.X);
                if (wx == cx || wx == lx || wx == rx) return true;
            }
        }

        foreach (var c in Level.Crates)
        {
            if (c.Piece != piece) continue;
            int cx = ((c.CellX % w) + w) % w;
            int cy = ((c.CellY % h) + h) % h;
            int fy = ((c.FeetY % h) + h) % h;
            if (cx == wx && (cy == wy || fy == wy)) return true;
        }

        return false;
    }

    /// <summary>
    /// 每帧检查事件：先跑触碰，再看压力板的按压状态，最后重算"哪些格子被藏"。
    ///
    /// 触发口径：角色身体中心落在哪一格就算"碰到"那一格（和以前拿宝石一致）。
    /// 一格可以挂多条事件，**按顺序逐条判定**：条件满足就执行，不满足就跳过接着判下一条。
    /// 前置条件不满足的事件不会被标记成已触发 —— 玩家在格子里站着不动、
    /// 把摄像机滚到指定范围，事件照样会亮，这正是解谜要的手感。
    /// 同一条触碰事件一次进入关卡最多触发一次。
    ///
    /// 关键：某条事件把地图换掉了（跳关），这一格剩下的序列立刻全部取消 ——
    /// 地图、角色、格子都已经不是刚才那个了，继续拿旧状态往下判定只会出鬼。
    /// </summary>
    void UpdateEvents()
    {
        if (Level.Events.Count == 0)
        {
            if (_activeEvents.Count > 0 || _hidden.Count > 0) RebuildHidden();
            return;
        }

        UpdateTouchEvents();
        UpdatePlateEvents();
        RebuildHidden();
    }

    void UpdateTouchEvents()
    {
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
            _activeEvents.Add(idx);
            Fire(ev);

            // 地图切换已经开始：剩下的取消，等新关卡把一切重置干净
            if (Fading) return;
        }
    }

    /// <summary>
    /// 压力板事件：板子按着就算激活（藏起来的图块随之消失），松开就撤掉。
    /// goto / run 这类一次性动作只在"刚踩下去"那一帧跑，不会每帧重放。
    /// </summary>
    void UpdatePlateEvents()
    {
        for (int i = 0; i < _plateRefs.Count; i++)
        {
            var (index, piece) = _plateRefs[i];
            if (index < 0 || index >= Level.Events.Count) continue;

            var ev = Level.Events[index];
            bool down = PlatePressed(piece, ev.PlateX, ev.PlateY) && ConditionsMet(ev);

            bool was = _plateDown.Contains(index);
            if (down == was) continue;

            if (down)
            {
                _plateDown.Add(index);
                _activeEvents.Add(index);
                foreach (var act in ev.OneShots)
                {
                    if (Fading) return;
                    Execute(act);
                }
            }
            else
            {
                _plateDown.Remove(index);
                _activeEvents.Remove(index);
            }
        }
    }

    /// <summary>
    /// 重算隐藏格：一格上可能挂着好几条"隐藏本格"的事件，**只要有一条激活就藏**。
    /// 触碰事件触发过就一直算激活，压力板事件跟着板子走，所以松开板子墙会重新出现。
    /// </summary>
    void RebuildHidden()
    {
        foreach (var set in _hidden.Values) set.Clear();

        for (int i = 0; i < _hideCells.Count; i++)
        {
            var (piece, x, y, seq) = _hideCells[i];

            bool hide = false;
            for (int k = 0; k < seq.Count; k++)
            {
                int idx = seq[k];
                if (idx < 0 || idx >= Level.Events.Count) continue;
                if (_activeEvents.Contains(idx) && Level.Events[idx].Hides) { hide = true; break; }
            }
            if (!hide) continue;

            if (!_hidden.TryGetValue(piece, out var set)) _hidden[piece] = set = new HashSet<int>();
            set.Add(y * piece.Width + x);
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

            case "hide":
                // 状态型操作：不做"一次性"的事，格子藏不藏由 RebuildHidden 按激活状态决定。
                // 走到这里说明某条事件被触发了，隐藏会在这一帧末尾自然生效。
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

    // ── 换关卡 / 重开的过场 ──────────────────────────────────────────────

    enum FadePhase { None, Out, In }

    FadePhase _fadePhase = FadePhase.None;

    /// <summary>白场强度 0..1：Out 时 0→1，换关后 In 时 1→0。</summary>
    float _fade;

    string _pendingLevel = "";

    /// <summary>白场盖住的那一刻要做的不是跳关，而是把当前关卡整个重置回初始状态。</summary>
    bool _restartPending;

    const float FadeOutTime = 0.20f;
    const float FadeInTime = 0.34f;

    /// <summary>正在过场（切关卡的闪白）。这期间物理和输入全部挂起。</summary>
    public bool Fading => _fadePhase != FadePhase.None;

    /// <summary>过场白场的当前强度，GameWindow 画遮罩用。</summary>
    public float FadeAlpha => Math.Clamp(_fade, 0f, 1f);

    /// <summary>换关卡时程序在拆/建窗口，窗口的关闭事件不该当成"退出游戏"。</summary>
    bool _swapping;

    public bool Swapping => _swapping;

    /// <summary>程序自己在开窗（换关卡 / 重开），不响"新窗口"音效。</summary>
    bool _suppressSfx;

    void BeginFadeTo(string level)
    {
        if (Fading) return;
        _pendingLevel = level;
        _restartPending = false;
        StartFade();
    }

    /// <summary>
    /// 按 R / 右键菜单「重开本关」：把整个关卡重置回刚进入它时的样子。
    ///
    /// 走的是和跳关完全同一套白场过场（0.2s 闪白 → 重建 → 0.34s 淡出）：
    /// 拆窗建窗那一帧被白场盖着，看不到闪烁，物理和输入在过场期间也自动挂起。
    /// 只重置内存里这一局的状态，**不重新读盘**（口径已跟用户确认）。
    /// </summary>
    public void RestartLevel()
    {
        if (Fading) return;
        if (Windows.Count == 0) return;   // 已经没有窗口了，主循环随时会收摊
        _restartPending = true;
        _pendingLevel = "";
        StartFade();
    }

    void StartFade()
    {
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
            if (_restartPending)
            {
                _restartPending = false;
                RebuildLevel();          // 关卡数据本身不动，只按它从零再摆一遍
            }
            else
            {
                SwitchLevel(_pendingLevel);
            }
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
        RebuildLevel();
    }

    /// <summary>
    /// 按当前 <see cref="Level"/> 从零重建整局：只留一块窗口继续用，多余的拆掉，
    /// 角色放回出生点，触发过的事件、接缝特效、拖拽与相机惯性全部清空。
    /// 重开（<see cref="RestartLevel"/>）和跳关（<see cref="SwitchLevel"/>）都走这里，
    /// 唯一区别是跳关之前先把关卡文件换掉了。
    ///
    /// **必须是"留一块旧窗口继续用"，不能拆光再新建**：新建的窗口在游戏画出第一帧之前，
    /// 会先被系统按 BackColor 抹一遍（深蓝），而那一刻白场正盖满画面——
    /// 于是纯白中间闪出一个深色方块。旧窗口在过场期间一直是纯白，
    /// 换地图、换镜头、清状态全在白色底下完成，看不出任何变化。
    /// 留用的是 1 号窗口（<see cref="Windows"/> 的第一个），它的屏幕位置原样保留。
    /// </summary>
    void RebuildLevel()
    {
        GameWindow? keep = null;
        foreach (var w in Windows)
        {
            if (w.IsDisposed) continue;
            keep = w;   // 1 号窗口
            break;
        }

        _swapping = true;
        try
        {
            foreach (var w in Windows.ToArray())
                if (w != keep && !w.IsDisposed) w.Close();
            Windows.Clear();
        }
        finally { _swapping = false; }

        ClearRoundState();

        if (keep == null)
        {
            // 连一块能留用的窗口都没有（启动那一帧），只能老实新建一个
            _suppressSfx = true;
            try { OpenWindow(); }
            finally { _suppressSfx = false; }
            return;
        }

        Windows.Add(keep);

        // 回到"刚进这一关"的摆法：显示第一块地图、镜头对准出生点。
        // **窗口自己的屏幕位置不动**（用户口径：重开保留 1 号窗口的位置），
        // 所以这里不碰 ClientScreen —— 位置没变化，白场下面连一次移动都不会发生。
        keep.Piece = PieceForSlot(0);
        keep.Dragging = false;

        var spawnPos = new Vector2(keep.Piece.Spawn.X + 0.5f, keep.Piece.Spawn.Y + 0.5f);
        var cam = CameraTarget(spawnPos);
        keep.SetCamera(cam.X, cam.Y);

        PlaceAtSpawn(keep);
        RefreshLayout();
    }

    /// <summary>把这一局的所有运行状态归位（不碰关卡数据和窗口）。</summary>
    void ClearRoundState()
    {
        _spawn = null;   // 新生动画作废：窗口马上要重排，飞的那块已经不算数了
        _spawnQueued = false;
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

        // 箱子回到关卡里写的位置；事件激活状态、隐藏格、推箱蓄力一起清空。
        // 重开是"这一局从零再来"，所以箱子被推到哪儿了不能留着。
        Level.ResetCrates();
        ResetEventRuntime();
        _pushCrate = null;
        _pushCharge = 0f;
        _pushDir = 0;
        _carryCrate = null;
        _carryDir = 0;
        _rideCrate = null;
        _moveAxis = 0f;

        // 角色状态清干净：出生点会在 OpenWindow → PlaceAtSpawn 里重新设定
        Player.Pos = Vector2.Zero;
        Player.Vel = Vector2.Zero;
        Player.Buffer = 0;
        Player.OnGround = false;
        Player.Coyote = 0;
        Player.LadderLock = 0;
        Player.ClimbAxis = 0;
    }

    void Respawn()
    {
        var w = _lastWindow;
        if (w == null || w.IsDisposed) w = Windows.Count > 0 ? Windows[0] : null;
        if (w == null) return;
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
