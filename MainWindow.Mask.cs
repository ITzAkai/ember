// Marking what to change: brush strokes (the InkCanvas) plus rectangle and lasso shapes, and an Invert switch.
// Two tools work from the marks: Remove (fill the area from its surroundings) and Fix detail (redraw a small area at high resolution).
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageForge;

public partial class MainWindow
{
    readonly List<Geometry> shapes = new();     // rectangles and lassos, in photo pixels
    readonly List<object> marks = new();        // strokes and shapes in the order they were made, for undo
    readonly List<Point> lassoPoints = new();
    bool shaping;
    Point shapeFrom;

    bool ShapeTool => RectTool.IsChecked == true || LassoTool.IsChecked == true;
    bool HasMarks => Ink.Strokes.Count > 0 || shapes.Count > 0;
    // orange: will change; green: kept (Invert is on)
    Color MarkColor => InvertToggle?.IsChecked == true ? Color.FromRgb(0x5B, 0xC9, 0x8B) : Color.FromRgb(0xF0, 0xA0, 0x4B);

    void ClearMarks()
    {
        Ink.Strokes.Clear();
        shapes.Clear();
        marks.Clear();
        DrawShapes();
    }

    void ClearMask_Click(object sender, RoutedEventArgs e) => ClearMarks();

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        while (marks.Count > 0)
        {
            var last = marks[^1];
            marks.RemoveAt(marks.Count - 1);
            if (last is Stroke st && Ink.Strokes.Contains(st)) { Ink.Strokes.Remove(st); return; }
            if (last is Geometry geo && shapes.Remove(geo)) { DrawShapes(); return; }
        }
        if (Ink.Strokes.Count > 0) Ink.Strokes.RemoveAt(Ink.Strokes.Count - 1);   // pieces left by the eraser aren't in the list
    }

    void DrawShapes()
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var g in shapes) group.Children.Add(g);
        ShapesPath.Data = group;
        ShapesPath.Fill = new SolidColorBrush(MarkColor);
    }

    void Invert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var st in Ink.Strokes) st.DrawingAttributes.Color = MarkColor;
        UpdateBrush();
        DrawShapes();
        Note(InvertToggle.IsChecked == true ? "Inverted: what you mark (green) stays; everything else changes." : "");
    }

    // ---------------- rectangle and lasso ----------------
    void Shape_MouseDown(object sender, MouseButtonEventArgs e)
    {
        shaping = true;
        shapeFrom = ClampToPhoto(e.GetPosition(EditorCanvas));
        lassoPoints.Clear();
        lassoPoints.Add(shapeFrom);
        ShapePreview.StrokeThickness = 1.5 * ScreenPixel();
        ShapeInput.CaptureMouse();
        e.Handled = true;
    }

    void Shape_MouseMove(object sender, MouseEventArgs e)
    {
        if (!shaping) return;
        var p = ClampToPhoto(e.GetPosition(EditorCanvas));
        if (LassoTool.IsChecked == true) { lassoPoints.Add(p); ShapePreview.Data = Polygon(lassoPoints); }
        else ShapePreview.Data = new RectangleGeometry(new Rect(shapeFrom, p));
    }

    void Shape_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!shaping) return;
        shaping = false;
        ShapeInput.ReleaseMouseCapture();
        var geo = ShapePreview.Data;
        ShapePreview.Data = null;
        if (geo == null || geo.Bounds.Width * geo.Bounds.Height < 64) return;     // a click, not a shape
        geo.Freeze();
        shapes.Add(geo);
        marks.Add(geo);
        DrawShapes();
    }

    Point ClampToPhoto(Point p) => new(Math.Clamp(p.X, 0, photo.PixelWidth), Math.Clamp(p.Y, 0, photo.PixelHeight));
    static Geometry Polygon(List<Point> pts) =>
        new PathGeometry(new[] { new PathFigure(pts[0], new[] { new PolyLineSegment(pts.Skip(1), true) }, true) });

    // ---------------- marks -> mask ----------------
    // 0..255 per pixel (255 = repaint), with a soft edge so the blend doesn't show; null if nothing is marked
    byte[] MaskAlpha(bool honourInvert = true)
    {
        if (!HasMarks) return null;
        int w = photo.PixelWidth, h = photo.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            foreach (var geo in shapes) dc.DrawGeometry(Brushes.White, null, geo);
            foreach (var stroke in Ink.Strokes)
            {
                var white = stroke.Clone();
                white.DrawingAttributes.Color = Colors.White;
                white.Draw(dc);
            }
        }
        var hard = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        hard.Render(dv);
        var blur = new Image { Source = hard, Width = w, Height = h, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 16 } };
        var px = RenderPixels(blur, w, h);
        bool invert = honourInvert && InvertToggle.IsChecked == true;
        var mask = new byte[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = invert ? (byte)(255 - px[i * 4 + 2]) : px[i * 4 + 2];
        return mask;
    }

    // ---------------- remove ----------------
    async void RemoveObject_Click(object sender, RoutedEventArgs e)
    {
        if (photo == null || !ready) return;
        if (CropLayer.Visibility == Visibility.Visible) { Note("Apply or cancel the crop first.", true); return; }
        var mask = MaskAlpha(honourInvert: false);
        if (mask == null) { Note("Mark what to remove first: brush, rectangle or lasso.", true); return; }
        int w = photo.PixelWidth, h = photo.PixelHeight;
        var px = new byte[w * h * 4];
        photo.CopyPixels(px, w * 4, 0);
        var before = MaskedPng((byte[])px.Clone(), w, h, null);
        FillHoles(px, mask, w, h);                  // start from the surroundings' colours, not the object's
        long seed = Random.Shared.NextInt64(0, 1L << 48);
        // always the inpainting model when installed: at CFG 1 (Krea 2 and other turbo models) "no people" does nothing, and tested on
        // Krea 2 the picked model painted a new person or a crowd into the gap, where the inpainting model left it clean
        bool inpaint = inpaintModel != null && baseCheckpoint != null;
        await Enqueue(1, async job =>
        {
            job.Before = before;
            var image = await Upload(MaskedPng(px, w, h, mask), job);
            var g = Graph(seed, image, masked: true, denoise: inpaint ? 1.0 : 0.85, plain: true, count: 1);
            ((JsonObject)g["6"]["inputs"])["text"] = "empty background, seamless natural continuation of the surroundings, nothing there";
            ((JsonObject)g["7"]["inputs"])["text"] = "person, people, animal, object, text, watermark, frame, border, artifacts, blurry";
            if (inpaint) UseInpaintModel(g);
            Soften(g, 12, 6);
            return new JsonObject { ["prompt"] = g, ["client_id"] = clientId };
        }, label: "Removing");
    }

    // painted edits and Expand: the SDXL inpainting model when it's installed and the setting is on, otherwise the picked model
    bool UsingInpaintModel => inpaintModel != null && baseCheckpoint != null && settings.InpaintEdits;

    // the marks grown and blurred, so a change fades in over their edge instead of stopping at a hard line, and Differential Diffusion,
    // which changes the middle fully and the soft edge only a little, so the model paints the join itself. Tested on Remove and Expand:
    // with hard marks the redrawn part showed as a patch. Not with Crop & Stitch, which blends its own crop.
    void Soften(JsonObject g, int grow, double blur, bool diffuse = true)
    {
        g["grow"] = Node("GrowMask", new() { ["mask"] = Link("10", 1), ["expand"] = grow, ["tapered_corners"] = true });
        g["mimg"] = Node("MaskToImage", new() { ["mask"] = Link("grow", 0) });
        g["mblur"] = Node("ImageBlur", new() { ["image"] = Link("mimg", 0), ["blur_radius"] = 31, ["sigma"] = blur });
        g["soft"] = Node("ImageToMask", new() { ["image"] = Link("mblur", 0), ["channel"] = "red" });
        foreach (var id in new[] { "13", "15", "43" })
            if (g[id]?["inputs"] is JsonObject i && i.ContainsKey("mask")) i["mask"] = Link("soft", 0);
        if (!diffuse) return;
        var k = (JsonObject)g["3"]["inputs"];
        g["dd"] = Node("DifferentialDiffusion", new() { ["model"] = k["model"].DeepClone(), ["strength"] = 1.0 });
        k["model"] = Link("dd", 0);
    }

    // a painted edit with a description, made by Qwen-Image 2.1 when it's installed: it reads the description as an instruction ("make the
    // door red", "fix the hand") and redraws the whole picture from it, so the change fits the light; only the painted part is kept.
    // Tested on Krea 2: asked for a red door, Krea 2 left it purple at any strength and redrew the window; Qwen-Image made it red and kept
    // the rest. A Turbo copy is preferred (8 steps); Noct Q Anime would turn photos into anime.
    string QwenEditModel => MissingParts("qwen_image") ? null : Qwen && !Named("noct") ? (string)ModelBox.SelectedItem
        : separate.Where(kv => kv.Value.Family == "qwen_image" && !kv.Key.Contains("noct", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key)
            .OrderBy(m => m.Contains("turbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();

    void UseQwenEdit(JsonObject g, string model, string instruction)
    {
        var r = RecFor(model);
        AllActiveLoras(instruction.Trim(), out instruction);       // <lora:…> tags out of the words, as in Qwen's own edits
        g["q_model"] = UnetNode(separate[model].Unet);
        g["q_clip"] = ClipNode(TeFor("qwen_image", model), "qwen_image");
        g["q_vae"] = Node("VAELoader", new() { ["vae_name"] = VaeFor("qwen_image", model) });
        g["q_enc"] = Node("TextEncodeQwenImage21", new()
        {
            ["clip"] = Link("q_clip", 0), ["prompt"] = instruction + " Keep everything else exactly the same.", ["negative_prompt"] = "",
            ["vae"] = Link("q_vae", 0), ["resolution"] = 1024, ["images.image_1"] = Link("10", 0),
        });
        ((JsonObject)g["12"]["inputs"])["samples"] = Link("q_enc", 2);
        var k = (JsonObject)g["3"]["inputs"];
        k["model"] = Link("q_model", 0); k["positive"] = Link("q_enc", 0); k["negative"] = Link("q_enc", 1); k["denoise"] = 1.0;
        k["steps"] = r.Steps[1]; k["cfg"] = r.Cfg; k["sampler_name"] = r.Sampler; k["scheduler"] = r.Scheduler;
        ((JsonObject)g["8"]["inputs"])["vae"] = Link("q_vae", 0);
        ((JsonObject)g["15"]["inputs"])["resize_source"] = true;            // Qwen picks its own size near 1 megapixel, same shape
        foreach (var id in g.Where(kv => kv.Value?["class_type"]?.ToString() == "LoraLoader").Select(kv => kv.Key).ToList()) g.Remove(id);
        foreach (var unused in new[] { "4", "44", "45", "6", "7", "11", "13" }) g.Remove(unused);
        Soften(g, 8, 6, diffuse: false);
    }

    // swap a masked edit over to the SDXL inpainting model: it sees the hole and its edges, so fills blend far better
    void UseInpaintModel(JsonObject g)
    {
        g["41"] = Node("UNETLoader", new() { ["unet_name"] = inpaintModel, ["weight_dtype"] = "default" });
        g["42"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = baseCheckpoint });   // its CLIP and VAE
        foreach (var id in new[] { "6", "7" }) ((JsonObject)g[id]["inputs"])["clip"] = Link("42", 1);
        g["43"] = Node("InpaintModelConditioning", new()
        {
            ["positive"] = Link("6", 0), ["negative"] = Link("7", 0), ["vae"] = Link("42", 2),
            ["pixels"] = Link("10", 0), ["mask"] = Link("10", 1), ["noise_mask"] = true,
        });
        ((JsonObject)g["12"]["inputs"])["samples"] = Link("43", 2);
        var k = (JsonObject)g["3"]["inputs"];
        k["model"] = Link("41", 0); k["positive"] = Link("43", 0); k["negative"] = Link("43", 1);
        k["steps"] = 25; k["cfg"] = 7.0; k["sampler_name"] = "dpmpp_2m"; k["scheduler"] = "karras";
        ((JsonObject)g["8"]["inputs"])["vae"] = Link("42", 2);
        // the usual model, its loaders and LoRAs, and the latent path (the inpainting nodes 41-43 above stay)
        foreach (var id in g.Where(kv => kv.Value?["class_type"]?.ToString() == "LoraLoader").Select(kv => kv.Key).ToList()) g.Remove(id);
        foreach (var unused in new[] { "4", "44", "45", "11", "13" }) g.Remove(unused);
    }

    // Fills marked pixels from the unmarked ones around them ("push-pull"): average down a pyramid using only
    // known pixels, then paint back up, so a hole gets a smooth blend of its surroundings instead of its old content.
    static void FillHoles(byte[] px, byte[] mask, int w, int h)
    {
        var levels = new List<(float[] C, float[] A, int W, int H)>();
        var c0 = new float[w * h * 3];
        var a0 = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            float known = mask[i] < 128 ? 1 : 0;
            a0[i] = known;
            for (int ch = 0; ch < 3; ch++) c0[i * 3 + ch] = px[i * 4 + ch] * known;     // colour times confidence
        }
        levels.Add((c0, a0, w, h));
        while (levels[^1].W > 1 || levels[^1].H > 1)                                   // pull: halve, averaging known pixels
        {
            var (c, a, lw, lh) = levels[^1];
            int nw = Math.Max(1, (lw + 1) / 2), nh = Math.Max(1, (lh + 1) / 2);
            var nc = new float[nw * nh * 3];
            var na = new float[nw * nh];
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x, j = y / 2 * nw + x / 2;
                    na[j] += a[i];
                    for (int ch = 0; ch < 3; ch++) nc[j * 3 + ch] += c[i * 3 + ch];
                }
            for (int j = 0; j < na.Length; j++)
                if (na[j] > 0)
                {
                    float k = Math.Min(1, na[j]) / na[j];
                    for (int ch = 0; ch < 3; ch++) nc[j * 3 + ch] *= k;
                    na[j] = Math.Min(1, na[j]);
                }
            levels.Add((nc, na, nw, nh));
        }
        for (int l = levels.Count - 2; l >= 0; l--)                                      // push: fill gaps from the coarser level
        {
            var (c, a, lw, lh) = levels[l];
            var (pc, _, pw, _) = levels[l + 1];
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x, j = y / 2 * pw + x / 2;
                    float missing = 1 - a[i];
                    if (missing <= 0) continue;
                    for (int ch = 0; ch < 3; ch++) c[i * 3 + ch] += missing * pc[j * 3 + ch];
                    a[i] = 1;
                }
        }
        for (int i = 0; i < w * h; i++)
            if (mask[i] >= 128)
                for (int ch = 0; ch < 3; ch++) px[i * 4 + ch] = (byte)Math.Clamp(c0[i * 3 + ch], 0, 255);
    }

    // ---------------- fix detail ----------------
    // a face or hand that is small in the picture gets few pixels; redraw just that box at up to 4× and fit it back
    async void FixDetail_Click(object sender, RoutedEventArgs e)
    {
        if (photo == null || !ready) return;
        if (CropLayer.Visibility == Visibility.Visible) { Note("Apply or cancel the crop first.", true); return; }
        var mask = MaskAlpha(honourInvert: false);
        if (mask == null) { Note("Mark the face or detail to fix first.", true); return; }
        int w = photo.PixelWidth, h = photo.PixelHeight, x0 = w, y0 = h, x1 = 0, y1 = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x] > 32) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        // room around it (35 %, at least 192 px a side) so the redraw sees what surrounds it
        int bw = x1 - x0, bh = y1 - y0;
        int padX = Math.Max((int)(bw * 0.35), (192 - bw) / 2), padY = Math.Max((int)(bh * 0.35), (192 - bh) / 2);
        int rx = Math.Max(0, x0 - padX), ry = Math.Max(0, y0 - padY), rw = Math.Min(w, x1 + padX) - rx, rh = Math.Min(h, y1 + padY) - ry;
        if (rw * rh > 0.6 * w * h) { Note("Fix detail is for small areas like a face. For big areas use Apply edit.", true); return; }
        double k = Math.Min(4, Math.Sqrt(1024.0 * 1024 / (rw * rh)));
        int tw = Round64(rw * k), th = Round64(rh * k);
        long seed = Random.Shared.NextInt64(0, 1L << 48);
        var before = EditPng(null);
        var png = EditPng(mask);
        await Enqueue(1, async job =>
        {
            job.Before = before;
            var image = await Upload(png, job);
            var g = Graph(seed, image, masked: true, denoise: 0.45, count: 1);
            var text = (JsonObject)g["6"]["inputs"];
            text["text"] = text["text"].GetValue<string>() + ", highly detailed, sharp focus, detailed face, detailed eyes";
            g["50"] = Node("ImageCrop", new() { ["image"] = Link("10", 0), ["width"] = rw, ["height"] = rh, ["x"] = rx, ["y"] = ry });
            g["51"] = Node("ImageScale", new() { ["image"] = Link("50", 0), ["upscale_method"] = "lanczos", ["width"] = tw, ["height"] = th, ["crop"] = "disabled" });
            g["52"] = Node("CropMask", new() { ["mask"] = Link("10", 1), ["x"] = rx, ["y"] = ry, ["width"] = rw, ["height"] = rh });
            g["53"] = Node("MaskToImage", new() { ["mask"] = Link("52", 0) });
            g["54"] = Node("ImageScale", new() { ["image"] = Link("53", 0), ["upscale_method"] = "bilinear", ["width"] = tw, ["height"] = th, ["crop"] = "disabled" });
            g["55"] = Node("ImageToMask", new() { ["image"] = Link("54", 0), ["channel"] = "red" });
            g["56"] = Node("ImageScale", new() { ["image"] = Link("8", 0), ["upscale_method"] = "lanczos", ["width"] = rw, ["height"] = rh, ["crop"] = "disabled" });
            ((JsonObject)g["11"]["inputs"])["pixels"] = Link("51", 0);
            ((JsonObject)g["13"]["inputs"])["mask"] = Link("55", 0);
            var c = (JsonObject)g["15"]["inputs"];
            c["source"] = Link("56", 0); c["x"] = rx; c["y"] = ry; c["mask"] = Link("52", 0);
            return new JsonObject { ["prompt"] = g, ["client_id"] = clientId };
        }, label: "Fixing");
    }

    static int Round64(double v) => Math.Max(64, (int)Math.Round(v / 64) * 64);

    // how much of the picture the box around the painted area covers (0 to 1)
    static double MaskBoxShare(byte[] mask, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x] > 32) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        return x1 < 0 ? 0 : (x1 - x0 + 1.0) * (y1 - y0 + 1) / ((double)w * h);
    }

    // Inpaint Crop & Stitch (plugin): a small painted area is cut out with its surroundings, redrawn at the model's own size
    // instead of a few pixels of the whole picture, and blended back in. Everything that read the photo or its mask reads the crop.
    void UseCropAndStitch(JsonObject g)
    {
        foreach (var (id, node) in g.ToList())
        {
            if (id is "14" or "15" || node?["inputs"] is not JsonObject inputs) continue;
            foreach (var (key, value) in inputs.ToList())
                if (value is JsonArray { Count: 2 } l && l[0]?.ToString() == "10")
                    inputs[key] = Link("60", l[1]!.GetValue<int>() == 0 ? 1 : 2);
        }
        g["60"] = CropNode(Link("10", 0), Link("10", 1), 1.5);
        g["61"] = Node("InpaintStitchImproved", new() { ["stitcher"] = Link("60", 0), ["inpainted_image"] = Link("8", 0) });
        g.Remove("14"); g.Remove("15");
        ((JsonObject)g["9"]["inputs"])["images"] = Link("61", 0);
    }

    // the masked area with `context` times its size around it, at 1024 × 1024. Outputs: stitcher, image, mask
    static JsonObject CropNode(JsonNode image, JsonNode mask, double context) => Node("InpaintCropImproved", new()
    {
        ["image"] = image, ["mask"] = mask,
        ["downscale_algorithm"] = "bilinear", ["upscale_algorithm"] = "bicubic", ["preresize"] = false, ["preresize_mode"] = "ensure minimum resolution",
        ["preresize_min_width"] = 1024, ["preresize_min_height"] = 1024, ["preresize_max_width"] = 8192, ["preresize_max_height"] = 8192,
        ["mask_fill_holes"] = true, ["mask_expand_pixels"] = 0, ["mask_invert"] = false, ["mask_blend_pixels"] = 32, ["mask_hipass_filter"] = 0.1,
        ["extend_for_outpainting"] = false, ["extend_up_factor"] = 1.0, ["extend_down_factor"] = 1.0, ["extend_left_factor"] = 1.0, ["extend_right_factor"] = 1.0,
        ["context_from_mask_extend_factor"] = context, ["output_resize_to_target_size"] = true, ["output_target_width"] = 1024, ["output_target_height"] = 1024,
        ["output_padding"] = "32", ["device_mode"] = "gpu (much faster)",
    });
}
