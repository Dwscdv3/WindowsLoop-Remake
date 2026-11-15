using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WindowsLoop.Tileset;

namespace WindowsLoop.LevelEditor;

/// <summary>
/// 整张图集的点选面板：一格一个精灵，点一下就选中。
/// 取代原来那排字符按钮——素材有 90 多格，靠字符名找很难对上画面。
/// </summary>
internal sealed class SheetPanel : Panel
{
    public const int Cols = 8;
    const int Pad = 6;
    const int Gap = 1;

    Bitmap? _sheet;
    bool[] _empty = Array.Empty<bool>();

    /// <summary>每格的显示边长（像素）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CellSize { get; set; } = 30;

    /// <summary>当前选中的精灵下标，-1 表示没选。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected { get; set; } = -1;

    /// <summary>鼠标悬停到的下标，-1 表示不在格子上。</summary>
    public int HoverIndex { get; private set; } = -1;

    public event Action<int>? Picked;
    public event Action<int>? Hovered;

    public SheetPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(26, 29, 37);
        // 图集尺寸固定，面板本身够高就不用滚动；开 AutoScroll 反而会让空白区露出宿主底色
        AutoScroll = false;
        ResizeRedraw = true;
    }

    public int Count => _sheet == null ? 0 : (_sheet.Width / 8) * (_sheet.Height / 8);
    int Rows => (Count + Cols - 1) / Cols;

    public void SetSheet(Bitmap? bmp)
    {
        _sheet = bmp;
        int n = Count;
        _empty = new bool[n];
        for (int i = 0; i < n; i++) _empty[i] = IsEmptyCell(i);
        AutoScrollMinSize = new Size(Cols * CellSize + Pad * 2, Rows * CellSize + Pad * 2);
        Invalidate();
    }

    bool IsEmptyCell(int i)
    {
        if (_sheet == null) return true;
        int x0 = (i % Cols) * 8, y0 = (i / Cols) * 8;
        if (x0 + 8 > _sheet.Width || y0 + 8 > _sheet.Height) return true;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (_sheet.GetPixel(x0 + x, y0 + y).A > 8) return false;
        return true;
    }

    Rectangle CellRect(int i) =>
        new(Pad + (i % Cols) * CellSize, Pad + (i / Cols) * CellSize, CellSize - Gap, CellSize - Gap);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        using var grid = new Pen(Color.FromArgb(44, 48, 60));
        using var selPen = new Pen(Color.FromArgb(255, 170, 60), 2);
        using var hoverPen = new Pen(Color.FromArgb(150, 120, 200, 255), 1);
        using var emptyBr = new SolidBrush(Color.FromArgb(70, 74, 88));
        using var solidBr = new SolidBrush(Color.FromArgb(235, 92, 84));
        using var platformBr = new SolidBrush(Color.FromArgb(210, 130, 210, 255));

        for (int i = 0; i < Count; i++)
        {
            var r = CellRect(i);
            if (_empty[i])
            {
                // 空格画个小点，让人知道这里确实没有素材
                g.FillEllipse(emptyBr, r.X + r.Width / 2 - 2, r.Y + r.Height / 2 - 2, 4, 4);
                g.DrawRectangle(grid, r);
                continue;
            }

            var src = new Rectangle((i % Cols) * 8, (i / Cols) * 8, 8, 8);
            g.DrawImage(_sheet!, r, src, GraphicsUnit.Pixel);
            g.DrawRectangle(grid, r);

            // 右上角小红块 = 瓦片工具里这块默认实心，顶部蓝条 = 默认是单向平台。
            // 工具改了默认值、重新 build 后这里会跟着变。
            var meta = TileDefaults.ForSprite(i);
            if (meta?.Solid == true)
                g.FillRectangle(solidBr, r.Right - 7, r.Top + 2, 5, 5);
            else if (meta?.Platform == true)
                g.FillRectangle(platformBr, r.Left + 2, r.Top + 2, r.Width - 4, 3);

            if (i == HoverIndex) g.DrawRectangle(hoverPen, r);
            if (i == Selected)
            {
                g.DrawRectangle(selPen, r);
                using var tint = new SolidBrush(Color.FromArgb(60, 255, 170, 60));
                g.FillRectangle(tint, r);
            }
        }
    }

    int IndexAt(Point p)
    {
        p = new Point(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
        int cx = (p.X - Pad) / CellSize, cy = (p.Y - Pad) / CellSize;
        if (cx < 0 || cx >= Cols || cy < 0) return -1;
        int i = cy * Cols + cx;
        return i >= 0 && i < Count ? i : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = IndexAt(e.Location);
        if (i < 0 || _empty[i]) return;
        Selected = i;
        Invalidate();
        Picked?.Invoke(i);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i == HoverIndex) return;
        HoverIndex = i;
        Invalidate();
        Hovered?.Invoke(i);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (HoverIndex == -1) return;
        HoverIndex = -1;
        Invalidate();
        Hovered?.Invoke(-1);
    }
}
