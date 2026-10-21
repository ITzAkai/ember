// Reference pictures (Create): a character or a place that new pictures keep exactly as it is.
// Qwen-Image 2.1's text encoder looks at each picture and splices it in as latents, so the face or the place carries over.
// With any other picture model, Qwen-Image makes the picture and the picked model then redraws it in its own style.
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageForge;

public partial class MainWindow
{
    const int MaxReferences = 3;
    class RefPicture { public byte[] Png; public BitmapSource Thumb; public Task<string> Looks; public string Seen; }
    readonly List<RefPicture> references = new();

    const string LooksSystem = Private + "Another image model will redraw the main subject of this picture, so list what it must keep. If there is a person, " +
        "describe only that person: hair colour, length and style, eye colour, skin tone, face details, and every piece of clothing and accessory with its colour; " +
        "nothing around them (no setting, signs, lights or weather). Only if there is no person, describe the place: its main features, materials and colours. " +
        "No pose, lighting or art style. Reply with 10 to 30 short comma-separated tags only.";

    // the picture in words, for models other than Qwen-Image: their redraw gets them too, so hair, eyes and clothes hold at higher strengths.
    // Written once per picture, in the background. "" when the words would make the prompt sexual (a reference may show a real person).
    Task<string> LooksOf(RefPicture r)
    {
        if (r.Looks is { IsCompleted: true, Result: null }) r.Looks = null;      // the text model was busy or failed: try again
        return r.Looks ??= Looks(r);
    }

    async Task<string> Looks(RefPicture r)
    {
        var text = await AskTextModel("List what this looks like.", LooksSystem, r.Png, "Noting what the reference looks like…", maxTokens: 120);
        if (text == null) return null;
        if (SexualWords.IsMatch(text)) return "";
        r.Seen = text;
        ShowReferences();           // its tooltip shows the words
        return text;
    }

    // the Qwen-Image model that reads the pictures: the picked one, or the plain one first (it keeps faces best)
    string QwenForReferences => Qwen ? ModelBox.SelectedItem as string
        : imageModels.Where(m => separate.TryGetValue(m, out var s) && s.Family == "qwen_image" && !MissingParts("qwen_image")).OrderBy(m => m.Contains("noct", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

    bool UsingReferences => references.Count > 0 && CreateMode.IsChecked == true && QwenForReferences != null;

    void AddReference_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a character or a place", Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) AddReference(f);
    }

    static string[] DroppedPictures(DragEventArgs e) => e.Data.GetData(DataFormats.FileDrop) is string[] files
        ? files.Where(f => PictureTypes.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray() : [];

    void Reference_DragOver(object sender, DragEventArgs e) { e.Effects = DroppedPictures(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    void Reference_Drop(object sender, DragEventArgs e) { foreach (var f in DroppedPictures(e)) AddReference(f); e.Handled = true; }

    void AddReference(string path)
    {
        if (references.Count >= MaxReferences) { Note($"Up to {MaxReferences} reference pictures.", true); return; }
        BitmapSource img;
        try { img = Oriented(path); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); return; }
        AddReference(img);
    }

    // the Paste button, or Ctrl+V while the reference area has focus: a copied file, a browser's picture or a screenshot
    void PasteReference_Click(object sender, RoutedEventArgs e) => PasteReference();

    void PasteReference()
    {
        try
        {
            if (ClipboardPicture() is { } p) AddReference(p.Image);
            else Note("Copy a picture first: Copy image in a browser, a screenshot, or a file in Explorer.", true);
        }
        catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); }
    }

    void AddReference(BitmapSource img)
    {
        if (references.Count >= MaxReferences) { Note($"Up to {MaxReferences} reference pictures.", true); return; }
        double scale = Math.Min(1, 1280.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        img.Freeze();
        var r = new RefPicture { Png = PngOf(img), Thumb = img };
        references.Add(r);
        ShowReferences();
        if (QwenForReferences == null) Note("Reference pictures need Qwen-Image 2.1: get it on the Models page.", true);
        else if (!Qwen)
        {
            Note($"Qwen-Image keeps who or what is in the picture, then {ModelRec.Label} redraws it in its own style.");
            // one at a time: the text model answers one request, and the others would come back empty
            if (ready && !busy && !references.Any(x => x != r && x.Looks is { IsCompleted: false })) _ = LooksOf(r);       // ready by the time it's used; while pictures are being made, Generate asks instead
        }
    }

    bool fillingRefStyle;

    // with a model other than Qwen-Image: how much that model redraws Qwen's picture
    void ShowRefStyle()
    {
        if (RefStylePanel == null) return;
        bool show = references.Count > 0 && !Qwen && QwenForReferences != null;
        RefStylePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        fillingRefStyle = true;
        RefStyleSlider.Value = settings.ReferenceStyle;
        fillingRefStyle = false;
        RefStyleLabel.Text = $"How much {ModelRec.Label} redraws it (lower keeps the face closer)";
        RefStyleValue.Text = settings.ReferenceStyle.ToString("0.00");
    }

    void RefStyle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (fillingRefStyle || !IsLoaded) return;          // while the window loads, the slider snaps to its minimum: not a choice
        settings.ReferenceStyle = Math.Round(e.NewValue, 2);
        RefStyleValue.Text = settings.ReferenceStyle.ToString("0.00");
        settings.Save();
    }

    void ShowReferences()
    {
        ReferenceList.Children.Clear();
        for (int i = 0; i < references.Count; i++)
        {
            int index = i;
            var remove = new Button { Style = TryFindResource("IconBtn") as Style, Content = "", FontSize = 9, Width = 22, Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 3, 0),
                Background = Brush("Surface"), ToolTip = "Remove this reference" };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove reference {i + 1}");
            remove.Click += (_, _) => { references.RemoveAt(index); ShowReferences(); };
            ReferenceList.Children.Add(new Grid
            {
                Width = 72, Height = 72, Margin = new Thickness(0, 0, 8, 8),
                ToolTip = references[i].Seen is { } seen ? "Seen as: " + seen : null,
                Children = { new Border { CornerRadius = new CornerRadius(8), Background = new ImageBrush(references[i].Thumb) { Stretch = Stretch.UniformToFill } }, remove },
            });
        }
        ReferenceList.Visibility = SaveCharacterBtn.Visibility = references.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddReferenceRow.Visibility = references.Count < MaxReferences ? Visibility.Visible : Visibility.Collapsed;
        ShowRefStyle();
    }

