// Run any ComfyUI workflow from Create mode. Workflows live in ComfyUI's own workflows folder (so ones saved in the node editor show
// too) and are picked from a list, like LoRAs; new ones come from a link (Civitai, Hugging Face, GitHub, or straight to a .json, .zip
// or .png), a file, or the clipboard. Generate then runs the picked one instead of Ember's own graph, with the Prompt and Negative
// boxes in its prompt text, a new seed for each picture, and its pictures saved into the gallery.
// ComfyUI's editor format is turned into the runnable (API) one by ComfyUI's own editor, in a hidden WebView2: its graphToPrompt is
// what ComfyUI runs when you press Run, so grouped nodes, bypassed nodes, reroutes and primitives come out the way ComfyUI makes them.
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ImageForge;

public partial class MainWindow
{
    JsonObject workflow;            // the picked workflow in API format: node id -> { class_type, inputs }
    string workflowName;            // its name in the list (its path under the workflows folder, without .json)
    bool fillingWorkflows;
    WebView2 converterView;

    string WorkflowsDir => Path.Combine(Root, "ComfyUI", "user", "default", "workflows");
    bool UsingWorkflow => workflow != null && CreateMode.IsChecked == true && !VideoOn;

    // ---------------- the list ----------------
    void FillWorkflows(string select = null)
    {
        if (WorkflowBox == null) return;
        fillingWorkflows = true;
        var names = Directory.Exists(WorkflowsDir)
            ? Directory.GetFiles(WorkflowsDir, "*.json", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(WorkflowsDir, f)[..^5])
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        WorkflowBox.ItemsSource = new[] { "None" }.Concat(names).ToList();
        var want = select ?? workflowName;
        WorkflowBox.SelectedItem = want != null && names.Contains(want) ? want : "None";
        fillingWorkflows = false;
        if (select != null) _ = PickWorkflow(select);
        else ShowWorkflow();
    }

