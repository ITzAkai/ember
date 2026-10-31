// Bundles: a ready-to-use setup in one file. A bundle is a model tab (model, steps, CFG, sampler, size, LoRAs, boosters, negative
// prompt, style) plus a download link and checksum for every file it needs, showcase pictures and sample prompts.
// "Get & set up" downloads what's missing (each file checked against its SHA-256) and opens the tab. "Make a bundle" turns the
// current tab into one, finding each file's source on Civitai by its hash; bundles share as .emberbundle files (a zip).
// Bundles marked adult show their pictures and prompts blurred until the viewer confirms they are 18 or older.
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ImageForge;

public class BundleFile
{
    public string Name { get; set; }
    public string Folder { get; set; }
    public string File { get; set; }
    public string Url { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; }
    public string License { get; set; }
}

public class Bundle
{
    public int Format { get; set; } = 1;
    public string Id { get; set; }
    public string Name { get; set; }
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Adult { get; set; }
    public bool Recommended { get; set; }
    public ModelProfile Tab { get; set; }
    public List<BundleFile> Files { get; set; } = new();
    public List<string> Prompts { get; set; } = new();
    public List<string> Showcase { get; set; } = new();       // picture files inside the bundle's folder
    [System.Text.Json.Serialization.JsonIgnore] public string Dir { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool BuiltIn { get; set; }
}

public partial class MainWindow
{
    static string BundlesDir => Path.Combine(Path.GetDirectoryName(Settings.FilePath), "bundles");
    static readonly JsonSerializerOptions bundleJson = new() { WriteIndented = true };
    static readonly string[] ShowcaseTypes = { ".png", ".jpg", ".jpeg", ".webp" };
    readonly HashSet<string> settingUp = new();
    List<Bundle> bundles = new();

    // one built-in bundle, made from the free catalog, so the page is never empty and shows what a bundle does
    Bundle StarterBundle()
    {
        BundleFile From(string file)
        {
            var c = Catalog.First(x => x.File == file);
            return new BundleFile { Name = c.Name, Folder = c.Folder, File = c.File, Url = c.Url, Size = c.Size, Sha256 = c.Sha256, License = c.License };
        }
        return new Bundle
        {
            Id = "builtin-krea2", BuiltIn = true, Recommended = true, Name = "Realistic photos · Krea 2 Turbo", Author = "Ember",
            Description = "Krea 2 Turbo with its two parts, set to its best settings: 8 steps, CFG 1, euler. Photos and art that follow the prompt closely.",
            Tab = new ModelProfile { Name = "Krea 2 photos", Model = "krea2_turbo_fp8_scaled.safetensors", Quality = "30", Steps = 8, Cfg = 1, Sampler = "euler",
                Scheduler = "simple", Size = "1024x1024", Count = 1, Negative = "", Style = "none", Saved = true },
            Files = { From("krea2_turbo_fp8_scaled.safetensors"), From("qwen3vl_4b_fp8_scaled.safetensors"), From("qwen_image_vae.safetensors") },
            Prompts = { "A woman reading by a rain-streaked window, warm lamp light, 35mm film photo", "A red fox in fresh snow at sunrise, telephoto, shallow depth of field" },
        };
    }

    void Bundles_Click(object sender, RoutedEventArgs e) { LoadBundles(); BuildBundles(); BundlesPanel.Visibility = Visibility.Visible; }

    // bundles shipped inside the app (the repo's bundles\<id>\ folders), unpacked next to the settings so their pictures open like any file
    static IEnumerable<Bundle> ShippedBundles()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var root = Path.Combine(Path.GetDirectoryName(Settings.FilePath), "builtin-bundles");
        var parts = asm.GetManifestResourceNames().Select(n => (Res: n, Path: n.Replace('\\', '/'))).Where(r => r.Path.StartsWith("bundles/") && r.Path.Count(c => c == '/') == 2);
        foreach (var group in parts.GroupBy(r => r.Path.Split('/')[1]))
        {
            Bundle b;
            try
            {
                var dir = Path.Combine(root, group.Key);
                Directory.CreateDirectory(dir);
                foreach (var (res, path) in group)
                {
                    using var s = asm.GetManifestResourceStream(res);
                    var target = Path.Combine(dir, Path.GetFileName(path));
                    var ms = new MemoryStream(); s.CopyTo(ms);
                    var bytes = ms.ToArray();
                    if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;    // by content: a fix can keep the size
                    File.WriteAllBytes(target, bytes);
                }
                b = JsonSerializer.Deserialize<Bundle>(File.ReadAllText(Path.Combine(dir, "bundle.json")));
                if (b?.Tab == null) continue;
                b.Dir = dir;
                b.BuiltIn = true;
                b.Tab.Loras ??= new();
            }
            catch { continue; /* a broken shipped bundle is skipped */ }
            yield return b;
        }
    }

