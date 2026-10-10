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
        quietSettings = false;
        InpaintSwitch.IsEnabled = inpaintModel != null;
        InpaintNote.Text = inpaintModel != null
            ? "Cleaner blending where you painted or expanded (about 25 steps)."
            : "Needs the SDXL inpainting model (see Models).";
        ShowOutputFolder();
        HistoryNote.Text = settings.History.Count == 1 ? "1 recent prompt saved" : $"{settings.History.Count} recent prompts saved";
        ShortcutNote.Text = "";
        ShowCivitaiKey();
        ShowPlugins();
        AboutText.Text = $"Image Forge {AppVersion.ToString(2)}, ComfyUI in {Root.TrimEnd('\\')}\nComfyUI {(server != null ? "started by this app" : "already running")} at {Api}\nSettings in {Path.GetDirectoryName(Settings.FilePath)}";
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
        restarting = true;
        var adopt = server is { HasExited: false } ? $"--adopt {server.Id}" : "";
        Process.Start(new ProcessStartInfo(Environment.ProcessPath, adopt) { WorkingDirectory = Root, UseShellExecute = false });
        Close();
    }

    void SettingSwitch_Click(object sender, RoutedEventArgs e)
    {
        settings.KeepComfyRunning = KeepComfySwitch.IsChecked == true;
        settings.InpaintEdits = InpaintSwitch.IsChecked == true;
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
        settings.OutputDir = folder;
        settings.Save();
        ShowOutputFolder();
        for (int i = shots.Count - 1; i >= 0; i--) if (!shots[i].Pending) shots.RemoveAt(i);
        await LoadGallery();
        if (server != null) Restart_Click(null, null);
        else Note("ComfyUI was already running before Image Forge, so restart it yourself to use the new folder.", true);
    }

    void StartMenu_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Image Forge.lnk"), "Start menu");

    void DesktopShortcut_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Image Forge.lnk"), "desktop");

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
    void CloseOverlays_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = ModelsPanel.Visibility = Visibility.Collapsed;
    void OverlayBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseOverlays_Click(null, null);
    void OverlayCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;      // clicks inside the card don't close it
}
