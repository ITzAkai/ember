// ComfyUI: starting and stopping it, finding the models, and building the graph for a new picture.
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageForge;

public partial class MainWindow
{
    // ---------------- ComfyUI ----------------
    async Task<bool> Alive()
    {
        try { using var r = await http.GetAsync("/system_stats"); return r.IsSuccessStatusCode; } catch { return false; }
    }

    bool listening;

    void Offline(string why)
    {
        SetState("Offline", Brush("Danger"));
        if (why != null) Note(why, true);
        RestartPanel.Visibility = Visibility.Visible;
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        RestartPanel.Visibility = Visibility.Collapsed;
        ready = false;
        GenerateBtn.IsEnabled = false;
        SetState("Starting", Brush("Accent"));
        StopServer();                           // ours, if it is hung rather than gone
        await StartUp();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var log = ServerLog;
        if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    // Settings > About: a report of this PC and ComfyUI's recent log, for when something failed without crashing
    void MakeReport_Click(object sender, RoutedEventArgs e)
    {
        var file = ErrorReport.Write("Made from Settings", null, ServerLog);
        if (file == null) { Note("Couldn't write the report.", true); return; }
        Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    bool startingServer;        // while ComfyUI (re)starts, a quiet websocket isn't "Offline"

    async Task StartUp()
    {
        startingServer = true;
        try { await StartComfy(); }
        finally { startingServer = false; }
    }

    async Task StartComfy()
    {
        if (!await Alive())
        {
            var python = Path.Combine(Root, "python_embeded", "python.exe");
            if (!File.Exists(python) || !File.Exists(Path.Combine(Root, "ComfyUI", "main.py")))
            {
                SetState("Not set up", Brush("Danger"));
                Note("ComfyUI isn't set up yet. Press Set up ComfyUI and pick your ComfyUI folder, or any folder to download it into (2 GB).", true);
                SetupPanel.Visibility = Visibility.Visible;
                return;
            }
            Note("Starting ComfyUI on your graphics card…");
            WriteFinishNode();
            // through cmd so ComfyUI writes its own log file: it can then outlive this app ("Keep ComfyUI running")
            var args = "-s ComfyUI\\main.py --windows-standalone-build --listen 127.0.0.1 --port " + Port + " --disable-auto-launch --preview-method auto" + LaunchFlags();
            if (!string.IsNullOrWhiteSpace(settings.OutputDir)) args += $" --output-directory \"{settings.OutputDir.TrimEnd('\\')}\"";
            server = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/s /c \"\"{python}\" {args} > {Path.GetFileName(ServerLog)} 2>&1\"")
                { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true }
            };
            server.Start();
            for (int i = 0; i < 240 && !await Alive(); i++)
            {
                if (server.HasExited) { Offline("ComfyUI stopped while starting."); return; }
                // how long so far, rather than a guess: with many plugins it can take a minute or two
                Note($"Starting ComfyUI on your graphics card… {i} s" + (settings.FastStart ? "" : " (loading all your plugins)"));
                await Task.Delay(1000);
            }
            if (!await Alive()) { Offline("ComfyUI didn't start in time."); return; }
        }
        await LoadOptions();
        await LoadSystem();
        await LoadNodeNames();
        ShowPluginBanner();
        ShowBoosters();
        ShowEditExtras();
        FillWorkflows();
        if (!listening) { listening = true; _ = Listen(); }
        ready = true;
        GenerateBtn.IsEnabled = ModelBox.Items.Count > 0;
        SetState("Ready", Brush("Ok"));
        if (ModelBox.Items.Count > 0) Note(setupNote ?? "");
    }

