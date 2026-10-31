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

// Strength: the strength its page recommends (one value, or low and high), [] when it gives none, null when not looked for yet
public record LoraInfo(string Name, string Description, string[] Trigger, string Url, long Size, DateTime Modified, double[] Strength = null,
                       string BaseModel = null,      // what it was trained for ("Illustrious", "Pony", "SDXL 1.0"…); "" when nobody says, null when not looked for
                       string Picture = null, string SafePicture = null);     // small pictures from its Civitai page: its first, and its first rated safe for work; "" when none, null when not looked for

public partial class MainWindow
{
    static string LoraInfoFile => Path.Combine(Path.GetDirectoryName(Settings.FilePath), "lora_info.json");
    Dictionary<string, LoraInfo> loraInfo = new(StringComparer.OrdinalIgnoreCase);
    bool lookingUpLoras;

    // after the LoRA list loads: cached answers right away, then the new or changed files in the background
    async Task LoadLoraInfo()
    {
        LoraLineConverter.Line = LoraLine;          // the lines under the names in the LoRA lists
        if (lookingUpLoras) return;
        lookingUpLoras = true;
        try
        {
            try { loraInfo = JsonSerializer.Deserialize<Dictionary<string, LoraInfo>>(File.ReadAllText(LoraInfoFile)) is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : loraInfo; }
            catch { /* none yet */ }
            ShowLoraInfo();
            bool pictured = false;
            foreach (var lora in loras.ToList())
            {
                var path = Path.Combine(ModelsDir, "loras", lora);
                if (!File.Exists(path)) continue;
                var fi = RealFile(path);
                if (loraInfo.TryGetValue(lora, out var known) && known.Size == fi.Length && known.Modified == fi.LastWriteTimeUtc && known.Strength != null && known.BaseModel != null)     // (many LoRAs have no trigger words: not a reason to look again)
                {
                    // looked up before pictures were kept: its page has them, without reading the whole file again
                    if (known.Picture == null && known.Url != null) { loraInfo[lora] = await WithPictures(known, lora); pictured = true; }
                    continue;
                }
                loraInfo[lora] = await LookUp(path, fi);
                try { File.WriteAllText(LoraInfoFile, JsonSerializer.Serialize(loraInfo)); } catch { }
                ShowLoraInfo();
            }
            if (pictured)          // saved and shown once at the end: a few hundred at a time would redraw the Models page for each
            {
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
                string about = null, verAbout = Plain(ver["description"]?.ToString());
                try { about = Plain(JsonNode.Parse(await web.GetStringAsync($"https://{host}/api/v1/models/{modelId}"))?["description"]?.ToString()); } catch { }
                var strength = StrengthIn(verAbout) ?? StrengthIn(about) ?? [];
                about = Short(about ?? verAbout ?? local.Description);
                var trigger = (ver["trainedWords"] as JsonArray)?.Select(t => t?.ToString()?.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToArray() ?? [];
                var (picture, safe) = Pictures(ver["images"]);
                return new LoraInfo(ver["model"]?["name"]?.ToString() ?? local.Name, about, trigger.Length > 0 ? trigger : local.Trigger,
                    $"https://{host}/models/{modelId}", fi.Length, fi.LastWriteTimeUtc, strength, ver["baseModel"]?.ToString() is { Length: > 0 } bm ? bm : local.BaseModel, picture, safe);
            }
        }
        catch { /* offline, or Civitai busy: the file's own notes will do */ }
        return local;
    }

    // the first picture of a Civitai version, and its first rated PG or PG-13 (nsfwLevel 1 or 2), each asked for small; "" when there is none
    static (string Picture, string Safe) Pictures(JsonNode images)
    {
        var list = (images as JsonArray)?.Where(i => i?["url"] != null && (i["type"]?.ToString() ?? "image") == "image").ToList() ?? [];
        string Small(JsonNode i) => i == null ? "" : Regex.Replace(i["url"].ToString(), @"/(width=\d+|original=true)/", "/width=160/");
        int Level(JsonNode i) => i["nsfwLevel"] is JsonValue v && v.TryGetValue(out int n) ? n : 16;        // not rated: treated as adult
        return (Small(list.FirstOrDefault()), Small(list.FirstOrDefault(i => Level(i) <= 2)));
    }

    // a LoRA looked up before pictures were kept: the pictures of its version on its Civitai page (the version whose file has its name or size)
    async Task<LoraInfo> WithPictures(LoraInfo info, string lora)
    {
        try
        {
            var page = new Uri(info.Url);
            if (!IsCivitai(page) || page.AbsolutePath.Trim('/').Split('/') is not ["models", var id, ..]) return info with { Picture = "", SafePicture = "" };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{page.Host}/api/v1/models/{id}");
            if (CivitaiKey is string key) req.Headers.Authorization = new("Bearer", key);
            using var res = await web.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.NotFound) return info with { Picture = "", SafePicture = "" };
            if (!res.IsSuccessStatusCode) return info;          // busy or offline: asked again next time
            var versions = JsonNode.Parse(await res.Content.ReadAsStringAsync())?["modelVersions"] as JsonArray;
            var name = Path.GetFileName(lora);
            var ver = versions?.FirstOrDefault(v => (v?["files"] as JsonArray)?.Any(f => f?["name"]?.ToString() == name
                          || f?["sizeKB"] is JsonValue kb && kb.TryGetValue(out double k) && Math.Abs(k * 1024 - info.Size) < 2048) == true) ?? versions?.FirstOrDefault();
            var (picture, safe) = Pictures(ver?["images"]);
            return info with { Picture = picture, SafePicture = safe };
        }
        catch { return info; }
    }

