namespace WindowsLoop;

/// <summary>
/// 实体碰撞里"纯几何"的那部分：矩形相交、朝某方向够不够得着、贴齐时该在哪。
///
/// 这里不碰地图、窗口、瓦片，只有数学 —— 所以能脱离游戏直接自测（见 PushSelfTest）。
///
/// 为什么要单独拎出来：老版本判断"人贴没贴住箱子"用的是"从身体边缘往外撒一个 0.10 格的探针"，
/// 而角色每帧最多走 Speed 10 × 1/60 ≈ 0.167 格 —— 走到箱子边上被挡住时，
/// 身体和箱面之间会留 0 ~ 0.167 格的缝。缝一旦超过 0.10，探针就落在箱子外面，
/// 于是"明明贴着箱子却推不动"，而且是随机复现（取决于停下的那一下的相位）。
///
/// 正确做法是量距离：身体边缘到箱面还剩多少，比完再决定够不够得着。
/// 这个距离和帧率、步长、停在哪儿都无关，所以判定是稳定的。
/// </summary>
internal static class EntityCollision
{
    /// <summary>浮点比较的容差（格）。贴齐时两边正好相等，不能算重叠。</summary>
    public const float Eps = 0.001f;

    /// <summary>两个 AABB 是否相交。**边界刚好相接不算相交** —— 贴着箱面站立/推动都靠这一条。</summary>
    public static bool Overlap(
        float ax, float ay, float aw, float ah,
        float bx, float by, float bw, float bh) =>
        ax + aw > bx + Eps && bx + bw > ax + Eps &&
        ay + ah > by + Eps && by + bh > ay + Eps;

    /// <summary>
    /// 站在 (cx,cy) 的角色，朝 <paramref name="dir"/> 伸手 <paramref name="reach"/> 格，
    /// 够不够得着左边在 <paramref name="crateLeft"/>、顶面在 <paramref name="crateTop"/> 的那只 1×1 箱子。
    /// 够得着时给出身体边缘到箱面的距离 <paramref name="gap"/>（≥ 0，0 = 正好贴住）。
    ///
    /// 三条要求：
    ///   ① 纵向真的压着这只箱子（上下边刚好相接不算 —— 站在箱顶上不能推它自己）；
    ///   ② 箱子在身体的前方，不是脚下也不是身后；
    ///   ③ 中间那段空隙不超过 reach。
    /// </summary>
    public static bool ReachesCrate(
        int dir,
        float playerCx, float playerCy, float playerW, float playerH,
        float crateLeft, float crateTop, float reach, out float gap)
    {
        gap = 0f;

        float l = playerCx - playerW / 2f, r = playerCx + playerW / 2f;
        float t = playerCy - playerH / 2f, b = playerCy + playerH / 2f;

        if (b <= crateTop + Eps || t >= crateTop + 1f - Eps) return false;

        gap = dir > 0 ? crateLeft - r : l - (crateLeft + 1f);
        return gap >= -Eps && gap <= reach;
    }

    /// <summary>
    /// 朝 <paramref name="dir"/> 推的时候，身体边缘正好贴着箱面应该在的 X。
    /// 往右推贴箱子左边，往左推贴箱子右边。
    /// </summary>
    public static float FlushX(int dir, float playerW, float crateLeft) =>
        dir > 0 ? crateLeft - playerW / 2f : crateLeft + 1f + playerW / 2f;

    /// <summary>
    /// 这一步被挡住了：挡路的到底是不是这只箱子？
    ///
    /// 口径是"身体挪到 <paramref name="blockedCx"/>/<paramref name="blockedCy"/> 之后，
    /// 是不是真的和这只箱子的矩形重叠"。只有这种箱子才值得贴齐。
    ///
    /// **不能用"距离最近"之类的宽松条件**：身后的箱子算出来的 gap 是个很大的负数，
    /// 一旦拿它当贴齐目标，角色撞到任意一堵墙都会瞬移到那只箱子旁边（真出过这个 bug）。
    /// </summary>
    public static bool BlocksStep(
        float playerW, float playerH, float blockedCx, float blockedCy,
        float crateLeft, float crateTop) =>
        Overlap(blockedCx - playerW / 2f, blockedCy - playerH / 2f, playerW, playerH,
                crateLeft, crateTop, 1f, 1f);
}