    async Task LoadOptions()
    {
        loadingOptions = true;
        bool otherModel = false;
        try
        {
            var ck = JsonNode.Parse(await http.GetStringAsync("/object_info/CheckpointLoaderSimple"));
            var ks = JsonNode.Parse(await http.GetStringAsync("/object_info/KSampler"));
            var unets = ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/UNETLoader"))?["UNETLoader"]?["input"]?["required"]?["unet_name"]).ToList();
            // smaller GGUF files, when a GGUF loader plugin is there (an empty answer otherwise)
            unets.AddRange(ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/UnetLoaderGGUF"))?["UnetLoaderGGUF"]?["input"]?["required"]?["unet_name"])
                .Where(u => u.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)));
            var tes = ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/CLIPLoader"))?["CLIPLoader"]?["input"]?["required"]?["clip_name"]).ToList();
            // MiniMax H3's text encoder also comes as GGUF (loaded by the GGUF plugin), but only one with its picture reader works
            tes.AddRange(ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/CLIPLoaderGGUF"))?["CLIPLoaderGGUF"]?["input"]?["required"]?["clip_name"])
                .Where(t => t.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && PartKind("text_encoders", t) == "qwen3vl_32b"));
            var vaes = ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/VAELoader"))?["VAELoader"]?["input"]?["required"]?["vae_name"]).ToList();
            FindParts(tes, vaes);      // by what's inside each file, not its name
            var videos = new List<string>();
            var ckpts = ComboOptions(ck["CheckpointLoaderSimple"]["input"]["required"]["ckpt_name"]).ToList();
            // plain SDXL base stays installed for the inpainting model, but every other model makes better pictures
            var models = ckpts.Where(c => !c.StartsWith("sd_xl_base", StringComparison.OrdinalIgnoreCase) && IsFullCheckpoint(c)).ToList();
            separate.Clear(); setupNote = null; fiboEdit = null;
            foreach (var u in unets)
            {
                var family = FamilyOf(u);
                if (family == null) continue;
                // Fibo 2.0 Edit Turbo only edits: it makes painted edits (see UseFiboEdit) and stays out of the model list
                if (family == "fibo2") { if (u.Contains("edit", StringComparison.OrdinalIgnoreCase)) fiboEdit = u; continue; }
                var (te, vae, label) = family switch
                {
                    "qwen_image" => (qwenTe, qwenVae, "Qwen-Image 2.1"), "krea2" => (kreaTe, kreaVae, "Krea 2"), "anima" => (animaTe, kreaVae, "Anima"),
                    "moss_image" => (mossTe, mossVae, "MOSS-Image"),
                    "minimax_h3" => (h3Te, h3AudioVae == null ? null : h3Vae, "MiniMax H3"),        // and its audio VAE
                    _ => (wanTe, wanVae, "Wan 2.1"),
                };
                if (te == null || vae == null)
                {
                    setupNote = $"{Path.GetFileNameWithoutExtension(u)} needs the {label} text encoder and VAE{(family == "minimax_h3" ? "s (video and audio)" : "")}: get them on the Models page.";
                    continue;
                }
                var name = u.Contains("qwen_image_2.1", StringComparison.OrdinalIgnoreCase) ? QwenName : u;   // the original keeps the name saved settings know
                separate[name] = (u, family);
                (IsVideoFamily(family) ? videos : models).Add(name);
            }
            if (models.Count == 0) models.AddRange(ckpts.Where(IsFullCheckpoint));        // SDXL base only when there's nothing else
            (imageModels, videoModels) = (models, videos);
            FillModels();
            otherModel = ModelBox.SelectedItem as string != (showingVideoModels ? settings.VideoModel : settings.Model);   // the saved one is gone or hidden: its guidance and sampler don't fit
            Fill(SamplerBox, ComboOptions(ks["KSampler"]["input"]["required"]["sampler_name"]), settings.Sampler, "dpmpp_2m");
            Fill(SchedulerBox, ComboOptions(ks["KSampler"]["input"]["required"]["scheduler"]), settings.Scheduler, "karras");
            if (ModelBox.Items.Count == 0) Note("No model yet. Open Models and download one: Krea 2 Turbo is a good start.", true);
            var up = JsonNode.Parse(await http.GetStringAsync("/object_info/UpscaleModelLoader"))?["UpscaleModelLoader"]?["input"]?["required"]?["model_name"];
            upscaleModel = ComboOptions(up).FirstOrDefault();
            var lo = JsonNode.Parse(await http.GetStringAsync("/object_info/LoraLoader"))?["LoraLoader"]?["input"]?["required"]?["lora_name"];
            // the Krea 2 pose LoRA only works through Pose picture (with its edit patch): not offered as an ordinary LoRA
            loras = ComboOptions(lo).Where(l => !l.Equals(KreaPoseLora, StringComparison.OrdinalIgnoreCase) && !l.Equals(MossPoseLora.File, StringComparison.OrdinalIgnoreCase)).ToList();
            loraFamily = loras.ToDictionary(l => l, l => LoraFamily(Path.Combine(ModelsDir, "loras", l)));
            FillLoras();
            _ = LoadLoraInfo();                 // what each LoRA does, looked up in the background
            inpaintModel = unets.FirstOrDefault(n => n.Contains("inpaint", StringComparison.OrdinalIgnoreCase));
            baseCheckpoint = ckpts.FirstOrDefault(c => c.Contains("sd_xl_base", StringComparison.OrdinalIgnoreCase))
                ?? ckpts.FirstOrDefault(c => !c.Contains("flux", StringComparison.OrdinalIgnoreCase));
            RefreshStyleTiles();
            UpdateLoraChip();
            ShowEditExtras();
        }
        catch (Exception e) { Note("Couldn't read ComfyUI's models: " + e.Message, true); }
        loadingOptions = false;
        // the settings on screen belong to the saved model, not the one picked instead: don't keep them as its profile
        if (otherModel) { profileModel = null; Model_SelectionChanged(null, null); }
    }

    bool loadingOptions;
    bool Flux => Named("flux");
    bool Named(string part) => ModelBox.SelectedItem is string m && m.Contains(part, StringComparison.OrdinalIgnoreCase);
    // models in separate files (diffusion model + text encoder + VAE): Qwen-Image 2.1, Krea 2 and their fine-tunes
    const string QwenName = "Qwen-Image 2.1";
    string qwenTe, qwenVae, kreaTe, kreaVae, animaTe, mossTe, mossVae, fiboEdit, fiboTe, fiboVae, setupNote;
    readonly Dictionary<string, (string Unet, string Family)> separate = new();
    string Family => ModelBox.SelectedItem is string m && separate.TryGetValue(m, out var s) ? s.Family : null;   // also its CLIPLoader type
    bool Qwen => Family == "qwen_image";
    bool Krea => Family == "krea2";
    bool Anima => Family == "anima";
    bool Moss => Family == "moss_image";
    bool AnimeModel => Anima || Named("noct") || ModelRec.Label.Contains("Illustrious") || ModelRec.Label == "NoobAI";
    // Anima's own quality negative, from ComfyUI's template (its training used these score tags)
    const string AnimaNegative = "worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, sepia";
    bool Sdxl => Family == null && !Flux;

    // steps for Fast / Standard / Best, guidance and sampler, from each model's recommended settings
    (int Fast, int Standard, int Best, double Cfg, string Sampler, string Scheduler) Preset
    {
        get { var r = ModelRec; return (r.Steps[0], r.Steps[1], r.Steps[2], r.Cfg, r.Sampler, r.Scheduler); }
    }
    int StepsFor(string quality) => quality switch { "20" => Preset.Fast, "30" => Preset.Standard, _ => Preset.Best };
    int Steps => (int)StepsSlider.Value;

    void TypedSteps()
    {
        if (int.TryParse(StepsBox.Text, out var v)) StepsSlider.Value = Math.Clamp(v, 1, 100);
        StepsBox.Text = Steps.ToString();
    }

    // which family a diffusion model is, told from its tensor names the way ComfyUI does, so renamed and fine-tuned files work too
    // a real checkpoint carries its own text encoder and VAE; a model-only file in checkpoints can't be loaded from there
    static bool IsFullCheckpoint(string name)
    {
        try
        {
            using var f = File.OpenRead(Path.Combine(ModelsDir, "checkpoints", name));
            var len = new byte[8]; f.ReadExactly(len);
            long n = BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n <= 0 || n > 100_000_000) return true;             // not a safetensors header (.ckpt): can't tell, leave it listed
            var head = new byte[n]; f.ReadExactly(head);
            var keys = Encoding.UTF8.GetString(head);
            return keys.Contains("first_stage_model.") || keys.Contains("\"vae.") || keys.Contains("conditioner.") || keys.Contains("cond_stage_model.") || keys.Contains("text_encoders.");
        }
        catch { return true; }
    }

    static string FamilyOf(string name)
    {
        foreach (var dir in new[] { "diffusion_models", "unet" })
        {
            var file = Path.Combine(ModelsDir, dir, name);
            if (File.Exists(file)) return FamilyOfFile(file);
        }
        return null;
    }

    static string FamilyOfFile(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            string keys;
            if (file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                // GGUF keeps the original tensor names (length-prefixed) near the start of the file
                var start = new byte[Math.Min(f.Length, 8 << 20)]; f.ReadExactly(start);
                keys = Encoding.Latin1.GetString(start);
                if (!keys.StartsWith("GGUF")) return null;
            }
            else
            {
                var len = new byte[8]; f.ReadExactly(len);
                long n = BinaryPrimitives.ReadInt64LittleEndian(len);
                if (n <= 0 || n > 100_000_000) return null;
                var head = new byte[n]; f.ReadExactly(head);
                keys = Encoding.UTF8.GetString(head);
            }
            if (keys.Contains("video_patch_proj.weight") && keys.Contains("audio_patch_proj.weight")) return "minimax_h3";     // MiniMax H3: video and sound
            if (keys.Contains("llm_adapter.")) return "anima";           // Anima (Cosmos-based, a Qwen3 0.6B adapter)
            if (keys.Contains("shared_block_modulation.weight") && keys.Contains("proj.ffn_up_gate.weight")) return "moss_image";   // as ComfyUI tells MOSS-Image
            if (keys.Contains("perceiver.latent_queries") && keys.Contains("final_layer.linear.weight")) return "fibo2";            // Fibo 2.0
            if (Regex.IsMatch(keys, @"txtfusion\.projector\.weight(?![\w.])")) return "krea2";
            if (Regex.IsMatch(keys, @"txt_in\.text_norm\.weight(?![\w.])")) return "qwen_image";
            // Wan 2.1 text-to-video takes 16 latent channels in; picture-to-video (Fun InP) 36: the start picture's latent and mask too.
            // Wan 2.2 5B (48, its own VAE) isn't supported.
            var wan = Regex.Match(keys, @"patch_embedding\.weight""\s*:\s*\{[^}]*""shape""\s*:\s*\[\s*\d+\s*,\s*(\d+)");
            return !wan.Success ? null : wan.Groups[1].Value switch { "16" => "wan", "36" => "wan_i2v", _ => null };
        }
        catch { return null; }
    }

    // --selftest part: a GGUF file's family comes from the tensor names in its header, like a safetensors file's
    // (MiniMax H3 GGUFs say "wan" as their architecture so the GGUF plugin loads them: the tensor names still tell)
    static bool SelfTestGguf()
    {
        var file = Path.Combine(Path.GetTempPath(), "imageforge-selftest.gguf");
        string Family(params string[] names)
        {
            using (var w = new BinaryWriter(File.Create(file)))
            {
                w.Write(Encoding.ASCII.GetBytes("GGUF")); w.Write(3u); w.Write((ulong)names.Length); w.Write(0UL);
                foreach (var name in names)
                {
                    w.Write((ulong)name.Length); w.Write(Encoding.UTF8.GetBytes(name)); w.Write(2u); w.Write(64UL); w.Write(64UL); w.Write(0u); w.Write(0UL);
                }
            }
            return FamilyOfFile(file);
        }
        try
        {
            return Family("txtfusion.projector.weight", "blocks.0.attn.qkv.weight") == "krea2"
                && Family("blocks.0.attn.qkv.weight", "video_patch_proj.weight", "audio_patch_proj.weight") == "minimax_h3"
                && Family("shared_block_modulation.weight", "layers.0.proj.ffn_up_gate.weight") == "moss_image"
                && H3Frames(33) == 56 && H3Frames(49) == 73 && H3Frames(81) == 124;      // 2, 3 and 5 s on H3's 17k + 5 grid
        }
        finally { try { File.Delete(file); } catch { } }
    }

    List<string> loras = new();
    string inpaintModel, baseCheckpoint;  // the SDXL inpainting UNet (diffusion_models) and a plain SDXL checkpoint for its CLIP and VAE

    // the Pixel art style's own (SDXL) LoRA wins over the picked one
    (string Name, double Strength)? ActiveLora()
    {
        if (Sdxl && CurrentStyle.Lora is string own && loras.Contains(own)) return (own, 1.0);
        var first = loraRows.FirstOrDefault(r => r.Box?.SelectedItem is string l && l != "None" && LorasForModel.Contains(l));
        if (first != null)
        {
            double str = Math.Round(first.Slider?.Value ?? 0.8, 2);
            if (first.ValueBox != null && double.TryParse(first.ValueBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
                str = Math.Round(p, 2);
            return (first.Box.SelectedItem as string, str);
        }
        return null;
    }

    // Collects all active LoRAs: UI dropdown LoRA + any <lora:filename:strength> specified in the prompt
    List<(string Name, double Strength)> AllActiveLoras(string promptText, out string cleanedPrompt)
    {
        var list = new List<(string Name, double Strength)>();
        var fit = LorasForModel.ToList();          // a LoRA for another model family would do nothing (or worse)
        try
        {
            if (Sdxl && CurrentStyle.Lora is string own && loras.Contains(own))
                list.Add((own, 1.0));

            foreach (var row in loraRows)
            {
                if (row.Box?.SelectedItem is string l && l != "None" && fit.Contains(l))
                {
                    double str = Math.Round(row.Slider?.Value ?? 0.8, 2);
                    if (row.ValueBox != null && double.TryParse(row.ValueBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
                        str = Math.Round(p, 2);
                    if (!list.Any(x => x.Name.Equals(l, StringComparison.OrdinalIgnoreCase)))
                        list.Add((l, str));
                }
            }
        }
        catch { }

        cleanedPrompt = promptText ?? "";
        if (string.IsNullOrEmpty(promptText)) return list;

        var matches = Regex.Matches(promptText, @"<lora:([^:>]+)(?::([0-9.-]+))?>", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Value.Trim();
            double str = 1.0;
            if (m.Groups[2].Success && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                str = parsed;

            // match against installed loras in ComfyUI\models\loras
            var matched = fit.FirstOrDefault(l =>          // exact names only: a loose match could pick the wrong file
                l.Equals(raw, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(l).Equals(raw, StringComparison.OrdinalIgnoreCase)
                || l.Replace('\\', '/').EndsWith("/" + raw.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || l.Replace('\\', '/').EndsWith("/" + raw.Replace('\\', '/') + ".safetensors", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(l).Equals(raw + ".safetensors", StringComparison.OrdinalIgnoreCase)
            );

            if (matched != null)
            {
                int existingIdx = list.FindIndex(x => x.Name.Equals(matched, StringComparison.OrdinalIgnoreCase));
                if (existingIdx >= 0)
                    list[existingIdx] = (matched, str);
                else
                    list.Add((matched, str));
            }
        }

        cleanedPrompt = Regex.Replace(promptText, @"<lora:[^>]+>", "").Trim();
        cleanedPrompt = Regex.Replace(cleanedPrompt, @",\s*,+", ",");
        cleanedPrompt = Regex.Replace(Regex.Replace(cleanedPrompt, @"\s+(?=[,.])", ""), @"\s{2,}", " ").Trim(' ', ',');   // no " ," or double spaces where a tag was
        return list;
    }

    // a LoRA only fits the model family it was trained on: "sdxl", "qwen_image", "krea2", or null when the file doesn't say
    Dictionary<string, string> loraFamily = new();
    string ModelFamily => Family == "wan_i2v" ? "wan" : Family ?? (Flux ? "flux" : "sdxl");      // picture-to-video takes the 1.3B text-to-video LoRAs
    // the ones made for this kind of model first (Pony / Illustrious / plain SDXL), the rest after
    IEnumerable<string> LorasForModel
    {
        get { var want = ModelLineage; return loras.Where(l => loraFamily.GetValueOrDefault(l) is not string f || f == ModelFamily).OrderBy(l => LoraFit(l, want) != null); }
    }

    string loraFamilyShown;     // the family whose LoRA rows are on screen

    void FillLoras()
    {
        if (loras.Count == 0 && loraFamily.Count == 0) return;      // the list hasn't loaded yet: keep the saved rows as they are
        var modelLoras = LorasForModel.ToList();
        if (ModelFamily != loraFamilyShown)
        {
            if (loraFamilyShown != null) settings.LorasByFamily[loraFamilyShown] = CurrentLoras();
            var wanted = settings.LorasByFamily.TryGetValue(ModelFamily, out var saved) ? saved
                : loraFamilyShown == null ? CurrentLoras() : new List<LoraItemSetting>();       // first load: the rows saved last time
            loraFamilyShown = ModelFamily;
            LoraRowsPanel.Children.Clear();
            loraRows.Clear();
            foreach (var l in wanted.Where(l => modelLoras.Contains(l.Name))) CreateLoraRow(l.Name, l.Strength);
        }
        else
        {
            var items = new[] { "None" }.Concat(modelLoras).ToList();
            foreach (var r in loraRows.Where(r => r.Box != null))
            {
                var current = r.Box.SelectedItem as string ?? "None";
                r.Box.ItemsSource = items;
                r.Box.SelectedItem = items.Contains(current) ? current : "None";
                ApplySearch(r.Box, LoraSearch.Text, LoraSearchText);
            }
        }
        UpdateLoraTitles();
        UpdateLoraChip();
        ShowLoraFitNote();
    }

    List<LoraItemSetting> CurrentLoras() => loraRows.Where(r => r.Box?.SelectedItem is string n && n != "None")
        .Select(r => new LoraItemSetting { Name = (string)r.Box.SelectedItem, Strength = r.Slider?.Value ?? 0.8 }).ToList();

    // the trainer's own label when there is one, else the layer names: SDXL's U-Net blocks, Qwen-Image's img/txt streams, Krea 2's wk/gate attention
    static string LoraFamily(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            var len = new byte[8]; f.ReadExactly(len);
            long n = BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n <= 0 || n > 100_000_000) return null;
            var head = new byte[n]; f.ReadExactly(head);
            var json = JsonNode.Parse(head).AsObject();
            var meta = json["__metadata__"];
            var label = $"{meta?["modelspec.architecture"]} {meta?["ss_base_model_version"]}".ToLowerInvariant();
            if (label.Contains("moss")) return "moss_image";
            if (label.Contains("krea")) return "krea2";
            if (label.Contains("qwen")) return "qwen_image";
            if (label.Contains("anima")) return "anima";
            if (label.Contains("minimax")) return "minimax_h3";
            if (label.Contains("wan")) return "wan";
            if (label.Contains("sdxl") || label.Contains("stable-diffusion-xl")) return "sdxl";
            var keys = string.Join('\n', json.Select(k => k.Key));
            // H3's own layers (not Hunyuan's individual_token_refiner), or its blocks' attn.qkv_proj and mlp.fc1/fc2 for LoRAs that train only those
            if (Regex.IsMatch(keys, @"(?<![\w])token_refiner\.") || keys.Contains("audio_patch_proj")
                || Regex.IsMatch(keys, @"blocks[._]\d+[._]attn[._]qkv_proj") && Regex.IsMatch(keys, @"mlp[._]fc[12]")) return "minimax_h3";
            // MOSS-Image's blocks: proj.qkv / proj.o and a fused proj.ffn_up_gate (written with dots or, in kohya files, underscores)
            if (Regex.IsMatch(keys, @"layers[._]\d+[._]proj[._](ffn_up_gate|ffn_down|qkv|[qkvo])[._]")) return "moss_image";
            if (keys.Contains(".attn.wk.") || keys.Contains(".attn.gate.") || keys.Contains("txtfusion") || keys.Contains("text_fusion")) return "krea2";
            if (keys.Contains("img_mlp") || keys.Contains("txt_mlp") || keys.Contains("img_mod")) return "qwen_image";
            if (keys.Contains("llm_adapter") || keys.Contains("adaln_modulation")) return "anima";
            // Anima's blocks: attention with q_proj .. output_proj and mlp.layer1/2 (any one is enough: slider LoRAs often train only
            // cross- or only self-attention); Wan's attention has .q / .k / .v / .o and an ffn
            if (Regex.IsMatch(keys, @"(self_attn|cross_attn)[._](q|k|v|output)_proj|mlp[._]layer[12]")) return "anima";
            if (keys.Contains("self_attn") && keys.Contains("cross_attn")) return "wan";
            if (keys.Contains("input_blocks") || keys.Contains("output_blocks") || keys.Contains("down_blocks") || keys.Contains("up_blocks")) return "sdxl";
            return null;
        }
        catch { return null; }
    }

    // --selftest part: on anime models a style keeps no negatives against drawn looks, and "Use prompt" unwraps either version
    static bool SelfTestStyles()
    {
        var cine = StylePreset.All.First(s => s.Key == "cinematic");
        var anime = cine.NegativeFor(true);
        return anime == "flat lighting, bright" && cine.NegativeFor(false) == cine.Negative
            && StylePreset.Unwrap(cine.Apply("a fox"), "blurry, " + anime) is ("a fox", "blurry", var a) && a == cine
            && StylePreset.Unwrap(cine.Apply("a fox"), "blurry, " + cine.Negative) is ("a fox", "blurry", _);
    }

    // --selftest part: a batch picture's copy gets its own seed in every sampler that had the first one's, and nothing else changes
    // --selftest part: settings holding an infinite or NaN number must still save and load (they once made every save fail silently)
    static bool SelfTestSettings()
    {
        try
        {
            var s = new Settings { WinLeft = double.PositiveInfinity, WinWidth = double.NegativeInfinity, StyleStrength = double.NaN };
            s.Profiles["m"] = new ModelProfile { Model = "m", Cfg = double.NaN, Boosters = new Boosters { HiResScale = double.PositiveInfinity } };
            var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, Settings.JsonOptions), Settings.JsonOptions);
            return double.IsPositiveInfinity(back.WinLeft) && double.IsNaN(back.Profiles["m"].Cfg);
        }
        catch (Exception e) { Console.Error.WriteLine("settings: " + e.Message); return false; }
    }

    // --selftest part: the pose preview's "no person found" (a black stick-figure picture) must tell black from a drawn pose
    static bool SelfTestAllDark()
    {
        BitmapSource Pic(bool dot)
        {
            var px = new byte[8 * 8 * 4];
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;          // the 4th byte set, as decoders do
            if (dot) px[4 * 9 + 2] = 255;                                 // one red pixel
            return BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, px, 8 * 4);
        }
        return AllDark(Pic(false)) && !AllDark(Pic(true));
    }

    static bool SelfTestReseed()
    {
        var body = JsonNode.Parse("""{"prompt":{"3":{"inputs":{"seed":7,"steps":7}},"refine":{"inputs":{"seed":7}},"52":{"inputs":{"noise_seed":7}},"9":{"inputs":{"seed":8}}}}""").AsObject();
        var p = Reseed(body, 7, 9)["prompt"];
        return (long)p["3"]["inputs"]["seed"] == 9 && (long)p["3"]["inputs"]["steps"] == 7 && (long)p["refine"]["inputs"]["seed"] == 9
            && (long)p["52"]["inputs"]["noise_seed"] == 9 && (long)p["9"]["inputs"]["seed"] == 8;
    }

    // --selftest part: Civitai's base-model names map to the SDXL line a LoRA fits (NoobAI is built on Illustrious)
    static bool SelfTestLineage() =>
        Lineage("Pony") == "Pony" && Lineage("Illustrious") == "Illustrious" && Lineage("NoobAI") == "Illustrious" && Lineage("SDXL 1.0") == "SDXL"
        && Lineage("Unholy Desire Mix (Illustrious) unholyDesireMixSinister_v90.safetensors") == "Illustrious" && Lineage("Flux.1 D") == null && Lineage(null) == null;

    // --selftest part: slider LoRAs that train only cross- or only self-attention are still Anima's; Wan's and SDXL's stay theirs;
    // an H3 LoRA in kohya names (attn_qkv_proj, mlp_fc1) is H3's
    static bool SelfTestLoraFamily()
    {
        var file = Path.Combine(Path.GetTempPath(), "imageforge-selftest-family.safetensors");
        string Family(params string[] keys)
        {
            var head = Encoding.UTF8.GetBytes("{" + string.Join(",", keys.Select((k, i) => $"\"{k}\":{{\"dtype\":\"F32\",\"shape\":[1],\"data_offsets\":[{i * 4},{i * 4 + 4}]}}")) + "}");
            using (var w = new BinaryWriter(File.Create(file))) { w.Write((long)head.Length); w.Write(head); w.Write(new byte[keys.Length * 4]); }
            return LoraFamily(file);
        }
        try
        {
            return Family("lora_unet_blocks_0_cross_attn_k_proj.lora_down.weight") == "anima"
                && Family("lora_unet_blocks_0_self_attn_q_proj.lora_down.weight", "lora_unet_blocks_0_mlp_layer1.lora_up.weight") == "anima"
                && Family("diffusion_model.blocks.0.self_attn.q.lora_A.weight", "diffusion_model.blocks.0.cross_attn.k.lora_A.weight", "diffusion_model.blocks.0.ffn.0.lora_A.weight") == "wan"
                && Family("lora_unet_input_blocks_4_1_transformer_blocks_0_attn1_to_q.lora_down.weight") == "sdxl"
                && Family("lora_unet_blocks_0_attn_qkv_proj.lora_down.weight", "lora_unet_blocks_0_mlp_fc1.lora_down.weight") == "minimax_h3"
                && Family("diffusion_model.layers.0.proj.ffn_up_gate.lora_A.weight") == "moss_image"
                && Family("lora_unet_layers_0_proj_ffn_down.lora_down.weight") == "moss_image";
        }
        finally { try { File.Delete(file); } catch { } }
    }

    void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LeavingModel();
        negativeUnlocked = false;
        foreach (var r in QualityGroup.Children.OfType<RadioButton>())
            ((TextBlock)((StackPanel)r.Content).Children[1]).Text = $"{StepsFor((string)r.Tag)} steps";
        if (LoraSection != null) { LoraSection.IsEnabled = !Flux; FillLoras(); }
        ShowVideoOptions();
        UpdateLoraChip();
        if (ModelNote != null)
        {
            ModelNote.Text = Qwen ? "Qwen Research Licence: personal and research use only, not commercial. In Edit photo, describe the change (\"make it sunset\")."
                    + (Named("noctq") ? " Start prompts with \"An anime illustration of…\"." : "")
                : Krea ? "Krea 2: realistic photos and art; the negative prompt isn't used. Check the model's licence before commercial use."
                : Anima ? "Anima: anime and illustration. Tags (1girl, masterpiece) or plain sentences both work; a quality negative prompt is added for you."
                : Moss ? "MOSS-Image: photos and art from long, detailed prompts, with clear lettering (put words to show in quotes). Apache 2.0 licence."
                : H3 ? "MiniMax H3: video with its own sound, 24 frames a second. Describe what happens, how the camera moves and what you hear. "
                    + "Slow on small graphics cards; check the MiniMax H3 licence for where it may be used."
                : PictureVideo ? "Wan 2.1 picture-to-video: the clip starts on your picture. Describe what moves and how the camera moves."
                : Wan ? "Wan 2.1 video, 16 frames a second. A clip takes a few minutes; describe the movement and the camera."
                : Flux ? "Good at readable text. Slower; the negative prompt isn't used." : "";
            ModelNote.Visibility = ModelNote.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowBoosters();
        ShowParts();
        if (loadingOptions || ModelBox.SelectedItem == null) { profileModel = ModelBox.SelectedItem as string ?? profileModel; return; }   // on startup keep the saved guidance and sampler
        var p = Preset;
        CfgSlider.Value = p.Cfg; SamplerBox.SelectedItem = p.Sampler; SchedulerBox.SelectedItem = p.Scheduler;
        StepsSlider.Value = StepsFor(Picked(QualityGroup));
        ArrivedAtModel();       // a model's own saved settings win over the recommendation
        ShowBoosters();
        ShowParts();
        RefreshGuard();
        UpdateNegative();
        ShowRefStyle();
    }

    static void Fill(ComboBox box, IEnumerable<string> list, string saved, string prefer)
    {
        var items = list.ToArray();
        box.ItemsSource = items;
        box.SelectedItem = items.Contains(saved) ? saved : items.FirstOrDefault(i => i.Contains(prefer, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
    }

    // graphics card shown in the header, e.g. "NVIDIA GeForce RTX 3060 · 12 GB"
    async Task LoadSystem()
    {
        try
        {
            var dev = JsonNode.Parse(await http.GetStringAsync("/system_stats"))?["devices"]?[0];
            var name = (dev?["name"]?.GetValue<string>() ?? "").Split(" : ")[0];   // "cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync"
            if (name.StartsWith("cuda:")) name = name[(name.IndexOf(' ') + 1)..];
            double gb = (dev?["vram_total"]?.GetValue<double>() ?? 0) / (1L << 30);
            Pill.ToolTip = gb > 0 ? $"{name}  ·  {gb:0} GB" : name;
        }
        catch { }
    }

    // create: empty latent -> sampler. edit: photo -> latent (-> noise mask) -> sampler at partial denoise,
    // and with a mask the result is blended back so unpainted pixels stay exactly as they were.
    // plain: no style, no LoRA. count: images to make (0 = the Number of images setting)
    // prompt, negative: the words as they were when Generate was pressed (default: the boxes now)
    JsonObject Graph(long seed, string image = null, bool masked = false, double? denoise = null, bool plain = false, int count = 0, string[] refs = null, string looks = null,
        string prompt = null, string negative = null)
    {
        prompt = (prompt ?? PromptBox.Text).Trim(); negative = (negative ?? NegativeBox.Text).Trim();
        int n = count > 0 ? count : int.Parse(Picked(CountGroup));
        var g = new JsonObject();
        JsonArray model, clip, vae;
        if (Family is string family)
        {
            var (te, vaeFile, type) = family switch
            {
                "anima" => (TeFor(family), VaeFor(family), "stable_diffusion"), _ => (TeFor(family), VaeFor(family), family),   // Anima's loader type, per ComfyUI's template
            };
            g["4"] = UnetNode(separate[(string)ModelBox.SelectedItem].Unet);
            g["44"] = Node("CLIPLoader", new() { ["clip_name"] = te, ["type"] = type });
            g["45"] = Node("VAELoader", new() { ["vae_name"] = vaeFile });
            (model, clip, vae) = (Link("4", 0), Link("44", 0), Link("45", 0));
        }
        else
        {
            g["4"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = (string)ModelBox.SelectedItem });
            (model, clip, vae) = (Link("4", 0), Link("4", 1), Link("4", 2));
        }
        var words = prompt + (looks is { Length: > 0 } ? ", " + looks : "");        // looks: what the reference character looks like
        var rawPrompt = plain ? words : CurrentStyle.Apply(words);
        var activeLoras = plain ? new List<(string, double)>() : AllActiveLoras(rawPrompt, out rawPrompt);

        int loraNo = 1;
        foreach (var (loraName, strength) in activeLoras)
        {
            var idStr = $"lora{loraNo++}";          // named apart from the numbered nodes, however many LoRAs there are
            g[idStr] = Node("LoraLoader", new() { ["model"] = model, ["clip"] = clip, ["lora_name"] = loraName, ["strength_model"] = strength, ["strength_clip"] = strength });
            (model, clip) = (Link(idStr, 0), Link(idStr, 1));
        }

        g["6"] = Node("CLIPTextEncode", new() { ["text"] = rawPrompt, ["clip"] = clip });
        g["7"] = Node("CLIPTextEncode", new() { ["text"] = Flux ? "" : string.Join(", ", new[] { negative, CurrentStyle.NegativeFor(AnimeModel), Anima ? AnimaNegative : "" }.Where(t => t != "")), ["clip"] = clip.DeepClone() });
        g["3"] = Node("KSampler", new()
        {
            ["seed"] = seed, ["steps"] = Steps, ["cfg"] = CfgSlider.Value,
            ["sampler_name"] = (string)SamplerBox.SelectedItem, ["scheduler"] = (string)SchedulerBox.SelectedItem,
            ["denoise"] = image == null ? 1 : Math.Round(denoise ?? StrengthSlider.Value, 2),
            ["model"] = model, ["positive"] = Link("6", 0), ["negative"] = Link("7", 0), ["latent_image"] = Link(image == null ? "5" : "12", 0)
        });
        g["8"] = Node("VAEDecode", new() { ["samples"] = Link("3", 0), ["vae"] = vae.DeepClone() });
        g["9"] = Node("SaveImage", new() { ["filename_prefix"] = image == null ? "forge/img" : "forge/edit", ["images"] = Link(masked ? "15" : "8", 0) });
        if (image == null)
        {
            var (width, height) = PictureSize();
            g["5"] = Node(Flux ? "EmptySD3LatentImage" : "EmptyLatentImage", new() { ["width"] = width, ["height"] = height, ["batch_size"] = n });
            if (refs is { Length: > 0 }) UseReferences(g, refs, clip, vae, rawPrompt);
            return g;
        }
        g["10"] = Node("LoadImage", new() { ["image"] = image });                 // output 1 = mask from the PNG's alpha
        g["11"] = Node("VAEEncode", new() { ["pixels"] = Link("10", 0), ["vae"] = vae.DeepClone() });
        if (Qwen && !masked && !plain)
        {
            // an instruction ("make it sunset") stays an instruction: the style is a short hint, not its wording wrapped around it
            AllActiveLoras(prompt, out var instruction);
            if (CurrentStyle.Key != "none") instruction += $" ({CurrentStyle.Label} style)";
            // Qwen-Image 2.1 edits by instruction: the photo goes in as a reference, and the picture is redrawn from it
            g["6"] = Node("TextEncodeQwenImage21", new()
            {
                ["clip"] = clip.DeepClone(), ["prompt"] = instruction, ["negative_prompt"] = negative,
                ["vae"] = vae.DeepClone(), ["resolution"] = 1024, ["images.image_1"] = Link("10", 0),
            });
            g.Remove("7");
            var k = (JsonObject)g["3"]["inputs"];
            k["positive"] = Link("6", 0); k["negative"] = Link("6", 1); k["denoise"] = 1.0;
            g["12"] = Node("RepeatLatentBatch", new() { ["samples"] = Link("6", 2), ["amount"] = n });
            g.Remove("11");
            return g;
        }
        g["12"] = Node("RepeatLatentBatch", new() { ["samples"] = Link(masked ? "13" : "11", 0), ["amount"] = n });
        if (masked)
        {
            g["13"] = Node("SetLatentNoiseMask", new() { ["samples"] = Link("11", 0), ["mask"] = Link("10", 1) });
            g["14"] = Node("RepeatImageBatch", new() { ["image"] = Link("10", 0), ["amount"] = n });
            g["15"] = Node("ImageCompositeMasked", new()
            {
                ["destination"] = Link("14", 0), ["source"] = Link("8", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false, ["mask"] = Link("10", 1)
            });
        }
        return g;
    }
    static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    static JsonObject UnetNode(string unet) => unet.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        ? Node("UnetLoaderGGUF", new() { ["unet_name"] = unet })
        : Node("UNETLoader", new() { ["unet_name"] = unet, ["weight_dtype"] = "default" });
    static JsonObject ClipNode(string te, string type) => te.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        ? Node("CLIPLoaderGGUF", new() { ["clip_name"] = te, ["type"] = type })
        : Node("CLIPLoader", new() { ["clip_name"] = te, ["type"] = type });
    static JsonArray Link(string node, int slot) => new() { node, slot };

    void StopServer()
    {
        try { if (server != null && !server.HasExited) server.Kill(true); } catch { }
    }

    // a dropdown's choices in /object_info: ["a", "b"] first (older nodes), or ["COMBO", {"options": [...]}] (newer nodes)
    static IEnumerable<string> ComboOptions(JsonNode input) =>
        (input?[0] as JsonArray ?? input?[1]?["options"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(n => n != null) ?? [];
}
