// MiniMax H3 from a video, and long videos.
// Extend: H3 goes on from the clip's last 1.6 s (anchored as a guide, with its sound) and the new part is joined on.
// Replace the character / reference: H3's ref2va model sees the video as <Video 1> and the picture as <Picture 1>;
// replacing adds akatz-ai's Character Swap LoRA, the recipe its model card gives.
// Restyle: H3's Fun ControlNet holds the new video to the old one's pose (DWPose) or outlines (Canny).
// Longer than 10 s: made in 10-second parts, each a prompt of its own that goes on from the end of the one before
// (Job.Next), then ComfyUI's Concatenate Video joins them part by part on disk.
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageForge;

public partial class MainWindow
{
    record VideoInfo(int Width, int Height, double Fps, double Seconds, bool Sound, bool EmberMade);
    string sourceVideo;                 // the video chosen under From a video, on disk
    VideoInfo sourceInfo;
    byte[] characterPicture;            // the new character (replace) or a picture the prompt can name (reference)
    const int PartFrames = 243, Overlap = 39;       // 10 s parts; each new part starts on the last 39 frames (1.6 s) of the one before

    static readonly (string Key, string Label)[] SourceUses =
    {
        ("extend", "Extend it: go on from where it ends"),
        ("swap", "Replace the person in it with my character"),
        ("reference", "Use it as a reference for a new video"),
        ("pose", "Restyle it, keeping the motion"),
        ("outline", "Restyle it, keeping the shapes"),
    };
    string SourceUse => sourceVideo != null && H3 && VideoOn && SourceUseBox.SelectedIndex >= 0 ? SourceUses[SourceUseBox.SelectedIndex].Key : null;

    string RefUnet => separate.Values.FirstOrDefault(s => s.Family == "minimax_h3" && s.Unet.Contains("ref2va", StringComparison.OrdinalIgnoreCase)).Unet;
    string SwapLora => loras.FirstOrDefault(l => l.Contains("character_swap", StringComparison.OrdinalIgnoreCase));
    string ControlPatch => Directory.Exists(Path.Combine(ModelsDir, "model_patches"))
        ? Directory.GetFiles(Path.Combine(ModelsDir, "model_patches")).Select(Path.GetFileName).FirstOrDefault(f => f.Contains("minimax_h3_fun_controlnet", StringComparison.OrdinalIgnoreCase)) : null;

    // what each use needs that isn't there, or null
    string SourceMissing(string use) => use switch
    {
        "swap" when RefUnet == null || SwapLora == null => "Replacing needs the MiniMax H3 reference model (minimax_h3_ref2va) and the Character Swap LoRA (h3_character_swap).",
        "swap" when characterPicture == null => "Choose the character picture first.",
        "reference" when RefUnet == null => "Needs the MiniMax H3 reference model (minimax_h3_ref2va) in diffusion_models.",
        "pose" or "outline" when ControlPatch == null => "Restyling needs MiniMax H3's Fun ControlNet (minimax_h3_fun_controlnet) in models\\model_patches.",
        "pose" when !nodeNames.Contains("DWPreprocessor") => "Keeping the motion needs Pose control (Settings > Plugins).",
        "extend" when sourceInfo != null && Math.Abs(sourceInfo.Fps - 24) > 0.5 => $"This video runs at {sourceInfo.Fps:0.#} frames a second: extending works on 24, like MiniMax H3's own clips.",
        _ => null,
    };

