// The centre of the window: the picture you're on (the canvas), the whole gallery, or the photo editor; the rail picks which.
using System.Windows;

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

    void GalleryRail_Click(object sender, RoutedEventArgs e) => ShowCentre(Centre.Gallery);

    // a mode's rail item brings its view back even when that mode is already on (Checked fires only on a change)
    void ModeRail_Click(object sender, RoutedEventArgs e) => ShowCentre(EditMode.IsChecked == true ? Centre.Editor : Centre.Canvas);
}