    void WorkflowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!fillingWorkflows) _ = PickWorkflow(WorkflowBox.SelectedItem as string);
    }

    async Task PickWorkflow(string name)
    {
        workflow = null;
        workflowName = name is null or "None" ? null : name;
        if (workflowName == null) { ShowWorkflow(); return; }
        WorkflowNote.Text = "Reading it…";
        try
        {
            var text = await File.ReadAllTextAsync(Path.Combine(WorkflowsDir, name + ".json"));
            var json = JsonNode.Parse(text) as JsonObject;
            if (json?["nodes"] is JsonArray)
            {
                WorkflowNote.Text = "Turning it into one Ember can run, with ComfyUI's own editor…";
                json = await ConvertWorkflow(text);
            }
            if (!IsApiWorkflow(json)) throw new Exception("it isn't a ComfyUI workflow.");
            if (workflowName != name) return;                  // another one was picked meanwhile
            workflow = json;
        }
        catch (Exception ex) { if (workflowName == name) WorkflowNote.Text = "Couldn't use it: " + ex.Message; return; }
        ShowWorkflow();
    }

    // the runnable (API) format: every entry a node with a class_type (the editor format has lists, like "nodes" and "links")
    static bool IsApiWorkflow(JsonObject json) => json is { Count: > 0 } && json.All(kv => kv.Value is JsonObject o && o["class_type"] != null);

    void ShowWorkflow()
    {
        if (WorkflowSection == null) return;
        if (workflow == null)
        {
            if (workflowName == null) WorkflowNote.Text = "None: Ember's own models. Paste a link to a ComfyUI workflow and press Get, or add a file, then pick it here.";
            return;
        }
        var (pos, neg) = PromptNodes(workflow);
        WorkflowNote.Text = WorkflowProblem() ?? "Generate runs this workflow instead of the model above. " +
            (pos == null ? "It has no prompt box Ember could find, so it runs as it is." : "The Prompt box goes into its prompt" + (neg != null ? ", the Negative box into its negative." : ".")) +
            " Its pictures arrive in the gallery.";
    }

    // ---------------- adding one: a link, a file, the clipboard ----------------
    async void WorkflowGet_Click(object sender, RoutedEventArgs e)
    {
        var link = WorkflowLinkBox.Text.Trim();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) { WorkflowNote.Text = "Paste the whole link, starting with https://"; return; }
        WorkflowGetBtn.IsEnabled = false;
        WorkflowNote.Text = "Downloading the workflow…";
        try
        {
            var (url, name) = await WorkflowDownload(uri);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsCivitai(new Uri(url)) && CivitaiKey is string key) req.Headers.Authorization = new("Bearer", key);
            using var res = await web.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new Exception(CivitaiKey == null ? "the site wants a sign-in: for Civitai, add your API key in Settings, under Downloads." : "the site didn't let it be downloaded.");
            res.EnsureSuccessStatusCode();
            if (res.Content.Headers.ContentLength > 50_000_000) throw new Exception("that file is too big to be a workflow.");
            var bytes = await res.Content.ReadAsByteArrayAsync();
            name ??= res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Path.GetFileName(uri.AbsolutePath);
            var saved = SaveWorkflows(bytes, name);
            WorkflowLinkBox.Text = "";
            FillWorkflows(saved);
        }
        catch (Exception ex) { WorkflowNote.Text = "Couldn't get it: " + ex.Message; }
        finally { WorkflowGetBtn.IsEnabled = true; }
    }

    // where the file behind a link is: Civitai pages through Civitai's API, Hugging Face and GitHub pages to their raw file
    async Task<(string Url, string Name)> WorkflowDownload(Uri uri)
    {
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (IsCivitai(uri))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string version = query["modelVersionId"];
            if (parts is ["api", "download", "models", var v, ..]) return (uri.ToString(), null);
            JsonNode ver;
            string title = null;
            if (version != null) ver = JsonNode.Parse(await web.GetStringAsync($"https://{uri.Host}/api/v1/model-versions/{version}"));
            else if (parts is ["models", var id, ..])
            {
                var model = JsonNode.Parse(await web.GetStringAsync($"https://{uri.Host}/api/v1/models/{id}"));
                title = model?["name"]?.ToString();
                ver = model?["modelVersions"]?[0];
            }
            else throw new Exception("that isn't a Civitai page.");
            title ??= ver?["model"]?["name"]?.ToString();
            var file = (ver?["files"] as JsonArray)?.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? (ver?["files"] as JsonArray)?.FirstOrDefault();
            if (file?["downloadUrl"]?.ToString() is not string url) throw new Exception("Civitai didn't list a file for it.");
            return (url, title is null ? file["name"]?.ToString() : title + Path.GetExtension(file["name"]?.ToString() ?? ".json"));
        }
        if (uri.Host is "huggingface.co" or "www.huggingface.co" && parts.Length >= 5 && parts[2] == "blob")
            return ($"https://huggingface.co/{parts[0]}/{parts[1]}/resolve/{string.Join('/', parts[3..])}", null);
        if (uri.Host is "github.com" or "www.github.com" && parts.Length >= 5 && parts[2] == "blob")
            return ($"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/{string.Join('/', parts[3..])}", null);
        return (uri.ToString(), null);
    }

    void WorkflowAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a ComfyUI workflow", Filter = "ComfyUI workflows, pictures or zips|*.json;*.png;*.zip" };
        if (dlg.ShowDialog(this) == true) AddWorkflowFile(dlg.FileName);
    }

    void Workflow_DragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    void Workflow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddWorkflowFile(files[0]);
        e.Handled = true;
    }

    // a copied workflow file or picture (Explorer), or the workflow's text itself
    void WorkflowPaste_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files) AddWorkflowFile(files[0]);
        else if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith('{'))
        {
            try { FillWorkflows(SaveWorkflows(Encoding.UTF8.GetBytes(Clipboard.GetText()), "Pasted workflow.json")); }
            catch (Exception ex) { WorkflowNote.Text = "Couldn't use it: " + ex.Message; }
        }
        else WorkflowNote.Text = "Nothing to paste: copy a workflow file or picture in Explorer, or a workflow's text.";
    }

    void AddWorkflowFile(string path)
    {
        try { FillWorkflows(SaveWorkflows(File.ReadAllBytes(path), Path.GetFileName(path))); }
        catch (Exception ex) { WorkflowNote.Text = "Couldn't use it: " + ex.Message; }
    }

    // saves the workflow(s) in a downloaded or chosen file into the workflows folder: a .json, a picture ComfyUI made (its workflow
    // is inside), or a .zip of them. Returns the name of the first one, to pick.
    string SaveWorkflows(byte[] bytes, string name)
    {
        var found = new List<(string Name, string Json)>();
        void Add(byte[] data, string n)
        {
            string text = data.Length > 8 && data[0] == 0x89 && data[1] == 'P' ? PngText(new MemoryStream(data), "workflow") ?? PngText(new MemoryStream(data), "prompt")
                : n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || data.Length > 0 && data[0] is (byte)'{' or 0xEF ? Encoding.UTF8.GetString(data).TrimStart('﻿') : null;
            if (text == null) return;
            var json = JsonNode.Parse(text) as JsonObject;
            bool editor = json?["nodes"] is JsonArray, api = !editor && IsApiWorkflow(json);
            if (editor || api) found.Add((Path.GetFileNameWithoutExtension(n), text));
        }
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            foreach (var entry in zip.Entries.Where(en => en.Length is > 0 and < 50_000_000 && Path.GetExtension(en.Name).ToLowerInvariant() is ".json" or ".png"))
            {
                using var s = entry.Open(); var ms = new MemoryStream(); s.CopyTo(ms);
                try { Add(ms.ToArray(), entry.Name); } catch { /* not a workflow: skipped */ }
            }
        }
        else Add(bytes, name);
        if (found.Count == 0) throw new Exception("there's no ComfyUI workflow in it.");
        Directory.CreateDirectory(WorkflowsDir);
        string first = null;
        foreach (var (n, json) in found)
        {
            var clean = Regex.Replace(n, @"[\\/:*?""<>|]", "_").Trim();
            var unique = clean;
            for (int i = 2; File.Exists(Path.Combine(WorkflowsDir, unique + ".json")) && File.ReadAllText(Path.Combine(WorkflowsDir, unique + ".json")) != json; i++) unique = $"{clean} ({i})";
            File.WriteAllText(Path.Combine(WorkflowsDir, unique + ".json"), json);
            first ??= unique;
        }
        return first;
    }

    // ---------------- ComfyUI's editor format -> its runnable (API) format ----------------
    async Task<JsonObject> ConvertWorkflow(string editorJson)
    {
        if (converterView == null)
        {
            var view = new WebView2();
            ConverterHost.Child = view;
            await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetDirectoryName(Settings.FilePath), "WebView2")));
            var loaded = new TaskCompletionSource<bool>();
            view.CoreWebView2.NavigationCompleted += (_, a) => loaded.TrySetResult(a.IsSuccess);
            view.CoreWebView2.Navigate(Api);
            if (!await loaded.Task) { ConverterHost.Child = null; throw new Exception("ComfyUI isn't answering."); }
            converterView = view;
        }
        for (int i = 0; i < 120 && (await Eval("!!(window.app && window.app.graph && window.app.graphToPrompt)"))?.GetValue<bool>() != true; i++) await Task.Delay(500);
        var result = await Eval($"(async () => {{ await window.app.loadGraphData({editorJson}, true, false); const p = await window.app.graphToPrompt(); return JSON.stringify(p.output); }})()")
            ?? throw new Exception("ComfyUI's editor gave nothing back.");
        return JsonNode.Parse(result.GetValue<string>()) as JsonObject;
    }

    // JavaScript in the hidden editor, waiting for a promise (ExecuteScriptAsync doesn't)
    async Task<JsonNode> Eval(string expression)
    {
        var res = JsonNode.Parse(await converterView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true }.ToJsonString()));
        if (res?["exceptionDetails"] is JsonNode ex) throw new Exception(ex["exception"]?["description"]?.ToString()?.Split('\n')[0] ?? "the editor couldn't read it.");
        return res?["result"]?["value"];
    }

    // ---------------- running it ----------------
    // what keeps it from running here: plugins that aren't loaded, or input pictures that aren't there
    string WorkflowProblem()
    {
        var missing = workflow.Select(kv => kv.Value?["class_type"]?.ToString()).Where(c => c != null && nodeNames.Count > 0 && !nodeNames.Contains(c)).Distinct().ToList();
        if (missing.Count > 0) return $"It needs nodes this ComfyUI doesn't have: {string.Join(", ", missing)}. Install their plugin (Settings > Plugins, or ComfyUI Manager).";
        var pictures = workflow.Where(kv => kv.Value?["class_type"]?.ToString() == "LoadImage").Select(kv => kv.Value["inputs"]?["image"]?.ToString()).Where(n => n != null)
            .Where(n => !File.Exists(n.EndsWith(" [output]") ? Path.Combine(Output, n[..^9]) : Path.Combine(Root, "ComfyUI", "input", n.Replace(" [input]", "")))).ToList();
        return pictures.Count > 0 ? $"It loads pictures that aren't in ComfyUI's input folder: {string.Join(", ", pictures)}." : null;
    }

    // the text boxes the samplers' positive and negative come from: followed back through conditioning nodes to the first one with text
    static (JsonObject Pos, JsonObject Neg) PromptNodes(JsonObject g)
    {
        JsonObject Text(JsonNode link)
        {
            var seen = new HashSet<string>();
            var queue = new Queue<JsonNode>(); queue.Enqueue(link);
            while (queue.Count > 0)
            {
                if (queue.Dequeue() is not JsonArray { Count: 2 } l || l[0]?.ToString() is not string id || !seen.Add(id) || g[id]?["inputs"] is not JsonObject inputs) continue;
                if (TextKey(inputs) != null) return inputs;
                foreach (var (_, v) in inputs) if (v is JsonArray) queue.Enqueue(v);
            }
            return null;
        }
        foreach (var (_, node) in g)
            if (node?["inputs"] is JsonObject i && i["positive"] is JsonArray p)
            {
                var pos = Text(p);
                var neg = i["negative"] is JsonArray n ? Text(n) : null;
                return (pos, neg == pos ? null : neg);
            }
        var first = g.Select(kv => kv.Value?["inputs"] as JsonObject).FirstOrDefault(i => i != null && TextKey(i) != null);
        return (first, null);
    }

    // a prompt typed into the node itself (not linked from another node)
    static string TextKey(JsonObject inputs) =>
        new[] { "text", "prompt", "text_g", "value" }.FirstOrDefault(k => inputs[k] is JsonValue v && v.TryGetValue<string>(out _));

    // the under-18 block covers every word that goes into the picture, not only the boxes (all its texts but the negative, which
    // lists what to keep out, as Ember's own check leaves it out too; file names aside); and a workflow that loads pictures is held
    // to the reference-picture rule: they may show a real person, so never with a nude or sexual prompt
    string WorkflowBlocked(string prompt, string negative)
    {
        var g = WorkflowGraph(0, prompt, negative);
        var neg = PromptNodes(g).Neg;
        var words = string.Join("\n", g.Select(kv => kv.Value?["inputs"] as JsonObject).Where(i => i != null && i != neg)
            .SelectMany(i => i.Select(p => p.Value)).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s != null && !Regex.IsMatch(s, @"\.(safetensors|gguf|ckpt|pth?|onnx|png|jpe?g|webp|mp4)(\s*\[\w+\])?$", RegexOptions.IgnoreCase)));
        if (SexualWithMinor(words)) return MinorNote;
        if (g.Any(kv => kv.Value?["class_type"]?.ToString() == "LoadImage") && SexualWords.IsMatch(words))
            return "A workflow that loads pictures can't be used for nude or sexual pictures: they may show a real person.";
        return null;
    }

    JsonObject WorkflowGraph(long seed, string prompt, string negative)
    {
        var g = (JsonObject)workflow.DeepClone();
        var (pos, neg) = PromptNodes(g);
        if (pos != null && prompt.Trim() != "") pos[TextKey(pos)] = prompt.Trim();
        if (neg != null && negative.Trim() != "") neg[TextKey(neg)] = negative.Trim();
        foreach (var (_, node) in g)
        {
            if (node?["inputs"] is not JsonObject i) continue;
            foreach (var key in new[] { "seed", "noise_seed" })
                if (i[key] is JsonValue v && v.TryGetValue<long>(out _)) i[key] = seed;
            if (node["class_type"]?.ToString() == "SaveImage") i["filename_prefix"] = "forge/wf";     // into the gallery
        }
        return g;
    }

    // --selftest part: the prompt boxes are found through a guidance node and a linked text node, and the negative is told from the positive
    static bool SelfTestWorkflow()
    {
        var g = JsonNode.Parse("""
            { "1": { "class_type": "PrimitiveStringMultiline", "inputs": { "value": "a cat" } },
              "2": { "class_type": "CLIPTextEncode", "inputs": { "text": ["1", 0], "clip": ["9", 0] } },
              "3": { "class_type": "FluxGuidance", "inputs": { "conditioning": ["2", 0], "guidance": 3.5 } },
              "4": { "class_type": "CLIPTextEncode", "inputs": { "text": "blurry", "clip": ["9", 0] } },
              "5": { "class_type": "KSampler", "inputs": { "positive": ["3", 0], "negative": ["4", 0], "seed": 1 } } }
            """).AsObject();
        var (pos, neg) = PromptNodes(g);
        return pos == g["1"]["inputs"] && TextKey(pos) == "value" && neg == g["4"]["inputs"] && TextKey(neg) == "text";
    }

    // a tEXt chunk of a PNG (ComfyUI keeps its workflow under "workflow", and the runnable one under "prompt")
    static string PngText(Stream fs, string key)
    {
        var head = new byte[8];
        fs.ReadExactly(head);
        while (fs.Position < fs.Length)
        {
            fs.ReadExactly(head);
            int len = BinaryPrimitives.ReadInt32BigEndian(head);
            var kind = Encoding.ASCII.GetString(head, 4, 4);
            if (kind is "IDAT" or "IEND") return null;
            if (kind != "tEXt") { fs.Seek(len + 4, SeekOrigin.Current); continue; }
            var chunk = new byte[len]; fs.ReadExactly(chunk); fs.Seek(4, SeekOrigin.Current);
            var t = Encoding.UTF8.GetString(chunk);
            if (t.StartsWith(key + "\0")) return t[(key.Length + 1)..];
        }
        return null;
    }
}
