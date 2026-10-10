// What each LoRA does: its name, a short description and its trigger words, looked up on Civitai by the file's SHA-256
// (only the hash is sent), or read from the file's own training metadata when Civitai doesn't know it.
// Kept in lora_info.json next to the settings, so a file is looked up once (again only if it changes).
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace ImageForge;

public record LoraInfo(string Name, string Description, string[] Trigger, string Url, long Size, DateTime Modified);

public partial class MainWindow
{
    static string LoraInfoFile => Path.Combine(Path.GetDirectoryName(Settings.FilePath), "lora_info.json");
    Dictionary<string, LoraInfo> loraInfo = new(StringComparer.OrdinalIgnoreCase);
    bool lookingUpLoras;

    // after the LoRA list loads: cached answers right away, then the new or changed files in the background
    async Task LoadLoraInfo()
    {
        if (lookingUpLoras) return;
        lookingUpLoras = true;
        try
        {
            try { loraInfo = JsonSerializer.Deserialize<Dictionary<string, LoraInfo>>(File.ReadAllText(LoraInfoFile)) is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : loraInfo; }
            catch { /* none yet */ }
            ShowLoraInfo();
            foreach (var lora in loras.ToList())
            {
                var path = Path.Combine(ModelsDir, "loras", lora);
                if (!File.Exists(path)) continue;
                var fi = new FileInfo(path);
                if (loraInfo.TryGetValue(lora, out var known) && known.Size == fi.Length && known.Modified == fi.LastWriteTimeUtc) continue;
                loraInfo[lora] = await LookUp(path, fi);
                try { File.WriteAllText(LoraInfoFile, JsonSerializer.Serialize(loraInfo)); } catch { }
                ShowLoraInfo();
            }
        }
        finally { lookingUpLoras = false; }
    }

    async Task<LoraInfo> LookUp(string path, FileInfo fi)
    {
        var local = await Task.Run(() => FromMetadata(path, fi));
        try
        {
            var sha = await Task.Run(() => { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); });
            foreach (var host in new[] { "civitai.red", "civitai.com" })        // .red lists everything, .com only the safe-for-work part
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v1/model-versions/by-hash/{sha}");
                if (CivitaiKey is string key) req.Headers.Authorization = new("Bearer", key);
                using var res = await web.SendAsync(req);
                if (!res.IsSuccessStatusCode) continue;
                var ver = JsonNode.Parse(await res.Content.ReadAsStringAsync());
                var modelId = ver?["modelId"]?.ToString();
                if (modelId == null) continue;
                string about = null;
                try { about = Plain(JsonNode.Parse(await web.GetStringAsync($"https://{host}/api/v1/models/{modelId}"))?["description"]?.ToString()); } catch { }
                about = Short(about ?? Plain(ver["description"]?.ToString()) ?? local.Description);
                var trigger = (ver["trainedWords"] as JsonArray)?.Select(t => t?.ToString()?.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToArray() ?? [];
                return new LoraInfo(ver["model"]?["name"]?.ToString() ?? local.Name, about, trigger.Length > 0 ? trigger : local.Trigger,
                    $"https://{host}/models/{modelId}", fi.Length, fi.LastWriteTimeUtc);
            }
        }
        catch { /* offline, or Civitai busy: the file's own notes will do */ }
        return local;
    }

    // what the trainer wrote into the file: a title or description if any, else the tags it learned most
    static LoraInfo FromMetadata(string path, FileInfo fi)
    {
        string name = null, about = null; string[] trigger = [];
        try
        {
            using var f = File.OpenRead(path);
            var len = new byte[8]; f.ReadExactly(len);
            long n = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n > 0 && n < 100_000_000)
            {
                var head = new byte[n]; f.ReadExactly(head);
                var meta = JsonNode.Parse(head)?["__metadata__"];
                name = meta?["modelspec.title"]?.ToString() is { Length: > 0 } t && !t.StartsWith("training_") ? t : null;
                about = Short(Plain(meta?["modelspec.description"]?.ToString()));
                if (meta?["modelspec.trigger_phrase"]?.ToString() is { Length: > 0 } phrase) trigger = phrase.Split(',').Select(x => x.Trim()).Where(x => x != "").ToArray();
                if (about == null && meta?["ss_tag_frequency"]?.ToString() is { Length: > 0 } freq)
                {
                    var tags = JsonNode.Parse(freq)?.AsObject().SelectMany(d => d.Value?.AsObject() ?? new JsonObject())
                        .OrderByDescending(kv => (double?)kv.Value ?? 0).Select(kv => kv.Key.Trim()).Where(k => k != "").Distinct().Take(8).ToList();
                    if (tags is { Count: > 0 }) about = "Trained on pictures tagged: " + string.Join(", ", tags) + ".";
                }
            }
        }
        catch { }
        return new LoraInfo(name, about, trigger, null, fi.Length, fi.LastWriteTimeUtc);
    }

    static string Plain(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"<(br|/p|/li|/h\d)[^>]*>", " "), "<[^>]+>", ""));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text == "" ? null : text;
    }

    static string Short(string text, int max = 260)
    {
        if (text == null || text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max);
        return text[..(cut > max / 2 ? cut : max)].TrimEnd(',', '.', ';', ' ') + "…";
    }

    // each LoRA row: what the picked LoRA does, and its trigger words a click away
    void ShowLoraInfo()
    {
        foreach (var r in loraRows) ShowLoraInfo(r);
        if (ModelsPanel.Visibility == Visibility.Visible && downloads.Count == 0) BuildModels();
    }

    void ShowLoraInfo(LoraRow r)
    {
        if (r.Info == null) return;
        var info = r.Box?.SelectedItem is string l && l != "None" ? loraInfo.GetValueOrDefault(l) : null;
        var parts = new List<string>();
        var problem = r.Box?.SelectedItem is string chosen && chosen != "None" ? LoraProblem(chosen) : null;
        if (problem != null) parts.Add(problem);
        r.Info.Foreground = Brush(problem != null ? "Danger" : "Muted");
        if (r.FixBtn != null) r.FixBtn.Visibility = problem != null && r.Box.SelectedItem is string pf && HasPeftKeys(Path.Combine(ModelsDir, "loras", pf)) ? Visibility.Visible : Visibility.Collapsed;
        if (info?.Trigger is { Length: > 0 } t) parts.Add("Trigger words: " + string.Join(", ", t));
        if (info?.Description is string d) parts.Add(d);
        r.Info.Text = string.Join("\n", parts);
        r.Info.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        r.Info.ToolTip = info == null ? null : string.Join("\n\n", new[] { info.Name, info.Description, info.Url }.Where(x => x != null));
        r.TriggerBtn.Visibility = info?.Trigger is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (r.Box?.SelectedItem is string sel && sel != "None" && info == null && lookingUpLoras) { r.Info.Text = "Looking up what this LoRA does…"; r.Info.Visibility = Visibility.Visible; }
    }

    void AddTriggerWords(LoraRow r)
    {
        if (r.Box?.SelectedItem is not string l || loraInfo.GetValueOrDefault(l)?.Trigger is not { Length: > 0 } trigger) return;
        var text = PromptBox.Text.TrimEnd();
        var missing = trigger.Where(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0) { Note("Its trigger words are already in the prompt."); return; }
        PromptBox.Text = text + (text.Length > 0 && !text.EndsWith(',') ? ", " : text.Length > 0 ? " " : "") + string.Join(", ", missing);
        PromptBox.CaretIndex = PromptBox.Text.Length;
        Note("Trigger words added to the prompt.");
    }

    static void OpenPage(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}
