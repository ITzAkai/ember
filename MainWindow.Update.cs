// Updates from the GitHub releases: a newer release's exe downloads in the background and must match the SHA-256 GitHub publishes.
// It replaces this exe when the app closes (a running exe can be renamed, not overwritten), or right away with "Update to …".
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;

namespace ImageForge;

public partial class MainWindow
{
    const string LatestRelease = "https://api.github.com/repos/ITzAkai/ember/releases/latest";
    static readonly Version AppVersion = Assembly.GetEntryAssembly().GetName().Version;
    static string NewExe => Environment.ProcessPath + ".new";
    static string OldExe => Environment.ProcessPath + ".old";
    bool updateReady;
    static string rejectedDigest;          // a release whose exe turned out older than this one: not downloaded again

    async Task CheckForUpdate()
    {
        if (Environment.GetEnvironmentVariable("IMAGEFORGE_SETTINGS") is { Length: > 0 }) return;    // a test copy: leave GitHub's hourly limit to the real app
        await Task.Delay(3000);
        try { File.Delete(OldExe); } catch { }          // the copy the last update replaced (gone by now, or next time)
        // GitHub answers 60 checks an hour per internet address: a refused or failed check is tried again in an hour,
        // and an app left open checks again every 6 hours
        while (!closing && !updateReady)
            await Task.Delay(await TryUpdate() ? TimeSpan.FromHours(6) : TimeSpan.FromHours(1));
    }

    // true when GitHub answered (whether or not there was anything new)
    async Task<bool> TryUpdate()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            req.Headers.UserAgent.ParseAdd("Ember/" + AppVersion);
            using var res = await web.SendAsync(req);
            if (!res.IsSuccessStatusCode) return false;
            var release = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            var tag = release?["tag_name"]?.ToString();
            if (!Version.TryParse(tag?.TrimStart('v'), out var latest) || latest <= AppVersion) return true;
            var exe = (release["assets"] as JsonArray)?.FirstOrDefault(a => a?["name"]?.ToString().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            if (exe?["browser_download_url"]?.ToString() is not string url || exe["digest"]?.ToString() is not string digest) return true;
            if (digest == rejectedDigest) return true;
            var sha = digest.Replace("sha256:", "");
            if (!File.Exists(NewExe) || !Sha256Is(NewExe, sha))
            {
                await DownloadFile(url, NewExe);
                if (!Sha256Is(NewExe, sha)) { File.Delete(NewExe); return false; }
            }
            // a release holding an older build would reinstall itself on every start
            if (!Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(NewExe).FileVersion, out var got) || got <= AppVersion)
            { File.Delete(NewExe); rejectedDigest = digest; return true; }
            updateReady = true;
            UpdateBtnText.Text = $"Update to {tag}";
            UpdateBtn.Visibility = Visibility.Visible;
            return true;
        }
        catch { return false; /* offline, or no rights to this folder */ }
    }

    static bool Sha256Is(string file, string sha)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).Equals(sha, StringComparison.OrdinalIgnoreCase);
    }

    // false when the files couldn't be swapped (an antivirus holding the new exe, say): this version keeps running
    bool ApplyUpdate()
    {
        if (!updateReady) return true;
        updateReady = false;
        try { File.Move(Environment.ProcessPath, OldExe, true); File.Move(NewExe, Environment.ProcessPath); return true; }
        catch { try { if (!File.Exists(Environment.ProcessPath)) File.Move(OldExe, Environment.ProcessPath); } catch { } return false; }
    }

    void Update_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { Note("A picture is being made: update once it's finished.", true); return; }
        if (!ApplyUpdate() || !File.Exists(Environment.ProcessPath)) { Note("The update couldn't be installed (the new file is in use). Close Ember and try again.", true); return; }
        // the new version starts its own ComfyUI (no taking over this one), so launch settings it adds, like live previews, apply at once
        StopServer();
        restarting = true;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { WorkingDirectory = Root, UseShellExecute = false });
        Close();
    }
}
