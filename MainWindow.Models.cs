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
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        new("SeedVR2 3B (int8)", "Upscaler", "diffusion_models", "seedvr2_3b_int8_convrot.safetensors",
            "https://huggingface.co/Comfy-Org/SeedVR2/resolve/main/diffusion_models/seedvr2_3b_int8_convrot.safetensors", 3458259704,
            "c3dec8bcc5916843a8a858572970597462e1f2dc598d6dfd818f6cd40f53a157", "Apache 2.0", "The sharpest upscale: rebuilds fine detail instead of smoothing it. Needs the SeedVR2 VAE below."),
        new("SeedVR2 VAE", "VAE", "vae", "seedvr2_ema_vae_fp16.safetensors",
            "https://huggingface.co/Comfy-Org/SeedVR2/resolve/main/vae/seedvr2_ema_vae_fp16.safetensors", 501324814,
            "20678548f420d98d26f11442d3528f8b8c94e57ee046ef93dbb7633da8612ca1", "Apache 2.0", "Turns SeedVR2's output into pixels."),
        new("BiRefNet lite", "Background", "onnx", "BiRefNet_lite.onnx",
            "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx", 224005088,
            "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", "MIT", "Cut out: keeps the subject, removes the background."),
    };

    readonly Dictionary<CatalogItem, CancellationTokenSource> downloads = new();
    readonly Dictionary<CatalogItem, (ProgressBar Bar, TextBlock Text)> downloadUi = new();
    readonly List<(string Name, string Result, string Colour)> finished = new();      // the last few downloads that ended, newest first

    // one row of the list: an installed file (Path) or one to get (Get)
    record ModelEntry(string Group, string Family, string Title, string Kind, string About, string Facts, string Path = null, CatalogItem Get = null,
                      bool InUse = false, Action Use = null, string Warn = null, string Page = null, string Picture = null, long Size = 0, DateTime Added = default);

    List<ModelEntry> installedEntries = new(), modelsShown = new();      // every installed file; what the filters let through
    string modelsKind, modelsFamily;            // the chosen kind and family: null shows all, a family of "" the files with none
    int modelsLimit = 60;                       // rows drawn at once, so a few hundred LoRAs stay quick: Show more draws the next ones
    readonly HashSet<string> picked = new(StringComparer.OrdinalIgnoreCase);       // installed files ticked, to remove together

    void Models_Click(object sender, RoutedEventArgs e) { BuildModels(); ShowDownloads(); ModelsPanel.Visibility = Visibility.Visible; }

    void OpenModelsFolder_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", ModelsDir);

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";
    static string PathOf(CatalogItem m) => Path.Combine(ModelsDir, m.Folder, m.File);
    // a model moved to a faster drive leaves a link behind, whose own size is 0: the size is the file it points to
    static FileInfo RealFile(string path) { var f = new FileInfo(path); return f.LinkTarget != null && f.ResolveLinkTarget(true) is FileInfo t && t.Exists ? t : f; }
    static long SizeOf(string path) => RealFile(path).Length;

    // an installed file and what it's for, worked out from its folder and its contents
    record Installed(string Path, string Folder, string Name, string Group, string Family, string About, string License);

    static readonly (string Group, string Note)[] Groups =
    {
        ("Picture models", "Press Use, or pick one in the Model card."),
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
        "krea2" => "Krea 2", "qwen_image" => "Qwen-Image", "wan" => "Wan 2.1", "anima" => "Anima", "minimax_h3" => "MiniMax H3", "moss_image" => "MOSS-Image", "fibo2" => "Fibo 2.0", "sdxl" => "SDXL", _ => null,
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
                "moss_image" => ("Picture models", "moss_image", "Photos and art, with clear lettering and long prompts."),
                "fibo2" => ("Tools", "fibo2", "Makes painted edits: follows what you write and keeps the rest of the picture."),
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
        "anima" => animaTe == null || kreaVae == null, "minimax_h3" => h3Te == null || h3Vae == null || h3AudioVae == null,
        "moss_image" => mossTe == null || mossVae == null, _ => false,
    };

    // looks in the model folders again, then shows the list
    void BuildModels()
    {
        var files = ModelFolders.Where(f => Directory.Exists(Path.Combine(ModelsDir, f)))
            .SelectMany(f => Directory.GetFiles(Path.Combine(ModelsDir, f)).Select(p => (Folder: f, Path: p)))
            .Where(x => x.Path.EndsWith(".safetensors") || x.Path.EndsWith(".gguf") || x.Path.EndsWith(".ckpt") || x.Path.EndsWith(".pth") || x.Path.EndsWith(".onnx"))
            .Select(x => Inspect(x.Folder, x.Path)).ToList();
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(ModelsDir));
            ModelsSummary.Text = $"{files.Count} files, {Size(files.Sum(f => SizeOf(f.Path)))} · {Size(drive.AvailableFreeSpace)} free on {drive.Name.TrimEnd('\\')}";
        }
        catch { ModelsSummary.Text = ""; }

        // which installed models lean on a part: shown on it, and asked about before it goes
        string Users(string family) => string.Join(", ", files.Where(f => f.Family == family && f.Group is "Picture models" or "Video models").Select(f => f.Name));
        var entries = new List<ModelEntry>();
        foreach (var f in files)
        {
            var group = f.Group;
            var file = Path.GetFileName(f.Path);
            long size = SizeOf(f.Path);
            string about = f.About, warn = null, page = null, title = f.Name, picture = null,
                facts = f.License == null ? $"{Size(size)} · {file}" : $"{Size(size)} · {f.License}";
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
                    picture = settings.AdultAgreed ? li.Picture : li.SafePicture;
                    if (li.BaseModel is { Length: > 0 } made) facts = $"{made} · {facts}";
                }
                inUse = loraRows.Any(r => (r.Box?.SelectedItem as string) == file);
                var family = f.Family;
                use = () => UseLora(file, family);
            }
            var fi = new FileInfo(f.Path);
            entries.Add(new ModelEntry(group, f.Family, title, KindOf(f.Folder), about ?? "", facts, f.Path, null, inUse, use, warn, page, picture, size,
                fi.CreationTimeUtc > fi.LastWriteTimeUtc ? fi.CreationTimeUtc : fi.LastWriteTimeUtc));      // a copied file keeps its old date: added is the later one
        }
        installedEntries = entries;
        picked.RemoveWhere(p => !File.Exists(p));
        ShowModels();
    }

    // the list as the filters say: Installed or Get more, the search, a kind and a family (each chip with its count), in the chosen order
    void ShowModels()
    {
        ModelsList.Children.Clear();
        if (linked != null && !File.Exists(PathOf(linked)))
        {
            Section("From your link");
            ModelsList.Children.Add(ModelRow(Entry(linked) with { Family = null, Facts = $"{Size(linked.Size)} · {linked.License} · goes in {linked.Folder}" }));
        }
        bool toGet = ShowToGet.IsChecked == true;
        var more = Catalog.Where(c => !File.Exists(PathOf(c))).Select(Entry).ToList();
        ShowInstalled.Content = $"Installed · {installedEntries.Count}";
        ShowToGet.Content = $"Get more · {more.Count}";
        var words = ModelsSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = (toGet ? more : installedEntries)
            .Where(e => words.All(w => $"{e.Title} {e.Kind} {FamilyLabel(e.Family)} {e.About} {e.Facts}".Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();

        Chips(ModelKinds, "Kind", "All kinds", found.Count, modelsKind, k => modelsKind = k,
            Groups.Where(g => g.Group == modelsKind || found.Any(e => e.Group == g.Group)).Select(g => (g.Group, KindLabel(g.Group), found.Count(e => e.Group == g.Group))));
        var ofKind = found.Where(e => modelsKind == null || e.Group == modelsKind).ToList();
        string Label(string family) => FamilyLabel(family) ?? (family == "" ? "Unknown" : family);
        var families = ofKind.GroupBy(e => e.Family ?? "").OrderByDescending(g => g.Count()).Select(g => (g.Key, Label(g.Key), g.Count())).ToList();
        if (modelsFamily != null && families.All(f => f.Key != modelsFamily)) families.Add((modelsFamily, Label(modelsFamily), 0));
        Chips(ModelFamilies, "Family", "All families", ofKind.Count, modelsFamily, f => modelsFamily = f, families);
        ModelFamilies.Visibility = families.Count > 1 || modelsFamily != null ? Visibility.Visible : Visibility.Collapsed;
        modelsShown = ofKind.Where(e => modelsFamily == null || (e.Family ?? "") == modelsFamily).ToList();

        // by name, in sections by kind; by size or date, one list across kinds (to find what takes the room, or what came last)
        IEnumerable<ModelEntry> Sorted(IEnumerable<ModelEntry> list) => SortSize.IsChecked == true ? list.OrderByDescending(e => e.Size)
            : SortNew.IsChecked == true ? list.OrderByDescending(e => e.Added) : list.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string Group, string Note)> sections = SortName.IsChecked == true ? Groups : [(null, null)];
        int drawn = 0;
        foreach (var (group, note) in sections)
        {
            var list = Sorted(modelsShown.Where(e => group == null || e.Group == group)).ToList();
            if (list.Count == 0 || drawn >= modelsLimit) continue;
            if (group != null) Section($"{group} · {list.Count}", toGet ? null : note);
            foreach (var e in list.Take(modelsLimit - drawn)) { ModelsList.Children.Add(ModelRow(e)); drawn++; }
        }
        if (modelsShown.Count > drawn)
        {
            int rest = modelsShown.Count - drawn;
            var b = new Button { Content = rest > 60 ? $"Show 60 more (of {rest})" : $"Show the other {rest}", Style = (Style)FindResource("Ghost"), Height = 32,
                                 HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(b, "Show more models");
            b.Click += (_, _) => { modelsLimit += 60; ShowModels(); };
            ModelsList.Children.Add(b);
        }
        else if (modelsShown.Count == 0)
            ModelsList.Children.Add(new TextBlock { Text = toGet && more.Count == 0 ? "Everything here is installed. Paste a link above for more." : "Nothing matches. Try fewer words, or All kinds.",
                                                    Style = (Style)FindResource("Note"), Margin = new Thickness(0, 16, 0, 0) });
        ShowPicks();
    }

    // the line above the list: how many files show, or how many are picked and their size, with Pick all, Clear and Remove
    void ShowPicks()
    {
        bool toGet = ShowToGet.IsChecked == true;
        ModelsCount.Text = toGet ? "Free, open-licence downloads, each checked against its published checksum."
            : picked.Count > 0 ? $"{picked.Count} picked · {Size(installedEntries.Where(x => picked.Contains(x.Path)).Sum(x => x.Size))}"
            : modelsShown.Count == installedEntries.Count ? $"{modelsShown.Count} files" : $"{modelsShown.Count} of {installedEntries.Count} files";
        PickAllBtn.Content = $"Pick all {modelsShown.Count}";
        PickAllBtn.Visibility = !toGet && modelsShown.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ClearPicksBtn.Visibility = RemovePickedBtn.Visibility = !toGet && picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovePickedBtn.Content = $"Remove {picked.Count}…";
    }

    static string KindLabel(string group) => group switch { "LoRA add-ons" => "LoRAs", "Other files" => "Other", _ => group };

    // a row of filter chips after its heading: All, then each choice with how many files it has; pressing one picks it and draws the list again
    void Chips(WrapPanel panel, string heading, string all, int total, string chosen, Action<string> choose, IEnumerable<(string Key, string Label, int Count)> items)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = heading, Width = 56, FontSize = 12, Foreground = Brush("Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var (key, label, count) in items.Prepend((null, "All", total)))
        {
            var text = new TextBlock();
            text.Inlines.Add(new Run(label));
            text.Inlines.Add(new Run($"  {count}") { Foreground = Brush("Faint") });
            var b = new ToggleButton { Style = (Style)FindResource("Chip"), Content = text, IsChecked = key == chosen, Margin = new Thickness(0, 0, 4, 4) };
            AutomationProperties.SetName(b, key == null ? all : label);
            // Checked and Unchecked, not Click: a screen reader (and the UI test) toggles without clicking
            b.Checked += (_, _) => { choose(key); Refilter(); };
            b.Unchecked += (_, _) => { choose(key); Refilter(); };       // pressing the one that's on keeps it on
            panel.Children.Add(b);
        }
    }

    void Refilter() { modelsLimit = 60; ShowModels(); ModelsScroll.ScrollToTop(); }
    void ModelsSearch_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Refilter(); }
    void ModelsView_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) Refilter(); }
    void PickAll_Click(object sender, RoutedEventArgs e) { picked.UnionWith(modelsShown.Where(x => x.Path != null).Select(x => x.Path)); ShowModels(); }
    void ClearPicks_Click(object sender, RoutedEventArgs e) { picked.Clear(); ShowModels(); }
    void RemovePicked_Click(object sender, RoutedEventArgs e) => RemoveModels(installedEntries.Where(x => picked.Contains(x.Path)).ToList());

    // a catalog file as a row: its kind's group, and its family from its name
    static ModelEntry Entry(CatalogItem c) => new(
        c.Kind switch { "Model" => "Picture models", "Video model" => "Video models", "LoRA" => "LoRA add-ons", "Text encoder" or "VAE" => "Parts", _ => "Tools" },
        c.Name.StartsWith("Krea 2") ? "krea2" : c.Name.StartsWith("Wan") ? "wan" : c.Folder is "checkpoints" or "loras" || c.Name.StartsWith("SDXL") ? "sdxl" : null,
        c.Name, c.Kind, c.About, $"{Size(c.Size)} · {c.License} · {new Uri(c.Url).Host}", Get: c, Size: c.Size);

    // Use: pick the model (switching between pictures and video if needed)
    void UseModel(string item, bool video)
    {
        CloseOverlays_Click(null, null);
        if (video) VideoMode.IsChecked = true;
        else if (VideoOn) CreateMode.IsChecked = true;
        PickModel(item);
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
        AdvancedToggle.IsChecked = false;
        Dispatcher.BeginInvoke(() => LoraSection.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        Note($"LoRA add-on on: {Path.GetFileNameWithoutExtension(lora)}.");
    }

    static string KindOf(string folder) => folder switch
    {
        "checkpoints" => "Model", "diffusion_models" => "Diffusion model", "text_encoders" => "Text encoder", "vae" => "VAE",
        "loras" => "LoRA", "upscale_models" => "Upscaler", _ => "Background",
    };

    void Section(string title, string note = null)
    {
        ModelsList.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 16, 0, note == null ? 8 : 2) });
        if (note != null) ModelsList.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
    }

    // one card: a tick (installed files, to remove several at once), the picture for a LoRA, name, kind, family and In use; what it's for; the facts.
    // Use / Page / Show / Remove when installed, Download when not
    FrameworkElement ModelRow(ModelEntry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (e.Path != null)
        {
            var mark = new TextBlock { FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15 };
            var tick = new ToggleButton { Style = (Style)FindResource("Chip"), Content = mark, Width = 30, Padding = new Thickness(0), Margin = new Thickness(-8, 0, 6, 0),
                                          VerticalAlignment = VerticalAlignment.Center, IsChecked = picked.Contains(e.Path), ToolTip = "Pick it, to remove several at once" };
            void Mark() => mark.Text = tick.IsChecked == true ? "\uE73A" : "\uE739";      // a ticked box, an empty one
            Mark();
            tick.Checked += (_, _) => { picked.Add(e.Path); Mark(); ShowPicks(); };
            tick.Unchecked += (_, _) => { picked.Remove(e.Path); Mark(); ShowPicks(); };
            AutomationProperties.SetName(tick, "Pick " + e.Title);
            grid.Children.Add(tick);
        }
        if (e.Group == "LoRA add-ons")
        {
            var img = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 6, 6) };
            var tile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(6), Background = Brush("Raised2"), Margin = new Thickness(0, 0, 12, 0),
                                    VerticalAlignment = VerticalAlignment.Top, Child = img };
            AutomationProperties.SetAutomationId(img, "LoraPicture");          // the Models test finds them to black them out in its shots
            if (!string.IsNullOrEmpty(e.Picture)) ShowPicture(img, e.Picture);
            Grid.SetColumn(tile, 1);
            grid.Children.Add(tile);
        }

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        Border Chip(string text, string colour) => new()
        {
            Background = Brush("Raised2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontSize = 11, Foreground = Brush(colour) },
        };
        title.Children.Add(Chip(e.Kind, "Muted"));
        if (FamilyLabel(e.Family) is string family) title.Children.Add(Chip(family, "Accent"));
        if (e.InUse) title.Children.Add(Chip("In use", "Ok"));
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(title);
        // three lines at most, so a long list stays easy to scan: the rest is in the tooltip
        left.Children.Add(new TextBlock { Text = e.About, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                                          MaxHeight = 52, TextTrimming = TextTrimming.WordEllipsis,
                                          ToolTip = e.About.Length > 150 ? new TextBlock { Text = e.About, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 } : null });
        left.Children.Add(new TextBlock { Text = e.Facts, FontSize = 11.5, Foreground = Brush("Faint"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(left, 2);
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Style = (Style)FindResource("Ghost"), Height = 32, Margin = new Thickness(6, 0, 0, 0), FontSize = 13 };
            if (click != null) b.Click += click; else b.IsEnabled = false;
            return b;
        }

        if (e.Path != null)
        {
            if (e.Use != null) { var b = Btn(e.InUse ? "In use" : "Use", (_, _) => e.Use()); b.IsEnabled = !e.InUse; right.Children.Add(b); }
            if (e.Page != null) right.Children.Add(Btn("Page", (_, _) => OpenPage(e.Page)));
            right.Children.Add(Btn("Show", (_, _) => Process.Start("explorer.exe", $"/select,\"{e.Path}\"")));
            right.Children.Add(Btn("Remove", (_, _) => RemoveModels(new() { e })));
        }
        else if (downloads.ContainsKey(e.Get)) right.Children.Add(Btn("Downloading…", null));         // its progress is in Downloads, at the top
        else right.Children.Add(Btn("Download", (_, _) => _ = Download(e.Get)));                       // one per row: quiet, like Use and Show

        return new Border
        {
            Background = Brush("Raised"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Child = grid,
        };
    }

    static readonly Dictionary<string, BitmapSource> pictures = new();      // LoRA pictures fetched this run, by address

    async void ShowPicture(Image img, string url)
    {
        try
        {
            if (!pictures.TryGetValue(url, out var bmp))
            {
                var bytes = await web.GetByteArrayAsync(url);
                var b = new BitmapImage();
                b.BeginInit(); b.StreamSource = new MemoryStream(bytes); b.DecodePixelWidth = 112; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                pictures[url] = bmp = b;
            }
            img.Source = bmp;
        }
        catch { /* offline, or a format Windows can't read: the empty tile stays */ }
    }

    // what's downloading, each with its progress, speed, time left and Cancel; then the last few that ended
    void ShowDownloads()
    {
        DownloadsList.Children.Clear();
        downloadUi.Clear();
        foreach (var (m, cts) in downloads)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Ghost"), Height = 30, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(cancel, "Cancel download");
            cancel.Click += (_, _) => cts.Cancel();
            DockPanel.SetDock(cancel, Dock.Right);
            row.Children.Add(cancel);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
            var text = new TextBlock { Style = (Style)FindResource("Note"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), Text = $"Starting… {Size(m.Size)}" };
            info.Children.Add(bar);
            info.Children.Add(text);
            row.Children.Add(info);
            DownloadsList.Children.Add(row);
            downloadUi[m] = (bar, text);
        }
        foreach (var (name, result, colour) in finished)
        {
            var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
            line.Inlines.Add(new Run(name) { Foreground = Brush("Soft") });
            line.Inlines.Add(new Run("   " + result) { Foreground = Brush(colour) });
            DownloadsList.Children.Add(line);
        }
        DownloadsTitle.Text = downloads.Count > 0 ? $"Downloading {downloads.Count}" : "Downloads";
        ClearDownloadsBtn.Visibility = finished.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsBox.Visibility = downloads.Count + finished.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearDownloads_Click(object sender, RoutedEventArgs e) { finished.Clear(); ShowDownloads(); }

    void Finished(string name, string result, string colour)
    {
        finished.Insert(0, (name, result, colour));
        if (finished.Count > 6) finished.RemoveAt(6);
    }

    static string TimeLeft(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:m\\:ss}";

    async Task Download(CatalogItem m)
    {
        var dest = PathOf(m);
        var part = dest + ".part";
        var cts = new CancellationTokenSource();
        downloads[m] = cts;
        ShowDownloads();
        ShowModels();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var url = IsCivitai(new Uri(m.Url)) ? await CivitaiFileUrl(m.Url, cts.Token) : m.Url;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new Exception("the site wants you to sign in for this file; download it in your browser instead");
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? m.Size;
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
                    if (clock.Elapsed - shown > TimeSpan.FromMilliseconds(300) && total > 0 && downloadUi.TryGetValue(m, out var ui))
                    {
                        shown = clock.Elapsed;
                        double speed = done / Math.Max(0.1, clock.Elapsed.TotalSeconds);
                        ui.Bar.Value = (double)done / total;
                        ui.Text.Text = $"{Size(done)} of {Size(total)} · {done * 100 / total}% · {Size((long)speed)}/s · {TimeLeft(TimeSpan.FromSeconds(Math.Max(0, total - done) / Math.Max(1, speed)))} left";
                    }
                }
            }
            if (downloadUi.TryGetValue(m, out var check)) { check.Bar.Value = 1; check.Text.Text = "Checking…"; }
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
            Finished(m.Name, "Installed", "Ok");
            if (ready) await LoadOptions();          // ComfyUI lists new files the next time it is asked
        }
        catch (OperationCanceledException) { ModelsNote.Text = $"{m.Name}: download cancelled."; Finished(m.Name, "Cancelled", "Muted"); }
        catch (Exception ex) { ModelsNote.Text = $"{m.Name}: download failed: {ex.Message}"; Finished(m.Name, "Failed: " + ex.Message, "Danger"); }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }   // our own half-finished file
            downloads.Remove(m);
            ShowDownloads();
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

    // to the Recycle Bin, after one question that lists them and says what stops working without them
    async void RemoveModels(List<ModelEntry> list)
    {
        if (list.Count == 0) return;
        var question = list.Count == 1 ? $"Move {list[0].Title} ({Size(list[0].Size)}) to the Recycle Bin?"
            : $"Move these {list.Count} files ({Size(list.Sum(x => x.Size))}) to the Recycle Bin?\n\n" + string.Join("\n", list.Take(12).Select(x => "• " + x.Title))
              + (list.Count > 12 ? $"\n…and {list.Count - 12} more" : "");
        var warnings = list.Where(x => x.Warn != null).Select(x => list.Count == 1 ? x.Warn : $"{x.Title}: {x.Warn}").Distinct().ToList();
        if (warnings.Count > 0) question += "\n\n" + string.Join("\n", warnings.Take(6)) + (warnings.Count > 6 ? "\n…" : "");
        if (MessageBox.Show(this, question, "Ember", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var failed = new List<string>();
        foreach (var x in list)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(x.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                picked.Remove(x.Path);
            }
            catch (Exception ex) { failed.Add($"{x.Title}: {ex.Message}"); }
        }
        ModelsNote.Text = failed.Count == 0 ? (list.Count == 1 ? $"{list[0].Title} is in the Recycle Bin." : $"{list.Count} files are in the Recycle Bin.")
            : $"Couldn't remove {failed[0]}" + (failed.Count > 1 ? $" (and {failed.Count - 1} more)" : "");
        BuildModels();
        if (ready) await LoadOptions();
    }
}
