using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;
using WindowsLoop.Tileset;

namespace WindowsLoop.TileEditor;

/// <summary>
/// 独立的「瓦片默认属性编辑器」。
///
/// 它不引用游戏工程、也不引用关卡编辑器工程，只通过三样东西和它们打交道：
///   1. Shared/TileMeta.cs —— 三个工程链接同一份模型，格式不会漂；
///   2. src/WindowsLoop/assets/tiles.meta.json —— 唯一可写的数据源（本工具保存它）；
///   3. build.cmd —— 把这份 JSON 复制进游戏 / 关卡编辑器各自的输出目录。
///
/// 所以：工具改完要跑一次 build.cmd，游戏和地图编辑器才会用上新默认值。
/// </summary>
internal sealed class MainForm : Form
{
    readonly TileGridPanel _grid = new();
    readonly SplitContainer _split = new();
    readonly MenuStrip _menu = new();
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _statusLabel = new();

    readonly TextBox _search = new(), _name = new(), _note = new();
    readonly ComboBox _group = new();
    readonly CheckBox _solid = new(), _water = new(), _climb = new(), _spring = new(), _platform = new(), _plate = new();
    readonly Label _selLabel = new(), _pathLabel = new();
    readonly PictureBox _preview = new();

    TileMetaFile _meta = new();
    TileMetaFile _saved = new();          // 上次保存的基线，用来算「影响预览」
    readonly List<TileMetaFile> _undo = new(), _redo = new();
    Bitmap? _sheet;
    string _path = "";
    bool _dirty, _loading;

    public MainForm()
    {
        Text = "WindowsLoop · 瓦片默认属性编辑器";
        ClientSize = new Size(1220, 800);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(940, 620);

        BuildUi();
        LoadAll();

        KeyPreview = true;
        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
    }

    // ------------------------------------------------------------------ UI

