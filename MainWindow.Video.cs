// Video: Wan 2.1 text-to-video (the 1.3B model fits a 12 GB card). A clip is an MP4 in forge\clips plus a PNG poster frame with the
// same name in forge, so the gallery, favourites, search, delete and export all work through the poster.
// (Two folders because ComfyUI numbers files across every type in a folder: side by side they'd get different numbers.)
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageForge;

public partial class MainWindow
{
    string wanTe, wanVae;
    List<string> imageModels = new(), videoModels = new();
    bool showingVideoModels;
    bool Wan => Family is "wan" or "wan_i2v";
    bool PictureVideo => Family == "wan_i2v";           // Wan 2.1 Fun InP: the clip starts on a picture
    byte[] startPicture;                                // that picture, as PNG, at most 1280 px
    string startName;
    bool VideoOn => VideoMode.IsChecked == true;

    // Wan's own negative prompt, which it was trained with: oversaturated, overexposed, static, blurry, subtitles, worst quality,
    // JPEG artefacts, extra or fused fingers, badly drawn hands and faces, deformed limbs, cluttered or crowded background, walking backwards
    const string WanNegative = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，" +
        "多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

    // the model list for the current mode: picture models, or video models
    void FillModels()
    {
        if (ModelBox.SelectedItem is string current) { if (showingVideoModels) settings.VideoModel = current; else settings.Model = current; }
        showingVideoModels = VideoOn;
        Fill(ModelBox, showingVideoModels ? videoModels : imageModels, showingVideoModels ? settings.VideoModel : settings.Model, showingVideoModels ? "" : "krea");      // no saved choice: Krea 2, the best all-rounder
    }

    async Task MakeVideo(long seed)
    {
        var start = PictureVideo ? startPicture : null;
        if (PictureVideo && start == null) { Note("Choose the start picture first (above Video shape).", true); return; }
        var from = startName;
        var model = (string)ModelBox.SelectedItem;
        var size = Picked(VideoSizeGroup).Split('x');
        int frames = int.Parse(Picked(LengthGroup)), steps = Steps;
        var rawPrompt = CurrentStyle.Apply(PromptBox.Text.Trim());
        var activeLoras = AllActiveLoras(rawPrompt, out rawPrompt);
        var prompt = rawPrompt;
        var negative = string.Join(", ", new[] { NegativeBox.Text.Trim(), CurrentStyle.Negative }.Where(t => t != ""));
        double cfg = CfgSlider.Value;
        string sampler = (string)SamplerBox.SelectedItem, scheduler = (string)SchedulerBox.SelectedItem;
        await Enqueue(1, async job =>
        {
            var name = $"vid_{DateTime.Now:yyMMdd_HHmmss_fff}";
            var g = new JsonObject
            {
                ["4"] = Node("UNETLoader", new() { ["unet_name"] = separate[model].Unet, ["weight_dtype"] = "default" }),
                ["44"] = Node("CLIPLoader", new() { ["clip_name"] = TeFor("wan"), ["type"] = "wan" }),
                ["45"] = Node("VAELoader", new() { ["vae_name"] = VaeFor("wan") }),
                ["46"] = Node("ModelSamplingSD3", new() { ["model"] = Link("4", 0), ["shift"] = 8.0 }),
            };
            JsonArray m = Link("46", 0), clip = Link("44", 0);
            int loraNo = 1;
            foreach (var (loraName, strength) in activeLoras)
            {
                var idStr = $"lora{loraNo++}";
                g[idStr] = Node("LoraLoader", new() { ["model"] = m, ["clip"] = clip, ["lora_name"] = loraName, ["strength_model"] = strength, ["strength_clip"] = strength });
                (m, clip) = (Link(idStr, 0), Link(idStr, 1));
            }
            g["6"] = Node("CLIPTextEncode", new() { ["text"] = prompt, ["clip"] = clip });
            g["7"] = Node("CLIPTextEncode", new() { ["text"] = negative == "" ? WanNegative : negative + ", " + WanNegative, ["clip"] = clip.DeepClone() });
            g["3"] = Node("KSampler", new()
            {
                ["seed"] = seed, ["steps"] = steps, ["cfg"] = cfg, ["sampler_name"] = sampler, ["scheduler"] = scheduler, ["denoise"] = 1,
                ["model"] = m, ["positive"] = Link("6", 0), ["negative"] = Link("7", 0), ["latent_image"] = Link("5", 0),
            });
            if (start == null)
                g["5"] = Node("EmptyHunyuanLatentVideo", new() { ["width"] = int.Parse(size[0]), ["height"] = int.Parse(size[1]), ["length"] = frames, ["batch_size"] = 1 });
            else
            {
                // the picture becomes the first frame (centre-cropped to the clip's shape); the prompts pass through with it
                g["10"] = Node("LoadImage", new() { ["image"] = await Upload(start, job) });
                g["50"] = Node("WanFunInpaintToVideo", new()
                {
                    ["positive"] = Link("6", 0), ["negative"] = Link("7", 0), ["vae"] = Link("45", 0), ["width"] = int.Parse(size[0]), ["height"] = int.Parse(size[1]),
                    ["length"] = frames, ["batch_size"] = 1, ["start_image"] = Link("10", 0),
                });
                var k = (JsonObject)g["3"]["inputs"];
                k["positive"] = Link("50", 0); k["negative"] = Link("50", 1); k["latent_image"] = Link("50", 2);
            }
            g["8"] = Node("VAEDecode", new() { ["samples"] = Link("3", 0), ["vae"] = Link("45", 0) });
            g["40"] = Node("CreateVideo", new() { ["images"] = Link("8", 0), ["fps"] = 16.0 });          // Wan 2.1 makes 16 frames a second
            g["41"] = Node("SaveVideo", new() { ["video"] = Link("40", 0), ["filename_prefix"] = $"forge/clips/{name}", ["format"] = "auto", ["format.codec"] = "auto" });
            g["47"] = Node("ImageFromBatch", new() { ["image"] = Link("8", 0), ["batch_index"] = frames / 2, ["length"] = 1 });
            g["9"] = Node("SaveImage", new() { ["filename_prefix"] = $"forge/{name}", ["images"] = Link("47", 0) });
            var meta = new Meta(prompt, negative, seed, steps, cfg, sampler, scheduler, model, From: start == null ? null : $"Started from {from}");
            return new JsonObject
            {
                ["prompt"] = g, ["client_id"] = clientId,
                ["extra_data"] = new JsonObject { ["extra_pnginfo"] = new JsonObject { ["forge"] = JsonSerializer.SerializeToNode(meta) }, ["preview_method"] = "auto" },
            };
        }, "Filming · step", "Starting");
    }

