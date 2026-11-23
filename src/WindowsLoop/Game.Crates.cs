using System.Numerics;
using WindowsLoop.Tileset;

namespace WindowsLoop;

/// <summary>
/// 箱子的实体逻辑：推箱蓄力、推箱补间、下落、载人。单独一个文件，免得 Game.cs 继续膨胀。
///
/// ── 两套坐标，各管一件事 ────────────────────────────────────────────────
///   <see cref="Crate.CellX"/> / <see cref="Crate.CellY"/>  逻辑格：**占位**
///       推箱目标格、压力板、窗口交接、别的箱子能不能挤进来 —— 都按这一格算。
///   <see cref="Crate.X"/> / <see cref="Crate.Y"/>          视觉矩形：**碰撞与绘制**
///       角色和箱子的碰撞、踩箱顶、推箱判定 —— 都按这个连续矩形算。
///
/// 推箱补间期间两者会短暂不一致（逻辑格在开始推的那一刻就落到目标格，视觉位置追过去），
/// 这是故意的：地盘先占住，别的箱子不会挤进同一个格子；而碰撞和画面看的是连续矩形，所以看起来是滑过去的。
///
/// ── 判定口径只有一条 ───────────────────────────────────────────────────
/// "人贴没贴住箱子"一律走 <see cref="EntityCollision.ReachesCrate"/>：量身体边缘到箱面的距离。
/// 不撒探针、不按格子猜 —— 老版本用 0.10 格的探针，比角色一帧的位移（0.167）还短，
/// 所以走到箱子边上停住时有一半概率探不到，表现为"明明贴着却推不动"。
/// </summary>
internal sealed partial class Game
{
    // ── 参数 ──────────────────────────────────────────────────────────────

    /// <summary>贴着箱子持续朝它走多久才推得动（秒）。</summary>
    const float PushChargeTime = 0.30f;

    /// <summary>
    /// "够得着"的距离（格）。**必须比角色一帧的最大位移（Speed 10 × 1/60 ≈ 0.167）大**，
    /// 否则走到箱子边上停住、还没完全贴住时会被判成够不着。
    ///
    /// 贴齐由 <see cref="SnapFlushToCrate"/> 兜底（撞上箱子就贴到箱面上），
    /// 所以这个值只是"手够得着"的余量，不必很大。
    /// </summary>
    const float PushReach = 0.22f;

    /// <summary>
    /// 推一格用多久。这段时间里人是被箱子带着走的（同速位移），
    /// 所以它同时决定了"推箱时人和箱子走多快"。
    /// </summary>
    const float CrateSlideTime = 0.26f;

    const float CrateGravity = 30f;        // 格/秒²

    /// <summary>
    /// 箱子下落的速度上限（20，玩家是 30）：明显比人慢，
    /// 玩家从上面追得下来、能落到它顶上；站在上面时是以箱子的速度慢慢沉，不是自由落体。
    /// </summary>
    const float CrateMaxFallSpeed = 20f;   // 格/秒

    const float CrateImpactDecay = 0.16f;  // 落地压扁量衰减到 0 用的时间

    /// <summary>这一帧手按着的水平方向（-1 / 0 / +1）。推箱子看它，不看速度。</summary>
    float _moveAxis;

    /// <summary>正在蓄力顶的那一个箱子、方向、已经顶了多久。</summary>
    Crate? _pushCrate;
    int _pushDir;
    float _pushCharge;

    /// <summary>正在被推着走的那个箱子（补间期间玩家跟着它一起位移）。</summary>
    Crate? _carryCrate;
    int _carryDir;

    /// <summary>
    /// 正踩着的箱子。落地时认下来（<see cref="TryCrateTopUnder"/>），起跳 / 走出箱顶 / 换地图时解除。
    /// 有了它，"站在箱子上"是一个明确状态，而不是每帧拿"脚底离箱顶多近"去猜。
    /// </summary>
    Crate? _rideCrate;

    /// <summary>
    /// 这一帧玩家是不是正被箱子带着走：推箱补间进行中、而且手还朝那个方向按着。
    /// 这期间玩家的水平速度让位给箱子（`Player.Vel.X` 被清 0），位置由 <see cref="StepSlide"/> 钉住。
    /// </summary>
    public bool CarryingCrate => _carryCrate is { Sliding: true } && _moveAxis == _carryDir;

    // ── 几何：把箱子换算成"离参照点最近的那一份矩形" ─────────────────────

