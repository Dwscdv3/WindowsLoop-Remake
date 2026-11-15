using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WindowsLoop.Tileset;

namespace WindowsLoop.TileEditor;

/// <summary>
/// 整张图集的可多选网格。每格画 sprite 本体 + 下标 + 名称，
/// 选中项用橙色框标出；点空白处清空选择。
/// </summary>
internal sealed class TileGridPanel : Panel
{
    public const int Cols = 8;
    const int Cell = 74;
    const int Art = 48;

    public Bitmap? Sheet;
    public TileMetaFile? Meta;

    public readonly List<int> Selection = new();
    public int Hovered = -1;

    public event Action? SelectionChanged;

    readonly Font _idxFont = new("Consolas", 7f);
    readonly Font _nameFont = new("Microsoft YaHei UI", 7f);

    public TileGridPanel()
    {
        AutoScroll = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(26, 29, 37);
        ResizeRedraw = true;
    }

    public int Count => Sheet == null ? 0 : (Sheet.Width / 8) * (Sheet.Height / 8);

    public void ResetSheet(Bitmap? sheet)
    {
        Sheet = sheet;
        int rows = Count == 0 ? 0 : (Count + Cols - 1) / Cols;
        AutoScrollMinSize = new Size(Cols * Cell + 2, rows * Cell + 2);
        Invalidate();
    }

    public void SetSelection(IEnumerable<int> sprites)
    {
        Selection.Clear();
        Selection.AddRange(sprites);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    static Rectangle CellRect(int sprite) =>
        new((sprite % Cols) * Cell, (sprite / Cols) * Cell, Cell, Cell);

    public int SpriteAt(Point clientPoint)
    {
        var p = new Point(clientPoint.X - AutoScrollPosition.X, clientPoint.Y - AutoScrollPosition.Y);
        int col = p.X / Cell, row = p.Y / Cell;
        if (col < 0 || col >= Cols || row < 0) return -1;
        int idx = row * Cols + col;
        return idx < Count ? idx : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Sheet == null) return;

        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var bgA = new SolidBrush(Color.FromArgb(33, 38, 63));     // 和游戏窗口底色一致
        using var bgB = new SolidBrush(Color.FromArgb(29, 33, 56));
        using var selPen = new Pen(Color.Orange, 2);
        using var hoverPen = new Pen(Color.FromArgb(150, 120, 200, 255), 1);
        using var idxBrush = new SolidBrush(Color.FromArgb(150, 210, 215, 230));
        using var nameBrush = new SolidBrush(Color.FromArgb(225, 225, 232));
        using var emptyBrush = new SolidBrush(Color.FromArgb(120, 150, 120, 120));
        using var solidBrush = new SolidBrush(Color.FromArgb(235, 92, 84));
        using var flagBrush = new SolidBrush(Color.FromArgb(120, 200, 235));

        for (int i = 0; i < Count; i++)
        {
            var cell = CellRect(i);
            var inner = new Rectangle(cell.X + 6, cell.Y + 6, Cell - 12, Cell - 12);

            g.FillRectangle(((i + i / Cols) & 1) == 0 ? bgA : bgB, inner);
            g.DrawImage(Sheet, new Rectangle(inner.X + (inner.Width - Art) / 2, inner.Y + 2, Art, Art),
                new Rectangle((i % 8) * 8, (i / 8) * 8, 8, 8), GraphicsUnit.Pixel);

            g.DrawString(i.ToString(), _idxFont, idxBrush, cell.X + 7, cell.Y + 6);

            var meta = Meta?.Find(i);
            string name = meta?.Name ?? "";
            if (name.Length == 0)
                g.DrawString("未登记", _nameFont, emptyBrush, cell.X + 6, cell.Bottom - 18);
            else
                g.DrawString(name, _nameFont, nameBrush,
                    new RectangleF(cell.X + 5, cell.Bottom - 19, Cell - 10, 16));

            // 右上角红块 = 默认实心，顶部蓝条 = 默认单向平台。
            // 额外属性在右下角用小字标出来，一眼能看到五个开关的状态。
            if (meta != null)
            {
                if (meta.Solid) g.FillRectangle(solidBrush, cell.Right - 15, cell.Y + 7, 8, 8);
                else if (meta.Platform) g.FillRectangle(flagBrush, inner.Left, inner.Top, inner.Width, 3);
                string flags = (meta.Water ? "W" : "") + (meta.Climb ? "C" : "") + (meta.Spring ? "P" : "");
                if (flags.Length > 0)
                    g.DrawString(flags, _idxFont, flagBrush, cell.Right - 22, cell.Bottom - 17);
            }

            if (Selection.Contains(i)) g.DrawRectangle(selPen, inner);
            else if (i == Hovered) g.DrawRectangle(hoverPen, inner);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int sprite = SpriteAt(e.Location);
        if (sprite < 0)
        {
            SetSelection(Array.Empty<int>());
            return;
        }

        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift) != 0;

        if (ctrl)
        {
            if (!Selection.Remove(sprite)) Selection.Add(sprite);
            SelectionChanged?.Invoke();
        }
        else if (shift && Selection.Count > 0)
        {
            int a = Math.Min(Selection[^1], sprite), b = Math.Max(Selection[^1], sprite);
            SetSelection(Enumerable.Range(a, b - a + 1));
        }
        else
        {
            SetSelection(new[] { sprite });
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int sprite = SpriteAt(e.Location);
        if (sprite == Hovered) return;
        Hovered = sprite;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (Hovered < 0) return;
        Hovered = -1;
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.A))
        {
            SetSelection(Enumerable.Range(0, Count));
            Invalidate();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