    void BuildUi()
    {
        var mFile = new ToolStripMenuItem("文件(&F)");
        mFile.DropDownItems.Add(new ToolStripMenuItem("保存(&S)", null, (_, _) => Save()) { ShortcutKeys = Keys.Control | Keys.S });
        mFile.DropDownItems.Add(new ToolStripMenuItem("重新载入(&R)", null, (_, _) => Reload()));
        mFile.DropDownItems.Add(new ToolStripMenuItem("打开所在文件夹(&D)", null, (_, _) => OpenFolder()));
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add(new ToolStripMenuItem("退出(&X)", null, (_, _) => Close()));
        _menu.Items.Add(mFile);

        var mEdit = new ToolStripMenuItem("编辑(&E)");
        mEdit.DropDownItems.Add(new ToolStripMenuItem("撤销", null, (_, _) => Undo()) { ShortcutKeys = Keys.Control | Keys.Z });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("重做", null, (_, _) => Redo()) { ShortcutKeys = Keys.Control | Keys.Y });
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        mEdit.DropDownItems.Add(new ToolStripMenuItem("全部选中", null, (_, _) => SelectAll()) { ShortcutKeys = Keys.Control | Keys.A });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("补全图集里缺失的登记项", null, (_, _) => FillMissing()));
        _menu.Items.Add(mEdit);

        var mView = new ToolStripMenuItem("查看(&V)");
        mView.DropDownItems.Add(new ToolStripMenuItem("影响预览：哪些关卡会变(&I)", null, (_, _) => ShowImpact()) { ShortcutKeys = Keys.Control | Keys.I });
        mView.DropDownItems.Add(new ToolStripMenuItem("重新打包图集(&T)", null, (_, _) => ReloadSheet()));
        _menu.Items.Add(mView);
        MainMenuStrip = _menu;

        var split = _split;
        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.SplitterWidth = 6;
        // Panel1MinSize / Panel2MinSize 要等窗口显示、控件有实际宽度后再设：
        // 此时 SplitContainer 还是默认小尺寸，两个最小值相加超宽会直接抛异常。

        // 顶层用表格布局，Menu / 主体 / Status 各占一行，不依赖 Dock 的 z-order
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _menu.Dock = DockStyle.Fill;
        _status.Dock = DockStyle.Fill;
        root.Controls.Add(_menu, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);

        // ---- 左：搜索条 + 图集网格 ----
        var topBar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 5, 6, 0) };
        _search.SetBounds(6, 5, 200, 24);
        _search.PlaceholderText = "查找名称 / 编号 / 分组，回车选中";
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Search(); e.SuppressKeyPress = true; } };
        topBar.Controls.Add(_search);

        var findBtn = MakeBtn("查找", 212, 5, 60, 24, (_, _) => Search());
        topBar.Controls.Add(findBtn);

        _group.SetBounds(280, 5, 150, 24);
        _group.DropDownStyle = ComboBoxStyle.DropDownList;
        _group.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _group.SelectedIndex < 0) return;
            string g = _group.Items[_group.SelectedIndex]!.ToString() ?? "";
            if (g == "（全部分组）") SelectAll();
            else _grid.SetSelection(_meta.Tiles.Where(t => t.Group == g).Select(t => t.Sprite));
        };
        topBar.Controls.Add(_group);

        // 左侧同样用表格布局：搜索条 / 网格 / 提示条各占一行
        var leftTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        leftTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        leftTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        topBar.Dock = DockStyle.Fill;
        _grid.Dock = DockStyle.Fill;
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "单击选择 · Ctrl 加选 · Shift 连选 · 橙色框=选中 · 右上角红块=默认实心 · 顶部蓝条=单向平台",
            ForeColor = Color.FromArgb(170, 180, 195),
        };
        leftTable.Controls.Add(topBar, 0, 0);
        leftTable.Controls.Add(_grid, 0, 1);
        leftTable.Controls.Add(hint, 0, 2);
        split.Panel1.Controls.Add(leftTable);
        _grid.SelectionChanged += RefreshEditor;

        // ---- 右：属性面板 ----
        var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        split.Panel2.Controls.Add(right);

        _selLabel.SetBounds(12, 10, 300, 42);
        _selLabel.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        right.Controls.Add(_selLabel);

        _preview.SetBounds(12, 58, 128, 128);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(33, 38, 63);
        right.Controls.Add(_preview);

        _pathLabel.SetBounds(152, 58, 180, 128);
        _pathLabel.ForeColor = Color.FromArgb(165, 175, 190);
        right.Controls.Add(_pathLabel);

        int y = 202;
        AddLabel(right, "显示名（单选可改）", 12, y);
        _name.SetBounds(12, y + 20, 316, 24);
        WireText(_name);
        right.Controls.Add(_name);

        y += 54;
        AddLabel(right, "分组（单选可改）", 12, y);
        var groupEdit = new ComboBox { Left = 12, Top = y + 20, Width = 316, Height = 24 };
        groupEdit.Name = "groupEdit";
        WireText(groupEdit);
        right.Controls.Add(groupEdit);
        _groupEdit = groupEdit;

        y += 54;
        AddLabel(right, "备注（单选可改）", 12, y);
        _note.SetBounds(12, y + 20, 316, 62);
        _note.Multiline = true;
        _note.ScrollBars = ScrollBars.Vertical;
        WireText(_note);
        right.Controls.Add(_note);

        y += 94;
        AddLabel(right, "默认物理属性（多选时批量生效）", 12, y);
        y += 24;
        WireFlag(_solid, right, "solid 实心 / 阻挡", y, t => t.Solid, (t, v) => t.Solid = v);
        WireFlag(_platform, right, "platform 平台（下方能穿、上面能站）", y + 30, t => t.Platform, (t, v) => t.Platform = v);
        WireFlag(_water, right, "water 水", y + 60, t => t.Water, (t, v) => t.Water = v);
        WireFlag(_climb, right, "climb 可攀爬", y + 90, t => t.Climb, (t, v) => t.Climb = v);
        WireFlag(_spring, right, "spring 弹簧", y + 120, t => t.Spring, (t, v) => t.Spring = v);
        WireFlag(_plate, right, "plate 压力板（玩家 / 箱子压住即激活）", y + 150, t => t.Plate, (t, v) => t.Plate = v);

        y += 192;
        right.Controls.Add(MakeBtn("全部设为实心", 12, y, 150, 30, (_, _) => BatchSolid(SolidKind.Block)));
        right.Controls.Add(MakeBtn("全部设为可穿过", 170, y, 158, 30, (_, _) => BatchSolid(SolidKind.Pass)));

        y += 36;
        right.Controls.Add(MakeBtn("全部设为单向平台", 12, y, 150, 30, (_, _) => BatchSolid(SolidKind.Platform)));

        y += 44;
        right.Controls.Add(MakeBtn("保存", 12, y, 100, 34, (_, _) => Save()));
        right.Controls.Add(MakeBtn("影响预览", 120, y, 100, 34, (_, _) => ShowImpact()));
        right.Controls.Add(MakeBtn("补全缺失项", 228, y, 100, 34, (_, _) => FillMissing()));

        y += 48;
        right.Controls.Add(new Label
        {
            Text = "保存会直接写回 src/WindowsLoop/assets/tiles.meta.json。\n" +
                   "改完请运行 build.cmd，让游戏与关卡编辑器\n拿到新的默认值副本。\n\n" +
                   "关卡里显式写了 solid 的图例以关卡为准；\n没写的才跟随这里的默认值。",
            Left = 12, Top = y, Width = 316, Height = 110,
            ForeColor = Color.FromArgb(160, 170, 185),
        });

        _statusLabel.Text = "";
        _status.Items.Add(_statusLabel);
    }

    /// <summary>--uicheck：不显示窗口，只验证构造 + 布局 + 数据加载是否正常。</summary>
    public string LayoutReport()
    {
        PerformLayout();
        ApplySplitLayout();
        _split.PerformLayout();
        bool ok = _sheet != null && _meta.Tiles.Count > 0 &&
                  _grid.Width > 200 && _grid.Height > 200 &&
                  _split.Width > 600 && _groupEdit.Width > 200 &&
                  _path.Length > 0;
        return $"{(ok ? "OK" : "FAIL")} | client={ClientSize.Width}x{ClientSize.Height} " +
               $"split={_split.Width}x{_split.Height} grid={_grid.Width}x{_grid.Height} " +
               $"sheet={(_sheet == null ? "missing" : _sheet.Width + "x" + _sheet.Height)} " +
               $"meta={_meta.Tiles.Count} path={_path}";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitLayout();
    }

    /// <summary>句柄建好、有实际宽度后再设最小值和分割位置，避免 SplitContainer 的校验异常。</summary>
    void ApplySplitLayout()
    {
        try
        {
            _split.Panel1MinSize = 300;
            _split.Panel2MinSize = 280;
            int distance = Math.Clamp(ClientSize.Width - 350, 300, Math.Max(300, _split.Width - 280));
            _split.SplitterDistance = distance;
        }
        catch (InvalidOperationException) { /* 窗口极小时保持默认布局 */ }
    }

    ComboBox _groupEdit = null!;
    int _textSprite = -1;

    static void AddLabel(Control parent, string text, int x, int y) =>
        parent.Controls.Add(new Label { Text = text, Left = x, Top = y, Width = 316, Height = 18 });

    static Button MakeBtn(string text, int x, int y, int w, int h, EventHandler click)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = h };
        b.Click += click;
        return b;
    }

    void WireFlag(CheckBox box, Control parent, string text, int y,
                  Func<TileMeta, bool> get, Action<TileMeta, bool> set)
    {
        box.Text = text;
        box.SetBounds(12, y, 316, 26);
        box.ThreeState = true;
        box.AutoCheck = false;       // 状态由我们自己算，避免多选混合态被内部循环改掉
        box.Click += (_, _) =>
        {
            if (_loading || _grid.Selection.Count == 0) return;
            bool next = box.CheckState != CheckState.Checked;   // 混合/未选 -> 勾上；已勾 -> 取消
            PushUndo();
            foreach (int sprite in _grid.Selection) set(_meta.Ensure(sprite), next);
            Changed();
        };
        parent.Controls.Add(box);
    }

    // ------------------------------------------------------------------ 数据

    void LoadAll()
    {
        _path = AssetLocator.SourceTileMetaPath;
        _meta = TileMetaFile.LoadOrEmpty(_path);
        _saved = DeepClone(_meta);
        _undo.Clear();
        _redo.Clear();
        ReloadSheet(quiet: true);
        RefreshGroups();
        RefreshEditor();
        MarkSaved();
    }

    void ReloadSheet(bool quiet = false)
    {
        _sheet?.Dispose();
        _sheet = null;
        try { _sheet = new Bitmap(AssetLocator.SourceTileSheetPath); }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(this, "图集读不到：" + ex.Message);
        }
        _grid.Meta = _meta;
        _grid.ResetSheet(_sheet);
        RefreshStatus();
    }

    static TileMetaFile DeepClone(TileMetaFile src)
    {
        var copy = new TileMetaFile { Version = src.Version };
        copy.Tiles.AddRange(src.Tiles.Select(t => t.Clone()));
        return copy;
    }

    void PushUndo()
    {
        _undo.Add(DeepClone(_meta));
        if (_undo.Count > 80) _undo.RemoveAt(0);
        _redo.Clear();
    }

    void Changed()
    {
        _grid.Meta = _meta;
        _grid.Invalidate();
        MarkDirty();
        RefreshStatus();
        RefreshEditor();
    }

    void MarkDirty()
    {
        _dirty = true;
        Text = "WindowsLoop · 瓦片默认属性编辑器 *";
    }

    void MarkSaved()
    {
        _dirty = false;
        Text = "WindowsLoop · 瓦片默认属性编辑器";
    }

    void RefreshGroups()
    {
        _loading = true;
        var groups = _meta.Tiles.Select(t => t.Group).Where(g => !string.IsNullOrWhiteSpace(g))
                          .Distinct().OrderBy(g => g).ToArray();
        _group.Items.Clear();
        _group.Items.Add("（全部分组）");
        _group.Items.AddRange(groups.Cast<object>().ToArray());
        _group.SelectedIndex = 0;

        string cur = _groupEdit.Text;
        _groupEdit.Items.Clear();
        _groupEdit.Items.AddRange(groups.Cast<object>().ToArray());
        _groupEdit.Text = cur;
        _loading = false;
    }

    /// <summary>把当前选中项的字段填进右侧面板；多选时字段按混合态显示。</summary>
    void RefreshEditor()
    {
        _loading = true;
        var sel = _grid.Selection;
        _selLabel.Text = sel.Count == 0 ? "未选择瓦片"
            : sel.Count == 1 ? $"sprite #{sel[0]}"
            : $"已选中 {sel.Count} 块瓦片";

        var entries = sel.Select(s => _meta.Find(s)).Where(t => t != null).Select(t => t!).ToList();
        var one = sel.Count == 1 ? _meta.Find(sel[0]) : null;

        // 文本框还有焦点时先别覆盖它：点图集换选中项会让 Leave 晚于 SelectionChanged 到达，
        // 过早覆盖会把「旧瓦片的新文字」写到新瓦片上去。CommitText 之后会再刷一次。
        bool textFocused = _name.Focused || _groupEdit.Focused || _note.Focused;
        if (!textFocused)
        {
            _name.Text = one?.Name ?? "";
            _groupEdit.Text = one?.Group ?? "";
            _note.Text = one?.Note ?? "";
            _textSprite = sel.Count == 1 ? sel[0] : -1;
        }
        _name.Enabled = sel.Count == 1;
        _groupEdit.Enabled = sel.Count == 1;
        _note.Enabled = sel.Count == 1;

        SetFlag(_solid, entries, t => t.Solid, sel.Count);
        SetFlag(_platform, entries, t => t.Platform, sel.Count);
        SetFlag(_water, entries, t => t.Water, sel.Count);
        SetFlag(_climb, entries, t => t.Climb, sel.Count);
        SetFlag(_spring, entries, t => t.Spring, sel.Count);
        SetFlag(_plate, entries, t => t.Plate, sel.Count);

        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
        if (sel.Count == 1 && _sheet != null) _preview.Image = MakePreview(sel[0]);

        _pathLabel.Text = "数据源：\n" + Shorten(_path, 30) + "\n\n" +
                          $"已登记 {_meta.Tiles.Count} / {_grid.Count} 格\n" +
                          (_dirty ? "● 有未保存改动" : "✓ 已保存");
        _loading = false;
        RefreshStatus();
    }

    static string Shorten(string path, int max) =>
        path.Length <= max ? path : "…" + path[^max..];

    static void SetFlag(CheckBox box, List<TileMeta> entries, Func<TileMeta, bool> get, int selCount)
    {
        int on = entries.Count(t => get(t));
        box.CheckState = selCount == 0 ? CheckState.Unchecked
            : on == 0 ? CheckState.Unchecked
            : on == selCount ? CheckState.Checked
            : CheckState.Indeterminate;
        box.Enabled = selCount > 0;
    }

    Bitmap? MakePreview(int sprite)
    {
        if (_sheet == null) return null;
        int cols = _sheet.Width / 8;
        var src = new Rectangle((sprite % cols) * 8, (sprite / cols) * 8, 8, 8);
        if (src.Right > _sheet.Width || src.Bottom > _sheet.Height) return null;
        var bmp = new Bitmap(128, 128);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(33, 38, 63));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_sheet, new Rectangle(0, 0, 128, 128), src, GraphicsUnit.Pixel);
        }
        return bmp;
    }

    /// <summary>
    /// 文本框在获得焦点时记住「当时选中的是哪块瓦片」。
    /// 点图集换选中项时，Leave 会晚于 SelectionChanged 触发，不记住的话会把旧文字写到新瓦片上。
    /// </summary>
    void WireText(Control c)
    {
        c.Enter += (_, _) =>
        {
            if (!_loading) _textSprite = _grid.Selection.Count == 1 ? _grid.Selection[0] : -1;
        };
        c.Leave += (_, _) => CommitText();
        c.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            CommitText();
            e.SuppressKeyPress = true;
        };
    }

    void CommitText()
    {
        if (_loading || _textSprite < 0) return;
        int sprite = _textSprite;
        var t = _meta.Find(sprite);
        string name = _name.Text, group = _groupEdit.Text, note = _note.Text;
        if (t != null && (t.Name != name || t.Group != group || (t.Note ?? "") != note))
        {
            PushUndo();
            t.Name = name;
            t.Group = group;
            t.Note = string.IsNullOrWhiteSpace(note) ? null : note;
            RefreshGroups();
            MarkDirty();
            _grid.Invalidate();
        }
        _textSprite = -1;
        RefreshEditor();
    }

    /// <summary>批量把选中瓦片的阻挡类型设成实心 / 可穿过 / 单向平台（三者互斥）。</summary>
    void BatchSolid(SolidKind kind)
    {
        if (_grid.Selection.Count == 0) return;
        PushUndo();
        foreach (int sprite in _grid.Selection)
        {
            var t = _meta.Ensure(sprite);
            t.Solid = kind == SolidKind.Block;
            t.Platform = kind == SolidKind.Platform;
        }
        Changed();
    }

    void FillMissing()
    {
        int n = _grid.Count;
        if (n == 0) { MessageBox.Show(this, "图集没读到，先检查 assets/tiles.png"); return; }
        var missing = Enumerable.Range(0, n).Where(i => _meta.Find(i) == null).ToList();
        if (missing.Count == 0) { RefreshStatus("图集里没有缺失的登记项"); return; }
        PushUndo();
        foreach (int i in missing)
        {
            var t = _meta.Ensure(i);
            t.Name = $"未命名 {i}";
            t.Group = "未分类";
        }
        RefreshGroups();
        Changed();
        RefreshStatus($"补全了 {missing.Count} 格：{string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? "…" : "")}");
    }

    void Search()
    {
        string q = _search.Text.Trim();
        if (q.Length == 0) return;
        var hits = _meta.Tiles.Where(t =>
                t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Group.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Sprite.ToString() == q ||
                (t.Note ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Sprite).ToList();
        if (hits.Count == 0)
        {
            if (int.TryParse(q, out int sprite) && sprite >= 0 && sprite < _grid.Count) hits.Add(sprite);
        }
        if (hits.Count == 0) { RefreshStatus($"没找到「{q}」"); return; }
        _grid.SetSelection(hits);
        RefreshStatus($"找到 {hits.Count} 块：{string.Join(", ", hits.Take(14))}{(hits.Count > 14 ? "…" : "")}");
    }

    void SelectAll() => _grid.SetSelection(Enumerable.Range(0, _grid.Count));

    // ------------------------------------------------------------------ 命令

    void Save()
    {
        CommitText();
        try
        {
            _meta.Save(_path);
            _saved = DeepClone(_meta);
            MarkSaved();
            RefreshEditor();
            RefreshStatus($"已保存 {_meta.Tiles.Count} 条 → {_path}；请运行 build.cmd 同步到游戏与关卡编辑器");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message);
        }
    }

    void Reload()
    {
        if (_dirty && MessageBox.Show(this, "有未保存的改动，确定丢弃并重新载入？", "重新载入",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        LoadAll();
        RefreshStatus("已重新载入 " + _path);
    }

    void OpenFolder()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir != null && Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(DeepClone(_meta));
        _meta = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        RefreshGroups();
        Changed();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(DeepClone(_meta));
        _meta = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        RefreshGroups();
        Changed();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+A 在文本框里应该是全选文字，不是全选瓦片
        if (e.Control && e.KeyCode == Keys.A && ActiveControl is TextBox) return;
        if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; }
        if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty) return;
        var r = MessageBox.Show(this, "有未保存的改动，要保存后再退出吗？", "退出",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) { e.Cancel = true; return; }
        if (r == DialogResult.Yes) Save();
    }

    void RefreshStatus(string? message = null)
    {
        _statusLabel.Text = message ??
            $"{_meta.Tiles.Count}/{_grid.Count} 格已登记 · {_grid.Selection.Count} 格选中 · " +
            (_dirty ? "未保存" : "已保存") + " · " + Shorten(_path, 46);
    }

    // ------------------------------------------------------------------ 影响预览

    /// <summary>
    /// 列出「关卡里没显式写属性的图例」中，哪些会因为这次编辑（相对上次保存）改变行为。
    /// 和游戏 Level.Load / 编辑器 EffectiveSolid 用同一套判定：主贴图 + 显式优先。
    /// </summary>
    string BuildImpactReport()
    {
        var lines = new List<string>
        {
            $"数据源：{_path}",
            $"对比基线：上次保存时的 {_saved.Tiles.Count} 条登记",
            "规则：逐格 solid > 关卡图例显式值 > 瓦片默认 > false；多精灵图例取第一个主贴图。",
            "注意：游戏 / 关卡编辑器读的是 build.cmd 复制进输出目录的副本，改完需重新构建。",
            "",
        };

        string levelsDir = Path.Combine(Path.GetDirectoryName(_path) ?? "", "levels");
        if (!Directory.Exists(levelsDir))
        {
            lines.Add("找不到关卡目录：" + levelsDir);
            return string.Join(Environment.NewLine, lines);
        }

        string[] flags = { "solid", "water", "climb", "spring" };
        int totalChanged = 0, totalExplicit = 0;
        foreach (string file in Directory.GetFiles(levelsDir, "*.json").OrderBy(f => f))
        {
            var hits = new List<string>();
            int explicitCount = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("tiles", out var tiles)) continue;
                foreach (var prop in tiles.EnumerateObject())
                {
                    var v = prop.Value;
                    int sprite = FirstSprite(v);
                    if (sprite < 0) continue;
                    var before = _saved.Find(sprite);
                    var after = _meta.Find(sprite);
                    foreach (string flag in flags)
                    {
                        if (v.TryGetProperty(flag, out _)) { if (flag == "solid") explicitCount++; continue; }
                        bool b = FlagValue(before, flag), a = FlagValue(after, flag);
                        if (b != a)
                            hits.Add($"  {prop.Name,-3} sprite {sprite,-3} {flag,-6} {Word(b)} → {Word(a)}");
                    }
                }
            }
            catch (Exception ex)
            {
                hits.Add("  读取失败：" + ex.Message);
            }

            totalExplicit += explicitCount;
            totalChanged += hits.Count;
            lines.Add($"【{Path.GetFileName(file)}】显式写 solid 的图例 {explicitCount} 个（不受影响）；" +
                      (hits.Count == 0 ? "本次改动不影响它" : $"以下 {hits.Count} 项会变："));
            lines.AddRange(hits);
            lines.Add("");
        }

        lines.Add($"合计：{totalChanged} 项会变；{totalExplicit} 个图例因关卡显式写了 solid 而不受影响。");
        if (totalChanged == 0)
            lines.Add("本次改动没有改变任何现有关卡的默认行为（新图例 / 未表态的图例以后会用新默认值）。");
        return string.Join(Environment.NewLine, lines);
    }

    static int FirstSprite(JsonElement v)
    {
        if (v.TryGetProperty("sprites", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            var first = arr[0];
            if (first.ValueKind == JsonValueKind.Number) return first.GetInt32();
        }
        if (v.TryGetProperty("sprite", out var one) && one.ValueKind == JsonValueKind.Number)
            return one.GetInt32();
        return -1;
    }

    static bool FlagValue(TileMeta? t, string flag) => flag switch
    {
        "solid" => t?.Solid ?? false,
        "water" => t?.Water ?? false,
        "climb" => t?.Climb ?? false,
        "spring" => t?.Spring ?? false,
        _ => false,
    };

    static string Word(bool v) => v ? "是" : "否";

    void ShowImpact()
    {
        var f = new Form
        {
            Text = "影响预览 · 这次改动会改变哪些关卡",
            ClientSize = new Size(760, 560),
            StartPosition = FormStartPosition.CenterParent,
            ShowIcon = false,
        };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Text = BuildImpactReport(),
            BackColor = Color.FromArgb(24, 27, 34),
            ForeColor = Color.FromArgb(220, 225, 235),
        };
        f.Controls.Add(tb);
        f.ShowDialog(this);
    }
}