    void ShowSourceOptions()
    {
        if (SourceSection == null) return;
        if (SourceUseBox.Items.Count == 0) { foreach (var u in SourceUses) SourceUseBox.Items.Add(u.Label); SourceUseBox.SelectedIndex = 0; }
        SourceSection.Visibility = VideoOn && H3 ? Visibility.Visible : Visibility.Collapsed;
        SourceArea.Visibility = sourceVideo != null ? Visibility.Visible : Visibility.Collapsed;
        ChooseSourceBtn.Visibility = sourceVideo == null ? Visibility.Visible : Visibility.Collapsed;
        var use = SourceUses[Math.Max(0, SourceUseBox.SelectedIndex)].Key;
        CharacterArea.Visibility = use is "swap" or "reference" ? Visibility.Visible : Visibility.Collapsed;
        SourceStartRow.Visibility = use == "extend" ? Visibility.Collapsed : Visibility.Visible;
        CharacterCard.Visibility = characterPicture != null ? Visibility.Visible : Visibility.Collapsed;
        ChooseCharacterBtn.Visibility = characterPicture == null ? Visibility.Visible : Visibility.Collapsed;
        SourceNote.Text = SourceMissing(use) ?? use switch
        {
            "extend" => "Adds the Length you pick to the end of the video, going on from its last moments with the prompt. The start picture is skipped.",
            "swap" => "The person in the video becomes your character, with the same moves and sound. Say who in the prompt if there are several (\"the man on the left\"). Uses 10 seconds from Start at. Works best on real footage of people; a character picture on a plain background keeps the video's own scene.",
            "reference" => "The prompt can name the video as <Video 1> and the picture as <Picture 1>, for example: the woman from <Picture 1> dancing like in <Video 1>.",
            "pose" => "A new video with the same body movement, looking the way the prompt says. Uses 10 seconds from Start at and keeps the sound.",
            _ => "A new video with the same shapes and layout, looking the way the prompt says. Uses 10 seconds from Start at and keeps the sound.",
        };
        LongNote.Visibility = VideoOn && H3 && int.TryParse(Picked(LengthGroup), out var f) && f > 161 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SourceUse_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (IsLoaded) ShowSourceOptions(); }

