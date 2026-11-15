using System.Windows.Forms;

namespace WindowsLoop.LevelEditor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            SelfTest.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--uicheck")
        {
            RunUiCheck();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string? open = null;
        int tab = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tab" && i + 1 < args.Length && int.TryParse(args[i + 1], out int t)) tab = t;
            else if (File.Exists(args[i])) open = args[i];
        }
        Application.Run(new MainForm(open, tab));
    }

    /// <summary>
    /// 不弹窗的界面自检：构造完整 MainForm（会跑 BuildUi / NewDocument / RefreshAll），
    /// 再走一遍真实的粘贴命令路径，确认框选相关的 UI 状态与数据操作都没问题。
    /// </summary>
    static void RunUiCheck()
    {
        var lines = new List<string>();
        int fail = 0;
        try
        {
            using var f = new MainForm();
            lines.Add($"client={f.ClientSize.Width}x{f.ClientSize.Height}");

            var p = f.CurrentPiece;
            lines.Add($"piece={(p == null ? "none" : p.Width + "x" + p.Height)}");
            if (p == null) { fail++; }
            else
            {
                var cell = new GridCell('g', 2, 5, 1, true, 0);
                GridOps.Write(p, 1, 1, cell);
                GridClipboard.Data = GridBuffer.Capture(p, new Rectangle(1, 1, 1, 1));

                bool pasted = f.PasteAtGrid(3, 2);
                bool sixLayers = GridOps.Read(p, 3, 2) == cell;
                bool sourceKept = GridOps.Read(p, 1, 1) == cell;   // 粘贴不剪切，源还在
                lines.Add($"paste={pasted} sixLayers={sixLayers} sourceKept={sourceKept}");
                if (!pasted || !sixLayers || !sourceKept) fail++;

                // 越界裁剪：贴到地图外应安全返回，且不改尺寸
                int w = p.Width, h = p.Height;
                bool outOfRange = !f.PasteAtGrid(w + 5, h + 5);
                lines.Add($"outOfRangeRejected={outOfRange} sizeKept={p.Width == w && p.Height == h}");
                if (!outOfRange || p.Width != w || p.Height != h) fail++;
            }

            // 打开一张带事件的关卡：事件表要进到列表里、并且默认选中第一条（事件工具靠它刷格子）
            string levelPath = Path.Combine(LevelStore.LevelsDir, "main.json");
            if (File.Exists(levelPath))
            {
                using var g = new MainForm(levelPath);
                lines.Add($"events={g.EventCount} rows={g.EventListRows} selected={g.EvtIndex}");
                if (g.EventCount < 1 || g.EventListRows != g.EventCount || g.EvtIndex != 0) fail++;
            }
            else lines.Add("main.json missing, skip event ui check");
        }
        catch (Exception ex)
        {
            lines.Add($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            fail++;
        }

        lines.Add($"result: {(fail == 0 ? "ALL PASS" : fail + " FAILED")}");
        string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-editor-uicheck.txt");
        File.WriteAllLines(outPath, lines);
        Console.WriteLine(outPath);
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }
}
