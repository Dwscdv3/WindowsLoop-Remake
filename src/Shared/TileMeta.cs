using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsLoop.Tileset;

/// <summary>
/// 一格的阻挡类型。数值直接对应关卡 solid 层的一个字符（'0' / '1' / '2' / '3'），
/// 游戏、关卡编辑器、瓦片工具三端共用这一套，别各写各的。
///
/// Block 挡四面；Platform 只挡"从上方落下"，从下往上、左右走过都不挡
/// （暂时没有"站在平台上按下往下穿"的玩法）。
///
/// Crate 是**箱子**：和 Block 一样挡住玩家，但它不是地形，而是一个能被推动、
/// 会自己下落的实体。数据上仍然写在 solid 层里（一格一个字符 '3'），
/// 而且必须落在空瓦片格上 —— 地图里没有对应的图块，箱子由引擎单独画。
/// 游戏读关卡时会把 '3' 的格子抽成箱子实体，之后箱子的位置归实体自己管。
/// </summary>
public enum SolidKind
{
    Pass = 0,
    Block = 1,
    Platform = 2,
    Crate = 3,
}

/// <summary>
/// 一个瓦片的元数据：显示名、分组，以及**默认物理属性**。
///
/// 这份数据是全局的（不属于任何一关）：瓦片工具编辑它，关卡编辑器拿它给图集点选兜底，
/// 游戏在关卡 JSON 没写某个属性时也用它。关卡里写了就以关卡为准 —— 元数据只是默认值。
/// </summary>
public sealed class TileMeta
{
    [JsonPropertyName("sprite")] public int Sprite { get; set; } = -1;

    /// <summary>显示名，例如「草土地面·上中」。只用于编辑器/工具，不影响渲染。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>分组名，例如「地面」「洞壁」。用于工具里归类，不写进关卡。</summary>
    [JsonPropertyName("group")] public string Group { get; set; } = "";

    [JsonPropertyName("solid")] public bool Solid { get; set; }

    /// <summary>
    /// 单向平台：可以从下方跳穿上去，落下来又能踩在上面。
    /// Solid 同时为 true 时按实心算（实心优先），别指望一个瓦片既当墙又当平台。
    /// </summary>
    [JsonPropertyName("platform")] public bool Platform { get; set; }

    [JsonPropertyName("water")] public bool Water { get; set; }
    [JsonPropertyName("climb")] public bool Climb { get; set; }
    [JsonPropertyName("spring")] public bool Spring { get; set; }

    /// <summary>
    /// 压力板：玩家或箱子与这一格重叠（身体所在格 / 脚底那一格）就算"被踩下"。
    /// 它不影响阻挡，只是一条可以被事件触发器读到的状态。
    /// </summary>
    [JsonPropertyName("plate")] public bool Plate { get; set; }

    /// <summary>可选备注，写点「为什么这块是实心」之类的说明。</summary>
    [JsonPropertyName("note")] public string? Note { get; set; }

    public TileMeta Clone() => new()
    {
        Sprite = Sprite,
        Name = Name,
        Group = Group,
        Solid = Solid,
        Platform = Platform,
        Water = Water,
        Climb = Climb,
        Spring = Spring,
        Plate = Plate,
        Note = Note,
    };
}

/// <summary>assets/tiles.meta.json 的内容。</summary>
public sealed class TileMetaFile
{
    public const string FileName = "tiles.meta.json";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>按 sprite 下标排列的条目；顺序只是为了 diff 好读。</summary>
    [JsonPropertyName("tiles")] public List<TileMeta> Tiles { get; set; } = new();

    [JsonIgnore] Dictionary<int, TileMeta>? _index;

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TileMetaFile Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>从一段 JSON 直接解析：素材编在程序集里时没有文件可读。</summary>
    public static TileMetaFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<TileMetaFile>(json, Opts);
        if (file == null) return new TileMetaFile();
        file.Tiles ??= new List<TileMeta>();
        file._index = null;
        return file;
    }

    /// <summary>文件不存在或读坏了都返回空表：元数据只是默认值，缺了不能让游戏起不来。</summary>
    public static TileMetaFile LoadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? Load(path) : new TileMetaFile();
        }
        catch
        {
            return new TileMetaFile();
        }
    }

    public void Save(string path)
    {
        Sort();
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts) + Environment.NewLine);
        _index = null;
    }

    public void Sort() => Tiles.Sort((a, b) => a.Sprite.CompareTo(b.Sprite));

    public void Invalidate() => _index = null;

    public TileMeta? Find(int sprite)
    {
        if (_index == null)
        {
            _index = new Dictionary<int, TileMeta>();
            foreach (var t in Tiles) _index[t.Sprite] = t;   // 重复条目以最后一条为准
        }
        return _index.TryGetValue(sprite, out var hit) ? hit : null;
    }

    /// <summary>取一条，没有就新建（工具里「补全缺失项」用）。</summary>
    public TileMeta Ensure(int sprite)
    {
        var hit = Find(sprite);
        if (hit != null) return hit;
        hit = new TileMeta { Sprite = sprite };
        Tiles.Add(hit);
        Invalidate();
        return hit;
    }

    public void Remove(int sprite)
    {
        Tiles.RemoveAll(t => t.Sprite == sprite);
        Invalidate();
    }
}