    /// <summary>
    /// 把箱子的矩形换算到 <paramref name="owner"/> 这块窗口的地图坐标里，
    /// 并取离 (<paramref name="refX"/>, <paramref name="refY"/>) 最近的**那一份**。
    ///
    /// 为什么是"最近的一份"：单块窗口的地图是环形的，同一只箱子会同时出现在
    /// 相差整数个地图尺寸的位置上（掉出画面又从另一头回来就是这么实现的）。
    /// 碰撞和载人只关心离角色最近的那一份。
    ///
    /// 箱子在别的地图块上时（跨缝），先把它折进那块窗口正在显示的那一段，
    /// 再用两块窗口的相对位移换算过来 —— 接缝另一侧的箱子照样挡得住人。
    /// </summary>
    /// <returns>false = 这只箱子不属于这片拼接（根本没被任何窗口显示）。</returns>
    bool TryCrateRect(GameWindow owner, Crate c, float refX, float refY, out float left, out float top)
    {
        left = top = 0f;

        if (c.Piece == owner.Piece)
        {
            int w = c.Piece.Width, h = c.Piece.Height;
            left = c.X + (w > 0 ? w * MathF.Floor((refX - c.X) / w + 0.5f) : 0f);
            top = c.Y + (h > 0 ? h * MathF.Floor((refY - c.Y) / h + 0.5f) : 0f);
            return true;
        }

        var other = WindowFor(c.Piece);
        if (other == null || other == owner || other.IsDisposed) return false;

        int ow = c.Piece.Width, oh = c.Piece.Height;
        if (ow <= 0 || oh <= 0) return false;

        // 折进那块窗口正在显示的那一段（同一张环形地图可能同时露出好几个周期）
        float sx = c.X + ow * MathF.Ceiling((other.CamX - c.X) / ow);
        float sy = c.Y + oh * MathF.Ceiling((other.CamY - c.Y) / oh);

        // owner 画面 → 对方画面减一个偏移，换算成地图坐标就是加回来
        var off = ViewOffset(owner, other);
        left = sx - other.CamX + owner.CamX + off.X;
        top = sy - other.CamY + owner.CamY + off.Y;
        return true;
    }

    /// <summary>
    /// 某个 AABB 和任何一只箱子重叠吗。**只按箱子的实际矩形算**，不按"它占哪一格"算 ——
    /// 下落/推箱途中箱子的视觉位置会在格子里滑，拿格子判会出现"脚踩在箱顶上却被横向卡住"、
    /// 或者"人浮在半空"这类怪相。
    /// </summary>
    bool CrateHitsAabb(Vector2 pos)
    {
        var window = WindowFor(Player.Piece);
        if (window == null) return false;

        float l = pos.X - Player.Width / 2f, t = pos.Y - Player.Height / 2f;

        foreach (var c in Level.Crates)
        {
            if (!TryCrateRect(window, c, pos.X, pos.Y, out float cl, out float ct)) continue;
            if (EntityCollision.Overlap(l, t, Player.Width, Player.Height, cl, ct, 1f, 1f)) return true;
        }
        return false;
    }

    // ── 占位：逻辑格 ──────────────────────────────────────────────────────

