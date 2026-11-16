using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsLoop;

internal sealed class GameWindow : Form
{
    public const int TileSize = 40;      // screen px per world tile
    public const int ViewTilesX = 15;    // 600 / 40
    public const int ViewTilesY = 10;    // 400 / 40
    public const int LogicalCell = 16;   // 8px source art drawn at 2x
    public const int SnapDistance = 40;

    readonly Game _game;
    readonly Bitmap _frame = new(ViewTilesX * LogicalCell, ViewTilesY * LogicalCell);

    // 原生菜单的命令号
    const int MenuOpenWindow = 1;
    const int MenuToggleMute = 2;
    const int MenuQuit = 3;
    int _clientOffsetX, _clientOffsetY;
    Point _desiredClientOrigin;
    bool _dragActive;
    Point _dragGrab;

    public Piece Piece;

    /// <summary>取景框左上角对应的地图坐标（浮点，用于平滑滚动）。</summary>
    public float CamX, CamY;

    public Point View => new((int)MathF.Floor(CamX), (int)MathF.Floor(CamY));

    public GameWindow? NeighborLeft, NeighborRight, NeighborTop, NeighborBottom;
    public bool HasNeighbors => NeighborLeft != null || NeighborRight != null || NeighborTop != null || NeighborBottom != null;

    /// <summary>直接贴合的邻居。组内不相邻的成员不会出现在这里。</summary>
    public IEnumerable<GameWindow> Neighbors
    {
        get
        {
            if (NeighborLeft != null) yield return NeighborLeft;
            if (NeighborRight != null) yield return NeighborRight;
            if (NeighborTop != null) yield return NeighborTop;
            if (NeighborBottom != null) yield return NeighborBottom;
        }
    }

    /// <summary>当前所属的窗口组（连成一片的整组窗口，含不相邻的间接成员）。</summary>
    public WindowGroup? Group;

    /// <summary>正在被拖动，且预览松手后会落到的客户原点。</summary>
    public bool Dragging;
    public Point PreviewOrigin;

    /// <summary>
    /// 这块窗口正处在"新生动画"里（还没落定）。这期间它不算进任何窗口组、
    /// 不参与相邻关系提交、不能被拖动 —— 否则飞过去蹭到谁就会被当成"连上了"。
    /// </summary>
    public bool Spawning;

    public GameWindow(Game game, Piece piece, Point clientOrigin)
    {
        _game = game;
        Piece = piece;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Windows";
        BackColor = Palette.Background;
        DoubleBuffered = true;
        ClientSize = new Size(ViewTilesX * TileSize, ViewTilesY * TileSize);

        SetClientOrigin(clientOrigin);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MeasureClientOffset();
        SetClientOrigin(_desiredClientOrigin);
        _game.RefreshLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 句柄建好时量到的非客户区尺寸可能还是旧样式，显示后再校正一次
        MeasureClientOffset();
        SetClientOrigin(_desiredClientOrigin);
        _game.RefreshLayout();
    }

