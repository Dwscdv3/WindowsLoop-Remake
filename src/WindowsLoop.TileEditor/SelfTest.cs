using WindowsLoop.Tileset;

namespace WindowsLoop.TileEditor;

/// <summary>
/// 不带界面的自测：WindowsLoop.TileEditor.exe --selftest
/// 结果写到 %TEMP%\windowsloop-tileeditor-selftest.txt，有 FAIL 时进程返回 1。
/// </summary>
internal static class SelfTest
{
    static readonly List<string> Lines = new();
    static int _fail;

    public static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wl-tileeditor-selftest");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "meta.json");

        Check("AssetLocator 指向源码树里的数据源",
            File.Exists(AssetLocator.SourceTileMetaPath), AssetLocator.SourceTileMetaPath);

        try
        {
            RoundTrip(file);
            MissingAndCorrupt(dir);
            IndexApi();
            Priority();
            SpriteOrder();
            RealFile();
        }
        catch (Exception ex)
        {
            Lines.Add($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            _fail++;
        }

        Lines.Add($"result: {(_fail == 0 ? "ALL PASS" : _fail + " FAILED")}");
        string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-tileeditor-selftest.txt");
        File.WriteAllLines(outPath, Lines);
        Console.WriteLine(outPath);
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }

    static void Check(string name, bool ok, string? detail = null)
    {
        Lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail == null ? "" : "  | " + detail)}");
        if (!ok) _fail++;
    }

    static void RoundTrip(string file)
    {
        var m = new TileMetaFile();
        m.Tiles.Add(new TileMeta
        {
            Sprite = 9, Name = "草土地面·上", Group = "地面", Solid = true, Climb = true,
        });
        m.Tiles.Add(new TileMeta { Sprite = 0, Name = "洞壁·空", Group = "洞壁", Note = "全透明" });
        m.Tiles.Add(new TileMeta { Sprite = 86, Name = "草丛", Group = "地面" });
        m.Tiles.Add(new TileMeta { Sprite = 23, Name = "木板", Group = "木构", Platform = true });
        m.Save(file);

        string json = File.ReadAllText(file);
        var back = TileMetaFile.Load(file);
        bool sorted = back.Tiles.Select(t => t.Sprite).SequenceEqual(new[] { 0, 9, 23, 86 });
        bool chinese = json.Contains("草土地面·上", StringComparison.Ordinal);
        bool noNull = !json.Contains("\"note\": null", StringComparison.Ordinal);
        bool noteKept = back.Find(0)?.Note == "全透明";
        bool flags = back.Find(9)?.Solid == true && back.Find(9)?.Climb == true &&
                     back.Find(86)?.Solid == false && back.Find(86)?.Water == false &&
                     back.Find(86)?.Platform == false && back.Find(23)?.Platform == true &&
                     back.Find(23)?.Solid == false;
        Check("保存→读回：中文名、排序、备注、五个开关", sorted && chinese && noNull && noteKept && flags,
            $"sorted={sorted} chinese={chinese} noNull={noNull} note={noteKept} flags={flags}");
    }

    static void MissingAndCorrupt(string dir)
    {
        var missing = TileMetaFile.LoadOrEmpty(Path.Combine(dir, "not-there.json"));
        string bad = Path.Combine(dir, "broken.json");
        File.WriteAllText(bad, "{ this is not json ");
        var corrupt = TileMetaFile.LoadOrEmpty(bad);
        Check("文件缺失 / 损坏都退化成空表（不让游戏起不来）",
            missing.Tiles.Count == 0 && corrupt.Tiles.Count == 0);
    }

    static void IndexApi()
    {
        var m = new TileMetaFile();
        var t = m.Ensure(3);
        t.Solid = true;
        bool found = m.Find(3)?.Solid == true;
        t.Solid = false;
        bool live = m.Find(3)?.Solid == false;          // 同一对象引用
        m.Remove(3);
        bool gone = m.Find(3) == null;
        m.Ensure(7);
        bool added = m.Find(7) != null;
        Check("Ensure / Find / Remove 与索引失效", found && live && gone && added,
            $"found={found} live={live} gone={gone} added={added}");
    }

    static void Priority()
    {
        var m = new TileMetaFile();
        m.Tiles.Add(new TileMeta { Sprite = 9, Solid = true, Water = true });
        m.Tiles.Add(new TileMeta { Sprite = 86, Solid = false });
        string file = Path.Combine(Path.GetTempPath(), "wl-tileeditor-selftest", "defaults.json");
        m.Save(file);
        TileDefaults.LoadFrom(file);

        bool follow = TileDefaults.SolidOf(null, new[] { 9 });
        bool explicitFalse = !TileDefaults.SolidOf(false, new[] { 9 });
        bool explicitTrue = TileDefaults.SolidOf(true, new[] { 86 });
        bool unregistered = !TileDefaults.SolidOf(null, new[] { 999 });
        bool water = TileDefaults.WaterOf(null, new[] { 9 });
        bool empty = !TileDefaults.SolidOf(null, Array.Empty<int>());
        Check("关卡优先 / 元数据兜底 / 未登记=false",
            follow && explicitFalse && explicitTrue && unregistered && water && empty,
            $"follow={follow} explicitFalse={explicitFalse} explicitTrue={explicitTrue} " +
            $"unreg={unregistered} water={water} empty={empty}");
    }

    static void SpriteOrder()
    {
        var m = new TileMetaFile();
        m.Tiles.Add(new TileMeta { Sprite = 8, Solid = false });
        m.Tiles.Add(new TileMeta { Sprite = 90, Solid = true });
        string file = Path.Combine(Path.GetTempPath(), "wl-tileeditor-selftest", "order.json");
        m.Save(file);
        TileDefaults.LoadFrom(file);
        // 多精灵图例取第一个主贴图：背景块里混了一格实心板也不该整块变墙
        bool firstWins = !TileDefaults.SolidOf(null, new[] { 8, 90 });
        bool single = TileDefaults.SolidOf(null, new[] { 90 });
        Check("多精灵图例按主贴图取默认", firstWins && single,
            $"firstWins={firstWins} single={single}");
    }

    static void RealFile()
    {
        string path = AssetLocator.SourceTileMetaPath;
        if (!File.Exists(path)) { Check("真实数据源可读", false, path); return; }
        var m = TileMetaFile.LoadOrEmpty(path);
        int solid = m.Tiles.Count(t => t.Solid);
        int platform = m.Tiles.Count(t => t.Platform);
        int dup = m.Tiles.GroupBy(t => t.Sprite).Count(g => g.Count() > 1);
        Check("真实 tiles.meta.json 可读且无重复 sprite",
            m.Tiles.Count > 0 && dup == 0, $"tiles={m.Tiles.Count} solid={solid} platform={platform} dup={dup}");
        TileDefaults.LoadFrom(path);
    }
}
