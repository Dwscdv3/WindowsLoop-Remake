using System.Text.Json;
using WindowsLoop.Tileset;

namespace WindowsLoop;

/// <summary>
/// 无界面自测：WindowsLoop.exe --tile-defaults-selftest
/// 验证游戏真的读到了瓦片工具的数据，且「关卡显式值 > 瓦片默认」的优先级没跑偏。
/// 结果写到 %TEMP%\windowsloop-game-tiledefaults-selftest.txt。
/// </summary>
internal static class TileDefaultsSelfTest
{
    public static void Run()
    {
        var lines = new List<string>();
        int fail = 0;
        string tmp = Path.Combine(Path.GetTempPath(), "wl-tile-defaults-selftest.json");

        try
        {
            // 和游戏启动走同一个入口：瓦片默认值是编在程序集里的，不再从磁盘找
            Assets.LoadEmbeddedTileDefaults();
            var meta = TileDefaults.Current;
            lines.Add("meta: " + TileDefaults.PathUsed);
            lines.Add($"registered={meta.Tiles.Count} solid={meta.Tiles.Count(t => t.Solid)}");
            bool expected8 = meta.Find(8)?.Solid ?? false;
            lines.Add($"sprite 8 solid (in meta) = {expected8}");

            int solidSprite = meta.Tiles.FirstOrDefault(t => t.Solid)?.Sprite ?? -1;
            int openSprite = meta.Tiles.FirstOrDefault(t => !t.Solid)?.Sprite ?? -1;
            int climbSprite = meta.Tiles.FirstOrDefault(t => t.Climb)?.Sprite ?? -1;
            lines.Add($"picked solid={solidSprite} open={openSprite} climb={climbSprite}");

            string json =
                "{\"name\":\"selftest\",\"maxWindows\":1," +
                "\"tiles\":{" +
                $"\"a\":{{\"sprite\":{solidSprite}}}," +                         // 没写 -> 跟随默认，应该实心
                $"\"b\":{{\"sprite\":{solidSprite},\"solid\":false}}," +        // 显式 false -> 关卡优先
                $"\"c\":{{\"sprite\":{openSprite}}}," +                          // 没写 -> 默认可穿过
                $"\"d\":{{\"sprite\":{openSprite},\"solid\":true}}," +          // 显式 true -> 关卡优先
                $"\"e\":{{\"sprite\":{climbSprite}}}," +                         // 没写 climb -> 跟随默认
                "\"h\":{\"sprite\":8}," +                                         // 固定观测点：跟着工具里的值走
                $"\"f\":{{\"sprite\":{climbSprite},\"climb\":false}}" +          // 显式关掉 climb
                "}," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\"abcdef\"],\"spawn\":[0,0]}]}";
            File.WriteAllText(tmp, json);

            var level = Level.Load(tmp);
            bool a = TileSet.Defs[TileSet.IdOf('a')].Solid;
            bool b = TileSet.Defs[TileSet.IdOf('b')].Solid;
            bool c = TileSet.Defs[TileSet.IdOf('c')].Solid;
            bool d = TileSet.Defs[TileSet.IdOf('d')].Solid;
            bool e = TileSet.Defs[TileSet.IdOf('e')].Climb;
            bool f = TileSet.Defs[TileSet.IdOf('f')].Climb;
            bool h = TileSet.Defs[TileSet.IdOf('h')].Solid;
            bool at = level.Pieces[0].SolidAt(0, 0);       // 逐格层仍是最高优先

            Check(lines, ref fail, "关卡未写 solid 时跟随瓦片默认（实心）", a);
            Check(lines, ref fail, "关卡显式 solid:false 覆盖默认", !b);
            Check(lines, ref fail, "关卡未写 solid 时跟随瓦片默认（可穿过）", !c);
            Check(lines, ref fail, "关卡显式 solid:true 覆盖默认", d);
            Check(lines, ref fail, "climb 同样按默认兜底", e, climbSprite >= 0);
            Check(lines, ref fail, "climb 显式 false 覆盖默认", !f);
            Check(lines, ref fail, "逐格 SolidAt 走的是 TileDef 结果", at == a);
            Check(lines, ref fail, "固定 sprite 8 的阻挡与元数据一致", h == expected8);

            // 多精灵图例按主贴图取默认：第一格不实心，第二格实心，也不能整块变墙
            string multi =
                "{\"name\":\"selftest\",\"maxWindows\":1," +
                $"\"tiles\":{{\"g\":{{\"sprites\":[{openSprite},{solidSprite}]}}}}," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\"gg\"],\"spawn\":[0,0]}]}";
            File.WriteAllText(tmp, multi);
            Level.Load(tmp);
            Check(lines, ref fail, "多精灵图例以主贴图为准", !TileSet.Defs[TileSet.IdOf('g')].Solid);

            // 单向平台：图例 platform:true 生效，逐格 '2' 覆盖，solid 压过 platform
            string plat =
                "{\"name\":\"selftest\",\"maxWindows\":1," +
                $"\"tiles\":{{\"t\":{{\"sprite\":{openSprite},\"platform\":true}}," +
                $"\"u\":{{\"sprite\":{openSprite}}}," +
                $"\"w\":{{\"sprite\":{openSprite},\"solid\":true,\"platform\":true}}}}," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\"tuwt\"],\"solid\":[\"0..2\"],\"spawn\":[0,0]}]}";
            File.WriteAllText(tmp, plat);
            var lv = Level.Load(tmp);
            var piece = lv.Pieces[0];

            bool legendPlatform = TileSet.Defs[TileSet.IdOf('t')].Platform;
            bool legendPlain = !TileSet.Defs[TileSet.IdOf('u')].Platform;
            bool cellPass = piece.KindAt(0, 0) == SolidKind.Pass;          // 逐格 '0' 关掉平台
            bool cellPlatform = piece.KindAt(3, 0) == SolidKind.Platform;   // 逐格 '2' 标成平台
            bool solidWins = piece.KindAt(2, 0) == SolidKind.Block;         // 跟随图例：solid 压过 platform
            bool notSolid = !piece.SolidAt(3, 0);
            bool notPlatform = !piece.PlatformAt(0, 0);

            Check(lines, ref fail, "图例 platform:true 让瓦片成为平台", legendPlatform);
            Check(lines, ref fail, "没写 platform 的图例不是平台", legendPlain);
            Check(lines, ref fail, "逐格 '0' 把平台关成可穿过", cellPass);
            Check(lines, ref fail, "逐格 '2' 把平台标成单向平台", cellPlatform);
            Check(lines, ref fail, "图例同时 solid+platform 时按实心算", solidWins);
            Check(lines, ref fail, "平台不算 SolidAt（横向 / 交接不挡）", notSolid && notPlatform);

            // ---- 压力板：69 号瓦片默认是板，图例也能显式改 ----
            bool plate69 = meta.Find(69)?.Plate ?? false;
            File.WriteAllText(tmp,
                "{\"name\":\"selftest\",\"maxWindows\":1," +
                "\"tiles\":{\"a\":{\"sprite\":69},\"b\":{\"sprite\":69,\"plate\":false}," +
                "\"c\":{\"sprite\":8,\"plate\":true}}," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\"abc\"],\"spawn\":[0,0]}]}");
            Level.Load(tmp);
            Check(lines, ref fail, "69 号瓦片默认是压力板", plate69);
            Check(lines, ref fail, "图例没写 plate 时跟随默认（是板）", TileSet.Defs[TileSet.IdOf('a')].Plate);
            Check(lines, ref fail, "图例显式 plate:false 覆盖默认（不是板）", !TileSet.Defs[TileSet.IdOf('b')].Plate);
            Check(lines, ref fail, "普通瓦片显式 plate:true 也是板", TileSet.Defs[TileSet.IdOf('c')].Plate);

            // ---- 箱子：solid 层写 '3' 的格子抽成实体，静态层让位 ----
            File.WriteAllText(tmp,
                "{\"name\":\"selftest\",\"maxWindows\":1," +
                "\"tiles\":{\"g\":{\"sprite\":9,\"solid\":true},\"P\":{\"sprite\":69}}," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\".g..\"]," +
                "\"solid\":[\"3.3.\"],\"spawn\":[1,0]}]}");
            var crateLevel = Level.Load(tmp);
            var cp = crateLevel.Pieces[0];
            bool spawns = cp.CrateSpawns.Count == 2 &&
                          cp.CrateSpawns[0] == new System.Drawing.Point(0, 0) &&
                          cp.CrateSpawns[1] == new System.Drawing.Point(2, 0);
            bool entities = crateLevel.Crates.Count == 2 &&
                            crateLevel.Crates[0].CellX == 0 && crateLevel.Crates[1].CellX == 2;
            bool yielded = cp.KindAt(0, 0) == SolidKind.Pass;   // 静态层清成 255 → 跟瓦片（空气）
            bool notStatic = !cp.SolidAt(0, 0);
            Check(lines, ref fail, "solid 层 '3' 读成箱子（位置对）", spawns);
            Check(lines, ref fail, "读关卡时就建出箱子实体", entities);
            Check(lines, ref fail, "箱子格在静态图层里让位（不双重阻挡）", yielded && notStatic);

            // 推动 / 重开：ResetCrates 要把箱子摆回关卡里写的位置
            crateLevel.Crates[0].CellX = 5;
            crateLevel.Crates[0].Y = 3.7f;
            crateLevel.ResetCrates();
            Check(lines, ref fail, "ResetCrates 把箱子摆回原位",
                crateLevel.Crates[0].CellX == 0 && crateLevel.Crates[0].Y == 0f &&
                crateLevel.Crates[1].CellX == 2);

        }
        catch (Exception ex)
        {
            lines.Add($"FAIL  EXCEPTION {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        lines.Add($"result: {(fail == 0 ? "ALL PASS" : fail + " FAILED")}");
        string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-game-tiledefaults-selftest.txt");
        File.WriteAllLines(outPath, lines);
        Console.WriteLine(outPath);
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    static void Check(List<string> lines, ref int fail, string name, bool ok, bool applicable = true)
    {
        if (!applicable) return;
        lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) fail++;
    }
}
