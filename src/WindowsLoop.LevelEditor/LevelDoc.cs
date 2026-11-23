using System.Drawing;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsLoop.Tileset;

namespace WindowsLoop.LevelEditor;

/// <summary>与游戏 src/WindowsLoop/Level.cs 的关卡 JSON 格式一一对应的可写模型。</summary>
internal sealed class TileDoc
{
    [JsonPropertyName("sprite")] public int? Sprite { get; set; }
    [JsonPropertyName("sprites")] public int[]? Sprites { get; set; }
    [JsonPropertyName("solid")] public bool? Solid { get; set; }

    /// <summary>单向平台：下方能顶穿，上方能站立。和 solid 同时为真时按实心算。</summary>
    [JsonPropertyName("platform")] public bool? Platform { get; set; }

    [JsonPropertyName("water")] public bool? Water { get; set; }
    [JsonPropertyName("spring")] public bool? Spring { get; set; }
    [JsonPropertyName("climb")] public bool? Climb { get; set; }

    /// <summary>压力板：玩家或箱子与这一格重叠就算踩下（事件触发器可以读它）。</summary>
    [JsonPropertyName("plate")] public bool? Plate { get; set; }

    /// <summary>模型还没认识的字段原样保留，避免"打开→保存"把以后的关卡字段吃掉。</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public int FirstSprite => Sprites is { Length: > 0 } ? Sprites[0] : Sprite ?? -1;

    /// <summary>和引擎 TileSet.SpriteAt 同一套规则：variant 指定了就用它，否则按坐标哈希。</summary>
    public int SpriteFor(int x, int y, int variant = -1)
    {
        if (Sprites is { Length: > 0 })
        {
            if (variant >= 0 && variant < Sprites.Length) return Sprites[variant];
            if (Sprites.Length == 1) return Sprites[0];
            int h = (x * 73856093) ^ (y * 19349663);
            return Sprites[(h & 0x7fffffff) % Sprites.Length];
        }
        return Sprite ?? -1;
    }

    /// <summary>这一条目里可选的 sprite 列表（单 sprite 也返回长度 1）。</summary>
    public int[] SpriteList() =>
        Sprites is { Length: > 0 } ? Sprites : Sprite is int one ? new[] { one } : Array.Empty<int>();

    /// <summary>
    /// 「实际生效」的五个属性：图例里显式写了（含 false）就用关卡的，没写才看
    /// tiles.meta.json 里这块主贴图的默认值。和游戏 Level.ReadFlag 同一套口径。
    /// </summary>
    [JsonIgnore] public bool EffectiveSolidFlag => TileDefaults.SolidOf(Solid, SpriteList());
    [JsonIgnore] public bool EffectivePlatformFlag => TileDefaults.PlatformOf(Platform, SpriteList());
    [JsonIgnore] public bool EffectiveWaterFlag => TileDefaults.WaterOf(Water, SpriteList());
    [JsonIgnore] public bool EffectiveSpringFlag => TileDefaults.SpringOf(Spring, SpriteList());
    [JsonIgnore] public bool EffectiveClimbFlag => TileDefaults.ClimbOf(Climb, SpriteList());
    [JsonIgnore] public bool EffectivePlateFlag => TileDefaults.PlateOf(Plate, SpriteList());

    /// <summary>五个属性里有没有哪一项是「跟随瓦片默认」（没在关卡里写死）。</summary>
    [JsonIgnore] public bool FollowsDefaults =>
        Solid == null || Platform == null || Water == null || Spring == null || Climb == null;

    public TileDoc Clone() => new()
    {
        Sprite = Sprite,
        Sprites = Sprites?.ToArray(),
        Solid = Solid,
        Platform = Platform,
        Water = Water,
        Spring = Spring,
        Climb = Climb,
        Plate = Plate,
        Extra = Extra,
    };
}

internal sealed class PieceDoc
{
    [JsonPropertyName("id")] public string Id { get; set; } = "piece";
    [JsonPropertyName("rows")] public List<string> Rows { get; set; } = new();

    /// <summary>每格在 sprites 里取第几个；'.' = 不指定（走坐标哈希）。</summary>
    [JsonPropertyName("var")] public List<string>? Var { get; set; }

    /// <summary>每格朝向：'0'..'7'，编码 = 旋转步数 + 水平镜像(4)。'.' = 原样。</summary>
    [JsonPropertyName("xf")] public List<string>? Xf { get; set; }

    /// <summary>
    /// 每格阻挡类型：'0' = 可穿过，'1' = 实心，'2' = 单向平台，'.' = 跟随瓦片自带的 solid / platform。
    /// 阻挡是格子的属性，不是瓦片的——这正是这次改动的核心。
    /// </summary>
    [JsonPropertyName("solid")] public List<string>? Solid { get; set; }

    /// <summary>每格是否压暗绘制（原来的"背景层"）：'1' = 压暗，'.' / 缺省 = 正常亮度。</summary>
    [JsonPropertyName("dim")] public List<string>? Dim { get; set; }

    /// <summary>
    /// 每格挂的**事件组**下标（指向 LevelDoc.EvtSeq），编码和 var 层一样：
    /// '0'-'9' = 0-9，'a'-'z' = 10-35，'A'-'Z' = 36-61，'.' = 这一格没有事件。
    /// 一格可以挂一串事件，所以这里存的是"组"而不是直接的事件下标。
    /// </summary>
    [JsonPropertyName("evt")] public List<string>? Evt { get; set; }

    [JsonPropertyName("spawn")] public int[] Spawn { get; set; } = new[] { 0, 0 };

    /// <summary>
    /// 旧格式的独立背景层。只在读旧关卡时出现，Normalize 里会合并进 Rows 并转成 Dim，
    /// 合并后置空，所以保存出来永远是新的单层格式。
    /// </summary>
    [JsonPropertyName("back")] public List<string>? LegacyBack { get; set; }

    /// <summary>模型还没认识的字段原样保留，避免"打开→保存"把以后的关卡字段吃掉。</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public int Width => Rows.Count == 0 ? 0 : Rows[0].Length;
    [JsonIgnore] public int Height => Rows.Count;

    public void Normalize()
    {
        if (Rows.Count == 0) Rows.Add("");
        int w = Math.Max(1, Rows.Max(r => r.Length));
        for (int i = 0; i < Rows.Count; i++) Rows[i] = Rows[i].PadRight(w, '.');

        MergeLegacyBack(w);

        Var = CleanLayer(Var, Rows.Count, w);
        Xf = CleanLayer(Xf, Rows.Count, w);
        Solid = CleanLayer(Solid, Rows.Count, w);
        Dim = CleanLayer(Dim, Rows.Count, w);
        Evt = CleanLayer(Evt, Rows.Count, w);
    }

    /// <summary>把层对齐到 rows 的尺寸；整层都是占位符时丢掉，JSON 里不留空壳。</summary>
    static List<string>? CleanLayer(List<string>? layer, int h, int w)
    {
        if (layer == null) return null;
        while (layer.Count < h) layer.Add(new string('.', w));
        if (layer.Count > h) layer.RemoveRange(h, layer.Count - h);
        for (int i = 0; i < layer.Count; i++)
            layer[i] = layer[i].PadRight(w, '.').Substring(0, w);
        return layer.All(r => IsAirRow(r)) ? null : layer;
    }

    /// <summary>
    /// 旧关卡把"背景"存成独立的 back 层，整层统一压暗。新模型是一格一图 + dim 标志，
    /// 所以把 back 里"前景空着"的格子搬进 rows 并标 dim。两层都有内容时保留前景。
    /// </summary>
    void MergeLegacyBack(int w)
    {
        if (LegacyBack == null) return;

        while (LegacyBack.Count < Rows.Count) LegacyBack.Add(new string('.', w));
        if (LegacyBack.Count > Rows.Count) LegacyBack.RemoveRange(Rows.Count, LegacyBack.Count - Rows.Count);
        for (int i = 0; i < LegacyBack.Count; i++)
            LegacyBack[i] = LegacyBack[i].PadRight(w, '.').Substring(0, w);

        Dim ??= new List<string>();
        while (Dim.Count < Rows.Count) Dim.Add(new string('.', w));
        for (int i = 0; i < Dim.Count; i++)
            Dim[i] = Dim[i].PadRight(w, '.').Substring(0, w);

        for (int y = 0; y < Rows.Count; y++)
        {
            var fg = Rows[y].ToCharArray();
            var bg = LegacyBack[y].ToCharArray();
            var dm = Dim[y].ToCharArray();
            for (int x = 0; x < w; x++)
            {
                if (IsAir(fg[x]) && !IsAir(bg[x]))
                {
                    fg[x] = bg[x];
                    dm[x] = '1';
                }
            }
            Rows[y] = new string(fg);
            Dim[y] = new string(dm);
        }

        LegacyBack = null;
    }

    public static bool IsAir(char c) => c == '.' || c == ' ';
    public static bool IsAirRow(string s) => s.All(IsAir);

    // ---------- var / xf / solid / dim：每格一个小整数 ----------

    public static int DigitOf(char c) =>
        c is >= '0' and <= '9' ? c - '0'
        : c is >= 'a' and <= 'z' ? c - 'a' + 10
        : c is >= 'A' and <= 'Z' ? c - 'A' + 36
        : -1;