/// <summary>从可执行目录往上找游戏的 assets 目录。三个程序各自在自己的 bin 里，靠这个统一定位。</summary>
public static class AssetLocator
{
    public static string FindAssetsDir()
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

    /// <summary>源码树里的那一份，是唯一可写的数据源（瓦片工具保存的就是它）。</summary>
    public static string SourceTileMetaPath => Path.Combine(FindAssetsDir(), TileMetaFile.FileName);

    public static string SourceTileSheetPath => Path.Combine(FindAssetsDir(), "tiles.png");

    /// <summary>
    /// 游戏 / 关卡编辑器读自己输出目录里的副本（build 时复制进去），没有副本才退回源码树。
    /// 所以改完瓦片工具后要重新 build.cmd，两边才会同步到新默认值。
    /// 瓦片工具自己不用这个属性，它固定读写 SourceTileMetaPath。
    /// </summary>
    public static string TileMetaPath
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "assets", TileMetaFile.FileName);
            return File.Exists(local) ? local : SourceTileMetaPath;
        }
    }

    public static string TileSheetPath
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "assets", "tiles.png");
            return File.Exists(local) ? local : SourceTileSheetPath;
        }
    }
}

/// <summary>
/// 三个程序共用的「瓦片默认属性」入口。
///
/// 优先级（两边必须一致，否则编辑器画布和游戏里看到的碰撞会对不上）：
///   逐格 solid 标记  >  关卡 tiles 图例里显式写的属性  >  tiles.meta.json 的默认值  >  false
///
/// 多精灵图例按**主贴图**（sprites 的第一个）取默认值：跟关卡编辑器 TileDoc.FirstSprite
/// 是同一套约定，避免「背景块里混了一格实心贴图」把整块背景变成墙。
/// </summary>
public static class TileDefaults
{
    static TileMetaFile? _cache;
    static string _cachePath = "";

    public static TileMetaFile Current
    {
        get
        {
            if (_cache == null) LoadFrom(AssetLocator.TileMetaPath);
            return _cache!;
        }
    }

    public static string PathUsed => _cachePath;

    /// <summary>指定文件加载（瓦片工具保存后、编辑器手动重载时调用）。</summary>
    public static TileMetaFile LoadFrom(string path)
    {
        _cachePath = path;
        _cache = TileMetaFile.LoadOrEmpty(path);
        return _cache;
    }

    /// <summary>
    /// 从内存里的一段 JSON 装载。单文件分发的游戏走这条：meta 编在程序集里，
    /// 没有文件路径可传，也就不该在磁盘上留副本。
    /// </summary>
    public static TileMetaFile LoadFromJson(string json, string label)
    {
        _cachePath = label;
        try { _cache = TileMetaFile.Parse(json); }
        catch { _cache = new TileMetaFile(); }   // 默认值读坏了不能把游戏拖死，全按 false 兜底
        return _cache;
    }

    /// <summary>重新读同一份文件，用于「重新载入瓦片默认值」菜单。</summary>
    public static void Reload() => LoadFrom(string.IsNullOrEmpty(_cachePath) ? AssetLocator.TileMetaPath : _cachePath);

    /// <summary>按主贴图取登记条目；没登记返回 null（调用方一律按 false 兜底，绝不让游戏起不来）。</summary>
    public static TileMeta? ForSprites(IEnumerable<int> sprites)
    {
        foreach (int s in sprites) return Current.Find(s);
        return null;
    }

    public static TileMeta? ForSprite(int sprite) => Current.Find(sprite);

    /// <summary>关卡显式写了（含 false）就以关卡为准，没写才看瓦片默认。</summary>
    public static bool SolidOf(bool? explicitValue, IEnumerable<int> sprites) =>
        explicitValue ?? (ForSprites(sprites)?.Solid ?? false);

    public static bool PlatformOf(bool? explicitValue, IEnumerable<int> sprites) =>
        explicitValue ?? (ForSprites(sprites)?.Platform ?? false);

    public static bool WaterOf(bool? explicitValue, IEnumerable<int> sprites) =>
        explicitValue ?? (ForSprites(sprites)?.Water ?? false);

    public static bool ClimbOf(bool? explicitValue, IEnumerable<int> sprites) =>
        explicitValue ?? (ForSprites(sprites)?.Climb ?? false);

    public static bool SpringOf(bool? explicitValue, IEnumerable<int> sprites) =>
        explicitValue ?? (ForSprites(sprites)?.Spring ?? false);

    public static bool PlateOf(bool? explicitValue, IEnumerable<int> sprites) =>
        explicitValue ?? (ForSprites(sprites)?.Plate ?? false);
}
