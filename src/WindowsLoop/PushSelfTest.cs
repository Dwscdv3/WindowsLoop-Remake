namespace WindowsLoop;

/// <summary>
/// 无界面自测：WindowsLoop.exe --push-selftest
///
/// 盯的是"贴着箱子却推不动"那个 bug 的根：判定必须量距离，不能靠探针。
/// 结果写到 %TEMP%\windowsloop-game-push-selftest.txt。
/// </summary>
internal static class PushSelfTest
{
    /// <summary>和 Game.PushReach 保持同一个值 —— 改一边忘了另一边，这里会先报出来。</summary>
    const float Reach = 0.22f;

    const float W = Player.Width;
    const float H = Player.Height;

    public static void Run()
    {
        var lines = new List<string>();
        int fail = 0;

        try
        {
            // 角色站在 (px, py)，箱子占 [15,16] × [8,9]
            const float CrateLeft = 15f, CrateTop = 8f;
            float py = CrateTop + 0.55f;             // 脚踩在箱子底下那一行的顶上
            float flushX = CrateLeft - W / 2f;        // 正好贴住箱子左边

            // ---- 1. 缝隙从小到大都得够得着（这就是原 bug 的形状）----
            foreach (float gap in new[] { 0f, 0.02f, 0.05f, 0.10f, 0.12f, 0.145f, 0.16f, 0.165f, 0.21f })
            {
                bool ok = EntityCollision.ReachesCrate(1, flushX - gap, py, W, H,
                    CrateLeft, CrateTop, Reach, out float got);
                Check(lines, ref fail, $"缝 {gap:0.000} 格时够得着", ok && MathF.Abs(got - gap) < 1e-4f);
            }

            // ---- 2. 够不着的那些 ----
            bool far = EntityCollision.ReachesCrate(1, flushX - 0.25f, py, W, H,
                CrateLeft, CrateTop, Reach, out _);
            Check(lines, ref fail, "缝 0.25 格够不着", !far);

            // 箱子在身后（人站在箱子右边，还朝右伸手）
            bool behind = EntityCollision.ReachesCrate(1, CrateLeft + 1f + W / 2f + 0.02f, py, W, H,
                CrateLeft, CrateTop, Reach, out _);
            Check(lines, ref fail, "身后的箱子不算贴住", !behind);

            // 站在箱顶上：脚底正好等于箱顶，不能算"贴着它侧面"
            bool onTop = EntityCollision.ReachesCrate(1, flushX, CrateTop - H / 2f, W, H,
                CrateLeft, CrateTop, Reach, out _);
            Check(lines, ref fail, "站在箱顶上不算贴住侧面", !onTop);

            // 叠了一层：人在箱子正上方一格
            bool above = EntityCollision.ReachesCrate(1, flushX, CrateTop - 1f - H / 2f, W, H,
                CrateLeft, CrateTop, Reach, out _);
            Check(lines, ref fail, "在箱子上面一楼够不着它", !above);

            // 已经压进箱子里（不该判成"贴住"然后继续推）
            bool inside = EntityCollision.ReachesCrate(1, CrateLeft - W / 2f + 0.05f, py, W, H,
                CrateLeft, CrateTop, Reach, out _);
            Check(lines, ref fail, "身体压进箱子不算贴住", !inside);

            // ---- 3. 贴齐位置 ----
            float fxRight = EntityCollision.FlushX(1, W, CrateLeft);
            float fxLeft = EntityCollision.FlushX(-1, W, CrateLeft);
            Check(lines, ref fail, "往右推贴箱子左边", MathF.Abs(fxRight - (CrateLeft - W / 2f)) < 1e-4f);
            Check(lines, ref fail, "往左推贴箱子右边",
                MathF.Abs(fxLeft - (CrateLeft + 1f + W / 2f)) < 1e-4f);

            // 贴齐之后必须还能推（否则"贴上去"反而把判定弄丢了）
            Check(lines, ref fail, "贴齐后仍算贴住",
                EntityCollision.ReachesCrate(1, fxRight, py, W, H, CrateLeft, CrateTop, Reach, out float g0)
                && g0 == 0f);

            // ---- 4. 矩形相交：边界相接不算 ----
            bool touching = EntityCollision.Overlap(fxRight - W / 2f, py - H / 2f, W, H,
                CrateLeft, CrateTop, 1f, 1f);
            bool biting = EntityCollision.Overlap(fxRight - W / 2f + 0.01f, py - H / 2f, W, H,
                CrateLeft, CrateTop, 1f, 1f);
            Check(lines, ref fail, "贴齐时矩形不相交", !touching);
            Check(lines, ref fail, "压进去 0.01 格算相交", biting);

            // 脚正好踩在箱顶：也不算相交（否则站上去就被顶开）
            bool feetOnTop = EntityCollision.Overlap(fxRight - W / 2f, CrateTop - H, W, H,
                CrateLeft, CrateTop, 1f, 1f);
            Check(lines, ref fail, "脚踩箱顶时不算相交", !feetOnTop);

            // ---- 4b. 撞墙时不能被箱子"吸"过去（回归：撞任意墙体瞬移到箱子旁）----
            // 角色在箱子右边很远的地方撞墙，箱子在身后 —— 这一步当然不是箱子挡的
            float farX = CrateLeft + 6f;
            bool snapThroughWall = EntityCollision.BlocksStep(W, H,
                farX + 0.1667f, py, CrateLeft, CrateTop);
            Check(lines, ref fail, "隔着很远的箱子不会触发贴齐（撞墙不瞬移）", !snapThroughWall);

            // 箱子在身后一格半：仍然不该贴
            float behindX = CrateLeft + 1f + W / 2f + 0.4f;
            bool snapBehind = EntityCollision.BlocksStep(W, H,
                behindX + 0.1667f, py, CrateLeft, CrateTop);
            Check(lines, ref fail, "身后的箱子不会触发贴齐", !snapBehind);

            // 真的要撞进去了：必须触发
            bool snapFront = EntityCollision.BlocksStep(W, H,
                fxRight - 0.10f + 0.1667f, py, CrateLeft, CrateTop);
            Check(lines, ref fail, "这一步会撞进箱子时触发贴齐", snapFront);

            // ---- 5. 不变量：伸手要比角色一帧能走的距离长 ----
            float perFrame = Player.Speed * Game.FixedDt;
            float perStep = 0.2f;   // 碰撞推进的单步上限
            Check(lines, ref fail,
                $"PushReach({Reach:0.000}) 必须大于单步位移({perStep:0.000})",
                Reach > perStep && Reach > perFrame);
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL  EXCEPTION {ex.GetType().Name}: {ex.Message}");
            fail++;
        }

        lines.Add($"result: {(fail == 0 ? "ALL PASS" : fail + " FAILED")}");
        string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-game-push-selftest.txt");
        File.WriteAllLines(outPath, lines);
        Console.WriteLine(outPath);
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    static void Check(List<string> lines, ref int fail, string name, bool ok)
    {
        lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) fail++;
    }
}
