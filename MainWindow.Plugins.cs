// Recommended ComfyUI plugins: a curated list pinned to exact commits (docs/research/custom-nodes.md).
// Custom nodes run their own Python inside ComfyUI, so only these archives are installed, each checked against its SHA-256,
// and pip is held to the installed torch and numpy so a plugin can't swap them out.
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace ImageForge;

record PluginModel(string Url, string Folder, string File, string Sha256, long Bytes);
record Plugin(string Folder, string Name, string Group, bool Checked, string Repo, string Commit, string Version, string ZipSha256,
              long Bytes, string Node, string About, PluginModel Model = null);

enum PluginState { Missing, Installed, Older, Other, Disabled, Failed }

public partial class MainWindow
{
    const string ManagerName = "ComfyUI Manager";
    static readonly Plugin[] Recommended =
    {
        new("ComfyUI-Inpaint-CropAndStitch", "Inpaint Crop & Stitch", "Image quality", true, "lquesada/ComfyUI-Inpaint-CropAndStitch", "8584b08d85", "3.0.17",
            "022f4342035cd1af4d0b32efadb472b3c3a41411a27487460f3d22c4f783e0dc", 15_727_638, "InpaintCropImproved",
            "Better edits: repaints just the area around what you painted, at full detail, and blends it back in."),
        new("ComfyUI-Impact-Pack", "Impact Pack", "Image quality", true, "ltdrdata/ComfyUI-Impact-Pack", "429d0159ad", "8.28.3",
            "af6226a583b310ad6efa04a70ad51baccfd6b29b76ba6ad8154971a3bebd7650", 180_000_000, "FaceDetailer",
            "Fix faces: finds each face and redraws it at higher detail. Adds about 180 MB of Python packages (OpenCV and others)."),
        new("ComfyUI-Impact-Subpack", "Impact Subpack + face finder", "Image quality", true, "ltdrdata/ComfyUI-Impact-Subpack", "50c7b71a6a", "1.3.5",
            "8dcaf54b147ab2c558259aeb17779de3a9a5b29c5e8181ae35730f9a8909f694", 40_000_000, "UltralyticsDetectorProvider",
            "The face detector that Fix faces uses.",
            new("https://huggingface.co/Bingsu/adetailer/resolve/main/face_yolov8m.pt", @"ultralytics\bbox", "face_yolov8m.pt",
                "717923c19b3f4bbf5250b728f1fa6b2cb72a33aed1d236ea9caf0e21ad943e5f", 52_026_019)),
        new("ComfyUI-ppm", "Attention Couple (ppm)", "Image quality", true, "pamparamm/ComfyUI-ppm", "6c6c360155", "1.1.10",
            "5c0387bb3051af48856ca20cc4dd7b7096fcc9d493577dfa177ec70fc4cea27f", 743_693, "AttentionCouplePPM",
            "Keeps two people from merging into one on SDXL and Anima models."),
        new("ComfyUI_UltimateSDUpscale", "Ultimate SD Upscale", "Optional", false, "ssitu/ComfyUI_UltimateSDUpscale", "a5547db9e1", "1.7.2",
            "9a651798034fbd979a06b456a70c5133390b991d500c7d857c16b81ef04bcabe", 459_494, "UltimateSDUpscale",
            "Tiled detail upscale for big prints. Fetches a helper script the first time ComfyUI starts."),
        new("ComfyUI-Anima-NAG", "Anima NAG", "Optional", false, "hybskgks28275/ComfyUI-Anima-NAG", "1590401343", "1.0.1",
            "077ea49dddf009295e2de91ba35dd65fe2392aeb91ea7d6617b50984fb5e8f12", 33_149, "AnimaNormalizedAttentionGuidance",
            "Makes the negative prompt work on Anima Turbo (CFG 1)."),
        new("ComfyUI_IPAdapter_plus", "IPAdapter Plus", "Optional", false, "cubiq/ComfyUI_IPAdapter_plus", "a0f451a511", "2.0.0",
            "743dd77ce80d1d27103fb6a15b431bdf1100fa110f83140398d4306ed4874a0e", 734_885, "IPAdapterAdvanced",
            "Style reference for SDXL models. Its own models (about 3.4 GB) are downloaded separately. No longer developed."),
        new("ComfyUI-GGUF", "GGUF loader (Krea 2)", "Optional", false, "leejet/ComfyUI-GGUF", "373048b840", "2.0.0",
            "51e9582593aea82fe3c847a51ebb87ceca2e32584b5f2b4ad843b056c83646b0", 122_247, "UnetLoaderGGUF",
            "Loads smaller GGUF Krea 2 files for 6–8 GB graphics cards. Experimental; Qwen-Image GGUF files don't work yet."),
    };

