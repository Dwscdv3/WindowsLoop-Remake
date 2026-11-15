using System.Drawing;

namespace WindowsLoop;

/// <summary>
/// 窗口组：通过直接贴合（传递地）连成一片的窗口集合。
///
/// 注意组内成员不一定两两相邻——A 贴在 B 左边、C 贴在 B 右边时，A 和 C 在同一个组里
/// 但彼此不是邻居，隔着 B。相邻关系（能不能走过去）只看直接贴合的那一边。
///
/// 组的存在意义是把"整片窗口"当成一个整体来算外轮廓：光效只在整片形状的最外圈亮，
/// 内部接缝不亮，拐角也能连成一条平滑的折线。
/// </summary>
internal sealed class WindowGroup
{
    public readonly List<GameWindow> Windows = new();

    /// <summary>整片形状的外轮廓，屏幕坐标，若干条闭合折线。</summary>
    public List<List<PointF>> Boundary = new();

    public bool Contains(GameWindow window) => Windows.Contains(window);
}
