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
    public bool Running;
    public long LogStart;                           // where ComfyUI's log stood when it was sent, to read what it said about this job
}

public partial class MainWindow
{
    readonly List<Job> jobs = new();
    bool busy => jobs.Count > 0;

    void Generate_Click(object sender, RoutedEventArgs e) => _ = Generate();

    // stops the image being made now; the queue carries on
    async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Stage("Stopping…", "");
        try { await http.PostAsync("/interrupt", null); } catch { }
    }

    async void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        try { await http.PostAsync("/queue", new StringContent("{\"clear\": true}", Encoding.UTF8, "application/json")); } catch { }
        foreach (var j in jobs.Where(j => !j.Running).ToList()) Finish(j, null, quiet: true);
    }

    async Task Generate(bool newSeed = false, double? expand = null)
    {
        if (!ready) return;
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) { Note("Write a prompt first.", true); PromptBox.Focus(); return; }
        if (SexualWithMinor(PromptBox.Text)) { Note(MinorNote, true); return; }       // the uncensored models won't stop it, so the app does
        Remember(PromptBox.Text.Trim());
        if (ModelBox.SelectedItem == null) { Note(VideoOn ? "No video model yet: get Wan 2.1 on the Models page." : "No model selected.", true); return; }
        bool edit = EditMode.IsChecked == true;
        if (edit && photo == null) { Note("Choose a photo to edit first.", true); return; }
        if (edit && CropLayer.Visibility == Visibility.Visible) { Note("Apply or cancel the crop first.", true); return; }
        // a reference picture may show a real person, so it never goes into a nude or sexual picture
        if (UsingReferences && SexualWords.IsMatch(PromptBox.Text)) { Note("Reference pictures can't be used for nude or sexual pictures: they may show a real person.", true); return; }
        long seed = newSeed || RandomSeed.IsChecked == true || !long.TryParse(SeedBox.Text, out var typed) ? Random.Shared.NextInt64(0, 1L << 48) : typed;
        SeedBox.Text = seed.ToString();
        SaveSettings();
        if (VideoOn && expand == null) { await MakeVideo(seed); return; }
        int count = expand != null ? 1 : int.Parse(Picked(CountGroup));      // an expansion is one picture, not variations
        await Enqueue(count, async job =>
        {
            string image = null;
            bool masked = false;
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
            }
            string[] refs = null;
            if (expand == null && UsingReferences)
            {
                refs = new string[references.Count];
                for (int i = 0; i < refs.Length; i++) refs[i] = await Upload(references[i].Png, job);
            }
            var g = Graph(seed, image, masked, denoise, count: count, refs: refs);
            // expansions always use the inpainting model when installed (far better joins); painted edits if the setting is on
            if (masked && inpaintModel != null && baseCheckpoint != null && (expand != null || settings.InpaintEdits)) UseInpaintModel(g);
            ApplyBoosters(g, newPicture: !edit && expand == null);
            return new JsonObject { ["prompt"] = g, ["client_id"] = clientId };
        });
    }

    // placeholders straight away, then build (and upload) the job, then hand it to ComfyUI
    async Task Enqueue(int images, Func<Job, Task<JsonObject>> build, string verb = "Painting · step", string label = null)
    {
        var job = new Job { Verb = verb };
        try { job.LogStart = File.Exists(ServerLog) ? new FileInfo(ServerLog).Length : 0; } catch { }
        for (int i = 0; i < images; i++)
        {
            var slot = new Shot { Pending = true, Progress = busy ? "Queued" : label ?? "Starting" };
            job.Slots.Add(slot);
            shots.Insert(0, slot);
        }
        jobs.Add(job);
        AllView.IsChecked = true;                    // new images land in "All"
        GalleryScroll.ScrollToTop();
        StripScroll.ScrollToLeftEnd();
        UpdateBusy();
        try
        {
            var body = await build(job);
            using var r = await http.PostAsync("/prompt", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            if (!r.IsSuccessStatusCode || j?["error"] != null) throw new Exception(j?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {(int)r.StatusCode}");
            job.Id = j["prompt_id"].GetValue<string>();
        }
        catch (Exception e) { Finish(job, "Couldn't start: " + e.Message); }
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
        menu.IsOpen = true;
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
                }
            }
            catch { }
            if (closing) return;
            if (!installingPlugins && !startingServer) Offline(null);      // installing and restarting stop ComfyUI on purpose
            foreach (var j in jobs.ToList()) Finish(j, "Lost the connection to ComfyUI.");
            await Task.Delay(2000);
        }
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
                foreach (var s in job.Slots) s.Progress = $"{v * 100 / max}%";
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
            if (i >= 0) { shots[i] = shot; ShowNewest(); return; }
        }
        shots.Insert(0, shot);
        ShowNewest();
    }
}
