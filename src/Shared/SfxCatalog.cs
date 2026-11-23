namespace WindowsLoop;

/// <summary>
/// 事件动作「播放音效」能选的音效表。
///
/// key 就是关卡 JSON 里 <c>action.sound</c> 的值（例如 "unlock"），
/// label 只给关卡编辑器的下拉框用，游戏不读它。
/// 游戏端由 <c>AudioManager.PlayByKey</c> 把 key 映射到 Sfx 枚举。
///
/// 游戏和关卡编辑器各自把这份文件编进自己的程序集（见两个 csproj 的
/// <c>Compile Include="..\Shared\SfxCatalog.cs"</c>），所以加一个音效只需要：
///   1. 这里加一行 key + 中文名；
///   2. AudioManager 里注册同名 Sfx；
/// 编辑器的下拉框会自动跟着变，不会和游戏对不上。
/// 顺序就是编辑器下拉框里的顺序。
/// </summary>
internal static class SfxCatalog
{
    internal sealed record Entry(string Key, string Label);

    internal static readonly Entry[] All =
    {
        new("unlock", "开锁"),
        new("jump", "起跳"),
        new("land", "落地"),
        new("gem", "收集宝石"),
        new("win", "通关"),
        new("window", "窗口吐出"),
        new("snap", "窗口拼接"),
        new("release", "窗口拆开"),
        new("transfer", "窗口传接"),
        new("bump", "撞屏幕边"),
        new("ahhh", "吐出前蓄力"),
        new("choo", "窗口飞出"),
        new("cantopen", "窗口到顶"),
    };

    /// <summary>下拉框下标 → key；越界返回空串（调用方按"还没选"处理）。</summary>
    internal static string KeyAt(int index) =>
        index >= 0 && index < All.Length ? All[index].Key : "";

    /// <summary>key → 下拉框下标；认不出来的 key 返回 -1。</summary>
    internal static int IndexOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>key → 中文名；认不出来的 key 原样返回（编辑器里能看出填错了什么）。</summary>
    internal static string LabelOf(string? key)
    {
        int i = IndexOf(key);
        return i >= 0 ? All[i].Label : key ?? "";
    }
}
