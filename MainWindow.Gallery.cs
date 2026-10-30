// Gallery: every image in output/forge (loaded in pages as you scroll, grouped by day), view tabs, search, multi-select and drag-out.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageForge;

public partial class MainWindow
{
    readonly List<string> unloaded = new();     // older images not on screen yet, newest first
    bool loadingMore, selectMode;
    Point dragFrom;

    // ---------------- loading ----------------
    async Task LoadGallery()
    {
        unloaded.Clear();
        var dir = Path.Combine(Output, "forge");
        if (Directory.Exists(dir))
            unloaded.AddRange(new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName).Where(p => !shots.Any(s => s.FilePath == p)));
        await LoadMore(120);
        if (canvasShot == null) ShowOnCanvas(shots.FirstOrDefault(s => !s.Pending && !s.Secret));
    }

    async Task LoadMore(int count)
    {
        if (loadingMore || unloaded.Count == 0) { UpdateCount(); return; }
        loadingMore = true;
        var batch = unloaded.Take(count).ToList();
        unloaded.RemoveRange(0, batch.Count);
        var loaded = await Task.Run(() => batch.Select(MakeShot).Where(s => s != null).ToList());
        foreach (var s in loaded)
        {
            s.Favorite = settings.Favorites.Contains(Path.GetFileName(s.FilePath));
            s.Selecting = selectMode;
            shots.Add(s);
        }
        loadingMore = false;
        UpdateCount();
    }

    // tabs and search look at every image, so pull in the rest first
    async Task LoadAll()
    {
        while (unloaded.Count > 0)
            if (loadingMore) await Task.Delay(100); else await LoadMore(200);
    }

    // ---------------- what's shown ----------------
    string ViewKind => (string)ViewGroup.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag;   // all, created, edited, upscaled, video, favourites, secret
    bool Searching => SearchBox.Text.Trim() != "";

    bool InView(Shot s)
    {
        if (s.Secret != (ViewKind == "secret")) return false;      // secret pictures only ever show in their own tab
        if (s.Pending) return ViewKind == "all" && !Searching;
        return (ViewKind switch { "all" or "secret" => true, "favourites" => s.Favorite, var kind => s.Kind == kind }) && Matches(s);
    }

    // every word must appear in the prompt (or file name), in any order
    bool Matches(Shot s)
    {
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => s.Caption.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    async void View_Checked(object sender, RoutedEventArgs e)
    {
        if (galleryView == null) return;        // still loading the window
        bool secret = ViewKind == "secret";
        if (!secret) LockSecret();
        else if (secretKey == null && !await OpenSecret()) { AllView.IsChecked = true; return; }
        ExportBtn.Visibility = secret ? Visibility.Collapsed : Visibility.Visible;
        SecretBtn.ToolTip = secret ? "Move out of the secret gallery" : "Move to the secret gallery";
        galleryView.Refresh();
        UpdateCount();
        GalleryScroll.ScrollToTop();
        if (ViewKind is not ("all" or "secret")) { await LoadAll(); galleryView.Refresh(); UpdateCount(); }
    }

    async void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (galleryView == null) return;
        galleryView.Refresh();
        UpdateCount();
        if (Searching && unloaded.Count > 0) { await LoadAll(); galleryView.Refresh(); UpdateCount(); }
    }

    void UpdateCount()
    {
        if (galleryView == null) return;
        string kind = ViewKind;
        int n = galleryView.Cast<Shot>().Count(s => !s.Pending) + (kind == "all" && !Searching ? unloaded.Count : 0);
        CountText.Text = Searching ? $"{n} found" : kind switch
        {
            "all" => n == 1 ? "1 image" : $"{n} images",
            "favourites" => $"{n} favourite{(n == 1 ? "" : "s")}",
            "secret" => $"{n} hidden",
            _ => $"{n} {kind}",
        };
        EmptyState.Visibility = galleryView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        (EmptyTitle.Text, EmptyText.Text) = Searching
            ? ("No matches", $"Nothing in this view has \"{SearchBox.Text.Trim()}\" in its prompt.")
            : kind switch
            {
                "favourites" => ("No favourites yet", "Hover over an image and click its star to keep it here."),
                "edited" => ("No edits yet", "Edit a photo, make variations or expand an image, and they show up here."),
                "upscaled" => ("No upscales yet", "Open an image and press Upscale for a sharper, bigger copy."),
                "secret" => ("Nothing hidden yet", "Right-click an image and pick Move to secret gallery. Only your password opens it."),
                _ => ("No images yet", "Write a prompt on the left and press Generate."),
            };
        UpdateSelection();
    }

    // ---------------- click, select, drag ----------------
    // ---------------- live view ----------------
    // a card being made, big: its preview updates each step, and the finished picture opens in the viewer when it arrives
    void OpenLive(Shot slot)
    {
        LivePanel.DataContext = slot;
        LivePanel.Visibility = Visibility.Visible;
    }

    void CloseLive() { LivePanel.Visibility = Visibility.Collapsed; LivePanel.DataContext = null; }
    void CloseLive_Click(object sender, RoutedEventArgs e) => CloseLive();
    void LiveBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseLive();

    void Card_Click(object sender, RoutedEventArgs e)
    {
        var s = ShotOf(sender);
        if (s?.Pending == true) { OpenLive(s); return; }
        if (s == null) return;
        if (!selectMode && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SetSelectMode(true);   // Ctrl+click starts picking
        if (selectMode) { s.Selected = !s.Selected; UpdateSelection(); }
        else OpenViewer(s);
    }

    void Select_Click(object sender, RoutedEventArgs e) => SetSelectMode(!selectMode);

    void SetSelectMode(bool on)
    {
        selectMode = on;
        foreach (var s in shots) { s.Selecting = on; if (!on) s.Selected = false; }
        SelectionBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    List<Shot> Picked() => shots.Where(s => s.Selected && !s.Pending).ToList();

    void UpdateSelection()
    {
        if (!selectMode) return;
        int n = shots.Count(s => s.Selected);
        SelectionText.Text = n == 0 ? "Pick images" : $"{n} selected";
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var shown = galleryView.Cast<Shot>().Where(s => !s.Pending).ToList();
        bool all = shown.All(s => s.Selected);
        foreach (var s in shown) s.Selected = !all;          // second press clears
        UpdateSelection();
    }

    void FavSelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        bool all = picked.Count > 0 && picked.All(s => s.Favorite);
        foreach (var s in picked.Where(s => s.Favorite == all)) ToggleFavorite(s);   // all starred: unstar them; otherwise star the rest
    }

    void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Copy {picked.Count} image{(picked.Count == 1 ? "" : "s")} to…" };
        if (dlg.ShowDialog(this) != true) return;
        int copied = 0;
        foreach (var s in picked)
        {
            try
            {
                var from = FileOf(s);                                // a clip's MP4, not its poster
                File.Copy(from, FreeName(dlg.FolderName, Path.GetFileNameWithoutExtension(s.FilePath) + Path.GetExtension(from)));
                copied++;
            }
            catch (Exception ex) { Note("Couldn't copy " + Path.GetFileName(s.FilePath) + ": " + ex.Message, true); }
        }
        if (copied == picked.Count) Note($"Copied {copied} image{(copied == 1 ? "" : "s")} to {dlg.FolderName}.");
    }

    // dir\file + ext, or "name (2).png" + ext… when taken: never overwrite
    static string FreeName(string dir, string file, string ext = "")
    {
        string stem = Path.GetFileNameWithoutExtension(file), type = Path.GetExtension(file), dest = Path.Combine(dir, file + ext);
        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{stem} ({i}){type}{ext}");
        return dest;
    }

    void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked.Count == 0) return;
        if (picked.Count > 1 && MessageBox.Show(this, $"Move {picked.Count} images to the Recycle Bin?", "Ember",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int gone = picked.Count(Delete);
        Note($"Moved {gone} image{(gone == 1 ? "" : "s")} to the Recycle Bin.");
        UpdateSelection();
    }

    // drag a card out to Explorer, a chat app, a browser… (in select mode, a picked card brings all picked images)
    void Card_MouseDown(object sender, MouseButtonEventArgs e) => dragFrom = e.GetPosition(this);

    void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ShotOf(sender) is not { Pending: false, Secret: false } s) return;
        var d = e.GetPosition(this) - dragFrom;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var files = selectMode && s.Selected ? Picked().Select(FileOf).ToArray() : new[] { FileOf(s) };
        ((UIElement)sender).ReleaseMouseCapture();          // the card button lets go, so no click fires afterwards
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy);
    }
}
