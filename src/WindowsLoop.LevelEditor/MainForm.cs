using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WindowsLoop.Tileset;

namespace WindowsLoop.LevelEditor;

internal sealed class MainForm : Form
{
    const int SheetCols = 8;
    const int SourceTile = 8;
    const int LayoutUnit = 10;   // 预览 1 格 = 10px；游戏里 1 格 = 40px，缩放 1/4
    const int ViewTilesX = 15, ViewTilesY = 10;
    const int MenuH = 26, StatusH = 24, LeftW = 252;

    /// <summary>背景层在游戏里被压暗到的亮度，预览按同样的暗度画，所见即所得。</summary>
    const float BackdropAlpha = 0.5f;

    LevelDoc _doc = LevelDoc.NewLevel();
    string? _path;
    Bitmap? _sheet;
    int _pieceIndex;
    char _brush = '#';

    /// <summary>画笔在所属图例的 sprites 里取第几个；-1 = 没指定（走坐标哈希）。</summary>
    int _brushVar = -1;

    /// <summary>画笔的朝向编码，和关卡 xf 层同一套：低两位是顺时针 90° 步数，bit2 是水平镜像。</summary>
    int _brushXf;

    /// <summary>新画格子的阻挡标记：-1 = 跟随瓦片，0 = 可穿过，1 = 实心，2 = 单向平台。</summary>
    int _brushSolid = -1;

    /// <summary>新画格子是否压暗绘制（原来的"背景"）。</summary>
    bool _brushDim;

    /// <summary>「事件」工具往格子上刷的事件下标；-1 = 事件表为空，没得刷。</summary>
    int _evtIndex = -1;

    /// <summary>最近一次用事件工具点过的格子（连同它属于哪块地图），右侧「这一格」面板用它。</summary>
    Point? _evtCell;
    int _evtCellPiece = -1;

    /// <summary>正在把数据同步到界面控件，别让控件事件反过来改数据。</summary>
    bool _evtSyncing;

    /// <summary>光标所在的格，H/V/R/S/D 会直接改这一格。</summary>
    Point? _hoverCell;

    /// <summary>最后一次落在画布上的格。鼠标移出画布/去点菜单时，Ctrl+V 还知道该贴哪儿。</summary>
    Point? _lastHoverCell;

    // ---- 框选 / 剪贴板 / 批量移动 ----
    Rectangle? _selection;          // 已确定的选区（网格坐标）
    Point? _marqueeStart, _marqueeEnd;
    bool _selecting;                // 正在拖框
    bool _moving;                   // 正在拖动选区
    Point _moveStartCell;
    Rectangle? _movePreview;        // 拖拽落位预览
    string _tool = "paint";
    int _cell = 24;
    bool _dirty, _painting, _restoring;
    readonly List<Point> _layout = new();
    readonly Dictionary<string, RadioButton> _toolRadios = new();
    readonly List<(string json, int piece)> _undo = new(), _redo = new();
    readonly Dictionary<float, ImageAttributes> _alphaCache = new();
    int _dragIndex = -1;
    Point _dragOffset;

    readonly MenuStrip _menu = new();
    readonly StatusStrip _status = new();
    readonly Panel _left = new();
    readonly TabControl _tabs = new();
    readonly TextBox _nameBox = new();
    readonly NumericUpDown _maxBox = new(), _wBox = new(), _hBox = new(), _cellBox = new();
    readonly ListBox _pieceList = new();
    readonly Label _pieceInfo = new();
    readonly Panel _canvas = new(), _layoutPanel = new();
    readonly SheetPanel _sheetPanel = new();
    readonly ComboBox _solidBox = new();
    readonly CheckBox _dimBox = new();
    readonly ToolStripStatusLabel _statusLabel = new();

    // ---- 网格事件页 ----
    readonly ListBox _eventList = new();
    readonly TextBox _evtName = new();
    readonly ComboBox _evtTrigger = new(), _actType = new(), _actTarget = new();
    readonly TextBox _actArgs = new();
    readonly ListBox _actList = new();
    readonly ListBox _condList = new();
    readonly ComboBox _condPiece = new();
    readonly NumericUpDown _camXMin = new(), _camXMax = new(), _camYMin = new(), _camYMax = new();
    readonly CheckBox _camXOn = new(), _camYOn = new();
    readonly ListBox _cellEvtList = new();
    readonly Label _cellEvtLabel = new();
    readonly Font _tagFont = new("Consolas", 9f, FontStyle.Bold);
    readonly WheelRouter _wheelRouter;

    PieceDoc? CurPiece =>
        _pieceIndex >= 0 && _pieceIndex < _doc.Pieces.Count ? _doc.Pieces[_pieceIndex] : null;

    public MainForm(string? openPath = null, int tab = 0)
    {
        Text = "WindowsLoop · 关卡编辑器";
        ClientSize = new Size(1280, 780);
        StartPosition = FormStartPosition.CenterScreen;

        try { _sheet = new Bitmap(LevelStore.TileSheetPath); }
        catch { _sheet = null; }

        BuildUi();
        NewDocument();
        DoLayout();

        // 滚轮只按光标位置路由：悬在画布上就缩放/滚动画布，不会跑去改左边的数字框
        _wheelRouter = new WheelRouter(this);
        Application.AddMessageFilter(_wheelRouter);

        if (openPath != null) LoadFile(openPath, quiet: true);
        _tabs.SelectedIndex = Math.Clamp(tab, 0, _tabs.TabCount - 1);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(_wheelRouter);
        base.OnFormClosed(e);
    }

    /// <summary>
    /// H / V / R 三个字母做翻转与旋转。作用两处：改画笔朝向（之后画的都带这个朝向），
    /// 同时把光标下的那一格就地转一下——单格微调不必重新刷一遍。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (IsTypingInField()) return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Control | Keys.X:
                CutSelection();
                return true;
            case Keys.Control | Keys.C:
                CopySelection();
                return true;
            case Keys.Control | Keys.V:
                PasteAtMouse();
                return true;
            case Keys.Escape:
                if (_selection != null) { ClearSelection(); return true; }
                break;
            case Keys.Up:
            case Keys.Down:
            case Keys.Left:
            case Keys.Right:
                // 列表框 / 下拉框自己也要用方向键，别抢
                if (_selection != null && ActiveControl is not ListBox && ActiveControl is not ComboBox)
                {
                    NudgeSelection(
                        keyData == Keys.Left ? -1 : keyData == Keys.Right ? 1 : 0,
                        keyData == Keys.Up ? -1 : keyData == Keys.Down ? 1 : 0);
                    return true;
                }
                break;
            case Keys.H:
            case Keys.V:
            case Keys.R:
                ApplyOrientation(keyData);
                return true;
            case Keys.S:
                CycleBrushSolid();
                return true;
            case Keys.D:
                ToggleBrushDim();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>焦点在文本/数字输入框里时不要抢字母键。</summary>
    bool IsTypingInField() =>
        ActiveControl is TextBoxBase or UpDownBase ||
        ActiveControl?.GetType().Name.Contains("UpDown", StringComparison.Ordinal) == true;

    /// <summary>把一次翻转/旋转作用到当前画笔和光标下的格子。</summary>
    void ApplyOrientation(Keys key)
    {
        _brushXf = CombineOrientation(_brushXf, key);

        var p = CurPiece;
        string? hint = null;
        if (p != null && _hoverCell is Point hc && !PieceDoc.IsAir(p.GetCell(hc.X, hc.Y)))
        {
            PushUndo();
            int next = CombineOrientation(p.GetXf(hc.X, hc.Y), key);
            p.SetXf(hc.X, hc.Y, next);
            MarkDirty();
            hint = $"({hc.X},{hc.Y}){XfName(next)}";
        }

        RefreshStatus(hint);
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
    }

    /// <summary>
    /// S：在「跟随瓦片 → 总是挡 → 可穿过 → 平台」之间循环。
    /// 同时改画笔（之后画的都带这个标记）和光标下的那一格。
    /// </summary>
    void CycleBrushSolid()
    {
        _brushSolid = _brushSolid switch
        {
            -1 => 1,
            1 => 0,
            0 => 2,
            _ => -1,
        };
        _solidBox.SelectedIndex = _brushSolid switch { 1 => 1, 0 => 2, 2 => 3, _ => 0 };

        var p = CurPiece;
        string? hint = null;
        if (p != null && _hoverCell is Point hc && !PieceDoc.IsAir(p.GetCell(hc.X, hc.Y)))
        {
            PushUndo();
            int next = p.GetSolid(hc.X, hc.Y) switch
            {
                -1 => 1,
                1 => 0,
                0 => 2,
                _ => -1,
            };
            p.SetSolid(hc.X, hc.Y, next);
            MarkDirty();
            hint = $"({hc.X},{hc.Y}) 阻挡：{SolidName(next)}";
        }

        RefreshStatus(hint);
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
    }

    /// <summary>D：切换「变暗」。</summary>
    void ToggleBrushDim()
    {
        _brushDim = !_brushDim;
        _dimBox.Checked = _brushDim;

        var p = CurPiece;
        string? hint = null;
        if (p != null && _hoverCell is Point hc && !PieceDoc.IsAir(p.GetCell(hc.X, hc.Y)))
        {
            PushUndo();
            bool next = !p.GetDim(hc.X, hc.Y);
            p.SetDim(hc.X, hc.Y, next);
            MarkDirty();
            hint = $"({hc.X},{hc.Y}) {(next ? "变暗" : "正常亮度")}";
        }

        RefreshStatus(hint);
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
    }

    static string SolidName(int v) => v switch
    {
        1 => "挡",
        0 => "可穿过",
        2 => "平台",
        _ => "跟随瓦片",
    };

    /// <summary>
    /// 朝向编码（低两位 = 顺时针 90° 步数，bit2 = 水平镜像）上的三种基本操作。
    /// 垂直翻转等于「水平翻转 + 180°」，所以没有独立的 bit。
    /// </summary>
    internal static int CombineOrientation(int xf, Keys key) => key switch
    {
        Keys.H => xf ^ 4,
        Keys.V => ((((xf & 3) + 2) & 3) | ((xf ^ 4) & 4)),
        Keys.R => ((((xf & 3) + 1) & 3) | (xf & 4)),
        _ => xf,
    };

