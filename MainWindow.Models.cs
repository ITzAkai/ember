// The Models page: what's installed in ComfyUI\models, and a short catalog of free, open-licence models to add.
// Downloads stream to a .part file, are checked against the publisher's SHA-256, and only then take their real name.
// A pasted Hugging Face or Civitai link becomes one more catalog item, with the folder worked out from the file itself.
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImageForge;

record CatalogItem(string Name, string Kind, string Folder, string File, string Url, long Size, string Sha256, string License, string About);

public partial class MainWindow
{
    static string ModelsDir => Path.Combine(Root, "ComfyUI", "models");   // a property: static fields in other files may run before Root is set
    static readonly string[] ModelFolders = { "checkpoints", "diffusion_models", "text_encoders", "vae", "loras", "upscale_models", "onnx" };

    static readonly CatalogItem[] Catalog =
    {
        new("DreamShaper XL Turbo v2.1", "Model", "checkpoints", "DreamShaperXL_Turbo_v2_1.safetensors",
            "https://huggingface.co/Lykon/dreamshaper-xl-v2-turbo/resolve/main/DreamShaperXL_Turbo_v2_1.safetensors", 6939220250,
            "4496b36d48bfd7cfe4e5dbce3485db567bcefa2bef7238d290dbd45612125083", "OpenRAIL++", "Fast all-rounder: art, photos and logos in about 15 seconds."),
        new("RealVisXL V5 Lightning", "Model", "checkpoints", "RealVisXL_V5.0_Lightning_fp16.safetensors",
            "https://huggingface.co/SG161222/RealVisXL_V5.0_Lightning/resolve/main/RealVisXL_V5.0_Lightning_fp16.safetensors", 6938065512,
            "fabcadd9330dcc4f9702063428d40b9d4d07168d8acefc819b8d1d9db466b3ec", "OpenRAIL++", "Photo-realistic people and places, as fast as Turbo."),
        new("Krea 2 Turbo (fp8)", "Model", "diffusion_models", "krea2_turbo_fp8_scaled.safetensors",
            "https://huggingface.co/Comfy-Org/Krea-2/resolve/main/diffusion_models/krea2_turbo_fp8_scaled.safetensors", 13141730784,
            "eb4dd8c612cfd10f64f25b057e6e6bbcb5737c94a7372177e456dbf7579502f1", "Krea 2 Community", "Realistic photos and art in 8 steps. Needs the two Krea 2 parts below."),
        new("Krea 2 text encoder", "Text encoder", "text_encoders", "qwen3vl_4b_fp8_scaled.safetensors",
            "https://huggingface.co/Comfy-Org/Krea-2/resolve/main/text_encoders/qwen3vl_4b_fp8_scaled.safetensors", 5242467968,
            "54bd5144df0bbc25dd6ccadfcb826b521445a1b06ae5a42570bdd2974ca87094", "Apache 2.0", "Reads the prompt for every Krea 2 model, including fine-tunes."),
        new("Krea 2 VAE", "VAE", "vae", "qwen_image_vae.safetensors",
            "https://huggingface.co/Comfy-Org/Krea-2/resolve/main/vae/qwen_image_vae.safetensors", 253806246,
            "a70580f0213e67967ee9c95f05bb400e8fb08307e017a924bf3441223e023d1f", "Apache 2.0", "Turns Krea 2's output into pixels."),
        new("Wan 2.1 text-to-video 1.3B", "Video model", "diffusion_models", "wan2.1_t2v_1.3B_fp16.safetensors",
            "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/diffusion_models/wan2.1_t2v_1.3B_fp16.safetensors", 2838303560,
            "be531024cd9018cb5b48c40cfbb6a6191645b1c792eb8bf4f8c1c6e10f924dc5", "Apache 2.0", "Short video clips from a prompt, for the Video tab. Needs the two Wan parts below."),
        new("Wan 2.1 picture-to-video 1.3B (Fun InP)", "Video model", "diffusion_models", "wan2.1_fun_inp_1.3B_bf16.safetensors",
            "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/diffusion_models/wan2.1_fun_inp_1.3B_bf16.safetensors", 3128957992,
            "8495d2b1673ffb18abb548a64ff3b0e4bd367734f653096f7a8a3ad46954d511", "Apache 2.0", "Starts a short clip from your picture. Works with Wan 1.3B LoRAs."),
        new("Wan text encoder (umT5-XXL)", "Text encoder", "text_encoders", "umt5_xxl_fp8_e4m3fn_scaled.safetensors",
            "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", 6735906897,
            "c3355d30191f1f066b26d93fba017ae9809dce6c627dda5f6a66eaa651204f68", "Apache 2.0", "Reads the prompt for every Wan 2.1 video model."),
        new("Wan 2.1 VAE", "VAE", "vae", "wan_2.1_vae.safetensors",
            "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/vae/wan_2.1_vae.safetensors", 253815318,
            "2fc39d31359a4b0a64f55876d8ff7fa8d780956ae2cb13463b0223e15148976b", "Apache 2.0", "Turns Wan's output into video frames."),
        new("Stable Diffusion XL base 1.0", "Model", "checkpoints", "sd_xl_base_1.0.safetensors",
            "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_base_1.0.safetensors", 6938078334,
            "31e35c80fc4829d14f90153f4c74cd59c90b779f6afe05a74cd6120b893f7e5b", "OpenRAIL++", "The original SDXL. Slower; needed for the inpainting model's text and colours."),
        new("SDXL inpainting 0.1", "Inpainting", "diffusion_models", "sdxl_inpainting_0.1_fp16.safetensors",
            "https://huggingface.co/diffusers/stable-diffusion-xl-1.0-inpainting-0.1/resolve/main/unet/diffusion_pytorch_model.fp16.safetensors", 5135178560,
            "6470840731e98cc16713ddf3ac7ee458c9fdbcb881a98c6727cd4a938f227d3f", "OpenRAIL++", "Cleaner Remove, painted edits and expansions. Uses SDXL base alongside."),
        new("Pixel Art XL", "LoRA", "loras", "pixel-art-xl.safetensors",
            "https://huggingface.co/nerijs/pixel-art-xl/resolve/main/pixel-art-xl.safetensors", 170543052,
            "4234637cb80c998f41e348e6a6cb6bc20d8d038b2b0f256b6129b3b5e353eef7", "CreativeML OpenRAIL-M", "Turns on the Pixel art style."),
        new("Real-ESRGAN x4plus", "Upscaler", "upscale_models", "RealESRGAN_x4plus.pth",
            "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth", 67040989,
            "4fa0d38905f75ac06eb49a7951b426670021be3018265fd191d2125df9d682f1", "BSD-3-Clause", "Upscale 2× and 4×."),
        new("BiRefNet lite", "Background", "onnx", "BiRefNet_lite.onnx",
            "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx", 224005088,
            "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", "MIT", "Cut out: keeps the subject, removes the background."),
    };