    void MeasureClientOffset()
    {
        if (!IsHandleCreated) return;

        // 无边框窗口客户区就等于窗口矩形，不要去量——句柄刚建好时量到的是旧样式的边框
        if (FormBorderStyle == FormBorderStyle.None)
        {
            _clientOffsetX = 0;
            _clientOffsetY = 0;
            return;
        }

        var screen = RectangleToScreen(ClientRectangle);
        var wr = new Native.RECT();
        GetWindowRect(Handle, out wr);
        _clientOffsetX = screen.Left - wr.Left;
        _clientOffsetY = screen.Top - wr.Top;
    }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out Native.RECT rect);

    public Rectangle ClientScreen
    {
        get
        {
            var s = RectangleToScreen(ClientRectangle);
            return new Rectangle(s.Left, s.Top, ViewTilesX * TileSize, ViewTilesY * TileSize);
        }
    }

    public void SetClientOrigin(Point clientOrigin)
    {
        _desiredClientOrigin = ClampToScreen(clientOrigin);
        if (!IsHandleCreated) return;
        Location = new Point(_desiredClientOrigin.X - _clientOffsetX, _desiredClientOrigin.Y - _clientOffsetY);
    }

    /// <summary>
    /// 把客户区原点夹进显示器工作区：窗口永远整块留在屏幕里，不会被拖到任务栏下面或者屏幕外面。
    /// 屏幕比窗口还小的时候退化成贴左上角。
    /// </summary>
    public static Point ClampToScreen(Point origin)
    {
        var size = new Size(ViewTilesX * TileSize, ViewTilesY * TileSize);
        var work = Screen.FromPoint(new Point(origin.X + size.Width / 2, origin.Y + size.Height / 2)).WorkingArea;

        int maxX = Math.Max(work.Left, work.Right - size.Width);
        int maxY = Math.Max(work.Top, work.Bottom - size.Height);
        return new Point(
            Math.Clamp(origin.X, work.Left, maxX),
            Math.Clamp(origin.Y, work.Top, maxY));
    }

    public static int GridRound(int v) => (int)Math.Round(v / (double)TileSize) * TileSize;

    public void SetCamera(float x, float y)
    {
        CamX = x;
        CamY = y;
    }

    /// <summary>相连时把取景框对齐到整格，避免接缝错半个瓦片。</summary>
    public bool SnapCameraIfNeeded()
    {
        float nx = MathF.Round(CamX), ny = MathF.Round(CamY);
        if (MathF.Abs(nx - CamX) < 0.0001f && MathF.Abs(ny - CamY) < 0.0001f) return false;
        CamX = nx;
        CamY = ny;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_RBUTTONUP:
                ShowGameMenu();
                return;

            case Native.WM_CAPTURECHANGED when _dragActive:
                FinishDrag();
                break;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// 弹出右键菜单（系统原生 TrackPopupMenu）。
    ///
    /// 为什么不用 ContextMenuStrip：它依赖 WinForms 自己的消息循环
    /// （Application.MessageLoop），而本程序是手写的 PeekMessage 泵、从未调用
    /// Application.Run，那套状态机起不来，菜单会进入降级状态——第一次点击只当
    /// "关掉菜单"，要点第二次才执行菜单项。原生菜单的输入由 Windows 自己处理，
    /// 和进程怎么泵消息无关。
    ///
    /// TrackPopupMenu 是阻塞的，所以选中项等它返回之后再执行：
    /// 开窗/退出都会重建或销毁窗口，不该在它自己的模态循环里做。
    /// 而且这里是本窗口的 WM_RBUTTONUP 处理中——退出要销毁所有窗口（包括这一个），
    /// 就地执行等于在自己的消息处理里销毁自己，所以统一丢给主循环延后执行。
    /// </summary>
    void ShowGameMenu()
    {
        int cmd = GameMenu.Show(Handle, Cursor.Position,
            new MenuEntry(MenuOpenWindow, "新开一个窗口"),
            new MenuEntry(MenuToggleMute, _game.Audio.Muted ? "取消静音" : "静音"),
            new MenuEntry(0, null),                       // 分隔线
            new MenuEntry(MenuQuit, "退出游戏"));

        switch (cmd)
        {
            case MenuOpenWindow: _game.Enqueue(_game.OpenWindow); break;
            case MenuToggleMute: _game.Enqueue(() => _game.Audio.ToggleMute()); break;
            case MenuQuit: _game.Enqueue(_game.Quit); break;
        }
    }

    // 自己接管拖拽：系统的模态移动循环会卡住主循环，那段时间画面是位图搬运，没法画预览
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        // 新生动画期间整局都不接受拖动：窗口正在抖/正在飞，
        // 这时候抓起来的落点、吸附目标全是不该存在的中间状态
        if (_game.SpawnAnimating || Spawning) return;

        var screen = PointToScreen(e.Location);
        var cs = ClientScreen.Location;
        _dragGrab = new Point(screen.X - cs.X, screen.Y - cs.Y);
        _dragActive = true;
        Capture = true;
        PreviewOrigin = cs;
        _game.BeginDrag(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragActive) return;

        var cursor = Cursor.Position;
        SetClientOrigin(ApplySnap(new Point(cursor.X - _dragGrab.X, cursor.Y - _dragGrab.Y)));
        PreviewOrigin = ClientScreen.Location;   // 夹过屏幕边界后的真实落点
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        if (_dragActive) FinishDrag();
    }

    void FinishDrag()
    {
        if (!_dragActive) return;
        _dragActive = false;
        Capture = false;

        var cs = ClientScreen.Location;
        SetClientOrigin(ApplySnap(new Point(GridRound(cs.X), GridRound(cs.Y))));
        PreviewOrigin = ClientScreen.Location;
        _game.EndDrag(this);
    }

    Point ApplySnap(Point p)
    {
        foreach (var other in _game.Windows)
        {
            if (other == this || other.IsDisposed || other.Spawning) continue;
            var r = other.ClientScreen;

            bool vOverlap = p.Y < r.Bottom && p.Y + ViewTilesY * TileSize > r.Top;
            bool hOverlap = p.X < r.Right && p.X + ViewTilesX * TileSize > r.Left;

            if (vOverlap)
            {
                if (Math.Abs(p.X - r.Right) <= SnapDistance) p.X = r.Right;
                else if (Math.Abs(p.X + ViewTilesX * TileSize - r.Left) <= SnapDistance) p.X = r.Left - ViewTilesX * TileSize;
            }
            if (hOverlap)
            {
                if (Math.Abs(p.Y - r.Bottom) <= SnapDistance) p.Y = r.Bottom;
                else if (Math.Abs(p.Y + ViewTilesY * TileSize - r.Top) <= SnapDistance) p.Y = r.Top - ViewTilesY * TileSize;
            }
        }
        return p;
    }

    /// <summary>背景层统一压暗到这个亮度倍数，和前景拉开差距，避免玩家把装饰背景当成可站立地形。</summary>
    public const float BackdropBrightness = 0.5f;

    /// <summary>背景层专用的颜色矩阵：只缩放 RGB，alpha 不变。</summary>
    static readonly ImageAttributes BackdropAttrs = CreateBackdropAttrs();

    static ImageAttributes CreateBackdropAttrs()
    {
        var m = new ColorMatrix
        {
            Matrix00 = BackdropBrightness,
            Matrix11 = BackdropBrightness,
            Matrix22 = BackdropBrightness,
            Matrix33 = 1f,
        };
        var attrs = new ImageAttributes();
        attrs.SetColorMatrix(m, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        return attrs;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>
    /// 按 xf 编码（低两位 = 顺时针 90° 的步数，bit2 = 水平镜像）画一块瓦片。
    /// 先镜像再旋转，和编辑器里的约定一致。
    /// </summary>
    internal static void DrawSprite(Graphics g, Image sheet, Rectangle src, Rectangle dst, int xf,
        ImageAttributes? attrs)
    {
        if (xf == 0)
        {
            g.DrawImage(sheet, dst, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return;
        }
        var state = g.Save();
        g.TranslateTransform(dst.X + dst.Width / 2f, dst.Y + dst.Height / 2f);
        g.RotateTransform((xf & 3) * 90f);
        if ((xf & 4) != 0) g.ScaleTransform(-1f, 1f);
        var local = new Rectangle(-dst.Width / 2, -dst.Height / 2, dst.Width, dst.Height);
        g.DrawImage(sheet, local, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        g.Restore(state);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float fx = CamX - View.X;
        float fy = CamY - View.Y;
        int shiftX = (int)MathF.Round(fx * LogicalCell);
        int shiftY = (int)MathF.Round(fy * LogicalCell);

        using (var g0 = Graphics.FromImage(_frame))
        {
            g0.Clear(Palette.BackgroundDark);
            g0.InterpolationMode = InterpolationMode.NearestNeighbor;
            g0.PixelOffsetMode = PixelOffsetMode.Half;

            for (int cy = -1; cy <= ViewTilesY; cy++)
            {
                for (int cx = -1; cx <= ViewTilesX; cx++)
                {
                    var (piece, mx, my) = _game.CellAt(this, cx, cy);
                    var dst = new Rectangle(cx * LogicalCell - shiftX, cy * LogicalCell - shiftY,
                        LogicalCell, LogicalCell);

                    var def = TileSet.Defs[piece.At(mx, my)];
                    int sprite = def.SpriteAt(mx, my, piece.VarAt(mx, my));
                    if (sprite < 0) continue;
                    // 每格自己决定亮不暗：dim 的格子按背景亮度画，就是以前的"背景层"
                    var attrs = piece.DimAt(mx, my) ? BackdropAttrs : null;
                    // 每格可以带自己的朝向（关卡 xf 层），素材本身方向不全时靠它补齐
                    DrawSprite(g0, _game.TileSheet, TileSet.SpriteRect(sprite), dst,
                        piece.XfAt(mx, my), attrs);
                }
            }

            DrawPlayer(g0);
        }

        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_frame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

        DrawConnectionGlow(g);
        DrawSeamFlashes(g);
        _game.DrawOverlay(g, this);
    }

    void DrawPlayer(Graphics g)
    {
        var p = _game.Player;
        if (p.Piece != Piece) return;   // 这块窗口显示的不是同一张地图
        if (Spawning) return;           // 还在飞的新窗口不画角色（单块地图的关卡里会撞上同一个 Piece）

        // 角色的地图坐标和镜头始终在同一圈里（跨窗口交接时就换算好了），直接用差值
        float lx = (p.Pos.X - CamX) * LogicalCell;
        float ly = (p.Pos.Y - CamY) * LogicalCell;
        if (lx < -LogicalCell || ly < -LogicalCell || lx > _frame.Width + LogicalCell || ly > _frame.Height + LogicalCell)
            return;

        float x = lx - LogicalCell / 2f, y = ly - LogicalCell / 2f;
        var src = new RectangleF(0, 0, _game.PlayerSheet.Width, _game.PlayerSheet.Height);

        // 朝左时水平镜像：用三点定位，第一、二个点对调就等于翻转，比负宽度更稳
        PointF[] dest = p.FacingRight
            ? new[] { new PointF(x, y), new PointF(x + LogicalCell, y), new PointF(x, y + LogicalCell) }
            : new[] { new PointF(x + LogicalCell, y), new PointF(x, y), new PointF(x + LogicalCell, y + LogicalCell) };

        g.DrawImage(_game.PlayerSheet, dest, src, GraphicsUnit.Pixel);
    }

    void DrawConnectionGlow(Graphics g)
    {
        var group = Group;
        if (group == null || group.Boundary.Count == 0) return;

        // 只要"松手会连上"，参与的两块窗口都切成同一套青蓝预览态：
        // 被拖的那块看自己会不会吸上，另一块看有没有人正要贴过来。
        bool imminent = _game.DraggingWindow != null &&
                        (Dragging ? _game.PreviewConnects(this) : _game.PreviewSeams(this).Count > 0);
        bool connected = Dragging ? imminent : HasNeighbors;

        float pulse = imminent
            ? 0.55f + 0.45f * MathF.Sin(_game.Time * 3.2f)
            : connected
                ? 0.70f + 0.30f * MathF.Sin(_game.Time * 2.0f)
                : 0.72f + 0.28f * MathF.Sin(_game.Time * 1.15f);

        Color color = imminent
            ? Color.FromArgb(130, 255, 240)
            : connected ? Color.FromArgb(150, 245, 225) : Color.FromArgb(255, 206, 128);

        // 整组共用一条外轮廓：换算到本窗口的客户坐标，超出部分由位图边界自然裁掉。
        // 所以拼缝内侧不亮、转角由折线的 join 连成一整圈。
        var origin = ClientScreen.Location;
        using var path = new GraphicsPath();
        foreach (var loop in group.Boundary)
        {
            if (loop.Count < 3) continue;
            var pts = new PointF[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                pts[i] = new PointF(loop[i].X - origin.X, loop[i].Y - origin.Y);
            path.AddLines(pts);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;

        // 一条闭合折线反复描边：宽度由外向里收，透明度由弱到强，拐角交给 GDI+ 的 join 处理
        float band = imminent ? 15f * (0.88f + 0.12f * MathF.Sin(_game.Time * 3.2f)) : 15f;
        const int layers = 10;
        for (int i = 0; i < layers; i++)
        {
            float t = i / (float)(layers - 1);
            float width = 2f * band * (1f - t) + 2.2f;
            float falloff = MathF.Pow(t, 1.9f);
            int alpha = (int)((connected ? 250 : 228) * falloff * pulse);
            if (alpha <= 1) continue;

            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), color), width)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawPath(pen, path);
        }

        DrawPreviewSeams(g);
    }

    /// <summary>拖动时，在"马上要被接上"的那段接触面上画传送门状的能流（两侧窗口各画自己这一半）。</summary>
    void DrawPreviewSeams(Graphics g)
    {
        if (_game.DraggingWindow == null) return;

        var seams = _game.PreviewSeams(this);
        if (seams.Count == 0) return;

        float t = _game.Time;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;

        foreach (var (side, s, e) in seams)
            DrawPortalBand(g, side, s, e, t, Color.FromArgb(120, 240, 235), 1f);
    }

    /// <summary>一条贴着窗口边缘的"能量裂缝"：外发光 + 蜿蜒能量丝 + 脉动核心 + 流动粒子。</summary>
    void DrawPortalBand(Graphics g, int side, float s, float e, float t, Color color, float strength)
    {
        if (strength <= 0.01f) return;
        int w = ClientSize.Width, h = ClientSize.Height;
        float len = e - s;
        if (len < 10) return;

        bool vertical = side is 0 or 1;
        float edge = side switch { 0 => 0f, 1 => w, 2 => 0f, _ => h };
        float inward = side is 0 or 2 ? 1f : -1f;

        PointF P(float u, float v) => vertical
            ? new PointF(edge + inward * v, s + u)
            : new PointF(s + u, edge + inward * v);

        // 1) 外发光
        const float halo = 20f;
        RectangleF rect = vertical
            ? side == 0 ? new RectangleF(0, s, halo, len) : new RectangleF(w - halo, s, halo, len)
            : side == 2 ? new RectangleF(s, 0, len, halo) : new RectangleF(s, h - halo, len, halo);
        float angle = side switch { 0 => 0f, 1 => 180f, 2 => 90f, _ => 270f };
        float breath = 0.68f + 0.32f * MathF.Sin(t * 3.4f);

        using (var brush = new LinearGradientBrush(rect,
                   Color.FromArgb((int)(115 * breath * strength), color),
                   Color.FromArgb(0, color), angle))
        {
            g.FillRectangle(brush, rect);
        }

        // 2) 蜿蜒的能量丝
        for (int k = 0; k < 3; k++)
        {
            float baseV = 3f + k * 4.6f;
            float amp = 1.5f + k * 0.7f;
            float speed = 1.5f + k * 0.55f;
            int steps = Math.Clamp((int)(len / 9f), 8, 44);

            float prevU = 0f;
            float prevV = baseV + amp * MathF.Sin(t * speed + k * 2.1f);

            for (int i = 1; i <= steps; i++)
            {
                float u = len * i / steps;
                float v = baseV + amp * MathF.Sin(u * 0.075f + t * speed + k * 2.1f);
                int alpha = (int)((128 - k * 30) * (0.55f + 0.45f * MathF.Sin(u * 0.11f + t * 5.5f)) * strength);

                if (alpha > 4)
                {
                    using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), color), 2.6f - k * 0.5f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                    };
                    g.DrawLine(pen, P(prevU, prevV), P(u, v));
                }

                prevU = u;
                prevV = v;
            }
        }

        // 4) 顺着缝隙流动的粒子
        const int sparks = 18;
        for (int i = 0; i < sparks; i++)
        {
            float r1 = Frac(MathF.Sin(i * 91.7f) * 43758.5453f);
            float r2 = Frac(MathF.Sin(i * 47.3f) * 24634.6345f);
            float r3 = Frac(MathF.Sin(i * 13.1f) * 9871.1234f);

            float speed = 0.10f + 0.16f * r1;
            float phase = (t * speed + r2) % 1f;
            float u = phase * len;
            float v = 2.5f + 11f * r3 + 1.5f * MathF.Sin(t * 2.3f + i);
            float fade = MathF.Pow(MathF.Sin(MathF.PI * phase), 0.6f);
            float size = 2.4f + 2.2f * r1;

            var p = P(u, v);
            int sparkAlpha = (int)(225 * fade * strength);
            if (sparkAlpha <= 2) continue;
            using var dot = new SolidBrush(Color.FromArgb(Math.Clamp(sparkAlpha, 0, 255), color));
            g.FillEllipse(dot, p.X - size / 2f, p.Y - size / 2f, size, size);
        }
    }

    static float Frac(float v) => v - MathF.Floor(v);

    /// <summary>刚连上的接缝：白光泛白、扩散、淡出。</summary>
    void DrawSeamFlashes(Graphics g)
    {
        for (int i = 0; i < _game.SeamFlashes.Count; i++)
        {
            var flash = _game.SeamFlashes[i];
            if (flash.Window != this) continue;

            float p = Math.Clamp((_game.Time - flash.Start) / flash.Duration, 0f, 1f);
            if (p >= 1f) continue;

            DrawSeamFlash(g, flash.Side, flash.S, flash.E, p);
        }
    }

    void DrawSeamFlash(Graphics g, int side, float s, float e, float p)
    {
        // 就是同一个传送门，只是颜色泛白、强度随进度衰减
        float strength = MathF.Pow(1f - p, 1.5f);
        DrawPortalBand(g, side, s, e, _game.Time, Color.White, strength);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 换关卡时是程序自己在拆窗口（Game.Swapping），别当成"玩家关窗退游戏"
        if (!_game.Quitting && !_game.Swapping)
        {
            // 不允许单独关掉某一个窗口：先拦下来，再让整局一起退出
            e.Cancel = true;
            _game.Enqueue(_game.Quit);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _game.OnWindowClosed(this);
    }
}

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(23, 27, 40);
    /// <summary>
    /// 窗口底色。必须和素材的"洞底"纯色 (33,38,63) 一致：
    /// 这套图集是不透明底设计，草地、岩体、草丛的内部空白全是这个色，
    /// 底色一旦比它暗，屏幕上就会到处出现一块块浅色方块（草丛尤其明显）。
    /// </summary>
    public static readonly Color BackgroundDark = Color.FromArgb(33, 38, 63);
    public static readonly Color Text = Color.FromArgb(228, 232, 240);
    public static readonly Color TextDim = Color.FromArgb(132, 142, 160);
}
