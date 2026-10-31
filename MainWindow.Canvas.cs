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

    Shot canvasShot, canvasLive;      // the picture you're on, and one being made, shown live over it
    BitmapSource canvasBitmap;        // canvasShot, decoded
    bool showingStart = true;         // Video mode shows its start picture until a strip picture is picked
    bool pickingOnStrip;

    // Video mode, with a start picture its model starts from
    bool StartOnCanvas => showingStart && startImage != null && VideoOn && (PictureVideo || H3);

    // what the canvas shows follows from canvasShot, canvasLive and the start picture: this is the one place that decides
    void RefreshCanvas()
    {
        if (CanvasImage == null) return;
        bool live = canvasLive != null, start = StartOnCanvas;
        CanvasImage.Source = start ? startImage : canvasBitmap;
        CanvasImage.Visibility = CanvasImage.Source != null ? Visibility.Visible : Visibility.Collapsed;
        CanvasLive.DataContext = canvasLive;
        CanvasLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;          // over the picture: its preview covers it once it arrives
        CanvasLabel.Visibility = start && !live ? Visibility.Visible : Visibility.Collapsed;
        CanvasActions.Visibility = CanvasFullBtn.Visibility = !live && !start && canvasBitmap != null ? Visibility.Visible : Visibility.Collapsed;
        CanvasEmpty.Visibility = !live && CanvasImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        SelectOnStrip(live ? canvasLive : start ? null : canvasShot);
    }

    // the picture you're on, decoded; false, with nothing changed, when it won't open
    bool SetCanvasPicture(Shot s)
    {
        BitmapSource img = null;
        if (s != null) try { img = LoadImage(s.FilePath, CanvasWidth(s.FilePath)); } catch { img = null; }       // a video's FilePath is its poster
        if (s != null && img == null) return false;
        canvasShot = s;
        canvasBitmap = img;
        // as in the viewer: a video's poster isn't upscaled, edited or animated
        CanvasUpscaleBtn.Visibility = CanvasEditBtn.Visibility = CanvasVideoBtn.Visibility = s?.IsVideo == true ? Visibility.Collapsed : Visibility.Visible;
        return true;
    }

    // show a picture: a finished one takes the place of the live view and the start picture; one being made shows live
    void ShowOnCanvas(Shot s)
    {
        if (s?.Pending == true) { ShowLiveOnCanvas(s); return; }
        if (s?.Secret == true) { OpenViewer(s); return; }        // secret pictures only ever show in their own tab's viewer
        CanvasEmpty.Text = "Your pictures show here. Write a prompt and press Generate";
        if (!SetCanvasPicture(s)) { Note("Couldn't open that picture.", true); RefreshCanvas(); return; }
        canvasLive = null;
        showingStart = false;
        RefreshCanvas();
    }

    void ShowLiveOnCanvas(Shot slot) { canvasLive = slot; RefreshCanvas(); }

    // Video mode: the start picture the clip begins on, labelled; set again by SetStart and on entering Video
    void ShowStartOnCanvas() { showingStart = true; RefreshCanvas(); }

    // the picture on the canvas left the list (deleted, moved to the secret tab, another output folder): show the newest one instead,
    // once, after the change settles (a folder switch removes hundreds one at a time)
    bool canvasRepick;
    void CanvasFollowShots()
    {
        // the live picture was cancelled or failed (a finished one is shown by ShowNewest right after): what it covered shows again
        if (canvasLive != null && !shots.Contains(canvasLive)) { canvasLive = null; RefreshCanvas(); }
        if (canvasShot == null || canvasRepick || shots.Contains(canvasShot) && !canvasShot.Secret) return;
        canvasRepick = true;
        Dispatcher.BeginInvoke(() =>
        {
            canvasRepick = false;
            if (canvasShot != null && shots.Contains(canvasShot) && !canvasShot.Secret) return;
            canvasShot = null; canvasBitmap = null;
            foreach (var x in shots.Where(x => !x.Pending && !x.Secret)) if (SetCanvasPicture(x)) break;      // the newest that opens
            RefreshCanvas();          // a live view or start picture stays on top
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

    // the wheel scrolls the strip sideways, as the editor's results strip does
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
        // the picture under a live view or the start picture brings itself back too
        if (!pickingOnStrip && RecentStrip.SelectedItem is Shot s && (s != canvasShot || canvasLive != null || StartOnCanvas)) ShowOnCanvas(s);
    }

    void CanvasImage_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2 && canvasLive == null && !StartOnCanvas) OpenViewer(canvasShot); }

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