    readonly Dictionary<CatalogItem, CancellationTokenSource> downloads = new();
    readonly Dictionary<CatalogItem, (ProgressBar Bar, TextBlock Text)> downloadUi = new();

    void Models_Click(object sender, RoutedEventArgs e) { BuildModels(); ModelsPanel.Visibility = Visibility.Visible; }

    void OpenModelsFolder_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", ModelsDir);

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";
    static string PathOf(CatalogItem m) => Path.Combine(ModelsDir, m.Folder, m.File);

    // an installed file and what it's for, worked out from its folder and its contents
    record Installed(string Path, string Folder, string Name, string Group, string Family, string About, string License);

    static readonly (string Group, string Note)[] Groups =
    {
        ("Picture models", "Press Use, or pick one under Advanced settings."),
        ("Video models", "For the Video tab."),
        ("LoRA add-ons", "Small trained styles or subjects. Each fits one model family and only shows up with it."),
        ("Parts", "Text encoders read your prompt and VAEs turn the result into pixels: a model can't run without its two parts."),
        ("Tools", "Used by Expand, Remove, Upscale and Cut out."),
        ("Other files", "Ember can't tell what these are for."),
    };

    // friendlier names and notes for well-known files that aren't in the catalog (matched on part of the file name)
    static readonly (string Part, string Name, string About)[] KnownModels =
    {
        ("qwen_image_2.1", "Qwen-Image 2.1", "Lettering, logos, and edits by instruction."),
        ("noctq", "Noct Q Anime", "Anime illustration, uncensored. Start prompts with \"An anime illustration of…\"."),
        ("fineporn", "FinePorn v5", "Realistic photos and art, uncensored (a Krea 2 fine-tune)."),
        ("wan_1.3b_", "NSFW Wan 1.3B", "Short clips, uncensored (a Wan 2.1 fine-tune)."),
        ("fun_inp", "Wan 2.1 picture-to-video", "Starts a clip from your picture. Works with Wan 1.3B LoRAs."),
        ("10eros", "10Eros Max", "Video with its own sound, uncensored (a MiniMax H3 fine-tune)."),
        ("anima", "Anima", "Anime and illustration. Tags (1girl, masterpiece) or plain sentences both work."),
    };

