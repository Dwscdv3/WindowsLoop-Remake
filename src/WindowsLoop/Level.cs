using System.Drawing;
using System.Text.Json;
using WindowsLoop.Tileset;

namespace WindowsLoop;

internal sealed class TileDef
{
    public int[] Sprites = Array.Empty<int>();
    public bool Solid;

    /// <summary>单向平台：下方能顶穿，上方能站立。Solid 为真时按实心算。</summary>
    public bool Platform;

    public bool Water;

    /// <summary>踩上去会把人弹起来的工具地形。</summary>
    public bool Spring;

    /// <summary>可以攀爬的工具地形（梯子）。</summary>
    public bool Climb;

    /// <summary>
    /// 压力板：玩家或箱子与这一格重叠（身体所在格 / 脚底那格）就算"踩下"。
    /// 它不改变阻挡，只是一条给事件触发器读的状态。
    /// </summary>
    public bool Plate;

    /// <summary>
    /// 取这一格用哪个 sprite。variant 为该格显式指定的下标（关卡 var 层），
    /// 没指定（-1）时退回老的坐标哈希。
    /// </summary>
    public int SpriteAt(int x, int y, int variant = -1)
    {
        if (Sprites.Length == 0) return -1;
        if (variant >= 0 && variant < Sprites.Length) return Sprites[variant];
        if (Sprites.Length == 1) return Sprites[0];
        int h = (x * 73856093) ^ (y * 19349663);
        return Sprites[(h & 0x7fffffff) % Sprites.Length];
    }
}

internal static class TileSet
{
    public const int SourceTile = 8;
    public const int SheetCols = 8;

    public static readonly List<TileDef> Defs = new();

    public static byte IdOf(char c) => _charToId.TryGetValue(c, out var id) ? id : (byte)0;

    static readonly Dictionary<char, byte> _charToId = new();

    public static void Reset()
    {
        Defs.Clear();
        _charToId.Clear();
        Defs.Add(new TileDef()); // id 0 = empty
        _charToId['.'] = 0;
        _charToId[' '] = 0;
    }

    public static void Register(char c, TileDef def)
    {
        _charToId[c] = (byte)Defs.Count;
        Defs.Add(def);
    }

    public static Rectangle SpriteRect(int sprite)
    {
        if (sprite < 0) return Rectangle.Empty;
        int cx = sprite % SheetCols, cy = sprite / SheetCols;
        return new Rectangle(cx * SourceTile, cy * SourceTile, SourceTile, SourceTile);
    }
}

internal sealed class Piece
{
    public string Id = "";
    public int Width, Height;
    public byte[] Cells = Array.Empty<byte>();

    /// <summary>
    /// 每格在 tiles 的 sprites 里取第几个；null = 全部走老哈希。
    /// 关卡里写成与 rows 等宽的字符串，'.' 表示"不指定"。
    /// </summary>
    public byte[]? Var;

    /// <summary>
    /// 每格的朝向变换。编码 = 旋转步数(0..3，每步 90°) + 水平镜像(0/4)，
    /// 即 D4 群的 8 个元素；0 = 原样。null = 全部原样。
    /// </summary>
    public byte[]? Xf;

    /// <summary>
    /// 每格是否阻挡：0 = 可穿过，1 = 实心，2 = 单向平台，3 = 箱子，255 = 跟随瓦片自带的 solid / platform。
    /// 阻挡是**格子**的属性而不是瓦片的——同一块瓦片可以这格挡人、那格不挡。
    /// 3（箱子）在 <see cref="Level.Parse"/> 里会被抽成 <see cref="Crate"/> 实体：
    /// 数据上它占这一格，运行时要动的是实体，静态层那一位随即清成 255。
    /// </summary>
    public byte[]? Solid;

    /// <summary>
    /// 关卡里写的那批箱子（solid 层为 3 的格子）。运行时箱子的位置归 <see cref="Level.Crates"/>，
    /// 这里只留"这一局开始时它们在哪"，按 R 重开时照这份重置。
    /// </summary>
    public readonly List<Point> CrateSpawns = new();

