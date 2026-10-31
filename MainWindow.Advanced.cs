// Advanced settings in collapsible sections, with a search box (only matching sections stay, opened) and Reset section buttons.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ImageForge;

public partial class MainWindow
{
    Dictionary<ToggleButton, bool> openBeforeSearch;

    (FrameworkElement Header, ToggleButton Toggle, StackPanel Body)[] AdvancedSections =>
    [
        (ModelSectionHeader, ModelSectionToggle, ModelSectionBody),
        (VideoSectionHeader, VideoSectionToggle, VideoSectionBody), (SamplingSectionHeader, SamplingSectionToggle, SamplingSectionBody), (BoostersSectionHeader, BoostersSectionToggle, BoostersSectionBody),
    ];

    // every word a section shows or explains: titles, labels, tooltips and item names
    static IEnumerable<string> Words(DependencyObject o)
    {
        switch (o)
        {
            case TextBlock tb: yield return tb.Text; break;
            case ContentControl cc when cc.Content is string s: yield return s; break;
        }
        if (o is FrameworkElement { ToolTip: string tip }) yield return tip;
        if (o is ItemsControl ic) foreach (var i in ic.Items) if (i is string s) yield return s;
        foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>())
            foreach (var w in Words(child)) yield return w;
    }

    void AdvancedSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = AdvancedSearch.Text.Trim();
        if (q == "")
        {
            if (openBeforeSearch != null) foreach (var (toggle, open) in openBeforeSearch) toggle.IsChecked = open;
            openBeforeSearch = null;
            foreach (var s in AdvancedSections) s.Header.Visibility = Visibility.Visible;
            return;
        }
        openBeforeSearch ??= AdvancedSections.ToDictionary(s => s.Toggle, s => s.Toggle.IsChecked == true);
        foreach (var (header, toggle, body) in AdvancedSections)
        {
            bool match = Words(header).Concat(Words(body)).Any(w => w?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
            header.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            toggle.IsChecked = match;
        }
    }

    void CloseMore_Click(object sender, RoutedEventArgs e) => AdvancedToggle.IsChecked = false;

    // the drawer replaces the panel's scroll area: hidden, its controls aren't reachable by Tab under the drawer
    void MoreToggle_Changed(object sender, RoutedEventArgs e) =>
        LeftScroll.Visibility = AdvancedToggle.IsChecked == true ? Visibility.Hidden : Visibility.Visible;

    void ResetSection_Click(object sender, RoutedEventArgs e)
    {
        var model = ModelBox.SelectedItem as string;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "model":                   // automatic text encoder and VAE
                if (model != null) { var p = ProfileOf(model); p.TextEncoder = p.Vae = null; }
                ShowParts();
                break;
            case "loras":
                LoraRowsPanel.Children.Clear();
                loraRows.Clear();
                UpdateLoraTitles();
                UpdateLoraChip();
                break;
            case "sampling":
                ResetRecommended_Click(null, null);
                RandomSeed.IsChecked = true;
                break;
            case "boosters":
                if (model != null) ProfileOf(model).Boosters = new Boosters();
                ShowBoosters();
                break;
        }
        settings.Save();
        RefreshGuard();
    }
}
