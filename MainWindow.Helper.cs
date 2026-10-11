// The prompt helper, model-aware: each family gets prompts in the form it was trained on (score tags for Pony, Danbooru tags for
// the anime SDXL models, plain sentences with the action first for Krea 2 and Qwen-Image), the same rules for scenes with two or
// more people, and no reliance on a negative prompt the model will ignore. It can also use a local Ollama model for text-only work.
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace ImageForge;

public partial class MainWindow
{
    const string ManyPeople = "If there are two or more people, describe each one separately (\"The first person is … The second person is …\") so their faces " +
        "and bodies don't blend together; name exactly what they are doing and where each is relative to the other; prefer full-body framing with a clear " +
        "camera angle over first-person views; and keep the action near the start. ";

    string EnhanceSystemFor()
    {
        var name = (ModelBox.SelectedItem as string ?? "").ToLowerInvariant();
        bool ignoresNegative = CfgSlider.Value <= 1;
        var noNegative = ignoresNegative ? "This model ignores negative prompts, so say what should be there rather than what to avoid. " : "";
        string form;
        if (Sdxl && name.Contains("pony"))
            form = "The image model is Pony Diffusion (SDXL), which reads comma-separated tags. Start with \"score_9, score_8_up, score_7_up\", then the number of " +
                   "people as tags (1girl, 1boy, 2girls…), each person's look and clothing, the action and pose, the setting, lighting and style. Write 25 to 50 tags. ";
        else if (Sdxl && Regex.IsMatch(name, "illustrious|wai|noob|animagine"))
            form = "The image model is an anime SDXL model trained on Danbooru tags. Write comma-separated Danbooru-style tags: start with \"masterpiece, best quality\", " +
                   "then the number of people (1girl, 2boys…), each character's features and clothing, the action and pose, the background and lighting. Write 25 to 50 tags. ";
        else if (Anima)
            form = "The image model is Anima, an anime model that reads Danbooru tags or plain sentences. Start with \"masterpiece, best quality,\" then describe the " +
                   "scene in short sentences, using tags for counts of people (1girl, 2boys). Write 40 to 70 words. ";
        else if (Krea || Qwen)
            form = (Named("noct") ? "Start with \"An anime illustration of\". " : "") +
                   "The image model reads plain sentences. Put the main action and every subject first, then where they are relative to each other and the " +
                   "camera angle, then the setting, and the lighting and style last. Write one paragraph of 40 to 80 words. ";
        else
            form = "Write one vivid paragraph of 40 to 70 words: the subject, setting, lighting, colours, composition and mood, plus camera and lens details " +
                   "if it should look like a photo. ";
        return Private + "You write prompts for an AI image generator. Turn the user's idea into one detailed prompt. " + form + ManyPeople + noNegative +
               KeepAsked + "Reply with the prompt only.";
    }

    // ---------------- a local Ollama model (optional) ----------------
    const string OllamaApi = "http://127.0.0.1:11434";
    static readonly HttpClient ollama = new() { Timeout = TimeSpan.FromMinutes(3) };

    async Task<List<string>> OllamaModels()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var tags = JsonNode.Parse(await ollama.GetStringAsync($"{OllamaApi}/api/tags", cts.Token));
            return (tags?["models"] as JsonArray)?.Select(m => m?["name"]?.ToString()).Where(n => n != null).ToList() ?? [];
        }
        catch { return []; }
    }

    async Task<string> AskOllama(string model, string prompt, string system)
    {
        var body = new JsonObject { ["model"] = model, ["system"] = system, ["prompt"] = prompt, ["stream"] = false, ["options"] = new JsonObject { ["temperature"] = 0.7 } };
        using var r = await ollama.PostAsync($"{OllamaApi}/api/generate", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        r.EnsureSuccessStatusCode();
        var text = JsonNode.Parse(await r.Content.ReadAsStringAsync())?["response"]?.ToString() ?? "";
        return Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim().Trim('"');      // reasoning models think out loud first
    }

    async Task ShowHelperChoice()
    {
        fillingHelper = true;
        var items = new List<string> { HelperDefault };
        items.AddRange((await OllamaModels()).Select(m => "Ollama: " + m));
        HelperBox.ItemsSource = items;
        HelperBox.SelectedItem = items.Contains(settings.HelperModel ?? "") ? settings.HelperModel : HelperDefault;
        HelperNote.Text = items.Count > 1
            ? "Ollama models write prompts from text only; Describe image and photo edits always use Qwen3-VL, which can see pictures."
            : "Qwen3-VL 8B runs inside ComfyUI. If Ollama is running on this PC, its models show up here too.";
        fillingHelper = false;
    }

    const string HelperDefault = "Qwen3-VL 8B in ComfyUI";
    bool fillingHelper;

    void Helper_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (fillingHelper) return;
        settings.HelperModel = HelperBox.SelectedItem as string == HelperDefault ? "" : HelperBox.SelectedItem as string ?? "";
        settings.Save();
    }
}
