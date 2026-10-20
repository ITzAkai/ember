// The Settings page: theme, ComfyUI behaviour, images folder, shortcuts, history. Saved straight away.
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ImageForge;

public partial class MainWindow
{
    void Settings_Click(object sender, RoutedEventArgs e)
    {
        quietSettings = true;
        (settings.Theme == "light" ? ThemeLight : ThemeDark).IsChecked = true;
        KeepComfySwitch.IsChecked = settings.KeepComfyRunning;
        InpaintSwitch.IsChecked = settings.InpaintEdits;
        SharpPreviewSwitch.IsChecked = settings.SharpPreviewAll;
        PreviewFilesRow.Visibility = PreviewFiles.Any(f => !File.Exists(Path.Combine(ModelsDir, "vae_approx", f.File))) ? Visibility.Visible : Visibility.Collapsed;
        quietSettings = false;
        InpaintSwitch.IsEnabled = inpaintModel != null;
        InpaintNote.Text = inpaintModel != null
            ? "Off: painted edits, Remove and Fix detail use your picked model. On: painted edits and Remove use the SDXL inpainting model, which blends cleanly but follows descriptions poorly. Expand always uses it."
            : "Needs the SDXL inpainting model (see Models).";
        ShowOutputFolder();
        HistoryNote.Text = settings.History.Count == 1 ? "1 recent prompt saved" : $"{settings.History.Count} recent prompts saved";
        ShortcutNote.Text = "";
        ShowCivitaiKey();
        ShowPlugins();
        ShowPerformance();
        _ = ShowHelperChoice();
        AboutText.Text = $"Ember {AppVersion.ToString(3)}, ComfyUI in {Root.TrimEnd('\\')}\nComfyUI {(server != null ? "started by this app" : "already running")} at {Api}\nSettings in {Path.GetDirectoryName(Settings.FilePath)}";
        SettingsPanel.Visibility = Visibility.Visible;
    }

    bool quietSettings;     // filling the page shouldn't count as changing it

