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

    // ideas pasted from other models' prompts: without this, BREAK became "breaking through barriers" and <lora:…> tags stayed in the text
    const string OtherTags = "The idea may be written in another image model's prompt style. Read every tag as part of the idea: booru tags " +
        "(1girl or 1boy is one female or male character, 2girls two, solo one person, looking at viewer, from above…); weights like (red dress:1.3) or " +
        "((red dress)) mean that part matters more, so give it more detail, and [word] or (word:0.7) less; quality and score tags (masterpiece, best quality, " +
        "score_9, score_8_up) ask for a high-quality picture, and score tags stay only if this model's form above starts with them; BREAK only separates parts of the prompt; <lora:name:0.8> tags are add-on files. " +
        "Write none of that syntax yourself (no brackets, weight numbers, BREAK or <lora:…> tags), and use tags only where this model's form above asks for them. ";

    // tested on MOSS-Image: its official instructions list "Studio photograph of" as an example opening, and the helper used it for anime
    // ideas too; and it softened sexual requests into "intimate" and "sensual"
    const string KeepMedium = "Keep the medium the idea asks for: anime, manga, hentai, cartoon, a named anime or game character, or booru tags such " +
        "as 1girl mean an anime-style illustration (clean line art, cel shading), never a photograph; a style the idea names stays that style; an idea " +
        "with no style or medium word is a realistic photograph (open with \"Photograph of\"), set where the idea implies, not a studio unless it asks for one. " +
        // tested: asked to describe a named character's look, the helper wrote it from memory and got it wrong; the image model knows them better
        "A named fictional character (anime, manga, game, cartoon or comic) keeps their name exactly as written, with their series, like \"Hinata Hyuga " +
        "from Naruto\", and their own gender. Don't describe their face, hair, eyes, body or usual outfit from memory: write only what the idea says about " +
        "their looks, and describe the pose, action, setting, light and style instead. Booru-style names like hatsune_miku or " +
        "tifa_lockhart_(final_fantasy) are such characters: write them as plain names (Hatsune Miku, Tifa Lockhart from Final Fantasy). " +
        "Character names are who is in the picture, never text to show in it: no sign, poster or caption with their names. " +
        "When the idea asks for a character's adult or older version, write \"adult\" before their name and draw them as adults. ";

    // well-known characters who are children or teenagers in their own series: with sexual words they're blocked like MinorWords.
    // Tested: the helper models can't judge this themselves (the 8B named no one, the 4B called Tifa 17 and 2B 15), so it's fixed lists.
    // ponytail: word lists of the best-known ones; add names as they come up. Adults are never on them, so they never block them.
    static readonly Regex MinorCharacters = new(@"\b(himiko toga|toga himiko|" +
        @"nezuko|tanjiro|zenitsu|inosuke|kanao|boruto|sarada uchiha|uchiha sarada|himawari uzumaki|uzumaki himawari|yuji itadori|itadori yuji|" +
        @"megumi fushiguro|fushiguro megumi|nobara|kugisaki|anya forger|denji|asuka langley|soryu asuka|rei ayanami|ayanami rei|" +
        @"madoka kaname|kaname madoka|homura akemi|akemi homura|sayaka miki|chibiusa|hitori gotoh?|gotoh? hitori|bocchi|" +
        @"taiga aisaka|aisaka taiga|ruby hoshino|hoshino ruby|ash ketchum|marin kitagawa|kitagawa marin)\b", RegexOptions.IgnoreCase);
    // teenagers through most of their story who are shown grown up later (My Hero Academia's epilogue 8 years on, Haikyu!!'s timeskip…):
    // their name alone means the teenager most pictures show, so only their adult version, asked for in words, goes into a sexual picture
    static readonly Regex GrownUpLater = new(@"\b(midoriya|izuku|deku|bakugou?|shoto todoroki|todoroki shoto|uraraka|ochaco|tenya iida|iida tenya|" +
        @"ashido|kirishima eijirou?|eijirou? kirishima|yaoyorozu|tsuyu asui|asui tsuyu|froppy|kyoka jiro|jirou? kyoka|kaminari|hagakure|" +
        @"shinji ikari|usagi tsukino|tsukino usagi|kaguya shinomiya|shinomiya kaguya|shoyo hinata|hinata shoyo|tobio kageyama|kageyama tobio)\b",
        RegexOptions.IgnoreCase);
    const string CharacterNote = MinorNote + " For a character shown grown up later in their story, ask for that version: \"adult Mina Ashido\".";
    static readonly Regex AdultVersion = new(@"\b(adult|grown[- ]?up|older|years later|time ?skip|epilogue)\b", RegexOptions.IgnoreCase);
    static bool SexualMinorCharacter(string text)
    {
        text = text.Replace('_', ' ');
        return SexualWords.IsMatch(text) && (MinorCharacters.IsMatch(text) || GrownUpLater.IsMatch(text) && !AdultVersion.IsMatch(text));
    }
    const string PlainWords = "For nude or sexual ideas, name body parts and acts with the user's own explicit words, or plain everyday ones " +
        "(breasts, nipples, pussy, penis, sex, blowjob…), and describe them directly; never soften them into words like intimate, sensual, " +
        "suggestive or private areas. ";

    string EnhanceSystemFor() => Private + "You write prompts for an AI image generator. Turn the user's idea into one detailed prompt. " + PromptForm(false) +
        OtherTags + KeepMedium + PlainWords + ManyPeople + NoNegative + KeepAsked + "Reply with the prompt only.";

    // a picture -> the prompt that recreates it on the selected model, as closely as it can be put in words
    // tested: without it a femboy in a skirt became "a young woman"; a first wording overshot (a long-haired man became a femboy, a woman a trans woman)
    const string WhoTheyAre = "For each person, decide who they are from their body as well as their clothes and hair. Most people are simply a man or a " +
        "woman: a man with long hair is a man, a woman in a plain dress is a woman. Say femboy only for a clearly male body (flat chest, narrow hips, male jaw " +
        "or Adam's apple, or a penis) dressed or styled in a girly way (skirt, dress, stockings, makeup, cute hair accessories); say trans woman only when " +
        "breasts and a penis both show; say trans man only when the picture makes it plain. Name it plainly (\"a man\", \"a woman\", \"a femboy\"). ";
    // the user wants the whole picture understood, nudity included, not only the clothes
    const string WholeBody = "Describe the bodies as fully as the clothes: build, body shape and proportions, chest or breast size, hips, butt and legs; " +
        "which parts are bare, which are covered, and how (fully nude, topless, partly covered, see-through, pulled aside, showing under or through clothing); " +
        "every uncovered intimate part with its size, shape and state; and any sexual act exactly: who does what to whom, with which body parts, their " +
        "positions and where they touch. ";

    string DescribeSystemFor() => Private + "Look at the picture and write the prompt that makes an AI image generator recreate it as exactly as possible. " +
        WhoTheyAre + WholeBody + PlainWords +
        "Describe everything you can see, specifically: how many people there are and where each one is in the frame; for each person their build, skin tone, " +
        "face, eye colour, expression, hair colour, length and style, every piece of clothing and accessory with its colour, material and fit, any nudity plainly, " +
        "the exact pose, what their hands are doing and where they are looking; every object, the background and the setting; the camera angle, distance and " +
        "framing; the light, where it comes from and its colours; the colour palette; and the medium and art style (photo with camera and lens, anime, painting, " +
        "3D render…). Use exact colours and specific words, never vague ones, and don't add anything that isn't in the picture. If there are two or more people, " +
        "describe each one separately. " + PromptForm(true) + NoNegative + "Reply with the prompt only.";

    string NoNegative => CfgSlider.Value <= 1 ? "This model ignores negative prompts, so say what should be there rather than what to avoid. " : "";

    // the form the selected model was trained on; detailed: room for a whole picture rather than a short idea
    string PromptForm(bool detailed)
    {
        var name = (ModelBox.SelectedItem as string ?? "").ToLowerInvariant();
        var tags = detailed ? "Write 50 to 90 tags. " : "Write 25 to 50 tags. ";
        string Words(string normal) => detailed ? "Write 120 to 180 words. " : normal;
        if (Sdxl && name.Contains("pony"))
            return "The image model is Pony Diffusion (SDXL), which reads comma-separated tags. Start with \"score_9, score_8_up, score_7_up\", then the number of " +
                   "people as tags (1girl, 1boy, 2girls…), each person's look and clothing, the action and pose, the setting, lighting and style. " + tags;
        if (Sdxl && Regex.IsMatch(name, "illustrious|wai|noob|animagine"))
            return "The image model is an anime SDXL model trained on Danbooru tags. Write comma-separated Danbooru-style tags: start with \"masterpiece, best quality\", " +
                   "then the number of people (1girl, 2boys…), each character's features and clothing, the action and pose, the background and lighting. " + tags;
        if (Anima)
            return "The image model is Anima, an anime model that reads Danbooru tags or plain sentences. Start with \"masterpiece, best quality,\" then describe the " +
                   "scene in short sentences, using tags for counts of people (1girl, 2boys). " + Words("Write 40 to 70 words. ");
        if (Krea || Qwen)
            return (Named("noct") ? "Start with \"An anime illustration of\". " : "") +
                   "The image model reads plain sentences. Put the main action and every subject first, then where they are relative to each other and the " +
                   "camera angle, then the setting, and the lighting and style last. Write one paragraph. " + Words("Write 40 to 80 words. ");
        return "Write one vivid paragraph: the subject, setting, lighting, colours, composition and mood, plus camera and lens details if it should look like " +
               "a photo. " + Words("Write 40 to 70 words. ");
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
        if (kreaTe != null) items.Add(HelperFast);
        items.AddRange((await OllamaModels()).Select(m => "Ollama: " + m));
        HelperBox.ItemsSource = items;
        HelperBox.SelectedItem = items.Contains(settings.HelperModel ?? "") ? settings.HelperModel : HelperDefault;
        HelperNote.Text = items.Count > 1
            ? "Ollama models write prompts from text only; Describe image and photo edits always use Qwen3-VL, which can see pictures."
            : "Qwen3-VL runs inside ComfyUI. If Ollama is running on this PC, its models show up here too.";
        if (kreaTe != null) HelperNote.Text = "The 4B writes about 40% faster, with a little less detail. " + HelperNote.Text;
        fillingHelper = false;
    }

    const string HelperDefault = "Qwen3-VL 8B in ComfyUI";
    // Krea 2's text encoder, the same Qwen3-VL family at half the size: it sees pictures too. Measured with MOSS-Image loaded: 10 s against 16 s.
    const string HelperFast = "Qwen3-VL 4B in ComfyUI (faster)";
    bool fillingHelper;

    void Helper_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (fillingHelper) return;
        settings.HelperModel = HelperBox.SelectedItem as string == HelperDefault ? "" : HelperBox.SelectedItem as string ?? "";
        settings.Save();
    }
}
