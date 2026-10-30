// Updates from the GitHub releases: a newer release's exe downloads in the background and must match the SHA-256 GitHub publishes
// and carry Ember.exe.sig, the release key's signature of that hash, so a release pushed without the key never installs.
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
    // the public half of the release key (ECDSA P-256); the private half stays on the release PC
    const string UpdateKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEPGm71y1DS8RPdgU6S/wHVWb2x01AefadMx3aLbf/4HGu047ehypmgbckjld2mJyZ6Pqwi34WuVlX71IArq8paQ==";

    static bool SignedByUs(string sha, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateKey), out _);
            return key.VerifyHash(Convert.FromHexString(sha), Convert.FromBase64String(signature.Trim()), DSASignatureFormat.Rfc3279DerSequence);
        }
        catch { return false; }
    }

    // --selftest part: the release key's signature of SHA-256("ember self-test") passes, and the same signature on another hash doesn't
    static bool SelfTestUpdateKey()
    {
        const string sig = "MEUCIGqnE/A8VEELjjOSTSv7gAYp6q6uK+AczzUm2ORHeDgfAiEAnwFcn0w+19fcmNbbZ2IYQ+RrzP7kMEW3+PsmpcRPT9s=";
        return SignedByUs("8855CDDFBDC56592CEA578ED8AC9A97F13339E1699D2FE6F4ED15C8C3C605BD5", sig)
            && !SignedByUs("8855CDDFBDC56592CEA578ED8AC9A97F13339E1699D2FE6F4ED15C8C3C605BD6", sig);
    }

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
            var sig = (release["assets"] as JsonArray)?.FirstOrDefault(a => a?["name"]?.ToString().Equals("Ember.exe.sig", StringComparison.OrdinalIgnoreCase) == true);
            if (sig?["browser_download_url"]?.ToString() is not string sigUrl || !SignedByUs(sha, await web.GetStringAsync(sigUrl)))
            { rejectedDigest = digest; return true; }
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
            UpdateBtn.ToolTip = $"Update to {tag}: restart now to use it, or it's installed when you close Ember";
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
