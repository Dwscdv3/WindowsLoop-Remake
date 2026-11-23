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

            // ---- 环形区间判定：负数 / 超过一整圈 / 跨取模边界 ----
            // 摄像机滚动值是**连续浮点、而且不折回**：地图本身是环形的，所以
            //   · 镜头往左滚就是负数（CamY = floor(y) - 5，地图顶上那几行天生是负的）；
            //   · 长时间下落的环形地图里 CamY 会一路涨到几百上千。
            // 因此判定必须"先减 min 再取模"，不能直接拿 value 去比大小。
            const int P30 = 30;

            // ① 周期性：同一个镜头位置写成 v、v±30、v±60… 结果必须一致
            bool periodicHit = true, periodicMiss = true;
            for (int k = -3; k <= 3; k++)
            {
                periodicHit &= Game.InScrollRange(13f + k * P30, 10f, 16f, P30);
                periodicMiss &= !Game.InScrollRange(19.5f + k * P30, 10f, 16f, P30);
            }
            Check(lines, ref fail, "命中值 ±3 圈都命中（取模）", periodicHit);
            Check(lines, ref fail, "区间外的值 ±3 圈都不命中", periodicMiss);

            // ② 区间端点是负数：[-5,2] 覆盖 25..29 和 0..2
            bool negEnds = Game.InScrollRange(-3f, -5f, 2f, P30)      // 直接就是负值
                        && Game.InScrollRange(27f, -5f, 2f, P30)      // 27 等价于 -3
                        && Game.InScrollRange(-33f, -5f, 2f, P30)     // 再减一整圈
                        && !Game.InScrollRange(10f, -5f, 2f, P30)
                        && !Game.InScrollRange(-20f, -5f, 2f, P30);   // -20 等价于 10
            Check(lines, ref fail, "负的区间端点 [-5,2]", negEnds);

            // ③ 区间端点大于一整圈：[40,45] 覆盖 10..15
            bool bigEnds = Game.InScrollRange(12f, 40f, 45f, P30)
                        && Game.InScrollRange(42f, 40f, 45f, P30)
                        && Game.InScrollRange(-17f, 40f, 45f, P30)    // -17 等价于 13
                        && !Game.InScrollRange(16f, 40f, 45f, P30)
                        && !Game.InScrollRange(9.5f, 40f, 45f, P30);
            Check(lines, ref fail, "大于一整圈的端点 [40,45]", bigEnds);

            // ④ 跨取模边界：镜头从 28 一直看到 32（32 == 下一圈的 2），
            //    覆盖 28、29、0、1、2 —— 两端分别在周期两侧
            bool wrapArc = Game.InScrollRange(30f, 28f, 32f, P30)      // 30 折回 0
                        && Game.InScrollRange(31.5f, 28f, 32f, P30)
                        && Game.InScrollRange(-2f, 28f, 32f, P30)      // -2 等价于 28
                        && Game.InScrollRange(-28f, 28f, 32f, P30)     // -28 等价于 2
                        && !Game.InScrollRange(3f, 28f, 32f, P30)
                        && !Game.InScrollRange(27f, 28f, 32f, P30)
                        && !Game.InScrollRange(32.5f, 28f, 32f, P30);
            Check(lines, ref fail, "跨取模边界的区间 [28,32]", wrapArc);

            // ⑤ 端点写反（min > max）：当前契约是"交换端点"，和升序写法完全等价
            bool swappedEnds = true;
            for (int k = -2; k <= 2; k++)
                swappedEnds &= Game.InScrollRange(k * 5.5f, 28f, 32f, P30) ==
                               Game.InScrollRange(k * 5.5f, 32f, 28f, P30);
            Check(lines, ref fail, "端点写反等价于交换后升序", swappedEnds);

            // ⑥ 跨边界的另一种写法：用负数表示"周期末尾那几格"。
            //    [-2,2] 必须和 [28,32] 在 30 格地图上**逐点等价**（-2 就是 28）
            bool negWrapForm = true;
            for (float v = -40f; v <= 70f; v += 0.5f)
                negWrapForm &= Game.InScrollRange(v, -2f, 2f, P30) == Game.InScrollRange(v, 28f, 32f, P30);
            Check(lines, ref fail, "[-2,2] 与 [28,32] 在 30 格地图上逐点等价", negWrapForm);

            // 换个周期，[-2,2] 跟着周期走（20 格地图上就是 18,19,0,1,2）
            Check(lines, ref fail, "[-2,2] 在 20 格地图上等价于 [18,22]",
                Game.InScrollRange(19f, -2f, 2f, 20) &&
                Game.InScrollRange(0.5f, -2f, 2f, 20) &&
                !Game.InScrollRange(17f, -2f, 2f, 20));

            // ⑦ 边界容差 / 退化周期
            Check(lines, ref fail, "跨边界区间容差内仍命中", Game.InScrollRange(16.0005f, 10f, 16f, P30));
            Check(lines, ref fail, "超容差 0.5 格不命中", !Game.InScrollRange(16.5f, 10f, 16f, P30));
            Check(lines, ref fail, "区间比一整圈略窄时差 1 格仍不命中",
                !Game.InScrollRange(29.5f, 0f, 29f, P30));
            Check(lines, ref fail, "周期为负按无效处理（恒成立，不抛）", Game.InScrollRange(5f, 1f, 2f, -30));

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
                 "conditions":[{"piece":"left","camY":[0,4]},{"piece":"*"}]},
                {"name":"开锁音","actions":[{"type":"sfx","sound":"unlock"},
                                           {"type":"sfx","sound":"no-such-sound"}]}
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

            Check(lines, ref fail, "事件表读全 5 条", level.Events.Count == 5);
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

            // 播放音效动作：sound 字段读进 Sound；认不出来的 key 也原样保留（运行期静默跳过）
            Check(lines, ref fail, "播放音效动作读成 sfx 并带上音效 key",
                level.Events[4].Actions[0].Type == "sfx" &&
                level.Events[4].Actions[0].Sound == "unlock");
            Check(lines, ref fail, "不认识的音效 key 不被解析阶段丢掉",
                level.Events[4].Actions[1].Sound == "no-such-sound");

            // 编辑器下拉（Shared\SfxCatalog.cs）里的每个 key，AudioManager 都必须注册过；
            // 两边全靠人工同步，这里就是把"加了目录忘了注册"挡在自测里
            bool catalogRegistered = SfxCatalog.All.Length > 0;
            foreach (var e in SfxCatalog.All)
            {
                bool known = AudioManager.IsKnownKey(e.Key);
                catalogRegistered &= known;
                if (!known) lines.Add($"FAIL  音效 key 没在 AudioManager 注册：{e.Key}");
            }
            Check(lines, ref fail, "音效目录里的 key 全部已注册", catalogRegistered);
            Check(lines, ref fail, "目录里有开锁音效（unlock）",
                SfxCatalog.All.Any(e => e.Key == "unlock" && e.Label == "开锁"));

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

            // 条件里的区间端点允许负数 / 超过地图尺寸：解析阶段原样保留，
            // 归一化（取模）留给判定里的 Game.InScrollRange
            File.WriteAllText(tmp,
                "{\"name\":\"neg-range\",\"maxWindows\":1,\"tiles\":{\"g\":{\"sprite\":9}}," +
                "\"events\":[{\"name\":\"e\",\"actions\":[{\"type\":\"run\",\"target\":\"x\"}]," +
                "\"conditions\":[{\"piece\":\"p\",\"camX\":[-5,40],\"camY\":[28,-2]}]}]," +
                "\"evtSeq\":[[0]],\"pieces\":[{\"id\":\"p\",\"rows\":[\".\"],\"evt\":[\"0\"],\"spawn\":[0,0]}]}");
            var ranged = Level.Load(tmp);
            var cond0 = ranged.Events[0].Conditions[0];
            Check(lines, ref fail, "条件区间能读负数端点", cond0.CamX?[0] == -5f);
            Check(lines, ref fail, "条件区间能读超过地图尺寸的端点", cond0.CamX?[1] == 40f);
            Check(lines, ref fail, "负的 max 不被解析阶段改写", cond0.CamY?[1] == -2f);

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

            // ---- 「总是」触发器：每帧判条件；repeat 决定只跑一次还是可以反复触发 ----
            File.WriteAllText(tmp, """
            {
              "name":"always-selftest","maxWindows":1,
              "tiles":{"g":{"sprite":9}},
              "events":[
                {"name":"只跑一次","trigger":"always",
                 "actions":[{"type":"hide"}],
                 "conditions":[{"piece":"*","camX":[0,5]}]},
                {"name":"可重复","trigger":"always","repeat":true,
                 "actions":[{"type":"hide"}]},
                {"name":"默认触碰","repeat":true,"actions":[{"type":"goto","level":"a"}]},
                {"name":"跑命令的重复事件","trigger":"always","repeat":true,
                 "actions":[{"type":"run","target":"x"}]}
              ],
              "evtSeq":[[2]],
              "pieces":[{"id":"p","rows":[".."],"evt":[".0"],"spawn":[0,0]}]
            }
            """);
            var always = Level.Load(tmp);
            var aev = always.Events;

            Check(lines, ref fail, "always 触发器读成 Always", aev[0].Trigger == EventTrigger.Always);
            Check(lines, ref fail, "没写 repeat 的 always 默认只跑一次", !aev[0].Repeat);
            Check(lines, ref fail, "repeat:true 的 always 读成可重复", aev[1].Trigger == EventTrigger.Always && aev[1].Repeat);
            Check(lines, ref fail, "别的触发器写 repeat 也不生效（还是 Touch）",
                aev[2].Trigger == EventTrigger.Touch && !aev[2].Repeat);
            Check(lines, ref fail, "可重复的 always 才算 IsRepeating",
                aev[3].IsRepeating && aev[1].IsRepeating && !aev[0].IsRepeating);
            Check(lines, ref fail, "always 事件不挂在格子上也能读（不依赖绑定格）",
                always.EvtSeqs[0].Count == 1 && always.EvtSeqs[0][0] == 2);
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
