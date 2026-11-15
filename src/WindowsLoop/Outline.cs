using System.Drawing;

namespace WindowsLoop;

/// <summary>
/// 一堆矩形并集的外轮廓。
///
/// 做法是坐标压缩：把所有矩形的左右边收集成一组 x、上下边收集成一组 y，
/// 这样每一格要么整格被覆盖、要么整格是空的；再把"占 / 不占"之间的那些边取出来，
/// 按"优先右转"的规则串成闭环。
///
/// 因为线段一律正交，结果必然是横平竖直的——结构上不可能画出斜线；
/// 又因为格线就是矩形的真实边缘，轮廓是像素级精确的，窗口停在非网格位置也照样贴合。
/// </summary>
internal static class Outline
{
    public static List<List<PointF>> Union(IReadOnlyList<Rectangle> rects)
    {
        var loops = new List<List<PointF>>();
        if (rects.Count == 0) return loops;

        var xs = new List<int>(rects.Count * 2);
        var ys = new List<int>(rects.Count * 2);
        foreach (var r in rects)
        {
            xs.Add(r.Left);
            xs.Add(r.Right);
            ys.Add(r.Top);
            ys.Add(r.Bottom);
        }
        xs.Sort();
        ys.Sort();
        xs = Distinct(xs);
        ys = Distinct(ys);

        int cols = xs.Count - 1;
        int rows = ys.Count - 1;
        if (cols <= 0 || rows <= 0) return loops;

        var occ = new bool[cols, rows];
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                foreach (var r in rects)
                {
                    // 格线都取自矩形边缘，所以一格要么整格在内、要么整格在外
                    if (xs[i] >= r.Left && xs[i + 1] <= r.Right && ys[j] >= r.Top && ys[j + 1] <= r.Bottom)
                    {
                        occ[i, j] = true;
                        break;
                    }
                }
            }

        // 边界线段：一条格线两侧"占用状态不同"就是外轮廓
        var edges = new List<(int x1, int y1, int x2, int y2)>();
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j < rows; j++)
            {
                bool west = i > 0 && occ[i - 1, j];
                bool east = i < cols && occ[i, j];
                if (west != east) edges.Add((xs[i], ys[j], xs[i], ys[j + 1]));
            }
        for (int j = 0; j <= rows; j++)
            for (int i = 0; i < cols; i++)
            {
                bool north = j > 0 && occ[i, j - 1];
                bool south = j < rows && occ[i, j];
                if (north != south) edges.Add((xs[i], ys[j], xs[i + 1], ys[j]));
            }

        if (edges.Count == 0) return loops;

        var at = new Dictionary<(int, int), List<int>>();
        for (int e = 0; e < edges.Count; e++)
        {
            Attach(at, (edges[e].x1, edges[e].y1), e);
            Attach(at, (edges[e].x2, edges[e].y2), e);
        }

        var used = new bool[edges.Count];

        for (int start = 0; start < edges.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;

            var path = new List<(int x, int y)>
            {
                (edges[start].x1, edges[start].y1),
                (edges[start].x2, edges[start].y2),
            };

            Extend(path, true);
            Extend(path, false);

            var pts = Simplify(path);
            if (pts.Count >= 3) loops.Add(pts);
        }

        return loops;

        void Extend(List<(int x, int y)> path, bool forward)
        {
            while (true)
            {
                var end = forward ? path[^1] : path[0];
                var from = forward ? path[^2] : path[1];
                if (!at.TryGetValue(end, out var candidates)) return;

                int dx = Math.Sign(end.x - from.x), dy = Math.Sign(end.y - from.y);
                int pick = -1, best = int.MaxValue;

                foreach (int idx in candidates)
                {
                    if (used[idx]) continue;
                    var far = Far(edges[idx], end);
                    int ndx = Math.Sign(far.x - end.x), ndy = Math.Sign(far.y - end.y);

                    int rank = TurnRank(dx, dy, ndx, ndy);
                    if (rank < best) { best = rank; pick = idx; }
                }

                if (pick < 0) return;

                used[pick] = true;
                var next = Far(edges[pick], end);
                if (forward) path.Add(next);
                else path.Insert(0, next);
            }
        }

        static void Attach(Dictionary<(int, int), List<int>> map, (int, int) key, int edge)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }
            list.Add(edge);
        }

        static (int x, int y) Far((int x1, int y1, int x2, int y2) e, (int x, int y) p) =>
            e.x1 == p.x && e.y1 == p.y ? (e.x2, e.y2) : (e.x1, e.y1);
    }

    static List<int> Distinct(List<int> sorted)
    {
        var result = new List<int>(sorted.Count);
        foreach (int v in sorted)
            if (result.Count == 0 || result[^1] != v) result.Add(v);
        return result;
    }

    /// <summary>
    /// 拐弯的偏好顺序。两块窗口只在一个角上相碰时，那个顶点会挂 4 条边；
    /// 这时必须"拐弯"而不是"直着穿过去"，否则会把两个矩形串成一个 8 字形。
    /// </summary>
    static int TurnRank(int dx, int dy, int ndx, int ndy)
    {
        int cross = dx * ndy - dy * ndx;
        int dot = dx * ndx + dy * ndy;

        if (cross > 0) return 0;    // 右转
        if (cross < 0) return 1;    // 左转
        return dot > 0 ? 2 : 3;     // 直行 / 掉头
    }

    /// <summary>去掉共线的中间点，只保留真正的转角。</summary>
    static List<PointF> Simplify(List<(int x, int y)> path)
    {
        var keep = new List<(int x, int y)>(path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0 && i < path.Count - 1)
            {
                var a = path[i - 1];
                var b = path[i];
                var c = path[i + 1];
                bool collinear = (a.x == b.x && b.x == c.x) || (a.y == b.y && b.y == c.y);
                if (collinear) continue;
            }
            keep.Add(path[i]);
        }

        if (keep.Count > 1 && keep[0] == keep[^1]) keep.RemoveAt(keep.Count - 1);

        var pts = new List<PointF>(keep.Count + 1);
        foreach (var (x, y) in keep)
            pts.Add(new PointF(x, y));

        if (pts.Count > 1) pts.Add(pts[0]);   // 闭合
        return pts;
    }
}
