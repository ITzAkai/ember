// The centre of the window: the picture you're on (the canvas), the whole gallery, or the photo editor; the rail picks which.
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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
        BitmapSource img = null;
        if (s != null) try { img = LoadImage(s.FilePath, 0); } catch { img = null; }       // a video's FilePath is its poster
        canvasShot = img != null ? s : null;
        CanvasImage.Source = img;
        CanvasImage.Visibility = CanvasActions.Visibility = CanvasFullBtn.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        CanvasEmpty.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
        SelectOnStrip(canvasShot);
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
    void CanvasUpscale_Click(object sender, RoutedEventArgs e) { if (canvasShot == null) return; OpenViewer(canvasShot); Upscale_Click(UpscaleBtn, e); }
    void CanvasVideo_Click(object sender, RoutedEventArgs e) { if (canvasShot == null) return; OpenViewer(canvasShot); MakeVideoFromViewed_Click(sender, e); }

    void GalleryRail_Click(object sender, RoutedEventArgs e) => ShowCentre(Centre.Gallery);

    // a mode's rail item brings its view back even when that mode is already on (Checked fires only on a change)
    void ModeRail_Click(object sender, RoutedEventArgs e) => ShowCentre(EditMode.IsChecked == true ? Centre.Editor : Centre.Canvas);
}