    public static char CharOf(int v) =>
        v < 0 ? '.'
        : v < 10 ? (char)('0' + v)
        : v < 36 ? (char)('a' + v - 10)
        : v < 62 ? (char)('A' + v - 36)
        : '.';

    int ReadLayer(List<string>? layer, int x, int y, int fallback)
    {
        if (layer == null || y < 0 || y >= layer.Count) return fallback;
        var row = layer[y];
        if (x < 0 || x >= row.Length) return fallback;
        int d = DigitOf(row[x]);
        return d < 0 ? fallback : d;
    }

    List<string>? WriteLayer(List<string>? layer, int x, int y, int value)
    {
        if (y < 0 || y >= Rows.Count) return layer;
        int w = Math.Max(1, Width);
        layer ??= new List<string>();
        while (layer.Count < Rows.Count) layer.Add(new string('.', w));
        if (layer.Count > Rows.Count) layer.RemoveRange(Rows.Count, layer.Count - Rows.Count);
        for (int i = 0; i < layer.Count; i++)
            layer[i] = layer[i].PadRight(w, '.').Substring(0, w);

        var chars = layer[y].ToCharArray();
        if (x < 0 || x >= chars.Length) return layer;
        chars[x] = CharOf(value);
        layer[y] = new string(chars);
        return layer;
    }

    /// <summary>前景第 y 行第 x 格（越界返回空气）。</summary>
    public char GetCell(int x, int y)
    {
        if (y < 0 || y >= Rows.Count) return '.';
        var row = Rows[y] ?? "";
        return x >= 0 && x < row.Length ? row[x] : '.';
    }

    public void SetCell(int x, int y, char c)
    {
        if (y < 0 || y >= Rows.Count) return;
        var row = Rows[y] ?? "";
        if (x < 0 || x >= row.Length) return;
        var chars = row.ToCharArray();
        if (chars[x] == c) return;
        chars[x] = c;
        Rows[y] = new string(chars);
    }

    /// <summary>这一格的变体下标；-1 = 未指定。</summary>
    public int GetVar(int x, int y) => ReadLayer(Var, x, y, -1);
    public void SetVar(int x, int y, int v) => Var = WriteLayer(Var, x, y, v);

    /// <summary>这一格的朝向编码（0..7）。</summary>
    public int GetXf(int x, int y) => ReadLayer(Xf, x, y, 0);
    public void SetXf(int x, int y, int v) => Xf = WriteLayer(Xf, x, y, v <= 0 ? -1 : v);

    /// <summary>这一格的阻挡标记：-1 = 跟随瓦片，0 = 可穿过，1 = 实心，2 = 单向平台。</summary>
    public int GetSolid(int x, int y) => ReadLayer(Solid, x, y, -1);
    public void SetSolid(int x, int y, int v) => Solid = WriteLayer(Solid, x, y, v);

    /// <summary>这一格是否压暗绘制。</summary>
    public bool GetDim(int x, int y) => ReadLayer(Dim, x, y, 0) != 0;
    public void SetDim(int x, int y, bool on) => Dim = WriteLayer(Dim, x, y, on ? 1 : -1);

    /// <summary>这一格挂的事件组下标；-1 = 没有事件。</summary>
    public int GetEvtSeq(int x, int y) => ReadLayer(Evt, x, y, -1);
    public void SetEvtSeq(int x, int y, int v) => Evt = WriteLayer(Evt, x, y, v);

    /// <summary>
    /// 这一格的实际阻挡类型：逐格标记 > 关卡图例显式属性 > 瓦片工具默认 > 可穿过。
    /// 实心优先于平台（和图例里 solid 压过 platform 的口径一致）。
    /// 瓦片工具改了默认值，这里（画布上的标记）和游戏会同步变。
    /// </summary>
    public SolidKind EffectiveKind(LevelDoc doc, int x, int y)
    {
        int s = GetSolid(x, y);
        if (s == (int)SolidKind.Crate) return SolidKind.Crate;
        if (s == (int)SolidKind.Block) return SolidKind.Block;
        if (s == (int)SolidKind.Platform) return SolidKind.Platform;
        if (s == (int)SolidKind.Pass) return SolidKind.Pass;

        char c = GetCell(x, y);
        if (!doc.Tiles.TryGetValue(c.ToString(), out var t)) return SolidKind.Pass;
        if (t.EffectiveSolidFlag) return SolidKind.Block;
        return t.EffectivePlatformFlag ? SolidKind.Platform : SolidKind.Pass;
    }

    /// <summary>这一格会不会四面挡人（单向平台不算；箱子算，它挡人只是会动）。</summary>
    public bool EffectiveSolid(LevelDoc doc, int x, int y) =>
        EffectiveKind(doc, x, y) is SolidKind.Block or SolidKind.Crate;

    /// <summary>这一格是不是箱子（solid 层写了 '3'）。</summary>
    public bool IsCrate(int x, int y) => GetSolid(x, y) == (int)SolidKind.Crate;

    /// <summary>这一格是不是单向平台。</summary>
    public bool EffectivePlatform(LevelDoc doc, int x, int y) => EffectiveKind(doc, x, y) == SolidKind.Platform;

    [JsonIgnore] public bool HasVar => Var != null && Var.Any(r => !IsAirRow(r));
    [JsonIgnore] public bool HasXf => Xf != null && Xf.Any(r => !IsAirRow(r));
    [JsonIgnore] public bool HasSolid => Solid != null && Solid.Any(r => !IsAirRow(r));
    [JsonIgnore] public bool HasDim => Dim != null && Dim.Any(r => !IsAirRow(r));
    [JsonIgnore] public bool HasEvt => Evt != null && Evt.Any(r => !IsAirRow(r));

    public static PieceDoc Blank(int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(2, h);
        var rows = Enumerable.Repeat(new string('.', w), h).ToList();
        rows[h - 1] = new string('g', w);
        return new PieceDoc { Id = "piece", Rows = rows, Spawn = new[] { w / 2, h - 2 } };
    }
}

internal sealed class LevelDoc
{
    [JsonPropertyName("name")] public string Name { get; set; } = "新关卡";
    [JsonPropertyName("maxWindows")] public int MaxWindows { get; set; } = 1;
    [JsonPropertyName("tiles")] public Dictionary<string, TileDoc> Tiles { get; set; } = new();
    [JsonPropertyName("pieces")] public List<PieceDoc> Pieces { get; set; } = new();

    /// <summary>
    /// 本关的网格事件表。PieceDoc.Evt 层里存的是「事件组」下标（见 EvtSeq）。
    /// 数组为空时保存出来的 JSON 里不带这个键。
    /// </summary>
    [JsonPropertyName("events")] public List<EventDoc> Events { get; set; } = new();

