// Edit photo: the editor, Expand (outpainting) and Crop.
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
    // ---------------- photo editor ----------------
    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        bool edit = EditMode.IsChecked == true, video = VideoMode.IsChecked == true;
        ShowCentre(edit ? Centre.Editor : Centre.Canvas);
        SizeSection.Visibility = CoupleSection.Visibility = WorkflowSection.Visibility = edit || video ? Visibility.Collapsed : Visibility.Visible;
        ShowWorkflow();
        ShowEditExtras();
        VideoSection.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        ShowVideoOptions();
        CountSection.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        PhotoSection.Visibility = StrengthSection.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        PromptHeading.Text = edit ? "Describe the result" : video ? "Describe the video" : "Prompt";
        TabsBar.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;     // video models have tabs too (MiniMax H3 bundles open as one)
        PromptBox.Tag = edit ? "e.g. the same scene in winter, snow on the roofs, golden hour"
            : video ? "e.g. a fox running through snowy woods, the camera following it" : "Describe the image: subject, style, lighting, mood…";
        ShowGenerateText();
        EnhanceBtn.ToolTip = edit ? "Looks at your photo and turns a short change into a clear, detailed one"
            : video ? "Turn a short idea into a detailed video prompt, with movement and camera" : "Turn a short idea into a detailed prompt, written by the local Qwen model";
        if (ready && video != showingVideoModels) FillModels();
        ShowBoosters();
    }

    // the Extras fold (pictures, two people, workflow): shown while one of its sections is, and opened when one of them is in use
    void WatchExtras()
    {
        FrameworkElement[] sections = [ReferenceSection, CoupleSection, WorkflowSection, StylePicSection],      // pose has its own card now
            used = [ReferenceList, CoupleBoxes, StylePicThumb];
        void Update(object s, EventArgs e)
        {
            ExtrasCard.Visibility = sections.Any(x => x.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            if (used.Any(x => x.Visibility == Visibility.Visible) || WorkflowBox.SelectedIndex > 0) ExtrasToggle.IsChecked = true;
        }
        var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement));
        foreach (var x in sections.Concat(used)) visibility.AddValueChanged(x, Update);
        WorkflowBox.SelectionChanged += (_, _) => Update(null, null);
        Update(null, null);
    }

    string TimeKey => $"{ModelBox.SelectedItem}|{(CustomSizeBox.IsChecked == true ? $"{CustomW.Text}x{CustomH.Text}" : Picked(SizeGroup))}|{Steps}";

    // "Generate 4 · about 40 s": the number, and the time the last picture with the same model, size and steps took
    void ShowGenerateText()
    {
        if (GenerateText == null) return;
        if (EditMode.IsChecked == true || VideoMode.IsChecked == true) { GenerateText.Text = EditMode.IsChecked == true ? "Apply edit" : "Make video"; return; }
        int n = PickedInt(CountGroup, 1);
        var text = n > 1 ? $"Generate {n}" : "Generate";
        if (!UsingWorkflow && settings.Took.TryGetValue(TimeKey, out var s))
            text += s * n < 90 ? $" · about {Math.Max(5, Math.Round(s * n / 5) * 5):0} s" : $" · about {Math.Round(s * n / 60):0} min";
        GenerateText.Text = text;
    }

    void WatchGenerateText()
    {
        RoutedEventHandler update = (_, _) => ShowGenerateText();
        foreach (var g in new Panel[] { CountGroup, SizeGroup, QualityGroup }) g.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, update);
        StepsSlider.ValueChanged += (_, _) => ShowGenerateText();
        ModelBox.SelectionChanged += (_, _) => ShowGenerateText();
        WorkflowBox.SelectionChanged += (_, _) => ShowGenerateText();
        CustomSizeBox.Checked += update; CustomSizeBox.Unchecked += update; CustomW.LostFocus += update; CustomH.LostFocus += update;
    }

    // the Model row picks in the full model list in Advanced; only a choice made here is passed on
    void QuickModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QuickModelBox.IsKeyboardFocusWithin && QuickModelBox.SelectedItem is string m && !Equals(ModelBox.SelectedItem, m)) ModelBox.SelectedItem = m;
    }


    // a picture dropped on the closed fold becomes a reference picture
    void ExtrasDrop(object sender, DragEventArgs e) { ExtrasToggle.IsChecked = true; Reference_Drop(sender, e); }

    // characters and the pose picture in edits too, when Fibo 2.0 makes them (known once ComfyUI's models are read)
    void ShowEditExtras()
    {
        ReferenceSection.Visibility = VideoMode.IsChecked == true || EditMode.IsChecked == true && !FiboEditing ? Visibility.Collapsed : Visibility.Visible;
        ShowPosePicture();
    }

    void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a photo to edit", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif" };
        if (dlg.ShowDialog(this) == true) LoadPhoto(dlg.FileName);
    }

    void Photo_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) LoadPhoto(files[0]);
    }

    void EditThis_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        var shot = viewing;
        CloseViewer();
        EditMode.IsChecked = true;
        LoadPhoto(shot.FilePath);
        UsePromptOf(shot.Meta);
    }

    // from the viewer: open the image in the editor with the Expand choices showing
    void ExpandViewed_Click(object sender, RoutedEventArgs e)
    {
        EditThis_Click(sender, e);
        if (photo != null) Dispatcher.BeginInvoke(() => Expand_Click(ExpandBtn, null), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ShotOf(sender) is not { Pending: false } s) return;
        EditMode.IsChecked = true;
        LoadPhoto(s.FilePath);
        UsePromptOf(s.Meta);
    }

    void LoadPhoto(string path)
    {
        try { ShowPhoto(Oriented(path), Path.GetFileName(path)); }
        catch (Exception ex) { Note("Couldn't open that photo: " + ex.Message, true); }
    }

    // the photo three ways: as loaded (Reset), after crop/rotate/flip, and `photo` = that plus the adjust sliders
    BitmapSource photoOriginal, photoBase;
    bool quietAdjust;

    void ShowPhoto(BitmapSource src, string name)
    {
        photoOriginal = Fit(src);
        EndCrop();
        ResetAdjust();
        SetPhotoBase(photoOriginal);
        EditorView.Visibility = Toolbar.Visibility = ImageBar.Visibility = PhotoCard.Visibility = Visibility.Visible;
        DropHint.Visibility = ChoosePhotoBtn.Visibility = Visibility.Collapsed;
        PhotoName.Text = name;
        Note("");
    }

    // a new shape or orientation: the painting no longer lines up, so it goes
    void SetPhotoBase(BitmapSource b)
    {
        photoBase = b;
        ClearMarks();
        RenderPhoto();
    }

    void RenderPhoto()
    {
        if (photoBase == null) return;
        photo = Adjust(photoBase, BrightSlider.Value, ContrastSlider.Value, SatSlider.Value);
        EditorImage.Source = PhotoThumb.ImageSource = photo;
        EditorCanvas.Width = photo.PixelWidth;
        EditorCanvas.Height = photo.PixelHeight;
        PhotoSize.Text = $"{photo.PixelWidth} × {photo.PixelHeight}";
    }

    // brightness and contrast through one lookup table, then saturation pulls each pixel toward or away from its grey
    static BitmapSource Adjust(BitmapSource src, double brightness, double contrast, double saturation)
    {
        if (brightness == 0 && contrast == 0 && saturation == 0) return src;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(px, stride, 0);
        double c = (100 + contrast) / 100, s = (100 + saturation) / 100;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp((i - 128) * c + 128 + brightness * 1.28, 0, 255);
        Parallel.For(0, h, y =>
        {
            for (int i = y * stride, end = i + stride; i < end; i += 4)
            {
                double b = lut[px[i]], g = lut[px[i + 1]], r = lut[px[i + 2]];
                double grey = 0.299 * r + 0.587 * g + 0.114 * b;
                px[i] = (byte)Math.Clamp(grey + (b - grey) * s, 0, 255);
                px[i + 1] = (byte)Math.Clamp(grey + (g - grey) * s, 0, 255);
                px[i + 2] = (byte)Math.Clamp(grey + (r - grey) * s, 0, 255);
            }
        });
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    void Adjust_Click(object sender, RoutedEventArgs e) => AdjustPopup.IsOpen = true;
    void Adjust_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!quietAdjust) RenderPhoto(); }
    void AdjustReset_Click(object sender, RoutedEventArgs e) { ResetAdjust(); RenderPhoto(); }
    void ResetAdjust()
    {
        quietAdjust = true;
        BrightSlider.Value = ContrastSlider.Value = SatSlider.Value = 0;
        quietAdjust = false;
    }

    void RotateLeft_Click(object sender, RoutedEventArgs e) => Reshape(b => new TransformedBitmap(b, new RotateTransform(270)));
    void RotateRight_Click(object sender, RoutedEventArgs e) => Reshape(b => new TransformedBitmap(b, new RotateTransform(90)));
    void Flip_Click(object sender, RoutedEventArgs e) => Reshape(b => new TransformedBitmap(b, new ScaleTransform(-1, 1)));
    void ResetPhoto_Click(object sender, RoutedEventArgs e) { EndCrop(); ResetAdjust(); SetPhotoBase(photoOriginal); }

    void Reshape(Func<BitmapSource, BitmapSource> change)
    {
        if (photoBase == null) return;
        try { SetPhotoBase(Fit(change(photoBase))); }
        catch (Exception ex) { Note("Couldn't change the photo: " + ex.Message, true); }
    }

    // ---------------- expand (outpainting) ----------------
    void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (photo == null) return;
        double here = (double)photo.PixelWidth / photo.PixelHeight;
        var menu = new ContextMenu { PlacementTarget = ExpandBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (label, aspect) in new[] { ("Zoom out", 0.0), ("Square 1:1", 1.0), ("Portrait 2:3", 2 / 3.0), ("Tall 9:16", 9 / 16.0), ("Landscape 3:2", 1.5), ("Wide 16:9", 16 / 9.0) })
        {
            if (aspect != 0 && Math.Abs(Math.Log(aspect / here)) < 0.08) continue;      // already about that shape
            var (w, h, _) = ExpandLayout(aspect);
            var item = new MenuItem { Header = $"{label}   ({w} × {h})", Tag = "" };
            item.Click += (_, _) => _ = Generate(expand: aspect);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // the bigger canvas (about one megapixel, sides in 64s) and where the photo sits on it, centred.
    // aspect 0 = zoom out: half the photo's size again, a quarter on every side
    (int W, int H, Rect At) ExpandLayout(double aspect)
    {
        double w = photo.PixelWidth, h = photo.PixelHeight, cw, ch;
        if (aspect == 0) (cw, ch) = (w * 1.5, h * 1.5);
        else if (w / h < aspect) (cw, ch) = (h * aspect, h);
        else (cw, ch) = (w, w / aspect);
        double k = Math.Sqrt(1024.0 * 1024 / (cw * ch));
        int W = Math.Max(256, (int)Math.Round(cw * k / 64) * 64), H = Math.Max(256, (int)Math.Round(ch * k / 64) * 64);
        double f = Math.Min(W / cw, H / ch);
        return (W, H, new Rect((W - w * f) / 2, (H - h * f) / 2, w * f, h * f));
    }

    // the canvas to paint (new area transparent = repaint) and a "before" for compare (the photo on plain grey)
    (byte[] Canvas, byte[] Before) ExpandCanvas(double aspect)
    {
        var (W, H, at) = ExpandLayout(aspect);
        Image Photo() => new() { Source = photo, Stretch = Stretch.Fill, Width = at.Width, Height = at.Height,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(at.X, at.Y, 0, 0) };

        // starting colours for the new area: the photo's edges carried outwards (FillHoles), so the model keeps its palette
        var canvas = new Grid { Width = W, Height = H, Background = Brushes.Black };
        canvas.Children.Add(Photo());
        var canvasPx = RenderPixels(canvas, W, H);
        var outside = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                outside[y * W + x] = at.Contains(x + 0.5, y + 0.5) ? (byte)0 : (byte)255;
        FillHoles(canvasPx, outside, W, H);

        // repaint the new area plus a soft band 64 px into the photo, so the join doesn't show
        // (tried on the anime viking: 24 px left hard vertical seams); sides already at the edge are left alone
        const double band = 64;
        double l = at.X > 1 ? at.X + band : -50, t = at.Y > 1 ? at.Y + band : -50;
        double r = at.Right < W - 1 ? at.Right - band : W + 50, b = at.Bottom < H - 1 ? at.Bottom - band : H + 50;
        var mask = new Canvas { Width = W, Height = H, Background = Brushes.White, ClipToBounds = true };
        var keep = new Border { Background = Brushes.Black, Width = r - l, Height = b - t, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 32 } };
        Canvas.SetLeft(keep, l); Canvas.SetTop(keep, t);
        mask.Children.Add(keep);

        var before = new Grid { Width = W, Height = H, Background = Brush("Raised") };
        before.Children.Add(Photo());

        var maskPx = RenderPixels(mask, W, H);
        var repaint = new byte[W * H];
        for (int i = 0; i < repaint.Length; i++) repaint[i] = maskPx[i * 4 + 2];
        return (MaskedPng(canvasPx, W, H, repaint), MaskedPng(RenderPixels(before, W, H), W, H, null));
    }

    static byte[] RenderPixels(FrameworkElement root, int w, int h)
    {
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    // ---------------- crop ----------------
    Rect crop;                          // in photo pixels

    void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (photo == null) return;
        crop = new Rect(photo.PixelWidth * 0.1, photo.PixelHeight * 0.1, photo.PixelWidth * 0.8, photo.PixelHeight * 0.8);
        CropLayer.Visibility = CropTools.Visibility = Visibility.Visible;
        ImageTools.Visibility = BrushRing.Visibility = BrushRingDark.Visibility = Visibility.Collapsed;
        Ink.IsHitTestVisible = Toolbar.IsEnabled = ShapeInput.IsHitTestVisible = false;
        Dispatcher.BeginInvoke(LayoutCrop, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void EndCrop()
    {
        CropLayer.Visibility = CropTools.Visibility = Visibility.Collapsed;
        ImageTools.Visibility = Visibility.Visible;
        Ink.IsHitTestVisible = Toolbar.IsEnabled = true;
        UpdateBrush();
    }

    void CropCancel_Click(object sender, RoutedEventArgs e) => EndCrop();

    void CropApply_Click(object sender, RoutedEventArgs e)
    {
        var r = new Int32Rect((int)crop.X, (int)crop.Y, (int)crop.Width, (int)crop.Height);
        EndCrop();
        Reshape(b => new CroppedBitmap(b, r));    // ponytail: Fit() rounds sides to multiples of 64, so the crop's shape can shift by a few percent
    }

    void LayoutCrop()
    {
        if (CropLayer.Visibility != Visibility.Visible || photo == null) return;
        double px = ScreenPixel(), handle = 14 * px;
        CropShade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, photo.PixelWidth, photo.PixelHeight)), new RectangleGeometry(crop) },
        };
        Canvas.SetLeft(CropBox, crop.X); Canvas.SetTop(CropBox, crop.Y);
        CropBox.Width = crop.Width; CropBox.Height = crop.Height;
        CropBox.Tag = new Thickness(1.5 * px);
        foreach (var (thumb, x, y) in new[] { (CropTL, crop.Left, crop.Top), (CropTR, crop.Right, crop.Top), (CropBL, crop.Left, crop.Bottom), (CropBR, crop.Right, crop.Bottom) })
        {
            thumb.Width = thumb.Height = handle;
            thumb.Tag = new Thickness(px);
            Canvas.SetLeft(thumb, x - handle / 2);
            Canvas.SetTop(thumb, y - handle / 2);
        }
    }

    void CropMove_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        crop.X = Math.Clamp(crop.X + e.HorizontalChange, 0, photo.PixelWidth - crop.Width);
        crop.Y = Math.Clamp(crop.Y + e.VerticalChange, 0, photo.PixelHeight - crop.Height);
        LayoutCrop();
    }

    // corner names say which edges they move: CropTL = top + left
    void CropCorner_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var name = ((FrameworkElement)sender).Name;
        const double min = 64;
        double l = crop.Left, t = crop.Top, r = crop.Right, b = crop.Bottom;
        if (name.EndsWith('L')) l = Math.Clamp(l + e.HorizontalChange, 0, r - min); else r = Math.Clamp(r + e.HorizontalChange, l + min, photo.PixelWidth);
        if (name[4] == 'T') t = Math.Clamp(t + e.VerticalChange, 0, b - min); else b = Math.Clamp(b + e.VerticalChange, t + min, photo.PixelHeight);
        crop = new Rect(new Point(l, t), new Point(r, b));
        LayoutCrop();
    }

    // one screen pixel, in photo pixels: keeps outlines and handles the same size at any zoom
    double ScreenPixel() => 1 / Math.Max(0.01, EditorCanvas.TransformToAncestor(this).TransformBounds(new Rect(0, 0, 1, 1)).Width);

    // a copied file from Explorer, a browser's PNG, or a plain bitmap (screenshots); null when there's no picture
    static (BitmapSource Image, string Name)? ClipboardPicture()
    {
        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => File.Exists(f)) is string file)
            return (Oriented(file), Path.GetFileName(file));
        if (Clipboard.GetData("PNG") is MemoryStream png)
            return (BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), "Pasted image");
        if (Clipboard.ContainsImage())
            return (new FormatConvertedBitmap(Clipboard.GetImage(), PixelFormats.Bgr32, null, 0), "Pasted image");   // Bgr32: bitmaps often arrive with an empty alpha channel
        return null;
    }

    // Ctrl+V outside a text box: the copied picture becomes the photo to edit
    bool PastePhoto()
    {
        try
        {
            if (ClipboardPicture() is not { } p) return false;
            ShowPhoto(p.Image, p.Name);
        }
        catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); }
        return true;
    }

    // phone photos store their rotation in EXIF instead of rotating the pixels
    static BitmapSource Oriented(string path)
    {
        var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        ushort o = 1;
        try { if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort v) o = v; } catch { }
        int angle = o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };      // ponytail: mirrored orientations (2,4,5,7) are rare and ignored
        return angle == 0 ? frame : new TransformedBitmap(frame, new RotateTransform(angle));
    }

    // SDXL works best near one megapixel, with sides in multiples of 64
    static BitmapSource Fit(BitmapSource src)
    {
        double k = Math.Sqrt(1024.0 * 1024 / ((double)src.PixelWidth * src.PixelHeight));
        int w = Math.Max(256, (int)Math.Round(src.PixelWidth * k / 64) * 64), h = Math.Max(256, (int)Math.Round(src.PixelHeight * k / 64) * 64);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    void Tool_Checked(object sender, RoutedEventArgs e) => UpdateBrush();
    void BrushSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateBrush();

    void UpdateBrush()
    {
        if (Ink == null || EraserTool == null || LassoTool == null) return;      // still loading the window
        double s = BrushSlider.Value;
        Ink.DefaultDrawingAttributes = new DrawingAttributes { Color = MarkColor, Width = s, Height = s, FitToCurve = true };
        Ink.EraserShape = new EllipseStylusShape(s, s);
        Ink.EditingMode = InkCanvasEditingMode.None;        // a new eraser shape only applies when the mode is set again
        if (!ShapeTool) Ink.EditingMode = EraserTool.IsChecked == true ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
        ShapeInput.IsHitTestVisible = ShapeTool && CropLayer.Visibility != Visibility.Visible;
        BrushRing.StrokeDashArray = EraserTool.IsChecked == true ? new DoubleCollection { 3, 2 } : null;
    }

    // the outline sits where the brush will paint, at the brush's size
    void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (CropLayer.Visibility == Visibility.Visible || ShapeTool) { Canvas_MouseLeave(null, null); return; }
        var p = e.GetPosition(EditorCanvas);
        double s = BrushSlider.Value, px = ScreenPixel();
        foreach (var (ring, thickness) in new[] { (BrushRingDark, 3 * px), (BrushRing, 1.2 * px) })
        {
            ring.Width = ring.Height = s;
            ring.StrokeThickness = thickness;
            Canvas.SetLeft(ring, p.X - s / 2);
            Canvas.SetTop(ring, p.Y - s / 2);
            ring.Visibility = Visibility.Visible;
        }
    }
    void Canvas_MouseLeave(object sender, MouseEventArgs e) => BrushRing.Visibility = BrushRingDark.Visibility = Visibility.Collapsed;

    void Strip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        StripScroll.ScrollToHorizontalOffset(StripScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // the photo as PNG; ComfyUI's LoadImage turns transparency into its mask (transparent = repaint)
    byte[] EditPng(byte[] mask)
    {
        int w = photo.PixelWidth, h = photo.PixelHeight;
        var px = new byte[w * h * 4];
        photo.CopyPixels(px, w * 4, 0);
        return MaskedPng(px, w, h, mask);
    }

    static byte[] MaskedPng(byte[] px, int w, int h, byte[] mask)
    {
        if (mask != null) for (int i = 0; i < mask.Length; i++) px[i * 4 + 3] = (byte)(255 - mask[i]);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // into ComfyUI's input/forge folder, overwriting the previous edit's photo
    async Task<string> Upload(byte[] png, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new("image/png");
        form.Add(file, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");
        form.Add(new StringContent("forge"), "subfolder");
        using var r = await http.PostAsync("/upload/image", form);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
        if (!r.IsSuccessStatusCode || j?["name"] == null) throw new Exception($"photo upload failed (HTTP {(int)r.StatusCode})");
        var sub = j["subfolder"]?.GetValue<string>();
        return string.IsNullOrEmpty(sub) ? j["name"].GetValue<string>() : $"{sub}/{j["name"].GetValue<string>()}";
    }
}
