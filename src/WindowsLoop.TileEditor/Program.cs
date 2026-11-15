using System.Windows.Forms;

namespace WindowsLoop.TileEditor;

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
            string report;
            using (var f = new MainForm()) report = f.LayoutReport();
            string outPath = Path.Combine(Path.GetTempPath(), "windowsloop-tileeditor-uicheck.txt");
            File.WriteAllText(outPath, report + Environment.NewLine);
            Console.WriteLine(report);
            Environment.ExitCode = report.StartsWith("OK") ? 0 : 1;
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