    // where in the video the uses that follow it begin, in seconds ("1:05" works too); they take up to 10 s from there
    double SourceStart
    {
        get
        {
            var t = SourceStartBox.Text.Trim().Replace(',', '.');
            var mmss = Regex.Match(t, @"^(\d+):(\d{1,2}(?:\.\d+)?)$");
            if (mmss.Success) return int.Parse(mmss.Groups[1].Value) * 60 + double.Parse(mmss.Groups[2].Value, CultureInfo.InvariantCulture);
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        }
    }
    void Length_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowSourceOptions(); }

    // ---------------- choosing the video ----------------
    static readonly string[] VideoExtensions = { ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v" };
    static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a video", Filter = "Videos|*.mp4;*.mov;*.webm;*.mkv;*.avi;*.m4v" };
        if (dlg.ShowDialog(this) == true) _ = SetSource(dlg.FileName);
    }

    void PasteSource_Click(object sender, RoutedEventArgs e)
    {
        var file = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsVideoFile) : null;
        if (file != null) _ = SetSource(file);
        else Note("Copy a video file in Explorer first.", true);
    }

    void Source_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetData(DataFormats.FileDrop) as string[])?.Any(IsVideoFile) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Source_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(IsVideoFile) is string f) _ = SetSource(f);
    }

    void RemoveSource_Click(object sender, RoutedEventArgs e) { sourceVideo = null; sourceInfo = null; ShowSourceOptions(); }

    async Task SetSource(string path, string use = null)
    {
        try
        {
            var info = await ProbeVideo(path);
            if (info.Seconds < 0.5) { Note("That video is too short.", true); return; }
            (sourceVideo, sourceInfo) = (path, info);
            SourceName.Text = Path.GetFileName(path);
            SourceAbout.Text = $"{info.Width} × {info.Height} · {info.Seconds:0.#} s{(info.Sound ? " · with sound" : "")}";
            double aspect = (double)info.Width / info.Height;        // the new video takes the shape closest to it
            Pick(VideoSizeGroup, aspect > 1.2 ? "832x480" : aspect < 0.83 ? "480x832" : "624x624", "832x480");
            if (use != null) SourceUseBox.SelectedIndex = Array.FindIndex(SourceUses, u => u.Key == use);
            ShowSourceOptions();
        }
        catch (Exception ex) { Note("Couldn't read that video: " + ex.Message, true); }
    }

    // size, frame rate, length and sound, read with the PyAV that ComfyUI itself uses for video
    async Task<VideoInfo> ProbeVideo(string path)
    {
        // the size as shown: a phone video filmed upright is stored on its side with a rotation, which ComfyUI applies to the frames
        const string script = "import av,sys\nc=av.open(sys.argv[1]);s=c.streams.video[0]\n" +
            "d=c.duration/1e6 if c.duration else float(s.duration*s.time_base)\nw,h=s.codec_context.width,s.codec_context.height\n" +
            "f=next(c.decode(s),None)\nif f is not None and int(round((f.rotation or 0)//90))%2: w,h=h,w\n" +
            "print('VIDEO',w,h,float(s.average_rate or s.guessed_rate),d,int(len(c.streams.audio)>0)," +
            "int(s.codec_context.name=='h264' and int(s.codec_context.color_trc or 0)==13))";      // ComfyUI's own encoding: H.264 tagged sRGB
        var (_, output) = await Run(Python, new[] { "-I", "-c", script, path });
        var m = Regex.Match(output, @"VIDEO (\d+) (\d+) ([\d.]+) ([\d.]+) ([01]) ([01])");
        if (!m.Success) throw new Exception(output.Trim().Split('\n').LastOrDefault() ?? "not a video");
        double D(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return new VideoInfo(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), D(3), D(4), m.Groups[5].Value == "1", m.Groups[6].Value == "1");
    }

    // the viewer's Extend: this clip, an H3 model, and the Video tab
    void ExtendViewed_Click(object sender, RoutedEventArgs e)
    {
        if (viewing is not { IsVideo: true } s || !File.Exists(s.VideoPath)) return;
        var item = videoModels.Where(i => separate.TryGetValue(i, out var p) && p.Family == "minimax_h3")
            .OrderBy(i => separate[i].Unet.Contains("ref2va", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (item == null) { ViewerNote.Text = "Extending needs a MiniMax H3 model."; return; }
        var path = s.VideoPath;
        CloseViewer();
        if (!(VideoOn && H3)) UseModel(item, true);
        _ = SetSource(path, "extend");
        PromptBox.Focus();
        Note("Describe what happens next, pick how much longer under Length, then press Make video.");
    }

    // ---------------- the character picture ----------------
    void ChooseCharacter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the character", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) == true) SetCharacter(dlg.FileName);
    }

    void Character_Drop(object sender, DragEventArgs e) { if (DroppedPicture(e) is string path) SetCharacter(path); }

    void SetCharacter(string path)
    {
        try { SetCharacter(Oriented(path), Path.GetFileName(path)); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); }
    }

    void PasteCharacter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClipboardPicture() is { } p) SetCharacter(p.Image, p.Name ?? "Pasted picture");
            else Note("Copy a picture first: Copy image in a browser, a screenshot, or a file in Explorer.", true);
        }
        catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); }
    }

    void SetCharacter(BitmapSource img, string name)
    {
        double scale = Math.Min(1, 1280.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        characterPicture = PngOf(img);
        CharacterThumb.ImageSource = img;
        CharacterName.Text = name;
        ShowSourceOptions();
    }

    void RemoveCharacter_Click(object sender, RoutedEventArgs e) { characterPicture = null; ShowSourceOptions(); }

    // ---------------- making it ----------------
    // the frame counts of the parts: one part up to 10 s; longer, 10-second parts that each also redo the last 1.6 s of the one before.
    // Extending, even the first part goes on from a video.
    static List<int> PartsFor(int frames, bool goOn)
    {
        static int Grid(int n) { n = Math.Max(56, n); while (n % 17 != 5) n++; return n; }
        var parts = new List<int>();
        for (int left = frames; left > 0;)
        {
            int len = Grid(Math.Min(PartFrames, goOn ? left + Overlap : left));
            parts.Add(len);
            left -= goOn ? len - Overlap : len;
            goOn = true;
        }
        return parts;
    }

    // ComfyUI's name for a file: in its output folder, by the [output] tag; anywhere else it's uploaded to input/forge first
    async Task<string> ComfyVideoName(string path, Job job)
    {
        var rel = Path.GetRelativePath(Output, path);
        if (!rel.StartsWith("..") && !Path.IsPathRooted(rel)) return rel.Replace('\\', '/') + " [output]";
        var stored = await Upload(await File.ReadAllBytesAsync(path), $"forge_{Guid.NewGuid():N}{Path.GetExtension(path).ToLowerInvariant()}");
        job.Uploads.Add(Path.Combine(Root, "ComfyUI", "input", stored.Replace('/', Path.DirectorySeparatorChar)));
        return stored;
    }

    async Task MakeH3Video(long seed)
    {
        var model = (string)ModelBox.SelectedItem;
        if (h3AudioVae == null) { Note("MiniMax H3 needs its audio VAE (minimax_h3_audio_vae_fp32) in the vae folder.", true); return; }
        var use = SourceUse;
        if (use != null && SourceMissing(use) is string missing) { Note(missing, true); return; }
        var (src, info) = (sourceVideo, sourceInfo);
        var size = Picked(VideoSizeGroup).Split('x').Select(v => (int.Parse(v) + 16) / 32 * 32).ToArray();     // H3 works in steps of 32 pixels
        if (use == "extend")
        {
            // the new part at the video's shape and Ember's usual size (832 × 480's pixels, in steps of 32), then scaled up to the video's:
            // at 1280 × 704 H3 came out blocky in tests, and each step took 2.5 min instead of 27 s
            double fit = Math.Min(1, Math.Sqrt(832.0 * 480 / ((double)info.Width * info.Height)));
            size = new[] { Math.Max(32, (int)(info.Width * fit) / 32 * 32), Math.Max(32, (int)(info.Height * fit) / 32 * 32) };
        }
        // a video H3 can't match exactly (1080p, or not in steps of 32): the new frames are scaled to its size, so the joined video
        // keeps the original's size
        bool rescale = use == "extend" && (size[0] != info.Width || size[1] != info.Height);
        // the join needs every piece encoded alike: a clip ComfyUI encoded (Ember's) joins as it is, any other video is rebuilt
        // from its frames, which ComfyUI holds in memory as floats (and copies once): keep that under about 6 GB
        bool ours = info?.EmberMade == true;
        if (use == "extend" && !ours && info.Seconds * 24 * info.Width * info.Height * 3 * 4 * 2 > 6e9)
        {
            Note($"That video is too long to extend at {info.Width} × {info.Height}: about {6e9 / (24.0 * info.Width * info.Height * 24):0} s is the most. Trim it first.", true);
            return;
        }
        double from0 = use is "swap" or "reference" or "pose" or "outline" ? Math.Clamp(SourceStart, 0, Math.Max(0, info.Seconds - 1)) : 0;
        int frames = H3Frames(int.Parse(Picked(LengthGroup))), steps = Steps;
        // the uses that follow a video take its length, up to 10 s
        if (use is "swap" or "pose" or "outline") { frames = Math.Max(22, Math.Min(PartFrames, (int)((info.Seconds - from0) * 24))); while (frames % 17 != 5) frames--; }
        if (use == "reference") frames = Math.Min(frames, PartFrames);
        var parts = use is null or "extend" ? PartsFor(frames, use == "extend") : new List<int> { frames };
        var rawPrompt = CurrentStyle.Apply(PromptBox.Text.Trim());
        var activeLoras = AllActiveLoras(rawPrompt, out rawPrompt).ToList();
        var prompt = WithCamera(rawPrompt);
        if (use == "swap")
        {
            // the wording of the Character Swap LoRA's own example workflow; the prompt, if any, says who is replaced ("the man on the left")
            var typed = Regex.Replace(PromptBox.Text, @"<lora:[^>]*>", "").Trim().TrimEnd('.');      // what was typed: no style words, no LoRA tags
            var who = typed == "" ? "the person" : typed;
            prompt = $"Replace only {who} in <Video 1> with the character in <Picture 1>. Keep the replacement character's identity, outfit, and art style from <Picture 1>. " +
                "Preserve the source video's camera, framing, background, lighting, objects, and all other people. " +
                "Match the replaced person's position, scale, pose, and movement throughout the clip. Do not show the reference sheet or its background.";
            activeLoras.RemoveAll(l => l.Item1 == SwapLora);
            activeLoras.Add((SwapLora, 1.0));
        }
        var (start, end, from, character) = (use is null ? startPicture : null, use is null or "pose" or "outline" ? endPicture : null, startName, characterPicture);
        double motion = MotionSlider.Value;
        var negative = string.Join(", ", new[] { NegativeBox.Text.Trim(), CurrentStyle.Negative }.Where(t => t != ""));
        double cfg = CfgSlider.Value;
        string sampler = (string)SamplerBox.SelectedItem, scheduler = (string)SchedulerBox.SelectedItem;
        string te = TeFor("minimax_h3"), vae = VaeFor("minimax_h3"), audioVae = h3AudioVae, patch = ControlPatch;
        string unet = use is "swap" or "reference" ? RefUnet : separate[model].Unet;
        string name = $"vid_{DateTime.Now:yyMMdd_HHmmss_fff}", sourceName = null;
        var partFiles = new List<string>();          // ComfyUI's names of the parts made so far
        bool goOnFromSource = use == "extend";

        async Task<JsonObject> Part(Job job, int k)
        {
            int len = parts[k];
            bool last = k == parts.Count - 1, goOn = k > 0 || goOnFromSource;
            if (parts.Count > 1) job.Verb = $"Filming part {k + 1} of {parts.Count} · step";
            sourceName ??= src == null || use == null ? null : await ComfyVideoName(src, job);
            var g = new JsonObject
            {
                ["4"] = UnetNode(unet),
                ["44"] = ClipNode(te, "minimax"),
                ["45"] = Node("VAELoader", new() { ["vae_name"] = vae }),
                ["48"] = Node("VAELoader", new() { ["vae_name"] = audioVae }),
            };
            var m = "4";            // the node whose model goes on: a fresh link each use, since a JSON node can't sit in two places
            int loraNo = 1;
            foreach (var (loraName, strength) in activeLoras)        // H3 LoRAs change the model only: its text encoder isn't trained
            {
                var idStr = $"lora{loraNo++}";
                g[idStr] = Node("LoraLoaderModelOnly", new() { ["model"] = Link(m, 0), ["lora_name"] = loraName, ["strength_model"] = strength });
                m = idStr;
            }
            if (motion != 12)            // H3's own shift is 12: higher spends more of the steps on big changes, so more motion
            {
                g["50"] = Node("MiniMaxH3SigmaShift", new() { ["model"] = Link(m, 0), ["shift_video"] = motion, ["shift_audio"] = 3.0 });
                m = "50";
            }
            // the video the uses follow: from the chosen second, up to this clip's length
            if (use is "swap" or "reference" or "pose" or "outline")
            {
                g["90"] = Node("LoadVideo", new() { ["file"] = sourceName });
                g["91"] = Node("Video Slice", new() { ["video"] = Link("90", 0), ["start_time"] = from0, ["duration"] = len / 24.0, ["strict_duration"] = false });
                g["92"] = Node("GetVideoComponents", new() { ["video"] = Link("91", 0) });
            }
            if (use is "pose" or "outline")
            {
                g["83"] = use == "pose"
                    ? Node("DWPreprocessor", new()
                    {
                        ["image"] = Link("92", 0), ["detect_hand"] = "enable", ["detect_body"] = "enable", ["detect_face"] = "enable", ["resolution"] = 512,
                        ["bbox_detector"] = "yolox_l.onnx", ["pose_estimator"] = "dw-ll_ucoco_384.onnx", ["scale_stick_for_xinsr_cn"] = "disable",
                    })
                    : Node("Canny", new() { ["image"] = Link("92", 0), ["low_threshold"] = 0.4, ["high_threshold"] = 0.8 });
                g["84"] = Node("ModelPatchLoader", new() { ["name"] = patch });
                g["85"] = Node("MiniMaxH3FunControlNetApply", new()
                {
                    ["model"] = Link(m, 0), ["model_patch"] = Link("84", 0), ["vae"] = Link("45", 0), ["strength"] = 1.0,
                    ["start_percent"] = 0.0, ["end_percent"] = 1.0, ["control_video"] = Link("83", 0),
                });
                m = "85";
            }
            JsonObject h3;
            if (use is "swap" or "reference")
            {
                h3 = new JsonObject
                {
                    ["clip"] = Link("44", 0), ["vae"] = Link("45", 0), ["audio_vae"] = Link("48", 0), ["prompt"] = prompt, ["width"] = size[0], ["height"] = size[1],
                    ["length"] = len, ["ref_image_size"] = "match", ["ref_videos.ref_video_0"] = Link("92", 0),
                };
                if (info.Sound) h3["ref_video_audios.ref_video_audio_0"] = Link("92", 1);
                if (character != null) { g["93"] = Node("LoadImage", new() { ["image"] = await Upload(character, job) }); h3["ref_images.ref_image_0"] = Link("93", 0); }
                g["6"] = Node("MiniMaxH3ReferenceToVideo", h3);
            }
            else
            {
                h3 = new JsonObject
                {
                    ["clip"] = Link("44", 0), ["vae"] = Link("45", 0), ["prompt"] = prompt, ["width"] = size[0], ["height"] = size[1], ["length"] = len,
                };
                if (k == 0 && start != null) { g["60"] = Node("LoadImage", new() { ["image"] = await Upload(start, job) }); h3["first_frame"] = Link("60", 0); }
                // going on: the anchored frames alone only nudge H3; their first one as the start picture is what it reads
                // (through Qwen too), and the new part then carries on seamlessly
                if (goOn) h3["first_frame"] = Link("79", 0);
                if (last && end != null) { g["61"] = Node("LoadImage", new() { ["image"] = await Upload(end, job) }); h3["last_frame"] = Link("61", 0); }
                g["6"] = Node("MiniMaxH3ImageToVideo", h3);
            }
            var positive = Link("6", 0);
            // going on: the previous part's (or the extended video's) last 1.6 s, picture and sound, anchored at the start of this part
            bool sound = info?.Sound != false;            // extending a silent video: no sound anchored, and none added (the join needs all or none)
            if (goOn)
            {
                g["70"] = Node("LoadVideo", new() { ["file"] = k == 0 ? sourceName : partFiles[k - 1] });
                // only its last 3 s is decoded: a whole long or big video would fill the memory
                g["69"] = Node("Video Slice", new() { ["video"] = Link("70", 0), ["start_time"] = -3.0, ["duration"] = 0.0, ["strict_duration"] = false });
                g["71"] = Node("GetVideoComponents", new() { ["video"] = Link("69", 0) });
                g["72"] = Node("ImageFromBatch", new() { ["image"] = Link("71", 0), ["batch_index"] = -Overlap, ["length"] = Overlap });
                g["79"] = Node("ImageFromBatch", new() { ["image"] = Link("71", 0), ["batch_index"] = -Overlap, ["length"] = 1 });
                var guide = new JsonObject
                {
                    ["positive"] = positive, ["vae"] = Link("45", 0), ["audio_vae"] = Link("48", 0), ["latent"] = Link("6", 1), ["image"] = Link("72", 0), ["frame_idx"] = 0,
                };
                if (sound)
                {
                    g["73"] = Node("TrimAudioDuration", new() { ["audio"] = Link("71", 1), ["start_index"] = -Overlap / 24.0, ["duration"] = Overlap / 24.0 });
                    guide["audio"] = Link("73", 0);
                }
                g["74"] = Node("MiniMaxH3AddGuide", guide);
                positive = Link("74", 0);
            }
            if (cfg > 1)
            {
                g["7"] = Node("CLIPTextEncode", new() { ["text"] = negative, ["clip"] = Link("44", 0) });
                g["51"] = Node("CFGGuider", new() { ["model"] = Link(m, 0), ["positive"] = positive, ["negative"] = Link("7", 0), ["cfg"] = cfg });
            }
            else g["51"] = Node("BasicGuider", new() { ["model"] = Link(m, 0), ["conditioning"] = positive });
            g["52"] = Node("RandomNoise", new() { ["noise_seed"] = seed + k });
            g["53"] = Node("KSamplerSelect", new() { ["sampler_name"] = sampler });
            g["54"] = Node("BasicScheduler", new() { ["model"] = Link(m, 0), ["scheduler"] = scheduler, ["steps"] = steps, ["denoise"] = 1.0 });
            g["3"] = Node("SamplerCustomAdvanced", new()
            {
                ["noise"] = Link("52", 0), ["guider"] = Link("51", 0), ["sampler"] = Link("53", 0), ["sigmas"] = Link("54", 0), ["latent_image"] = Link("6", 1),
            });
            g["8"] = Node("VAEDecode", new() { ["samples"] = Link("3", 0), ["vae"] = Link("45", 0) });
            g["49"] = Node("VAEDecodeAudio", new() { ["samples"] = Link("3", 0), ["vae"] = Link("48", 0) });
            // following a video with sound: its own sound stays (H3's would only approximate it)
            var audio = use is "swap" or "pose" or "outline" && info.Sound ? Link("92", 1) : Link("49", 0);
            if (rescale) g["81"] = Node("ImageScale", new() { ["image"] = Link("8", 0), ["upscale_method"] = "lanczos", ["width"] = info.Width, ["height"] = info.Height, ["crop"] = "center" });
            g["40"] = goOnFromSource && !info.Sound
                ? Node("CreateVideo", new() { ["images"] = Link(rescale ? "81" : "8", 0), ["fps"] = 24.0 })
                : Node("CreateVideo", new() { ["images"] = Link(rescale ? "81" : "8", 0), ["audio"] = audio, ["fps"] = 24.0 });   // H3 makes 24 frames a second
            if (!last)
            {
                // a part on the way: kept apart (no poster, so not in the gallery) until the last one joins them
                g["41"] = Node("SaveVideo", new() { ["video"] = Link("40", 0), ["filename_prefix"] = $"forge/parts/{name}_{k}", ["format"] = "auto", ["format.codec"] = "auto" });
                var file = $"forge/parts/{name}_{k}_00001_.mp4";
                partFiles.Add(file + " [output]");
                job.Uploads.Add(Path.Combine(Output, file.Replace('/', Path.DirectorySeparatorChar)));    // removed with the job
                job.Next = j => Part(j, k + 1);
            }
            else
            {
                // the pieces in order, each part after the first without the 1.6 s it redid
                var pieces = new List<JsonArray>();
                if (goOnFromSource)
                {
                    g["75"] = Node("LoadVideo", new() { ["file"] = sourceName });
                    if (ours) pieces.Add(Link("75", 0));
                    else
                    {
                        // another video's colour tags and encoding differ from the new parts': rebuilt from its frames, it joins
                        g["76"] = Node("GetVideoComponents", new() { ["video"] = Link("75", 0) });
                        g["78"] = info.Sound
                            ? Node("CreateVideo", new() { ["images"] = Link("76", 0), ["audio"] = Link("76", 1), ["fps"] = 24.0 })
                            : Node("CreateVideo", new() { ["images"] = Link("76", 0), ["fps"] = 24.0 });
                        pieces.Add(Link("78", 0));
                    }
                }
                for (int i = 0; i < partFiles.Count; i++)
                {
                    g[$"p{i}"] = Node("LoadVideo", new() { ["file"] = partFiles[i] });
                    if (i > 0 || goOnFromSource)
                    {
                        g[$"ps{i}"] = Node("Video Slice", new() { ["video"] = Link($"p{i}", 0), ["start_time"] = Overlap / 24.0, ["duration"] = 0.0, ["strict_duration"] = false });
                        pieces.Add(Link($"ps{i}", 0));
                    }
                    else pieces.Add(Link($"p{i}", 0));
                }
                if (goOn)
                {
                    g["42"] = Node("Video Slice", new() { ["video"] = Link("40", 0), ["start_time"] = Overlap / 24.0, ["duration"] = 0.0, ["strict_duration"] = false });
                    pieces.Add(Link("42", 0));
                }
                else pieces.Add(Link("40", 0));
                var whole = pieces[0];
                if (pieces.Count > 1)
                {
                    var join = new JsonObject { ["codec"] = "auto" };
                    for (int i = 0; i < pieces.Count; i++) join[$"videos.video{i}"] = pieces[i];
                    g["43"] = Node("ConcatenateVideo", join);
                    whole = Link("43", 0);
                }
                g["41"] = Node("SaveVideo", new() { ["video"] = whole, ["filename_prefix"] = $"forge/clips/{name}", ["format"] = "auto", ["format.codec"] = "auto" });
                g["47"] = Node("ImageFromBatch", new() { ["image"] = Link("8", 0), ["batch_index"] = len / 2, ["length"] = 1 });
                g["9"] = Node("SaveImage", new() { ["filename_prefix"] = $"forge/{name}", ["images"] = Link("47", 0) });
            }
            var made = use switch
            {
                "extend" => $"Extended {Path.GetFileName(src)}",
                "swap" => $"Character replaced in {Path.GetFileName(src)}",
                "reference" => $"With {Path.GetFileName(src)} as reference",
                "pose" or "outline" => $"Restyled {Path.GetFileName(src)}",
                _ => start == null ? null : $"Started from {from}",
            };
            var meta = new Meta(prompt, cfg > 1 ? negative : "", seed, steps, cfg, sampler, scheduler, model, From: made);
            return new JsonObject
            {
                ["prompt"] = g, ["client_id"] = clientId,
                ["extra_data"] = new JsonObject { ["extra_pnginfo"] = new JsonObject { ["forge"] = JsonSerializer.SerializeToNode(meta) }, ["preview_method"] = "auto" },
            };
        }

        await Enqueue(1, job => Part(job, 0), parts.Count > 1 ? $"Filming part 1 of {parts.Count} · step" : "Filming · step", "Starting");
    }
}