    /// <summary>
    /// 格子事件组表：每一项是一串事件下标（有序，按顺序逐个判定）。
    /// evt 层一格一个字符就是这个表的下标——"一格挂多个事件"仍然保持一格一字符的层格式，
    /// 框选复制 / 移动 / 改尺寸都不用特殊处理。
    /// 编辑器会自动整理：相同的组合合并、没人引用的组删掉、下标压紧到 0..n-1。
    /// </summary>
    [JsonPropertyName("evtSeq")] public List<List<int>> EvtSeq { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static LevelDoc NewLevel() => new()
    {
        Name = "新关卡",
        MaxWindows = 1,
        Tiles = TilePresets.DefaultTiles(),
        Pieces = new() { PieceDoc.Blank(24, 10) },
    };

    // ---------- 格子事件组 ----------

    /// <summary>一格最多能挂几条事件（只是编辑器的一个软上限，避免一层格子写成一长串）。</summary>
    public const int MaxEventsPerCell = 16;

    /// <summary>这一格挂的事件（有序）。空列表 = 没有事件。</summary>
    public List<int> SeqOf(PieceDoc p, int x, int y)
    {
        int i = p.GetEvtSeq(x, y);
        if (i < 0 || i >= EvtSeq.Count) return new List<int>();
        return new List<int>(EvtSeq[i]);
    }

    /// <summary>按内容找一组，找不到就追加一组；返回组下标。</summary>
    public int FindOrAddSeq(IReadOnlyList<int> events)
    {
        for (int i = 0; i < EvtSeq.Count; i++)
        {
            var s = EvtSeq[i];
            if (s.Count != events.Count) continue;
            bool same = true;
            for (int k = 0; k < s.Count; k++)
                if (s[k] != events[k]) { same = false; break; }
            if (same) return i;
        }
        EvtSeq.Add(new List<int>(events));
        return EvtSeq.Count - 1;
    }

    /// <summary>
    /// 把这一格的事件设成 events（空列表 = 清掉）；组表里没有相同组合就新建一组。
    /// 返回组下标；-1 = 清空成功；-2 = 组太多（一层格子最多 62 组），这次没有写入。
    /// </summary>
    public int SetCellEvents(PieceDoc p, int x, int y, IReadOnlyList<int> events)
    {
        if (events.Count == 0)
        {
            p.SetEvtSeq(x, y, -1);
            return -1;
        }

        int i = FindOrAddSeq(events);
        if (i > 61) return -2;      // 一个字符放不下，别静默写成 '.'
        p.SetEvtSeq(x, y, i);
        return i;
    }

    /// <summary>
    /// 整理组表：丢掉没人引用的组和空组、合并完全相同的组、把下标压紧到 0..n-1，
    /// 并按映射重写所有地图块的 evt 层。保存前 / 每次改完都会跑一次，文件里不留垃圾。
    /// </summary>
    public void CompactEvt()
    {
        // 先统计各层到底引用了哪几组 —— 只整理被引用的，没人用的组直接丢掉
        var referenced = new bool[EvtSeq.Count];
        foreach (var p in Pieces)
        {
            if (p.Evt == null) continue;
            foreach (var row in p.Evt)
                foreach (char c in row)
                {
                    int d = PieceDoc.DigitOf(c);
                    if (d >= 0 && d < referenced.Length) referenced[d] = true;
                }
        }

        var remap = new int[EvtSeq.Count];
        for (int i = 0; i < remap.Length; i++) remap[i] = -1;

        var kept = new List<List<int>>();
        for (int i = 0; i < EvtSeq.Count; i++)
        {
            if (!referenced[i]) continue;

            var seq = EvtSeq[i].Where(v => v >= 0 && v < Events.Count).Distinct().ToList();
            if (seq.Count == 0) continue;

            int existing = -1;
            for (int k = 0; k < kept.Count; k++)
                if (kept[k].SequenceEqual(seq)) { existing = k; break; }

            if (existing >= 0) remap[i] = existing;
            else { kept.Add(seq); remap[i] = kept.Count - 1; }
        }
        EvtSeq = kept;

        foreach (var p in Pieces)
        {
            if (p.Evt == null) continue;
            for (int y = 0; y < p.Evt.Count; y++)
            {
                var chars = p.Evt[y].ToCharArray();
                bool changed = false;
                for (int x = 0; x < chars.Length; x++)
                {
                    int d = PieceDoc.DigitOf(chars[x]);
                    if (d < 0) continue;
                    int next = d < remap.Length ? remap[d] : -1;
                    if (next == d) continue;
                    chars[x] = next < 0 ? '.' : PieceDoc.CharOf(next);
                    changed = true;
                }
                if (changed) p.Evt[y] = new string(chars);
            }
            p.Normalize();
        }
    }

    /// <summary>
    /// 删掉第 index 条事件，并把所有事件组里对它的引用一并处理：
    /// 去掉对它的引用、大于它的下标整体前移，最后压紧组表。
    /// 只删数组而不重映射，后面所有格子和条件都会指错事件。
    /// </summary>
    public void RemoveEvent(int index)
    {
        if (index < 0 || index >= Events.Count) return;
        Events.RemoveAt(index);

        foreach (var seq in EvtSeq)
        {
            seq.RemoveAll(v => v == index);
            for (int k = 0; k < seq.Count; k++)
                if (seq[k] > index) seq[k]--;
        }

        CompactEvt();
    }
}

/// <summary>
/// 一条动作（对着任务计划程序里的"操作"）。
/// goto = 跳转到指定关卡；run = 在电脑上打开程序 / 网址 / 文件；
/// hide = 把事件绑定的那几格图块藏起来、并且不再阻挡；
/// sfx = 播一次音效（Sound 是 Shared\SfxCatalog.cs 里的 key）。
/// 一条事件可以挂多条，按顺序执行。
/// </summary>
internal sealed class ActionDoc
{
    /// <summary>"goto" / "run" / "hide" / "sfx"；认不出来的值游戏里按"什么都不做"处理。</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "goto";

    /// <summary>goto 的目标关卡（assets/levels 下的文件名，不带 .json）。留空 = 还没填。</summary>
    [JsonPropertyName("level")] public string Level { get; set; } = "";

    /// <summary>run 的目标：程序路径、网址，或任何 ShellExecute 认识的东西。</summary>
    [JsonPropertyName("target")] public string Target { get; set; } = "";

    /// <summary>run 的命令行参数，可空。</summary>
    [JsonPropertyName("args")] public string Args { get; set; } = "";

    /// <summary>sfx 的音效 key（"unlock" / "gem" / "land" …），下拉框里选，留空 = 还没选。</summary>
    [JsonPropertyName("sound")] public string Sound { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ActionDoc Clone() => new()
    {
        Type = Type,
        Level = Level,
        Target = Target,
        Args = Args,
        Sound = Sound,
        Extra = Extra,
    };

    /// <summary>这条动作还缺必需的目标（跳关没选关卡 / 执行命令没填目标 / 音效没选）。</summary>
    [JsonIgnore]
    public bool IsUnset => Type switch
    {
        "goto" => string.IsNullOrWhiteSpace(Level),
        "run" => string.IsNullOrWhiteSpace(Target),
        "sfx" => string.IsNullOrWhiteSpace(Sound),
        _ => false,
    };

    public string Describe() => Type switch
    {
        "goto" => "跳转 " + (string.IsNullOrWhiteSpace(Level) ? "(未填)" : Level),
        "run" => "执行 " + (string.IsNullOrWhiteSpace(Target) ? "(未填)" : Shorten(Target)),
        "hide" => "隐藏本格图块（不再阻挡）",
        "sfx" => "音效 " + (string.IsNullOrWhiteSpace(Sound) ? "(未选)" : SfxCatalog.LabelOf(Sound)),
        _ => "无动作(" + Type + ")",
    };

    static string Shorten(string s) => s.Length <= 28 ? s : s[..27] + "…";
}

/// <summary>
/// 一条前置条件：某块地图的摄像机滚动（取景框左上角，单位格）落在 [min,max] 里。
/// Piece 为 "*" 表示所有开着的窗口都要满足。
/// </summary>
internal sealed class ConditionDoc
{
    [JsonPropertyName("piece")] public string Piece { get; set; } = "*";

    /// <summary>[min, max]；null = 这一轴不限制。</summary>
    [JsonPropertyName("camX")] public float[]? CamX { get; set; }

    [JsonPropertyName("camY")] public float[]? CamY { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ConditionDoc Clone() => new()
    {
        Piece = Piece,
        CamX = CamX?.ToArray(),
        CamY = CamY?.ToArray(),
        Extra = Extra,
    };

    public string Describe() =>
        Piece + "：" +
        (CamX == null ? "" : $"X∈[{CamX[0]:0.#},{CamX[1]:0.#}]") +
        (CamX != null && CamY != null ? " " : "") +
        (CamY == null ? "" : $"Y∈[{CamY[0]:0.#},{CamY[1]:0.#}]");
}

/// <summary>
/// 压力板触发器盯的是哪一块板：地图块 id + 格坐标。
/// Piece 留空 = 用"事件绑定格"所在的那块地图（事件挂在好几块地图上时按每块分别算）。
/// </summary>
internal sealed class PlateRefDoc
{
    [JsonPropertyName("piece")] public string Piece { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public PlateRefDoc Clone() => new() { Piece = Piece, X = X, Y = Y, Extra = Extra };

    public string Describe() =>
        $"{(string.IsNullOrEmpty(Piece) ? "(绑定格所在图块)" : Piece)} ({X},{Y})";
}

/// <summary>
/// 一条网格事件，结构对着 Windows 任务计划程序：
/// 触发器（什么时候判）+ 动作（按顺序做什么，可以多条）+ 条件（能不能做）。
/// </summary>
internal sealed class EventDoc
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>触发器："touch"（角色走进这一格）/ "plate"（指定的压力板被压住）/ "always"（每帧判条件）。</summary>
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "touch";

