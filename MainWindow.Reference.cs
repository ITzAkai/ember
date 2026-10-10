// Reference pictures (Create): a character or a place that new pictures keep exactly as it is.
// Qwen-Image 2.1's text encoder looks at each picture and splices it in as latents, so the face or the place carries over.
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

    bool UsingReferences => references.Count > 0 && Qwen && CreateMode.IsChecked == true;

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
        double scale = Math.Min(1, 1280.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        img.Freeze();
        references.Add((PngOf(img), img));
        ShowReferences();
        if (Qwen) return;
        // only Qwen-Image models read reference pictures: the plain one first, it keeps faces best
        var qwen = imageModels.Where(m => separate.TryGetValue(m, out var s) && s.Family == "qwen_image")
            .OrderBy(m => m.Contains("noct", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (qwen == null) { Note("Reference pictures need Qwen-Image 2.1: get it on the Models page.", true); return; }
        ModelBox.SelectedItem = qwen;
        Note($"Switched to {(qwen.EndsWith(".safetensors") ? Path.GetFileNameWithoutExtension(qwen) : qwen)}: it keeps the faces and places from reference pictures.");
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
        AddReferenceBtn.Visibility = references.Count < MaxReferences ? Visibility.Visible : Visibility.Collapsed;
    }

    // the encoder sees the pictures; the words tell it to keep who and what is in them
    void UseReferences(JsonObject g, string[] uploaded, JsonArray clip, JsonArray vae, string prompt)
    {
        var encode = new JsonObject
        {
            ["clip"] = clip.DeepClone(), ["vae"] = vae.DeepClone(), ["resolution"] = 1024,
            // the encoder labels the pictures <image1>, <image2>…, so the words can point at them
            ["prompt"] = $"Keep the same person, face, hair and features, or the same place, exactly as in {Images(uploaded.Length)}. {prompt}",
            ["negative_prompt"] = g["7"]["inputs"]["text"].GetValue<string>(),
        };
        for (int i = 0; i < uploaded.Length; i++)
        {
            g[$"ref{i + 1}"] = Node("LoadImage", new() { ["image"] = uploaded[i] });
            encode[$"images.image_{i + 1}"] = Link($"ref{i + 1}", 0);
        }
        g["6"] = Node("TextEncodeQwenImage21", encode);
        static string Images(int n) => n == 1 ? "image 1" : string.Join(", ", Enumerable.Range(1, n - 1).Select(i => $"image {i}")) + $" and image {n}";
        g.Remove("7");
        var k = (JsonObject)g["3"]["inputs"];
        k["positive"] = Link("6", 0);
        k["negative"] = Link("6", 1);
    }
}
