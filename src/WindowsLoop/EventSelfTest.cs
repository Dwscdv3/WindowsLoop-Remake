namespace WindowsLoop;

/// <summary>
/// 无界面自测：WindowsLoop.exe --event-selftest
/// 只验纯数据 / 纯算法的部分——关卡 JSON 里的事件表怎么读、
/// 以及「摄像机滚动落在某个区间」在环形地图上的判定口径。
/// 触碰触发涉及窗口和角色位置，交给实测。
/// 结果写到 %TEMP%\windowsloop-game-event-selftest.txt。
/// </summary>
internal static class EventSelfTest
{
    public static void Run()
    {
        var lines = new List<string>();
        int fail = 0;
        string tmp = Path.Combine(Path.GetTempPath(), "wl-event-selftest.json");

        try
        {
            // ---- 环形区间判定 ----
            // 地图宽 30 格，区间 [10,16]；镜头绕一圈回到 0 附近时用 30+ 的值表示
            Check(lines, ref fail, "区间内命中", Game.InScrollRange(13f, 10f, 16f, 30));
            Check(lines, ref fail, "区间左端算命中", Game.InScrollRange(10f, 10f, 16f, 30));
            Check(lines, ref fail, "区间右端算命中", Game.InScrollRange(16f, 10f, 16f, 30));
            Check(lines, ref fail, "区间外不命中", !Game.InScrollRange(17.5f, 10f, 16f, 30));
            Check(lines, ref fail, "绕一圈回来仍在区间内", Game.InScrollRange(-17f, 10f, 16f, 30) == true);
            Check(lines, ref fail, "min > max 自动换向", Game.InScrollRange(13f, 16f, 10f, 30));

            // 跨零点的区间：[28,32] 是一个覆盖 28,29,0,1,2 的窗口
            Check(lines, ref fail, "跨零点区间命中 0", Game.InScrollRange(0f, 28f, 32f, 30));
            Check(lines, ref fail, "跨零点区间命中 29", Game.InScrollRange(29f, 28f, 32f, 30));
            Check(lines, ref fail, "跨零点区间不命中 5", !Game.InScrollRange(5f, 28f, 32f, 30));

            // 区间比一整圈还宽 = 随便什么位置都成立
            Check(lines, ref fail, "区间覆盖整圈时恒成立", Game.InScrollRange(123f, 0f, 40f, 30));
            Check(lines, ref fail, "地图尺寸为 0 时不炸", Game.InScrollRange(5f, 1f, 2f, 0));

            // ---- 关卡 JSON：事件表 + evt 层 ----
            string json = """
            {
              "name":"event-selftest","maxWindows":2,
              "tiles":{"g":{"sprite":9,"solid":true}},
              "events":[
                {"name":"去第二关","trigger":"touch",
                 "actions":[{"type":"goto","level":"level2"},
                            {"type":"run","target":"https://example.com"}],
                 "conditions":[{"piece":"*","camX":[10,16]},
                               {"piece":"left","camX":[0,6],"camY":[1.5,8]}]},
                {"trigger":"touch","actions":[{"type":"goto"}]},
                {"name":"草稿","actions":[{"type":"whatever"}]},
                {"actions":[{"type":"goto","level":"x"}],
                 "conditions":[{"piece":"left","camY":[0,4]},{"piece":"*"}]}
              ],
              "evtSeq":[[0,1],[2],[3]],
              "pieces":[
                {"id":"left","rows":["....","...."],"evt":["0...","..1."],"spawn":[0,0]},
                {"id":"right","rows":["....","...."],"evt":["2..."],"spawn":[0,0]}
              ]
            }
            """;
            File.WriteAllText(tmp, json);
            var level = Level.Load(tmp);

            Check(lines, ref fail, "事件表读全 4 条", level.Events.Count == 4);
            Check(lines, ref fail, "事件组读全 3 组", level.EvtSeqs.Count == 3);
            Check(lines, ref fail, "一组可以挂多条事件",
                level.EvtSeqs[0].Count == 2 && level.EvtSeqs[0][0] == 0 && level.EvtSeqs[0][1] == 1);

            Check(lines, ref fail, "跳转动作与目标关卡",
                level.Events[0].Actions.Count == 2 &&
                level.Events[0].Actions[0].Type == "goto" &&
                level.Events[0].Actions[0].Level == "level2");
            Check(lines, ref fail, "执行命令动作带目标",
                level.Events[0].Actions[1].Type == "run" &&
                level.Events[0].Actions[1].Target == "https://example.com");

            Check(lines, ref fail, "条件读全 2 条", level.Events[0].Conditions.Count == 2);
            Check(lines, ref fail, "条件地图块 + X 区间", level.Events[0].Conditions[0].Piece == "*" &&
                                                       level.Events[0].Conditions[0].CamX is { Length: 2 });
            Check(lines, ref fail, "按地图块的条件带 X/Y 两轴", level.Events[0].Conditions[1].Piece == "left" &&
                                                          level.Events[0].Conditions[1].CamY?[0] == 1.5f);
            Check(lines, ref fail, "只写一个轴的条件也能读", level.Events[3].Conditions.Count == 1 &&
                                                       level.Events[3].Conditions[0].CamX == null &&
                                                       level.Events[3].Conditions[0].CamY?[1] == 4f);
            Check(lines, ref fail, "一个轴都没写的条件被丢掉", level.Events[3].Conditions.Count == 1);

            Check(lines, ref fail, "目标留空 = 有动作但没目标", level.Events[1].Actions.Count == 1 &&
                                                            level.Events[1].Actions[0].Type == "goto" &&
                                                            level.Events[1].Actions[0].Level == "");
            Check(lines, ref fail, "没写 actions 的事件动作为空", level.Events[2].Actions.Count == 0 ||
                                                            level.Events[2].Actions[0].Type == "whatever");

            var left = level.Pieces[0];
            Check(lines, ref fail, "evt 层取到事件组", left.EvtSeqAt(0, 0) == 0 && left.EvtSeqAt(2, 1) == 1);
            Check(lines, ref fail, "没有事件的格子是 -1", left.EvtSeqAt(1, 0) == -1);
            Check(lines, ref fail, "evt 层按环形取格", left.EvtSeqAt(4, 0) == 0);
            Check(lines, ref fail, "别的地图块的事件组", level.Pieces[1].EvtSeqAt(0, 0) == 2);

            // 旧格式（没有 evtSeq、action / when 单数）要能等价读进来
            File.WriteAllText(tmp,
                "{\"name\":\"legacy\",\"maxWindows\":1,\"tiles\":{\"g\":{\"sprite\":9}}," +
                "\"events\":[{\"action\":{\"type\":\"goto\",\"level\":\"level2\"}," +
                "\"when\":[{\"piece\":\"*\",\"camX\":[0,5]}]}," +
                "{\"action\":{\"type\":\"goto\",\"level\":\"main\"}}]," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\"gg\"],\"evt\":[\"1.\"],\"spawn\":[0,0]}]}");
            var legacy = Level.Load(tmp);
            Check(lines, ref fail, "旧格式 action 读成单动作",
                legacy.Events[0].Actions.Count == 1 && legacy.Events[0].Actions[0].Level == "level2");
            Check(lines, ref fail, "旧格式 when 读成 conditions",
                legacy.Events[0].Conditions.Count == 1 && legacy.Events[0].Conditions[0].CamX?[1] == 5f);
            Check(lines, ref fail, "没有 evtSeq 时 evt 层按事件下标解释",
                legacy.EvtSeqs.Count == 2 && legacy.Pieces[0].EvtSeqAt(0, 0) == 1 &&
                legacy.EvtSeqs[1].Count == 1 && legacy.EvtSeqs[1][0] == 1);

            // 没有 events 字段的旧关卡：事件表 / 组表为空，不能抛
            File.WriteAllText(tmp,
                "{\"name\":\"old\",\"maxWindows\":1,\"tiles\":{\"g\":{\"sprite\":9,\"solid\":true}}," +
                "\"pieces\":[{\"id\":\"p\",\"rows\":[\"gg\"],\"spawn\":[0,0]}]}");
            var old = Level.Load(tmp);
            Check(lines, ref fail, "旧关卡没有事件表也能读",
                old.Events.Count == 0 && old.EvtSeqs.Count == 0 && old.Pieces[0].EvtSeqAt(0, 0) == -1);

            // ---- 压力板触发器 + 隐藏操作 ----
            File.WriteAllText(tmp, """
            {
              "name":"plate-selftest","maxWindows":1,
              "tiles":{"P":{"sprite":69},"w":{"sprite":9,"solid":true}},
              "events":[
                {"name":"开门","trigger":"plate","plate":{"piece":"p","x":3,"y":2},"actions":[{"type":"hide"}]},
                {"name":"跟绑定格","trigger":"plate","plate":{"x":5,"y":6},"actions":[{"type":"hide"}]},
                {"name":"没坐标","trigger":"plate","actions":[{"type":"run","target":"x"}]},
                {"name":"又藏又跳","trigger":"touch","actions":[{"type":"hide"},{"type":"goto","level":"a"}]}
              ],
              "evtSeq":[[0],[1],[3]],
              "pieces":[
                {"id":"p","rows":["....","...."],"evt":[".0..","..1."],"spawn":[0,0]}
              ]
            }
            """);
            var plate = Level.Load(tmp);
            var pev = plate.Events;

            Check(lines, ref fail, "压力板触发器读成 Plate", pev[0].Trigger == EventTrigger.Plate);
            Check(lines, ref fail, "压力板坐标读到（地图块 + x,y）",
                pev[0].PlatePiece == "p" && pev[0].PlateX == 3 && pev[0].PlateY == 2);
            Check(lines, ref fail, "地图块留空 = 跟绑定格所在的那块",
                pev[1].PlatePiece == "" && pev[1].PlateX == 5 && pev[1].PlateY == 6);
            Check(lines, ref fail, "没填坐标的板事件仍是 Plate 但坐标非法（永不激活）",
                pev[2].Trigger == EventTrigger.Plate && pev[2].PlateX < 0);
            Check(lines, ref fail, "hide 动作被认成隐藏而不是一次性动作",
                pev[0].Hides && !pev[0].OneShots.Any());
            Check(lines, ref fail, "一条事件里 hide 和 goto 能共存，只有 goto 算一次性",
                pev[3].Hides && pev[3].OneShots.Count() == 1 && pev[3].OneShots.First().Type == "goto");
            Check(lines, ref fail, "69 号瓦片读成压力板",
                TileSet.Defs[TileSet.IdOf('P')].Plate);
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
        string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-game-event-selftest.txt");
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
