using System.Drawing;
using System.Text;
using WindowsLoop.Tileset;

namespace WindowsLoop;

/// <summary>
/// 素材仓库：关卡 JSON、音频、图集、瓦片默认值全部在构建时编进程序集
/// （见 csproj 的 EmbeddedResource 一段），运行时只从内存读，不落盘、不读 exe 同级目录。
///
/// 逻辑名统一是 <c>asset/&lt;assets 下的相对路径&gt;</c>，例如
/// <c>asset/levels/main.json</c>、<c>asset/audio/whoosh.wav</c>。
/// 调用方拿到的只有「有没有」和「字节」，不再有文件路径，
/// 所以单文件分发时 exe 旁边可以什么都没有。
/// </summary>
internal static class Assets
{
    const string Prefix = "asset/";

    /// <summary>相对路径 → 程序集内逻辑名。相对路径统一用 '/'，比较不分大小写。</summary>
    static readonly Dictionary<string, string> Index = BuildIndex();

    static Dictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in typeof(Assets).Assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            map[name[Prefix.Length..]] = name;
        }
        return map;
    }

    /// <summary>把调用方传进来的相对路径规范成索引里的键（兼容 Windows 的反斜杠）。</summary>
    static string Key(string rel) => rel.Replace('\\', '/');

    public static bool Has(string rel) => Index.ContainsKey(Key(rel));

    /// <summary>某个目录下的文件名（只返回文件名，不含目录部分）。</summary>
    public static IEnumerable<string> List(string dir)
    {
        string prefix = Key(dir).TrimEnd('/') + "/";
        foreach (string key in Index.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = key[prefix.Length..];
            if (rest.Length > 0 && !rest.Contains('/')) yield return rest;
        }
    }

    public static Stream? Open(string rel)
    {
        if (!Index.TryGetValue(Key(rel), out string? name)) return null;
        return typeof(Assets).Assembly.GetManifestResourceStream(name);
    }

    public static byte[]? Read(string rel)
    {
        using var stream = Open(rel);
        if (stream == null) return null;
        using var buffer = new MemoryStream(stream.CanSeek ? (int)stream.Length : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string? ReadText(string rel)
    {
        byte[]? bytes = Read(rel);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// GDI+ 的 Bitmap 是惰性读源流的：构造函数返回之后还会回头去流里取像素，
    /// 所以流必须一直开着。这里把用过的流挂在静态表上，活到进程结束。
    /// </summary>
    static readonly List<Stream> KeepAlive = new();

    public static Bitmap LoadBitmap(string rel)
    {
        var stream = Open(rel) ?? throw new FileNotFoundException($"程序集里没有素材 {rel}");
        KeepAlive.Add(stream);
        return new Bitmap(stream);
    }

    /// <summary>
    /// 按游戏启动的方式装载瓦片默认属性（内嵌的 tiles.meta.json）。
    /// 游戏和 --tile-defaults-selftest 都走这一个入口，自测才测的是真实路径。
    /// </summary>
    public static void LoadEmbeddedTileDefaults() =>
        TileDefaults.LoadFromJson(ReadText("tiles.meta.json") ?? "{}", "(embedded) tiles.meta.json");
}