    /// <summary>
    /// trigger == "always" 时的重复方式：true = 条件成立就反复触发，false = 只触发一次。
    /// 重复触发的事件不能执行命令（run），游戏里会跳过那条动作。
    /// </summary>
    [JsonPropertyName("repeat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Repeat { get; set; }

    /// <summary>trigger == "plate" 时盯的那块板。</summary>
    [JsonPropertyName("plate")] public PlateRefDoc? Plate { get; set; }

    /// <summary>动作串，按顺序执行。空表 = 什么都不做（草稿）。</summary>
    [JsonPropertyName("actions")] public List<ActionDoc>? Actions { get; set; }

    [JsonPropertyName("conditions")] public List<ConditionDoc>? Conditions { get; set; }

    // ---- 旧格式（单动作 action / 条件叫 when）：读进来就并进新字段，保存时不再写 ----
    [JsonPropertyName("action")] public ActionDoc? LegacyAction { get; set; }
    [JsonPropertyName("when")] public List<ConditionDoc>? LegacyConditions { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public EventDoc Clone() => new()
    {
        Name = Name,
        Trigger = Trigger,
        Repeat = Repeat,
        Plate = Plate?.Clone(),
        Actions = Actions?.Select(a => a.Clone()).ToList(),
        Conditions = Conditions?.Select(c => c.Clone()).ToList(),
        Extra = Extra,
    };

    /// <summary>有哪条动作还缺必需的目标（跳关没选关卡 / 执行命令没填目标）——编辑器里标红用。</summary>
    [JsonIgnore]
    public bool HasUnsetAction => Actions?.Any(a => a.IsUnset) ?? false;

    /// <summary>这条事件里有没有「隐藏本格」的动作（它需要绑定格才有格子可藏）。</summary>
    [JsonIgnore]
    public bool HidesCell => Actions?.Any(a => a.Type == "hide") ?? false;

    /// <summary>可重复触发的「总是」事件里，游戏会跳过的命令动作。</summary>
    [JsonIgnore]
    public bool RepeatingRunsCommand => Trigger == "always" && Repeat && (Actions?.Any(a => a.Type == "run") ?? false);

    public string Describe()
    {
        var acts = Actions ?? new List<ActionDoc>();
        string act = acts.Count == 0
            ? "无动作"
            : acts.Count == 1 ? acts[0].Describe() : $"{acts[0].Describe()} 等 {acts.Count} 条动作";
        int n = Conditions?.Count ?? 0;
        string cond = n == 0 ? "" : $"  [{n} 条件]";
        string trig = Trigger switch
        {
            "plate" => $"板{Plate?.Describe() ?? "(未选)"} → ",
            "always" => Repeat ? "总是·每帧 → " : "总是·一次 → ",
            _ => "",
        };
        return $"{trig}{act}{cond}";
    }
}

internal static class LevelStore
{
    static int? _spriteCount;

    /// <summary>图集里的 sprite 总数，按 tiles.png 实际尺寸推导（8px 一格），换大图集不用改代码。</summary>
    public static int MaxSprite
    {
        get
        {
            if (_spriteCount is int n) return n;
            try
            {
                using var fs = File.OpenRead(TileSheetPath);
                using var bmp = new Bitmap(fs);
                _spriteCount = Math.Max(1, (bmp.Width / 8) * (bmp.Height / 8));
            }
            catch { _spriteCount = 96; }
            return _spriteCount.Value;
        }
    }

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>从输出目录逐级往上找游戏项目的 assets，找到就能直接保存回游戏用的关卡目录。</summary>
    public static string AssetsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var p = Path.Combine(dir.FullName, "src", "WindowsLoop", "assets");
                if (Directory.Exists(p)) return p;
                dir = dir.Parent;
            }
            return Path.Combine(Directory.GetCurrentDirectory(), "src", "WindowsLoop", "assets");
        }
    }

    public static string LevelsDir => Path.Combine(AssetsDir, "levels");
    public static string TileSheetPath => Path.Combine(AssetsDir, "tiles.png");

    /// <summary>
    /// 重新读一次瓦片默认值（「编辑 → 重新载入瓦片默认值」菜单用）。
    /// 数据来自 build 时复制进输出目录的 tiles.meta.json，改完工具要重新 build.cmd。
    /// </summary>
    public static string ReloadTileDefaults()
    {
        TileDefaults.Reload();
        return TileDefaults.PathUsed;
    }

    public static LevelDoc Load(string path) => Deserialize(File.ReadAllText(path));

    public static void Save(string path, LevelDoc doc)
    {
        // 先把格子事件组压紧（合并相同组合、丢掉没人引用的组、重写 evt 层字符），
        // 文件里就不会出现"删了半天还剩一堆空组"的垃圾。
        doc.CompactEvt();
        foreach (var p in doc.Pieces) p.Normalize();
        File.WriteAllText(path, Serialize(doc) + Environment.NewLine);
    }

    public static string Serialize(LevelDoc doc)
    {
        // 事件表 / 事件组表为空时不写空壳（新关卡都带一行 "events": []、"evtSeq": [] 太吵）。
        // 临时置空，靠 WhenWritingNull 把键省掉，写完立刻还原。
        var events = doc.Events;
        var seqs = doc.EvtSeq;
        bool blankEvents = events.Count == 0;
        bool blankSeqs = seqs.Count == 0;
        if (blankEvents) doc.Events = null!;
        if (blankSeqs) doc.EvtSeq = null!;
        try { return JsonSerializer.Serialize(doc, Opts); }
        finally
        {
            if (blankEvents) doc.Events = events;
            if (blankSeqs) doc.EvtSeq = seqs;
        }
    }

    public static LevelDoc Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<LevelDoc>(json, Opts) ?? LevelDoc.NewLevel();
        doc.Tiles ??= new Dictionary<string, TileDoc>();
        doc.Pieces ??= new List<PieceDoc>();
        doc.Events ??= new List<EventDoc>();
        foreach (var p in doc.Pieces)
        {
            p.Rows ??= new List<string>();
            p.Spawn ??= new[] { 0, 0 };
            p.Normalize();
        }
        if (doc.Pieces.Count == 0) doc.Pieces.Add(PieceDoc.Blank(24, 10));

        // 事件表里的空条目（读坏 / 手写漏字段）一律兜底，别让编辑器崩在 null 上；
        // 顺便把旧格式（单动作 action / when）升级成新格式（actions / conditions）。
        foreach (var ev in doc.Events)
        {
            if (ev.Actions == null && ev.LegacyAction != null)
                ev.Actions = new List<ActionDoc> { ev.LegacyAction };
            ev.LegacyAction = null;

            if (ev.Conditions == null && ev.LegacyConditions != null)
                ev.Conditions = ev.LegacyConditions;
            ev.LegacyConditions = null;

            ev.Actions ??= new List<ActionDoc>();
            ev.Actions.RemoveAll(a => a == null);

            ev.Conditions ??= new List<ConditionDoc>();
            ev.Conditions.RemoveAll(c => c == null);
        }
        doc.Events.RemoveAll(e => e == null);

        doc.EvtSeq ??= new List<List<int>>();
        doc.EvtSeq.RemoveAll(s => s == null);
        foreach (var s in doc.EvtSeq) s.RemoveAll(v => v < 0);

        // 旧格式升级：没有 evtSeq 但已经在用 evt 层 —— 那时 evt 里的字符直接就是事件下标，
        // 等价的组表就是恒等映射 [[0],[1],...]。读进来后照常编辑，保存时自动写成新格式。
        if (doc.EvtSeq.Count == 0 && doc.Pieces.Any(p => p.HasEvt))
            doc.EvtSeq = Enumerable.Range(0, doc.Events.Count).Select(i => new List<int> { i }).ToList();

        return doc;
    }

    /// <summary>assets/levels 下可跳转的关卡名（不带 .json），给「目标关卡」下拉用。</summary>
    public static List<string> LevelNames()
    {
        try
        {
            if (!Directory.Exists(LevelsDir)) return new List<string>();
            return Directory.GetFiles(LevelsDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch { return new List<string>(); }
    }

    /// <summary>保存前校验，返回空串表示通过。口径与 Level.Load 一致。</summary>
    public static string Validate(LevelDoc doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Name)) return "关卡名不能为空";
        if (doc.MaxWindows < 1) return "maxWindows 至少为 1";
        if (doc.Pieces.Count == 0) return "至少要有一块地图";

        foreach (var p in doc.Pieces)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) return "有地图块没有 id";
            if (p.Rows.Count == 0) return $"{p.Id} 没有行";
            int w = p.Rows[0].Length;
            if (w == 0) return $"{p.Id} 宽度为 0";
            for (int y = 0; y < p.Rows.Count; y++)
            {
                if (p.Rows[y].Length != w)
                    return $"{p.Id} 第 {y} 行宽度不一致（{p.Rows[y].Length} vs {w}）";
                // 未定义字符不阻止保存：游戏里 TileSet.IdOf 会把它当空气，
                // 编辑器画布上会画成红色 '?'，由用户决定刷掉还是补图例
            }
            if (!InRange(p.Spawn, w, p.Height)) return $"{p.Id} 的 spawn 超出地图";
            // 挡不挡是格子的属性（可以逐格覆盖瓦片默认），所以用 EffectiveSolid 判断
            if (p.EffectiveSolid(doc, p.Spawn[0], p.Spawn[1]))
                return $"{p.Id} 的 spawn 落在阻挡格里，角色会被卡住";

            // evt 层引用了不存在的事件组：游戏会把它当"没挂事件"，纯属数据错误，值得拦住
            if (p.Evt != null)
                for (int y = 0; y < p.Evt.Count; y++)
                    for (int x = 0; x < p.Evt[y].Length; x++)
                    {
                        int d = PieceDoc.DigitOf(p.Evt[y][x]);
                        if (d >= doc.EvtSeq.Count)
                            return $"{p.Id} 的 ({x},{y}) 挂了不存在的事件组 #{d}（组表只有 {doc.EvtSeq.Count} 组）";
                    }
        }

        // 组表里的每一项都必须指向真事件，否则那条永远判不出来
        for (int i = 0; i < doc.EvtSeq.Count; i++)
            foreach (int v in doc.EvtSeq[i])
                if (v < 0 || v >= doc.Events.Count)
                    return $"事件组 #{i} 引用了不存在的事件 #{v}（事件表只有 {doc.Events.Count} 条）";

        // 条件里引用的地图块必须存在，否则游戏里那条条件永远不成立
        var ids = doc.Pieces.Select(p => p.Id).ToHashSet();
        for (int i = 0; i < doc.Events.Count; i++)
        {
            var ev = doc.Events[i];
            foreach (var c in ev.Conditions ?? new List<ConditionDoc>())
                if (c.Piece != "*" && !ids.Contains(c.Piece))
                    return $"事件 #{i} 的条件指向不存在的地图块「{c.Piece}」";
        }

