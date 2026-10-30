// The gallery's cards, favourites and moving pictures to the Recycle Bin.
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
    // ---------------- gallery ----------------
    static Shot MakeShot(string path)
    {
        try { return new Shot { FilePath = path, Thumb = LoadImage(path, 360), Meta = ReadMeta(path), Date = File.GetLastWriteTime(path) }; }
        catch { return null; }
    }

    // --selftest: checks that need no ComfyUI; exit code 0 when they pass. A picture saved again under the same name
    // must load as the new picture (ComfyUI reuses file names after deletes; WPF's image cache used to show the old one).
    public static int SelfTest()
    {
        var file = Path.Combine(Path.GetTempPath(), "imageforge-selftest.png");
        static void Save(string f, Color c)
        {
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, px, 16)));
            using var s = File.Create(f);
            enc.Save(s);
        }
        static Color First(BitmapSource b)
        {
            var px = new byte[4];
            new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }
        try
        {
            Save(file, Colors.Red);
            var first = First(LoadImage(file, 0));
            Save(file, Colors.Blue);
            var second = First(LoadImage(file, 0));
            return (first == Colors.Red && second == Colors.Blue ? 0 : 1) | (SelfTestLoraConvert() ? 0 : 4) | (SelfTestStrength() ? 0 : 8) | (SelfTestGguf() ? 0 : 16) | (SelfTestSecret(file) ? 0 : 32) | (SelfTestMinorBlock() ? 0 : 64) | (SelfTestLoraFamily() ? 0 : 128) | (SelfTestStyles() ? 0 : 256) | (SelfTestLineage() ? 0 : 512) | (SelfTestReseed() ? 0 : 1024) | (SelfTestSettings() ? 0 : 2048) | (SelfTestAllDark() ? 0 : 4096) | (SelfTestWorkflow() ? 0 : 8192) | (SelfTestUpdateKey() ? 0 : 16384) | (ErrorReport.SelfTest() ? 0 : 32768);
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }      // seen when run with stderr redirected (a build script)
        finally { try { File.Delete(file); } catch { } }
    }

    // decoded into memory (the file isn't locked) and frozen so it can cross threads
    static BitmapSource LoadImage(string path, int width)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        if (path.EndsWith(".locked")) bi.StreamSource = OpenPicture(path);     // (the image cache is for files only: it throws on a stream)
        else
        {
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;     // ComfyUI reuses file names after deletes: never show a stale picture
            bi.UriSource = new Uri(path);
        }
        if (width > 0) bi.DecodePixelWidth = width;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    // ---------------- favourites and delete ----------------
    void ToggleFavorite(Shot s)
    {
        if (s == null || s.Pending) return;
        s.Favorite = !s.Favorite;
        var name = Path.GetFileName(s.FilePath);
        if (s.Favorite) settings.Favorites.Add(name); else settings.Favorites.Remove(name);
        settings.Save();
        if (ViewKind == "favourites") galleryView.Refresh();
        UpdateCount();
        if (s == viewing) ShowFavorite();
    }

    // to the Recycle Bin, never a permanent delete
    bool Delete(Shot s)
    {
        if (s == null || s.Pending) return false;
        if (s == viewing) StopClip();
        if (s.IsVideo && File.Exists(s.VideoPath))
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(s.VideoPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch (Exception ex) { Note("Couldn't delete: " + ex.Message, true); return false; }
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(s.FilePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { Note("Couldn't delete: " + ex.Message, true); return false; }
        if (File.Exists(OriginalOf(s.FilePath)))
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(OriginalOf(s.FilePath), Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch { }
        if (settings.Favorites.Remove(Path.GetFileName(s.FilePath))) settings.Save();
        shots.Remove(s);
        return true;
    }

    void Star_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                        // on mouse-down, before the card button takes the click: don't also open the viewer
        ToggleFavorite(ShotOf(sender));
    }
    void MenuFavorite_Click(object sender, RoutedEventArgs e) => ToggleFavorite(ShotOf(sender));
    void MenuDelete_Click(object sender, RoutedEventArgs e) { if (Delete(ShotOf(sender))) Note("Moved to the Recycle Bin."); }

    void FavViewed_Click(object sender, RoutedEventArgs e) => ToggleFavorite(viewing);

    void ShowFavorite()
    {
        FavBtn.Content = viewing?.Favorite == true ? "" : "";
        FavBtn.Foreground = Brush(viewing?.Favorite == true ? "Accent" : "Muted");
    }

    void DeleteViewed_Click(object sender, RoutedEventArgs e) => RemoveViewed(Delete, "Moved to the Recycle Bin.");

    // the image on screen leaves this view (deleted, hidden…): show the next one
    void RemoveViewed(Func<Shot, bool> remove, string note)
    {
        var list = Browsable().ToList();
        int i = list.IndexOf(viewing);
        if (!remove(viewing)) return;
        if (i < 0) { CloseViewer(); Note(note); return; }      // it wasn't in the list being walked
        list.RemoveAt(i);
        if (list.Count == 0) { CloseViewer(); Note(note); return; }
        OpenViewer(list[Math.Min(i, list.Count - 1)]);
        ViewerNote.Text = note;
    }

    void Gallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (GalleryScroll.VerticalOffset + GalleryScroll.ViewportHeight > GalleryScroll.ExtentHeight - 700) _ = LoadMore(60);   // older images as you near the end
        if (e.ViewportWidthChange != 0) SizeCards();
    }

    // Ctrl+scroll: bigger or smaller pictures
    void Gallery_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        settings.CardSize = Math.Clamp(settings.CardSize + Math.Sign(e.Delta) * 60, 130, 370);
        SizeCards();
    }

    // cards share the row evenly instead of leaving a ragged gap on the right
    void SizeCards()
    {
        double w = GalleryScroll.ViewportWidth;
        int cols = Math.Max(1, (int)(w / settings.CardSize));
        Resources["Card"] = Math.Floor(w / cols) - 12;
    }

    // ComfyUI writes its graph into the PNG's "prompt" text chunk (before the image data).
    // Upscales also carry a "forge" chunk: the source image's details, which win over the (prompt-less) upscale graph.
    static Meta ReadMeta(string path)
    {
        Meta fromGraph = null;
        try
        {
            using var fs = OpenPicture(path);
            var head = new byte[8];
            fs.ReadExactly(head);
            while (true)
            {
                fs.ReadExactly(head);
                int len = BinaryPrimitives.ReadInt32BigEndian(head);
                var kind = Encoding.ASCII.GetString(head, 4, 4);
                if (kind is "IDAT" or "IEND") return fromGraph;
                if (kind != "tEXt") { fs.Seek(len + 4, SeekOrigin.Current); continue; }
                var chunk = new byte[len];
                fs.ReadExactly(chunk);
                fs.Seek(4, SeekOrigin.Current);
                var t = Encoding.UTF8.GetString(chunk);
                int z = t.IndexOf('\0');
                if (z <= 0) continue;
                if (t[..z] == "forge") return JsonSerializer.Deserialize<Meta>(t[(z + 1)..]);
                if (t[..z] == "prompt") fromGraph = ParseMeta(JsonNode.Parse(t[(z + 1)..]) as JsonObject);
            }
        }
        catch { return fromGraph; }
    }

    static Meta ParseMeta(JsonObject g)
    {
        if (g == null) return null;
        var nodes = g.Select(kv => kv.Value).ToList();
        JsonNode First(params string[] types) => nodes.FirstOrDefault(n => types.Contains(Str(n?["class_type"])))?["inputs"];
        string TextOf(JsonNode link) => link is JsonArray a && a.Count > 0 && Str(a[0]) is string id ? Str(g[id]?["inputs"]?["text"]) : null;
        var ks = First("KSampler", "KSamplerAdvanced");
        var prompt = TextOf(ks?["positive"]) ?? Str(First("CLIPTextEncode")?["text"]);
        var loraNodes = nodes.Where(n => Str(n?["class_type"]) == "LoraLoader").Select(n => n?["inputs"]).Where(i => i != null).ToList();
        string loras = loraNodes.Count == 0 ? null : string.Join("\n", loraNodes.Select(i =>
        {
            var name = Path.GetFileNameWithoutExtension(Str(i["lora_name"]) ?? "?");
            var str = Dbl(i["strength_model"]) ?? 1;
            return $"{name} ({str:0.##})";
        }));
        return new Meta(prompt, TextOf(ks?["negative"]), Long(ks?["seed"] ?? ks?["noise_seed"]), Long(ks?["steps"]), Dbl(ks?["cfg"]),
            Str(ks?["sampler_name"]), Str(ks?["scheduler"]),
            Str(g["4"]?["inputs"]?["ckpt_name"]) ?? Str(g["4"]?["inputs"]?["unet_name"]) ?? Str(First("CheckpointLoaderSimple")?["ckpt_name"]),
            Loras: loras);   // node 4 is the picked model; Krea, Qwen and Anima load theirs with UNETLoader
    }
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) ? s : null;
    static long? Long(JsonNode n) => n is JsonValue v && v.TryGetValue(out long x) ? x : null;
    static double? Dbl(JsonNode n) => n is JsonValue v && v.TryGetValue(out double x) ? x : null;

    static Shot ShotOf(object sender) => (sender as FrameworkElement)?.DataContext as Shot;

    void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenViewer(ShotOf(sender));
    void MenuPrompt_Click(object sender, RoutedEventArgs e) => UsePromptOf(ShotOf(sender)?.Meta);
    void MenuCopy_Click(object sender, RoutedEventArgs e) { if (ShotOf(sender) is { Pending: false } s) Clipboard.SetImage(LoadImage(s.FilePath, 0)); }
    void MenuFolder_Click(object sender, RoutedEventArgs e) { if (ShotOf(sender) is { Pending: false } s) ShowInFolder(s.FilePath); }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Output, "forge");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }
    static void ShowInFolder(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");
}
