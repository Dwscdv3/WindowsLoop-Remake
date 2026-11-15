namespace WindowsLoop.LevelEditor;

/// <summary>一条预设图例：字符、显示名、分组、默认属性。</summary>
internal sealed record TilePreset(string Key, string Name, string Group, TileDoc Def);

/// <summary>
/// 预设瓦片目录。名字和分组只活在编辑器里，不写进关卡 JSON；
/// sprite 编号取自 assets/tiles.png（Caves of Gallet 素材），随时可以换。
///
/// 注意：预设里写死的 solid / platform / climb / spring 是「关卡图例显式值」，按既定优先级
/// 高于瓦片工具的默认值（例如 L 梯子用 65 号水纹贴图但语义是可攀爬）。
/// 想跟随工具默认值，用图集点选新建图例即可（新条目一律不写死属性）。
/// </summary>
internal static class TilePresets
{
    public const string GGroupGround = "地面 / 岩体";
    public const string GGroupWall = "装饰墙体";
    public const string GGroupGimmick = "机关";
    public const string GGroupItem = "道具";
    public const string GGroupDecor = "装饰";
    public const string GGroupBack = "背景块";

    public static readonly IReadOnlyList<TilePreset> All = new List<TilePreset>
    {
        // ---- 地面 / 岩体：关卡的基本骨架 ----
        new("g", "草土地面", GGroupGround, new TileDoc { Sprite = 9, Solid = true }),
        new("#", "岩体", GGroupGround, new TileDoc { Sprites = new[] { 0, 1, 2, 26 }, Solid = true }),

        // ---- 装饰墙体：实心，但材质各不相同，用来区分房间/区域 ----
        new("=", "石砖墙", GGroupWall, new TileDoc { Sprite = 11, Solid = true }),
        new("m", "灰石砌墙", GGroupWall, new TileDoc { Sprites = new[] { 30, 33, 34 }, Solid = true }),
        new("r", "碎石墙", GGroupWall, new TileDoc { Sprites = new[] { 32, 47, 4 }, Solid = true }),
        new("s", "砂岩墙", GGroupWall, new TileDoc { Sprites = new[] { 35, 36, 44 }, Solid = true }),
        new("w", "木墙 / 梁", GGroupWall, new TileDoc { Sprites = new[] { 14, 22, 23, 46 }, Solid = true }),
        new("p", "立柱", GGroupWall, new TileDoc { Sprites = new[] { 20, 40, 43 }, Solid = true }),

        // ---- 机关：沿用引擎已有的四种行为 ----
        new("L", "梯子", GGroupGimmick, new TileDoc { Sprite = 65, Climb = true }),
        new("J", "弹簧", GGroupGimmick, new TileDoc { Sprite = 49, Solid = true, Spring = true }),
        new("~", "水面", GGroupGimmick, new TileDoc { Sprite = 81, Water = true }),

        // 单向平台：下方能顶穿，落下来能站住，侧面不挡
        new("T", "木板平台（单向）", GGroupGimmick, new TileDoc { Sprite = 23, Platform = true }),

        // ---- 道具：宝石只是普通瓦片，"碰到会怎样"由网格事件决定 ----
        new("@", "宝石", GGroupItem, new TileDoc { Sprite = 93 }),

        // ---- 装饰：不实心，纯画面 ----
        new("v", "草丛", GGroupDecor, new TileDoc { Sprite = 86 }),
        new("x", "藤蔓", GGroupDecor, new TileDoc { Sprites = new[] { 24, 25, 26 } }),
        new("c", "火把", GGroupDecor, new TileDoc { Sprites = new[] { 21, 45 } }),
        new("o", "橙晶", GGroupDecor, new TileDoc { Sprites = new[] { 38, 39, 31 } }),
        new("i", "蓝晶", GGroupDecor, new TileDoc { Sprites = new[] { 48, 50 } }),

        // ---- 背景块：画在 back 层，不参与碰撞 ----
        new("1", "暗室底", GGroupBack, new TileDoc { Sprites = new[] { 0, 1, 4, 5 } }),
        new("2", "暗岩底", GGroupBack, new TileDoc { Sprites = new[] { 3, 6, 7 } }),
        new("3", "夜色底", GGroupBack, new TileDoc { Sprites = new[] { 88, 89, 90, 91 } }),
        new("4", "深蓝底", GGroupBack, new TileDoc { Sprites = new[] { 82, 83 } }),
        new("5", "苔石底", GGroupBack, new TileDoc { Sprites = new[] { 57, 58 } }),
    };

    static readonly Dictionary<char, TilePreset> ByKey =
        All.ToDictionary(p => p.Key[0], p => p);

    public static TilePreset? Find(char c) => ByKey.TryGetValue(c, out var p) ? p : null;

    public static string NameOf(char c) => Find(c)?.Name ?? "";

    public static string GroupOf(char c) => Find(c)?.Group ?? "自定义";

    /// <summary>调色板排序：按预设分组，组内保持目录顺序，自定义字符排最后。</summary>
    public static IEnumerable<KeyValuePair<string, TileDoc>> InPaletteOrder(Dictionary<string, TileDoc> tiles)
    {
        var order = All.Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i);
        string[] groups = { GGroupGround, GGroupWall, GGroupGimmick, GGroupItem, GGroupDecor, GGroupBack };
        return tiles
            .OrderBy(kv => Array.IndexOf(groups, GroupOf(kv.Key[0])) is var g && g >= 0 ? g : groups.Length)
            .ThenBy(kv => order.TryGetValue(kv.Key, out int i) ? i : int.MaxValue)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal);
    }

    /// <summary>新关卡的默认图例：常用地形 + 平台 + 三种背景块，直接在调色板里可点。</summary>
    public static Dictionary<string, TileDoc> DefaultTiles()
    {
        string[] keys = { "g", "#", "=", "v", "~", "L", "J", "T", "@", "1", "2", "3" };
        var dict = new Dictionary<string, TileDoc>();
        foreach (string k in keys)
        {
            var preset = Find(k[0]);
            if (preset != null) dict[k] = preset.Def.Clone();
        }
        return dict;
    }

    /// <summary>把某条预设加进关卡图例（字符被占用就换一个空字符）。</summary>
    public static char? AddTo(Dictionary<string, TileDoc> tiles, TilePreset preset)
    {
        if (!tiles.ContainsKey(preset.Key))
        {
            tiles[preset.Key] = preset.Def.Clone();
            return preset.Key[0];
        }
        const string pool = "abcdefghijklmnopqrstuvwxyzKLMNOPQRSTUVWXYZ0123456789";
        foreach (char c in pool)
        {
            if (c == '.' || c == ' ' || tiles.ContainsKey(c.ToString())) continue;
            tiles[c.ToString()] = preset.Def.Clone();
            return c;
        }
        return null;
    }
}