    /// <summary>
    /// 每格是否压暗绘制，对应以前的"背景层"。0 = 正常亮度，1 = 压暗。
    /// </summary>
    public byte[]? Dim;

    /// <summary>
    /// 每格挂的**事件组**下标（指向 Level.EvtSeqs）；255 = 这一格没有事件。
    /// 一格可以挂一串事件（按顺序逐个判定），所以这一层存的是"组"而不是直接的事件下标——
    /// 层格式保持"一格一个字符"，多事件靠组表表达。
    /// </summary>
    public byte[]? Evt;

    public Point Spawn;

    public byte At(int x, int y)
    {
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        return Cells[wy * Width + wx];
    }

    /// <summary>这一格显式指定的变体下标；没有就是 -1。</summary>
    public int VarAt(int x, int y)
    {
        if (Var == null) return -1;
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        byte v = Var[wy * Width + wx];
        return v == 255 ? -1 : v;
    }

    /// <summary>这一格的朝向变换（0..7），没有就是 0。</summary>
    public int XfAt(int x, int y)
    {
        if (Xf == null) return 0;
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        return Xf[wy * Width + wx];
    }

    /// <summary>
    /// 这一格的实际阻挡类型：逐格标记优先，没标就跟随图例 / 瓦片默认（实心优先于平台）。
    /// </summary>
    public SolidKind KindAt(int x, int y)
    {
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        int i = wy * Width + wx;

        if (Solid != null)
        {
            byte v = Solid[i];
            if (v == (byte)SolidKind.Block) return SolidKind.Block;
            if (v == (byte)SolidKind.Platform) return SolidKind.Platform;
            if (v == (byte)SolidKind.Crate) return SolidKind.Crate;
            if (v == (byte)SolidKind.Pass) return SolidKind.Pass;
        }

        var def = TileSet.Defs[Cells[i]];
        return def.Solid ? SolidKind.Block : def.Platform ? SolidKind.Platform : SolidKind.Pass;
    }

    /// <summary>
    /// 这一格挡不挡四面（实心）。单向平台不算，它只在下落时由 CollidesFalling 处理。
    /// 箱子也算 —— 它挡人和实心一样，只是会动；真正跑起来时箱子的位置由实体决定
    /// （见 <see cref="Level.Crates"/>），静态层里那一位读关卡时就已经清掉了。
    /// </summary>
    public bool SolidAt(int x, int y)
    {
        var k = KindAt(x, y);
        return k is SolidKind.Block or SolidKind.Crate;
    }

    /// <summary>这一格是不是压力板（瓦片自带的 plate 属性）。</summary>
    public bool PlateAt(int x, int y)
    {
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        return TileSet.Defs[Cells[wy * Width + wx]].Plate;
    }

    /// <summary>这一格是不是单向平台。</summary>
    public bool PlatformAt(int x, int y) => KindAt(x, y) == SolidKind.Platform;

    /// <summary>这一格挂的事件组下标（指向 Level.EvtSeqs）；没有就是 -1。</summary>
    public int EvtSeqAt(int x, int y)
    {
        if (Evt == null) return -1;
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        byte v = Evt[wy * Width + wx];
        return v == 255 ? -1 : v;
    }

    /// <summary>这一格是否压暗绘制。</summary>
    public bool DimAt(int x, int y)
    {
        if (Dim == null) return false;
        int wx = ((x % Width) + Width) % Width;
        int wy = ((y % Height) + Height) % Height;
        return Dim[wy * Width + wx] != 0;
    }
}