    static string FamilyLabel(string family) => family switch
    {
        "krea2" => "Krea 2", "qwen_image" => "Qwen-Image", "wan" => "Wan 2.1", "anima" => "Anima", "minimax_h3" => "MiniMax H3", "sdxl" => "SDXL", _ => null,
    };

    Installed Inspect(string folder, string path)
    {
        var file = Path.GetFileName(path);
        var known = Catalog.FirstOrDefault(c => c.Folder == folder && c.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        bool Has(string part) => file.Contains(part, StringComparison.OrdinalIgnoreCase);
        (string Group, string Family, string About) info = folder switch
        {
            "checkpoints" when Has("sd_xl_base") => ("Tools", "sdxl", "Lends the inpainting model its text encoder and VAE."),
            "checkpoints" when !IsFullCheckpoint(file) => ("Other files", null,
                "A model-only file in the checkpoints folder, so it can't be used from here: it belongs in diffusion_models."),
            "checkpoints" => ("Picture models", "sdxl", "Works with SDXL LoRAs."),
            "diffusion_models" when Has("inpaint") => ("Tools", "sdxl", "Paints in what Expand, Remove and painted edits ask for."),
            "diffusion_models" => FamilyOf(file) switch
            {
                "wan" => ("Video models", "wan", "Short video clips."),
                "wan_i2v" => ("Video models", "wan", "Starts a clip from your picture."),
                "minimax_h3" => ("Video models", "minimax_h3", "Video with its own sound."),
                "krea2" => ("Picture models", "krea2", "Realistic photos and art."),
                "qwen_image" => ("Picture models", "qwen_image", "Lettering, logos, and edits by instruction."),
                "anima" => ("Picture models", "anima", "Anime and illustration."),
                _ => ("Other files", null, null),
            },
            "text_encoders" or "vae" => ("Parts", PartFamily(folder, file), null),
            "loras" => ("LoRA add-ons", LoraFamily(path), null),
            "upscale_models" => ("Tools", null, "Used by Upscale (2× and 4×)."),
            _ => ("Tools", null, "Used by Cut out (removes the background)."),
        };
        var named = folder == "diffusion_models" ? KnownModels.FirstOrDefault(k => Has(k.Part)) : default;
        var name = known?.Name ?? named.Name
            ?? (info.Group == "Parts" && info.Family != null
                ? $"{FamilyLabel(info.Family)} {(folder == "vae" ? (PartKind(folder, file) == "h3_audio_vae" ? "audio VAE" : "VAE") : "text encoder")}" : Path.GetFileNameWithoutExtension(file).Replace('_', ' '));
        var about = known?.About ?? named.About ?? info.About
            ?? (folder == "loras" ? (info.Family == null ? "Can't tell which models it fits, so it's listed with all of them." : $"For {FamilyLabel(info.Family)} models.") : null);
        return new Installed(path, folder, name, info.Group, info.Family, about, known?.License);
    }

    bool MissingParts(string family) => family switch
    {
        "qwen_image" => qwenTe == null || qwenVae == null, "krea2" => kreaTe == null || kreaVae == null, "wan" => wanTe == null || wanVae == null,
        "anima" => animaTe == null || kreaVae == null, "minimax_h3" => h3Te == null || h3Vae == null || h3AudioVae == null, _ => false,
    };

    void BuildModels()
    {
        ModelsList.Children.Clear();
        downloadUi.Clear();
        var files = ModelFolders.Where(f => Directory.Exists(Path.Combine(ModelsDir, f)))
            .SelectMany(f => Directory.GetFiles(Path.Combine(ModelsDir, f)).Select(p => (Folder: f, Path: p)))
            .Where(x => x.Path.EndsWith(".safetensors") || x.Path.EndsWith(".gguf") || x.Path.EndsWith(".ckpt") || x.Path.EndsWith(".pth") || x.Path.EndsWith(".onnx"))
            .Select(x => Inspect(x.Folder, x.Path)).ToList();
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(ModelsDir));
            ModelsSummary.Text = $"{files.Count} files, {Size(files.Sum(f => new FileInfo(f.Path).Length))} · {Size(drive.AvailableFreeSpace)} free on {drive.Name.TrimEnd('\\')}";
        }
        catch { ModelsSummary.Text = ""; }

