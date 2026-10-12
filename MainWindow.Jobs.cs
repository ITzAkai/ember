// Jobs: everything sent to ComfyUI (images, edits, expansions, upscales) goes through here.
// ComfyUI keeps its own queue, so jobs are sent at once and run in order; the app just tracks each one by its prompt id.
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace ImageForge;

class Job
{
    public string Id;                               // ComfyUI's prompt id, once sent
    public string Verb = "Painting · step";         // progress wording: "Painting · step 3 of 8"
    public byte[] Before;                           // saved next to each result for before/after
    public readonly List<Shot> Slots = new();       // placeholders still waiting for a result
    public readonly List<string> Uploads = new();   // its files in ComfyUI\input, removed when done
    public DateTime Started = DateTime.Now;
    public int Number;                              // which of the pictures asked for together it is: 0, 1, …
    public bool Running;
    public long LogStart;                           // where ComfyUI's log stood when it was sent, to read what it said about this job
    public JsonObject Body;                         // what was sent, to send again at the front of the queue
}

public partial class MainWindow
{
    readonly List<Job> jobs = new();
    bool busy => jobs.Count > 0;

    void Generate_Click(object sender, RoutedEventArgs e) => _ = Generate();

    // stops the image being made now; the queue carries on
    void Cancel_Click(object sender, RoutedEventArgs e) => _ = Stop(jobs.FirstOrDefault(j => j.Running));

    // by its prompt id, so one that finishes meanwhile doesn't take the next one down instead
    async Task Stop(Job job)
    {
        Stage("Stopping…", "");
        try { await http.PostAsync("/interrupt", job?.Id == null ? null : Json(new JsonObject { ["prompt_id"] = job.Id })); } catch { }
    }