    void LoadBundles()
    {
        bundles = new() { StarterBundle() };
        bundles.AddRange(ShippedBundles());
        if (!Directory.Exists(BundlesDir)) return;
        foreach (var dir in Directory.GetDirectories(BundlesDir))
        {
            try
            {
                var b = JsonSerializer.Deserialize<Bundle>(File.ReadAllText(Path.Combine(dir, "bundle.json")));
                if (b?.Tab == null) continue;
                b.Dir = dir;
                bundles.Add(b);
            }
            catch { /* a broken bundle folder is skipped */ }
        }
    }

    // where an installed file lives: Civitai's "Checkpoint" files without their own parts are moved to diffusion_models on download,
    // and GGUF model cards often say unet (ComfyUI loads diffusion models from either)
    static string InstalledPath(BundleFile f) => PartPaths(f.Folder, f.File).FirstOrDefault(File.Exists);

    static IEnumerable<string> PartPaths(string folder, string file) =>
        new[] { folder, folder == "checkpoints" ? "diffusion_models" : null, folder is "checkpoints" or "diffusion_models" ? "unet" : null, folder == "text_encoders" ? "clip" : null }
            .Where(d => d != null).Select(d => Path.Combine(ModelsDir, d, file));

    void BuildBundles()
    {
        BundlesList.Children.Clear();
        var ordered = bundles.OrderByDescending(b => b.Recommended).ThenBy(b => b.BuiltIn ? 0 : 1).ThenBy(b => b.Name).ToList();
        if (ordered.Any(b => b.Recommended)) BundlesHeading("Recommended");
        bool othersShown = false;
        foreach (var b in ordered)
        {
            if (!b.Recommended && !othersShown) { BundlesHeading("More bundles"); othersShown = true; }
            BundlesList.Children.Add(BundleCard(b));
        }
    }