        if (linked != null && !File.Exists(PathOf(linked)))
        {
            Section("From your link");
            ModelsList.Children.Add(ModelRow(linked.Name, linked.Kind, linked.About, $"{Size(linked.Size)} · {linked.License} · goes in {linked.Folder}", null, linked));
        }

        // which installed models lean on a part: shown on it, and asked about before it goes
        string Users(string family) => string.Join(", ", files.Where(f => f.Family == family && f.Group is "Picture models" or "Video models").Select(f => f.Name));
        foreach (var (group, note) in Groups)
        {
            var list = files.Where(f => f.Group == group).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) continue;
            Section($"{group} · {list.Count}", note);
            foreach (var f in list)
            {
                var file = Path.GetFileName(f.Path);
                string about = f.About, warn = null, page = null, title = f.Name;
                bool inUse = false;
                Action use = null;
                if (group == "Parts")
                {
                    var users = Users(f.Family);
                    if (file.Contains("qwen_image_vae", StringComparison.OrdinalIgnoreCase))             // Krea 2 and Anima share this VAE
                        users = string.Join(", ", new[] { users, Users("anima") }.Where(x => x != ""));
                    if (f.Family == "qwen_image" && f.Folder == "text_encoders") users = users == "" ? "Improve prompt, Describe image" : users + ", Improve prompt, Describe image";
                    about = users == "" ? "No installed model uses it." : $"Needed by {users}.";
                    warn = users == "" ? null : $"{users} won't work without it.";
                }
                else if (group == "Tools" && f.Folder is "checkpoints" or "diffusion_models") warn = "Expand, Remove and painted edits need it.";
                else if (group is "Picture models" or "Video models")
                {
                    bool video = group == "Video models";
                    var item = f.Folder == "checkpoints" ? file : separate.FirstOrDefault(kv => kv.Value.Unet == file).Key;
                    if (item == null && MissingParts(f.Family)) about += " Missing its text encoder or VAE: get them under Get more.";
                    inUse = item != null && video == showingVideoModels && ModelBox.SelectedItem as string == item;
                    if (item != null) use = () => UseModel(item, video);
                }
                else if (group == "LoRA add-ons")
                {
                    if (loraInfo.GetValueOrDefault(file) is { } li)        // what it does, from Civitai or the file itself
                    {
                        about = string.Join(" ", new[] { li.Trigger.Length > 0 ? $"Trigger words: {string.Join(", ", li.Trigger)}." : null, li.Description ?? about }.Where(x => x != null));
                        page = li.Url;
                        title = li.Name ?? f.Name;
                    }
                    inUse = loraRows.Any(r => (r.Box?.SelectedItem as string) == file);
                    var family = f.Family;
                    use = () => UseLora(file, family);
                }
                ModelsList.Children.Add(ModelRow(title, KindOf(f.Folder), about ?? "", f.License == null ? $"{Size(new FileInfo(f.Path).Length)} · {file}" : $"{Size(new FileInfo(f.Path).Length)} · {f.License}",
                    f.Path, null, FamilyLabel(f.Family), inUse, use, warn, page));
            }
        }

        var more = Catalog.Where(c => !File.Exists(PathOf(c))).ToList();
        if (more.Count > 0) Section("Get more", "Free, open-licence downloads, each checked against its published checksum.");
        foreach (var c in more) ModelsList.Children.Add(ModelRow(c.Name, c.Kind, c.About, $"{Size(c.Size)} · {c.License} · {new Uri(c.Url).Host}", null, c));
    }

    // Use: pick the model (switching between pictures and video if needed)
    void UseModel(string item, bool video)
    {
        CloseOverlays_Click(null, null);
        if (video) VideoMode.IsChecked = true;
        else if (VideoOn) CreateMode.IsChecked = true;
        PickModel(item);
        AdvancedToggle.IsChecked = true;
        Note($"Now using {Path.GetFileNameWithoutExtension(item)}.");
    }

    // a LoRA only works with its own family, so a model of that family comes first when the current one doesn't fit
    void UseLora(string lora, string family)
    {
        if (family != null && family != ModelFamily)
        {
            bool video = IsVideoFamily(family);
            var model = family == "sdxl" ? imageModels.FirstOrDefault(i => !separate.ContainsKey(i))
                : (video ? videoModels : imageModels).FirstOrDefault(i => separate.TryGetValue(i, out var s) && s.Family == family);
            if (model == null) { ModelsNote.Text = $"No {FamilyLabel(family)} model is installed for this LoRA."; return; }
            UseModel(model, video);
        }
        else CloseOverlays_Click(null, null);

        var existing = loraRows.FirstOrDefault(r => (r.Box?.SelectedItem as string) == lora);
        if (existing == null)
        {
            var empty = loraRows.FirstOrDefault(r => (r.Box?.SelectedItem as string ?? "None") == "None");
            if (empty != null)
                PickLora(empty.Box, lora);
            else
                CreateLoraRow(lora, 0.8);
        }
        AdvancedToggle.IsChecked = true;
        Note($"LoRA add-on on: {Path.GetFileNameWithoutExtension(lora)}.");
    }

    static string KindOf(string folder) => folder switch
    {
        "checkpoints" => "Model", "diffusion_models" => "Diffusion model", "text_encoders" => "Text encoder", "vae" => "VAE",
        "loras" => "LoRA", "upscale_models" => "Upscaler", _ => "Background",
    };

    void Section(string title, string note = null)
    {
        ModelsList.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading"), Foreground = Brush("Accent"), Margin = new Thickness(0, 16, 0, note == null ? 8 : 2) });
        if (note != null) ModelsList.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
    }

    // one card: name, kind, family and In use; what it's for; size and licence. Use / Show / Remove when installed, Download (or progress) when not
    FrameworkElement ModelRow(string name, string kind, string about, string facts, string installedPath, CatalogItem toGet,
                              string family = null, bool inUse = false, Action use = null, string removeWarning = null, string page = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        Border Chip(string text, string colour) => new()
        {
            Background = Brush("Raised2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontSize = 11, Foreground = Brush(colour) },
        };
        title.Children.Add(Chip(kind, "Muted"));
        if (family != null) title.Children.Add(Chip(family, "Accent"));
        if (inUse) title.Children.Add(Chip("In use", "Ok"));
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(title);
        left.Children.Add(new TextBlock { Text = about, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        left.Children.Add(new TextBlock { Text = facts, FontSize = 11.5, Foreground = Brush("Faint"), Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Button Btn(string text, string style, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Style = (Style)FindResource(style), Height = 32, Margin = new Thickness(6, 0, 0, 0), FontSize = 13 };
            b.Click += click;
            return b;
        }

        if (installedPath != null)
        {
            if (use != null) { var b = Btn(inUse ? "In use" : "Use", "Ghost", (_, _) => use()); b.IsEnabled = !inUse; right.Children.Add(b); }
            if (page != null) right.Children.Add(Btn("Page", "Ghost", (_, _) => OpenPage(page)));
            right.Children.Add(Btn("Show", "Ghost", (_, _) => Process.Start("explorer.exe", $"/select,\"{installedPath}\"")));
            right.Children.Add(Btn("Remove", "Ghost", (_, _) => RemoveModel(name, installedPath, removeWarning)));
        }
        else if (downloads.ContainsKey(toGet))
        {
            var progress = new StackPanel { Width = 170, VerticalAlignment = VerticalAlignment.Center };
            var bar = new ProgressBar { Maximum = 1 };
            var text = new TextBlock { Style = (Style)FindResource("Note"), FontSize = 11.5, Margin = new Thickness(0, 5, 0, 0), Text = "Starting…" };
            progress.Children.Add(bar);
            progress.Children.Add(text);
            right.Children.Add(progress);
            right.Children.Add(Btn("Cancel", "Ghost", (_, _) => downloads[toGet].Cancel()));
            downloadUi[toGet] = (bar, text);
        }
        else right.Children.Add(Btn("Download", "Primary", (_, _) => _ = Download(toGet)));

        return new Border
        {
            Background = Brush("Raised"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Child = grid,
        };
    }

    async Task Download(CatalogItem m)
    {
        var dest = PathOf(m);
        var part = dest + ".part";
        var cts = new CancellationTokenSource();
        downloads[m] = cts;
        BuildModels();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var url = IsCivitai(new Uri(m.Url)) ? await CivitaiFileUrl(m.Url, cts.Token) : m.Url;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new Exception("the site wants you to sign in for this file; download it in your browser instead");
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var clock = Stopwatch.StartNew();
            var shown = TimeSpan.Zero;
            long done = 0;
            await using (var from = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var to = File.Create(part))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = await from.ReadAsync(buf, cts.Token)) > 0)
                {
                    await to.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    hash.AppendData(buf, 0, n);
                    done += n;
                    if (clock.Elapsed - shown > TimeSpan.FromMilliseconds(300) && downloadUi.TryGetValue(m, out var ui))
                    {
                        shown = clock.Elapsed;
                        double speed = done / Math.Max(0.1, clock.Elapsed.TotalSeconds);
                        ui.Bar.Value = (double)done / m.Size;
                        ui.Text.Text = $"{done * 100 / m.Size}% · {Size((long)speed)}/s · {TimeSpan.FromSeconds((m.Size - done) / Math.Max(1, speed)):m\\:ss} left";
                    }
                }
            }
            if (downloadUi.TryGetValue(m, out var check)) check.Text.Text = "Checking…";
            if (m.Sha256 != null && Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != m.Sha256)
                throw new Exception("it didn't match its published checksum, so it was thrown away");
            File.Move(part, dest, true);
            // Civitai calls some model-only files "Checkpoint": one without its own text encoder and VAE goes where ComfyUI loads those
            if (m.Folder == "checkpoints" && !IsFullCheckpoint(m.File))
            {
                var moved = Path.Combine(ModelsDir, "diffusion_models", m.File);
                File.Move(dest, moved, true);
            }
            if (m == linked) linked = null;
            ModelsNote.Text = $"{m.Name} is installed.";
            if (ready) await LoadOptions();          // ComfyUI lists new files the next time it is asked
        }
        catch (OperationCanceledException) { ModelsNote.Text = $"{m.Name}: download cancelled."; }
        catch (Exception ex) { ModelsNote.Text = $"{m.Name}: download failed: {ex.Message}"; }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }   // our own half-finished file
            downloads.Remove(m);
            BuildModels();
        }
    }

    // ---------------- a model from a link ----------------
    CatalogItem linked;     // what the pasted link points to: shown with its own Download button
    static readonly HttpClient web = new() { Timeout = TimeSpan.FromSeconds(30) };

    void LinkBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) CheckLink_Click(sender, e); }

    async void CheckLink_Click(object sender, RoutedEventArgs e)
    {
        var link = LinkBox.Text.Trim();
        if (link == "") { ModelsNote.Text = "Paste a link to a model on Hugging Face or Civitai first."; return; }
        LinkBtn.IsEnabled = false;
        ModelsNote.Text = "Looking up the link…";
        try
        {
            linked = await FromLink(link);
            ModelsNote.Text = File.Exists(PathOf(linked)) ? $"{linked.File} is already installed (in {linked.Folder})."
                : $"Found {linked.File}, {Size(linked.Size)}. Press Download under \"From your link\".";
            BuildModels();
        }
        catch (Exception ex) { ModelsNote.Text = "Couldn't use that link: " + ex.Message; }
        finally { LinkBtn.IsEnabled = true; }
    }

    static bool IsCivitai(Uri uri) => uri.Host.Replace("www.", "") is "civitai.com" or "civitai.red" or "civitai.green";

    static async Task<CatalogItem> FromLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new Exception("paste the whole link, starting with https://");
        if (uri.Host is "huggingface.co" or "www.huggingface.co") return await FromHuggingFace(uri);
        if (IsCivitai(uri)) return await FromCivitai(uri);
        throw new Exception("only Hugging Face and Civitai links work here.");
    }

    // huggingface.co/<owner>/<repo>[/blob|resolve/<revision>/<file>]: a page with exactly one model file works too
    static async Task<CatalogItem> FromHuggingFace(Uri uri)
    {
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0] is "datasets" or "spaces") throw new Exception("that isn't a model page.");
        var repo = $"{parts[0]}/{parts[1]}";
        string rev = "main", file = null;
        if (parts.Length >= 5 && parts[2] is "blob" or "resolve") { rev = parts[3]; file = Uri.UnescapeDataString(string.Join('/', parts[4..])); }
        var info = JsonNode.Parse(await web.GetStringAsync($"https://huggingface.co/api/models/{repo}/revision/{rev}?blobs=true"));
        if (info?["gated"] is JsonNode g && g.ToString() != "false") throw new Exception("this model needs a Hugging Face sign-in; download it in your browser instead.");
        var files = info["siblings"].AsArray().Where(s => s["rfilename"].ToString().EndsWith(".safetensors")).ToList();
        var pick = file != null ? files.FirstOrDefault(s => s["rfilename"].ToString() == file) : files.Count == 1 ? files[0] : null;
        if (pick == null)
            throw new Exception(file != null ? "that file isn't a .safetensors model." : files.Count == 0 ? "there's no .safetensors model on that page."
                : $"that page has {files.Count} model files: open the one you want and copy its link.");
        var path = pick["rfilename"].ToString();
        var url = $"https://huggingface.co/{repo}/resolve/{rev}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        // Comfy-Org and others sort files into folders named like ComfyUI's; otherwise the file's own tensor names tell
        var folder = path.Split('/')[0] switch
        {
            "checkpoints" => "checkpoints", "diffusion_models" or "unet" => "diffusion_models", "text_encoders" or "clip" => "text_encoders",
            "vae" => "vae", "loras" => "loras", _ => FolderFor(await HeaderKeys(url)),
        } ?? throw new Exception("Ember can't tell what kind of model that is.");
        return new CatalogItem(Path.GetFileNameWithoutExtension(path), KindOf(folder), folder, Path.GetFileName(path), url,
            pick["size"].GetValue<long>(), pick["lfs"]?["sha256"]?.ToString(), $"{repo} · Hugging Face", "From your link.");
    }

    // civitai.com|red|green/models/<id>[?modelVersionId=<v>] or /api/download/models/<v>[?fileId=<f>]; Civitai says what kind of model it is
    static async Task<CatalogItem> FromCivitai(Uri uri)
    {
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string version = query["modelVersionId"], fileId = query["fileId"], modelName = null, modelType = null;
        if (parts is ["api", "download", "models", var v, ..]) version = v;
        JsonNode ver;
        if (version != null) ver = JsonNode.Parse(await web.GetStringAsync($"https://{uri.Host}/api/v1/model-versions/{version}"));
        else if (parts is ["models", var id, ..])
        {
            var model = JsonNode.Parse(await web.GetStringAsync($"https://{uri.Host}/api/v1/models/{id}"));
            (modelName, modelType) = (model?["name"]?.ToString(), model?["type"]?.ToString());
            ver = model?["modelVersions"]?[0];                 // the newest version
        }
        else throw new Exception("that isn't a Civitai model page.");
        if (ver?["files"] is not JsonArray files) throw new Exception("Civitai didn't list any files for it.");
        modelName ??= ver["model"]?["name"]?.ToString();
        modelType ??= ver["model"]?["type"]?.ToString();
        var f = (fileId != null ? files.FirstOrDefault(x => x?["id"]?.ToString() == fileId) : null)
            ?? files.FirstOrDefault(x => x?["primary"]?.GetValue<bool>() == true)
            ?? files.FirstOrDefault(x => x?["name"]?.ToString().EndsWith(".safetensors") == true);
        if (f?["name"]?.ToString() is not string name || !name.EndsWith(".safetensors")) throw new Exception("its file isn't a .safetensors model.");
        // ponytail: trusts the uploader's labels; a mislabelled diffusion-only "Model" lands in checkpoints (move it on the Models page)
        var folder = (modelType, f["type"]?.ToString()) switch
        {
            (_, "Diffusion Model") => "diffusion_models",
            (_, "Text Encoder") => "text_encoders",
            (_, "VAE") or ("VAE", _) => "vae",
            ("LORA" or "LoCon" or "DoRA", _) => "loras",
            ("Upscaler", _) => "upscale_models",
            ("Checkpoint", _) => "checkpoints",
            _ => throw new Exception($"Ember can't use this kind of model ({modelType})."),
        };
        return new CatalogItem($"{modelName} · {ver["name"]}", KindOf(folder), folder, name, f["downloadUrl"].ToString(),
            (long)Math.Round(f["sizeKB"].GetValue<double>() * 1024), f["hashes"]?["SHA256"]?.ToString().ToLowerInvariant(),
            $"{ver["baseModel"]} · Civitai", "From your link.");
    }

    // a safetensors file starts with 8 bytes of header length and a JSON header naming every tensor: two small range requests
    static async Task<List<string>> HeaderKeys(string url)
    {
        async Task<byte[]> Bytes(long from, long to)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new(from, to) } };
            using var res = await web.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (res.StatusCode != HttpStatusCode.PartialContent) throw new Exception("the site wouldn't send just the start of the file.");
            return await res.Content.ReadAsByteArrayAsync();
        }
        long n = BinaryPrimitives.ReadInt64LittleEndian(await Bytes(0, 7));
        if (n <= 0 || n > 100_000_000) throw new Exception("that isn't a safetensors file.");
        return JsonNode.Parse(await Bytes(8, 8 + n - 1)).AsObject().Select(p => p.Key).Where(k => k != "__metadata__").ToList();
    }

    // where a model goes, told from its tensor names, as checked against every kind installed here
    static string FolderFor(List<string> keys)
    {
        bool Starts(params string[] prefixes) => keys.Any(k => prefixes.Any(k.StartsWith));
        if (keys.Any(k => k.Contains("lora", StringComparison.OrdinalIgnoreCase))) return "loras";
        // model, text encoder and VAE in one: SD and SDXL call the VAE first_stage_model., newer ComfyUI saves (FLUX, SD3) vae. and text_encoders.
        if (Starts("model.diffusion_model.") && Starts("first_stage_model.", "vae.", "conditioner.", "cond_stage_model.", "text_encoders.")) return "checkpoints";
        if (Starts("model.diffusion_model.", "diffusion_model.", "transformer_blocks.", "double_blocks.", "single_blocks.", "joint_blocks.",
                   "input_blocks.", "down_blocks.", "txtfusion.", "img_in.")) return "diffusion_models";
        if (Starts("model.layers.", "model.language_model.", "language_model.", "text_model.", "encoder.block.", "model.embed_tokens")) return "text_encoders";
        if (Starts("decoder.", "encoder.down")) return "vae";
        return null;
    }

    // Civitai answers a download with a redirect to the file itself. The user's API key goes to Civitai only:
    // the redirect is followed by hand, without it.
    async Task<string> CivitaiFileUrl(string url, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (CivitaiKey is string key) req.Headers.Authorization = new("Bearer", key);
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new Exception(CivitaiKey == null ? "Civitai wants your API key for this file: add it in Settings, under Downloads." : "Civitai didn't accept your API key; check it in Settings.");
        if (res.Headers.Location is Uri to) return (to.IsAbsoluteUri ? to : new Uri(new Uri(url), to)).ToString();
        res.EnsureSuccessStatusCode();
        return url;
    }

    async void RemoveModel(string name, string path, string warning = null)
    {
        if (MessageBox.Show(this, $"Move {name} ({Size(new FileInfo(path).Length)}) to the Recycle Bin?" + (warning == null ? "" : "\n\n" + warning), "Ember",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { ModelsNote.Text = $"Couldn't remove {name}: {ex.Message}"; return; }
        ModelsNote.Text = $"{name} is in the Recycle Bin.";
        BuildModels();
        if (ready) await LoadOptions();
    }
}
