// Error reports: a text file in the settings folder's "reports" folder, written when Ember hits an unexpected error or when the
// user asks for one. Nothing is sent anywhere; the user reads it and decides whether to share it.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace ImageForge;

static class ErrorReport
{
    public static string Folder => Path.Combine(Path.GetDirectoryName(Settings.FilePath), "reports");

    // the three ways an error can escape: on the window's thread (Ember keeps running), on any other thread (the app is ending),
    // and in a background task nobody waited for
    public static void Watch(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            var file = Write("An unexpected error (Ember kept running)", e.Exception, null);
            e.Handled = true;
            Offer($"Ember ran into an unexpected problem and carried on. If something now looks wrong, restart Ember.\n\nA report was saved:\n{file}\n\nOpen its folder?", file);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("An unexpected error that closed Ember", e.ExceptionObject as Exception, null);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("An unexpected error in background work (Ember kept running)", e.Exception, null); e.SetObserved(); };
    }

    // what happened, this PC, and the end of ComfyUI's log; returns the file, or null if it couldn't be written
    public static string Write(string what, Exception error, string comfyLog)
    {
        try
        {
            var r = new StringBuilder();
            r.AppendLine($"Ember error report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            r.AppendLine(what);
            r.AppendLine($"Ember {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version}, {Environment.OSVersion}, .NET {Environment.Version}");
            r.AppendLine($"Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB; graphics: {Graphics()}");
            if (error != null) r.AppendLine().AppendLine("Error:").AppendLine(error.ToString());
            if (comfyLog != null && File.Exists(comfyLog))
            {
                // the log is held open by ComfyUI: read it shared
                using var s = new FileStream(comfyLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var lines = new StreamReader(s).ReadToEnd().Split('\n');
                r.AppendLine().AppendLine($"The last {Math.Min(200, lines.Length)} lines of {Path.GetFileName(comfyLog)}:");
                r.AppendJoin('\n', lines.TakeLast(200));
            }
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"ember-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, r.ToString());
            return file;
        }
        catch { return null; }
    }

    static string Graphics()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            var gpu = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return gpu == "" ? "unknown" : gpu;
        }
        catch { return "no NVIDIA card found"; }
    }

    // --selftest part (it runs with IMAGEFORGE_SETTINGS on a probe folder): a report is written, holds the error, and is cleaned up
    public static bool SelfTest()
    {
        var file = Write("self-test", new InvalidOperationException("self-test error 4711"), null);
        try { return file != null && File.ReadAllText(file).Contains("self-test error 4711"); }
        finally { try { File.Delete(file); } catch { } }
    }

    public static void Offer(string message, string file)
    {
        if (file == null) return;
        if (MessageBox.Show(message, "Ember", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Process.Start("explorer.exe", $"/select,\"{file}\"");
    }
}
