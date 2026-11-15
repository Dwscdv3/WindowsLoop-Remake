using System.Drawing;

namespace WindowsLoop.LevelEditor;

/// <summary>接缝上的一小段（1 格宽）：位置 + 这一格能不能走人。</summary>
internal readonly record struct SeamSegment(bool Vertical, int X, int Y, int Length, bool Open);

/// <summary>
/// 拼接接缝判定。规则和引擎里的 TransferIfCrossed 一致：
/// 角色从自己窗口的边缘格跨到邻窗对应的那一格，目标格是实心就走不过去。
/// </summary>
internal static class SeamCheck
{
    public const int ViewX = 15, ViewY = 10;   // 取景框 = 600x400 / 40

    /// <summary>窗口初始取景框左上角对应的地图格（和游戏 CameraTarget 对齐）。</summary>
    public static Point CamOrigin(PieceDoc p) => new(p.Spawn[0] - ViewX / 2, p.Spawn[1] - ViewY / 2);

    public static int Wrap(int v, int n) => n <= 0 ? 0 : ((v % n) + n) % n;

    public static char VisibleCell(PieceDoc p, int col, int row)
    {
        var cam = CamOrigin(p);
        return p.GetCell(Wrap(cam.X + col, p.Width), Wrap(cam.Y + row, p.Height));
    }

    /// <summary>
    /// 接缝格挡不挡：和游戏 Level.ReadFlag / 编辑器 EffectiveSolid 同一套口径，
    /// 关卡图例没写 solid 时跟随瓦片工具的默认值。
    /// </summary>
    public static bool IsSolid(LevelDoc doc, char c) =>
        c != '.' && c != ' ' && doc.Tiles.TryGetValue(c.ToString(), out var t) && t.EffectiveSolidFlag;

    public static bool Overlaps(Rectangle a, Rectangle b) =>
        Rectangle.Intersect(a, b) is { Width: > 0, Height: > 0 };

    /// <summary>两块窗口贴合处的逐格判定；没贴合或重叠都返回空。</summary>
    public static List<SeamSegment> Segments(LevelDoc doc, IReadOnlyList<Rectangle> rects, int i, int j, int unit)
    {
        var list = new List<SeamSegment>();
        if (i == j || i < 0 || j < 0 || i >= rects.Count || j >= rects.Count) return list;
        if (i >= doc.Pieces.Count || j >= doc.Pieces.Count) return list;

        var a = rects[i];
        var b = rects[j];
        if (Overlaps(a, b)) return list;   // 重叠不算相连
        var pa = doc.Pieces[i];
        var pb = doc.Pieces[j];

        // 左右相接：沿接缝逐行判定
        if (a.Right == b.Left || b.Right == a.Left)
        {
            bool aLeft = a.Right == b.Left;
            int x = aLeft ? a.Right : b.Right;
            int top = Math.Max(a.Top, b.Top), bottom = Math.Min(a.Bottom, b.Bottom);
            for (int y = top; y < bottom; y += unit)
            {
                int rowA = (y - a.Top) / unit, rowB = (y - b.Top) / unit;
                bool open = !IsSolid(doc, VisibleCell(pa, aLeft ? ViewX - 1 : 0, rowA)) &&
                            !IsSolid(doc, VisibleCell(pb, aLeft ? 0 : ViewX - 1, rowB));
                list.Add(new SeamSegment(true, x, y, Math.Min(unit, bottom - y), open));
            }
            return list;
        }

        // 上下相接：沿接缝逐列判定
        if (a.Bottom == b.Top || b.Bottom == a.Top)
        {
            bool aTop = a.Bottom == b.Top;
            int y = aTop ? a.Bottom : b.Bottom;
            int left = Math.Max(a.Left, b.Left), right = Math.Min(a.Right, b.Right);
            for (int x = left; x < right; x += unit)
            {
                int colA = (x - a.Left) / unit, colB = (x - b.Left) / unit;
                bool open = !IsSolid(doc, VisibleCell(pa, colA, aTop ? ViewY - 1 : 0)) &&
                            !IsSolid(doc, VisibleCell(pb, colB, aTop ? 0 : ViewY - 1));
                list.Add(new SeamSegment(false, x, y, Math.Min(unit, right - x), open));
            }
        }
        return list;
    }

    /// <summary>(贴合处数, 可通行格数, 接缝总格数, 重叠对数)</summary>
    public static (int seams, int open, int rows, int overlaps) Stats(
        LevelDoc doc, IReadOnlyList<Rectangle> rects, int unit)
    {
        int seams = 0, open = 0, rows = 0, overlaps = 0;
        int n = Math.Min(rects.Count, doc.Pieces.Count);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (Overlaps(rects[i], rects[j])) { overlaps++; continue; }
                var segs = Segments(doc, rects, i, j, unit);
                if (segs.Count == 0) continue;
                seams++;
                rows += segs.Count;
                open += segs.Count(s => s.Open);
            }
        return (seams, open, rows, overlaps);
    }
}