    // ---------------- the start picture (picture-to-video) ----------------
    void ChooseStart_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the picture the clip starts from", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) == true) SetStart(dlg.FileName);
    }

    void Start_DragOver(object sender, DragEventArgs e) { e.Effects = DroppedPicture(e) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    void Start_Drop(object sender, DragEventArgs e) { if (DroppedPicture(e) is string path) SetStart(path); }

    void SetStart(string path)
    {
        BitmapSource img;
        try { img = Oriented(path); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); return; }
        double scale = Math.Min(1, 1280.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        startPicture = PngOf(img);
        startName = Path.GetFileName(path);
        StartThumb.ImageSource = img;
        StartName.Text = startName;
        StartSize.Text = $"{img.PixelWidth} × {img.PixelHeight}";
        StartCard.Visibility = Visibility.Visible;
        ChooseStartBtn.Visibility = Visibility.Collapsed;
        double aspect = (double)img.PixelWidth / img.PixelHeight;           // the clip takes the shape closest to the picture
        Pick(VideoSizeGroup, aspect > 1.2 ? "832x480" : aspect < 0.83 ? "480x832" : "624x624", "832x480");
    }

    // the viewer's Make a video: this picture, the picture-to-video model, and the Video tab
    void MakeVideoFromViewed_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        var item = videoModels.FirstOrDefault(i => separate.TryGetValue(i, out var s) && s.Family == "wan_i2v");
        if (item == null) { ViewerNote.Text = "Get the picture-to-video model (Wan 2.1 Fun InP) on the Models page first."; return; }
        var path = viewing.FilePath;
        CloseViewer();
        UseModel(item, true);
        SetStart(path);
        PromptBox.Focus();
        Note("Describe what should move and how the camera moves, then press Make video.");
    }

    // ---------------- clips in the viewer ----------------
    // a clip plays on a loop over its poster; the picture-only actions step aside
    void ShowClip(Shot s)
    {
        bool clip = s.IsVideo && File.Exists(s.VideoPath);
        DetailsTitle.Text = s.IsVideo ? "Video details" : "Image details";
        ViewerVideo.Visibility = clip ? Visibility.Visible : Visibility.Collapsed;
        ViewerVideo.Source = clip ? new Uri(s.VideoPath) : null;
        if (clip) ViewerVideo.Play();
        foreach (var b in new UIElement[] { EditThisBtn, MoreLikeThisBtn, UpscaleBtn, CutoutBtn, ExpandViewedBtn, DescribeBtn, CopyBtn, MakeVideoBtn, SecretViewedBtn })
            b.Visibility = s.IsVideo ? Visibility.Collapsed : Visibility.Visible;
    }

    void StopClip() { ViewerVideo.Stop(); ViewerVideo.Source = null; }     // also lets go of the file, so it can be deleted

    void ViewerVideo_Ended(object sender, RoutedEventArgs e) { ViewerVideo.Position = TimeSpan.Zero; ViewerVideo.Play(); }

    static string FileOf(Shot s) => s.IsVideo && File.Exists(s.VideoPath) ? s.VideoPath : s.FilePath;   // what to copy or drag out
}