    async void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        try { await http.PostAsync("/queue", new StringContent("{\"clear\": true}", Encoding.UTF8, "application/json")); } catch { }
        foreach (var j in jobs.Where(j => !j.Running).ToList()) Finish(j, null, quiet: true);
    }

    static StringContent Json(JsonNode n) => new(n.ToJsonString(), Encoding.UTF8, "application/json");

    // right-click on a picture still waiting or being made: cancel just that one, or let it go next
    void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Shot { Pending: true } s) return;
        e.Handled = true;                                    // the picture menu has nothing to act on yet
        if (jobs.FirstOrDefault(j => j.Slots.Contains(s)) is not { } job) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var cancel = new MenuItem { Header = job.Running ? "Stop this picture" : "Cancel this picture", Tag = "" };
        cancel.Click += (_, _) => { menu.IsOpen = false; _ = CancelJob(job); };     // closed here too: an item picked by a screen reader leaves it open
        menu.Items.Add(cancel);
        if (!job.Running && job.Id != null && jobs.FirstOrDefault(j => !j.Running && j.Id != null) != job)
        {
            var next = new MenuItem { Header = "Make it next", Tag = "" };
            next.Click += (_, _) => { menu.IsOpen = false; _ = MakeNext(job); };
            menu.Items.Add(next);
        }
        menu.IsOpen = true;
    }

    async Task CancelJob(Job job)
    {
        if (!job.Running && job.Id != null) try { await http.PostAsync("/queue", Json(new JsonObject { ["delete"] = new JsonArray(job.Id) })); } catch { }
        if (job.Running) { await Stop(job); return; }                // ComfyUI then reports it interrupted
        Finish(job, "Cancelled.");                                    // a job still being built is dropped before it's sent
    }

    // ComfyUI can't reorder its queue: the waiting prompt comes out and goes in again at the front
    async Task MakeNext(Job job)
    {
        try
        {
            await http.PostAsync("/queue", Json(new JsonObject { ["delete"] = new JsonArray(job.Id) }));
            var q = await http.GetStringAsync("/queue");
            if (job.Running || !jobs.Contains(job) || q.Contains(job.Id)) return;      // it started (or ended) meanwhile
            var body = (JsonObject)job.Body.DeepClone();
            body["front"] = true;
            using var r = await http.PostAsync("/prompt", Json(body));
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            if (!r.IsSuccessStatusCode || j?["error"] != null) throw new Exception(j?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {(int)r.StatusCode}");
            job.Id = j["prompt_id"].GetValue<string>();
            jobs.Remove(job);
            jobs.Insert(jobs.Count(x => x.Running), job);
            Note("It's next.");
        }
        catch (Exception e) { Finish(job, "Couldn't move it: " + e.Message); }
    }

    // the same prompt and seed on a few models, one picture each; every model uses its own saved settings
    void CompareModels_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var picks = imageModels.Where(m => m != ModelBox.SelectedItem as string)
            .Select(m => new MenuItem { Header = m, Tag = "", IsCheckable = true, StaysOpenOnClick = true, DataContext = m }).ToList();
        foreach (var p in picks) menu.Items.Add(p);
        menu.Items.Add(new Separator());
        var go = new MenuItem { Header = "Make one picture on each", Tag = "", FontWeight = FontWeights.SemiBold };
        go.Click += (_, _) => { menu.IsOpen = false; _ = CompareModels(picks.Where(p => p.IsChecked).Select(p => (string)p.DataContext).ToList()); };
        menu.Items.Add(go);
        menu.IsOpen = true;
    }

    async Task CompareModels(List<string> others)
    {
        if (others.Count == 0) { Note("Tick the models to compare with first.", true); return; }
        if (VideoOn || EditMode.IsChecked == true) { Note("Compare is for new pictures: switch to Create first.", true); return; }
        var first = (string)ModelBox.SelectedItem;
        bool random = RandomSeed.IsChecked == true;
        if (random || !long.TryParse(SeedBox.Text, out _)) SeedBox.Text = Random.Shared.NextInt64(0, 1L << 48).ToString();
        RandomSeed.IsChecked = false;
        try
        {
            // one after another: each picture's graph is built before the next model is picked
            foreach (var m in others.Prepend(first)) { ModelBox.SelectedItem = m; await Generate(one: true); }
        }
        finally { ModelBox.SelectedItem = first; RandomSeed.IsChecked = random; }
        Note($"Seed {SeedBox.Text} on {others.Count + 1} models: open a picture to see which model made it.");
    }

    async Task Generate(bool newSeed = false, double? expand = null, bool one = false)
    {
        if (!ready) return;
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) { Note("Write a prompt first.", true); PromptBox.Focus(); return; }
        var words = PromptBox.Text + "\n" + CoupleText;           // everything typed that goes into the picture, the two-people boxes too
        Remember(PromptBox.Text.Trim());
        if (ModelBox.SelectedItem == null) { Note(VideoOn ? "No video model yet: get Wan 2.1 on the Models page." : "No model selected.", true); return; }
        bool edit = EditMode.IsChecked == true;
        if (edit && photo == null) { Note("Choose a photo to edit first.", true); return; }
        if (edit && CropLayer.Visibility == Visibility.Visible) { Note("Apply or cancel the crop first.", true); return; }
        // a reference or style picture may show a real person, so it never goes into a nude or sexual picture
        if (!edit && UsingCouple && (CoupleLeftBox.Text.Trim() == "" || CoupleRightBox.Text.Trim() == ""))
        { Note("Describe both people, or turn off Two people.", true); return; }
        long seed = newSeed || RandomSeed.IsChecked == true || !long.TryParse(SeedBox.Text, out var typed) ? Random.Shared.NextInt64(0, 1L << 48) : typed;
        SeedBox.Text = seed.ToString();
        SaveSettings();
        if (VideoOn && expand == null) { await MakeVideo(seed); return; }
        int count = expand != null || one ? 1 : int.Parse(Picked(CountGroup));      // an expansion is one picture, not variations
        await Enqueue(count, async job =>
        {
            string image = null;
            bool masked = false, small = false;
            double? denoise = null;
            if (expand is double aspect)
            {
                (var canvas, job.Before) = ExpandCanvas(aspect);
                image = await Upload(canvas, job);
                // the inpainting model paints the new area from scratch; without it, Turbo at full strength
                // can leave the sides black, so it keeps a little of the carried-out edge colours
                (masked, denoise) = (true, inpaintModel != null && baseCheckpoint != null ? 1.0 : 0.9);
            }
            else if (edit)
            {
                var mask = MaskAlpha();
                image = await Upload(EditPng(mask), job);
                job.Before = EditPng(null);
                masked = mask != null;
                small = masked && MaskBoxShare(mask, photo.PixelWidth, photo.PixelHeight) < 0.5;
            }
            string[] refs = null, looks = [];
            if (expand == null && UsingReferences)
            {
                refs = new string[references.Count];
                for (int i = 0; i < refs.Length; i++) refs[i] = await Upload(references[i].Png, job);
                if (!Qwen) looks = await Task.WhenAll(references.Select(LooksOf));         // usually written already, when it was added
            }
            bool newPicture = !edit && expand == null;
            var style = newPicture && UsingStylePicture ? await Upload(stylePicture.Png, job) : null;
            bool couple = newPicture && UsingCouple;
            // picture n gets seed + n, so the seed saved with each one makes that same picture again
            var g = Graph(seed + job.Number, image, masked, denoise, count: 1, refs: refs, looks: string.Join(", ", looks.Where(t => !string.IsNullOrEmpty(t))));
            // expansions always use the inpainting model when installed (far better joins); painted edits if the setting is on
            if (masked && inpaintModel != null && baseCheckpoint != null && (expand != null || settings.InpaintEdits)) UseInpaintModel(g);
            if (small && nodeNames.Contains("InpaintCropImproved")) UseCropAndStitch(g);
            ApplyBoosters(g, newPicture);
            if (style != null) UseStylePicture(g, style);
            if (couple) UseCouple(g);
            return new JsonObject { ["prompt"] = g, ["client_id"] = clientId };
        });
    }

    // a job per picture, so each waits its own turn in ComfyUI's queue and is stopped, cancelled or moved on its own.
    // Placeholders straight away, then each job is built (and uploaded) and handed to ComfyUI, in order.
    async Task Enqueue(int images, Func<Job, Task<JsonObject>> build, string verb = "Painting · step", string label = null)
    {
        var queued = new List<Job>();
        for (int i = 0; i < images; i++)
        {
            var job = new Job { Verb = verb, Number = i };
            try { job.LogStart = File.Exists(ServerLog) ? new FileInfo(ServerLog).Length : 0; } catch { }
            var slot = new Shot { Pending = true, Progress = busy ? "Queued" : label ?? "Starting" };
            job.Slots.Add(slot);
            shots.Insert(0, slot);
            jobs.Add(job);
            queued.Add(job);
        }
        AllView.IsChecked = true;                    // new images land in "All"
        GalleryScroll.ScrollToTop();
        StripScroll.ScrollToLeftEnd();
        UpdateBusy();
        foreach (var job in queued)
        {
            if (!jobs.Contains(job)) continue;       // cancelled, or the queue cleared, before it was built
            try
            {
                var body = await build(job);
                // sharp live previews (TAESD) where they cost nothing measurable: Qwen's and Krea 2's slow steps hide the decode;
                // on anime models it's about 6% slower, on SDXL about 19%, so those keep the quick preview unless the setting asks
                if (body["extra_data"] is not JsonObject extra) body["extra_data"] = extra = new JsonObject();
                extra["preview_method"] ??= Qwen || Krea || settings.SharpPreviewAll ? "taesd" : "auto";
                if (body["prompt"] is JsonObject sent) lastGraph = (JsonObject)sent.DeepClone();
                if (!jobs.Contains(job)) { foreach (var f in job.Uploads) try { File.Delete(f); } catch { } continue; }    // cancelled while being built
                job.Body = body;
                using var r = await http.PostAsync("/prompt", Json(body));
                var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
                if (!r.IsSuccessStatusCode || j?["error"] != null) throw new Exception(j?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {(int)r.StatusCode}");
                job.Id = j["prompt_id"].GetValue<string>();
            }
            catch (Exception e) { Finish(job, "Couldn't start: " + e.Message); }
        }
    }

    // into ComfyUI's input/forge folder under a fresh name, so queued jobs never overwrite each other's photo
    async Task<string> Upload(byte[] png, Job job)
    {
        var stored = await Upload(png, $"forge_{Guid.NewGuid():N}.png");
        job.Uploads.Add(Path.Combine(Root, "ComfyUI", "input", stored.Replace('/', Path.DirectorySeparatorChar)));
        return stored;
    }

    void Finish(Job job, string error, bool quiet = false)
    {
        if (!jobs.Remove(job)) return;
        if (error == null && server != null && loraRows.Any(r => r.Box?.SelectedItem is string s && s != "None")) CheckLoraLog(job.LogStart);
        foreach (var slot in job.Slots) shots.Remove(slot);           // nothing came for these
        foreach (var f in job.Uploads) try { File.Delete(f); } catch { }
        if (!quiet)
        {
            if (error == null) { Note($"Finished in {(DateTime.Now - job.Started).TotalSeconds:0} s."); Flash(); }
            else if (error == "Cancelled.") Note(error);
            else { Note(error, true); Taskbar.ProgressState = TaskbarItemProgressState.Error; Taskbar.ProgressValue = 1; Flash(); }
        }
        UpdateBusy(keepErrorOnTaskbar: error != null && error != "Cancelled.");
    }

    void UpdateBusy(bool keepErrorOnTaskbar = false)
    {
        CancelBtn.Visibility = ProgressArea.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        int waiting = jobs.Count(j => !j.Running) - (jobs.Any(j => j.Running) ? 0 : 1);
        QueueRow.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueText.Text = waiting == 1 ? "1 more waiting" : $"{waiting} more waiting";
        if (busy) { SetState("Generating", Brush("Accent")); Note(""); }
        else
        {
            Bar.Value = 0;
            if (!keepErrorOnTaskbar) Taskbar.ProgressState = TaskbarItemProgressState.None;
            SetState("Ready", Brush("Ok"));
        }
        if (busy && !jobs.Any(j => j.Running)) { Stage("Starting…", ""); Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate; }
    }

    // ---------------- upscale ----------------
    string upscaleModel;                // first model in ComfyUI\models\upscale_models, if any

    void Upscale_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        if (upscaleModel == null) { ViewerNote.Text = "No upscale model in ComfyUI\\models\\upscale_models."; return; }
        var s = viewing;
        var size = ViewerImage.Source is BitmapSource b ? (b.PixelWidth, b.PixelHeight) : (1024, 1024);
        var menu = new ContextMenu { PlacementTarget = UpscaleBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var f in new[] { 2, 4 })
        {
            var item = new MenuItem { Header = $"{f}×   ({size.Item1 * f} × {size.Item2 * f})", Tag = "" };
            item.Click += (_, _) => _ = Upscale(s, f);
            menu.Items.Add(item);
        }
        bool canDetail = nodeNames.Contains("UltimateSDUpscale");
        var detail = new MenuItem
        {
            Header = canDetail ? $"2× with added detail   ({size.Item1 * 2} × {size.Item2 * 2})" : "2× with added detail: needs Ultimate SD Upscale (Settings > Plugins)",
            Tag = "", IsEnabled = canDetail && !VideoOn && ModelBox.SelectedItem != null,
            ToolTip = $"Enlarges, then redraws the picture in tiles with {ModelBox.SelectedItem} at low strength: sharper skin, hair and texture. Takes a few minutes.",
        };
        detail.Click += (_, _) => _ = DetailUpscale(s);
        menu.Items.Add(detail);
        menu.IsOpen = true;
    }

    // Ultimate SD Upscale: Real-ESRGAN to 2×, then the selected model redraws it tile by tile at low strength, with the picture's own prompt
    async Task DetailUpscale(Shot s)
    {
        if (!ready) { ViewerNote.Text = "ComfyUI isn't ready yet."; return; }
        var prompt = s.Meta?.Prompt ?? PromptBox.Text.Trim();
        CloseViewer();
        long seed = Random.Shared.NextInt64(0, 1L << 48);
        await Enqueue(1, async job =>
        {
            var source = await File.ReadAllBytesAsync(s.FilePath);
            job.Before = source;
            var image = await Upload(source, job);
            var g = Graph(seed, count: 1);                  // the model, its LoRAs and the prompt encoders, as for a new picture
            var k = g["3"]["inputs"].AsObject();
            if (g["6"]?["inputs"]?["text"] != null) g["6"]["inputs"]["text"] = prompt;
            var vae = g["8"]["inputs"]["vae"].DeepClone();
            g["20"] = Node("LoadImage", new() { ["image"] = image });
            g["21"] = Node("UpscaleModelLoader", new() { ["model_name"] = upscaleModel });
            g["22"] = Node("UltimateSDUpscale", new()
            {
                ["image"] = Link("20", 0), ["model"] = k["model"].DeepClone(), ["positive"] = k["positive"].DeepClone(), ["negative"] = k["negative"].DeepClone(),
                ["vae"] = vae, ["upscale_by"] = 2.0, ["seed"] = seed, ["steps"] = Math.Max(4, Steps / 2), ["cfg"] = k["cfg"].DeepClone(),
                ["sampler_name"] = k["sampler_name"].DeepClone(), ["scheduler"] = k["scheduler"].DeepClone(), ["denoise"] = 0.25,
                ["upscale_model"] = Link("21", 0), ["mode_type"] = "Linear", ["tile_width"] = 1024, ["tile_height"] = 1024, ["mask_blur"] = 8, ["tile_padding"] = 32,
                ["seam_fix_mode"] = "None", ["seam_fix_denoise"] = 1.0, ["seam_fix_width"] = 64, ["seam_fix_mask_blur"] = 8, ["seam_fix_padding"] = 16,
                ["force_uniform_tiles"] = true, ["tiled_decode"] = false, ["batch_size"] = 1,
            });
            foreach (var id in new[] { "3", "5", "8" }) g.Remove(id);
            g["9"] = Node("SaveImage", new() { ["filename_prefix"] = "forge/up", ["images"] = Link("22", 0) });
            var meta = (s.Meta ?? new Meta(null, null, null, null, null, null, null, null)) with { Upscaled = $"2× with added detail from {Path.GetFileName(s.FilePath)}" };
            return new JsonObject
            {
                ["prompt"] = g, ["client_id"] = clientId,
                ["extra_data"] = new JsonObject { ["extra_pnginfo"] = new JsonObject { ["forge"] = JsonSerializer.SerializeToNode(meta) } },
            };
        }, "Adding detail · tile", "Upscaling");
    }

    // Real-ESRGAN always makes 4×; for 2× it is scaled back down with Lanczos, which is sharper than upscaling 2× directly
    async Task Upscale(Shot s, int factor)
    {
        if (!ready) { ViewerNote.Text = "ComfyUI isn't ready yet."; return; }
        CloseViewer();
        await Enqueue(1, async job =>
        {
            var source = await File.ReadAllBytesAsync(s.FilePath);
            job.Before = source;                                // before/after: the image it came from
            var image = await Upload(source, job);
            var g = new JsonObject
            {
                ["20"] = Node("LoadImage", new() { ["image"] = image }),
                ["21"] = Node("UpscaleModelLoader", new() { ["model_name"] = upscaleModel }),
                ["22"] = Node("ImageUpscaleWithModel", new() { ["upscale_model"] = Link("21", 0), ["image"] = Link("20", 0) }),
                ["9"] = Node("SaveImage", new() { ["filename_prefix"] = "forge/up", ["images"] = Link(factor == 4 ? "22" : "23", 0) }),
            };
            if (factor == 2) g["23"] = Node("ImageScaleBy", new() { ["image"] = Link("22", 0), ["upscale_method"] = "lanczos", ["scale_by"] = 0.5 });
            var meta = (s.Meta ?? new Meta(null, null, null, null, null, null, null, null)) with { Upscaled = $"{factor}× from {Path.GetFileName(s.FilePath)}" };
            return new JsonObject
            {
                ["prompt"] = g, ["client_id"] = clientId,
                ["extra_data"] = new JsonObject { ["extra_pnginfo"] = new JsonObject { ["forge"] = JsonSerializer.SerializeToNode(meta) } },
            };
        }, "Upscaling · tile", "Upscaling");
    }

    // ---------------- live progress ----------------
    // ComfyUI's websocket; reconnects if ComfyUI restarts
    async Task Listen()
    {
        while (!closing)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/ws?clientId={clientId}"), CancellationToken.None);
                if (!busy) SetState("Ready", Brush("Ok"));
                RestartPanel.Visibility = Visibility.Collapsed;
                var buf = new byte[1 << 16];
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult res;
                    do { res = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, res.Count); } while (!res.EndOfMessage);
                    if (res.MessageType == WebSocketMessageType.Text) OnMessage(Encoding.UTF8.GetString(ms.ToArray()));
                    else if (res.MessageType == WebSocketMessageType.Binary) OnPreview(ms.ToArray());
                }
            }
            catch { }
            if (closing) return;
            if (!installingPlugins && !startingServer) Offline(null);      // installing and restarting stop ComfyUI on purpose
            foreach (var j in jobs.ToList()) Finish(j, "Lost the connection to ComfyUI.");
            await Task.Delay(2000);
        }
    }

    // ComfyUI's live preview of the picture being made (event 1: a 4-byte image type, then a JPEG or PNG), shown on the job's card
    void OnPreview(byte[] data)
    {
        if (data.Length < 9 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) != 1) return;
        if (jobs.FirstOrDefault(j => j.Running)?.Slots.FirstOrDefault() is not { } slot) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(data, 8, data.Length - 8);
            img.EndInit();
            img.Freeze();
            slot.Preview = img;
        }
        catch { /* a frame that doesn't decode: the next one will */ }
    }

    void OnMessage(string text)
    {
        var m = JsonNode.Parse(text);
        var type = m?["type"]?.GetValue<string>();
        var data = m?["data"];
        var id = data?["prompt_id"]?.GetValue<string>();
        var job = id == null ? null : jobs.FirstOrDefault(j => j.Id == id);
        if (job == null) return;
        switch (type)
        {
            case "execution_start":
                job.Running = true;
                job.Started = DateTime.Now;
                UpdateBusy();
                break;
            case "executing" when data["node"] is JsonNode node:
                job.Running = true;
                if (Stages.TryGetValue(node.GetValue<string>(), out var stage))
                {
                    Stage(stage, "");
                    foreach (var s in job.Slots) s.Progress = stage;
                }
                stepFrom = -1;
                break;
            case "progress":
                int v = data["value"].GetValue<int>(), max = data["max"].GetValue<int>();
                Bar.Maximum = max; Bar.Value = Math.Min(v, max);
                if (stepFrom < 0 || v <= stepFrom) { stepFrom = v; stepTime = DateTime.Now; }
                double left = v > stepFrom ? (DateTime.Now - stepTime).TotalSeconds / (v - stepFrom) * (max - v) : -1;
                Stage($"{job.Verb} {v} of {max}", left >= 0 ? $"about {Math.Ceiling(left)} s left" : "");
                foreach (var s in job.Slots) { s.Progress = $"{v * 100 / max}%"; s.ProgressValue = (double)v / max; }
                Taskbar.ProgressState = TaskbarItemProgressState.Normal;
                Taskbar.ProgressValue = (double)v / max;
                break;
            case "executed" when (data["output"]?["images"]) is JsonArray imgs:
                foreach (var im in imgs)
                    if (im["type"]?.GetValue<string>() == "output")
                        Arrive(Path.Combine(Output, im["subfolder"]?.GetValue<string>() ?? "", im["filename"].GetValue<string>()), job);
                break;
            case "executing":           // node null: the whole prompt is finished
            case "execution_success":
                Finish(job, null); break;
            case "execution_error":
                Finish(job, "Error: " + (data["exception_message"]?.GetValue<string>() ?? "see comfy_server.log")); break;
            case "execution_interrupted":
                Finish(job, "Cancelled."); break;
        }
    }

    void ShowNewest() { GalleryScroll.ScrollToTop(); StripScroll.ScrollToLeftEnd(); }

    // a finished image takes the place of its job's next placeholder
    void Arrive(string path, Job job)
    {
        if (shots.FirstOrDefault(s => s.FilePath == path) is { } same)
        {
            if (File.GetLastWriteTimeUtc(path) <= same.Made) return;     // a cached result ComfyUI reports again: already shown
            shots.Remove(same);                                          // a new picture under a reused name (after deletes)
        }
        if (job.Before != null)
            try { Directory.CreateDirectory(Path.GetDirectoryName(OriginalOf(path))); File.WriteAllBytes(OriginalOf(path), job.Before); }
            catch { /* before/after is a nicety */ }
        var shot = MakeShot(path);
        if (shot == null) return;
        shot.Selecting = selectMode;
        if (job.Slots.Count > 0)
        {
            var slot = job.Slots[0];
            job.Slots.RemoveAt(0);
            int i = shots.IndexOf(slot);
            if (LivePanel.DataContext == slot) { CloseLive(); OpenViewer(shot); }      // watching it being made: show the result
            if (i >= 0) { shots[i] = shot; ShowNewest(); return; }
        }
        shots.Insert(0, shot);
        ShowNewest();
    }
}
