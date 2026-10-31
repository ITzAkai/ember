// The centre of the window: the picture you're on (the canvas), the whole gallery, or the photo editor; the rail picks which.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageForge;

public partial class MainWindow
{
    enum Centre { Canvas, Gallery, Editor }

    void ShowCentre(Centre c)
    {
        CanvasPanel.Visibility = c == Centre.Canvas ? Visibility.Visible : Visibility.Collapsed;
        GalleryPanel.Visibility = c == Centre.Gallery ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = c == Centre.Editor ? Visibility.Visible : Visibility.Collapsed;
        GalleryRail.IsChecked = c == Centre.Gallery;
    }

    Shot canvasShot;
    bool pickingOnStrip;

    // the picture you're on; a picture still being made opens the live view instead, and the canvas stays where it was
    void ShowOnCanvas(Shot s)
    {
        if (s?.Pending == true) { OpenLive(s); SelectOnStrip(canvasShot); return; }
        if (s?.Secret == true) { OpenViewer(s); return; }        // secret pictures only ever show in their own tab's viewer
        BitmapSource img = null;
        if (s != null) try { img = LoadImage(s.FilePath, CanvasWidth(s.FilePath)); } catch { img = null; }       // a video's FilePath is its poster
        if (s != null && img == null && canvasShot != null) { Note("Couldn't open that picture.", true); SelectOnStrip(canvasShot); return; }
        canvasShot = img != null ? s : null;
        CanvasImage.Source = img;
        CanvasImage.Visibility = CanvasActions.Visibility = CanvasFullBtn.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        CanvasEmpty.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
        CanvasEmpty.Text = "Your pictures show here. Write a prompt and press Generate";
        // as in the viewer: a video's poster isn't upscaled, edited or animated
        CanvasUpscaleBtn.Visibility = CanvasEditBtn.Visibility = CanvasVideoBtn.Visibility = s?.IsVideo == true ? Visibility.Collapsed : Visibility.Visible;
        SelectOnStrip(canvasShot);
    }

    // the picture on the canvas left the list (deleted, moved to the secret tab, another output folder): show the newest one instead,
    // once, after the change settles (a folder switch removes hundreds one at a time)
    bool canvasRepick;
    void CanvasFollowShots()
    {
        if (canvasShot == null || canvasRepick || shots.Contains(canvasShot) && !canvasShot.Secret) return;
        canvasRepick = true;
        Dispatcher.BeginInvoke(() =>
        {
            canvasRepick = false;
            if (canvasShot != null && shots.Contains(canvasShot) && !canvasShot.Secret) return;
            canvasShot = null;
            ShowOnCanvas(shots.FirstOrDefault(x => !x.Pending && !x.Secret));
        }, DispatcherPriority.Background);
    }

    // the canvas decodes big pictures (4x upscales) at 2560 px wide at most; the full view still opens them at full size
    static int CanvasWidth(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return BitmapDecoder.Create(f, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth > 2560 ? 2560 : 0;
        }
        catch { return 0; }
    }

    // M5: the wheel scrolls the strip sideways, as the editor's results strip does
    void RecentStrip_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (VisualTreeHelper.GetChildrenCount(RecentStrip) > 0 && VisualTreeHelper.GetChild(RecentStrip, 0) is Border { Child: ScrollViewer sv })
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    void SelectOnStrip(Shot s)
    {
        pickingOnStrip = true;
        RecentStrip.SelectedItem = s;
        if (s != null) RecentStrip.ScrollIntoView(s);
        pickingOnStrip = false;
    }

    void RecentStrip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!pickingOnStrip && RecentStrip.SelectedItem is Shot s && s != canvasShot) ShowOnCanvas(s);
    }

    void CanvasImage_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) OpenViewer(canvasShot); }
    void CanvasFull_Click(object sender, RoutedEventArgs e) => OpenViewer(canvasShot);

    // Edit and Use prompt act on the picture straight away; Upscale and Make a video run in the full view, where their menu and notes show
    void CanvasEdit_Click(object sender, RoutedEventArgs e) { if (canvasShot == null) return; viewing = canvasShot; EditThis_Click(sender, e); }
    void CanvasUsePrompt_Click(object sender, RoutedEventArgs e) { if (canvasShot == null) return; viewing = canvasShot; UsePrompt_Click(sender, e); }
    void CanvasUpscale_Click(object sender, RoutedEventArgs e)
    {
        if (canvasShot == null) return;
        OpenViewer(canvasShot);
        Dispatcher.BeginInvoke(() => Upscale_Click(UpscaleBtn, e), DispatcherPriority.Loaded);      // after the viewer is laid out, so the menu sits on its button
    }
    void CanvasVideo_Click(object sender, RoutedEventArgs e) { if (canvasShot == null) return; OpenViewer(canvasShot); MakeVideoFromViewed_Click(sender, e); }

    void GalleryRail_Click(object sender, RoutedEventArgs e) => ShowCentre(Centre.Gallery);

    // a mode's rail item brings its view back even when that mode is already on (Checked fires only on a change)
    void ModeRail_Click(object sender, RoutedEventArgs e) => ShowCentre(EditMode.IsChecked == true ? Centre.Editor : Centre.Canvas);
}