        foreach (var kv in doc.Tiles)
        {
            if (kv.Key.Length != 1) return $"图例 '{kv.Key}' 必须是单个字符";
            if (kv.Key == "." || kv.Key == " ") return "'.' 和空格是保留的空气字符，不能定义图例";
            foreach (int s in kv.Value.Sprites ?? Array.Empty<int>())
                if (s < 0 || s >= MaxSprite) return $"图例 '{kv.Key}' 的 sprite {s} 超出图集(0..{MaxSprite - 1})";
            if (kv.Value.Sprite is int one && (one < 0 || one >= MaxSprite))
                return $"图例 '{kv.Key}' 的 sprite {one} 超出图集(0..{MaxSprite - 1})";
        }
        return "";
    }

    static bool InRange(int[] pt, int w, int h) =>
        pt.Length >= 2 && pt[0] >= 0 && pt[0] < w && pt[1] >= 0 && pt[1] < h;

    /// <summary>
    /// 不阻止保存的提醒（事件还没填目标 / 事件没挂到任何格子 / 组下标太多）。
    /// 编辑器把它显示在状态栏和事件列表里，游戏里遇到会当"没动作"处理。
    /// </summary>
    public static List<string> Warnings(LevelDoc doc)
    {
        var list = new List<string>();

        var used = new HashSet<int>();
        foreach (var p in doc.Pieces)
        {
            if (p.Evt == null) continue;
            foreach (var row in p.Evt)
                foreach (char c in row)
                {
                    int d = PieceDoc.DigitOf(c);
                    if (d >= 0 && d < doc.EvtSeq.Count)
                        foreach (int v in doc.EvtSeq[d]) used.Add(v);
                }
        }

        for (int i = 0; i < doc.Events.Count; i++)
        {
            var ev = doc.Events[i];
            if (ev.HasUnsetAction) list.Add($"事件 #{i} 有动作还没填目标");

            // "总是"触发不依赖绑定格，只有带「隐藏本格」动作的才必须有格子可藏
            bool needsCell = ev.Trigger != "always" || ev.HidesCell;
            if (!used.Contains(i) && needsCell) list.Add($"事件 #{i} 没挂到任何格子");

            // 重复触发的「总是」事件不允许执行命令：游戏里会跳过 run，这里先提醒
            if (ev.RepeatingRunsCommand)
                list.Add($"事件 #{i} 可重复触发，不能执行命令 —— run 动作在游戏里会被忽略");

            // 压力板触发器：坐标越界 / 指错了板子的，游戏里永远不激活
            if (ev.Trigger == "plate")
            {
                if (ev.Plate == null) list.Add($"事件 #{i} 是压力板触发，但还没填是哪块板");
                // 地图块留空 = 跟随"事件绑定格"所在的那块，这里没法预先验，交给游戏按链接算
                else if (!string.IsNullOrEmpty(ev.Plate.Piece))
                {
                    var target = doc.Pieces.FirstOrDefault(p => p.Id == ev.Plate.Piece);
                    if (target == null) list.Add($"事件 #{i} 的压力板指向不存在的地图块「{ev.Plate.Piece}」");
                    else if (ev.Plate.X < 0 || ev.Plate.Y < 0 ||
                             ev.Plate.X >= target.Width || ev.Plate.Y >= target.Height)
                        list.Add($"事件 #{i} 的压力板坐标 ({ev.Plate.X},{ev.Plate.Y}) 超出地图块「{target.Id}」");
                }
            }
        }

        // 箱子按口径要放在空瓦片格里：同一格又画了图块的，游戏里会两个叠着画
        int crateNotes = 0, crateTotal = 0;
        foreach (var p in doc.Pieces)
        {
            if (p.Solid == null) continue;
            for (int y = 0; y < p.Solid.Count; y++)
                for (int x = 0; x < p.Solid[y].Length; x++)
                {
                    if (PieceDoc.DigitOf(p.Solid[y][x]) != (int)SolidKind.Crate) continue;
                    if (PieceDoc.IsAir(p.GetCell(x, y))) continue;
                    crateTotal++;
                    if (crateNotes < 3)
                    {
                        list.Add($"{p.Id} 的 ({x},{y}) 是箱子，但那一格还画了图块");
                        crateNotes++;
                    }
                }
        }
        if (crateTotal > crateNotes) list.Add($"…还有 {crateTotal - crateNotes} 处箱子盖在图块上");

        if (doc.EvtSeq.Count > 62)
            list.Add($"事件组有 {doc.EvtSeq.Count} 组，超过一层格子能表示的 62 组");

        return list;
    }

    /// <summary>地图里出现、但图例没定义的字符（游戏会把它们当空气，编辑器里画成红色 '?'）。</summary>
    public static string UnknownChars(LevelDoc doc)
    {
        var set = new HashSet<char>();
        foreach (var p in doc.Pieces)
            foreach (var row in p.Rows)
                foreach (char c in row)
                    if (c != '.' && c != ' ' && !doc.Tiles.ContainsKey(c.ToString())) set.Add(c);
        return string.Join("", set.OrderBy(c => c));
    }
}