    /// <summary>
    /// 某一格上的箱子（按逻辑格取）。
    /// 压力板、窗口交接、箱子之间互相挤占都用它 —— 那是"占位"的口径，不是碰撞口径。
    ///
    /// 两边都折回一个周期再比：箱子的逻辑格是连续坐标（可能比地图尺寸大出好几圈），
    /// 而调用方给的通常是地图里的下标。只在"占位"这里折，碰撞那条路不折 ——
    /// 折了就等于把格子问到了地图另一头。
    /// </summary>
    public Crate? CrateAtCell(Piece piece, int x, int y, Crate? except = null)
    {
        int w = piece.Width, h = piece.Height;
        if (w <= 0 || h <= 0) return null;
        int wx = WrapInt(x, w);
        int wy = WrapInt(y, h);

        var list = Level.Crates;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == except || c.Piece != piece) continue;
            if (WrapInt(c.CellX, w) == wx && WrapInt(c.CellY, h) == wy) return c;
        }
        return null;
    }

    /// <summary>角色是不是占着这一格（身体矩形和这一格真的重叠，不是"踩在它上沿"）。</summary>
    bool PlayerOccupies(Piece piece, int x, int y)
    {
        if (Player.Piece != piece) return false;

        // 把这一格折到离角色最近的那个周期上再比（环形地图上两者可能差整数个周期）
        float cx = x + 0.5f, cy = y + 0.5f;
        int w = piece.Width, h = piece.Height;
        if (w > 0) cx += w * MathF.Floor((Player.Pos.X - cx) / w + 0.5f);
        if (h > 0) cy += h * MathF.Floor((Player.Pos.Y - cy) / h + 0.5f);

        return EntityCollision.Overlap(
            Player.Pos.X - Player.Width / 2f, Player.Pos.Y - Player.Height / 2f,
            Player.Width, Player.Height,
            cx - 0.5f, cy - 0.5f, 1f, 1f);
    }

    /// <summary>
    /// 箱子能不能占这一格。挡住箱子的东西：拼接外边界之外、实心地形、单向平台
    /// （箱子不像人那样能侧身穿过去）、别的箱子、站在那儿的玩家。被事件藏起来的格子不算挡。
    ///
    /// 取景框外的处理跟地形一致（见 <see cref="CellBlocks"/>）：跟别的窗口拼在一起的，
    /// 框外就是拼接后的外边界；单独一块自由滚动的只是镜头没跟上，按自己的环形地图绕回去接着算。
    /// </summary>
    bool CrateCellBlocked(Piece piece, int x, int y, Crate? self)
    {
        var window = WindowFor(piece);
        if (window == null) return true;   // 这张地图没有窗口显示：别动，免得在看不见的地方乱跑

        if (!TryResolvePoint(window, x + 0.5f, y + 0.5f, out var target, out int px, out int py))
        {
            if (window.HasNeighbors) return true;   // 拼接后的外边界之外
            target = piece;
            px = x;                                 // 环形：还是自己这张地图，坐标由 KindAt 自己 wrap
            py = y;
        }

        if (IsHidden(target, px, py)) return false;
        if (target.KindAt(px, py) != SolidKind.Pass) return true;
        if (CrateAtCell(target, px, py, self) != null) return true;
        return PlayerOccupies(target, px, py);
    }

    /// <summary>
    /// 这一脚往下落，会不会正好踩到某只箱子的顶上？返回那只箱子（最上面那只）和它顶面的连续坐标。
    /// 认的是"顶面落在这一小步的脚底区间里"的箱子 —— 也就是刚好压在它顶上的那一只。
    /// </summary>
    bool TryCrateTopUnder(Vector2 pos, float feetBefore, out Crate? crate, out float top)
    {
        crate = null;
        top = 0f;

        var window = WindowFor(Player.Piece);
        if (window == null) return false;

        float l = pos.X - Player.Width / 2f, r = pos.X + Player.Width / 2f;
        float feet = pos.Y + Player.Height / 2f;

        bool hit = false;
        foreach (var c in Level.Crates)
        {
            if (!TryCrateRect(window, c, pos.X, pos.Y, out float cl, out float ct)) continue;

            // 横向得压着它
            if (r <= cl + EntityCollision.Eps || l >= cl + 1f - EntityCollision.Eps) continue;

            // 顶面要落在这一小步的脚底区间里（上下各留 0.02 的容差）
            if (ct > feet + 0.02f || ct < feetBefore - 0.02f) continue;

            if (!hit || ct < top) { top = ct; crate = c; hit = true; }
        }

        return hit;
    }

    // ── 环形世界 ↔ 连通世界 ───────────────────────────────────────────────

    /// <summary>
    /// 这只箱子在窗口 <paramref name="w"/> 里**实际被画出来的**那一份（取景框里最靠中间的那份）。
    /// 一份都没落在框里就返回 false。
    ///
    /// 判"可见"必须和绘制（<c>GameWindow.DrawCrates</c>）用同一个口径：
    /// 地图比取景框高不了多少时（比如 12 格高的图配 10 格的框），同一只箱子会同时有两份落在框里，
    /// 而"离镜头最近的那一份"可能恰好在框外 —— 只按最近的一份判，就会把一只
    /// **明明画在画面里**的箱子当成"看不见"冻住，看着就是悬在空中不动。
    /// </summary>
    static bool TryCrateDrawnImage(GameWindow w, Crate c, out Vector2 image)
    {
        image = default;

        int pw = c.Piece.Width, ph = c.Piece.Height;
        if (pw <= 0 || ph <= 0)
        {
            image = new Vector2(c.X, c.Y);
            return true;
        }

        // 挑"离箱子自己最近"的那一份，不是"离取景框中心最近"的。
        // 地图高度≈取景框高度时，顶部和底部两份会同时在框里；按中心挑的话，
        // 箱子明明在底部、却会被折到顶部那一份上 —— 每帧折一次，就是"在自己窗口里无限循环"。
        float cx = c.X + 0.5f;
        float cy = c.Y + 0.5f;

        int kx0 = (int)MathF.Floor((w.CamX - 1f - c.X) / pw);
        int kx1 = (int)MathF.Ceiling((w.CamX + GameWindow.ViewTilesX + 1f - c.X) / pw);
        int ky0 = (int)MathF.Floor((w.CamY - 1f - c.Y) / ph);
        int ky1 = (int)MathF.Ceiling((w.CamY + GameWindow.ViewTilesY + 1f - c.Y) / ph);

        float best = float.MaxValue;
        bool found = false;

        for (int ky = ky0; ky <= ky1; ky++)
            for (int kx = kx0; kx <= kx1; kx++)
            {
                float x = c.X + kx * pw, y = c.Y + ky * ph;

                // 这一份要和取景框真的交叠（和 DrawCrates 里那次裁剪同一个意思）
                if (x + 1f < w.CamX || x > w.CamX + GameWindow.ViewTilesX) continue;
                if (y + 1f < w.CamY || y > w.CamY + GameWindow.ViewTilesY) continue;

                float dx = x - c.X, dy = y - c.Y;
                float d = dx * dx + dy * dy;
                if (d >= best) continue;

                best = d;
                image = new Vector2(x, y);
                found = true;
            }

        return found;
    }

    /// <summary>离镜头最近的那一份，露没露在取景框里（以"画出来的那一份"为准）。</summary>
    static bool CrateVisibleIn(GameWindow w, Crate c) => TryCrateDrawnImage(w, c, out _);

    /// <summary>箱子整块和这块窗口的取景框有交叠吗。</summary>
    static bool CrateTouchesWindow(GameWindow w, Crate c)
    {
        var u = ViewLocal(w, new Vector2(c.X, c.Y));
        return u.X + 1f > 0f && u.X < GameWindow.ViewTilesX &&
               u.Y + 1f > 0f && u.Y < GameWindow.ViewTilesY;
    }

    /// <summary>
    /// "刚跨出去"：整块已经在取景框外，但还贴着框边（2 格以内）。
    /// 这种情况要**先换窗口、不能先折叠** —— 折叠会把窗口另一头的周期副本当成"画面上的那一份"
    /// 拽过来，跨出底边的箱子就被折回顶部，于是永远走不到接缝（在自己的窗口里无限循环）。
    /// 离得远的（环形世界里飘了好几圈）不算，那种要折回来。
    /// </summary>
    static bool CrateNearOutside(GameWindow w, Crate c)
    {
        if (CrateTouchesWindow(w, c)) return false;

        const float margin = 2f;
        var u = ViewLocal(w, new Vector2(c.X, c.Y));
        return u.X + 1f >= -margin && u.X <= GameWindow.ViewTilesX + margin &&
               u.Y + 1f >= -margin && u.Y <= GameWindow.ViewTilesY + margin;
    }

    /// <summary>
    /// 把箱子的连续坐标折到"画出来的那一份"上。
    ///
    /// 什么时候需要它：窗口连上了，世界从"环形"变成"有边界的拼接"。箱子之前可能已经
    /// 沿着环形地图掉出去好几个地图高（连续坐标远超地图尺寸），而拼接世界只有眼前这一片 ——
    /// 得先把它收回**画面上那一份**，坐标和画面才是同一个东西，之后的下落/落地也才对得上。
    /// 骑在它上面的人跟着一起折，否则会被拽走整数个地图高。
    /// </summary>
    void FoldCrateIntoView(GameWindow w, Crate c)
    {
        if (!TryCrateDrawnImage(w, c, out var p)) return;   // 一份都没画出来：保持原样

        float dx = p.X - c.X, dy = p.Y - c.Y;
        if (MathF.Abs(dx) < 1e-4f && MathF.Abs(dy) < 1e-4f) return;

        c.X = p.X;
        c.Y = p.Y;

        // 逻辑格跟着一起折；同样是连续坐标，不做环形折回
        c.CellX = (int)MathF.Floor(c.X + 0.5f);
        c.CellY = (int)MathF.Floor(c.Y + 0.5f);

        if (c == _rideCrate && Player.Piece == c.Piece)
            Player.Pos = new Vector2(Player.Pos.X + dx, Player.Pos.Y + dy);
    }

    static int WrapInt(int v, int period) => ((v % period) + period) % period;

    /// <summary>
    /// 箱子掉出自己这块窗口、落到相邻窗口那边去时，换到对方的地图上。
    /// 换算和角色的 <c>TransferIfCrossed</c> 完全一样（只用两块窗口的相对位移，不经过屏幕坐标）。
    /// </summary>
    void TransferCrateIfCrossed(Crate c)
    {
        if (c.Sliding) return;   // 补间期间别换地图，等它落定

        var w = WindowFor(c.Piece);
        if (w == null || !w.HasNeighbors) return;   // 没连成一片：还是环形世界，不换

        var u = ViewLocal(w, new Vector2(c.X, c.Y));

        // ① 还和自己这块窗口有交叠就留在原地：只露出一半的时候它还画在自己窗格里，
        //    这时候换过去，看到的就是箱子莫名闪进隔壁。
        if (u.X + 1f > 0f && u.X < GameWindow.ViewTilesX &&
            u.Y + 1f > 0f && u.Y < GameWindow.ViewTilesY) return;

        // ①b 只处理"刚跨出去"的那种：位置得还贴着框边（留 2 格余量）。
        //     差了好几圈的坐标是在环形世界里飘着的，拿它去换窗口等于瞬移。
        if (u.X + 1f < -2f || u.X > GameWindow.ViewTilesX + 2f ||
            u.Y + 1f < -2f || u.Y > GameWindow.ViewTilesY + 2f) return;

        GameWindow? best = null;
        float bestArea = 0f;
        Vector2 bestDelta = default;

        foreach (var target in w.Neighbors)
        {
            if (target.IsDisposed) continue;

            var off = ViewOffset(w, target);
            var tl = u - off;   // 箱子在对方画面里的左上角

            // ② 按"箱子矩形和对方取景框的交叠面积"挑候选。
            //    只问中心点的话，贴着右边缘往下掉的箱子，中心已经出了下面那块窗口，
            //    就会被判成"不该换"，于是卡死在接缝上。
            float ox = MathF.Min(tl.X + 1f, GameWindow.ViewTilesX) - MathF.Max(tl.X, 0f);
            float oy = MathF.Min(tl.Y + 1f, GameWindow.ViewTilesY) - MathF.Max(tl.Y, 0f);
            if (ox <= 0f || oy <= 0f) continue;

            float area = ox * oy;

            // ③ 面积并列时优先往下 —— 重力方向，拐角处不会因为遍历顺序左右乱跳
            bool better = area > bestArea + 1e-4f
                || (MathF.Abs(area - bestArea) <= 1e-4f && target == w.NeighborBottom);

            if (!better) continue;

            best = target;
            bestArea = area;
            bestDelta = new Vector2(target.CamX - w.CamX - off.X, target.CamY - w.CamY - off.Y);
        }

        if (best == null) return;

        // 骑在它上面的人跟着一起换地图：位置挪过去、Piece 也换 —— 只挪坐标不换 Piece，
        // 人就会挂着旧地图的坐标，下一帧 CanRide 直接判"不是同一张地图"把人甩下来。
        bool riding = c == _rideCrate && Player.Piece == c.Piece;

        c.Piece = best.Piece;
        c.X += bestDelta.X;
        c.Y += bestDelta.Y;
        c.CellX = (int)MathF.Floor(c.X + 0.5f);
        c.CellY = (int)MathF.Floor(c.Y + 0.5f);

        if (riding)
        {
            Player.Piece = best.Piece;
            Player.Pos += bestDelta;
        }
    }

    // ── 每帧 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 每帧推进箱子：先结算推箱蓄力，再走补间 / 下落，最后把踩在箱顶上的人贴回去。
    /// 放在 <see cref="MoveAndCollide"/> 之后调用 —— 角色位置定下来了，"贴没贴住"才是准的。
    /// </summary>
    void UpdateCrates(float dt)
    {
        UpdatePush(dt);

        var crates = Level.Crates;
        for (int i = 0; i < crates.Count; i++)
        {
            var c = crates[i];

            // 窗口连成一片之后，世界从"环形"变成"有边界的拼接"，两者不能混着算：
            //   · 箱子就在可见区域里 → 当场转入连通世界：先把连续坐标折到画出来的那一份，
            //     之后按拼接规则下落（接缝对面照样接得住，掉进对面窗口就换地图）；
            //   · 箱子在可见区域外 → 它只属于原来的滚动世界，跟拼接世界没法换算。
            //     物理整个冻住，等窗口分开再接着掉。
            //     以前不冻：脚下那一格落到取景框外就被当成拼接外边界，于是每帧"落地"一次 ——
            //     箱子悬在空中、压扁动画和落地音一直循环。
            var w = WindowFor(c.Piece);
            if (w == null)
            {
                c.PhysicsPaused = true;   // 这张地图没有窗口显示：看不见的东西不动
                continue;
            }

            // ① 刚跨出这块窗口（贴着框边、整块在外面）→ 交给隔壁。
            //    这一步必须在折叠之前：折叠会把窗口另一头的周期副本拽过来，
            //    跨出底边的箱子就被"折"回顶部，永远走不到接缝（在窗口里无限循环）。
            if (w.HasNeighbors && !c.Sliding && CrateNearOutside(w, c))
            {
                TransferCrateIfCrossed(c);
                w = WindowFor(c.Piece);
                if (w == null) { c.PhysicsPaused = true; continue; }
            }

            // ② 还在窗口里的（或是在环形世界里飘了好几圈的）→ 归一到"画出来的那一份"。
            //    窗口分开那段时间箱子是自由环行的，连续坐标可能差了好几圈；不先收回来，
            //    后面的碰撞就是在拿"地图另一头"的位置算。
            if (w.HasNeighbors && !c.Sliding) FoldCrateIntoView(w, c);

            // ③ 折叠之后位置可能变了，再给换窗口一次机会
            if (w.HasNeighbors) TransferCrateIfCrossed(c);
            w = WindowFor(c.Piece);
            if (w == null) { c.PhysicsPaused = true; continue; }

            if (w.HasNeighbors && !CrateVisibleIn(w, c))
            {
                c.PhysicsPaused = true;
                continue;
            }

            c.PhysicsPaused = false;

            if (c.Sliding) StepSlide(c, dt);

            // 脚下那一格空了就开始掉（自由滚动的窗口里，脚下越过地图底部会绕回顶部）
            if (!c.Sliding && !c.Falling &&
                !CrateCellBlocked(c.Piece, c.CellX, c.CellY + 1, c))
            {
                c.Falling = true;
                c.VY = 0f;
            }
            if (c.Falling) StepFall(c, dt);

            // 踩在这只箱子上：箱子动完之后把脚底重新贴到它顶面。
            // 直接"贴"而不是"加一个位移增量"，所以不会累积误差。
            if (c == _rideCrate)
            {
                if (CanRide(c)) GlueToRide(c);
                else _rideCrate = null;
            }

            if (c.Impact > 0f) c.Impact = MathF.Max(0f, c.Impact - dt / CrateImpactDecay);
        }

    }

    /// <summary>还能不能算"踩在这只箱子上"：同一张地图、脚在箱顶附近、横向还压着、没在起跳。</summary>
    bool CanRide(Crate c)
    {
        if (c.Piece != Player.Piece) return false;
        if (Player.Vel.Y < -0.01f) return false;         // 正在起跳

        var window = WindowFor(Player.Piece);
        if (window == null) return false;
        if (!TryCrateRect(window, c, Player.Pos.X, Player.Pos.Y, out float cl, out float ct)) return false;

        float feet = Player.Pos.Y + Player.Height / 2f;
        if (MathF.Abs(feet - ct) > 0.5f) return false;   // 已经离开箱顶了（一帧最多掉 0.5 格）

        float l = Player.Pos.X - Player.Width / 2f, r = Player.Pos.X + Player.Width / 2f;
        return r > cl + 0.02f && l < cl + 0.98f;
    }

    /// <summary>把脚底贴到箱顶，并把这一帧当成"落地"：不落、不抖、随时能起跳。</summary>
    void GlueToRide(Crate c)
    {
        var window = WindowFor(Player.Piece);
        if (window == null) return;

        // 贴"离角色最近的那一份"，别在环形地图上把人生生挪走整数个地图高
        if (!TryCrateRect(window, c, Player.Pos.X, Player.Pos.Y, out _, out float ct)) return;

        Player.Pos = new Vector2(Player.Pos.X, ct - Player.Height / 2f);
        Player.Vel.Y = 0f;
        Player.OnGround = true;
        Player.Coyote = Player.CoyoteTime;
        _wasOnGround = true;
        _airTime = 0f;
    }

    /// <summary>
    /// 推箱蓄力：手朝箱子那边按着、并且够得着它，就开始计时；满 300ms 推一格。
    /// 松开、换方向、换箱子、箱子正在动，全都把计时清零 —— 所以"持续"是字面意思。
    /// </summary>
    void UpdatePush(float dt)
    {
        int dir = _moveAxis > 0f ? 1 : _moveAxis < 0f ? -1 : 0;
        var crate = dir == 0 ? null : CrateInFront(dir);

        if (crate == null || crate.Busy)
        {
            _pushCrate = null;
            _pushDir = 0;
            _pushCharge = 0f;
            return;
        }

        if (crate != _pushCrate || dir != _pushDir)
        {
            _pushCrate = crate;
            _pushDir = dir;
            _pushCharge = 0f;
        }

        _pushCharge += dt;
        if (_pushCharge < PushChargeTime) return;

        _pushCharge = 0f;
        int tx = crate.CellX + dir;
        int ty = crate.CellY;
        if (CrateCellBlocked(crate.Piece, tx, ty, crate)) return;   // 推不动：原地顶住

        // 推得动：箱子开始滑，玩家从这一帧起跟着它一起走（见 CarryingCrate / StepSlide）。
        // 平推不放音效 —— 落地才响。
        crate.BeginSlide(tx, ty);
        _carryCrate = crate;
        _carryDir = dir;
    }

    /// <summary>
    /// 正前方够得着的那只箱子（最靠近身体的那只）。
    /// 判定是纯几何的：身体边缘到箱面的距离 ≤ <see cref="PushReach"/>，并且纵向压着它。
    /// </summary>
    Crate? CrateInFront(int dir)
    {
        var window = WindowFor(Player.Piece);
        if (window == null) return null;

        Crate? best = null;
        float bestGap = float.MaxValue;

        foreach (var c in Level.Crates)
        {
            if (!TryCrateRect(window, c, Player.Pos.X, Player.Pos.Y, out float cl, out float ct)) continue;
            if (!EntityCollision.ReachesCrate(dir, Player.Pos.X, Player.Pos.Y,
                    Player.Width, Player.Height, cl, ct, PushReach, out float gap)) continue;

            if (gap < bestGap) { bestGap = gap; best = c; }
        }

        return best;
    }

    /// <summary>
    /// 水平方向被挡住时调用：如果前面是箱子，就把身体贴到箱面上。
    ///
    /// 两个作用：① 看起来真的是"贴着推"；② 推箱判定不再受每帧位移步长的影响 ——
    /// 不贴的话，角色会停在离箱面 0~0.167 格的任意位置，判定就得靠容差去猜。
    /// 只被地形挡住时不动（贴墙是另一回事，改了就动了原来的手感）。
    ///
    /// <paramref name="blocked"/> 是"被挡住的那一步"想走到的位置。只有**这一步真的会撞进它**的
    /// 箱子才配当贴齐目标 —— 按"离得最近"去找会出大错：身后的箱子 gap 是个很大的负数，
    /// 角色撞到任意一堵墙都会被它"吸"过去，瞬移好几格（真出过这个 bug）。
    /// </summary>
    void SnapFlushToCrate(Vector2 blocked, int dir)
    {
        var window = WindowFor(Player.Piece);
        if (window == null) return;

        float snapX = 0f;
        bool found = false;

        foreach (var c in Level.Crates)
        {
            if (c.Busy) continue;   // 正在滑 / 正在掉：别往它身上贴
            if (!TryCrateRect(window, c, Player.Pos.X, Player.Pos.Y, out float cl, out float ct)) continue;

            // 挡路的就是它吗？（这一步挪过去之后身体会不会压进它）
            if (!EntityCollision.BlocksStep(Player.Width, Player.Height,
                    blocked.X, blocked.Y, cl, ct)) continue;

            float want = EntityCollision.FlushX(dir, Player.Width, cl);

            // 兜底：贴齐最多挪一步的距离。挪得更多说明状态本来就不对，宁可不贴。
            if (MathF.Abs(want - Player.Pos.X) > 1f) continue;

            snapX = want;
            found = true;
            break;
        }

        if (!found) return;

        var target = new Vector2(snapX, Player.Pos.Y);
        if (TerrainCollides(target)) return;   // 贴过去会插进墙里，那就算了
        Player.Pos = target;
    }

    /// <summary>
    /// 推箱补间：一记快推 + 一小段贴格，走完落到整数格上；玩家同速跟着走。
    /// 到位**不做压扁/回弹** —— 推的时候那一下"弹"看着像弹簧。
    /// </summary>
    void StepSlide(Crate c, float dt)
    {
        c.SlideT += dt;
        float u = Math.Clamp(c.SlideT / CrateSlideTime, 0f, 1f);
        float e = SlideEase(u);

        c.X = c.FromX + (c.ToX - c.FromX) * e;
        c.Y = c.FromY + (c.ToY - c.FromY) * e;

        // 人被箱子带着走：把位置**钉**在箱子实际的边上。
        //
        // 不能写成"人按自己的速度走 + 再加一个箱子位移增量"：箱子一开推就把目标格占住了
        // （BeginSlide 里 CellX 先挪过去），原来的格子当场空出来，于是人会先自己走进那个空位、
        // 再被增量推进箱子里面。一旦身体压进箱子，横向怎么走都是"撞" —— 看起来就是推不动。
        if (c == _carryCrate && CarryingCrate && Player.Piece == c.Piece)
        {
            float want = EntityCollision.FlushX(_carryDir, Player.Width, c.X);

            // 被箱子顶着走，但别被顶进墙里（顶到墙就停下来，箱子自己滑完）
            if (!TerrainCollides(new Vector2(want, Player.Pos.Y)))
                Player.Pos = new Vector2(want, Player.Pos.Y);
        }

        if (u < 1f) return;

        c.CommitCell();
        if (c == _carryCrate) _carryCrate = null;
    }

    /// <summary>
    /// 推动曲线：前 72% 的时间里走完 93%（三次缓出，起步就有速度、越走越慢），
    /// 剩下 28% 用二次缓出补最后 7% —— 速度在 72% 处会重新"提"一下，那一下就是顿挫感。
    /// </summary>
    static float SlideEase(float u)
    {
        if (u < 0.72f)
        {
            float k = u / 0.72f;
            return 0.93f * (1f - MathF.Pow(1f - k, 3f));
        }
        float k2 = (u - 0.72f) / 0.28f;
        return 0.93f + 0.07f * (1f - MathF.Pow(1f - k2, 2f));
    }

    /// <summary>
    /// 下落：逐小步往下探，箱底压到的那一行挡人就在它的上沿停住 —— 落点一定是整数格。
    ///
    /// 窗口滚动自由（没有邻居窗口）时地图是环形的：格子查表会绕回去（<c>KindAt</c> 自己 wrap），
    /// 所以箱子可以一直掉、绕地图一周。**视觉坐标 Y 故意不折回** —— 玩家的 Y 也不折回，
    /// 两者必须用同一个坐标系，否则会差出整数个地图高（人就"被拉出地图外"了）。
    /// 绘制那边按"离镜头最近的那一份"画，见 <c>GameWindow.DrawCrates</c>。
    /// </summary>
    void StepFall(Crate c, float dt)
    {
        c.VY = MathF.Min(c.VY + CrateGravity * dt, CrateMaxFallSpeed);

        float remain = c.VY * dt;
        while (remain > 1e-5f)
        {
            float step = MathF.Min(remain, 0.2f);
            remain -= step;

            float ny = c.Y + step;
            int row = (int)MathF.Floor(ny + 1f + 1e-4f);   // 箱底压到的那一行

            if (CrateCellBlocked(c.Piece, c.CellX, row, c))
            {
                c.Y = row - 1f;   // 贴着那一行的上沿，正好是整数格（不再往 0 夹：坐标是连续的，可以在地图上方）
                c.CellY = row - 1;
                c.VY = 0f;
                c.Falling = false;
                c.Impact = 1f;
                Audio.Play(Sfx.Bump);
                break;
            }

            c.Y = ny;
        }

        // 逻辑格跟着视觉位置走（连续坐标，**不折回**）：
        // 折回就等于把箱子问到地图另一头去，连通世界里那一格可能是"外面"，会被判成落地。
        c.CellY = (int)MathF.Floor(c.Y + 0.5f);
    }

    // ── 给绘制用的小查询 ──────────────────────────────────────────────────

    /// <summary>这个箱子正在蓄力顶的时候朝推的方向"压"了多少（-1..1）。</summary>
    public float PushLean(Crate c)
    {
        if (c != _pushCrate || _pushCharge <= 0f) return 0f;
        float k = Math.Clamp(_pushCharge / PushChargeTime, 0f, 1f);
        return _pushDir * k * k;
    }
}