    // ---------------- saved characters: reference pictures (and what they look like) kept under a name, to use again ----------------
    static string CharactersDir => Path.Combine(Path.GetDirectoryName(Settings.FilePath), "characters");

    void SaveCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (references.Count == 0 || Ask("Save as character", "") is not string typed) return;
        // Windows drops trailing dots and spaces ("Anna." would overwrite "Anna"), and "." or ".." would point at the folders above
        var name = string.Concat(typed.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.', ' ');
        var dir = Path.GetFullPath(Path.Combine(CharactersDir, name));
        if (name == "" || !string.Equals(Path.GetDirectoryName(dir), Path.GetFullPath(CharactersDir), StringComparison.OrdinalIgnoreCase))
        { Note("Give the character a name made of letters or numbers.", true); return; }
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);         // the same name again: these pictures replace the old ones
            Directory.CreateDirectory(dir);
            for (int i = 0; i < references.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{i + 1}.png"), references[i].Png);
                if (references[i].Seen is string seen) File.WriteAllText(Path.Combine(dir, $"{i + 1}.txt"), seen);
            }
            Note($"Saved as \"{name}\": it's under Saved characters from now on.");
        }
        catch (Exception ex) { Note("Couldn't save it: " + ex.Message, true); }
    }

    void Characters_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var names = Directory.Exists(CharactersDir) ? Directory.GetDirectories(CharactersDir).Select(Path.GetFileName).Order().ToList() : [];
        foreach (var n in names)
        {
            var item = new MenuItem { Header = n, Tag = "" };
            item.Click += (_, _) => { menu.IsOpen = false; LoadCharacter(n); };
            menu.Items.Add(item);
        }
        if (names.Count == 0) menu.Items.Add(new MenuItem { Header = "None yet: add pictures, then Save as character", Tag = "", IsEnabled = false });
        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = "Open the characters folder (rename or delete them there)", Tag = "" };
        open.Click += (_, _) => { menu.IsOpen = false; Directory.CreateDirectory(CharactersDir); System.Diagnostics.Process.Start("explorer.exe", CharactersDir); };
        menu.Items.Add(open);
        menu.IsOpen = true;
    }

    void LoadCharacter(string name)
    {
        references.Clear();
        foreach (var png in Directory.GetFiles(Path.Combine(CharactersDir, name), "*.png").Order().Take(MaxReferences))
        {
            try
            {
                var txt = Path.ChangeExtension(png, ".txt");
                var seen = File.Exists(txt) ? File.ReadAllText(txt) : null;
                references.Add(new RefPicture { Png = File.ReadAllBytes(png), Thumb = LoadImage(png, 200), Seen = seen, Looks = seen == null ? null : Task.FromResult(seen) });
            }
            catch { }
        }
        ShowReferences();
        Note(references.Count > 0 ? $"Using \"{name}\"." : $"\"{name}\" has no pictures left.", references.Count == 0);
    }

    // the encoder sees the pictures; the words tell it to keep who and what is in them
    void UseReferences(JsonObject g, string[] uploaded, JsonArray clip, JsonArray vae, string prompt)
    {
        if (!Qwen) { UseReferencesThroughQwen(g, uploaded, vae, prompt); return; }
        var encode = new JsonObject
        {
            ["clip"] = clip.DeepClone(), ["vae"] = vae.DeepClone(), ["resolution"] = 1024,
            // the encoder labels the pictures <image1>, <image2>…, so the words can point at them
            ["prompt"] = KeepPrompt(uploaded.Length, prompt),
            ["negative_prompt"] = g["7"]["inputs"]["text"].GetValue<string>(),
        };
        for (int i = 0; i < uploaded.Length; i++)
        {
            g[$"ref{i + 1}"] = Node("LoadImage", new() { ["image"] = uploaded[i] });
            encode[$"images.image_{i + 1}"] = Link($"ref{i + 1}", 0);
        }
        g["6"] = Node("TextEncodeQwenImage21", encode);
        g.Remove("7");
        var k = (JsonObject)g["3"]["inputs"];
        k["positive"] = Link("6", 0);
        k["negative"] = Link("6", 1);
    }

    static string KeepPrompt(int n, string prompt) =>
        $"Keep the same person, face, hair and features, or the same place, exactly as in {(n == 1 ? "image 1" : string.Join(", ", Enumerable.Range(1, n - 1).Select(i => $"image {i}")) + $" and image {n}")}. {prompt}";

    // another model: Qwen-Image makes the picture from the references, then this model's own sampler (its LoRAs, prompt and settings)
    // redraws it at the reference style strength. Calibrated with WAI and Nova Anime AM: 0.35 keeps the character in the model's own
    // style; from about 0.45 eye colour and clothes start to drift.
    void UseReferencesThroughQwen(JsonObject g, string[] uploaded, JsonArray vae, string prompt)
    {
        var qwen = QwenForReferences;
        var r = RecFor(qwen);
        g["qref_model"] = UnetNode(separate[qwen].Unet);
        g["qref_clip"] = Node("CLIPLoader", new() { ["clip_name"] = TeFor("qwen_image", qwen), ["type"] = "qwen_image" });
        g["qref_vae"] = Node("VAELoader", new() { ["vae_name"] = VaeFor("qwen_image", qwen) });
        var encode = new JsonObject
        {
            ["clip"] = Link("qref_clip", 0), ["vae"] = Link("qref_vae", 0), ["resolution"] = 1024, ["negative_prompt"] = "", ["prompt"] = KeepPrompt(uploaded.Length, prompt),
        };
        for (int i = 0; i < uploaded.Length; i++)
        {
            g[$"ref{i + 1}"] = Node("LoadImage", new() { ["image"] = uploaded[i] });
            encode[$"images.image_{i + 1}"] = Link($"ref{i + 1}", 0);
        }
        g["qref_text"] = Node("TextEncodeQwenImage21", encode);
        var k = (JsonObject)g["3"]["inputs"];
        g["qref_sample"] = Node("KSampler", new()
        {
            ["model"] = Link("qref_model", 0), ["positive"] = Link("qref_text", 0), ["negative"] = Link("qref_text", 1), ["latent_image"] = k["latent_image"].DeepClone(),
            ["seed"] = k["seed"].DeepClone(), ["steps"] = r.Steps[1], ["cfg"] = r.Cfg, ["sampler_name"] = r.Sampler, ["scheduler"] = r.Scheduler, ["denoise"] = 1.0,
        });
        g["qref_decode"] = Node("VAEDecode", new() { ["samples"] = Link("qref_sample", 0), ["vae"] = Link("qref_vae", 0) });
        g["qref_encode"] = Node("VAEEncode", new() { ["pixels"] = Link("qref_decode", 0), ["vae"] = vae.DeepClone() });
        k["latent_image"] = Link("qref_encode", 0);
        k["denoise"] = settings.ReferenceStyle;
    }

    // ---------------- two people, described apart (Attention Couple, SDXL and Anima) ----------------
    // The prompt above describes the scene; each box is one person, on the left and right half. The models with an LLM text encoder
    // (Krea 2, Qwen-Image) already keep people apart from the prompt alone.
    bool CoupleModel => Sdxl || Anima;
    bool CoupleReady => nodeNames.Contains("AttentionCouplePPM") && nodeNames.Contains("LatentToMaskBB");
    bool UsingCouple => CoupleSwitch.IsChecked == true && CreateMode.IsChecked == true && !VideoOn && CoupleModel && CoupleReady;
    string CoupleText => UsingCouple ? CoupleLeftBox.Text.Trim() + "\n" + CoupleRightBox.Text.Trim() : "";

    void Couple_Changed(object sender, RoutedEventArgs e) => ShowCouple();

    void ShowCouple()
    {
        if (CoupleSection == null) return;
        CoupleSwitch.IsEnabled = CoupleModel && CoupleReady;
        CoupleBoxes.Visibility = UsingCouple ? Visibility.Visible : Visibility.Collapsed;
        CoupleNote.Text = !CoupleReady ? "Needs Attention Couple (Settings > Plugins)."
            : !CoupleModel ? "For SDXL and Anima models; this one tells people apart from the prompt alone."
            : UsingCouple ? "Describe the scene in the prompt above, and each person here: their faces, hair and clothes stay their own."
            : "Keeps two people's looks from mixing: one description each.";
    }

    // whole picture at 0.6 with each half at 0.4 for its person (the plugin's own example); after the boosters, so Hi-res and Fix faces
    // redraw without the left/right split
    void UseCouple(JsonObject g, string left, string right)
    {
        var k = (JsonObject)g["3"]["inputs"];
        var clip = g["6"]["inputs"]["clip"];
        JsonObject Area(double x, double w, double value) => Node("LatentToMaskBB", new()
            { ["latent"] = k["latent_image"].DeepClone(), ["x"] = x, ["y"] = 0.0, ["w"] = w, ["h"] = 1.0, ["value"] = value });
        g["couple_all"] = Area(0, 1, 0.6);
        g["couple_left_area"] = Area(0, 0.5, 0.4);
        g["couple_right_area"] = Area(0.5, 0.5, 0.4);
        g["couple_left"] = Node("CLIPTextEncode", new() { ["text"] = left, ["clip"] = clip.DeepClone() });
        g["couple_right"] = Node("CLIPTextEncode", new() { ["text"] = right, ["clip"] = clip.DeepClone() });
        g["couple"] = Node("AttentionCouplePPM", new()
        {
            ["model"] = k["model"].DeepClone(), ["base_cond"] = k["positive"].DeepClone(), ["base_mask"] = Link("couple_all", 0),
            ["cond_1"] = Link("couple_left", 0), ["mask_1"] = Link("couple_left_area", 0), ["cond_2"] = Link("couple_right", 0), ["mask_2"] = Link("couple_right_area", 0),
        });
        k["model"] = Link("couple", 0);
    }

    // ---------------- style picture (IPAdapter Plus, SDXL): new pictures take on a picture's look ----------------
    RefPicture stylePicture;
    bool fillingStyle;
    static Plugin IpAdapter => Recommended.First(p => p.Folder == "ComfyUI_IPAdapter_plus");
    bool StyleModelsThere => IpAdapter.Models.All(m => File.Exists(Path.Combine(ModelsDir, m.Folder, m.File)));
    bool StyleReady => nodeNames.Contains("IPAdapterAdvanced") && StyleModelsThere;
    bool UsingStylePicture => stylePicture != null && CreateMode.IsChecked == true && !VideoOn && Sdxl && StyleReady;

    void ShowStylePicture()
    {
        if (StylePicSection == null) return;
        bool plugin = nodeNames.Contains("IPAdapterAdvanced"), ready = plugin && StyleModelsThere;
        StylePicSection.Visibility = Sdxl && CreateMode.IsChecked == true && !VideoOn ? Visibility.Visible : Visibility.Collapsed;
        StylePicThumb.Visibility = StylePicStrength.Visibility = stylePicture != null ? Visibility.Visible : Visibility.Collapsed;
        if (stylePicture != null) StylePicImage.Background = new ImageBrush(stylePicture.Thumb) { Stretch = Stretch.UniformToFill };
        StylePicAddRow.Visibility = stylePicture == null && ready ? Visibility.Visible : Visibility.Collapsed;
        StylePicGetBtn.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        StylePicGetBtn.Content = plugin ? $"Get its models ({Size(IpAdapter.Models.Sum(m => m.Bytes))})" : "Install IPAdapter Plus…";
        StylePicNote.Text = !plugin ? "Give new pictures the look of another picture: needs IPAdapter Plus (Settings > Plugins)."
            : !ready ? "IPAdapter Plus needs its two model files once."
            : "New pictures take on its colours, brushwork and light; the prompt still decides what's in them. Above about 0.6 it starts copying what's in the picture too.";
        fillingStyle = true;
        StylePicSlider.Value = settings.StyleStrength;
        fillingStyle = false;
        StylePicValue.Text = settings.StyleStrength.ToString("0.00");
    }

    void SetStylePicture(BitmapSource img)
    {
        double scale = Math.Min(1, 1024.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        img.Freeze();
        stylePicture = new RefPicture { Png = PngOf(img), Thumb = img };
        ShowStylePicture();
    }

    void StylePicAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a style picture", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) != true) return;
        try { SetStylePicture(Oriented(dlg.FileName)); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); }
    }

    void StylePicPaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClipboardPicture() is { } p) SetStylePicture(p.Image);
            else Note("Copy a picture first: Copy image in a browser, a screenshot, or a file in Explorer.", true);
        }
        catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); }
    }

    void StylePic_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!StyleReady || DroppedPictures(e).FirstOrDefault() is not string f) return;
        try { SetStylePicture(Oriented(f)); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); }
    }

    void StylePicRemove_Click(object sender, RoutedEventArgs e) { stylePicture = null; ShowStylePicture(); }

    void StylePicSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (fillingStyle || !IsLoaded) return;
        settings.StyleStrength = Math.Round(e.NewValue, 2);
        StylePicValue.Text = settings.StyleStrength.ToString("0.00");
        settings.Save();
    }

    async void StylePicGet_Click(object sender, RoutedEventArgs e)
    {
        if (!nodeNames.Contains("IPAdapterAdvanced")) { ChoosePlugins_Click(null, null); return; }
        StylePicGetBtn.IsEnabled = false;
        try { await GetPluginModels(IpAdapter, null); Note("The style models are ready."); }
        catch (Exception ex) { Note("Couldn't get them: " + ex.Message, true); }
        finally { StylePicGetBtn.IsEnabled = true; ShowStylePicture(); }
    }

    // ---------------- pose picture: the people in new pictures take the pose of the people in this picture ----------------
    // SDXL: xinsir's OpenPose ControlNet. Krea 2: thedeoxen's pose LoRA through ostris's edit nodes. Both from the same DWPose stick figure.
    RefPicture posePicture;
    bool fillingPose;
    static Plugin PosePlugin => Recommended.First(p => p.Folder == "comfyui_controlnet_aux");
    static Plugin KreaPosePlugin => Recommended.First(p => p.Folder == "ComfyUI-Krea2-Ostris-Edit");
    string PoseModelFile => PosePlugin.Models.First(m => m.Folder == "controlnet").File;
    string KreaPoseLora => KreaPosePlugin.Models[0].File;
    bool PoseFinderThere => StateOf(PosePlugin).Path is string dir && PosePlugin.Models.Where(m => m.InPack).All(m => File.Exists(Path.Combine(dir, m.Folder, m.File)));
    bool PoseFamily => Sdxl || Krea;
    // the plugin (nodes) each family needs, and whether its files are there
    bool PosePluginsThere => nodeNames.Contains("DWPreprocessor") && (!Krea || nodeNames.Contains("Krea2OstrisEditModelPatch"));
    bool PoseFilesThere => PoseFinderThere && (Krea ? File.Exists(Path.Combine(ModelsDir, "loras", KreaPoseLora)) : File.Exists(Path.Combine(ModelsDir, "controlnet", PoseModelFile)));
    bool PoseReady => PoseFamily && PosePluginsThere && PoseFilesThere;
    bool UsingPosePicture => posePicture != null && CreateMode.IsChecked == true && !VideoOn && PoseReady;

    void ShowPosePicture()
    {
        if (PosePicSection == null) return;
        bool plugin = PosePluginsThere, ready = plugin && PoseFilesThere;
        PosePicSection.Visibility = PoseFamily && CreateMode.IsChecked == true && !VideoOn || FiboEditing ? Visibility.Visible : Visibility.Collapsed;
        PosePicThumb.Visibility = PosePicStrength.Visibility = posePicture != null ? Visibility.Visible : Visibility.Collapsed;
        if (posePicture != null) PosePicImage.Background = new ImageBrush(posePicture.Thumb) { Stretch = Stretch.UniformToFill };
        PosePicAddRow.Visibility = posePicture == null && ready ? Visibility.Visible : Visibility.Collapsed;
        PosePicGetBtn.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        PosePicGetBtn.Content = plugin ? $"Get its files ({Size(PoseFilesToGet.Sum(m => m.Model.Bytes))})" : Krea && nodeNames.Contains("DWPreprocessor") ? "Install Krea 2 pose…" : "Install pose control…";
        PoseMapImage.Visibility = posePicture != null && poseMap != null ? Visibility.Visible : Visibility.Collapsed;
        if (poseMap != null) PoseMapImage.Background = new ImageBrush(poseMap) { Stretch = Stretch.Uniform };
        PosePicNote.Text = !plugin ? (Krea ? "Copy the pose of the people in a picture: needs Pose control and Krea 2 pose (Settings > Plugins)."
                : "Copy the pose of the people in a picture: needs Pose control (Settings > Plugins).")
            : !ready ? "Pose control needs its pose finder and pose model once."
            : posePicture != null && poseFound != null ? poseFound
            : "Only the pose carries over: the prompt still decides who they are and how they look.";
        fillingPose = true;
        PosePicSlider.Value = settings.PoseStrength;
        fillingPose = false;
        PosePicValue.Text = settings.PoseStrength.ToString("0.00");
    }

    void SetPosePicture(BitmapSource img)
    {
        double scale = Math.Min(1, 1280.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        img.Freeze();
        posePicture = new RefPicture { Png = PngOf(img), Thumb = img };
        (poseMap, poseMapPng, poseFound) = (null, null, null);
        ShowPosePicture();
        _ = FindPose(posePicture);
    }

    ImageSource poseMap;            // the stick figure the pose finder saw in posePicture, once found
    byte[] poseMapPng;              // the same as a PNG: what Fibo 2.0 edits get, so no one's face or body goes in, only the pose
    string poseFound;               // what it says about it

    // the pose finder on the picture alone, as soon as it's added: a misread pose shows before a picture is spent on it.
    // Its own small prompt, outside the job queue (it waits its turn behind pictures being made); nothing reaches the gallery.
    async Task FindPose(RefPicture pic)
    {
        if (!PoseReady || pic == null) return;
        poseFound = "Finding the pose in it…";
        ShowPosePicture();
        string stored = null;
        try
        {
            stored = await Upload(pic.Png, $"forge_pose_{Guid.NewGuid():N}.png");
            var g = new JsonObject
            {
                ["1"] = Node("LoadImage", new() { ["image"] = stored }),
                ["2"] = Node("DWPreprocessor", new()
                {
                    ["image"] = Link("1", 0), ["detect_hand"] = "enable", ["detect_body"] = "enable", ["detect_face"] = "enable", ["resolution"] = 512,
                    // drawn the way the model will get it: thick sticks for SDXL's xinsir ControlNet, plain ones for Krea 2's pose LoRA
                    ["bbox_detector"] = "yolox_l.onnx", ["pose_estimator"] = "dw-ll_ucoco_384.onnx", ["scale_stick_for_xinsr_cn"] = Krea ? "disable" : "enable",
                }),
                ["3"] = Node("PreviewImage", new() { ["images"] = Link("2", 0) }),
            };
            using var r = await http.PostAsync("/prompt", Json(new JsonObject { ["prompt"] = g, ["client_id"] = clientId }));
            var id = JsonNode.Parse(await r.Content.ReadAsStringAsync())?["prompt_id"]?.GetValue<string>() ?? throw new Exception("ComfyUI didn't take it");
            JsonNode found = null;
            for (int i = 0; i < 600 && found == null; i++)
            {
                await Task.Delay(1000);
                if (posePicture != pic) return;                       // changed or removed meanwhile
                var h = JsonNode.Parse(await http.GetStringAsync($"/history/{id}"))?[id];
                if (h == null) continue;                              // still waiting or running
                if (h["status"]?["status_str"]?.GetValue<string>() == "error") throw new Exception("the pose finder failed");
                found = h["outputs"]?["3"]?["images"]?[0] ?? throw new Exception("no result");
            }
            if (found == null) throw new Exception("it took too long");
            var bytes = await http.GetByteArrayAsync($"/view?filename={Uri.EscapeDataString(found["filename"].GetValue<string>())}" +
                $"&subfolder={Uri.EscapeDataString(found["subfolder"]?.GetValue<string>() ?? "")}&type={found["type"]?.GetValue<string>() ?? "temp"}");
            var map = new BitmapImage();
            map.BeginInit(); map.CacheOption = BitmapCacheOption.OnLoad; map.StreamSource = new MemoryStream(bytes); map.EndInit(); map.Freeze();
            if (posePicture != pic) return;
            bool nobody = AllDark(map);
            poseMap = nobody ? null : map;
            poseMapPng = nobody ? null : bytes;
            poseFound = nobody ? "No person found in this picture, so new pictures won't follow a pose. Try a clearer one where the whole body shows."
                : "The found pose is on the right: new pictures copy that stick figure. If it doesn't match the picture, try a clearer, full-body one.";
        }
        catch (Exception ex) { if (posePicture == pic) (poseMap, poseMapPng, poseFound) = (null, null, $"Couldn't show the pose ({ex.Message}); the picture is still used."); }
        finally
        {
            if (stored != null) try { File.Delete(Path.Combine(Root, "ComfyUI", "input", stored.Replace('/', Path.DirectorySeparatorChar))); } catch { }
            if (posePicture == pic) ShowPosePicture();
        }
    }

    // the pose finder draws on black: nothing brighter means it found no one
    static bool AllDark(BitmapSource b)
    {
        var c = new FormatConvertedBitmap(b, PixelFormats.Bgr32, null, 0);
        var px = new byte[c.PixelWidth * c.PixelHeight * 4];
        c.CopyPixels(px, c.PixelWidth * 4, 0);
        for (int i = 0; i < px.Length; i += 4)          // blue, green, red; the 4th byte is unused (often 255)
            if (px[i] > 40 || px[i + 1] > 40 || px[i + 2] > 40) return false;
        return true;
    }

    void PosePicAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a pose picture", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) != true) return;
        try { SetPosePicture(Oriented(dlg.FileName)); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); }
    }

    void PosePicPaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClipboardPicture() is { } p) SetPosePicture(p.Image);
            else Note("Copy a picture first: Copy image in a browser, a screenshot, or a file in Explorer.", true);
        }
        catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); }
    }

    void PosePic_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!PoseReady || DroppedPictures(e).FirstOrDefault() is not string f) return;
        try { SetPosePicture(Oriented(f)); } catch (Exception ex) { Note("Couldn't open that picture: " + ex.Message, true); }
    }

    void PosePicRemove_Click(object sender, RoutedEventArgs e) { (posePicture, poseMap, poseMapPng, poseFound) = (null, null, null, null); ShowPosePicture(); }

    // ComfyUI fits the pose map to the new picture by cutting out its middle, which loses arms and swords on a picture of another
    // shape; black bars to the new picture's shape keep the whole pose
    byte[] PosePng()
    {
        var (w, h) = PictureSize();
        var img = posePicture.Thumb;
        int iw = img.PixelWidth, ih = img.PixelHeight;
        double want = (double)w / h, have = (double)iw / ih;
        if (Math.Abs(want / have - 1) < 0.02) return posePicture.Png;
        int pw = have < want ? (int)Math.Round(ih * want) : iw, ph = have < want ? ih : (int)Math.Round(iw / want);
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, pw, ph));
            dc.DrawImage(img, new Rect((pw - iw) / 2.0, (ph - ih) / 2.0, iw, ih));
        }
        var rt = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rt.Render(v);
        return PngOf(rt);
    }

    void PosePicSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (fillingPose || !IsLoaded) return;
        settings.PoseStrength = Math.Round(e.NewValue, 2);
        PosePicValue.Text = settings.PoseStrength.ToString("0.00");
        settings.Save();
    }

    // the missing files this model's pose needs: the pose finder, and the SDXL pose model or the Krea 2 pose LoRA
    List<(Plugin Plugin, PluginModel Model)> PoseFilesToGet
    {
        get
        {
            var want = new List<(Plugin, PluginModel)>();
            var dir = StateOf(PosePlugin).Path;
            foreach (var m in PosePlugin.Models.Where(m => m.InPack && (dir == null || !File.Exists(Path.Combine(dir, m.Folder, m.File))))) want.Add((PosePlugin, m));
            var model = Krea ? (KreaPosePlugin, KreaPosePlugin.Models[0]) : (PosePlugin, PosePlugin.Models.First(m => m.Folder == "controlnet"));
            if (!File.Exists(Path.Combine(ModelsDir, model.Item2.Folder, model.Item2.File))) want.Add(model);
            return want;
        }
    }

    async void PosePicGet_Click(object sender, RoutedEventArgs e)
    {
        if (!PosePluginsThere || StateOf(PosePlugin).Path is not string dir) { ChoosePlugins_Click(null, null); return; }
        PosePicGetBtn.IsEnabled = false;
        try
        {
            // each plugin fetches its own (it skips what's already there): the finder with Pose control, the LoRA with Krea 2 pose
            foreach (var p in PoseFilesToGet.Select(f => f.Plugin).Distinct())
                await GetPluginModels(p, p == PosePlugin ? dir : StateOf(p).Path);
            Note("Pose control is ready.");
        }
        catch (Exception ex) { Note("Couldn't get them: " + ex.Message, true); }
        finally { PosePicGetBtn.IsEnabled = true; ShowPosePicture(); }
    }

    // DWPose finds the people's body, hands and face as a stick figure (drawn the way xinsir's pose model was trained), and the
    // ControlNet holds the new picture to it; both the prompt and the negative get it
    void UsePosePicture(JsonObject g, string uploaded)
    {
        if (Krea) { UseKreaPose(g, uploaded); return; }
        var k = (JsonObject)g["3"]["inputs"];
        g["pose_pic"] = Node("LoadImage", new() { ["image"] = uploaded });
        g["pose_find"] = Node("DWPreprocessor", new()
        {
            ["image"] = Link("pose_pic", 0), ["detect_hand"] = "enable", ["detect_body"] = "enable", ["detect_face"] = "enable", ["resolution"] = 1024,
            ["bbox_detector"] = "yolox_l.onnx", ["pose_estimator"] = "dw-ll_ucoco_384.onnx", ["scale_stick_for_xinsr_cn"] = "enable",
        });
        g["pose_model"] = Node("ControlNetLoader", new() { ["control_net_name"] = PoseModelFile });
        g["pose"] = Node("ControlNetApplyAdvanced", new()
        {
            ["positive"] = k["positive"].DeepClone(), ["negative"] = k["negative"].DeepClone(), ["control_net"] = Link("pose_model", 0),
            ["image"] = Link("pose_find", 0), ["strength"] = settings.PoseStrength, ["start_percent"] = 0.0, ["end_percent"] = 1.0,
        });
        k["positive"] = Link("pose", 0);
        k["negative"] = Link("pose", 1);
    }

    // Krea 2, as in the pose LoRA's own workflow: the stick figure goes in as Picture 1 of ostris's edit text encoder (for the prompt and
    // the negative, both as reference latents at index_timestep_zero), and the model gets the edit patch, then the pose LoRA.
    // The Pose strength slider is the LoRA's strength (its author: 0.8–1.0; lower if the pose looks stiff).
    void UseKreaPose(JsonObject g, string uploaded)
    {
        var k = (JsonObject)g["3"]["inputs"];
        g["pose_pic"] = Node("LoadImage", new() { ["image"] = uploaded });
        g["pose_find"] = Node("DWPreprocessor", new()
        {
            ["image"] = Link("pose_pic", 0), ["detect_hand"] = "enable", ["detect_body"] = "enable", ["detect_face"] = "enable", ["resolution"] = 512,
            ["bbox_detector"] = "yolox_l.onnx", ["pose_estimator"] = "dw-ll_ucoco_384.onnx", ["scale_stick_for_xinsr_cn"] = "disable",
        });
        // the stick figure at exactly the new picture's size: the edit patch lays the reference on the same grid from 0, so a different
        // size would shift the pose (PosePng already padded it to the picture's shape)
        var (w, h) = PictureSize();
        g["pose_scale"] = Node("ImageScale", new() { ["image"] = Link("pose_find", 0), ["upscale_method"] = "lanczos", ["width"] = w, ["height"] = h, ["crop"] = "disabled" });
        // new encoders beside "6" and "7", which stay plain text: Hi-res fix and Fix faces redraw with the plain model, and the saved
        // picture keeps its prompt (read from the first CLIPTextEncode)
        foreach (var (src, enc, id) in new[] { ("6", "pose_enc_pos", "pose_pos"), ("7", "pose_enc_neg", "pose_neg") })
        {
            var t = (JsonObject)g[src]["inputs"];
            g[enc] = Node("TextEncodeKrea2OstrisEdit", new()
            {
                ["clip"] = t["clip"].DeepClone(), ["prompt"] = t["text"].DeepClone(), ["vae"] = Link("45", 0), ["image1"] = Link("pose_scale", 0),
            });
            g[id] = Node("FluxKontextMultiReferenceLatentMethod", new() { ["conditioning"] = Link(enc, 0), ["reference_latents_method"] = "index_timestep_zero" });
        }
        g["pose_patch"] = Node("Krea2OstrisEditModelPatch", new() { ["model"] = k["model"].DeepClone(), ["kv_cache"] = true });
        g["pose"] = Node("LoraLoaderModelOnly", new() { ["model"] = Link("pose_patch", 0), ["lora_name"] = KreaPoseLora, ["strength_model"] = settings.PoseStrength });
        k["model"] = Link("pose", 0);
        k["positive"] = Link("pose_pos", 0);
        k["negative"] = Link("pose_neg", 0);
    }

    // "style transfer" puts the picture into the layers that carry look rather than content (PLUS: ViT-H image encoder)
    void UseStylePicture(JsonObject g, string uploaded)
    {
        var k = (JsonObject)g["3"]["inputs"];
        g["style_pic"] = Node("LoadImage", new() { ["image"] = uploaded });
        g["style_loader"] = Node("IPAdapterUnifiedLoader", new() { ["model"] = k["model"].DeepClone(), ["preset"] = "PLUS (high strength)" });
        g["style"] = Node("IPAdapterAdvanced", new()
        {
            ["model"] = Link("style_loader", 0), ["ipadapter"] = Link("style_loader", 1), ["image"] = Link("style_pic", 0), ["weight"] = settings.StyleStrength,
            ["weight_type"] = "style transfer", ["combine_embeds"] = "concat", ["start_at"] = 0.0, ["end_at"] = 1.0, ["embeds_scaling"] = "V only",
        });
        k["model"] = Link("style", 0);
    }
}