/// <summary>不带界面的读写往返自测：--selftest，结果写到 %TEMP%。</summary>
internal static class SelfTest
{
    public static void Run()
    {
        var lines = new List<string>();
        string dir = LevelStore.LevelsDir;
        lines.Add("assets levels: " + dir);
        if (!Directory.Exists(dir))
        {
            lines.Add("MISSING DIR");
        }
        else
        {
            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var a = LevelStore.Load(file);
                    string err = LevelStore.Validate(a);
                    string tmp = Path.Combine(Path.GetTempPath(), "wl-editor-roundtrip.json");
                    LevelStore.Save(tmp, a);
                    var b = LevelStore.Load(tmp);

                    bool same = a.Name == b.Name && a.MaxWindows == b.MaxWindows &&
                                a.Tiles.Count == b.Tiles.Count && a.Pieces.Count == b.Pieces.Count;
                    // 图例深比较：读写一圈后字段不能丢（以前只比数量，字段丢了也报 OK）
                    foreach (var kv in a.Tiles)
                    {
                        if (!same) break;
                        if (!b.Tiles.TryGetValue(kv.Key, out var tb) || !TileEquals(kv.Value, tb))
                            same = false;
                    }

                    for (int i = 0; i < a.Pieces.Count && same; i++)
                    {
                        var pa = a.Pieces[i];
                        var pb = b.Pieces[i];
                        bool varSame = SameLayer(pa.Var, pb.Var);
                        bool xfSame = SameLayer(pa.Xf, pb.Xf);
                        bool solidSame = SameLayer(pa.Solid, pb.Solid);
                        bool dimSame = SameLayer(pa.Dim, pb.Dim);
                        bool evtSame = SameLayer(pa.Evt, pb.Evt);
                        same = pa.Id == pb.Id && pa.Width == pb.Width && pa.Height == pb.Height &&
                               pa.Spawn.SequenceEqual(pb.Spawn) &&
                               varSame && xfSame && solidSame && dimSame && evtSame &&
                               pa.Rows.SequenceEqual(pb.Rows);
                    }
                    bool eventsSame = a.Events.Count == b.Events.Count &&
                                      a.Events.Zip(b.Events).All(t => SameEvent(t.First, t.Second));
                    if (!eventsSame) same = false;
                    string unknown = LevelStore.UnknownChars(a);
                    int evtCells = a.Pieces.Sum(p => CountEvtCells(p));
                    lines.Add($"{Path.GetFileName(file)}: validate='{err}' " +
                              $"roundtrip={(same ? "OK" : "MISMATCH")} " +
                              $"pieces={a.Pieces.Count} tiles={a.Tiles.Count} unknown='{unknown}' " +
                              $"var={a.Pieces.Sum(p => p.HasVar ? 1 : 0)} " +
                              $"xf={a.Pieces.Sum(p => p.HasXf ? 1 : 0)} " +
                              $"solid={a.Pieces.Sum(p => p.HasSolid ? 1 : 0)} " +
                              $"dim={a.Pieces.Sum(p => p.HasDim ? 1 : 0)} " +
                              $"events={a.Events.Count} evtCells={evtCells} " +
                              $"warn={LevelStore.Warnings(a).Count}");
                }
                catch (Exception ex)
                {
                    lines.Add($"{Path.GetFileName(file)}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        AddSeamCases(lines);
        AddFlagCase(lines);
        AddPlatformCase(lines);
        AddOrientationCase(lines);
        AddTileDefaultsCase(lines);
        AddGridOpsCase(lines);
        AddEventCase(lines);
        AddCratePlateCase(lines);
        string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-editor-selftest.txt");
        File.WriteAllLines(outPath, lines);
    }

    /// <summary>接缝判定的固定用例：两块 15x10、都有地面时左右贴合应只剩 9 格可通行。</summary>
    static void AddSeamCases(List<string> lines)
    {
        var doc = LevelDoc.NewLevel();
        doc.Tiles.Clear();
        doc.Tiles["g"] = new TileDoc { Sprite = 9, Solid = true };
        doc.Pieces.Clear();
        doc.Pieces.Add(SeamPiece("a", ground: true));
        doc.Pieces.Add(SeamPiece("b", ground: true));

        var side = new List<Rectangle>
        {
            new(0, 0, 150, 100),
            new(150, 0, 150, 100),
        };
        var s1 = SeamCheck.Stats(doc, side, 10);

        var overlap = new List<Rectangle>
        {
            new(0, 0, 150, 100),
            new(140, 0, 150, 100),
        };
        var s2 = SeamCheck.Stats(doc, overlap, 10);

        doc.Pieces[1] = SeamPiece("b", ground: false);
        var s3 = SeamCheck.Stats(doc, side, 10);

        // 上下相接：两侧都没地面时 15 列全通
        doc.Pieces[0] = SeamPiece("a", ground: false);
        var stacked = new List<Rectangle>
        {
            new(0, 0, 150, 100),
            new(0, 100, 150, 100),
        };
        var s4 = SeamCheck.Stats(doc, stacked, 10);

        lines.Add($"seam side-by-side(都有地面): seams={s1.seams} open={s1.open}/{s1.rows} overlap={s1.overlaps} " +
                  $"expect seams=1 open=9/10 overlap=0");
        lines.Add($"seam overlapping: seams={s2.seams} overlap={s2.overlaps} expect seams=0 overlap=1");
        lines.Add($"seam 只有一侧有地面: seams={s3.seams} open={s3.open}/{s3.rows} expect open=9/10（A 的地面那格仍挡人）");
        lines.Add($"seam stacked(都没地面): seams={s4.seams} open={s4.open}/{s4.rows} expect seams=1 open=15/15");
    }

    static PieceDoc SeamPiece(string id, bool ground)
    {
        var rows = Enumerable.Repeat(".", 10).Select(_ => new string('.', 15)).ToList();
        if (ground) rows[9] = new string('g', 15);
        return new PieceDoc { Id = id, Rows = rows, Spawn = new[] { 7, 5 } };
    }

    /// <summary>solid / dim 落盘往返，外加旧 back 层的合并兼容。</summary>
    static void AddFlagCase(List<string> lines)
    {
        var doc = LevelDoc.NewLevel();
        doc.Pieces[0].SetSolid(1, 1, 1);
        doc.Pieces[0].SetDim(1, 1, true);
        string json = LevelStore.Serialize(doc);
        var reloaded = LevelStore.Deserialize(json);
        bool hasKeys = json.Contains("\"solid\"") && json.Contains("\"dim\"");
        bool kept = reloaded.Pieces[0].GetSolid(1, 1) == 1 && reloaded.Pieces[0].GetDim(1, 1);

        // 旧格式：独立的 back 层应该被合并成"一格一图 + dim"
        var legacy = LevelStore.Deserialize(
            "{\"name\":\"x\",\"maxWindows\":1," +
            "\"tiles\":{\"g\":{\"sprite\":9,\"solid\":true}}," +
            "\"pieces\":[{\"id\":\"p\",\"rows\":[\"...\"]," +
            "\"back\":[\"ggg\"],\"spawn\":[0,0]}]}");
        bool merged = legacy.Pieces[0].GetCell(0, 0) == 'g' && legacy.Pieces[0].GetDim(0, 0);

        var cleared = LevelStore.Deserialize(json);
        cleared.Pieces[0].SetSolid(1, 1, -1);
        cleared.Pieces[0].SetDim(1, 1, false);
        cleared.Pieces[0].Normalize();
        bool clean = cleared.Pieces[0].Solid == null && cleared.Pieces[0].Dim == null;

        lines.Add($"solid/dim: keys={(hasKeys ? "yes" : "no")} cell={(kept ? "kept" : "lost")} " +
                  $"legacyBack={(merged ? "merged" : "lost")} emptyAfterClear={(clean ? "yes" : "no")} " +
                  $"expect keys=yes cell=kept legacyBack=merged emptyAfterClear=yes");
    }

    /// <summary>
    /// 单向平台：图例 platform:true 生效、逐格 '2' 能覆盖、'0' 能把平台关掉，
    /// 以及 '2' 落盘往返不丢。
    /// </summary>
    static void AddPlatformCase(List<string> lines)
    {
        var doc = LevelDoc.NewLevel();
        doc.Tiles["T"] = new TileDoc { Sprite = 23, Platform = true };
        var p = doc.Pieces[0];

        p.SetCell(0, 0, 'T');          // 跟随图例 → 平台
        p.SetCell(1, 0, 'T');
        p.SetSolid(1, 0, 1);           // 逐格改回实心
        p.SetCell(2, 0, 'g');          // 普通实心地面
        p.SetSolid(3, 0, 2);           // 普通瓦片逐格标成平台
        p.SetCell(4, 0, 'T');
        p.SetSolid(4, 0, 0);           // 平台瓦片逐格关成可穿过

        bool legend = p.EffectiveKind(doc, 0, 0) == SolidKind.Platform;
        bool perCellBlock = p.EffectiveKind(doc, 1, 0) == SolidKind.Block;
        bool normalSolid = p.EffectiveKind(doc, 2, 0) == SolidKind.Block;
        bool cellPlatform = p.EffectiveKind(doc, 3, 0) == SolidKind.Platform;
        bool explicitPass = p.EffectiveKind(doc, 4, 0) == SolidKind.Pass;
        bool solidIsNotPlatform = !p.EffectivePlatform(doc, 2, 0);

        string json = LevelStore.Serialize(doc);
        var back = LevelStore.Deserialize(json);
        bool roundtrip = back.Pieces[0].GetSolid(3, 0) == 2 && back.Tiles["T"].Platform == true;

        lines.Add($"platform: legend={legend} perCellBlock={perCellBlock} normal={normalSolid} " +
                  $"cellPlatform={cellPlatform} explicitPass={explicitPass} solidNotPlatform={solidIsNotPlatform} " +
                  $"roundtrip={roundtrip} expect all=true");
    }

    /// <summary>
    /// 瓦片默认值的接入自测：关卡显式值优先、没写时跟随工具、多精灵取主贴图，
    /// 画布判定和接缝判定口径一致。
    /// </summary>
    static void AddTileDefaultsCase(List<string> lines)
    {
        var meta = TileDefaults.Current;
        int solidSprite = meta.Tiles.FirstOrDefault(t => t.Solid)?.Sprite ?? -1;
        int openSprite = meta.Tiles.FirstOrDefault(t => !t.Solid)?.Sprite ?? -1;
        lines.Add($"tile defaults: path={TileDefaults.PathUsed} count={meta.Tiles.Count} " +
                  $"solidSprite={solidSprite} openSprite={openSprite} sprite8={meta.Find(8)?.Solid ?? false}");

        if (solidSprite < 0 || openSprite < 0)
        {
            lines.Add("tile defaults: SKIP（元数据里没有可用的实心/非实心格）");
            return;
        }

        var doc = LevelDoc.NewLevel();
        doc.Tiles.Clear();
        doc.Tiles["a"] = new TileDoc { Sprite = solidSprite };                          // 跟随默认
        doc.Tiles["b"] = new TileDoc { Sprite = solidSprite, Solid = false };           // 显式覆盖
        doc.Tiles["c"] = new TileDoc { Sprite = openSprite };
        doc.Tiles["d"] = new TileDoc { Sprites = new[] { openSprite, solidSprite } };   // 主贴图为准
        doc.Tiles["h"] = new TileDoc { Sprite = 8 };                                   // 固定观测点
        doc.Pieces.Clear();
        doc.Pieces.Add(new PieceDoc { Id = "p", Rows = new List<string> { "abcdh" }, Spawn = new[] { 0, 0 } });
        var p = doc.Pieces[0];

        bool follow = p.EffectiveSolid(doc, 0, 0);
        bool overridden = !p.EffectiveSolid(doc, 1, 0);
        bool open = !p.EffectiveSolid(doc, 2, 0);
        bool firstWins = !p.EffectiveSolid(doc, 3, 0);
        bool seamSame = SeamCheck.IsSolid(doc, 'a') && !SeamCheck.IsSolid(doc, 'b') &&
                        !SeamCheck.IsSolid(doc, 'c') && !SeamCheck.IsSolid(doc, 'd');
        p.SetSolid(0, 0, 0);
        bool perCell = !p.EffectiveSolid(doc, 0, 0);      // 逐格标记最高优先
        bool sprite8 = p.EffectiveSolid(doc, 4, 0) == (meta.Find(8)?.Solid ?? false);

        lines.Add($"tile defaults: follow={follow} explicit={overridden} open={open} " +
                  $"firstSprite={firstWins} seamSame={seamSame} perCell={perCell} sprite8={sprite8} " +
                  $"expect all=true");
    }

    /// <summary>
    /// 框选区域操作的固定用例：五层一起搬、空格透明、越界裁剪、源目标重叠、清空不留幽灵层。
    /// </summary>
    static void AddGridOpsCase(List<string> lines)
    {
        static PieceDoc Blank(int w, int h) => new()
        {
            Id = "t", Spawn = new[] { 0, 0 },
            Rows = Enumerable.Repeat(new string('.', w), h).ToList(),
        };
        static bool Same(PieceDoc p, int x, int y, GridCell c) => GridOps.Read(p, x, y) == c;

        var a = Blank(6, 4);
        var full = new GridCell('g', 2, 5, 1, true, 1);      // 图 + 五个标记全有
        var airMarked = new GridCell('.', -1, 0, 0, false, -1); // 空气但有「可穿过」标记，算内容
        var airEmpty = GridCell.Air;                       // 真空格，粘贴时透明
        var other = new GridCell('w', 1, 3, -1, false, -1);

        GridOps.Write(a, 1, 1, full);
        GridOps.Write(a, 2, 1, airMarked);
        GridOps.Write(a, 3, 1, other);
        GridOps.Write(a, 1, 2, full);
        GridOps.Write(a, 2, 2, airEmpty);
        GridOps.Write(a, 4, 2, other);   // 目标原有内容，透明空格应保留它
        GridOps.Write(a, 4, 3, other);

        var buf = GridBuffer.Capture(a, new Rectangle(1, 1, 3, 2));
        bool fiveLayers = buf.At(0, 0) == full && buf.At(1, 0) == airMarked &&
                          buf.At(2, 0) == other && buf.At(0, 1) == full;
        bool transparency = buf.At(1, 1).IsEmpty && !airMarked.IsEmpty;
        bool outOfBoundsAir = GridBuffer.Capture(a, new Rectangle(4, 2, 3, 2)).At(2, 1).IsEmpty;

        // 粘贴到 (3,2)：(4,3) 对应源里的真空格，应保留 w；(4,2) 对应 airMarked，应被整格覆盖
        GridClipboard.Data = buf;
        int written = GridOps.Paste(a, buf, 3, 2);
        bool copied = Same(a, 3, 2, full) && Same(a, 4, 2, airMarked) && Same(a, 3, 3, full);
        bool kept = Same(a, 4, 3, other);
        bool sizeKept = a.Width == 6 && a.Height == 4;

        // 越界裁剪：贴到右下角，不能改地图尺寸也不能抛异常
        int clipped = GridOps.Paste(a, buf, 5, 3);
        bool clipOk = a.Width == 6 && a.Height == 4 && clipped >= 0 &&
                      GridOps.InBounds(a, 5, 3) && !GridOps.InBounds(a, 6, 3);

        // 源 / 目标重叠的移动：先快照、清空源、再写目标，透明格要恢复目标原值
        var b = Blank(6, 4);
        GridOps.Write(b, 1, 1, full);
        GridOps.Write(b, 3, 1, other);      // 落在目标里、且被源里的空格对着
        GridOps.Write(b, 1, 2, full);
        int moved = GridOps.Move(b, new Rectangle(1, 1, 3, 2), 2, 1);
        bool overlapOk = Same(b, 2, 1, full) && Same(b, 3, 1, other) &&
                         Same(b, 1, 1, GridCell.Air) && Same(b, 2, 2, full);

        // 清空：五层都归位；整块清空后 Normalize 不该留下空壳层
        GridOps.Clear(b, new Rectangle(2, 1, 2, 2));
        bool clearedCells = Same(b, 2, 1, GridCell.Air) && Same(b, 3, 2, GridCell.Air);
        var c = Blank(4, 3);
        GridOps.Write(c, 1, 1, full);
        GridOps.Clear(c, new Rectangle(0, 0, 4, 3));
        bool cleared = clearedCells && !c.HasVar && !c.HasXf && !c.HasSolid && !c.HasDim && !c.HasEvt;

        // 落盘往返后五层值不能丢
        var holder = new LevelDoc { Name = "grid", Pieces = new List<PieceDoc> { a } };
        var back = LevelStore.Deserialize(LevelStore.Serialize(holder)).Pieces[0];
        bool roundtrip = Same(back, 3, 2, full) && Same(back, 4, 2, airMarked) && Same(back, 4, 3, other);

        // 拖框两点随便哪个先，都要得到同一个矩形
        var drag = GridOps.FromDrag(new Point(4, 3), new Point(2, 1));
        bool dragOk = drag == new Rectangle(2, 1, 3, 3);

        lines.Add($"grid ops: sixLayers={fiveLayers} transparent={transparency} outOfBounds={outOfBoundsAir} " +
                  $"paste={copied} keptTarget={kept} sizeKept={sizeKept} clip={clipOk} moved={moved} " +
                  $"overlap={overlapOk} cleared={cleared} roundtrip={roundtrip} drag={dragOk} " +
                  $"clipboard={GridClipboard.Description} expect all=true");
    }

    /// <summary>
    /// 箱子与压力板：solid 层 '3' 落盘往返不丢、判定成 Crate、
    /// 压力板触发器与 hide 动作写进 JSON 再读回来还是原样。
    /// </summary>
    static void AddCratePlateCase(List<string> lines)
    {
        var doc = LevelDoc.NewLevel();
        var p = doc.Pieces[0];
        doc.Tiles["P"] = new TileDoc { Sprite = 69 };

        p.SetCell(2, 1, '.');
        p.SetSolid(2, 1, (int)SolidKind.Crate);
        p.SetCell(3, 1, 'P');

        bool kindCrate = p.EffectiveKind(doc, 2, 1) == SolidKind.Crate;
        bool crateSolid = p.EffectiveSolid(doc, 2, 1);
        bool plateCell = doc.Tiles["P"].EffectivePlateFlag;

        doc.Events.Add(new EventDoc
        {
            Name = "板开门",
            Trigger = "plate",
            Plate = new PlateRefDoc { Piece = p.Id, X = 3, Y = 1 },
            Actions = new List<ActionDoc> { new() { Type = "hide" } },
        });
        doc.SetCellEvents(p, 5, 2, new[] { 0 });

        string json = LevelStore.Serialize(doc);
        bool keys = json.Contains("\"plate\"") && json.Contains("\"hide\"");
        var back = LevelStore.Deserialize(json);
        var bp = back.Pieces[0];
        bool crateKept = bp.GetSolid(2, 1) == (int)SolidKind.Crate;
        bool evtKept = back.Events.Count == 1 && back.Events[0].Trigger == "plate" &&
                       back.Events[0].Plate is { } r && r.Piece == p.Id && r.X == 3 && r.Y == 1 &&
                       back.Events[0].Actions is { Count: 1 } acts && acts[0].Type == "hide";
        bool boundKept = SameSeq(back.SeqOf(bp, 5, 2), new List<int> { 0 });

        // 清掉箱子后 solid 层不该留空壳
        bp.SetSolid(2, 1, -1);
        bp.Normalize();
        bool clean = bp.Solid == null;

        lines.Add($"crate/plate: kindCrate={kindCrate} solid={crateSolid} plateFlag={plateCell} " +
                  $"keys={(keys ? "yes" : "no")} crate={(crateKept ? "kept" : "lost")} " +
                  $"event={(evtKept ? "kept" : "lost")} bound={boundKept} emptyAfterClear={(clean ? "yes" : "no")} " +
                  $"expect all=true");
    }

    static bool SameLayer(List<string>? a, List<string>? b) =>
        a == null || b == null ? a == b : a.SequenceEqual(b);

    static bool SameRange(float[]? a, float[]? b) =>
        a == null ? b == null : b != null && a.SequenceEqual(b);

    static bool SameEvent(EventDoc a, EventDoc b)
    {
        var ca = a.Conditions ?? new List<ConditionDoc>();
        var cb = b.Conditions ?? new List<ConditionDoc>();
        var aa = a.Actions ?? new List<ActionDoc>();
        var ab = b.Actions ?? new List<ActionDoc>();

        if (ca.Count != cb.Count || aa.Count != ab.Count) return false;

        for (int i = 0; i < ca.Count; i++)
        {
            if (ca[i].Piece != cb[i].Piece) return false;
            if (!SameRange(ca[i].CamX, cb[i].CamX)) return false;
            if (!SameRange(ca[i].CamY, cb[i].CamY)) return false;
        }
        for (int i = 0; i < aa.Count; i++)
        {
            if (aa[i].Type != ab[i].Type) return false;
            if (aa[i].Level != ab[i].Level) return false;
            if (aa[i].Target != ab[i].Target) return false;
            if (aa[i].Args != ab[i].Args) return false;
            if (aa[i].Sound != ab[i].Sound) return false;
        }
        // 压力板触发器：写进 JSON 再读回来不能丢
        if (a.Trigger != b.Trigger) return false;
        if (a.Repeat != b.Repeat) return false;
        if (a.Plate == null || b.Plate == null)
        {
            if (a.Plate != b.Plate) return false;
        }
        else if (a.Plate.Piece != b.Plate.Piece || a.Plate.X != b.Plate.X || a.Plate.Y != b.Plate.Y)
            return false;

        return a.Name == b.Name;
    }

    static bool SameSeq(List<int>? a, List<int>? b) =>
        a == null || b == null ? a == b : a.SequenceEqual(b);

    static int CountEvtCells(PieceDoc p) =>
        p.Evt?.Sum(r => r.Count(c => PieceDoc.DigitOf(c) >= 0)) ?? 0;

    /// <summary>
    /// 网格事件：一格多事件（事件组表）、多条动作、条件，落盘往返 + 组表整理 + 删事件重映射。
    /// </summary>
    static void AddEventCase(List<string> lines)
    {
        var doc = LevelDoc.NewLevel();
        var p = doc.Pieces[0];

        doc.Events.Add(new EventDoc
        {
            Name = "去第二关",
            Trigger = "touch",
            Actions = new List<ActionDoc>
            {
                new() { Type = "goto", Level = "level2" },
                new() { Type = "run", Target = "https://example.com" },
            },
            Conditions = new List<ConditionDoc>
            {
                new() { Piece = "*", CamX = new[] { 2f, 12f } },
                new() { Piece = p.Id, CamX = new[] { 0f, 6f }, CamY = new[] { 1.5f, 8f } },
            },
        });
        doc.Events.Add(new EventDoc { Name = "草稿", Actions = new List<ActionDoc> { new() { Type = "goto", Level = "" } } });
        doc.Events.Add(new EventDoc { Name = "开程序", Actions = new List<ActionDoc> { new() { Type = "run", Target = "notepad.exe", Args = "a.txt" }, new() { Type = "sfx", Sound = "unlock" } } });
        // 「总是」触发器：只跑一次的带条件隐藏；可重复的挂一条 run（校验要提醒会被忽略）
        doc.Events.Add(new EventDoc
        {
            Name = "镜头滚到位就藏",
            Trigger = "always",
            Actions = new List<ActionDoc> { new() { Type = "hide" } },
            Conditions = new List<ConditionDoc> { new() { Piece = "*", CamX = new[] { 0f, 4f } } },
        });
        doc.Events.Add(new EventDoc
        {
            Name = "每帧跑命令（会被忽略）",
            Trigger = "always",
            Repeat = true,
            Actions = new List<ActionDoc> { new() { Type = "run", Target = "x.exe" } },
        });
        // 播放音效：没选音效的当成"还没填"，编辑器要标红
        doc.Events.Add(new EventDoc
        {
            Name = "音效没选",
            Actions = new List<ActionDoc> { new() { Type = "sfx", Sound = "" } },
        });

        // 一格挂两条事件；另一格挂同样的组合 → 应该复用同一个组
        doc.SetCellEvents(p, 3, 3, new[] { 0, 2 });
        doc.SetCellEvents(p, 5, 3, new[] { 0, 2 });
        doc.SetCellEvents(p, 4, 3, new[] { 1 });

        bool sharedGroup = p.GetEvtSeq(3, 3) == p.GetEvtSeq(5, 3) && p.GetEvtSeq(3, 3) != p.GetEvtSeq(4, 3);
        bool seqOk = SameSeq(doc.SeqOf(p, 3, 3), new List<int> { 0, 2 }) &&
                     SameSeq(doc.SeqOf(p, 4, 3), new List<int> { 1 });

        string json = LevelStore.Serialize(doc);
        bool keys = json.Contains("\"events\"") && json.Contains("\"evtSeq\"") &&
                    json.Contains("\"actions\"") && json.Contains("\"conditions\"") &&
                    json.Contains("\"sound\"") && json.Contains("unlock");
        bool noLegacy = !json.Contains("\"action\":") && !json.Contains("\"when\":");

        var back = LevelStore.Deserialize(json);
        bool eventsKept = back.Events.Count == 6 &&
                          back.Events.Zip(doc.Events).All(t => SameEvent(t.First, t.Second));
        bool cellsKept = SameSeq(back.SeqOf(back.Pieces[0], 3, 3), new List<int> { 0, 2 }) &&
                         SameSeq(back.SeqOf(back.Pieces[0], 4, 3), new List<int> { 1 });
        bool unsetFlag = back.Events[1].HasUnsetAction && !back.Events[0].HasUnsetAction &&
                         back.Events[5].HasUnsetAction;
        bool sfxKept = back.Events[2].Actions is { Count: 2 } sfxActs &&
                       sfxActs[1].Type == "sfx" && sfxActs[1].Sound == "unlock" &&
                       sfxActs[1].Describe().Contains("开锁");
        bool warned = LevelStore.Warnings(back).Any(w => w.Contains("有动作还没填目标"));

        // 「总是」触发器：repeat 往返不丢；带 hide 不挂格子要警告，不带 hide 的不挂格子没事；
        // 重复 + run 要提醒会被忽略
        bool repeatKept = back.Events[3].Trigger == "always" && !back.Events[3].Repeat &&
                          back.Events[4].Trigger == "always" && back.Events[4].Repeat;
        bool hideCellWarned = LevelStore.Warnings(back).Any(w => w.Contains("#3") && w.Contains("没挂到任何格子"));
        bool noCellOk = !LevelStore.Warnings(back).Any(w => w.Contains("#4") && w.Contains("没挂到任何格子"));
        bool runWarned = LevelStore.Warnings(back).Any(w => w.Contains("#4") && w.Contains("不能执行命令"));

        // 删掉事件 #0：格子里的引用要跟着消失、>=1 的下标整体前移，组表压紧
        back.RemoveEvent(0);
        bool remapped = SameSeq(back.SeqOf(back.Pieces[0], 3, 3), new List<int> { 1 }) &&
                        SameSeq(back.SeqOf(back.Pieces[0], 4, 3), new List<int> { 0 }) &&
                        back.Events.Count == 5 &&
                        back.Events[1].Actions?[0].Target == "notepad.exe";

        // 清空格子后，保存时组表不该留下没人引用的组
        var tidy = LevelStore.Deserialize(json);
        for (int y = 0; y < tidy.Pieces[0].Height; y++)
            for (int x = 0; x < tidy.Pieces[0].Width; x++)
                tidy.Pieces[0].SetEvtSeq(x, y, -1);
        tidy.CompactEvt();
        bool dropped = tidy.EvtSeq.Count == 0 && tidy.Pieces[0].Evt == null;

        // 旧格式（单动作 action + when）要能读成新结构
        var legacy = LevelStore.Deserialize(
            "{\"name\":\"x\",\"maxWindows\":1,\"tiles\":{\"g\":{\"sprite\":9}}," +
            "\"events\":[{\"action\":{\"type\":\"goto\",\"level\":\"level2\"}," +
            "\"when\":[{\"piece\":\"*\",\"camX\":[0,5]}]}]," +
            "\"pieces\":[{\"id\":\"p\",\"rows\":[\"g\"],\"evt\":[\"0\"],\"spawn\":[0,0]}]}");
        bool upgraded = legacy.Events.Count == 1 &&
                        legacy.Events[0].Actions?.Count == 1 &&
                        legacy.Events[0].Actions?[0].Level == "level2" &&
                        legacy.Events[0].Conditions?.Count == 1 &&
                        SameSeq(legacy.SeqOf(legacy.Pieces[0], 0, 0), new List<int> { 0 });

        // 没有事件的关卡不该写 events / evtSeq 键
        string plain = LevelStore.Serialize(LevelDoc.NewLevel());
        bool noKeys = !plain.Contains("\"events\"") && !plain.Contains("\"evtSeq\"");

        lines.Add($"grid events: keys={(keys ? "yes" : "no")} noLegacy={(noLegacy ? "yes" : "no")} " +
                  $"sharedGroup={sharedGroup} seq={(seqOk ? "ok" : "bad")} events={(eventsKept ? "ok" : "lost")} " +
                  $"cells={(cellsKept ? "ok" : "lost")} unsetFlag={unsetFlag} sfx={(sfxKept ? "ok" : "lost")} warned={warned} " +
                  $"repeatKept={repeatKept} hideCellWarned={hideCellWarned} noCellOk={noCellOk} runWarned={runWarned} " +
                  $"remapped={remapped} droppedUnused={dropped} legacyUpgrade={upgraded} " +
                  $"noKeyWhenEmpty={(noKeys ? "yes" : "no")} " +
                  $"expect all=true");
    }

    /// <summary>var / xf 落盘往返：写一格 → 序列化 → 读回；清空后不该留下空壳。</summary>
    static void AddOrientationCase(List<string> lines)
    {
        var doc = LevelDoc.NewLevel();
        doc.Pieces[0].SetVar(2, 2, 3);
        doc.Pieces[0].SetXf(2, 2, 5);
        string json = LevelStore.Serialize(doc);
        var back = LevelStore.Deserialize(json);
        bool hasKeys = json.Contains("\"var\"") && json.Contains("\"xf\"");
        bool kept = back.Pieces[0].GetVar(2, 2) == 3 && back.Pieces[0].GetXf(2, 2) == 5;

        var cleared = LevelStore.Deserialize(json);
        cleared.Pieces[0].SetVar(2, 2, -1);
        cleared.Pieces[0].SetXf(2, 2, 0);
        cleared.Pieces[0].Normalize();
        bool clean = cleared.Pieces[0].Var == null && cleared.Pieces[0].Xf == null;

        lines.Add($"var/xf paint: keys={(hasKeys ? "yes" : "no")} cell={(kept ? "kept" : "lost")} " +
                  $"emptyAfterClear={(clean ? "yes" : "no")} expect keys=yes cell=kept emptyAfterClear=yes");
    }

    static bool TileEquals(TileDoc a, TileDoc b) =>
        a.Sprite == b.Sprite &&
        a.Solid == b.Solid && a.Platform == b.Platform &&
        a.Water == b.Water && a.Spring == b.Spring && a.Climb == b.Climb &&
        a.Plate == b.Plate &&
        (a.Sprites == null ? b.Sprites == null : b.Sprites != null && a.Sprites.SequenceEqual(b.Sprites));
}