    // the Civitai API key: only Windows can decrypt it, and only for this user
    string CivitaiKey
    {
        get
        {
            try { return settings.CivitaiKey == "" ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.CivitaiKey), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }

    void ShowCivitaiKey()
    {
        var key = CivitaiKey;
        CivitaiKeyNote.Text = key == null ? "No key saved. Some Civitai downloads need one: civitai.com → Account settings → API keys."
            : $"Key saved, ending in …{key[^Math.Min(4, key.Length)..]}. Stored encrypted for your Windows account.";
        RemoveKeyBtn.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = CivitaiKeyBox.Text.Trim();
        if (key == "") return;
        settings.CivitaiKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        settings.Save();
        CivitaiKeyBox.Text = "";
        ShowCivitaiKey();
    }

    void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        settings.CivitaiKey = "";
        settings.Save();
        ShowCivitaiKey();
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (quietSettings) return;
        var theme = ThemeLight.IsChecked == true ? "light" : "dark";
        bool changed = theme != settings.Theme;
        settings.Theme = theme;
        settings.Save();
        RestartAppBtn.Visibility = changed || RestartAppBtn.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // a fresh copy of the app takes over the running ComfyUI (no 30 s restart), then this one closes, saving as usual
    void RestartApp_Click(object sender, RoutedEventArgs e)
    {
        ApplyUpdate();                          // a waiting update goes in first, so the restart runs it (as the Update button does)
        restarting = true;
        var adopt = server is { HasExited: false } ? $"--adopt {server.Id}" : "";
        Process.Start(new ProcessStartInfo(Environment.ProcessPath, adopt) { WorkingDirectory = Root, UseShellExecute = false });
        Close();
    }

    // ---------------- sharp live preview files ----------------
    // ComfyUI's portable build brings the SDXL one; these two it doesn't: Wan 2.1's (Krea 2, Anima, Wan, MiniMax H3) and Qwen-Image's.
    // Qwen's comes with an encoder and other layer names, so it is converted here, and the result checked against its known fingerprint.
    static readonly (string File, string Url, string Sha256, string Made)[] PreviewFiles =
    {
        ("lighttaew2_1.safetensors", "https://huggingface.co/lightx2v/Autoencoders/resolve/main/lighttaew2_1.safetensors",
            "8666e769f449f2d955651564bcadb0c5e12461b50480e67b7241f32c15f11a19", null),
        ("taeqi2_1_decoder.safetensors", "https://huggingface.co/madebyollin/taeqi2_1/resolve/main/taeqi2_1.safetensors",
            "6578b31cc5e336233a2c842eba61b56425bbcac97c7088705f7c7db93291887e", "992112baa2e31f321e4bfd9097666d5652107ff5fdacb373448887e1908a3e40"),
    };

    async void GetPreviewFiles_Click(object sender, RoutedEventArgs e)
    {
        PreviewFilesBtn.IsEnabled = false;
        var dir = Path.Combine(ModelsDir, "vae_approx");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (file, url, sha, made) in PreviewFiles)
            {
                var target = Path.Combine(dir, file);
                if (File.Exists(target)) continue;
                var download = made == null ? target + ".part2" : Path.Combine(dir, "taeqi2_1.original");
                await DownloadFile(url, download, (done, size) => PreviewFilesNote.Text = $"Downloading {file}: {Size(done)} of {Size(size)}…");
                if (!Sha256Is(download, sha)) { File.Delete(download); throw new Exception($"{file} didn't match its fingerprint."); }
                if (made == null) { File.Move(download, target); continue; }
                await Task.Run(() => TaeDecoder.Convert(download, target));
                File.Delete(download);
                if (!Sha256Is(target, made)) { File.Delete(target); throw new Exception($"{file} didn't convert as expected."); }
            }
            PreviewFilesRow.Visibility = Visibility.Collapsed;
            Note("Sharp preview files ready: they're used from the next picture.");
        }
        catch (Exception ex) { PreviewFilesNote.Text = "Couldn't get them: " + ex.Message; }
        finally { PreviewFilesBtn.IsEnabled = true; }
    }

    void SettingSwitch_Click(object sender, RoutedEventArgs e)
    {
        settings.KeepComfyRunning = KeepComfySwitch.IsChecked == true;
        settings.InpaintEdits = InpaintSwitch.IsChecked == true;
        settings.SharpPreviewAll = SharpPreviewSwitch.IsChecked == true;
        settings.Save();
    }

    void ShowOutputFolder()
    {
        OutputPathText.Text = Output;
        DefaultOutputBtn.IsEnabled = !string.IsNullOrWhiteSpace(settings.OutputDir);
    }

    async void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should new images go?", InitialDirectory = Output };
        if (dlg.ShowDialog(this) == true) await SwitchOutput(dlg.FolderName);
    }

    async void DefaultOutput_Click(object sender, RoutedEventArgs e) => await SwitchOutput("");

    // ComfyUI only takes its output folder at start, so the app restarts it (if it started it) and reloads the gallery
    async Task SwitchOutput(string folder)
    {
        if (folder == settings.OutputDir) return;
        if (busy) { ShortcutNote.Text = ""; Note("Wait for the queue to finish before switching folders.", true); return; }
        AllView.IsChecked = true;                   // locks the old folder's secret gallery
        settings.OutputDir = folder;
        settings.Save();
        ShowOutputFolder();
        for (int i = shots.Count - 1; i >= 0; i--) if (!shots[i].Pending) shots.RemoveAt(i);
        await LoadGallery();
        if (server != null) Restart_Click(null, null);
        else Note("ComfyUI was already running before Ember, so restart it yourself to use the new folder.", true);
    }

    void StartMenu_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Ember.lnk"), "Start menu");

    void DesktopShortcut_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ember.lnk"), "desktop");

    // Windows' own shortcut maker (the WScript.Shell COM object)
    void MakeShortcut(string lnk, string where)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = Environment.ProcessPath;
            link.WorkingDirectory = Root;
            link.IconLocation = Environment.ProcessPath + ",0";
            link.Description = "Make and edit images with ComfyUI on this PC";
            link.Save();
            ShortcutNote.Text = $"Added to the {where}.";
        }
        catch (Exception ex) { ShortcutNote.Text = $"Couldn't add the shortcut: {ex.Message}"; }
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        settings.History.Clear();
        settings.Save();
        HistoryNote.Text = "History cleared";
    }

    // ---------------- shared by the Settings and Models pages ----------------
    void CloseOverlays_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = ModelsPanel.Visibility = BundlesPanel.Visibility = ExpertPanel.Visibility = Visibility.Collapsed;
    void OverlayBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseOverlays_Click(null, null);
    void OverlayCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;      // clicks inside the card don't close it
}
