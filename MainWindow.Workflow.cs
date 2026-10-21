// Run any ComfyUI workflow from Create mode: a workflow saved with Workflow > Export (API), or a picture ComfyUI made (it carries its
// workflow). Generate then runs it instead of Ember's own graph, with the Prompt and Negative boxes in its prompt text, a new seed for
// each picture, and its pictures saved into the gallery. The editor's own "Save" format needs ComfyUI's converter, so it's asked for again.
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace ImageForge;

public partial class MainWindow
{
    JsonObject workflow;            // the workflow in API format: node id -> { class_type, inputs }
    string workflowName;

    bool UsingWorkflow => workflow != null && CreateMode.IsChecked == true && !VideoOn;

    void WorkflowAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a ComfyUI workflow", Filter = "ComfyUI workflows or pictures|*.json;*.png" };
        if (dlg.ShowDialog(this) == true) LoadWorkflow(dlg.FileName);
    }

    void Workflow_DragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    void Workflow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadWorkflow(files[0]);
        e.Handled = true;
    }

    void WorkflowRemove_Click(object sender, RoutedEventArgs e) { workflow = null; workflowName = null; ShowWorkflow(); }

    // a copied workflow file or picture (Explorer), or the workflow's text itself
    void WorkflowPaste_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files) LoadWorkflow(files[0]);
        else if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith('{')) LoadWorkflow(null, Clipboard.GetText(), "Pasted workflow");
        else WorkflowNote.Text = "Nothing to paste: copy a workflow file or picture in Explorer, or a workflow's text.";
    }

    void LoadWorkflow(string path, string text = null, string name = null)
    {
        try
        {
            text ??= path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? PngText(path, "prompt") : File.ReadAllText(path);
            if (text == null) { WorkflowNote.Text = "This picture has no ComfyUI workflow saved in it."; return; }
            var json = JsonNode.Parse(text) as JsonObject;
            if (json?["nodes"] is JsonArray && json["links"] != null)
            { WorkflowNote.Text = "This is ComfyUI's editor format. Open it in ComfyUI and save it again with Workflow > Export (API), then choose that file."; return; }
            if (json == null || json.Count == 0 || json.Any(kv => kv.Value?["class_type"] == null))
            { WorkflowNote.Text = "That isn't a ComfyUI workflow: choose one saved with Workflow > Export (API), or a picture made by ComfyUI."; return; }
            (workflow, workflowName) = (json, name ?? Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) { WorkflowNote.Text = "Couldn't read it: " + ex.Message; return; }
        ShowWorkflow();
    }

    void ShowWorkflow()
    {
        if (WorkflowSection == null) return;
        WorkflowRow.Visibility = workflow != null ? Visibility.Visible : Visibility.Collapsed;
        WorkflowAddRow.Visibility = workflow == null ? Visibility.Visible : Visibility.Collapsed;
        if (workflow == null) { WorkflowNote.Text = "Run a ComfyUI workflow with your prompt: one saved with Workflow > Export (API), or a picture made by ComfyUI."; return; }
        WorkflowName.Text = workflowName;
        var (pos, neg) = PromptNodes(workflow);
        WorkflowNote.Text = (WorkflowProblem() ?? "Generate runs this workflow instead of the model above. " +
            (pos == null ? "It has no prompt box Ember could find, so it runs as it is." : "The Prompt box goes into its prompt" + (neg != null ? ", the Negative box into its negative." : ".")) +
            " Its pictures arrive in the gallery.");
    }

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

    // a tEXt chunk of a PNG (ComfyUI keeps its workflow under "prompt")
    static string PngText(string path, string key)
    {
        using var fs = File.OpenRead(path);
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
