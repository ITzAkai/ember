// The full-size viewer and the before/after slider.
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
    // ---------------- viewer ----------------
    void OpenViewer(Shot s)
    {
        if (s == null || s.Pending) return;
        BitmapSource img;
        try { img = LoadImage(s.FilePath, 0); } catch (Exception ex) { Note("Couldn't open it: " + ex.Message, true); return; }
        viewing = s;
        ViewerImage.Source = img;
        var m = s.Meta;
        VPrompt.Text = m?.Prompt ?? "Not recorded in this image";
        VNegative.Text = string.IsNullOrWhiteSpace(m?.Negative) ? "None" : m.Negative;
        static KeyValuePair<string, string> Row(string k, string v) => new(k, v);
        VMeta.ItemsSource = new[]
        {
            Row("Size", $"{img.PixelWidth} × {img.PixelHeight}"),
            Row("Seed", m?.Seed?.ToString()),
            Row("Steps", m?.Steps?.ToString()),
            Row("Guidance", m?.Cfg?.ToString("0.0")),
            Row("Sampler", m?.Sampler == null ? null : $"{m.Sampler} · {m.Scheduler}"),
            Row("LoRAs", m?.Loras),
            Row("Model", m?.Model == null ? null : Path.GetFileNameWithoutExtension(m.Model)),
            Row("Created", File.GetLastWriteTime(s.FilePath).ToString("d MMM yyyy, HH:mm")),
            Row("Upscaled", m?.Upscaled),
            Row("Made", m?.From),
            Row("File", Path.GetFileName(s.FilePath)),
        }.Where(r => r.Value != null).ToList();
        ViewerNote.Text = "";
        ShowFavorite();
        SetCompare(false);
        BeforeImage.Source = null;
        CompareBtn.Visibility = File.Exists(OriginalOf(s.FilePath)) ? Visibility.Visible : Visibility.Collapsed;
        ShowClip(s);
        ViewerActions.Visibility = s.Plain ? Visibility.Visible : Visibility.Collapsed;     // a secret picture stays out of ComfyUI and other files
        SecretViewedBtn.ToolTip = s.SecretLabel;
        ViewerPanel.Visibility = Visibility.Visible;
        ViewerPanel.Focus();
    }

    void CloseViewer() { SetCompare(false); StopClip(); ViewerPanel.Visibility = Visibility.Collapsed; ViewerImage.Source = BeforeImage.Source = null; viewing = null; }

    // ---------------- before / after ----------------
    // an edit's original photo lives in output/forge/originals under the same file name (not shown in the gallery)
    static string OriginalOf(string path) => Path.Combine(Path.GetDirectoryName(path), "originals", Path.GetFileName(path));

    double split = 0.5;
    bool dragging;

    void Compare_Click(object sender, RoutedEventArgs e) => SetCompare(CompareLayer.Visibility != Visibility.Visible);

    void SetCompare(bool on)
    {
        if (on && (viewing == null || CompareBtn.Visibility != Visibility.Visible)) return;
        if (on && BeforeImage.Source == null)
            try { BeforeImage.Source = LoadImage(OriginalOf(viewing.FilePath), 0); } catch { return; }
        split = 0.5;
        CompareLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CompareText.Text = on ? "Hide comparison" : "Compare with original";
        if (on) Dispatcher.BeginInvoke(LayoutCompare, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void BeforeImage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCompare();

    void LayoutCompare()
    {
        double w = BeforeImage.ActualWidth, h = BeforeImage.ActualHeight;
        if (w == 0 || CompareLayer.Visibility != Visibility.Visible) return;
        BeforeImage.Clip = new RectangleGeometry(new Rect(0, 0, w * split, h));
        var o = BeforeImage.TranslatePoint(new Point(0, 0), CompareLayer);
        double x = o.X + w * split;
        Canvas.SetLeft(Divider, x - 1); Canvas.SetTop(Divider, o.Y); Divider.Height = h;
        Canvas.SetLeft(Handle, x - 18); Canvas.SetTop(Handle, o.Y + h / 2 - 18);
        Canvas.SetLeft(BeforeTag, o.X + 12); Canvas.SetTop(BeforeTag, o.Y + 12);
        Canvas.SetLeft(AfterTag, o.X + w - 12 - AfterTag.ActualWidth); Canvas.SetTop(AfterTag, o.Y + 12);
    }

    void Compare_MouseDown(object sender, MouseButtonEventArgs e) { dragging = true; CompareLayer.CaptureMouse(); MoveSplit(e); e.Handled = true; }
    void Compare_MouseMove(object sender, MouseEventArgs e) { if (dragging) MoveSplit(e); }
    void Compare_MouseUp(object sender, MouseButtonEventArgs e) { dragging = false; CompareLayer.ReleaseMouseCapture(); e.Handled = true; }
    void MoveSplit(MouseEventArgs e)
    {
        split = Math.Clamp(e.GetPosition(BeforeImage).X / Math.Max(1, BeforeImage.ActualWidth), 0, 1);
        LayoutCompare();
    }

    void Step(int d)
    {
        var list = Browsable().ToList();
        int i = list.IndexOf(viewing);
        if (i >= 0 && list.Count > 1) OpenViewer(list[(i + d + list.Count) % list.Count]);
    }

    // arrows walk what you were looking at: the gallery tab (all or favourites), or every image from the editor
    IEnumerable<Shot> Browsable() => (EditMode.IsChecked == true ? shots.Where(s => !s.Secret) : galleryView.Cast<Shot>()).Where(s => !s.Pending);

    void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Step(1);
    void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseViewer();
    void Backdrop_Click(object sender, MouseButtonEventArgs e) => CloseViewer();

    void UsePrompt_Click(object sender, RoutedEventArgs e)
    {
        UsePromptOf(viewing?.Meta);
        CloseViewer();
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    // variations: this image becomes the photo, half-strength repaint with its own prompt and a fresh seed
    void MoreLikeThis_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        if (!ready) { ViewerNote.Text = "ComfyUI isn't ready yet."; return; }
        var s = viewing;
        CloseViewer();
        EditMode.IsChecked = true;
        LoadPhoto(s.FilePath);
        // variations in the picture's own look: the model it was made with, when it's still installed
        if (!VideoOn && imageModels.FirstOrDefault(x => x == s.Meta?.Model || separate.TryGetValue(x, out var u) && u.Unet == s.Meta?.Model) is string own) PickModel(own);
        UsePromptOf(s.Meta);
        StrengthSlider.Value = 0.5;
        _ = Generate(newSeed: true);
    }
}
