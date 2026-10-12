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
    readonly List<(byte[] Png, BitmapSource Thumb)> references = new();

    // the Qwen-Image model that reads the pictures: the picked one, or the plain one first (it keeps faces best)
    string QwenForReferences => Qwen ? ModelBox.SelectedItem as string
        : imageModels.Where(m => separate.TryGetValue(m, out var s) && s.Family == "qwen_image").OrderBy(m => m.Contains("noct", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

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
        references.Add((PngOf(img), img));
        ShowReferences();
        if (QwenForReferences == null) Note("Reference pictures need Qwen-Image 2.1: get it on the Models page.", true);
        else if (!Qwen) Note($"Qwen-Image keeps who or what is in the picture, then {ModelRec.Label} redraws it in its own style.");
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
                Children = { new Border { CornerRadius = new CornerRadius(8), Background = new ImageBrush(references[i].Thumb) { Stretch = Stretch.UniformToFill } }, remove },
            });
        }
        ReferenceList.Visibility = references.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddReferenceRow.Visibility = references.Count < MaxReferences ? Visibility.Visible : Visibility.Collapsed;
        ShowRefStyle();
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
}
