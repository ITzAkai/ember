// First run without ComfyUI: point at a ComfyUI portable folder already on the PC, or download the official one and unpack it.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace ImageForge;

public partial class MainWindow
{
    static bool IsComfy(string dir) => !string.IsNullOrEmpty(dir)
        && File.Exists(Path.Combine(dir, "python_embeded", "python.exe")) && File.Exists(Path.Combine(dir, "ComfyUI", "main.py"));

    // the folder itself, or the ComfyUI_windows_portable folder the download unpacks inside it
    static string FindComfy(string dir) => IsComfy(dir) ? dir : Directory.Exists(dir) ? Directory.GetDirectories(dir).FirstOrDefault(IsComfy) : null;

    async void SetUpComfy_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick your ComfyUI folder, or any folder to download ComfyUI into (needs about 12 GB)" };
        if (dlg.ShowDialog() != true) return;
        var dir = dlg.FolderName;
        if (FindComfy(dir) is not string comfy)
        {
            var free = new DriveInfo(Path.GetPathRoot(dir)).AvailableFreeSpace;
            if (free < 12L << 30) { Note($"Only {Size(free)} free on that drive: ComfyUI needs about 12 GB. Pick another one.", true); return; }
            if (NvidiaBuild() is not string build) { Note("Ember needs an NVIDIA graphics card, and none was found.", true); return; }
            SetupPanel.Visibility = Visibility.Collapsed;
            SetState("Setting up", Brush("Accent"));
            try { comfy = await DownloadComfy(build, dir); }
            catch (Exception ex)
            {
                SetState("Not set up", Brush("Danger"));
                Note($"Setting up ComfyUI failed: {ex.Message}", true);
                SetupPanel.Visibility = Visibility.Visible;
                return;
            }
        }
        Root = comfy.TrimEnd('\\') + "\\";
        settings.ComfyDir = Root;
        settings.Save();
        SetupPanel.Visibility = Visibility.Collapsed;
        SetState("Starting", Brush("Accent"));
        _ = LoadGallery();
        await StartUp();
    }

    async Task<string> DownloadComfy(string build, string dir)
    {
        var file = Path.Combine(dir, build + ".7z");
        if (!File.Exists(file))         // kept after a failed unpack, so a second try doesn't download it again
        {
            var clock = Stopwatch.StartNew();
            var shown = TimeSpan.Zero;
            await DownloadFile($"https://github.com/Comfy-Org/ComfyUI/releases/latest/download/{build}.7z", file, (done, size) =>
            {
                if (size == 0 || clock.Elapsed - shown < TimeSpan.FromSeconds(0.5)) return;
                shown = clock.Elapsed;
                var speed = done / Math.Max(0.1, clock.Elapsed.TotalSeconds);
                Note($"Downloading ComfyUI: {done * 100 / size}% of {Size(size)} · {TimeSpan.FromSeconds((size - done) / Math.Max(1, speed)):m\\:ss} left");
            });
        }
        Note("Unpacking ComfyUI. This takes a few minutes…");
        var sevenZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        var psi = File.Exists(sevenZip) ? new ProcessStartInfo(sevenZip) { ArgumentList = { "x", "-y", "-o" + dir, file } }
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { ArgumentList = { "-xf", file, "-C", dir } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using (var p = Process.Start(psi))
        {
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new Exception(File.Exists(sevenZip) ? "7-Zip couldn't unpack it." : "Windows couldn't unpack it. Install 7-Zip from 7-zip.org, then press Set up ComfyUI again.");
        }
        File.Delete(file);
        return FindComfy(dir) ?? throw new Exception("the download didn't contain ComfyUI.");
    }

    // into a .part file that takes the real name only when complete. GitHub serves each connection slowly from some places,
    // so a big file comes in 8 pieces at once, and a piece that breaks off carries on from where it stopped.
    static async Task DownloadFile(string url, string dest, Action<long, long> progress = null)
    {
        var part = dest + ".part";
        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            long size, done = 0;
            using (var probe = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new(0, 0) } })
            using (var r = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead))
            {
                r.EnsureSuccessStatusCode();
                size = r.StatusCode == System.Net.HttpStatusCode.PartialContent ? r.Content.Headers.ContentRange?.Length ?? 0 : 0;   // 0: no pieces
            }
            int pieces = size >= 64 << 20 ? 8 : 1;
            long piece = (size + pieces - 1) / pieces;
            using (var f = File.Create(part)) f.SetLength(size);
            await Task.WhenAll(Enumerable.Range(0, pieces).Select(async i =>
            {
                long at = i * piece, end = Math.Min(size, at + piece) - 1;
                for (int tries = 0; ; tries++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (size > 0) req.Headers.Range = new(at, end);
                        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        res.EnsureSuccessStatusCode();
                        await using var from = await res.Content.ReadAsStreamAsync();
                        await using var to = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.Write) { Position = at };
                        var buf = new byte[1 << 20];
                        int n;
                        while ((n = await from.ReadAsync(buf)) > 0)
                        {
                            await to.WriteAsync(buf.AsMemory(0, n));
                            at += n;
                            progress?.Invoke(Interlocked.Add(ref done, n), size);
                        }
                        if (size > 0 && at <= end) throw new IOException("the connection broke off");
                        return;
                    }
                    catch when (size > 0 && tries < 5) { await Task.Delay(3000); }
                }
            }));
            File.Move(part, dest, true);
        }
        finally { try { File.Delete(part); } catch { } }
    }

    // the CUDA 13 build needs driver 580+ and a GTX 16 / RTX card (compute 7.5+); older ones get the CUDA 12.6 build
    static string NvidiaBuild()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version,compute_cap --format=csv,noheader")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            var gpu = p.StandardOutput.ReadLine()?.Split(',');
            p.WaitForExit();
            bool modern = int.Parse(gpu[0].Split('.')[0]) >= 580 && double.Parse(gpu[1], CultureInfo.InvariantCulture) >= 7.5;
            return modern ? "ComfyUI_windows_portable_nvidia" : "ComfyUI_windows_portable_nvidia_cu126";
        }
        catch { return null; }      // no nvidia-smi: no NVIDIA card
    }
}
