// ComfyUI (Expert): ComfyUI's own node editor inside Ember, through WebView2 (Open in browser when WebView2 isn't there).
// The most advanced option: nothing changed there touches Ember's settings. "Load the last job" opens Ember's last
// picture job in the editor to tweak by hand. Also the full list of installed plugins, with switch off / on and uninstall.
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ImageForge;

public partial class MainWindow
{
    WebView2 expertView;
    JsonObject lastGraph;           // the last job sent to ComfyUI, for Load the last job

    async void Expert_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlays_Click(null, null);
        ExpertPanel.Visibility = Visibility.Visible;
        LoadLastJobBtn.IsEnabled = lastGraph != null;
        if (expertView != null) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetDirectoryName(Settings.FilePath), "WebView2"));
            expertView = new WebView2();
            ExpertHost.Child = expertView;
            await expertView.EnsureCoreWebView2Async(env);
            expertView.CoreWebView2.NavigationCompleted += (_, a) =>
            {
                if (!a.IsSuccess) ExpertNote.Text = "ComfyUI isn't answering. Start it from Ember (or press Restart ComfyUI), then open this page again.";
            };
            expertView.CoreWebView2.Navigate(Api);
            ExpertNote.Text = "ComfyUI's full node editor: the most advanced option. Nothing you change here touches Ember's own settings.";
        }
        catch (Exception ex)
        {
            ExpertHost.Child = null;
            expertView = null;
            ExpertNote.Text = "ComfyUI can't show inside Ember on this PC (" + ex.Message + "). Open in browser works instead.";
        }
    }

    void ExpertBrowser_Click(object sender, RoutedEventArgs e) => OpenPage(Api);
    void ExpertClose_Click(object sender, RoutedEventArgs e) => ExpertPanel.Visibility = Visibility.Collapsed;

    async void LoadLastJob_Click(object sender, RoutedEventArgs e)
    {
        if (expertView?.CoreWebView2 == null || lastGraph == null) return;
        var result = await expertView.CoreWebView2.ExecuteScriptAsync(
            $"(window.app && window.app.loadApiJson) ? (window.app.loadApiJson({lastGraph.ToJsonString()}, 'Ember job'), 'ok') : 'missing'");
        ExpertNote.Text = result.Contains("ok") ? "Loaded Ember's last job into the editor." : "This ComfyUI version can't load it directly; use Save in Ember's viewer instead.";
    }

    // ---------------- every installed plugin ----------------
    void ShowAllPlugins(HashSet<string> failed)
    {
        if (!Directory.Exists(CustomNodesDir)) return;
        var packs = Directory.GetDirectories(CustomNodesDir).Select(d => (Path: d, Name: Path.GetFileName(d)))
            .Where(p => !p.Name.StartsWith('.') && p.Name != "__pycache__").OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PluginsList.Children.Add(new TextBlock { Text = "All installed plugins", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        foreach (var (path, name) in packs)
        {
            bool off = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var clean = Regex.Replace(name, @"\.disabled$", "", RegexOptions.IgnoreCase);
            var status = off ? "Switched off" : failed.Contains(name) ? "Failed to load" : settings.FastStart && !WhitelistedPacks().Contains(name) ? "Installed (not loaded in Fast start)" : "Installed";
            var actions = new List<(string, Action)> { off ? ("Switch on", () => EnablePack(path)) : ("Switch off", () => DisablePack(path)), ("Uninstall", () => UninstallPack(path, clean)) };
            PluginsList.Children.Add(PluginRow(clean, status, RepoOf(path) is string repo ? "github.com/" + repo : "", "", false, null, actions));
        }
        var more = new Button { Content = "Browse and install more plugins…", Style = (Style)FindResource("Ghost"), Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        more.Click += (_, _) => BrowseMorePlugins();
        PluginsList.Children.Add(more);
    }

    void UninstallPack(string path, string name)
    {
        if (MessageBox.Show(this, $"Move the plugin \"{name}\" to the Recycle Bin? ComfyUI needs a restart afterwards.", "Uninstall plugin",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            PluginsNote.Text = $"{name} is in the Recycle Bin. Restart ComfyUI to finish.";
        }
        catch (Exception ex) { PluginsNote.Text = "Couldn't uninstall it: " + ex.Message; }
        ShowPlugins();
    }

    // other plugins come through the ComfyUI Manager's own screen, after a plain warning about what plugins can do
    void BrowseMorePlugins()
    {
        if (MessageBox.Show(this, "Plugins from the ComfyUI Manager are written by many different people and run their own code on your PC, " +
                "with the same access as any program you install. Only install ones you trust; Ember doesn't check them.\n\nOpen the ComfyUI Manager?",
                "Install more plugins", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (settings.FastStart && !PipManager)
        {
            PluginsNote.Text = "Fast start doesn't load the ComfyUI Manager: turn Fast start off, restart ComfyUI, then use the Manager button in ComfyUI.";
            return;
        }
        Expert_Click(null, null);
        ExpertNote.Text = "Use the Manager button in ComfyUI's menu to browse plugins. Restart ComfyUI from Ember afterwards.";
    }
}