    // what the trainer wrote into the file: a title or description if any, else the tags it learned most
    static LoraInfo FromMetadata(string path, FileInfo fi)
    {
        string name = null, about = null, baseModel = ""; string[] trigger = [];
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
                // the model it was trained on first: kohya writes sdxl_base_v1-0 as the version even for Pony and Illustrious LoRAs
                baseModel = $"{meta?["ss_sd_model_name"]} {meta?["ss_base_model_version"]} {meta?["modelspec.architecture"]}".Trim();
                if (meta?["modelspec.trigger_phrase"]?.ToString() is { Length: > 0 } phrase) trigger = phrase.Split(',').Select(x => x.Trim()).Where(x => x != "").ToArray();
                if (meta?["ss_tag_frequency"]?.ToString() is { Length: > 0 } freq)
                {
                    var tags = JsonNode.Parse(freq)?.AsObject().SelectMany(d => d.Value?.AsObject() ?? new JsonObject())
                        .OrderByDescending(kv => (double?)kv.Value ?? 0).Select(kv => kv.Key.Trim()).Where(k => k != "").Distinct().Take(8).ToList();
                    if (trigger.Length == 0 && tags is { Count: > 0 })
                        trigger = tags.Where(t => !CommonTag.IsMatch(t)).Take(3).ToArray();
                    if (about == null && tags is { Count: > 0 }) about = "Trained on pictures tagged: " + string.Join(", ", tags) + ".";
                }
            }
        }
        catch { }
        return new LoraInfo(name, about, trigger, null, fi.Length, fi.LastWriteTimeUtc, StrengthIn(about) ?? [], baseModel);
    }

    // generic booru tags that are never trigger words: body counts, hair/eye colors, common poses
    static readonly Regex CommonTag = new(@"^\d+(boy|girl)s?$|^(solo|nude|naked|clothed|male|female|blonde|black|brown|red|white|blue|green|pink|purple|grey|gray|long|short|medium)\b|^(elf|pointy ears|smile|blush|breasts|penis|looking at viewer|standing|sitting|lying|tongue|open mouth|closed eyes|simple background|white background|full body|upper body|cowboy shot|portrait|close-up|from above|from below|outdoors|indoors|holding|1other)$", RegexOptions.IgnoreCase);

    // "weight 0.6-0.8", "strength: 0.7", "recommended scale 0.5 ~ 0.9", "<lora:x:0.75>": the strength an author recommends
    static readonly Regex StrengthRange = new(@"\b(weight|strength|scale|multiplier)s?\b[^\d<\n]{0,30}?(?<![\w.])(\d(?:\.\d+)?)\s*(?:-|–|~|to)\s*(\d(?:\.\d+)?)(?!\w|\.\d)", RegexOptions.IgnoreCase);
    static readonly Regex StrengthOne = new(@"\b(weight|strength|scale|multiplier)s?\b[^\d<\n]{0,30}?(?<![\w.])(\d(?:\.\d+)?)(?!\w|\.\d)|<lora:[^:>]+:(\d(?:\.\d+)?)>", RegexOptions.IgnoreCase);

    static double[] StrengthIn(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        static double N(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        static bool Ok(double v) => v >= 0.05 && v <= 2;
        foreach (Match m in StrengthRange.Matches(text))
        {
            double a = N(m.Groups[2].Value), b = N(m.Groups[3].Value);
            if (Ok(a) && Ok(b) && a != b) return [Math.Min(a, b), Math.Max(a, b)];
        }
        foreach (Match m in StrengthOne.Matches(text))
            if (N(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) is var v && Ok(v)) return [v];
        return null;
    }

    // the middle of a LoRA's recommended strength, or null when its page gives none
    double? RecommendedStrength(string lora) =>
        lora != null && loraInfo.GetValueOrDefault(lora)?.Strength is { Length: > 0 } s ? Math.Round(s.Average(), 2) : null;

    static string StrengthText(double[] s) => s.Length > 1 ? FormattableString.Invariant($"{s[0]:0.##}–{s[1]:0.##}") : FormattableString.Invariant($"{s[0]:0.##}");

    static bool SelfTestStrength()
    {
        string Got(string t) => StrengthIn(t) is { } s ? StrengthText(s) : "none";
        var cases = new (string Text, string Want)[]
        {
            ("Recommended weight: 0.6-0.8 for best results", "0.6–0.8"), ("Use strength 0.7. CFG 7", "0.7"), ("scale 0.5 ~ 0.9", "0.5–0.9"),
            ("try <lora:mystyle:0.75> in the prompt", "0.75"), ("trained with 1girl weight 1girl", "none"), ("weight 10 steps, cfg 7", "none"),
            ("Strength 1.0 to 1.2", "1–1.2"), ("strength: Krea2 base", "none"), ("Trained on 40 pictures", "none"),
        };
        return cases.All(c => Got(c.Text) == c.Want);
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
        foreach (var r in loraRows) { ShowLoraInfo(r); r.Box?.Items.Refresh(); }       // the lines in the lists, too
        ShowLoraFitNote();
        if (ModelsPanel.Visibility == Visibility.Visible && downloads.Count == 0) BuildModels();
    }

    // ---------------- searching the model and LoRA lists ----------------
    // a list keeps its chosen item while filtered, so a search never changes what's picked
    static void ApplySearch(ComboBox box, string q, Func<string, string> extra = null)
    {
        if (box == null) return;
        box.Items.Filter = string.IsNullOrWhiteSpace(q) ? null : o => o is string s && (s == box.SelectedItem as string || s == "None"
            || s.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || extra?.Invoke(s)?.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) == true);
    }

    void ModelSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch(ModelBox, ModelSearch.Text, ModelLine);

    // the line under each model in the picker: its kind and what it's good at (as on the Models page); worked out once per model
    readonly Dictionary<string, string> modelLines = new();
    string ModelLine(string m)
    {
        if (modelLines.TryGetValue(m, out var known)) return known;
        string about = null;
        try
        {
            var i = separate.TryGetValue(m, out var s) ? Inspect("diffusion_models", Path.Combine(ModelsDir, "diffusion_models", s.Unet))
                : Inspect("checkpoints", Path.Combine(ModelsDir, "checkpoints", m));
            about = i.About;
        }
        catch { }
        var label = RecFor(m)?.Label;
        return modelLines[m] = string.Join(" · ", new[] { label, about }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
    }

    // picking a model from code: a search could be hiding it, and a hidden item can't be selected (it would stay on the old model)
    void PickLora(ComboBox box, string lora)
    {
        if (LoraSearch.Text != "") LoraSearch.Text = "";
        box.SelectedItem = lora;
    }

    void PickModel(string m)
    {
        if (ModelSearch.Text != "") ModelSearch.Text = "";
        ModelBox.SelectedItem = m;
    }

    // by name, and by what it does: its title, description, trigger words and base model
    string LoraSearchText(string lora) => loraInfo.GetValueOrDefault(lora) is { } i
        ? string.Join(" ", new[] { i.Name, i.Description, i.BaseModel }.Concat(i.Trigger ?? [])) : null;

    void LoraSearch_TextChanged(object sender, TextChangedEventArgs e) { foreach (var r in loraRows) ApplySearch(r.Box, LoraSearch.Text, LoraSearchText); }

    // a LoRA's list shows a second line under each name: what it's made for and what it does (only in the open list, not the closed box)
    static DataTemplate loraItemTemplate, modelItemTemplate;
    static DataTemplate LoraItemTemplate => loraItemTemplate ??= LineTemplate("Instance");
    static DataTemplate ModelItemTemplate => modelItemTemplate ??= LineTemplate("Models");
    static DataTemplate LineTemplate(string converter) => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:local="clr-namespace:ImageForge;assembly=Ember">
          <StackPanel>
            <TextBlock Text="{Binding}"/>
            <TextBlock x:Name="Line" Text="{Binding Converter={x:Static local:LoraLineConverter.CONVERTER}}" FontSize="11" Opacity="0.7"
                       TextTrimming="CharacterEllipsis" MaxWidth="340" HorizontalAlignment="Left"/>
          </StackPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=ComboBoxItem}, FallbackValue={x:Null}}" Value="{x:Null}">
              <Setter TargetName="Line" Property="Visibility" Value="Collapsed"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding Text, ElementName=Line}" Value="">
              <Setter TargetName="Line" Property="Visibility" Value="Collapsed"/>
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """.Replace("CONVERTER", converter));

    // above the LoRA rows: how many of the listed LoRAs were made for another kind of model than the one picked
    void ShowLoraFitNote()
    {
        if (LoraFitNote == null) return;
        var want = ModelLineage;
        int off = LorasForModel.Count(l => LoraFit(l, want) != null);
        LoraFitNote.Text = off == 0 ? "" : $"{off} of your LoRAs {(off == 1 ? "was" : "were")} made for another kind of model than this {ModelLineage} one: they're at the end of the list, marked ⚠.";
        LoraFitNote.Visibility = off == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // SDXL LoRAs are trained for one line of models: Pony, Illustrious (NoobAI is built on it) or plain SDXL. Within the line they fit;
    // across it they often look off. Unknown either way: no warning.
    static string Lineage(string text) => text?.ToLowerInvariant() is not string t ? null
        : t.Contains("pony") ? "Pony" : Regex.IsMatch(t, "illustrious|noob|(?<![a-z])wai") ? "Illustrious" : Regex.IsMatch(t, "sdxl|sd ?xl|stable-diffusion-xl") ? "SDXL" : null;
    string ModelLineage => Sdxl && ModelBox.SelectedItem is string m ? Lineage($"{ModelRec.Label} {m}") ?? "SDXL" : null;

    // null when the LoRA fits the picked model (or nobody can tell); otherwise a short warning
    string LoraFit(string lora) => LoraFit(lora, ModelLineage);
    string LoraFit(string lora, string want)
    {
        if (want == null || loraInfo.GetValueOrDefault(lora)?.BaseModel is not { Length: > 0 } made || Lineage(made) is not string have) return null;
        return have == want || have == "SDXL" && want != "Pony" ? null : $"Made for {have} models: it may look off on this {want} model.";
    }

    // the line under each LoRA in its list: what it's made for and what it does
    string LoraLine(string lora)
    {
        var info = loraInfo.GetValueOrDefault(lora);
        var made = info?.BaseModel is { Length: > 0 } b ? b : null;
        var fit = LoraFit(lora) != null ? "⚠ " : "";
        var about = info?.Description is { Length: > 0 } d ? (d.Length > 90 ? d[..90].TrimEnd() + "…" : d) : null;
        return string.Join(" · ", new[] { made == null ? null : fit + made, about }.Where(x => x != null));
    }

    void ShowLoraInfo(LoraRow r)
    {
        if (r.Info == null) return;
        var info = r.Box?.SelectedItem is string l && l != "None" ? loraInfo.GetValueOrDefault(l) : null;
        var parts = new List<string>();
        var problem = r.Box?.SelectedItem is string chosen && chosen != "None" ? LoraProblem(chosen) : null;
        if (problem != null) parts.Add(problem);
        else if (r.Box?.SelectedItem is string f && LoraFit(f) is string fit) parts.Add(fit);
        r.Info.Foreground = Brush(problem != null ? "Danger" : "Muted");
        if (r.FixBtn != null) r.FixBtn.Visibility = problem != null && r.Box.SelectedItem is string pf && HasPeftKeys(Path.Combine(ModelsDir, "loras", pf)) ? Visibility.Visible : Visibility.Collapsed;
        if (info?.Strength is { Length: > 0 } s) parts.Add($"Its page recommends strength {StrengthText(s)}.");
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
        // ticked = in the prompt; the menu stays open so several can be picked, and unticking takes one out again
        bool InPrompt(string w) => PromptBox.Text.Contains(w, StringComparison.OrdinalIgnoreCase);
        var menu = new ContextMenu { PlacementTarget = r.TriggerBtn };
        var all = new MenuItem { Header = "Add all", FontWeight = FontWeights.SemiBold, StaysOpenOnClick = true };
        menu.Items.Add(all);
        menu.Items.Add(new Separator());
        var items = trigger.Select(w => new MenuItem { Header = w, IsCheckable = true, IsChecked = InPrompt(w), StaysOpenOnClick = true }).ToList();
        foreach (var item in items)
        {
            var word = (string)item.Header;
            item.Click += (_, _) => { if (item.IsChecked) AppendTrigger(word); else RemoveTrigger(word); };
            menu.Items.Add(item);
        }
        all.Click += (_, _) => { foreach (var i in items.Where(i => !i.IsChecked)) { i.IsChecked = true; AppendTrigger((string)i.Header); } };
        menu.IsOpen = true;
    }

    void AppendTrigger(string word)
    {
        var text = PromptBox.Text.TrimEnd();
        if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return;
        PromptBox.Text = text + (text.Length == 0 ? "" : text.EndsWith(',') ? " " : ", ") + word;
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    void RemoveTrigger(string word)
    {
        var parts = PromptBox.Text.Split(',').Select(p => p.Trim()).Where(p => p != "" && !p.Equals(word, StringComparison.OrdinalIgnoreCase));
        PromptBox.Text = string.Join(", ", parts);
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    static void OpenPage(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}

// the second line under a LoRA in its list (see LoraItemTemplate); the window supplies the text
public class LoraLineConverter : System.Windows.Data.IValueConverter
{
    public static readonly LoraLineConverter Instance = new(), Models = new() { ForModels = true };
    public static Func<string, string> Line = _ => "", ModelLine = _ => "";
    bool ForModels;
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is string s && s != "None" ? (ForModels ? ModelLine(s) : Line(s)) : "";
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