    void BuildUi()
    {
        var mFile = new ToolStripMenuItem("文件(&F)");
        mFile.DropDownItems.Add(new ToolStripMenuItem("新建(&N)", null, (_, _) => NewDocument()) { ShortcutKeys = Keys.Control | Keys.N });
        mFile.DropDownItems.Add(new ToolStripMenuItem("打开…(&O)", null, (_, _) => Open()) { ShortcutKeys = Keys.Control | Keys.O });
        mFile.DropDownItems.Add(new ToolStripMenuItem("保存(&S)", null, (_, _) => Save(false)) { ShortcutKeys = Keys.Control | Keys.S });
        mFile.DropDownItems.Add(new ToolStripMenuItem("另存为…(&A)", null, (_, _) => Save(true)));
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add(new ToolStripMenuItem("退出", null, (_, _) => Close()));
        _menu.Items.Add(mFile);

        var mEdit = new ToolStripMenuItem("编辑(&E)");
        mEdit.DropDownItems.Add(new ToolStripMenuItem("撤销", null, (_, _) => Undo()) { ShortcutKeys = Keys.Control | Keys.Z });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("重做", null, (_, _) => Redo()) { ShortcutKeys = Keys.Control | Keys.Y });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("重做（备用键）", null, (_, _) => Redo()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.Z });
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        mEdit.DropDownItems.Add(new ToolStripMenuItem("剪切选区(&X)", null, (_, _) => CutSelection()) { ShortcutKeys = Keys.Control | Keys.X });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("复制选区(&C)", null, (_, _) => CopySelection()) { ShortcutKeys = Keys.Control | Keys.C });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("粘贴到鼠标处(&V)", null, (_, _) => PasteAtMouse()) { ShortcutKeys = Keys.Control | Keys.V });
        mEdit.DropDownItems.Add(new ToolStripMenuItem("取消选区  Esc", null, (_, _) => ClearSelection()));
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        mEdit.DropDownItems.Add(new ToolStripMenuItem("清空这块地图", null, (_, _) => ClearPiece()));
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        mEdit.DropDownItems.Add(new ToolStripMenuItem("重新载入瓦片默认值(&T)", null, (_, _) => ReloadTileDefaults()));
        _menu.Items.Add(mEdit);

        var mMap = new ToolStripMenuItem("地图(&M)");
        mMap.DropDownItems.Add(new ToolStripMenuItem("添加一块地图", null, (_, _) => AddPiece()));
        mMap.DropDownItems.Add(new ToolStripMenuItem("复制选中地图", null, (_, _) => DuplicatePiece()));
        mMap.DropDownItems.Add(new ToolStripMenuItem("删除选中地图", null, (_, _) => RemovePiece()));
        mMap.DropDownItems.Add(new ToolStripMenuItem("重命名选中地图", null, (_, _) => RenamePiece()));
        _menu.Items.Add(mMap);
        MainMenuStrip = _menu;

        // ---- 左侧：关卡元数据 + 地图列表 + 图层 + 工具 ----
        _left.AutoScroll = true;
        Lbl(_left, "关卡名", 8, 8);
        Put(_left, _nameBox, 8, 28, 226, 24);
        _nameBox.TextChanged += (_, _) => { _doc.Name = _nameBox.Text; MarkDirty(); };

        Lbl(_left, "窗口上限 maxWindows", 8, 58);
        _maxBox.Minimum = 1; _maxBox.Maximum = 16;
        Put(_left, _maxBox, 8, 78, 100, 24);
        _maxBox.ValueChanged += (_, _) => { _doc.MaxWindows = (int)_maxBox.Value; MarkDirty(); };

        Lbl(_left, "地图块（顺序即开窗顺序）", 8, 108);
        Put(_left, _pieceList, 8, 128, 226, 128);
        _pieceList.SelectedIndexChanged += (_, _) => { _pieceIndex = _pieceList.SelectedIndex; RefreshPiece(); };
        _pieceList.DoubleClick += (_, _) => RenamePiece();

        _left.Controls.AddRange(new Control[]
        {
            Btn("添加", 8, 262, 55, 28, (_, _) => AddPiece()),
            Btn("复制", 68, 262, 55, 28, (_, _) => DuplicatePiece()),
            Btn("删除", 128, 262, 55, 28, (_, _) => RemovePiece()),
            Btn("改名", 188, 262, 46, 28, (_, _) => RenamePiece()),
        });

        Lbl(_left, "选中地图尺寸（宽 / 高）", 8, 294);
        _wBox.Minimum = 1; _wBox.Maximum = 400;
        _hBox.Minimum = 2; _hBox.Maximum = 400;
        Put(_left, _wBox, 8, 314, 70, 24);
        Put(_left, _hBox, 86, 314, 70, 24);
        _left.Controls.Add(Btn("应用", 164, 314, 70, 24, (_, _) => ResizePiece()));
        Put(_left, _pieceInfo, 8, 342, 226, 22);

        // 每格两个属性取代了原来的"前景层 / 背景层"：阻挡和变暗各管各的，
        // 所以同一个瓦片可以这格挡人、那格不挡，也可以挡住的同时压暗。
        var markBox = new GroupBox { Text = "画笔标记", Left = 8, Top = 370, Width = 226, Height = 96 };
        _solidBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _solidBox.Items.AddRange(new object[]
        {
            "阻挡：跟随瓦片",
            "阻挡：总是挡",
            "阻挡：可穿过",
            "阻挡：平台（下穿上站·侧面不挡）",
        });
        _solidBox.SetBounds(10, 22, 206, 24);
        _solidBox.SelectedIndex = 0;
        _solidBox.SelectedIndexChanged += (_, _) =>
        {
            _brushSolid = _solidBox.SelectedIndex switch { 1 => 1, 2 => 0, 3 => 2, _ => -1 };
            RefreshStatus();
        };
        markBox.Controls.Add(_solidBox);

        _dimBox.Text = "变暗（背景/远景）";
        _dimBox.SetBounds(10, 50, 190, 24);
        _dimBox.CheckedChanged += (_, _) => { _brushDim = _dimBox.Checked; RefreshStatus(); };
        markBox.Controls.Add(_dimBox);

        markBox.Controls.Add(new Label
        {
            Text = "S 循环阻挡 · D 切换变暗", Left = 10, Top = 74, Width = 206, Height = 18,
            ForeColor = SystemColors.GrayText,
        });
        _left.Controls.Add(markBox);

        var tools = new GroupBox { Text = "工具", Left = 8, Top = 440, Width = 226, Height = 128 };
        AddTool(tools, "画笔", "paint", 10, 20, true);
        AddTool(tools, "橡皮", "erase", 114, 20);
        AddTool(tools, "出生点", "spawn", 10, 46);
        AddTool(tools, "事件", "evt", 114, 46);
        AddTool(tools, "加事件", "evtadd", 10, 72);
        AddTool(tools, "擦事件", "noevt", 114, 72);
        AddTool(tools, "框选", "select", 10, 98);
        AddTool(tools, "选中", "pick", 114, 98);
        _left.Controls.Add(tools);

        Lbl(_left, "格子大小（像素）", 8, 578);
        _cellBox.Minimum = 12; _cellBox.Maximum = 48; _cellBox.Value = 24;
        Put(_left, _cellBox, 8, 598, 100, 24);
        _cellBox.ValueChanged += (_, _) =>
        {
            _cell = (int)_cellBox.Value;
            UpdateScrollSize(); _canvas.Invalidate(); RefreshStatus();
        };

        // 图例不再单独开窗口：右边整张图集就是调色板，点一格就能画。
        // 瓦片的默认物理属性（实心 / 平台 / 水 / 梯子 / 弹簧）统一在瓦片默认值工具里改。
        Lbl(_left, "点右侧图集选瓦片；默认属性在「瓦片默认值编辑器」里改。", 8, 630);
        Lbl(_left, "左键绘制，右键擦除；Ctrl+滚轮缩放。", 8, 652);
        Lbl(_left, "框选工具 / Shift+拖拽 选区（会自动切到框选）。", 8, 674);
        Lbl(_left, "有选区时左键拖拽=整体移动；想继续画先 Esc。", 8, 696);
        Lbl(_left, "Ctrl+X/C/V 剪切复制粘贴，方向键逐格移动。", 8, 718);
        Lbl(_left, "「事件」=设为单条，「加事件」=往这格追加一条。", 8, 740);

        // ---- 右侧：地图编辑 / 拼接预览 ----
        var pageEdit = new TabPage("地图编辑");
        _canvas.Dock = DockStyle.Fill;
        _canvas.AutoScroll = true;
        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        _canvas.MouseLeave += (_, _) => _hoverCell = null;
        pageEdit.Controls.Add(_canvas);

        // 右侧直接铺整张图集：素材有 90 多格，按字符名找太费劲，点哪格就用哪格。
        var palHost = new Panel
        {
            Dock = DockStyle.Right, Width = 268,
            BackColor = Color.FromArgb(26, 29, 37),
        };
        _sheetPanel.Dock = DockStyle.Fill;
        _sheetPanel.Picked += OnSheetPicked;
        _sheetPanel.Hovered += _ => RefreshStatus();
        _sheetPanel.SetSheet(_sheet);
        palHost.Controls.Add(_sheetPanel);
        pageEdit.Controls.Add(palHost);

        var pageLayout = new TabPage("拼接预览");
        _layoutPanel.Dock = DockStyle.Fill;
        _layoutPanel.Paint += Layout_Paint;
        _layoutPanel.MouseDown += Layout_MouseDown;
        _layoutPanel.MouseMove += Layout_MouseMove;
        _layoutPanel.MouseUp += Layout_MouseUp;
        pageLayout.Controls.Add(_layoutPanel);
        pageLayout.Controls.Add(new Label
        {
            Text = "拖动缩略窗口摆放，按 10px 网格对齐（= 游戏里 40px）。青线 = 接缝可通行，红线 = 那一格有墙走不过去；"
                 + "斜纹 = 窗口重叠（游戏里重叠不算相连）。此布局只用于预览，不写入关卡 JSON。",
            Dock = DockStyle.Top,
            Height = 44,
        });

        _tabs.TabPages.Add(pageEdit);
        _tabs.TabPages.Add(pageLayout);
        _tabs.TabPages.Add(BuildEventsPage());

        _status.Items.Add(_statusLabel);
        Controls.AddRange(new Control[] { _menu, _status, _left, _tabs });
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        DoLayout();
    }
    void DoLayout()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        _menu.SetBounds(0, 0, w, MenuH);
        _status.SetBounds(0, h - StatusH, w, StatusH);
        int bodyH = Math.Max(1, h - MenuH - StatusH);
        _left.SetBounds(0, MenuH, LeftW, bodyH);
        _tabs.SetBounds(LeftW, MenuH, Math.Max(1, w - LeftW), bodyH);
    }

    /// <summary>光标在画布上时的滚轮处理：Ctrl 缩放，否则滚动。</summary>
    public bool HandleCanvasWheel(int delta)
    {
        if (_tabs.SelectedIndex != 0 || !IsHandleCreated) return false;
        if (!_canvas.RectangleToScreen(_canvas.ClientRectangle).Contains(Cursor.Position)) return false;

        if ((ModifierKeys & Keys.Control) != 0)
        {
            int next = Math.Clamp(_cell + (delta > 0 ? 4 : -4), 12, 48);
            if (next != _cell) _cellBox.Value = next;
        }
        else
        {
            var pos = _canvas.AutoScrollPosition;
            _canvas.AutoScrollPosition = new Point(-pos.X, -pos.Y - delta);
            _canvas.Invalidate();
        }
        return true;
    }

    // ---------- 小控件辅助 ----------
    static Label Lbl(Control host, string text, int x, int y, int width = 226)
    {
        var l = new Label { Text = text, Left = x, Top = y, Width = width, Height = 20 };
        host.Controls.Add(l);
        return l;
    }

    static void Put(Control host, Control c, int x, int y, int w, int h)
    {
        c.SetBounds(x, y, w, h);
        host.Controls.Add(c);
    }

    static Button Btn(string text, int x, int y, int w, int h, EventHandler click)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = h };
        b.Click += click;
        return b;
    }

    void AddTool(GroupBox box, string label, string tool, int x, int y, bool first = false)
    {
        var rb = new RadioButton
        {
            Text = label, Left = x, Top = y, Width = 104, Height = 24, Checked = first,
        };
        rb.CheckedChanged += (_, _) => { if (rb.Checked) { _tool = tool; RefreshStatus(); } };
        _toolRadios[tool] = rb;
        box.Controls.Add(rb);
    }

    void SyncToolRadios()
    {
        foreach (var kv in _toolRadios) kv.Value.Checked = kv.Key == _tool;
    }

    // ---------- 撤销 / 重做 ----------
    void PushUndo()
    {
        if (_restoring) return;
        _undo.Add((LevelStore.Serialize(_doc), _pieceIndex));
        if (_undo.Count > 120) _undo.RemoveAt(0);
        _redo.Clear();
    }

    void Undo()
    {
        if (_undo.Count == 0) { RefreshStatus("已经是第一步"); return; }
        var cur = (LevelStore.Serialize(_doc), _pieceIndex);
        var snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(cur);
        Restore(snap);
    }

    void Redo()
    {
        if (_redo.Count == 0) { RefreshStatus("没有可重做的操作"); return; }
        var cur = (LevelStore.Serialize(_doc), _pieceIndex);
        var snap = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(cur);
        Restore(snap);
    }

    void Restore((string json, int piece) snap)
    {
        _restoring = true;
        try
        {
            _doc = LevelStore.Deserialize(snap.json);
            _pieceIndex = Math.Clamp(snap.piece, 0, Math.Max(0, _doc.Pieces.Count - 1));
            ResetLayoutIfNeeded();
            RefreshAll();
            _dirty = true;
        }
        finally { _restoring = false; }
        RefreshStatus("已恢复");
    }

    // ---------- 文档 ----------
    void NewDocument()
    {
        ResetSelectionState();
        _doc = LevelDoc.NewLevel();
        _path = null;
        _pieceIndex = 0;
        _brush = '#';
        _brushVar = -1;
        _brushXf = 0;
        _tool = "paint";
        _brushSolid = -1;
        _brushDim = false;
        _solidBox.SelectedIndex = 0;
        _dimBox.Checked = false;
        _undo.Clear();
        _redo.Clear();
        SyncToolRadios();
        ResetLayout();
        RefreshAll();
        _dirty = false;
        RefreshStatus();
    }

    void Open()
    {
        using var d = new OpenFileDialog
        {
            Filter = "关卡 JSON|*.json|所有文件|*.*",
            InitialDirectory = LevelStore.LevelsDir,
        };
        if (d.ShowDialog() != DialogResult.OK) return;
        LoadFile(d.FileName);
    }

    void LoadFile(string path, bool quiet = false)
    {
        try
        {
            _doc = LevelStore.Load(path);
            _path = path;
            _pieceIndex = 0;
            _undo.Clear();
            _redo.Clear();
            ResetLayout();
            RefreshAll();
            _dirty = false;
            // 上一关的画笔字符可能在这一关没定义（画下去就成了红色 '?'），换成本关的第一个前景瓦片
            _brush = PickBrush();
            _brushVar = -1;
            _brushXf = 0;
            SyncSheetSelection();
            RefreshStatus("已打开 " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(this, "打开失败：" + ex.Message);
            else RefreshStatus("打开失败：" + ex.Message);
        }
    }

    void Save(bool asNew)
    {
        string? target = _path;
        if (asNew || target == null)
        {
            using var d = new SaveFileDialog
            {
                Filter = "关卡 JSON|*.json",
                InitialDirectory = LevelStore.LevelsDir,
                FileName = _path == null ? "new-level.json" : Path.GetFileName(_path),
            };
            if (d.ShowDialog() != DialogResult.OK) return;
            target = d.FileName;
        }

        string err = LevelStore.Validate(_doc);
        if (err != "") { MessageBox.Show(this, "不能保存：" + err, "校验未通过"); return; }

        try
        {
            LevelStore.Save(target, _doc);
            _path = target;
            _dirty = false;
            RefreshStatus("已保存 " + Path.GetFileName(target));
        }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message); }
    }

    void MarkDirty()
    {
        _dirty = true;
        RefreshStatus();
    }

    void RefreshAll()
    {
        _nameBox.Text = _doc.Name;
        _maxBox.Value = Math.Clamp(_doc.MaxWindows, 1, 16);
        RefreshPieceList();
        RefreshSheet();
        RefreshEventList();
        RefreshCellEvents();
        ResetLayoutIfNeeded();
        UpdateScrollSize();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
        _dirty = false;
        RefreshStatus();
    }

    void RefreshPieceList()
    {
        _pieceList.Items.Clear();
        foreach (var p in _doc.Pieces)
            _pieceList.Items.Add($"{p.Id}  ({p.Width}×{p.Height}){(p.HasDim ? "  dim" : "")}");
        if (_doc.Pieces.Count == 0) _pieceIndex = -1;
        else
        {
            _pieceIndex = Math.Clamp(_pieceIndex, 0, _doc.Pieces.Count - 1);
            _pieceList.SelectedIndex = _pieceIndex;
        }
        RefreshPiece();
    }

    void RefreshPiece()
    {
        var p = CurPiece;
        if (p != null)
        {
            _wBox.Value = Math.Clamp(p.Width, 1, 400);
            _hBox.Value = Math.Clamp(p.Height, 2, 400);
        }
        _hoverCell = null;   // 换地图后旧坐标没意义，快捷键不能作用在上面
        ResetSelectionState();  // 选区也是旧坐标，换块 / 撤销 / 重载后一律清掉
        UpdatePieceInfo();
        RefreshCellEvents();    // "这一格"也属于旧坐标，跟着重算（换块后自动回到"点一个格子"）
        UpdateScrollSize();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
        RefreshStatus();
    }

    void UpdatePieceInfo()
    {
        var p = CurPiece;
        if (p == null) { _pieceInfo.Text = "无地图"; return; }
        _pieceInfo.Text = $"{p.Width}×{p.Height}  spawn=({p.Spawn[0]},{p.Spawn[1]})  " +
                          $"事件格={CountEvtCells(p)}";
    }

    static int CountEvtCells(PieceDoc p)
    {
        if (p.Evt == null) return 0;
        int n = 0;
        foreach (var row in p.Evt)
            foreach (char c in row)
                if (PieceDoc.DigitOf(c) >= 0) n++;
        return n;
    }

    /// <summary>瓦片工具改完默认值并重新 build 后，在这里手动重载一次即可，不必关掉编辑器。</summary>
    void ReloadTileDefaults()
    {
        string path = LevelStore.ReloadTileDefaults();
        RefreshSheet();
        _canvas.Invalidate();
        RefreshStatus($"已重载瓦片默认值（{TileDefaults.Current.Tiles.Count} 条，来自 {Path.GetFileName(path)}）");
    }

    void RefreshStatus(string? hint = null)
    {
        string file = _path == null ? "未保存" : Path.GetFileName(_path);
        string unknown = LevelStore.UnknownChars(_doc);
        string extra = unknown.Length == 0 ? "" : $"  |  未定义字符 '{unknown}'";
        var (seams, open, rows, overlaps) = SeamStats();
        string seamText = seams == 0 ? "无贴合" : $"贴合 {seams} 处 通 {open}/{rows} 格";
        if (overlaps > 0) seamText += $"  重叠 {overlaps} 对";
        int hov = _sheetPanel.HoverIndex;
        string hoverInfo = "";
        if (hov >= 0)
        {
            var m = TileDefaults.ForSprite(hov);
            hoverInfo = m == null ? $"  |  图集 #{hov} 未登记" :
                $"  |  图集 #{hov} {m.Name} 默认{DefaultKindName(m)}";
        }
        string selInfo = _selection is Rectangle s
            ? $"  |  选区 {s.Width}×{s.Height}@({s.X},{s.Y})" : "";
        string clipInfo = GridClipboard.Data == null ? "" : $"  |  剪贴板 {GridClipboard.Description}";

        // 事件表的"能存但不完整"的提醒：目标没填、事件没挂到格子上
        var warns = LevelStore.Warnings(_doc);
        string warnInfo = warns.Count == 0
            ? ""
            : "  |  ⚠ " + string.Join("；", warns.Take(2)) +
              (warns.Count > 2 ? $"…（共 {warns.Count} 条）" : "");

        _statusLabel.Text =
            $"{file}{(_dirty ? " *" : "")}  |  {_doc.Pieces.Count} 块  |  上限 {_doc.MaxWindows}  |  " +
            $"{ToolName()} '{_brush}'" +
            $"{(_brushVar >= 0 ? "[" + _brushVar + "]" : "")}{XfName(_brushXf)}" +
            (_tool == "evt" && _evtIndex >= 0 ? $" → 刷事件 #{_evtIndex}" : "") +
            $"  阻挡:{SolidName(_brushSolid)}{(_brushDim ? "  变暗" : "")}" +
            $"{(TilePresets.NameOf(_brush) is { Length: > 0 } n ? " " + n : "")}  |  {_cell}px  |  {seamText}" +
            selInfo + clipInfo + hoverInfo + extra + warnInfo + (hint == null ? "" : "  |  " + hint);
    }

    /// <summary>把瓦片默认的物理属性说成一个词，状态栏 / 画布提示共用。</summary>
    static string DefaultKindName(TileMeta m) => m.Solid ? "实心" : m.Platform ? "平台" : "可穿过";

    /// <summary>把朝向编码说成人话，状态栏用。</summary>
    static string XfName(int xf)
    {
        if (xf == 0) return "";
        string flip = (xf & 4) != 0 ? "⇄" : "";
        int rot = (xf & 3) * 90;
        return " " + flip + (rot == 0 ? "" : $"↻{rot}°");
    }

    string ToolName() => _tool switch
    {
        "paint" => "画笔", "erase" => "橡皮", "spawn" => "出生点",
        "evt" => "事件", "evtadd" => "加事件", "select" => "框选",
        "pick" => "选中", _ => "擦事件",
    };

    void AddPiece()
    {
        PushUndo();
        var p = PieceDoc.Blank(24, 10);
        p.Id = UniqueId("piece");
        _doc.Pieces.Add(p);
        _layout.Add(new Point(30 + _doc.Pieces.Count * 30, 20 + _doc.Pieces.Count * 20));
        _pieceIndex = _doc.Pieces.Count - 1;
        MarkDirty();
        RefreshPieceList();
        _layoutPanel.Invalidate();
    }

    void DuplicatePiece()
    {
        var src = CurPiece;
        if (src == null) return;
        PushUndo();
        var copy = new PieceDoc
        {
            Id = UniqueId(src.Id + "-copy"),
            Rows = new List<string>(src.Rows),
            Var = src.Var == null ? null : new List<string>(src.Var),
            Xf = src.Xf == null ? null : new List<string>(src.Xf),
            Solid = src.Solid == null ? null : new List<string>(src.Solid),
            Dim = src.Dim == null ? null : new List<string>(src.Dim),
            Evt = src.Evt == null ? null : new List<string>(src.Evt),
            Spawn = src.Spawn.ToArray(),
        };
        _doc.Pieces.Add(copy);
        _layout.Add(new Point(30 + _doc.Pieces.Count * 30, 20 + _doc.Pieces.Count * 20));
        _pieceIndex = _doc.Pieces.Count - 1;
        MarkDirty();
        RefreshPieceList();
        _layoutPanel.Invalidate();
    }

    string UniqueId(string baseId)
    {
        string id = baseId;
        int n = 2;
        while (_doc.Pieces.Any(p => p.Id == id)) id = $"{baseId}{n++}";
        return id;
    }

    void RemovePiece()
    {
        if (CurPiece == null) return;
        if (_doc.Pieces.Count <= 1) { MessageBox.Show(this, "至少要保留一块地图"); return; }
        PushUndo();
        _doc.Pieces.RemoveAt(_pieceIndex);
        if (_layout.Count > _pieceIndex) _layout.RemoveAt(_pieceIndex);
        _pieceIndex = Math.Clamp(_pieceIndex, 0, _doc.Pieces.Count - 1);
        MarkDirty();
        RefreshPieceList();
        _layoutPanel.Invalidate();
    }

    void RenamePiece()
    {
        var p = CurPiece;
        if (p == null) return;
        string? name = AskString("重命名地图块", p.Id);
        if (string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        p.Id = name.Trim();
        MarkDirty();
        RefreshPieceList();
        _layoutPanel.Invalidate();
    }

    void ResizePiece()
    {
        var p = CurPiece;
        if (p == null) return;
        int nw = (int)_wBox.Value, nh = (int)_hBox.Value;
        if (nw == p.Width && nh == p.Height) return;

        bool shrink = nw < p.Width || nh < p.Height;
        if (shrink && MessageBox.Show(this,
                $"缩小会把超出 {nw}×{nh} 的内容直接丢掉（可用 Ctrl+Z 撤销）。继续？",
                "确认缩小", MessageBoxButtons.OKCancel) != DialogResult.OK)
        {
            _wBox.Value = Math.Clamp(p.Width, 1, 400);
            _hBox.Value = Math.Clamp(p.Height, 2, 400);
            return;
        }

        PushUndo();
        var rows = new List<string>();
        for (int y = 0; y < nh; y++)
        {
            string old = y < p.Height ? p.Rows[y] : "";
            rows.Add(old.PadRight(nw, '.').Substring(0, nw));
        }
        p.Rows = rows;

        p.Spawn = new[] { Math.Clamp(p.Spawn[0], 0, nw - 1), Math.Clamp(p.Spawn[1], 0, nh - 1) };
        p.Normalize();
        MarkDirty();
        RefreshPieceList();
        _layoutPanel.Invalidate();
    }

    /// <summary>当前画笔在本关没定义时，换成本关第一个前景瓦片（避免一画就是红色 '?'）。</summary>
    char PickBrush()
    {
        if (_brush != '.' && _doc.Tiles.ContainsKey(_brush.ToString())) return _brush;
        var keys = _doc.Tiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        string? pick = keys.FirstOrDefault(k => TilePresets.GroupOf(k[0]) != TilePresets.GGroupBack)
                       ?? keys.FirstOrDefault();
        return pick is { Length: > 0 } ? pick[0] : '#';
    }

    /// <summary>清空当前地图的全部格子和标记（现在只有一层，所以是一次清空）。</summary>
    void ClearPiece()
    {
        var p = CurPiece;
        if (p == null) return;
        if (MessageBox.Show(this, $"清空「{p.Id}」的所有格子和标记？", "确认",
                MessageBoxButtons.OKCancel) != DialogResult.OK) return;

        PushUndo();
        p.Rows = Enumerable.Repeat(new string('.', Math.Max(1, p.Width)), p.Height).ToList();
        // 标记是一整套，漏清任何一层都会留下幽灵数据
        p.Var = null;
        p.Xf = null;
        p.Solid = null;
        p.Dim = null;
        p.Evt = null;

        MarkDirty();
        UpdatePieceInfo();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
    }

    static string? AskString(string title, string def)
    {
        using var f = new Form
        {
            Text = title, ClientSize = new Size(320, 100),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
        };
        var tb = new TextBox { Text = def, Left = 12, Top = 14, Width = 290 };
        var ok = new Button { Text = "确定", Left = 140, Top = 48, Width = 76, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 226, Top = 48, Width = 76, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { tb, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    // ---------- 右侧图集面板 ----------
    /// <summary>图集换了（改尺寸/重载）之后重新扫描空格并同步高亮。</summary>
    void RefreshSheet()
    {
        _sheetPanel.SetSheet(_sheet);
        SyncSheetSelection();
    }

    /// <summary>把当前画笔对应的精灵同步到面板高亮，免得不知道在画哪一格。</summary>
    void SyncSheetSelection()
    {
        if (_doc.Tiles.TryGetValue(_brush.ToString(), out var t))
        {
            var list = t.SpriteList();
            if (list.Length == 0) { _brushVar = -1; _sheetPanel.Selected = -1; }
            else
            {
                // 越界的下标要归成"未指定"：写进数据后 SpriteAt 会退回哈希，
                // 表现又是"选了这个画出来是另一个"
                if (_brushVar >= list.Length) _brushVar = -1;
                _sheetPanel.Selected = list[_brushVar >= 0 ? _brushVar : 0];
            }
        }
        else { _brushVar = -1; _sheetPanel.Selected = -1; }
        _sheetPanel.Invalidate();
    }

    /// <summary>
    /// 点图集里的某格：优先复用已有图例条目（多精灵条目会顺手定位到对应下标），
    /// 没有就给这个精灵新建一条，这样随便点哪格都能画。
    /// </summary>
    void OnSheetPicked(int sprite)
    {
        // 字典本身没有稳定顺序，排序后才不会"同样的操作有时选到 A 有时选到 B"
        var ordered = _doc.Tiles.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

        // 第一轮：单精灵且正好相等的条目最精确。它带着当初配好的 solid / climb 等属性，
        // 直接用它；否则会落到多精灵条目上（比如点草块却命中实心的岩体池，画出来是一堵墙）。
        foreach (var kv in ordered)
        {
            var list = kv.Value.SpriteList();
            if (list.Length == 1 && list[0] == sprite)
            {
                _brush = kv.Key[0];
                _brushVar = -1;
                UseBrushAsPaint();
                return;
            }
        }

        // 第二轮：多精灵条目，定位到对应下标
        foreach (var kv in ordered)
        {
            var list = kv.Value.SpriteList();
            int at = Array.IndexOf(list, sprite);
            if (at < 0) continue;
            _brush = kv.Key[0];
            _brushVar = at;
            UseBrushAsPaint();
            return;
        }

        PushUndo();   // 新建图例也是一次数据改动，得能撤回
        char? c = NewTileFor(sprite);
        if (c == null) { RefreshStatus("图例字符已用尽，无法为这块新建条目"); return; }
        _brush = c.Value;
        _brushVar = -1;
        UseBrushAsPaint();
        RefreshStatus($"已为 sprite {sprite} 新建图例 '{c}'");
    }

    void UseBrushAsPaint()
    {
        _tool = "paint";
        SyncToolRadios();
        RefreshStatus();
        _canvas.Invalidate();
    }

    /// <summary>这个字符有没有被用到——图例里定义了，或者地图里已经写了（哪怕没定义）。</summary>
    bool CharInUse(char c)
    {
        if (_doc.Tiles.ContainsKey(c.ToString())) return true;
        foreach (var p in _doc.Pieces)
        {
            if (p.Rows.Any(r => r.Contains(c))) return true;
        }
        return false;
    }

    /// <summary>给单个精灵建一条图例，返回分配到的字符；字符池用尽返回 null。</summary>
    char? NewTileFor(int sprite)
    {
        const string pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@$%&+-=?/";
        foreach (char c in pool)
        {
            // 地图里已经出现过但没定义的字符也不能征用：
            // 比如 level1 里有个孤立的 '*'，一旦被新图例认领，那张图的内容就变了
            if (CharInUse(c)) continue;
            _doc.Tiles[c.ToString()] = new TileDoc { Sprite = sprite };
            MarkDirty();
            return c;
        }
        return null;
    }

    // ---------- 网格画布 ----------
    void UpdateScrollSize()
    {
        var p = CurPiece;
        _canvas.AutoScrollMinSize = p == null
            ? Size.Empty
            : new Size(p.Width * _cell + 1, p.Height * _cell + 1);
    }

    ImageAttributes AlphaAttr(float a)
    {
        if (_alphaCache.TryGetValue(a, out var attrs)) return attrs;
        var cm = new ColorMatrix { Matrix33 = a };
        attrs = new ImageAttributes();
        attrs.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        _alphaCache[a] = attrs;
        return attrs;
    }

    void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        var p = CurPiece;
        if (p == null) return;
        var g = e.Graphics;
        g.Clear(Color.FromArgb(22, 24, 30));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TranslateTransform(_canvas.AutoScrollPosition.X, _canvas.AutoScrollPosition.Y);

        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                var r = new Rectangle(x * _cell, y * _cell, _cell, _cell);
                using (var br = new SolidBrush(((x + y) & 1) == 0
                    ? Color.FromArgb(29, 32, 40) : Color.FromArgb(25, 28, 35)))
                    g.FillRectangle(br, r);
            }

        // 单层网格：每格一张图，亮不亮由它自己的 dim 标记决定
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                char c = p.GetCell(x, y);
                if (IsAir(c)) continue;
                DrawTile(g, p, c, new Rectangle(x * _cell, y * _cell, _cell, _cell), 1f, x, y);
            }

        using (var pen = new Pen(Color.FromArgb(48, 53, 66)))
        {
            for (int x = 0; x <= p.Width; x++) g.DrawLine(pen, x * _cell, 0, x * _cell, p.Height * _cell);
            for (int y = 0; y <= p.Height; y++) g.DrawLine(pen, 0, y * _cell, p.Width * _cell, y * _cell);
        }

        // 初始取景框：CamX = spawn.X - 7、CamY = spawn.Y - 5。
        // 取景框可以超出地图边界（游戏里会绕回来），所以按 ±宽/±高 各画一份再裁到地图内。
        var mapRect = new Rectangle(0, 0, p.Width * _cell, p.Height * _cell);
        var clipBefore = g.Clip;
        g.SetClip(mapRect);
        using (var pen = new Pen(Color.FromArgb(150, 120, 200, 255), 2) { DashStyle = DashStyle.Dash })
        {
            int rx = (p.Spawn[0] - ViewTilesX / 2) * _cell;
            int ry = (p.Spawn[1] - ViewTilesY / 2) * _cell;
            int rw = ViewTilesX * _cell, rh = ViewTilesY * _cell;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    g.DrawRectangle(pen, rx + dx * mapRect.Width, ry + dy * mapRect.Height, rw, rh);
        }
        g.Clip = clipBefore;

        Mark(g, p.Spawn[0], p.Spawn[1], "S", Color.FromArgb(90, 230, 160));

        DrawEventMarks(g, p);
        DrawEvtCellMark(g, p);
        DrawSelectionOverlay(g, p);
    }

    /// <summary>当前选中的那一格（点画布就会选中）：描一圈琥珀色，方便确认"我看到的是哪一格"。</summary>
    void DrawEvtCellMark(Graphics g, PieceDoc p)
    {
        if (_evtCell is not Point c || _evtCellPiece != _pieceIndex) return;
        if (c.X < 0 || c.Y < 0 || c.X >= p.Width || c.Y >= p.Height) return;

        using var pen = new Pen(Color.FromArgb(230, 255, 205, 80), 2);
        g.DrawRectangle(pen, c.X * _cell + 1, c.Y * _cell + 1, _cell - 2, _cell - 2);
    }

    /// <summary>
    /// 事件格的小标签：左上角一枚色块，写着事件下标。
    /// 红 = 跳关但目标还没填，品红 = 引用了不存在的事件，青 = 正常。
    /// </summary>
    /// <summary>
    /// 事件格的小标签：左上角一枚色块，写着这一格第一条事件的下标。
    /// 一格挂多条时色块后面再垫一层，看得出是个"堆"。
    /// 红 = 有动作还没填目标，品红 = 引用了不存在的事件 / 组，青 = 正常。
    /// </summary>
    void DrawEventMarks(Graphics g, PieceDoc p)
    {
        if (!p.HasEvt) return;

        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                int si = p.GetEvtSeq(x, y);
                if (si < 0) continue;

                bool exists = si < _doc.EvtSeq.Count;
                var seq = exists ? _doc.EvtSeq[si] : new List<int>();
                bool bad = !exists || seq.Count == 0 ||
                           seq.Any(v => v < 0 || v >= _doc.Events.Count);
                bool noTarget = !bad && seq.Any(v => _doc.Events[v].HasUnsetAction);
                Color color = bad ? Color.FromArgb(255, 110, 230)
                    : noTarget ? Color.FromArgb(255, 110, 110)
                    : Color.FromArgb(110, 225, 255);

                int size = Math.Clamp(_cell / 2, 11, 18);
                var r = new Rectangle(x * _cell + 1, y * _cell + 1, size, size);

                // 多条事件：先垫一层错位的底片，一眼能看出"这格不止一条"
                if (seq.Count > 1)
                {
                    using var back = new SolidBrush(Color.FromArgb(120, color));
                    g.FillRectangle(back, new Rectangle(r.X + 3, r.Y + 3, size, size));
                }

                using (var br = new SolidBrush(Color.FromArgb(215, color)))
                    g.FillRectangle(br, r);

                if (size >= 14)
                {
                    string tag = bad ? "?" : PieceDoc.CharOf(seq[0]).ToString();
                    if (!bad && seq.Count > 1) tag = PieceDoc.CharOf(seq[0]).ToString() + "+";
                    TextRenderer.DrawText(g, tag, _tagFont, r, Color.Black,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
    }

    static bool IsAir(char c) => c == '.' || c == ' ';

    void Mark(Graphics g, int x, int y, string text, Color color)
    {
        var r = new Rectangle(x * _cell + 2, y * _cell + 2, _cell - 4, _cell - 4);
        using var br = new SolidBrush(Color.FromArgb(200, color));
        g.FillEllipse(br, r);
        TextRenderer.DrawText(g, text, _tagFont, r, Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawTile(Graphics g, PieceDoc p, char c, Rectangle dst, float alpha, int x = 0, int y = 0)
    {
        if (!_doc.Tiles.TryGetValue(c.ToString(), out var t))
        {
            using var br = new SolidBrush(Color.FromArgb((int)(150 * alpha), 60, 60));
            g.FillRectangle(br, dst);
            TextRenderer.DrawText(g, "?", _tagFont, dst, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int s = t.SpriteFor(x, y, p.GetVar(x, y));
        // 每格自己决定亮不亮：dim 的格子按背景亮度画，就是游戏里看到的样子
        if (s >= 0) DrawSpriteAt(g, s, dst, p.GetXf(x, y), p.GetDim(x, y) ? alpha * BackdropAlpha : alpha);

        if (alpha < 0.85f || dst.Width < 12) return;

        // 小标记标出"这格挡不挡 / 是不是平台"：阻挡已经和瓦片解耦，光看画面看不出来
        int solid = p.GetSolid(x, y);
        bool marked = solid != -1;
        var kind = p.EffectiveKind(_doc, x, y);

        if (kind == SolidKind.Platform)
        {
            // 平台：沿格子上沿画一条浅蓝横线，跟"实心白点 / 可穿过红点"一眼能分开
            int hLine = Math.Max(2, dst.Height / 8);
            using var br = new SolidBrush(Color.FromArgb(marked ? 225 : 110, 130, 210, 255));
            g.FillRectangle(br, new Rectangle(dst.Left + 1, dst.Top + 1, dst.Width - 2, hLine));
        }
        else if (marked)
        {
            using var br = new SolidBrush(kind == SolidKind.Block
                ? Color.FromArgb(200, 255, 255, 255)
                : Color.FromArgb(200, 255, 110, 110));
            g.FillRectangle(br, new Rectangle(dst.Right - 6, dst.Bottom - 6, 4, 4));
        }
        else if (kind == SolidKind.Block)
        {
            using var br = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            g.FillRectangle(br, new Rectangle(dst.Right - 5, dst.Bottom - 5, 3, 3));
        }
    }

    /// <summary>
    /// 画一个精灵，按 xf 做旋转/镜像——和游戏里 GameWindow.DrawSprite 同一套编码，
    /// 保证编辑器预览和实际运行长得一样。
    /// </summary>
    void DrawSpriteAt(Graphics g, int sprite, Rectangle dst, int xf, float alpha)
    {
        if (_sheet == null) return;
        var src = new Rectangle((sprite % SheetCols) * SourceTile, (sprite / SheetCols) * SourceTile,
            SourceTile, SourceTile);
        if (src.Right > _sheet.Width || src.Bottom > _sheet.Height) return;

        var attrs = AlphaAttr(alpha);
        if (xf == 0)
        {
            g.DrawImage(_sheet, dst, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return;
        }

        var state = g.Save();
        g.TranslateTransform(dst.X + dst.Width / 2f, dst.Y + dst.Height / 2f);
        g.RotateTransform((xf & 3) * 90f);
        if ((xf & 4) != 0) g.ScaleTransform(-1f, 1f);
        var local = new Rectangle(-dst.Width / 2, -dst.Height / 2, dst.Width, dst.Height);
        g.DrawImage(_sheet, local, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        g.Restore(state);
    }

    Point? CellAt(Point local)
    {
        var p = CurPiece;
        if (p == null) return null;
        int x = (local.X - _canvas.AutoScrollPosition.X) / _cell;
        int y = (local.Y - _canvas.AutoScrollPosition.Y) / _cell;
        if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) return null;
        return new Point(x, y);
    }

    void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        var c = CellAt(e.Location);
        if (c == null) return;
        // 画布本身不可聚焦，手动把焦点从数字框上摘掉，
        // 否则 H/V/R 会被 IsTypingInField 拦下、快捷键失灵
        ActiveControl = null;
        _hoverCell = c;
        _lastHoverCell = c;

        // 点哪一格就把它设成"当前格"：事件页那边立刻列出这格挂的事件，
        // 不用先切工具 —— 看事件和改事件是两件事。
        if (CurPiece is PieceDoc clicked) SetEvtCell(clicked, c.Value.X, c.Value.Y);

        // 纯「选中」工具：只改"当前格"（上面已经做过），左右键都不碰数据，
        // 也不开撤销点、不抢选区移动。
        if (_tool == "pick")
        {
            _canvas.Invalidate();   // 让"当前格"高亮跟着手走
            RefreshStatus($"已选中 ({c.Value.X},{c.Value.Y})，右侧列表是这一格挂的事件");
            return;
        }

        bool shift = (ModifierKeys & Keys.Shift) != 0;
        bool left = e.Button == MouseButtons.Left;

        // 框选：选中「框选」工具时左键拖，或任意工具下 Shift+左键拖
        if (left && (_tool == "select" || shift))
        {
            _painting = false;
            _selecting = true;
            _marqueeStart = _marqueeEnd = c;
            _selection = null;
            _canvas.Capture = true;
            _canvas.Invalidate();
            RefreshStatus("拖出框选范围，松手确定");
            return;
        }

        // 选区内左键按下 = 拖拽整体移动（松手才改数据，一次撤销）
        // 出生点 / 事件 / 纯选中这几种点工具例外，它们本来就是单击落点，不该被移动抢走
        bool pointTool = _tool is "spawn" or "evt" or "evtadd" or "noevt" or "pick";
        if (left && !shift && !pointTool && _selection is Rectangle sel && sel.Contains(c.Value))
        {
            _painting = false;
            _moving = true;
            _moveStartCell = c.Value;
            _movePreview = sel;
            _canvas.Capture = true;
            _canvas.Invalidate();
            RefreshStatus("拖动选区到目标位置，松手移动；方向键可逐格微调");
            return;
        }

        // 在非框选工具下点选区外 = 取消选区，然后照常绘制
        if (_selection != null && _tool != "select")
        {
            _selection = null;
            _canvas.Invalidate();
        }

        PushUndo();
        _painting = true;
        _canvas.Capture = true;   // 保证在画布外松手也能收到 MouseUp
        ApplyTool(c.Value.X, c.Value.Y, e.Button == MouseButtons.Right ? "erase" : _tool);
    }

    void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        // 记下光标在哪一格：H/V/R 直接作用到这一格，单格微调不用重画
        var hover = CellAt(e.Location);
        if (hover != _hoverCell) { _hoverCell = hover; RefreshStatus(); }
        if (hover != null) _lastHoverCell = hover;

        if (_selecting)
        {
            if (hover != null) _marqueeEnd = hover;
            _canvas.Invalidate();
            return;
        }

        if (_moving && _selection is Rectangle sel)
        {
            if (hover != null)
            {
                _movePreview = new Rectangle(
                    sel.X + hover.Value.X - _moveStartCell.X,
                    sel.Y + hover.Value.Y - _moveStartCell.Y,
                    sel.Width, sel.Height);
                _canvas.Invalidate();
            }
            return;
        }

        if (!_painting) return;
        if (e.Button == MouseButtons.None) { EndStroke(); return; }   // 丢了按键状态就收笔
        if (hover == null) return;
        ApplyTool(hover.Value.X, hover.Value.Y,
            (e.Button & MouseButtons.Right) != 0 ? "erase" : _tool);
    }

    void Canvas_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_selecting) { FinishMarquee(); return; }
        if (_moving) { FinishMove(); return; }
        EndStroke();
    }

    void EndStroke()
    {
        _painting = false;
        if (_canvas.Capture) _canvas.Capture = false;
    }

    void ReleaseCapture()
    {
        if (_canvas.Capture) _canvas.Capture = false;
    }

    void FinishMarquee()
    {
        _selecting = false;
        ReleaseCapture();
        var p = CurPiece;
        if (_marqueeStart is Point a && _marqueeEnd is Point b && p != null)
        {
            var r = GridOps.FromDrag(a, b);
            _selection = GridOps.ClipToMap(r, p);
            // Shift 触发的框选顺手切到框选工具，松手后立刻能在选区内拖拽移动
            if (_tool != "select") { _tool = "select"; SyncToolRadios(); }
        }
        _marqueeStart = _marqueeEnd = null;
        _canvas.Invalidate();
        RefreshStatus(_selection is Rectangle s
            ? $"已框选 {s.Width}×{s.Height} @({s.X},{s.Y})；选区内拖拽移动，Esc 取消，点画笔继续绘制"
            : null);
    }

    void FinishMove()
    {
        _moving = false;
        ReleaseCapture();
        var src = _selection;
        var dst = _movePreview;
        _movePreview = null;
        var p = CurPiece;

        if (src is Rectangle s && dst is Rectangle d && p != null && (d.X != s.X || d.Y != s.Y))
        {
            PushUndo();
            int written = GridOps.Move(p, s, d.X, d.Y);
            _selection = GridOps.ClipToMap(d, p) ?? d;
            MarkDirty();
            UpdatePieceInfo();
            _canvas.Invalidate();
            _layoutPanel.Invalidate();
            RefreshStatus($"已移动 {written} 格到 ({d.X},{d.Y})");
        }
        else
        {
            _canvas.Invalidate();
            RefreshStatus("未移动（原地松手）");
        }
    }

    void ApplyTool(int x, int y, string tool)
    {
        var p = CurPiece;
        if (p == null) return;
        switch (tool)
        {
            case "paint":
                // 图 + 五个逐格标记一起写进去，游戏里直接照数据画，不再猜。
                // 事件层不跟着画笔走：它由「事件」工具单独刷，画地形不该顺手把事件清掉。
                p.SetCell(x, y, _brush);
                p.SetVar(x, y, _brushVar);
                p.SetXf(x, y, _brushXf);
                p.SetSolid(x, y, _brushSolid);
                p.SetDim(x, y, _brushDim);
                break;
            case "erase":
                p.SetCell(x, y, '.');
                p.SetVar(x, y, -1);
                p.SetXf(x, y, 0);
                p.SetSolid(x, y, -1);
                p.SetDim(x, y, false);
                p.SetEvtSeq(x, y, -1);
                break;
            case "spawn": p.Spawn = new[] { x, y }; break;
            case "evt":
            {
                if (_evtIndex < 0) { RefreshStatus("先在「网格事件」页里选一条事件"); break; }
                if (_doc.SetCellEvents(p, x, y, new[] { _evtIndex }) == -2)
                {
                    RefreshStatus("事件组超过 62 组了，先清理一下再刷");
                    break;
                }
                SetEvtCell(p, x, y);
                break;
            }
            case "evtadd":
            {
                if (_evtIndex < 0) { RefreshStatus("先在「网格事件」页里选一条事件"); break; }
                var seq = _doc.SeqOf(p, x, y);
                if (seq.Contains(_evtIndex))
                {
                    SetEvtCell(p, x, y);      // 已经在这格里了，只是把面板切过去
                    break;
                }
                if (seq.Count >= LevelDoc.MaxEventsPerCell)
                {
                    RefreshStatus($"一格最多挂 {LevelDoc.MaxEventsPerCell} 条事件");
                    break;
                }
                seq.Add(_evtIndex);
                if (_doc.SetCellEvents(p, x, y, seq) == -2)
                {
                    RefreshStatus("事件组超过 62 组了，先清理一下再刷");
                    break;
                }
                SetEvtCell(p, x, y);
                RefreshStatus($"({x},{y}) 现在挂了 {seq.Count} 条事件，顺序见右侧列表");
                break;
            }
            case "noevt":
                p.SetEvtSeq(x, y, -1);
                SetEvtCell(p, x, y);
                break;
            case "pick": return;   // 纯选择：不碰任何数据（正常路径在 MouseDown 里就返回了）
        }
        UpdatePieceInfo();
        MarkDirty();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
    }

    // ---------- 框选 / 剪切复制粘贴 / 批量移动 ----------

    void ResetSelectionState()
    {
        _selection = null;
        _marqueeStart = _marqueeEnd = null;
        _movePreview = null;
        _selecting = _moving = false;
    }

    void ClearSelection()
    {
        ResetSelectionState();
        _canvas.Invalidate();
        RefreshStatus("已取消选区");
    }

    /// <summary>
    /// 粘贴锚点 = 鼠标箭头所在的格。鼠标不在画布上（去点菜单 / 在左侧面板），
    /// 就退回最后一次落在画布上的格；仍没有就用 (0,0)。
    /// </summary>
    Point MouseCellForPaste()
    {
        var local = _canvas.PointToClient(Cursor.Position);
        if (_canvas.ClientRectangle.Contains(local))
        {
            var c = CellAt(local);
            if (c != null) return c.Value;
        }
        var p = CurPiece;
        if (_lastHoverCell is Point last && p != null)
            return new Point(Math.Clamp(last.X, 0, p.Width - 1), Math.Clamp(last.Y, 0, p.Height - 1));
        return new Point(0, 0);
    }

    void CopySelection()
    {
        if (_selection is not Rectangle s || CurPiece is not PieceDoc p)
        {
            RefreshStatus("先用「框选」工具或 Shift+拖拽 框出一块区域");
            return;
        }
        var buf = GridBuffer.Capture(p, s);
        GridClipboard.Data = buf;
        RefreshStatus($"已复制 {buf.Width}×{buf.Height}（五层全带），Ctrl+V 贴到鼠标所在格");
    }

    void CutSelection()
    {
        if (_selection is not Rectangle s || CurPiece is not PieceDoc p)
        {
            RefreshStatus("先框选一块区域再剪切");
            return;
        }
        var buf = GridBuffer.Capture(p, s);
        if (buf.WritableCells(p, s.X, s.Y) == 0)
        {
            RefreshStatus("选区里没有内容，无需剪切");
            return;
        }
        GridClipboard.Data = buf;
        PushUndo();
        GridOps.Clear(p, s);
        ResetSelectionState();
        MarkDirty();
        UpdatePieceInfo();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
        RefreshStatus($"已剪切 {buf.Width}×{buf.Height}（五层全清），Ctrl+V 贴到鼠标所在格");
    }

    /// <summary>给 --uicheck 用：拿到当前地图块，走一遍真实的粘贴命令路径。</summary>
    internal PieceDoc? CurrentPiece => CurPiece;

    /// <summary>给 --uicheck 用：事件表条数 / 事件列表里实际显示的行数 / 当前选中的事件下标。</summary>
    internal int EventCount => _doc.Events.Count;
    internal int EventListRows => _eventList.Items.Count;
    internal int EvtIndex => _evtIndex;

    void PasteAtMouse()
    {
        var anchor = MouseCellForPaste();
        PasteAtGrid(anchor.X, anchor.Y);
    }

    /// <summary>
    /// 把剪贴板贴到 (x,y)，该格即选区左上角。UI 和无界面自检共用同一条命令路径。
    /// </summary>
    internal bool PasteAtGrid(int x, int y)
    {
        var p = CurPiece;
        var buf = GridClipboard.Data;
        if (p == null || buf == null)
        {
            RefreshStatus("剪贴板是空的：先框选，再 Ctrl+C 或 Ctrl+X");
            return false;
        }
        int writable = buf.WritableCells(p, x, y);
        if (writable == 0)
        {
            RefreshStatus($"目标 ({x},{y}) 没有可粘贴的内容（空格透明 / 越界裁剪）");
            return false;
        }

        PushUndo();
        int written = GridOps.Paste(p, buf, x, y);
        _selection = GridOps.ClipToMap(new Rectangle(x, y, buf.Width, buf.Height), p);
        MarkDirty();
        UpdatePieceInfo();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
        RefreshStatus($"已粘贴 {written} 格到 ({x},{y})，该格为选区左上角");
        return true;
    }

    /// <summary>方向键把整块选区挪一格；每次按键一个撤销点。</summary>
    void NudgeSelection(int dx, int dy)
    {
        if (_selection is not Rectangle s || CurPiece is not PieceDoc p) return;
        var d = new Rectangle(s.X + dx, s.Y + dy, s.Width, s.Height);
        if (GridOps.ClipToMap(d, p) == null)
        {
            RefreshStatus("再挪就完全出地图了");
            return;
        }
        PushUndo();
        int written = GridOps.Move(p, s, d.X, d.Y);
        _selection = GridOps.ClipToMap(d, p) ?? d;
        MarkDirty();
        UpdatePieceInfo();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
        RefreshStatus($"选区移动到 ({d.X},{d.Y})，写入 {written} 格");
    }

    /// <summary>选区、拖框、落位预览、以及 Ctrl+V 的落点提示。</summary>
    void DrawSelectionOverlay(Graphics g, PieceDoc p)
    {
        var mapRect = new Rectangle(0, 0, p.Width * _cell, p.Height * _cell);
        var clip = g.Clip;
        g.SetClip(mapRect);
        try
        {
            if (_selecting && _marqueeStart is Point a && _marqueeEnd is Point b)
                Outline(g, GridOps.FromDrag(a, b), Color.FromArgb(130, 200, 255), 2);

            if (_selection is Rectangle sel)
            {
                if (GridOps.ClipToMap(sel, p) is Rectangle rc)
                {
                    using var fill = new SolidBrush(Color.FromArgb(26, 255, 170, 60));
                    g.FillRectangle(fill, rc.X * _cell, rc.Y * _cell, rc.Width * _cell, rc.Height * _cell);
                }
                Outline(g, sel, Color.FromArgb(255, 170, 60), 2);
            }

            if (_moving && _movePreview is Rectangle mp)
                Outline(g, mp, Color.FromArgb(120, 230, 160), 2);
            else if (!_selecting && GridClipboard.Data is GridBuffer cb && cb.Width > 0)
            {
                var anchor = MouseCellForPaste();
                Outline(g, new Rectangle(anchor.X, anchor.Y, cb.Width, cb.Height),
                    Color.FromArgb(140, 150, 200, 255), 1);
            }
        }
        finally { g.Clip = clip; }
    }

    void Outline(Graphics g, Rectangle r, Color color, int width)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var pen = new Pen(color, width) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, r.X * _cell, r.Y * _cell, r.Width * _cell, r.Height * _cell);
    }

    // ---------- 拼接预览 ----------
    /// <summary>默认摆放：横向排开、首尾相接，一进来就能直接看接缝；上一轮不叠在一起。</summary>
    static Point DefaultSlot(int i) =>
        new(24 + (i % 4) * (ViewTilesX * LayoutUnit), 24 + (i / 4) * (ViewTilesY * LayoutUnit + 30));

    void ResetLayout()
    {
        _layout.Clear();
        for (int i = 0; i < _doc.Pieces.Count; i++) _layout.Add(DefaultSlot(i));
    }

    void ResetLayoutIfNeeded()
    {
        while (_layout.Count < _doc.Pieces.Count) _layout.Add(DefaultSlot(_layout.Count));
        while (_layout.Count > _doc.Pieces.Count && _layout.Count > 0)
            _layout.RemoveAt(_layout.Count - 1);
    }

    Rectangle LayoutRect(int i) => new(_layout[i], new Size(ViewTilesX * LayoutUnit, ViewTilesY * LayoutUnit));

    /// <summary>窗口初始取景框左上角对应的地图格——预览必须和游戏里的初始画面一致。</summary>
    static Point CamOrigin(PieceDoc p) => SeamCheck.CamOrigin(p);

    static int Wrap(int v, int n) => SeamCheck.Wrap(v, n);

    char VisibleCell(PieceDoc p, int col, int row) => SeamCheck.VisibleCell(p, col, row);

    void Layout_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        // 和游戏窗口底色 (33,38,63) 对齐，预览里才不会到处都是浅色方块
        g.Clear(Color.FromArgb(33, 38, 63));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        for (int i = 0; i < _doc.Pieces.Count && i < _layout.Count; i++)
        {
            var p = _doc.Pieces[i];
            var rect = LayoutRect(i);
            using (var b = new SolidBrush(Color.FromArgb(26, 30, 40))) g.FillRectangle(b, rect);

            // 预览按取景框逐格取图，但 var/xf 是按地图坐标存的，这里换算一次
            var cam = CamOrigin(p);
            for (int y = 0; y < ViewTilesY; y++)
                for (int x = 0; x < ViewTilesX; x++)
                {
                    char c = VisibleCell(p, x, y);
                    if (IsAir(c)) continue;
                    DrawTile(g, p, c, new Rectangle(rect.X + x * LayoutUnit, rect.Y + y * LayoutUnit,
                        LayoutUnit, LayoutUnit), 1f,
                        Wrap(cam.X + x, p.Width), Wrap(cam.Y + y, p.Height));
                }

            using var pen = new Pen(i == _dragIndex ? Color.Orange : Color.SteelBlue, 2);
            g.DrawRectangle(pen, rect);
            string label = $"{i + 1}. {p.Id}";
            var size = TextRenderer.MeasureText(label, Font);
            using (var chip = new SolidBrush(Color.FromArgb(190, 12, 14, 20)))
                g.FillRectangle(chip, rect.X + 2, rect.Y + 2, size.Width + 4, size.Height);
            TextRenderer.DrawText(g, label, Font, new Point(rect.X + 4, rect.Y + 3), Color.White);
        }

        DrawOverlaps(g);

        for (int i = 0; i < _doc.Pieces.Count && i < _layout.Count; i++)
            for (int j = i + 1; j < _doc.Pieces.Count && j < _layout.Count; j++)
                DrawSeam(g, i, j);
    }

    void DrawOverlaps(Graphics g)
    {
        for (int i = 0; i < _doc.Pieces.Count && i < _layout.Count; i++)
            for (int j = i + 1; j < _doc.Pieces.Count && j < _layout.Count; j++)
            {
                var r = Rectangle.Intersect(LayoutRect(i), LayoutRect(j));
                if (r.Width <= 0 || r.Height <= 0) continue;
                using var hatch = new HatchBrush(HatchStyle.LargeCheckerBoard,
                    Color.FromArgb(70, 255, 90, 90), Color.Transparent);
                g.FillRectangle(hatch, r);
                using var pen = new Pen(Color.FromArgb(160, 255, 110, 110), 1) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(pen, r);
            }
    }

    /// <summary>接缝逐格判定：两侧都不是实心才算这一格走得过去（和引擎里 TransferIfCrossed 的规则一致）。</summary>
    void DrawSeam(Graphics g, int i, int j)
    {
        var rects = _layout.Select((_, k) => LayoutRect(k)).ToList();
        foreach (var seg in SeamCheck.Segments(_doc, rects, i, j, LayoutUnit))
        {
            using var pen = new Pen(seg.Open ? Color.FromArgb(140, 255, 240) : Color.FromArgb(220, 255, 80, 80), 4);
            if (seg.Vertical) g.DrawLine(pen, seg.X, seg.Y, seg.X, seg.Y + seg.Length);
            else g.DrawLine(pen, seg.X, seg.Y, seg.X + seg.Length, seg.Y);
        }
    }

    /// <summary>(贴合处数, 可通行格数, 接缝总格数, 重叠对数)</summary>
    (int seams, int open, int rows, int overlaps) SeamStats()
    {
        var rects = _layout.Select((_, k) => LayoutRect(k)).ToList();
        return SeamCheck.Stats(_doc, rects, LayoutUnit);
    }

    void Layout_MouseDown(object? sender, MouseEventArgs e)
    {
        for (int i = Math.Min(_layout.Count, _doc.Pieces.Count) - 1; i >= 0; i--)
        {
            if (!LayoutRect(i).Contains(e.Location)) continue;
            _dragIndex = i;
            _dragOffset = new Point(e.Location.X - _layout[i].X, e.Location.Y - _layout[i].Y);
            _pieceIndex = i;
            RefreshPieceList();
            _layoutPanel.Invalidate();
            return;
        }
    }

    void Layout_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragIndex < 0) return;
        int x = e.Location.X - _dragOffset.X, y = e.Location.Y - _dragOffset.Y;
        x = (int)(Math.Round(x / (double)LayoutUnit) * LayoutUnit);
        y = (int)(Math.Round(y / (double)LayoutUnit) * LayoutUnit);
        _layout[_dragIndex] = new Point(Math.Max(0, x), Math.Max(0, y));
        _layoutPanel.Invalidate();
        RefreshStatus();
    }

    void Layout_MouseUp(object? sender, MouseEventArgs e)
    {
        _dragIndex = -1;
        _layoutPanel.Invalidate();
        RefreshStatus();
    }

    // ---------- 网格事件 ----------

    /// <summary>
    /// 「网格事件」页。结构对着 Windows 任务计划程序：
    ///   左边 —— 事件表 + 当前选中格子挂了哪些事件（顺序即判定顺序）
    ///   右边 —— 选中事件的名称 / 触发器 / 动作（可多条，按顺序执行）/ 条件
    /// 事件不绑定瓦片，画布上靠「事件」「加事件」工具挂到格子上（PieceDoc.Evt 层）。
    /// </summary>
    TabPage BuildEventsPage()
    {
        var page = new TabPage("网格事件");

        // ---- 左：事件表 + 这一格的事件 ----
        var left = new Panel { Dock = DockStyle.Left, Width = 340 };
        Lbl(left, "事件表（下标 = 事件层里的字符）", 10, 8, 320);

        _eventList.SetBounds(10, 30, 320, 250);
        _eventList.DrawMode = DrawMode.OwnerDrawFixed;
        _eventList.ItemHeight = 22;
        _eventList.IntegralHeight = false;
        _eventList.DrawItem += EventList_DrawItem;
        _eventList.SelectedIndexChanged += (_, _) =>
        {
            if (_evtSyncing) return;
            SyncEventEditor();
            RefreshStatus();
        };
        left.Controls.Add(_eventList);

        left.Controls.Add(Btn("新建事件", 10, 288, 102, 28, (_, _) => AddEvent()));
        left.Controls.Add(Btn("复制", 116, 288, 102, 28, (_, _) => DuplicateEvent()));
        left.Controls.Add(Btn("删除", 222, 288, 108, 28, (_, _) => RemoveEvent()));

        Lbl(left, "这一格挂的事件（顺序即判定顺序）", 10, 326, 320);

        _cellEvtLabel.SetBounds(10, 346, 320, 18);
        _cellEvtLabel.ForeColor = SystemColors.GrayText;
        _cellEvtLabel.Text = "在地图上点一个格子";
        left.Controls.Add(_cellEvtLabel);

        _cellEvtList.SetBounds(10, 368, 320, 116);
        _cellEvtList.IntegralHeight = false;
        _cellEvtList.DrawMode = DrawMode.OwnerDrawFixed;
        _cellEvtList.ItemHeight = 20;
        _cellEvtList.DrawItem += CellEvtList_DrawItem;
        left.Controls.Add(_cellEvtList);

        left.Controls.Add(Btn("上移", 10, 490, 102, 26, (_, _) => MoveCellEvent(-1)));
        left.Controls.Add(Btn("下移", 116, 490, 102, 26, (_, _) => MoveCellEvent(1)));
        left.Controls.Add(Btn("删单条", 222, 490, 108, 26, (_, _) => RemoveCellEvent()));
        left.Controls.Add(Btn("清空这一格", 10, 522, 320, 26, (_, _) => ClearCellEvents()));

        left.Controls.Add(new Label
        {
            Left = 10, Top = 556, Width = 320, Height = 168,
            ForeColor = SystemColors.GrayText,
            Text = "画布上点格子（任何工具都行）就会在这里显示它挂的事件。\r\n\r\n"
                 + "「事件」= 把这格设成选中的那一条；\r\n"
                 + "「加事件」= 往这格再追加一条；「擦事件」= 清空。\r\n\r\n"
                 + "事件层跟着框选复制 / 移动 / 粘贴一起走。\r\n"
                 + "下标 0-9 / a-z / A-Z = 事件组 0-61。",
        });

        // ---- 右：选中事件的属性 ----
        var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        Lbl(right, "事件名称（只影响编辑器显示）", 14, 12, 340);
        Put(right, _evtName, 14, 32, 340, 24);
        _evtName.TextChanged += (_, _) =>
        {
            if (_evtSyncing || CurEvent is not EventDoc ev) return;
            ev.Name = _evtName.Text;
            MarkDirty();
            _eventList.Invalidate();
        };

        Lbl(right, "触发器（什么时候判）", 14, 66, 200);
        _evtTrigger.DropDownStyle = ComboBoxStyle.DropDownList;
        _evtTrigger.Items.AddRange(new object[] { "触碰（角色进入该格）" });
        _evtTrigger.SelectedIndex = 0;
        Put(right, _evtTrigger, 14, 86, 200, 24);

        // ---- 动作（任务计划程序里的"操作"）----
        var actBox = new GroupBox
        {
            Text = "动作（按顺序执行；地图一被切走，后面的就不再执行）",
            Left = 14, Top = 120, Width = 470, Height = 256,
        };

        _actList.SetBounds(12, 22, 446, 100);
        _actList.IntegralHeight = false;
        _actList.DrawMode = DrawMode.OwnerDrawFixed;
        _actList.ItemHeight = 20;
        _actList.DrawItem += ActList_DrawItem;
        _actList.SelectedIndexChanged += (_, _) => { if (!_evtSyncing) SyncActEditor(); };
        actBox.Controls.Add(_actList);

        actBox.Controls.Add(new Label { Text = "类型", Left = 12, Top = 136, Width = 34, Height = 20 });
        _actType.DropDownStyle = ComboBoxStyle.DropDownList;
        _actType.Items.AddRange(new object[] { "跳转到指定关卡", "执行命令 / 打开链接" });
        _actType.SetBounds(50, 132, 150, 24);
        _actType.SelectedIndex = 0;
        _actType.SelectedIndexChanged += (_, _) => { if (!_evtSyncing) SyncActTargetHints(); };
        actBox.Controls.Add(_actType);

        actBox.Controls.Add(new Label { Text = "目标", Left = 208, Top = 136, Width = 34, Height = 20 });
        _actTarget.DropDownStyle = ComboBoxStyle.DropDown;     // 关卡从下拉选，命令/网址直接打
        _actTarget.SetBounds(246, 132, 212, 24);
        actBox.Controls.Add(_actTarget);

        actBox.Controls.Add(new Label { Text = "参数", Left = 12, Top = 166, Width = 34, Height = 20 });
        _actArgs.SetBounds(50, 162, 408, 24);
        actBox.Controls.Add(_actArgs);

        actBox.Controls.Add(new Label
        {
            Left = 12, Top = 190, Width = 446, Height = 18,
            ForeColor = SystemColors.GrayText,
            Text = "跳转关卡：从下拉里选；执行命令：填程序 / 网址 / 文件，参数只对程序有用。",
        });

        actBox.Controls.Add(Btn("添加动作", 12, 214, 96, 26, (_, _) => AddAction()));
        actBox.Controls.Add(Btn("改选中", 112, 214, 88, 26, (_, _) => UpdateAction()));
        actBox.Controls.Add(Btn("删除", 204, 214, 70, 26, (_, _) => RemoveAction()));
        actBox.Controls.Add(Btn("上移", 300, 214, 68, 26, (_, _) => MoveAction(-1)));
        actBox.Controls.Add(Btn("下移", 372, 214, 68, 26, (_, _) => MoveAction(1)));

        right.Controls.Add(actBox);

        // ---- 条件 ----
        var condBox = new GroupBox
        {
            Text = "条件：摄像机滚动（取景框左上角，单位格）",
            Left = 14, Top = 386, Width = 470, Height = 356,
        };

        _condList.SetBounds(12, 22, 446, 140);
        _condList.IntegralHeight = false;
        _condList.SelectedIndexChanged += (_, _) => { if (!_evtSyncing) SyncCondEditor(); };
        condBox.Controls.Add(_condList);

        condBox.Controls.Add(Btn("添加条件", 12, 168, 96, 26, (_, _) => AddCondition()));
        condBox.Controls.Add(Btn("改选中条件", 112, 168, 108, 26, (_, _) => UpdateCondition()));
        condBox.Controls.Add(Btn("删除条件", 224, 168, 96, 26, (_, _) => RemoveCondition()));

        condBox.Controls.Add(new Label { Text = "地图块", Left = 12, Top = 206, Width = 60, Height = 20 });
        _condPiece.DropDownStyle = ComboBoxStyle.DropDownList;
        _condPiece.SetBounds(76, 202, 240, 24);
        condBox.Controls.Add(_condPiece);

        foreach (var n in new[] { _camXMin, _camXMax, _camYMin, _camYMax })
        {
            n.Minimum = -999;
            n.Maximum = 999;
            n.DecimalPlaces = 1;
            n.Increment = 1;
        }

        _camXOn.Text = "X 滚动";
        _camXOn.SetBounds(12, 234, 76, 24);
        condBox.Controls.Add(_camXOn);
        _camXMin.SetBounds(92, 234, 76, 24);
        condBox.Controls.Add(_camXMin);
        condBox.Controls.Add(new Label { Text = "~", Left = 172, Top = 238, Width = 16, Height = 20 });
        _camXMax.SetBounds(190, 234, 76, 24);
        condBox.Controls.Add(_camXMax);

        _camYOn.Text = "Y 滚动";
        _camYOn.SetBounds(12, 264, 76, 24);
        condBox.Controls.Add(_camYOn);
        _camYMin.SetBounds(92, 264, 76, 24);
        condBox.Controls.Add(_camYMin);
        condBox.Controls.Add(new Label { Text = "~", Left = 172, Top = 268, Width = 16, Height = 20 });
        _camYMax.SetBounds(190, 264, 76, 24);
        condBox.Controls.Add(_camYMax);

        condBox.Controls.Add(new Label
        {
            Left = 12, Top = 296, Width = 446, Height = 52,
            ForeColor = SystemColors.GrayText,
            Text = "不勾某一轴 = 那一轴不限制。地图块选「*」= 每个开着的窗口都要满足。\r\n"
                 + "区间按环形地图折算：从 min 顺着正方向数，不超过 (max-min) 格都算命中。\r\n"
                 + "判定用连续滚动值：静止时它就是取景框左上角那格，滚动过渡中途可能暂时不满足。",
        });

        right.Controls.Add(condBox);

        page.Controls.Add(right);
        page.Controls.Add(left);
        return page;
    }

    EventDoc? CurEvent =>
        _evtIndex >= 0 && _evtIndex < _doc.Events.Count ? _doc.Events[_evtIndex] : null;

    /// <summary>事件表的选中状态与数据重建后，把列表和右边编辑区同步过来。</summary>
    void RefreshEventList()
    {
        _evtSyncing = true;
        try
        {
            _eventList.BeginUpdate();
            _eventList.Items.Clear();
            foreach (var _ in _doc.Events) _eventList.Items.Add("");
            if (_doc.Events.Count == 0) _evtIndex = -1;
            else _evtIndex = Math.Clamp(_evtIndex, 0, _doc.Events.Count - 1);
            _eventList.SelectedIndex = _evtIndex;
            _eventList.EndUpdate();
        }
        finally { _evtSyncing = false; }

        SyncEventEditor();
        _eventList.Invalidate();
    }

    string EventLine(int i)
    {
        if (i < 0 || i >= _doc.Events.Count) return "";
        var ev = _doc.Events[i];
        string name = string.IsNullOrWhiteSpace(ev.Name) ? "(未命名)" : ev.Name;
        return $"#{i}  {name}  ·  {ev.Describe()}";
    }

    void EventList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(selected
            ? Color.FromArgb(46, 78, 124)
            : Color.FromArgb(30, 33, 42)))
            e.Graphics.FillRectangle(b, e.Bounds);

        var ev = e.Index < _doc.Events.Count ? _doc.Events[e.Index] : null;
        Color fg = ev == null ? Color.Gray
            : ev.HasUnsetAction ? Color.FromArgb(255, 120, 120)
            : Color.FromArgb(222, 228, 238);

        TextRenderer.DrawText(e.Graphics, EventLine(e.Index), e.Font ?? Font, e.Bounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    /// <summary>把选中事件同步到右边控件；没有选中事件时整片禁用。</summary>
    void SyncEventEditor()
    {
        _evtSyncing = true;
        try
        {
            var ev = CurEvent;
            bool on = ev != null;
            foreach (var c in new Control[] { _evtName, _evtTrigger, _actList, _actType, _actTarget, _actArgs })
                c.Enabled = on;

            _evtName.Text = ev?.Name ?? "";
            _evtTrigger.SelectedIndex = 0;                                  // 目前只有触碰一种

            RefreshActList(ev);
            RefreshCondList(ev);
        }
        finally { _evtSyncing = false; }

        SyncActEditor();
        SyncCondEditor();
    }

    // ---------- 动作列表 ----------

    void RefreshActList(EventDoc? ev)
    {
        _actList.BeginUpdate();
        _actList.Items.Clear();
        if (ev?.Actions != null)
            foreach (var _ in ev.Actions) _actList.Items.Add("");
        _actList.EndUpdate();
        if (_actList.Items.Count > 0) _actList.SelectedIndex = 0;
    }

    void ActList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(selected ? Color.FromArgb(46, 78, 124) : Color.FromArgb(32, 35, 44)))
            e.Graphics.FillRectangle(b, e.Bounds);

        var acts = CurEvent?.Actions;
        var act = acts != null && e.Index < acts.Count ? acts[e.Index] : null;
        Color fg = act == null ? Color.Gray
            : act.IsUnset ? Color.FromArgb(255, 120, 120)
            : Color.FromArgb(222, 228, 238);

        string text = act == null
            ? ""
            : $"{e.Index + 1}. {act.Describe()}" +
              (act.Type == "run" && !string.IsNullOrWhiteSpace(act.Args) ? $"  ({act.Args})" : "");
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font, e.Bounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    /// <summary>选中某条动作时把它的值读进编辑区。</summary>
    void SyncActEditor()
    {
        _evtSyncing = true;
        try
        {
            int i = _actList.SelectedIndex;
            var acts = CurEvent?.Actions;
            var act = acts != null && i >= 0 && i < acts.Count ? acts[i] : null;
            bool on = act != null;

            _actType.Enabled = _actTarget.Enabled = _actArgs.Enabled = on;
            _actType.SelectedIndex = act == null ? 1 : act.Type == "goto" ? 0 : 1;
            SyncActTargetHints();

            _actTarget.Text = act == null ? ""
                : act.Type == "goto" ? act.Level : act.Target;
            _actArgs.Text = act?.Args ?? "";
        }
        finally { _evtSyncing = false; }
    }

    /// <summary>目标那个下拉：跳关时把关卡名填成候选，执行命令时清空（直接手打路径/网址）。</summary>
    void SyncActTargetHints()
    {
        string keep = _actTarget.Text;
        _actTarget.BeginUpdate();
        _actTarget.Items.Clear();
        if (_actType.SelectedIndex == 0)
            foreach (string n in LevelStore.LevelNames()) _actTarget.Items.Add(n);
        _actTarget.Text = keep;
        _actTarget.EndUpdate();
    }

    /// <summary>把动作编辑区读成一条动作。</summary>
    ActionDoc ReadActFromUi()
    {
        string target = _actTarget.Text.Trim();
        return _actType.SelectedIndex == 0
            ? new ActionDoc { Type = "goto", Level = target }
            : new ActionDoc { Type = "run", Target = target, Args = _actArgs.Text.Trim() };
    }

    void AddAction()
    {
        if (CurEvent is not EventDoc ev) return;
        var act = ReadActFromUi();

        PushUndo();
        ev.Actions ??= new List<ActionDoc>();
        ev.Actions.Add(act);
        MarkDirty();
        RefreshActList(ev);
        _actList.SelectedIndex = ev.Actions.Count - 1;
        _eventList.Invalidate();
        RefreshStatus("已添加动作：" + act.Describe());
    }

    void UpdateAction()
    {
        var ev = CurEvent;
        var acts = ev?.Actions;
        int i = _actList.SelectedIndex;
        if (ev == null || acts == null || i < 0 || i >= acts.Count) { RefreshStatus("先在上面选中一条动作"); return; }

        var act = ReadActFromUi();
        PushUndo();
        acts[i] = act;
        MarkDirty();
        RefreshActList(ev);
        _actList.SelectedIndex = i;
        _eventList.Invalidate();
        RefreshStatus("已更新动作：" + act.Describe());
    }

    void RemoveAction()
    {
        var ev = CurEvent;
        var acts = ev?.Actions;
        int i = _actList.SelectedIndex;
        if (ev == null || acts == null || i < 0 || i >= acts.Count) { RefreshStatus("先在上面选中一条动作"); return; }

        PushUndo();
        acts.RemoveAt(i);
        MarkDirty();
        RefreshActList(ev);
        _eventList.Invalidate();
        RefreshStatus("已删除动作");
    }

    void MoveAction(int dir)
    {
        var ev = CurEvent;
        var acts = ev?.Actions;
        int i = _actList.SelectedIndex;
        if (ev == null || acts == null || i < 0 || i >= acts.Count) return;
        int j = i + dir;
        if (j < 0 || j >= acts.Count) return;

        PushUndo();
        (acts[i], acts[j]) = (acts[j], acts[i]);
        MarkDirty();
        RefreshActList(ev);
        _actList.SelectedIndex = j;
        _eventList.Invalidate();
        RefreshStatus("已调整动作顺序（执行顺序从上到下）");
    }

    // ---------- 条件列表 ----------

    void RefreshCondList(EventDoc? ev)
    {
        _condList.BeginUpdate();
        _condList.Items.Clear();
        if (ev?.Conditions != null)
            foreach (var c in ev.Conditions) _condList.Items.Add(c.Describe());
        _condList.EndUpdate();
        if (_condList.Items.Count > 0) _condList.SelectedIndex = 0;
    }

    /// <summary>条件编辑区：选中某条条件时把它的值读进控件。</summary>
    void SyncCondEditor()
    {
        _evtSyncing = true;
        try
        {
            var ev = CurEvent;
            int i = _condList.SelectedIndex;
            var c = ev?.Conditions != null && i >= 0 && i < ev.Conditions.Count ? ev.Conditions[i] : null;

            _condPiece.Items.Clear();
            _condPiece.Items.Add("*（所有开着的窗口）");
            foreach (var p in _doc.Pieces) _condPiece.Items.Add(p.Id);
            int at = 0;
            if (c != null && c.Piece != "*")
            {
                int found = _condPiece.Items.IndexOf(c.Piece);
                at = found >= 0 ? found : 0;
            }
            _condPiece.SelectedIndex = at;

            _camXOn.Checked = c?.CamX != null;
            _camXMin.Value = ClampNum(c?.CamX is { Length: >= 2 } cx ? cx[0] : 0f);
            _camXMax.Value = ClampNum(c?.CamX is { Length: >= 2 } cx2 ? cx2[1] : 30f);
            _camYOn.Checked = c?.CamY != null;
            _camYMin.Value = ClampNum(c?.CamY is { Length: >= 2 } cy ? cy[0] : 0f);
            _camYMax.Value = ClampNum(c?.CamY is { Length: >= 2 } cy2 ? cy2[1] : 20f);
        }
        finally { _evtSyncing = false; }
    }

    static decimal ClampNum(float v) => (decimal)Math.Clamp(v, -999f, 999f);

    /// <summary>把条件编辑区当前的值读成一条条件；两轴都没勾时返回 null。</summary>
    ConditionDoc? ReadCondFromUi()
    {
        var c = new ConditionDoc
        {
            Piece = _condPiece.SelectedIndex <= 0
                ? "*"
                : _condPiece.SelectedItem as string ?? "*",
        };
        if (_camXOn.Checked) c.CamX = new[] { (float)_camXMin.Value, (float)_camXMax.Value };
        if (_camYOn.Checked) c.CamY = new[] { (float)_camYMin.Value, (float)_camYMax.Value };
        return c.CamX == null && c.CamY == null ? null : c;
    }

    void AddCondition()
    {
        if (CurEvent is not EventDoc ev) return;
        var c = ReadCondFromUi();
        if (c == null) { RefreshStatus("条件至少要勾一个轴的区间"); return; }

        PushUndo();
        ev.Conditions ??= new List<ConditionDoc>();
        ev.Conditions.Add(c);
        MarkDirty();
        RefreshCondList(ev);
        _condList.SelectedIndex = ev.Conditions.Count - 1;
        _eventList.Invalidate();
        RefreshStatus("已添加条件：" + c.Describe());
    }

    void UpdateCondition()
    {
        var ev = CurEvent;
        int i = _condList.SelectedIndex;
        if (ev?.Conditions == null || i < 0 || i >= ev.Conditions.Count)
        {
            RefreshStatus("先在上面选中一条条件");
            return;
        }

        var c = ReadCondFromUi();
        if (c == null) { RefreshStatus("条件至少要勾一个轴的区间"); return; }

        PushUndo();
        ev.Conditions[i] = c;
        MarkDirty();
        RefreshCondList(ev);
        _condList.SelectedIndex = i;
        _eventList.Invalidate();
        RefreshStatus("已更新条件：" + c.Describe());
    }

    void RemoveCondition()
    {
        var ev = CurEvent;
        int i = _condList.SelectedIndex;
        if (ev?.Conditions == null || i < 0 || i >= ev.Conditions.Count)
        {
            RefreshStatus("先在上面选中一条条件");
            return;
        }

        PushUndo();
        ev.Conditions.RemoveAt(i);
        MarkDirty();
        RefreshCondList(ev);
        _eventList.Invalidate();
        RefreshStatus("已删除条件");
    }

    // ---------- 事件表增删 ----------

    void AddEvent()
    {
        PushUndo();
        _doc.Events.Add(new EventDoc
        {
            Name = $"事件 {_doc.Events.Count}",
            Trigger = "touch",
            Actions = new List<ActionDoc> { new() { Type = "goto", Level = "" } },
        });
        _evtIndex = _doc.Events.Count - 1;
        MarkDirty();
        RefreshEventList();
        RefreshStatus($"新建事件 #{_evtIndex}，去右边填目标和条件，再用「事件」工具刷到格子上");
    }

    void DuplicateEvent()
    {
        if (CurEvent is not EventDoc src) return;
        PushUndo();
        _doc.Events.Add(src.Clone());
        _evtIndex = _doc.Events.Count - 1;
        MarkDirty();
        RefreshEventList();
        RefreshStatus($"已复制为事件 #{_evtIndex}（格子上的引用不会自动跟着变）");
    }

    /// <summary>
    /// 删除一条事件。事件组里对它的引用、以及大于它的下标都要重排（LevelDoc.RemoveEvent），
    /// 否则后面所有格子和条件都会指错事件。
    /// </summary>
    void RemoveEvent()
    {
        if (CurEvent == null) return;
        int gone = _evtIndex;

        PushUndo();
        _doc.RemoveEvent(gone);

        _evtIndex = _doc.Events.Count == 0 ? -1 : Math.Clamp(gone, 0, _doc.Events.Count - 1);
        MarkDirty();
        RefreshEventList();
        RefreshCellEvents();
        _canvas.Invalidate();
        RefreshStatus($"已删除事件 #{gone}，格子和条件里的引用已经重排");
    }

    // ---------- 这一格的事件 ----------

    /// <summary>把某个格子设为"当前格"，事件页的列表跟着显示它挂的事件。</summary>
    void SetEvtCell(PieceDoc p, int x, int y)
    {
        _evtCell = new Point(x, y);
        _evtCellPiece = _pieceIndex;
        RefreshCellEvents();
    }

    void RefreshCellEvents()
    {
        var p = CurPiece;
        var cell = _evtCell;
        bool valid = p != null && cell is Point c && _evtCellPiece == _pieceIndex &&
                     c.X >= 0 && c.Y >= 0 && c.X < p.Width && c.Y < p.Height;

        _cellEvtList.BeginUpdate();
        _cellEvtList.Items.Clear();
        if (!valid)
        {
            _cellEvtList.EndUpdate();
            _cellEvtLabel.Text = "在地图上点一个格子";
            return;
        }

        var pt = cell!.Value;
        var seq = _doc.SeqOf(p!, pt.X, pt.Y);
        _cellEvtLabel.Text = $"{p!.Id} · ({pt.X},{pt.Y}) · 共 {seq.Count} 条";
        foreach (var _ in seq) _cellEvtList.Items.Add("");
        if (seq.Count > 0) _cellEvtList.SelectedIndex = 0;
        _cellEvtList.EndUpdate();
        _cellEvtList.Invalidate();
    }

    void CellEvtList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(selected ? Color.FromArgb(46, 78, 124) : Color.FromArgb(30, 33, 42)))
            e.Graphics.FillRectangle(b, e.Bounds);

        var seq = CurCellSeq();
        int idx = seq != null && e.Index < seq.Count ? seq[e.Index] : -1;
        var ev = idx >= 0 && idx < _doc.Events.Count ? _doc.Events[idx] : null;

        Color fg = ev == null ? Color.FromArgb(255, 110, 230)
            : ev.HasUnsetAction ? Color.FromArgb(255, 120, 120)
            : Color.FromArgb(222, 228, 238);

        TextRenderer.DrawText(e.Graphics, $"{e.Index + 1}. {EventLine(idx)}", e.Font ?? Font, e.Bounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    /// <summary>当前选中格子挂的事件（副本）；没有选中格子时返回 null。</summary>
    List<int>? CurCellSeq()
    {
        var p = CurPiece;
        if (p == null || _evtCell is not Point c || _evtCellPiece != _pieceIndex) return null;
        if (c.X < 0 || c.Y < 0 || c.X >= p.Width || c.Y >= p.Height) return null;
        return _doc.SeqOf(p, c.X, c.Y);
    }

    /// <summary>把改好的序列写回当前格，并刷新面板与画布。</summary>
    void ApplyCellSeq(List<int> seq)
    {
        var p = CurPiece;
        if (p == null || _evtCell is not Point c) return;
        if (_doc.SetCellEvents(p, c.X, c.Y, seq) == -2)
        {
            RefreshStatus("事件组超过 62 组了，先清理一下");
            return;
        }
        MarkDirty();
        RefreshCellEvents();
        UpdatePieceInfo();
        _canvas.Invalidate();
        _layoutPanel.Invalidate();
    }

    void MoveCellEvent(int dir)
    {
        var seq = CurCellSeq();
        int i = _cellEvtList.SelectedIndex;
        if (seq == null || i < 0 || i >= seq.Count) return;
        int j = i + dir;
        if (j < 0 || j >= seq.Count) return;

        PushUndo();
        (seq[i], seq[j]) = (seq[j], seq[i]);
        ApplyCellSeq(seq);
        _cellEvtList.SelectedIndex = j;
        RefreshStatus("已调整这一格的事件顺序（判定顺序从上到下）");
    }

    void RemoveCellEvent()
    {
        var seq = CurCellSeq();
        int i = _cellEvtList.SelectedIndex;
        if (seq == null || i < 0 || i >= seq.Count) return;

        PushUndo();
        seq.RemoveAt(i);
        ApplyCellSeq(seq);
        _doc.CompactEvt();
        RefreshCellEvents();
        RefreshStatus("已从这一格删掉一条事件");
    }

    void ClearCellEvents()
    {
        if (CurCellSeq() == null) return;
        PushUndo();
        ApplyCellSeq(new List<int>());
        _doc.CompactEvt();
        RefreshCellEvents();
        RefreshStatus("已清空这一格的事件");
    }
}

/// <summary>
/// 滚轮消息按光标位置路由：Windows 默认把 WM_MOUSEWHEEL 发给焦点控件，
/// 而画布（Panel）拿不到焦点，鼠标悬在画布上滚动会去改左边的数字框。这里统一截下来。
/// </summary>
internal sealed class WheelRouter : IMessageFilter
{
    const int WM_MOUSEWHEEL = 0x020A;
    readonly MainForm _form;

    public WheelRouter(MainForm form) => _form = form;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
        return _form.HandleCanvasWheel(delta);
    }
}
