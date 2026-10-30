// Remove background: BiRefNet (an ONNX model, run here in the app on the graphics card through DirectML, else the CPU)
// finds the subject; everything else becomes transparent. Saved as cut_NNNNN_.png beside the other images.
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageForge;

public partial class MainWindow
{
    static string CutterModel => Path.Combine(Root, "ComfyUI", "models", "onnx", "BiRefNet_lite.onnx");   // a property: static fields in other files may run before Root is set
    static InferenceSession cutter;     // loaded on first use, then kept
    static readonly object cutterLock = new();
    const int CutSize = 1024;           // the model's fixed input size

    void Cutout_Click(object sender, RoutedEventArgs e) { if (viewing != null) _ = Cutout(viewing); }
    void MenuCutout_Click(object sender, RoutedEventArgs e) { if (ShotOf(sender) is { Pending: false } s) _ = Cutout(s); }

    async Task Cutout(Shot s)
    {
        if (!File.Exists(CutterModel)) { Note("Removing backgrounds needs ComfyUI\\models\\onnx\\BiRefNet_lite.onnx (see Models).", true); return; }
        CloseViewer();
        var slot = new Shot { Pending = true, Progress = "Cutting out" };
        shots.Insert(0, slot);
        AllView.IsChecked = true;
        GalleryScroll.ScrollToTop();
        StripScroll.ScrollToLeftEnd();
        var started = DateTime.Now;
        try
        {
            var src = LoadImage(s.FilePath, 0);
            var meta = (s.Meta ?? new Meta(null, null, null, null, null, null, null, null)) with { From = "Background removed from " + Path.GetFileName(s.FilePath) };
            var path = NextFile("cut");
            await Task.Run(() => SaveCutout(src, meta, path));
            try { Directory.CreateDirectory(Path.GetDirectoryName(OriginalOf(path))); File.Copy(s.FilePath, OriginalOf(path), true); } catch { }
            var shot = MakeShot(path);
            int i = shots.IndexOf(slot);
            if (shot != null && i >= 0) { shots[i] = shot; ShowNewest(shot); } else shots.Remove(slot);
            Note($"Background removed in {(DateTime.Now - started).TotalSeconds:0.0} s.");
        }
        catch (Exception ex) { shots.Remove(slot); Note("Couldn't remove the background: " + ex.Message, true); }
    }

    // next free cut_NNNNN_.png, numbered like ComfyUI's own files
    string NextFile(string prefix)
    {
        var dir = Path.Combine(Output, "forge");
        Directory.CreateDirectory(dir);
        int n = Directory.GetFiles(dir, prefix + "_*.png")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Split('_')[1], out var k) ? k : 0).DefaultIfEmpty(0).Max();
        return Path.Combine(dir, $"{prefix}_{n + 1:00000}_.png");
    }

    static void SaveCutout(BitmapSource src, Meta meta, string path)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);

        // the model sees a 1024×1024 copy, normalised the way it was trained (ImageNet mean and spread)
        var input = new DenseTensor<float>(new[] { 1, 3, CutSize, CutSize });
        float[] mean = { 0.485f, 0.456f, 0.406f }, spread = { 0.229f, 0.224f, 0.225f };
        for (int y = 0; y < CutSize; y++)
        {
            int sy = Math.Min(h - 1, y * h / CutSize);
            for (int x = 0; x < CutSize; x++)
            {
                int i = (sy * w + Math.Min(w - 1, x * w / CutSize)) * 4;
                input[0, 0, y, x] = (px[i + 2] / 255f - mean[0]) / spread[0];      // R (pixels are stored B, G, R, A)
                input[0, 1, y, x] = (px[i + 1] / 255f - mean[1]) / spread[1];
                input[0, 2, y, x] = (px[i] / 255f - mean[2]) / spread[2];
            }
        }
        float[] mask;
        lock (cutterLock)
        {
            cutter ??= OpenCutter();
            using var results = cutter.Run(new[] { NamedOnnxValue.CreateFromTensor(cutter.InputMetadata.Keys.First(), input) });
            mask = results.First().AsEnumerable<float>().ToArray();
        }
        if (mask.Min() < -0.01f || mask.Max() > 1.01f)                         // raw scores: squash to 0..1
            for (int i = 0; i < mask.Length; i++) mask[i] = 1 / (1 + MathF.Exp(-mask[i]));

        // back to the picture's own size (bilinear) as its alpha channel
        for (int y = 0; y < h; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * CutSize / h - 0.5f, 0, CutSize - 1);
            int y0 = (int)fy, y1 = Math.Min(CutSize - 1, y0 + 1);
            float ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * CutSize / w - 0.5f, 0, CutSize - 1);
                int x0 = (int)fx, x1 = Math.Min(CutSize - 1, x0 + 1);
                float tx = fx - x0;
                float a = (mask[y0 * CutSize + x0] * (1 - tx) + mask[y0 * CutSize + x1] * tx) * (1 - ty)
                        + (mask[y1 * CutSize + x0] * (1 - tx) + mask[y1 * CutSize + x1] * tx) * ty;
                px[(y * w + x) * 4 + 3] = (byte)Math.Clamp(a * 255, 0, 255);
            }
        }

        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        try
        {
            var md = new BitmapMetadata("png");
            md.SetQuery("/tEXt/{str=forge}", JsonSerializer.Serialize(meta));   // read back by ReadMeta: prompt, seed…
            enc.Frames.Add(BitmapFrame.Create(image, null, md, null));
        }
        catch { enc.Frames.Clear(); enc.Frames.Add(BitmapFrame.Create(image)); }
        using var f = File.Create(path);
        enc.Save(f);
    }

    static InferenceSession OpenCutter()
    {
        var o = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };   // what DirectML needs
        try { o.AppendExecutionProvider_DML(0); } catch { /* no DirectML: the CPU does it, slower */ }
        return new InferenceSession(CutterModel, o);
    }
}