/// <summary>
/// 网格事件的触发方式。
///
/// Touch  —— 角色所在的那一格挂着事件就每帧判一次（"走进来"）。
/// Plate  —— 事件里写的那个压力板被踩住（玩家或箱子压在上面）时算激活；
///           松开就不激活。它读的是状态而不是"进过一次"，所以隐藏类操作能随时收回。
///
/// 以后要加"进入房间""按下某键"之类，在这里扩枚举 + UpdateEvents 里分派。
/// </summary>
internal enum EventTrigger
{
    Touch,
    Plate,
}

/// <summary>
/// 一条动作。对着任务计划程序里的"操作"：跳转到别的关卡，在电脑上打开程序 / 网址，
/// 或者把一个图块藏起来。一条事件可以挂多条，按顺序执行；某条把地图切走了，后面的就不再执行。
/// </summary>
internal sealed class EventActionDef
{
    /// <summary>
    /// "goto" = 跳转关卡，"run" = 执行命令 / 打开网址，
    /// "hide" = 隐藏事件绑定格的图块并且不再阻挡（这是**状态**而不是一次性动作：
    /// 事件不激活了，图块就回来）。
    /// </summary>
    public string Type = "";

    /// <summary>goto 的目标关卡（assets/levels 下的文件名，不带 .json）。</summary>
    public string Level = "";

    /// <summary>run 的目标：程序路径、网址，或任何 ShellExecute 认识的东西。</summary>
    public string Target = "";

    /// <summary>run 的命令行参数，可空。</summary>
    public string Args = "";
}

/// <summary>
/// 一条前置条件：某块地图的**摄像机滚动**（取景框左上角，单位格）必须落在给定区间里。
/// Piece 为 "*" 表示"所有开着的窗口都要满足"。
/// 区间按环形地图折算：从 min 起顺着正方向数，不超过 span 格都算命中。
/// </summary>
internal sealed class EventCondition
{
    public string Piece = "*";
    public float[]? CamX;
    public float[]? CamY;
}

/// <summary>
/// 一条网格事件，结构对着 Windows 任务计划程序：
/// 触发器（什么时候判）+ 动作（按顺序做什么）+ 条件（能不能做）。
/// 它不属于任何一块瓦片，只挂在格子上（Piece.Evt 层 → Level.EvtSeqs → 这里）。
/// </summary>
internal sealed class GridEvent
{
    public string Name = "";
    public EventTrigger Trigger = EventTrigger.Touch;

    /// <summary>
    /// Trigger 为 <see cref="EventTrigger.Plate"/> 时要盯的那块压力板。
    /// Piece 留空 = 用"事件绑定格"所在的那块地图（事件可能挂在好几块地图的格子上，
    /// 这时按每块链接算，见 Game.BuildPlateRefs）。
    /// </summary>
    public string PlatePiece = "";
    public int PlateX = -1;
    public int PlateY = -1;

    /// <summary>按顺序执行的动作。空表 = 什么都不做（草稿）。</summary>
    public List<EventActionDef> Actions = new();

    public List<EventCondition> Conditions = new();

    /// <summary>这条事件里有没有「隐藏绑定格」的操作 —— 有的话它绑定的格子要参与隐藏判定。</summary>
    public bool Hides => Actions.Exists(a => a.Type == "hide");

    /// <summary>只跑一次的动作（goto / run）。hide 是状态，不是一次性动作。</summary>
    public IEnumerable<EventActionDef> OneShots => Actions.Where(a => a.Type != "hide");
}

internal sealed class Level
{
    public string Name = "";
    public List<Piece> Pieces = new();

    /// <summary>本关的事件表；Piece.Evt 层里存的是"事件组"下标，组表再指向这里。</summary>
    public List<GridEvent> Events = new();

    /// <summary>
    /// 这一局里活着的箱子。位置由实体自己管，按 R 重开时从每个地图块的
    /// <see cref="Piece.CrateSpawns"/> 重新摆一遍（见 <see cref="ResetCrates"/>）。
    /// </summary>
    public readonly List<Crate> Crates = new();

    public void ResetCrates()
    {
        Crates.Clear();
        foreach (var p in Pieces)
            foreach (var c in p.CrateSpawns)
                Crates.Add(new Crate(p, c.X, c.Y));
    }