    string CustomNodesDir => Path.Combine(Root, "ComfyUI", "custom_nodes");
    string Python => Path.Combine(Root, "python_embeded", "python.exe");
    string ServerLog => Path.Combine(Root, "comfy_server.log");
    HashSet<string> nodeNames = new();          // every node ComfyUI has loaded, from /object_info
    bool installingPlugins;

    // ---------------- what's there ----------------
    static string Norm(string name) => Regex.Replace(Regex.Replace(name.ToLowerInvariant(), @"\.disabled$", ""), @"^comfyui[-_ ]?|[-_ ]", "");

    // the folder that holds a pack, matched by name with or without the ComfyUI- prefix; disabled ones too (".disabled" or the Manager's .disabled folder)
    (string Path, bool Disabled) FindPack(string folder)
    {
        if (!Directory.Exists(CustomNodesDir)) return (null, false);
        var want = Norm(folder);
        foreach (var d in Directory.GetDirectories(CustomNodesDir))
            if (Norm(Path.GetFileName(d)) == want) return (d, d.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
        var off = Path.Combine(CustomNodesDir, ".disabled");
        if (Directory.Exists(off))
            foreach (var d in Directory.GetDirectories(off))
                if (Norm(Regex.Replace(Path.GetFileName(d), @"@.*$", "")) == want) return (d, true);
        return (null, false);
    }

    // "owner/repo" from the pack's git config or pyproject, lower case
    static string RepoOf(string dir)
    {
        foreach (var (file, pattern) in new[] { (Path.Combine(dir, ".git", "config"), @"url\s*=\s*(\S+)"), (Path.Combine(dir, "pyproject.toml"), @"Repository\s*=\s*""([^""]+)""") })
            try
            {
                if (File.Exists(file) && Regex.Match(File.ReadAllText(file), pattern) is { Success: true } m
                    && Regex.Match(m.Groups[1].Value, @"github\.com[/:]([^/]+/[^/.\s]+)") is { Success: true } r) return r.Groups[1].Value.ToLowerInvariant();
            }
            catch { }
        return null;
    }

    static Version VersionOf(string dir)
    {
        try { return Regex.Match(File.ReadAllText(Path.Combine(dir, "pyproject.toml")), @"^version\s*=\s*""([\d.]+)", RegexOptions.Multiline) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null; }
        catch { return null; }
    }

    // packs ComfyUI reported as failed in its start-up log ("... (IMPORT FAILED): C:\...\custom_nodes\X")
    HashSet<string> FailedPacks()
    {
        try
        {
            using var s = new FileStream(ServerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var log = new StreamReader(s).ReadToEnd();
            return Regex.Matches(log, @"\(IMPORT FAILED\):\s*(.+)").Select(m => Path.GetFileName(m.Groups[1].Value.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    (PluginState State, string Path) StateOf(Plugin p)
    {
        var (dir, disabled) = FindPack(p.Folder);
        if (dir == null) return (PluginState.Missing, null);
        if (disabled) return (PluginState.Disabled, dir);
        if (RepoOf(dir) is string repo && repo != p.Repo.ToLowerInvariant()) return (PluginState.Other, dir);
        if (FailedPacks().Contains(Path.GetFileName(dir))) return (PluginState.Failed, dir);
        if (VersionOf(dir) is Version v && Version.TryParse(p.Version, out var pin) && v < pin) return (PluginState.Older, dir);
        return (PluginState.Installed, dir);
    }

    // ComfyUI 0.39+ ships the Manager as a pip package started with --enable-manager; older installs have it as a custom node
    bool LegacyManager => Directory.Exists(CustomNodesDir) && Directory.GetDirectories(CustomNodesDir).Any(d => Path.GetFileName(d).Contains("comfyui-manager", StringComparison.OrdinalIgnoreCase));
    bool PipManager => Directory.Exists(Path.Combine(Root, "python_embeded", "Lib", "site-packages", "comfyui_manager"));
    bool ManagerShipped => File.Exists(Path.Combine(Root, "ComfyUI", "manager_requirements.txt"));
    bool ManagerMissing => ManagerShipped && !PipManager && !LegacyManager;

    // fast start loads only the recommended packs that are installed, not every custom node in the folder
    List<string> WhitelistedPacks() => Recommended.Select(StateOf).Where(s => s.State is PluginState.Installed or PluginState.Older).Select(s => Path.GetFileName(s.Path))
        .Concat(Directory.Exists(Path.Combine(CustomNodesDir, FinishFolder)) ? [FinishFolder] : []).ToList();

    string LaunchFlags()
    {
        var flags = "";
        if (settings.FastStart)
        {
            var keep = WhitelistedPacks();
            flags += " --disable-all-custom-nodes" + (keep.Count > 0 ? " --whitelist-custom-nodes " + string.Join(" ", keep.Select(k => k.Contains(' ') ? $"\"{k}\"" : k)) : "");
        }
        if (PipManager && !LegacyManager) flags += " --enable-manager";
        return flags;
    }

    async Task LoadNodeNames()
    {
        try { nodeNames = JsonNode.Parse(await http.GetStringAsync("/object_info"))?.AsObject().Select(kv => kv.Key).ToHashSet() ?? new(); }
        catch { }
    }

    // ---------------- the banner and the Settings list ----------------
    List<Plugin> MissingDefaults() => Recommended.Where(p => p.Checked && StateOf(p).State == PluginState.Missing).ToList();

    void ShowPluginBanner()
    {
        var missing = MissingDefaults();
        bool show = IsComfy(Root) && !settings.HidePluginBanner && !installingPlugins && (missing.Count > 0 || ManagerMissing);
        PluginBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var names = missing.Select(p => p.Name).Concat(ManagerMissing ? [ManagerName] : []).ToList();
        PluginBannerText.Text = $"{string.Join(", ", names)} · about {Size(missing.Sum(p => p.Bytes + (p.Model?.Bytes ?? 0)) + (ManagerMissing ? 20_000_000 : 0))} to download";
    }

    async void InstallPluginsBanner_Click(object sender, RoutedEventArgs e) => await InstallPlugins(MissingDefaults(), ManagerMissing);
    void ChoosePlugins_Click(object sender, RoutedEventArgs e) { Settings_Click(sender, e); PluginsHeading.BringIntoView(); }
    void HidePluginBanner_Click(object sender, RoutedEventArgs e) { settings.HidePluginBanner = true; settings.Save(); ShowPluginBanner(); }

    readonly Dictionary<Plugin, CheckBox> pluginChecks = new();

    void ShowPlugins()
    {
        FastStartSwitch.IsChecked = settings.FastStart;
        PluginsList.Children.Clear();
        pluginChecks.Clear();
        if (!IsComfy(Root)) { PluginsNote.Text = "Set up ComfyUI first."; return; }
        var failed = FailedPacks();
        foreach (var group in Recommended.GroupBy(p => p.Group))
        {
            PluginsList.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
            if (group.Key == "Image quality") PluginsList.Children.Add(PluginRow("Image Forge colour finish", nodeNames.Contains("ImageForgeFinish") ? "Built in" : "Built in (loads after a ComfyUI restart)",
                "Image Forge's own small node for the Colour finish booster: plain torch, no extra packages.", "", false, null));
            if (group.Key == "Image quality" && ManagerShipped) PluginsList.Children.Add(PluginRow(ManagerName,
                PipManager ? "Installed" : LegacyManager ? "Installed (older add-on version)" : "Not installed",
                "Install, update and switch off ComfyUI plugins. Started with ComfyUI.", "about 20 MB", ManagerMissing, null));
            foreach (var p in group)
            {
                var (state, dir) = StateOf(p);
                var status = state switch
                {
                    PluginState.Missing => "Not installed", PluginState.Installed => "Installed", PluginState.Older => "Older version installed",
                    PluginState.Other => "A different version is installed", PluginState.Disabled => "Switched off", _ => "Failed to load",
                };
                var actions = new List<(string, Action)>();
                if (state == PluginState.Disabled) actions.Add(("Enable", () => EnablePack(dir)));
                if (state == PluginState.Older) actions.Add(("Update", () => _ = InstallPlugins([p], false, replace: dir)));
                if (state == PluginState.Failed) { actions.Add(("Repair", () => _ = InstallPlugins([p], false, replace: dir))); actions.Add(("Switch off", () => DisablePack(dir))); }
                PluginsList.Children.Add(PluginRow(p.Name, status, p.About, $"{Size(p.Bytes + (p.Model?.Bytes ?? 0))} · {p.Repo} @ {p.Commit[..7]}",
                    state == PluginState.Missing, p, actions));
            }
        }
        // the user's own packs that ComfyUI couldn't load
        var others = failed.Where(f => !Recommended.Any(p => Norm(p.Folder) == Norm(f))).ToList();
        if (others.Count > 0)
        {
            PluginsList.Children.Add(new TextBlock { Text = "Other plugins that failed to load", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
            foreach (var f in others)
                PluginsList.Children.Add(PluginRow(f, "Failed to load", "ComfyUI couldn't start this plugin (see the ComfyUI log). Switching it off speeds up start-up.", "", false, null,
                    [("Switch off", () => DisablePack(Path.Combine(CustomNodesDir, f)))]));
        }
        PluginsNote.Text = settings.FastStart
            ? "Fast start is on: ComfyUI loads only these plugins, so it starts quicker. Your other plugins still work in ComfyUI's own window."
            : "ComfyUI loads every plugin in its custom_nodes folder.";
    }

    FrameworkElement PluginRow(string name, string status, string about, string facts, bool canInstall, Plugin p, List<(string, Action)> actions = null)
    {
        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        foreach (var (text, act) in actions ?? [])
        {
            var b = new Button { Content = text, Style = (Style)FindResource("Ghost"), Height = 28, Margin = new Thickness(6, 0, 0, 0), FontSize = 12 };
            b.Click += (_, _) => act();
            right.Children.Add(b);
        }
        var left = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (canInstall)
        {
            var check = new CheckBox { IsChecked = p?.Checked ?? true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(check, "Install " + name);
            if (p != null) pluginChecks[p] = check; else managerCheck = check;
            head.Children.Add(check);
        }
        head.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = "  " + status, Style = (Style)FindResource("Note"),
            Foreground = Brush(status.StartsWith("Installed") ? "Ok" : status.StartsWith("Failed") ? "Danger" : "Muted") });
        left.Children.Add(head);
        left.Children.Add(new TextBlock { Text = about, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (facts != "") left.Children.Add(new TextBlock { Text = facts, FontSize = 11, Foreground = Brush("Faint"), Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(left);
        return new Border { Background = Brush("Raised"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 0, 6), Child = dock };
    }

    CheckBox managerCheck;

    async void InstallSelected_Click(object sender, RoutedEventArgs e) =>
        await InstallPlugins(pluginChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList(), managerCheck?.IsChecked == true && ManagerMissing);

    void FastStart_Click(object sender, RoutedEventArgs e)
    {
        settings.FastStart = FastStartSwitch.IsChecked == true;
        settings.Save();
        ShowPlugins();
        PluginsNote.Text += " Restart ComfyUI to switch.";
    }

    void EnablePack(string dir)
    {
        try
        {
            var name = Regex.Replace(Path.GetFileName(dir), @"(\.disabled|@.*)$", "");
            Directory.Move(dir, Path.Combine(CustomNodesDir, name));
            PluginsNote.Text = "Switched on. Restart ComfyUI to load it.";
        }
        catch (Exception ex) { PluginsNote.Text = "Couldn't switch it on: " + ex.Message; }
        ShowPlugins();
    }

    void DisablePack(string dir)
    {
        try { Directory.Move(dir, dir + ".disabled"); PluginsNote.Text = "Switched off. Restart ComfyUI to apply."; }
        catch (Exception ex) { PluginsNote.Text = "Couldn't switch it off: " + ex.Message; }
        ShowPlugins();
    }

    // ---------------- installing ----------------
    void PluginProgress(string text) { PluginsNote.Text = text; Note(text); }

    async Task InstallPlugins(List<Plugin> list, bool manager, string replace = null)
    {
        if (installingPlugins || (list.Count == 0 && !manager)) return;
        if (server == null && await Alive())
        {
            PluginProgress("Close the ComfyUI window that's running first: plugins can only be installed while ComfyUI is stopped.");
            return;
        }
        installingPlugins = true;
        ShowPluginBanner();
        bool wasRunning = server is { HasExited: false };
        var work = Path.Combine(Path.GetTempPath(), "ImageForge-plugins");
        try
        {
            if (wasRunning) { PluginProgress("Stopping ComfyUI…"); StopServer(); ready = false; GenerateBtn.IsEnabled = false; SetState("Installing", Brush("Accent")); await Task.Delay(1500); }
            Directory.CreateDirectory(work);
            var requirements = new List<string>();
            foreach (var p in list)
            {
                var zip = Path.Combine(work, p.Folder + ".zip");
                await DownloadFile($"https://codeload.github.com/{p.Repo}/zip/{p.Commit}", zip, (done, _) => PluginProgress($"Downloading {p.Name}: {Size(done)}…"));
                if (!Sha256Is(zip, p.ZipSha256)) throw new Exception($"{p.Name} didn't match its checksum, so it wasn't installed.");
                var unpacked = Path.Combine(work, p.Folder);
                if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
                ZipFile.ExtractToDirectory(zip, unpacked);
                var top = Directory.GetDirectories(unpacked).Single();
                var dest = Path.Combine(CustomNodesDir, p.Folder);
                if (replace != null && Directory.Exists(replace))       // an update or repair: the old copy goes to the Recycle Bin
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(replace, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    dest = replace;
                }
                if (Directory.Exists(dest)) throw new Exception($"{dest} already exists.");
                Directory.Move(top, dest);
                var req = Path.Combine(dest, "requirements.txt");
                if (File.Exists(req)) requirements.AddRange(File.ReadAllLines(req));
                if (p.Model is { } m)
                {
                    var file = Path.Combine(ModelsDir, m.Folder, m.File);
                    if (!File.Exists(file) || !Sha256Is(file, m.Sha256))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        await DownloadFile(m.Url, file, (done, size) => PluginProgress($"Downloading {m.File}: {Size(done)} of {Size(size)}…"));
                        if (!Sha256Is(file, m.Sha256)) { File.Delete(file); throw new Exception($"{m.File} didn't match its checksum."); }
                    }
                }
            }
            if (manager) requirements.AddRange(File.ReadAllLines(Path.Combine(Root, "ComfyUI", "manager_requirements.txt")));
            if (requirements.Count > 0) await PipInstall(requirements, work);
            PluginProgress($"Installed {string.Join(", ", list.Select(p => p.Name).Concat(manager ? [ManagerName] : []))}.");
        }
        catch (Exception ex) { PluginProgress("Installing plugins failed: " + ex.Message); }
        finally
        {
            installingPlugins = false;
            try { Directory.Delete(work, true); } catch { }
            if (SettingsPanel.Visibility == Visibility.Visible) ShowPlugins();
            ShowPluginBanner();
        }
        if (wasRunning) { SetState("Starting", Brush("Accent")); await StartUp(); }
        else if (await Alive()) Note("Restart ComfyUI to load the new plugins.");
    }

    // pip with the portable Python; git lines are skipped (portable ComfyUI has no git) and torch, torchvision, torchaudio and numpy stay as they are
    async Task PipInstall(List<string> lines, string work)
    {
        static string NameOf(string l) => Regex.Match(l, @"^[A-Za-z0-9_.\-]+").Value.ToLowerInvariant().Replace('_', '-');
        string[] held = ["torch", "torchvision", "torchaudio", "numpy"];
        var wanted = lines.Select(l => l.Split('#')[0].Trim()).Where(l => l != "" && !l.StartsWith("git+") && !l.StartsWith('-') && !held.Contains(NameOf(l))).Distinct().ToList();
        if (wanted.Count == 0) return;
        PluginProgress("Installing Python packages for the plugins (a few minutes)…");
        var installed = await Run(Python, ["-s", "-m", "pip", "list", "--format=json", "--disable-pip-version-check"]);
        var pins = JsonNode.Parse(installed.Output)?.AsArray().Where(x => held.Contains(x?["name"]?.ToString().ToLowerInvariant()))
            .Select(x => $"{x["name"]}=={x["version"]}").ToList() ?? [];
        File.WriteAllLines(Path.Combine(work, "requirements.txt"), wanted);
        File.WriteAllLines(Path.Combine(work, "constraints.txt"), pins);
        var r = await Run(Python, ["-s", "-m", "pip", "install", "-r", Path.Combine(work, "requirements.txt"), "-c", Path.Combine(work, "constraints.txt"),
            "--disable-pip-version-check", "--no-warn-script-location"]);
        File.WriteAllText(Path.Combine(Root, "plugins_install.log"), r.Output);
        if (r.ExitCode != 0) throw new Exception("pip couldn't install their Python packages; details in plugins_install.log in the ComfyUI folder.");
    }

    static async Task<(int ExitCode, string Output)> Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await output + await errors);
    }
}