    void BundlesHeading(string text) =>
        BundlesList.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 14, 0, 8) });

    UIElement Badge(string text, string brush) => new Border
    {
        Background = Brush(brush), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
    };

    UIElement BundleCard(Bundle b)
    {
        bool hidden = b.Adult && !settings.AdultAgreed;
        var installed = b.Files.Count(f => InstalledPath(f) != null);
        var missing = b.Files.Where(f => InstalledPath(f) == null).ToList();
        var stack = new StackPanel();

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = b.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (b.Recommended) title.Children.Add(Badge("Recommended", "Accent"));
        if (b.Adult) title.Children.Add(Badge("18+", "Danger"));
        stack.Children.Add(title);

        var facts = $"{(b.Author is { Length: > 0 } a ? "by " + a + " · " : "")}{b.Files.Count} file{(b.Files.Count == 1 ? "" : "s")} · {Size(b.Files.Sum(f => f.Size))}"
            + (missing.Count == 0 ? " · all installed" : installed > 0 ? $" · {installed} installed, {Size(missing.Sum(f => f.Size))} to download" : "")
            + $" · {b.Tab.Model}";
        stack.Children.Add(new TextBlock { Text = facts, Style = (Style)FindResource("Note"), Foreground = Brush("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (b.Description is { Length: > 0 })
            stack.Children.Add(new TextBlock { Text = b.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var settingsLine = $"{(b.Tab.Steps > 0 ? b.Tab.Steps + " steps · " : "")}CFG {b.Tab.Cfg:0.#} · {b.Tab.Sampler}/{b.Tab.Scheduler} · {b.Tab.Size}"
            + (b.Tab.Loras.Count(l => l.Name != "None") is int n and > 0 ? $" · {n} LoRA{(n == 1 ? "" : "s")}: " + string.Join(", ", b.Tab.Loras.Where(l => l.Name != "None").Select(l => $"{Path.GetFileNameWithoutExtension(l.Name)} {l.Strength:0.##}")) : "");
        stack.Children.Add(new TextBlock { Text = settingsLine, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        // the showcase and sample prompts, behind the 18+ gate for adult bundles
        var show = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var pics = new WrapPanel();
        foreach (var file in b.Showcase.Select(s => Path.Combine(b.Dir ?? "", s)).Where(File.Exists).Take(8))
        {
            var img = new Image { Width = 150, Height = 150, Stretch = Stretch.UniformToFill, Source = Thumb(file, 300) };
            var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 8, 8), Child = img, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Open the picture" };
            frame.MouseLeftButtonUp += (_, _) => { if (!(b.Adult && !settings.AdultAgreed)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); };
            pics.Children.Add(frame);
        }
        if (pics.Children.Count > 0) show.Children.Add(pics);
        foreach (var p in b.Prompts.Take(4))
            show.Children.Add(new TextBlock { Text = "“" + p + "”", Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (show.Children.Count > 0)
        {
            if (hidden)
            {
                show.Effect = new BlurEffect { Radius = 40 };
                show.IsHitTestVisible = false;
                var agree = new Button { Content = "I'm 18 or older · Show", Style = (Style)FindResource("Primary"), Height = 34, Padding = new Thickness(16, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Center };
                agree.Click += (_, _) => AgreeAdult();
                var gate = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                gate.Children.Add(new TextBlock { Text = "Adult content", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
                gate.Children.Add(new TextBlock { Text = "This bundle's pictures and prompts are for adults only.", Style = (Style)FindResource("Note"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10) });
                gate.Children.Add(agree);
                var layered = new Grid { MinHeight = 120 };
                layered.Children.Add(show);
                layered.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(8), Child = gate, Margin = new Thickness(0, 10, 0, 0) });
                stack.Children.Add(layered);
            }
            else stack.Children.Add(show);
        }

        var status = new TextBlock { Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        bool busy = settingUp.Contains(b.Id);
        var get = new Button { Content = busy ? "Setting up…" : missing.Count == 0 ? "Use" : "Get & set up", Style = (Style)FindResource("Ghost"), Height = 34, Padding = new Thickness(16, 0, 16, 0),
            IsEnabled = !busy, ToolTip = missing.Count == 0 ? "Open it as a tab" : $"Download {missing.Count} file{(missing.Count == 1 ? "" : "s")} ({Size(missing.Sum(f => f.Size))}), then open it as a tab" };
        get.Click += async (_, _) => await SetUpBundle(b, status);
        buttons.Children.Add(get);
        if (!b.BuiltIn)
        {
            var export = new Button { Content = "Export…", Style = (Style)FindResource("Ghost"), Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Save it as an .emberbundle file to share" };
            export.Click += (_, _) => ExportBundle(b);
            var remove = new Button { Content = "Remove", Style = (Style)FindResource("Ghost"), Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Remove the bundle from this list (its downloaded models stay installed)" };
            remove.Click += (_, _) => RemoveBundle(b);
            buttons.Children.Add(export);
            buttons.Children.Add(remove);
        }
        stack.Children.Add(buttons);
        stack.Children.Add(status);

        return new Border
        {
            Background = Brush("Raised"), BorderBrush = b.Recommended ? Brush("LineStrong") : Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = stack,
        };
    }

    static BitmapImage Thumb(string file, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;         // don't keep the file locked
            bmp.DecodePixelWidth = width;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    void AgreeAdult()
    {
        if (MessageBox.Show(this, "Some bundles show adult (sexual) pictures and prompts.\n\nBy continuing you confirm that you are 18 or older and want to see them.",
                "Adult content · 18+", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        settings.AdultAgreed = true;
        settings.Save();
        BuildBundles();
    }

    // ---------------- get & set up ----------------
    async Task SetUpBundle(Bundle b, TextBlock status)
    {
        if (!settingUp.Add(b.Id)) return;
        BuildBundles();
        try
        {
            var missing = b.Files.Where(f => InstalledPath(f) == null).ToList();
            for (int i = 0; i < missing.Count; i++)
            {
                var f = missing[i];
                BundlesNote.Text = $"{b.Name}: downloading {i + 1} of {missing.Count}, {f.Name} ({Size(f.Size)})… Progress shows on the Models page.";
                // the Models page's own entry when it has one, so its progress and Cancel show there; never the same file twice at once
                if (downloads.Keys.Any(k => k.File == f.File)) { BundlesNote.Text = $"{b.Name}: {f.Name} is already downloading on the Models page. Set up again when it's done."; return; }
                await Download(Catalog.FirstOrDefault(c => c.File == f.File && string.Equals(c.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    ?? new CatalogItem(f.Name, KindOf(f.Folder), f.Folder, f.File, f.Url, f.Size, f.Sha256, f.License, "From the bundle " + b.Name));
                if (InstalledPath(f) == null) { BundlesNote.Text = $"{b.Name}: {f.Name} didn't download. {ModelsNote.Text}"; return; }
            }
            bool Listed(string model) => imageModels.Contains(model) || videoModels.Contains(model);
            if (ready && !Listed(b.Tab.Model)) await LoadOptions();
            var tab = Clone(b.Tab);
            tab.Name = Unique(string.IsNullOrWhiteSpace(tab.Name) ? b.Name : tab.Name, null);
            tab.Loras ??= new();
            settings.Tabs.Add(tab);
            settings.Save();
            ShowTabs();
            if (ready && Listed(tab.Model))
            {
                OpenTab(tab);
                BundlesPanel.Visibility = Visibility.Collapsed;
                Note($"\"{b.Name}\" is set up and open as the tab \"{tab.Name}\"." + (b.Prompts.Count > 0 ? " Try one of its sample prompts." : ""));
                if (b.Prompts.Count > 0 && string.IsNullOrWhiteSpace(PromptBox.Text)) PromptBox.Text = b.Prompts[0];
            }
            else BundlesNote.Text = $"{b.Name}: all files are in. Its tab \"{tab.Name}\" opens once ComfyUI is ready.";
        }
        finally { settingUp.Remove(b.Id); BuildBundles(); }
    }

    // ---------------- make a bundle ----------------
    async void MakeBundle_Click(object sender, RoutedEventArgs e)
    {
        if (ModelBox.SelectedItem is not string model) { BundlesNote.Text = "Pick a model first: a bundle is made from the model and settings on screen."; return; }
        // the model's settings only: the prompt on screen stays out of a bundle that gets shared (its sample prompts come from its pictures)
        var tab = new ModelProfile { Name = ActiveTab?.Name ?? Path.GetFileNameWithoutExtension(model), Model = model };
        Capture(tab);
        tab.Boosters = Clone(CurrentBoosters);
        tab.TextEncoder = ProfileOf(model).TextEncoder;
        tab.Vae = ProfileOf(model).Vae;

        var selected = shots.Where(s => s.Selected).Select(s => s.FilePath).ToList();
        if (BundleDialog(tab.Name, selected) is not { } answer) return;
        var (name, author, about, adult, recommended, pictures) = answer;

        var prompts = pictures.Select(p => { try { return ReadMeta(p)?.Prompt; } catch { return null; } })
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().Take(4).ToList();
        var everything = string.Join("\n", new[] { name, about, tab.Negative }.Concat(prompts));
        if (SexualWithMinor(everything)) { BundlesNote.Text = MinorNote; return; }
        adult |= SexualWords.IsMatch(everything);       // sexual prompts or text always get the 18+ gate

        // the files it needs: the model, its LoRAs, and for model-only files the text encoder and VAE it runs with
        var needed = new List<(string Folder, string File)>();
        var modelFolder = PartPaths("diffusion_models", model).Any(File.Exists) ? "diffusion_models" : "checkpoints";     // diffusion_models or unet
        needed.Add((modelFolder, model));
        foreach (var l in tab.Loras.Where(l => l.Name is { Length: > 0 } and not "None")) needed.Add(("loras", l.Name));
        if (modelFolder == "diffusion_models" && FamilyOf(model) is string fam)
        {
            var family = fam == "wan_i2v" ? "wan" : fam;
            if ((tab.TextEncoder ?? TeFor(family, model)) is string te) needed.Add(("text_encoders", te));
            if ((tab.Vae ?? VaeFor(family, model)) is string vae) needed.Add(("vae", vae));
            if (family == "minimax_h3" && h3AudioVae != null) needed.Add(("vae", h3AudioVae));       // its sound has a VAE of its own
        }

        var files = new List<BundleFile>();
        foreach (var (folder, file) in needed.Distinct())
        {
            var path = PartPaths(folder, file).FirstOrDefault(File.Exists);
            if (path == null) { BundlesNote.Text = $"{file} isn't in the models folder, so it can't go in a bundle."; return; }
            BundlesNote.Text = $"Checking {file} ({Size(SizeOf(path))})…";
            var sha = await Task.Run(() => { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); });
            var source = await SourceOf(folder, file, sha, SizeOf(path));
            if (source == null) { BundlesNote.Text = $"No download source for {file}, so the bundle wasn't made."; return; }
            files.Add(source);
        }

        var b = new Bundle { Id = Guid.NewGuid().ToString("N")[..12], Name = name, Author = author, Description = about, Adult = adult, Recommended = recommended,
            Tab = tab, Files = files, Prompts = prompts };
        b.Tab.Name = name;
        b.Dir = Path.Combine(BundlesDir, b.Id);
        Directory.CreateDirectory(b.Dir);
        for (int i = 0; i < pictures.Count; i++)
        {
            var target = $"showcase{i + 1}{Path.GetExtension(pictures[i]).ToLowerInvariant()}";
            File.Copy(pictures[i], Path.Combine(b.Dir, target), true);
            b.Showcase.Add(target);
        }
        File.WriteAllText(Path.Combine(b.Dir, "bundle.json"), JsonSerializer.Serialize(b, bundleJson));
        LoadBundles();
        BuildBundles();
        BundlesNote.Text = $"Made the bundle \"{name}\" ({files.Count} files, {Size(files.Sum(f => f.Size))}). Press Export on it to share it.";
    }

    // where a file can be downloaded again: the free catalog, then Civitai by its hash, then a link the user pastes
    async Task<BundleFile> SourceOf(string folder, string file, string sha, long size)
    {
        if (Catalog.FirstOrDefault(c => c.Sha256 == sha) is { } c)
            return new BundleFile { Name = c.Name, Folder = folder, File = file, Url = c.Url, Size = size, Sha256 = sha, License = c.License };
        foreach (var host in new[] { "civitai.red", "civitai.com" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v1/model-versions/by-hash/{sha}");
                if (CivitaiKey is string key) req.Headers.Authorization = new("Bearer", key);
                using var res = await web.SendAsync(req);
                if (!res.IsSuccessStatusCode) continue;
                var ver = JsonNode.Parse(await res.Content.ReadAsStringAsync());
                var match = (ver?["files"] as JsonArray)?.FirstOrDefault(f => f?["hashes"]?["SHA256"]?.ToString().Equals(sha, StringComparison.OrdinalIgnoreCase) == true);
                if (match?["downloadUrl"]?.ToString() is not string url) continue;
                return new BundleFile { Name = $"{ver["model"]?["name"]} · {ver["name"]}", Folder = folder, File = file, Url = url, Size = size, Sha256 = sha,
                    License = $"{ver["baseModel"]} · Civitai" };
            }
            catch { /* offline or busy: ask for a link */ }
        }
        while (Ask($"Where can {file} be downloaded?", "Paste its Hugging Face or Civitai link") is string link)
        {
            try
            {
                var item = await FromLink(link.Trim());
                if (item.Sha256 != null && item.Sha256 != sha) { BundlesNote.Text = $"That link is a different file than your {file}. Try another."; continue; }
                return new BundleFile { Name = item.Name, Folder = folder, File = file, Url = item.Url, Size = size, Sha256 = sha, License = item.License };
            }
            catch (Exception ex) { BundlesNote.Text = $"That link didn't work: {ex.Message}"; }
        }
        return null;
    }

    // name, author, description, adult, recommended and showcase pictures; null when cancelled
    // a borderless card like the password window: the app's own inputs, switches and buttons on the dark surface
    (string, string, string, bool, bool, List<string>)? BundleDialog(string name, List<string> selected)
    {
        TextBox Field(string text, string hint, bool multi = false) => new()
        {
            Text = text, Tag = hint, Style = (Style)FindResource("Input"), Height = multi ? 70 : 36,
            Padding = multi ? new Thickness(10, 8, 10, 8) : new Thickness(10, 0, 10, 0), VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Brush("Ink"), Margin = new Thickness(0, 10, 0, 4) };
        TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        CheckBox Toggle(string text) => new() { Content = text, Style = (Style)FindResource("Switch"), Foreground = Brush("Ink"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
        var nameBox = Field(name, "A short name");
        var authorBox = Field(settings.BundleAuthor ?? "", "Your name or handle");
        var aboutBox = Field("", "What it's good at, and anything to know before using it", true);
        var adultBox = Toggle("Adult content (18+)");
        var recBox = Toggle("Recommended");
        recBox.Margin = new Thickness(0, 12, 0, 0);
        var pictures = selected.Where(p => ShowcaseTypes.Contains(Path.GetExtension(p).ToLowerInvariant())).Take(8).ToList();
        var picNote = new TextBlock { Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        void ShowPics() => picNote.Text = pictures.Count == 0 ? "No showcase pictures yet. Pick a few made with this setup (their prompts become the bundle's sample prompts)."
            : $"{pictures.Count} picture{(pictures.Count == 1 ? "" : "s")}: " + string.Join(", ", pictures.Select(Path.GetFileName));
        ShowPics();
        var pick = new Button { Content = "Choose showcase pictures…", Style = (Style)FindResource("Ghost"), Height = 32, Padding = new Thickness(12, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Showcase pictures (up to 8)", Multiselect = true, Filter = "Pictures|*.png;*.jpg;*.jpeg;*.webp",
                InitialDirectory = Directory.Exists(Path.Combine(Output, "forge")) ? Path.Combine(Output, "forge") : Output };
            if (dlg.ShowDialog(Window.GetWindow(pick)) == true) { pictures = dlg.FileNames.Take(8).ToList(); ShowPics(); }
        };

        // header: icon and title, a close button in the corner
        var close = new Button { Content = "", Style = (Style)FindResource("IconBtn"), Focusable = false, ToolTip = "Close", Margin = new Thickness(0, -6, -12, -6), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close");
        DockPanel.SetDock(close, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Children = { close, new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Foreground = Brush("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0) },
            new TextBlock { Text = "Make a bundle from this setup", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush("Ink"), VerticalAlignment = VerticalAlignment.Center },
        } } } };

        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Ghost"), Height = 36, Width = 84, IsCancel = true };
        var ok = new Button { Content = "Make bundle", Style = (Style)FindResource("Primary"), Height = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var panel = new StackPanel();
        foreach (var el in new UIElement[] { header, Label("Name"), nameBox, Label("Made by"), authorBox, Label("What it does"), aboutBox, Label("Showcase"), picNote, pick,
                     new Border { Height = 1, Background = Brush("Line"), Margin = new Thickness(0, 18, 0, 14) },
                     adultBox, Hint("Pictures and prompts stay hidden until the viewer confirms their age."), recBox, Hint("List it at the top of the bundles page."),
                     new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0), Children = { cancel, ok } } })
            panel.Children.Add(el);
        // the margin leaves room for the shadow inside the transparent window
        var card = new Border
        {
            Margin = new Thickness(24), CornerRadius = new CornerRadius(14), Background = Brush("Surface"), BorderBrush = Brush("LineStrong"), BorderThickness = new Thickness(1),
            Padding = new Thickness(26, 20, 26, 22), Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.5 }, Child = panel,
        };
        var w = new Window
        {
            Title = "Make a bundle from this setup", Owner = this, Width = 560, SizeToContent = SizeToContent.Height, Content = card,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Foreground = Brush("Ink"), UseLayoutRounding = true,
        };
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        card.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) w.DragMove(); };
        close.Click += (_, _) => w.DialogResult = false;
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) w.DialogResult = true; else nameBox.Focus(); };
        w.Loaded += (_, _) => { nameBox.Focus(); nameBox.CaretIndex = nameBox.Text.Length; };
        if (w.ShowDialog() != true) return null;
        settings.BundleAuthor = authorBox.Text.Trim();
        settings.Save();
        return (nameBox.Text.Trim(), authorBox.Text.Trim(), aboutBox.Text.Trim(), adultBox.IsChecked == true, recBox.IsChecked == true, pictures);
    }

    // ---------------- share ----------------
    void ExportBundle(Bundle b)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export bundle", FileName = Regex.Replace(b.Name, @"[\\/:*?""<>|]", "_"), DefaultExt = ".emberbundle", Filter = "Ember bundle|*.emberbundle" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            ZipFile.CreateFromDirectory(b.Dir, dlg.FileName);
            BundlesNote.Text = $"Exported \"{b.Name}\". Anyone with Ember can import it and get the same setup in one click.";
        }
        catch (Exception ex) { BundlesNote.Text = "Couldn't export it: " + ex.Message; }
    }

    void ImportBundle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import bundle", Filter = "Ember bundle|*.emberbundle" };
        if (dlg.ShowDialog(this) != true) return;
        var temp = Path.Combine(Path.GetTempPath(), "ember-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(dlg.FileName, temp);      // refuses entries that point outside the folder
            var b = JsonSerializer.Deserialize<Bundle>(File.ReadAllText(Path.Combine(temp, "bundle.json"))) ?? throw new Exception("it has no bundle.json.");
            if (b.Tab?.Model is not { Length: > 0 } || string.IsNullOrWhiteSpace(b.Name)) throw new Exception("it doesn't say which model it uses.");
            // only files Ember knows how to place, from the two sites it downloads from, each with its checksum
            foreach (var f in b.Files)
            {
                if (!ModelFolders.Contains(f.Folder)) throw new Exception($"it puts a file in an unknown folder ({f.Folder}).");
                if (f.File != Path.GetFileName(f.File) || !(f.File.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || f.File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception($"{f.File} isn't a plain .safetensors or .gguf file name.");
                if (!Uri.TryCreate(f.Url, UriKind.Absolute, out var u) || u.Scheme != "https" || !(u.Host is "huggingface.co" or "www.huggingface.co" || IsCivitai(u)))
                    throw new Exception($"{f.File} doesn't come from Hugging Face or Civitai.");
                if (f.Sha256 is not { Length: 64 } || !Regex.IsMatch(f.Sha256, "^[0-9a-fA-F]+$")) throw new Exception($"{f.File} has no checksum.");
                f.Sha256 = f.Sha256.ToLowerInvariant();
            }
            var text = string.Join("\n", new[] { b.Name, b.Description, b.Tab.Negative, b.Tab.Prompt }.Concat(b.Prompts ?? new()));   // its tab's prompt goes in the prompt box too
            if (SexualWithMinor(text)) throw new Exception(MinorNote);
            b.Adult |= SexualWords.IsMatch(text);
            b.Showcase = (b.Showcase ?? new()).Where(s => s == Path.GetFileName(s) && ShowcaseTypes.Contains(Path.GetExtension(s).ToLowerInvariant()) && File.Exists(Path.Combine(temp, s))).ToList();
            b.Prompts ??= new();
            b.Tab.Loras ??= new();
            b.Id = Guid.NewGuid().ToString("N")[..12];
            var dir = Path.Combine(BundlesDir, b.Id);
            Directory.CreateDirectory(dir);
            foreach (var s in b.Showcase) File.Copy(Path.Combine(temp, s), Path.Combine(dir, s));
            File.WriteAllText(Path.Combine(dir, "bundle.json"), JsonSerializer.Serialize(b, bundleJson));
            LoadBundles();
            BuildBundles();
            BundlesNote.Text = $"Imported \"{b.Name}\". Press Get & set up to download what it needs and open it.";
        }
        catch (Exception ex) { BundlesNote.Text = "Couldn't import that bundle: " + ex.Message; }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    void RemoveBundle(Bundle b)
    {
        if (MessageBox.Show(this, $"Remove the bundle \"{b.Name}\"? Models it downloaded stay installed, and so does its tab.", "Ember", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try { Directory.Delete(b.Dir, true); } catch (Exception ex) { BundlesNote.Text = "Couldn't remove it: " + ex.Message; return; }
        LoadBundles();
        BuildBundles();
    }
}