    /// <summary>
    /// 格子事件组表：每一项是一串事件下标（有序，按顺序逐个判定）。
    /// evt 层里一格一个字符就是这个表的下标——这样"一格挂多个事件"仍然保持
    /// "一格一个字符"的层格式，框选复制 / 改尺寸那一套完全不用动。
    /// </summary>
    public List<List<int>> EvtSeqs = new();

    /// <summary>这一关最多能开几个窗口。</summary>
    public int MaxWindows = 1;

    public static Level Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// 从一段 JSON 直接解析。单文件分发时关卡是编在程序集里的资源，没有文件路径可传。
    /// </summary>
    public static Level Parse(string json)
    {
        TileSet.Reset();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var level = new Level { Name = root.GetProperty("name").GetString() ?? "" };

        var tiles = root.GetProperty("tiles");
        foreach (var prop in tiles.EnumerateObject())
        {
            int[] sprites;
            if (prop.Value.TryGetProperty("sprites", out var arr) && arr.ValueKind == JsonValueKind.Array)
                sprites = arr.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            else if (prop.Value.TryGetProperty("sprite", out var single))
                sprites = new[] { single.GetInt32() };
            else
                sprites = Array.Empty<int>();

            // 关卡图例显式写了（哪怕写成 false）就以关卡为准；没写才跟随瓦片工具的默认值。
            // 判定口径和关卡编辑器 EffectiveSolid / TileDefaults 完全一致，别只改一边。
            var def = new TileDef
            {
                Sprites = sprites,
                Solid = ReadFlag(prop.Value, "solid", sprites, m => m.Solid),
                Platform = ReadFlag(prop.Value, "platform", sprites, m => m.Platform),
                Water = ReadFlag(prop.Value, "water", sprites, m => m.Water),
                Spring = ReadFlag(prop.Value, "spring", sprites, m => m.Spring),
                Climb = ReadFlag(prop.Value, "climb", sprites, m => m.Climb),
                Plate = ReadFlag(prop.Value, "plate", sprites, m => m.Plate),
            };

            TileSet.Register(prop.Name[0], def);
        }

        foreach (var p in root.GetProperty("pieces").EnumerateArray())
        {
            var rows = p.GetProperty("rows").EnumerateArray().Select(r => r.GetString() ?? "").ToArray();
            int h = rows.Length;
            int w = rows.Max(r => r.Length);
            var cells = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                var row = rows[y];
                for (int x = 0; x < w; x++)
                    cells[y * w + x] = TileSet.IdOf(x < row.Length ? row[x] : '.');
            }

            var piece = new Piece
            {
                Id = p.GetProperty("id").GetString() ?? "",
                Width = w,
                Height = h,
                Cells = cells,
            };

            // 旧格式兼容：back 是独立的一层，整层统一压暗。
            // 新模型是「一格一图 + dim 标志」，所以把 back 里"前景空着"的格子搬进 rows 并标 dim，
            // 两层都有内容时保留前景（背景那层没地方放，会丢）。
            if (p.TryGetProperty("back", out var backEl) && backEl.ValueKind == JsonValueKind.Array)
            {
                var back = new byte[w * h];
                int y = 0;
                foreach (var r in backEl.EnumerateArray())
                {
                    if (y >= h) break;
                    var row = r.GetString() ?? "";
                    for (int x = 0; x < w; x++)
                        back[y * w + x] = TileSet.IdOf(x < row.Length ? row[x] : '.');
                    y++;
                }

                var dim = new byte[w * h];
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] == 0 && back[i] != 0) { cells[i] = back[i]; dim[i] = 1; }
                }
                piece.Dim = dim;
            }

            piece.Var = ParseNumericLayer(p, "var", w, h, 255);
            piece.Xf = ParseNumericLayer(p, "xf", w, h, 0);
            piece.Solid = ParseNumericLayer(p, "solid", w, h, 255);
            piece.Evt = ParseNumericLayer(p, "evt", w, h, 255);
            // 新格式的 dim 层覆盖旧 back 推导出来的结果
            piece.Dim = ParseNumericLayer(p, "dim", w, h, 0) ?? piece.Dim;

            // 箱子：solid 层里的 '3'。抽成实体，并把这一位清成"跟随瓦片"——
            // 之后箱子的位置归实体，静态图层不该再挡一次（不然推动了也走不过去）。
            if (piece.Solid != null)
            {
                for (int i = 0; i < piece.Solid.Length; i++)
                {
                    if (piece.Solid[i] != (byte)SolidKind.Crate) continue;
                    piece.CrateSpawns.Add(new Point(i % w, i / w));
                    piece.Solid[i] = 255;
                }
            }

            var spawn = p.GetProperty("spawn");
            piece.Spawn = new Point(spawn[0].GetInt32(), spawn[1].GetInt32());

            level.Pieces.Add(piece);
        }

        // 事件表：本关所有事件都在这里。没有 events 字段就是"这一关没有事件"。
        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in events.EnumerateArray())
                level.Events.Add(ParseEvent(e));
        }

        // 格子事件组表：evt 层里的字符指向这里。
        if (root.TryGetProperty("evtSeq", out var seqs) && seqs.ValueKind == JsonValueKind.Array)
        {
            foreach (var seq in seqs.EnumerateArray())
            {
                var list = new List<int>();
                if (seq.ValueKind == JsonValueKind.Array)
                    foreach (var v in seq.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetInt32());
                level.EvtSeqs.Add(list);
            }
        }
        else
        {
            // 旧格式：evt 层里的字符直接就是事件下标。等价的组表就是 [[0],[1],[2]...]，
            // 这样老关卡不用改数据也能读，事件组下标正好等于事件下标。
            for (int i = 0; i < level.Events.Count; i++)
                level.EvtSeqs.Add(new List<int> { i });
        }

        // 这一关最多开几个窗口：显式写了就用它，否则按地图块数
        level.MaxWindows = root.TryGetProperty("maxWindows", out var mx)
            ? Math.Max(1, mx.GetInt32())
            : Math.Max(1, level.Pieces.Count);

        level.ResetCrates();
        return level;
    }

    /// <summary>
    /// 读一条事件。字段认不出来就退化成"什么都不做"，绝不让整关加载失败——
    /// 编辑器里的事件表可能有还没填完的草稿。
    /// </summary>
    static GridEvent ParseEvent(JsonElement e)
    {
        var ev = new GridEvent();

        if (e.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            ev.Name = name.GetString() ?? "";

        if (e.TryGetProperty("trigger", out var trig) && trig.ValueKind == JsonValueKind.String)
            ev.Trigger = (trig.GetString() ?? "") switch
            {
                "plate" => EventTrigger.Plate,
                _ => EventTrigger.Touch,
            };

        // 压力板触发器的目标板：{"piece":"地图块","x":10,"y":8}，piece 可省（省略 = 绑定格所在的地图块）
        if (e.TryGetProperty("plate", out var plate) && plate.ValueKind == JsonValueKind.Object)
        {
            if (plate.TryGetProperty("piece", out var pp) && pp.ValueKind == JsonValueKind.String)
                ev.PlatePiece = pp.GetString() ?? "";
            if (plate.TryGetProperty("x", out var px) && px.ValueKind == JsonValueKind.Number)
                ev.PlateX = px.GetInt32();
            if (plate.TryGetProperty("y", out var py) && py.ValueKind == JsonValueKind.Number)
                ev.PlateY = py.GetInt32();
        }
        // 写了 plate 触发器却没给坐标：这块板不存在，永远不激活。
        // 故意不退化成"触碰"——那会让一条配错的板事件变成踩上去就跳关，更难查。

        // 新格式 actions: [...]；旧格式 action: {...} 当成只有一条动作的事件读
        if (e.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in acts.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object) ev.Actions.Add(ParseAction(a));
        }
        else if (e.TryGetProperty("action", out var one) && one.ValueKind == JsonValueKind.Object)
        {
            ev.Actions.Add(ParseAction(one));
        }

        // 条件：新格式 conditions，旧格式 when
        if (e.TryGetProperty("conditions", out var conds) && conds.ValueKind == JsonValueKind.Array)
            ParseConditions(conds, ev);
        else if (e.TryGetProperty("when", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
            ParseConditions(legacy, ev);

        return ev;
    }

    static EventActionDef ParseAction(JsonElement a)
    {
        var act = new EventActionDef();
        if (a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            act.Type = t.GetString() ?? "";
        if (a.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String)
            act.Level = lv.GetString() ?? "";
        if (a.TryGetProperty("target", out var tg) && tg.ValueKind == JsonValueKind.String)
            act.Target = tg.GetString() ?? "";
        if (a.TryGetProperty("args", out var ag) && ag.ValueKind == JsonValueKind.String)
            act.Args = ag.GetString() ?? "";
        return act;
    }

    static void ParseConditions(JsonElement arr, GridEvent ev)
    {
        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;

            var cond = new EventCondition();
            if (c.TryGetProperty("piece", out var pc) && pc.ValueKind == JsonValueKind.String)
            {
                string pid = pc.GetString() ?? "";
                cond.Piece = pid.Length == 0 ? "*" : pid;
            }
            cond.CamX = ReadRange(c, "camX");
            cond.CamY = ReadRange(c, "camY");
            if (cond.CamX == null && cond.CamY == null) continue;   // 空条件等于没写，丢掉
            ev.Conditions.Add(cond);
        }
    }

    /// <summary>读一个 [min, max] 区间；写得不成对就当作没写。</summary>
    static float[]? ReadRange(JsonElement cond, string name)
    {
        if (!cond.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var vals = arr.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        return vals.Length >= 2 ? new[] { vals[0], vals[1] } : null;
    }

    /// <summary>
    /// 读一个图例属性：关卡写了就用关卡的，没写就按主贴图查 tiles.meta.json。
    /// 元数据文件缺失 / 该 sprite 没登记时一律 false，绝不让关卡加载失败。
    /// </summary>
    static bool ReadFlag(JsonElement tile, string name, int[] sprites, Func<TileMeta, bool> pick)
    {
        if (tile.TryGetProperty(name, out var el)) return el.ValueKind == JsonValueKind.True;
        var meta = TileDefaults.ForSprites(sprites);
        return meta != null && pick(meta);
    }

    /// <summary>
    /// 解析 var / xf 这类"每格一个小整数"的层。写成与 rows 等宽的字符串，
    /// 一格一个字符：'0'-'9' = 0-9，'a'-'z' = 10-35，'A'-'Z' = 36-61，
    /// '.' 或空格 = 用 blank 占位（表示"这一格不指定"）。
    /// 整层都是占位符时返回 null，省得白占内存。
    /// </summary>
    static byte[]? ParseNumericLayer(JsonElement piece, string name, int w, int h, byte blank)
    {
        if (!piece.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        var arr = Enumerable.Repeat(blank, w * h).ToArray();
        int y = 0;
        foreach (var r in el.EnumerateArray())
        {
            if (y >= h) break;
            var row = r.GetString() ?? "";
            for (int x = 0; x < w; x++)
            {
                int d = CellDigit(x < row.Length ? row[x] : '.');
                if (d >= 0) arr[y * w + x] = (byte)d;
            }
            y++;
        }
        return arr.All(v => v == blank) ? null : arr;
    }

    static int CellDigit(char c) =>
        c is >= '0' and <= '9' ? c - '0'
        : c is >= 'a' and <= 'z' ? c - 'a' + 10
        : c is >= 'A' and <= 'Z' ? c - 'A' + 36
        : -1;
}
