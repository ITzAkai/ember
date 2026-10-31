// Who is in a picture, for Describe. SmilingWolf's WD EVA02 tagger (ONNX, Apache 2.0, run here on the CPU) tells a femboy, a trans woman
// and a woman apart from the body far better than the prompt writer's own eyes: on 307 pictures, femboys 87 % against about 45 %, and no
// woman called anything else. Its answer goes to the writer as a fact. Optional: without the model file Describe works as before.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageForge;

public partial class MainWindow
{
    static string TaggerModel => Path.Combine(Root, "ComfyUI", "models", "onnx", "wd-eva02-large-tagger-v3.onnx");   // in the catalog, pinned by SHA-256
    static Task<InferenceSession> tagger;       // loaded on first use (1.2 GB), then kept; the CPU, as the graphics card is about to hold the writer
    // where the tags used here sit among its 10,861 outputs (from the model's selected_tags.csv; the file is pinned, so they can't move)
    static readonly Dictionary<string, int> TagAt = new()
    {
        ["1girl"] = 4, ["solo"] = 5, ["male_focus"] = 72, ["penis"] = 113, ["testicles"] = 465, ["otoko_no_ko"] = 643, ["bulge"] = 949, ["futanari"] = 999,
    };

    // the note for the prompt writer and, in a few words, what the tagger saw; nulls when there's no tagger or nothing it's sure of
    async Task<(string Note, string Saw)> WhoNote(BitmapSource img)
    {
        if (!File.Exists(TaggerModel)) return default;
        try
        {
            var session = await (tagger ??= Task.Run(() => new InferenceSession(TaggerModel)));
            var input = session.InputMetadata.First();
            int size = input.Value.Dimensions[1];
            var pixels = TaggerPixels(img, size);
            var p = await Task.Run(() =>
            {
                using var r = session.Run(new[] { NamedOnnxValue.CreateFromTensor(input.Key, new DenseTensor<float>(pixels, new[] { 1, size, size, 3 })) });
                return r.First().AsEnumerable<float>().ToArray();
            });
            if (p.Length != 10861) return default;         // some other file under that name
            float S(params string[] tags) => tags.Max(t => p[TagAt[t]]);

            // thresholds tried on the user's pictures, by what each one's own prompt asked for (femboy, trans woman, woman, a woman with a man)
            bool male = S("penis", "testicles", "bulge") > .25f;
            bool femboy = S("otoko_no_ko") > .5f || male && S("male_focus") > .7f;      // male focus: the one shown is male (not a woman with a man's penis in view)
            bool trans = S("futanari") > .2f;
            const string Sure = "A picture tagger, which is reliable about this, found ", Femboy = "a femboy (an adult man with a feminine look)",
                Trans = "a trans woman (breasts and a penis)";
            if (S("solo") > .5f)
                return trans ? (Sure + $"that the person is {Trans}: call her \"a trans woman\", in those words, and describe her body as it is. ", "a trans woman")
                    : femboy ? (Sure + $"that the person is {Femboy}: call him \"a femboy\", in that word, never a woman, and describe his body as it is. ", "a femboy")
                    : S("1girl") > .5f && !male ? (Sure + "that the person is a woman. ", "a woman") : default;
            var found = new[] { trans ? Trans : null, femboy ? Femboy : null }.Where(x => x != null).ToList();
            return found.Count == 0 ? default
                : (Sure + string.Join(" and ", found) + " among the people: tell from each body which one that is, call them by those words (\"a femboy\", \"a trans woman\"), and the others what they are. ",
                   string.Join(" and ", found.Select(x => x[..x.IndexOf(" (")])) + " among them");
        }
        catch { tagger = null; return default; }       // the writer judges by itself, as before
    }

    // the picture centred on a white square of the tagger's size, as blue, green, red values from 0 to 255, row by row
    static float[] TaggerPixels(BitmapSource img, int size)
    {
        double scale = (double)size / Math.Max(img.PixelWidth, img.PixelHeight), w = img.PixelWidth * scale, h = img.PixelHeight * scale;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
            dc.DrawImage(img, new Rect((size - w) / 2, (size - h) / 2, w, h));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var bgra = new byte[size * size * 4];
        rtb.CopyPixels(bgra, size * 4, 0);
        var f = new float[size * size * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3) { f[j] = bgra[i]; f[j + 1] = bgra[i + 1]; f[j + 2] = bgra[i + 2]; }
        return f;
    }
}
