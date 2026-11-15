using System.Drawing;

namespace WindowsLoop.LevelEditor;

/// <summary>
/// 一格网格的完整数据：图 + 五个逐格标记层（变体 / 朝向 / 阻挡 / 压暗 / 事件）。
/// 框选复制粘贴必须六层一起搬，漏任何一层都会出现「画面对了但碰撞/朝向/事件不对」。
/// </summary>
internal readonly record struct GridCell(char Glyph, int Var, int Xf, int Solid, bool Dim, int Evt)
{
    public static GridCell Air => new('.', -1, 0, -1, false, -1);

    /// <summary>
    /// 「空格透明」的判定：图是空气，且五个标记层都是各自的默认值。
    /// 只画了阻挡标记 / 压暗标记 / 事件标记的空格算有内容，粘贴时会整格覆盖目标。
    /// </summary>
    public bool IsEmpty =>
        PieceDoc.IsAir(Glyph) && Var < 0 && Xf == 0 && Solid < 0 && !Dim && Evt < 0;
}

/// <summary>一块矩形区域的快照，脱离 PieceDoc 独立存在（放进剪贴板不会被后续编辑改掉）。</summary>
internal sealed class GridBuffer
{
    public int Width { get; }
    public int Height { get; }
    public string Label { get; }

    readonly GridCell[] _cells;

    public GridBuffer(int width, int height, string label = "")
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Label = label;
        _cells = new GridCell[Width * Height];
    }

    public GridCell At(int x, int y) => _cells[y * Width + x];

    public void Set(int x, int y, GridCell cell) => _cells[y * Width + x] = cell;

    /// <summary>从地图里抓一块矩形；越界位置填 Air，写入时会跳过（越界裁剪）。</summary>
    public static GridBuffer Capture(PieceDoc p, Rectangle rect)
    {
        var buf = new GridBuffer(rect.Width, rect.Height);
        for (int y = 0; y < rect.Height; y++)
            for (int x = 0; x < rect.Width; x++)
            {
                int tx = rect.X + x, ty = rect.Y + y;
                buf.Set(x, y, GridOps.InBounds(p, tx, ty) ? GridOps.Read(p, tx, ty) : GridCell.Air);
            }
        return buf;
    }

    /// <summary>越界裁剪后，这块区域真正落在地图内的格子数。</summary>
    public int InBoundsCells(PieceDoc p, int left, int top)
    {
        int n = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (GridOps.InBounds(p, left + x, top + y)) n++;
        return n;
    }

    /// <summary>按「空格透明 + 越界裁剪」真正会被写入的格子数（用来判断粘贴是不是空操作）。</summary>
    public int WritableCells(PieceDoc p, int left, int top)
    {
        int n = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (!At(x, y).IsEmpty && GridOps.InBounds(p, left + x, top + y)) n++;
        return n;
    }
}

/// <summary>进程内剪贴板：能跨地图块、跨文档粘贴，关掉编辑器进程就清空。</summary>
internal static class GridClipboard
{
    public static GridBuffer? Data { get; set; }

    public static string Description =>
        Data == null ? "空" : $"{Data.Width}×{Data.Height}{Data.Label}";
}

internal static class GridOps
{
    public static bool InBounds(PieceDoc p, int x, int y) =>
        x >= 0 && y >= 0 && x < p.Width && y < p.Height;

    public static GridCell Read(PieceDoc p, int x, int y) => new(
        p.GetCell(x, y), p.GetVar(x, y), p.GetXf(x, y), p.GetSolid(x, y), p.GetDim(x, y), p.GetEvtSeq(x, y));

    /// <summary>六个层一起写，不能只改图或只改标记。</summary>
    public static void Write(PieceDoc p, int x, int y, GridCell c)
    {
        p.SetCell(x, y, c.Glyph);
        p.SetVar(x, y, c.Var);
        p.SetXf(x, y, c.Xf);
        p.SetSolid(x, y, c.Solid);
        p.SetDim(x, y, c.Dim);
        p.SetEvtSeq(x, y, c.Evt);
    }

    /// <summary>把矩形里所有格清成空气；五个标记层一并归位，不留幽灵数据。</summary>
    public static void Clear(PieceDoc p, Rectangle rect)
    {
        int w = p.Width, h = p.Height;
        int x0 = Math.Max(0, rect.X), y0 = Math.Max(0, rect.Y);
        int x1 = Math.Min(w, rect.Right), y1 = Math.Min(h, rect.Bottom);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                Write(p, x, y, GridCell.Air);
        p.Normalize();
    }

    /// <summary>
    /// 粘贴：空格透明（目标原有内容保留），非空格六层整格覆盖；越界部分裁掉。
    /// 返回真正写入的格子数。
    /// </summary>
    public static int Paste(PieceDoc p, GridBuffer buf, int left, int top)
    {
        int written = 0;
        for (int y = 0; y < buf.Height; y++)
            for (int x = 0; x < buf.Width; x++)
            {
                var cell = buf.At(x, y);
                if (cell.IsEmpty) continue;                       // 透明：不动目标
                int tx = left + x, ty = top + y;
                if (!InBounds(p, tx, ty)) continue;               // 越界裁剪
                Write(p, tx, ty, cell);
                written++;
            }
        p.Normalize();
        return written;
    }

    /// <summary>
    /// 移动：先抓源与目标两份快照（源和目标可能重叠），清空源，再按粘贴规则写入目标。
    /// 空格透明在重叠情况下要恢复「目标原本的内容」，所以必须先存 destBefore。
    /// 返回真正写入的格子数。
    /// </summary>
    public static int Move(PieceDoc p, Rectangle from, int left, int top)
    {
        if (from.Width <= 0 || from.Height <= 0) return 0;

        var src = GridBuffer.Capture(p, from);
        var destRect = new Rectangle(left, top, from.Width, from.Height);
        var destBefore = GridBuffer.Capture(p, destRect);
        int w = p.Width, h = p.Height;

        // 清空源（只清地图内的部分）
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (InBounds(p, from.X + x, from.Y + y))
                    Write(p, from.X + x, from.Y + y, GridCell.Air);

        int written = 0;
        for (int y = 0; y < destRect.Height; y++)
            for (int x = 0; x < destRect.Width; x++)
            {
                int tx = destRect.X + x, ty = destRect.Y + y;
                if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;   // 越界裁剪

                var cell = src.At(x, y);
                if (cell.IsEmpty)
                {
                    // 源是空格：透明。目标若和源重叠，刚才被清掉了，这里恢复它原来的内容。
                    Write(p, tx, ty, destBefore.At(x, y));
                }
                else
                {
                    Write(p, tx, ty, cell);
                    written++;
                }
            }

        p.Normalize();
        return written;
    }

    /// <summary>把矩形裁到地图范围内；完全在界外返回 null。</summary>
    public static Rectangle? ClipToMap(Rectangle r, PieceDoc p)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(p.Width, r.Right), y1 = Math.Min(p.Height, r.Bottom);
        if (x1 <= x0 || y1 <= y0) return null;
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>两点拖出的框，统一成左上小、右下大的矩形（含两端格）。</summary>
    public static Rectangle FromDrag(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
}
