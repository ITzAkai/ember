// ModelRec settings and safe ranges per model family and per known model (docs/research/per-model-settings.md),
// and the Quality Guard that checks the current settings against them. The table ships inside the app and is copied to
// recommendations.json next to the settings, where it can be edited without a rebuild (a newer shipped version replaces it,
// keeping the old file as recommendations.old.json).
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;

namespace ImageForge;

public record Rec(double MaxMp, string Label, string Sampler, string Scheduler, int[] Steps, int[] StepsRange, double Cfg, double[] CfgRange,
                  bool Negative, string DefaultNegative, double LoraEach, double LoraTotal, string BadScheduler, string Why);

// a known model: matched by name, overriding its family's values where set
public record RecModel(string Match, string Family, string Label, string Sampler, string Scheduler, int[] Steps, int[] StepsRange,
                       double? Cfg, double[] CfgRange, bool? Negative, string DefaultNegative, string BadScheduler, string Why);

public record RecFile(int Version, Dictionary<string, Rec> Families, List<RecModel> Models);

record Issue(Shape Dot, int Level, string Text, Action Fix);    // Level 1: may look over-cooked; 2: known to break pictures on this model

public partial class MainWindow
{
    const string RecommendationsJson = """
    {
      "version": 8,
      "families": {
        "sdxl": { "maxMp": 1.2, "label": "SDXL", "sampler": "dpmpp_2m", "scheduler": "karras", "steps": [18, 25, 40], "stepsRange": [15, 50], "cfg": 7, "cfgRange": [5, 9],
          "negative": true, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.6, "badScheduler": null,
          "why": "SDXL: about 25 steps at CFG 7, as in ComfyUI's template." },
        "krea2": { "maxMp": 4.2, "label": "Krea 2 Turbo", "sampler": "euler", "scheduler": "simple", "steps": [6, 8, 10], "stepsRange": [6, 10], "cfg": 1, "cfgRange": [1, 1],
          "negative": false, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null,
          "why": "Krea 2 Turbo is made for 8 steps at CFG 1. More steps or a higher CFG over-cook it, and CFG above 1 doubles the time." },
        "qwen_image": { "maxMp": 4.2, "label": "Qwen-Image 2.1", "sampler": "euler", "scheduler": "simple", "steps": [20, 25, 30], "stepsRange": [18, 50], "cfg": 1, "cfgRange": [1, 2],
          "negative": false, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null,
          "why": "Qwen-Image 2.1: 25 steps at CFG 1; 40 steps adds fine detail. CFG 2 sharpens edges, 5 and up degrades the picture." },
        "anima": { "maxMp": 1.2, "label": "Anima", "sampler": "euler", "scheduler": "simple", "steps": [24, 30, 34], "stepsRange": [24, 50], "cfg": 4, "cfgRange": [3.5, 5],
          "negative": true, "defaultNegative": "worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, sepia", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null,
          "why": "Anima: 30 steps at CFG 4, as in ComfyUI's template; keep about 1 megapixel." },
        "wan": { "maxMp": 0.5, "label": "Wan 2.1", "sampler": "uni_pc", "scheduler": "simple", "steps": [20, 30, 50], "stepsRange": [20, 50], "cfg": 6, "cfgRange": [5, 7],
          "negative": true, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null,
          "why": "Wan 2.1: 30 steps at CFG 6 with uni_pc, as in ComfyUI's example." },
        "wan_i2v": { "maxMp": 0.5, "label": "Wan 2.1 picture-to-video", "sampler": "uni_pc", "scheduler": "simple", "steps": [15, 20, 40], "stepsRange": [15, 50], "cfg": 6, "cfgRange": [5, 7],
          "negative": true, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null,
          "why": "Wan 2.1 Fun InP: 20 steps at CFG 6, as in ComfyUI's template." },
        "minimax_h3": { "maxMp": 0.5, "label": "MiniMax H3", "sampler": "res_multistep", "scheduler": "simple", "steps": [12, 20, 30], "stepsRange": [8, 40], "cfg": 1, "cfgRange": [1, 2],
          "negative": false, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null,
          "why": "MiniMax H3: 20 steps at CFG 1 with res_multistep, as in ComfyUI's template. It makes the sound with the picture; a CFG above 1 doubles the time." },
        "flux": { "maxMp": 1.2, "label": "FLUX schnell", "sampler": "euler", "scheduler": "simple", "steps": [3, 4, 6], "stepsRange": [1, 8], "cfg": 1, "cfgRange": [1, 1],
          "negative": false, "defaultNegative": "", "loraEach": 1.0, "loraTotal": 1.5, "badScheduler": null, "why": "FLUX schnell: 4 steps at CFG 1." }
      },
      "models": [
        { "match": "v[-_ ]?pred", "family": "sdxl", "label": "v-prediction SDXL", "sampler": "euler", "scheduler": "normal", "steps": [28, 30, 35], "stepsRange": [26, 40],
          "cfg": 4.5, "cfgRange": [4, 5], "badScheduler": "karras", "why": "v-prediction models: Euler only, CFG 4 to 5; Karras schedulers don't work with them." },
        { "match": "lightning|hyper", "family": "sdxl", "label": "SDXL Lightning / Hyper", "sampler": "dpmpp_sde", "scheduler": "karras", "steps": [5, 6, 7], "stepsRange": [4, 8],
          "cfg": 1.5, "cfgRange": [1, 2], "negative": false, "why": "Few-step SDXL: about 6 steps at CFG 1.5." },
        { "match": "dmd", "family": "sdxl", "label": "SDXL DMD2", "sampler": "lcm", "scheduler": "sgm_uniform", "steps": [4, 8, 12], "stepsRange": [4, 12],
          "cfg": 1, "cfgRange": [1, 1.5], "negative": false, "why": "DMD2: the LCM sampler, 4 to 12 steps, CFG 1." },
        { "match": "sdxl[-_ ]?turbo", "family": "sdxl", "label": "SDXL Turbo", "sampler": "euler_ancestral", "scheduler": "simple", "steps": [1, 2, 4], "stepsRange": [1, 4],
          "cfg": 1, "cfgRange": [1, 1], "negative": false, "why": "SDXL Turbo: 1 to 4 steps, no guidance." },
        { "match": "turbo", "family": "sdxl", "label": "SDXL Turbo merge", "sampler": "dpmpp_sde", "scheduler": "karras", "steps": [6, 8, 10], "stepsRange": [4, 12],
          "cfg": 2, "cfgRange": [1.5, 3], "why": "Turbo merges like DreamShaper XL Turbo: 4 to 8 steps at CFG 2 with DPM++ SDE Karras." },
        { "match": "pony", "family": "sdxl", "label": "Pony", "sampler": "euler_ancestral", "scheduler": "normal", "steps": [18, 25, 35], "stepsRange": [15, 40],
          "cfg": 7, "cfgRange": [5, 8], "why": "Pony: Euler a, 25 steps; start prompts with score_9, score_8_up, score_7_up." },
        { "match": "noob", "family": "sdxl", "label": "NoobAI", "sampler": "euler_ancestral", "scheduler": "normal", "steps": [25, 28, 32], "stepsRange": [22, 35],
          "cfg": 5.5, "cfgRange": [4.5, 6.5], "why": "NoobAI: Euler a, 28 steps, CFG 5 to 6." },
        { "match": "unholy ?desire", "family": "sdxl", "label": "Unholy Desire Mix (Illustrious)", "sampler": "dpmpp_2m", "scheduler": "karras", "steps": [16, 24, 30],
          "stepsRange": [12, 40], "cfg": 2.5, "cfgRange": [2, 3.5], "why": "Unholy Desire Mix v7+ (Illustrious-based): DPM++ 2M Karras, CFG about 2.5, 16 steps or more, from its author." },
        { "match": "illustrious|wai", "family": "sdxl", "label": "Illustrious", "sampler": "euler_ancestral", "scheduler": "normal", "steps": [20, 28, 35], "stepsRange": [15, 40],
          "cfg": 5.5, "cfgRange": [4, 7.5], "why": "Illustrious: Euler a, 20 to 28 steps, CFG 5 to 7.5; keep quality tags and the negative short." },
        { "match": "animagine", "family": "sdxl", "label": "Animagine", "sampler": "euler_ancestral", "scheduler": "normal", "steps": [25, 28, 30], "stepsRange": [22, 32],
          "cfg": 5, "cfgRange": [4, 7], "why": "Animagine 4: Euler a, 28 steps, CFG 5." },
        { "match": "juggernaut", "family": "sdxl", "label": "Juggernaut", "sampler": "dpmpp_2m_sde", "scheduler": "karras", "steps": [30, 35, 40], "stepsRange": [25, 45],
          "cfg": 4.5, "cfgRange": [3, 6], "why": "Juggernaut: DPM++ 2M SDE, 30 to 40 steps; a lower CFG looks more realistic." },
        { "match": "realvis", "family": "sdxl", "label": "RealVisXL", "sampler": "dpmpp_sde", "scheduler": "karras", "steps": [30, 35, 50], "stepsRange": [30, 50],
          "cfg": 5, "cfgRange": [4, 7], "why": "RealVisXL: DPM++ SDE Karras with 30 steps or more." },
        { "match": "cyberrealistic", "family": "sdxl", "label": "CyberRealistic", "sampler": "dpmpp_2m_sde", "scheduler": "karras", "steps": [30, 32, 40], "stepsRange": [28, 45],
          "cfg": 4, "cfgRange": [3, 5], "why": "CyberRealistic: DPM++ 2M SDE Karras, 30+ steps, CFG 3 to 5." },
        { "match": "fineporn", "family": "krea2", "label": "FinePorn", "steps": [8, 10, 12], "stepsRange": [8, 14],
          "why": "FinePorn v5 is a Krea 2 Turbo merge. Its author recommends euler / simple, 12 steps, CFG 1; 8 to 14 steps all looked good in testing." },
        { "match": "(^|[-_ ])raw([-_ .]|$)", "family": "krea2", "label": "Krea 2 Raw", "steps": [30, 40, 52], "stepsRange": [28, 60], "cfg": 3.5, "cfgRange": [3, 4.5], "negative": true,
          "why": "Krea 2 Raw isn't distilled: it needs a real CFG and many more steps, so it's much slower than Turbo." },
        { "match": "noct", "family": "qwen_image", "label": "Noct Q Anime", "steps": [22, 25, 35], "stepsRange": [20, 40], "cfg": 3, "cfgRange": [2.5, 4], "negative": true,
          "defaultNegative": "low quality, low resolution, blurry, jpeg artifacts, washed-out colors, sloppy lines, messy line art, extra fingers, missing fingers, badly drawn hands, deformed anatomy, 3d render, photograph, photorealistic",
          "why": "The author's settings: 25 steps at CFG 3 with a negative prompt. Start prompts with \"An anime illustration of…\"." },
        { "match": "turbo", "family": "qwen_image", "label": "Qwen-Image 2.1 Turbo", "steps": [6, 8, 10], "stepsRange": [6, 10], "cfg": 1, "cfgRange": [1, 1], "negative": false,
          "why": "Qwen-Image 2.1 Turbo: 8 steps at CFG 1." },
        { "match": "turbo", "family": "anima", "label": "Anima Turbo", "steps": [8, 10, 12], "stepsRange": [8, 12], "cfg": 1, "cfgRange": [1, 1], "negative": false,
          "why": "Anima Turbo: 8 to 12 steps at CFG 1." },
        { "match": "aesthetic", "family": "anima", "label": "Anima Aesthetic", "cfg": 3.5, "cfgRange": [3, 5],
          "why": "Anima Aesthetic: CFG 3.5. Quality tags are optional: it was trained only on high-quality pictures." },
        { "match": "nova ?anime ?am", "family": "anima", "label": "Nova Anime AM", "sampler": "euler_ancestral", "scheduler": "normal", "steps": [20, 28, 30], "stepsRange": [20, 32],
          "cfg": 5, "cfgRange": [4, 6], "why": "Nova Anime AM (an Anima 2.9B merge): Euler a, 20 to 30 steps, CFG 4 to 6, from its author." },
        { "match": "eros.*turbo|turbo.*eros", "family": "minimax_h3", "label": "10Eros Max Turbo (MiniMax H3)", "steps": [6, 8, 10], "stepsRange": [4, 12], "cfg": 1, "cfgRange": [1, 1.5],
          "negative": false, "why": "10Eros Max Turbo has the speed-up built in: res_multistep / simple, 6 to 8 steps, from its author." },
        { "match": "10[-_ ]?eros|eros[-_ ]?max", "family": "minimax_h3", "label": "10Eros Max (MiniMax H3)", "cfg": 1.5, "cfgRange": [1, 2], "negative": true,
          "why": "10Eros Max: res_multistep / simple with CFG about 1.5, from its author. Keep CFG at 2 or lower or the sound turns to static; it needs the fp32 audio VAE." },
        { "match": "dev", "family": "flux", "label": "FLUX dev", "steps": [15, 20, 30], "stepsRange": [12, 50],
          "why": "FLUX dev: 20 steps at CFG 1, as in ComfyUI's example; it isn't a few-step model like schnell." }
      ]
    }
    """;

    static RecFile recommendations;

    static RecFile LoadRecommendations()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var shipped = JsonSerializer.Deserialize<RecFile>(RecommendationsJson, options);
        var file = Path.Combine(Path.GetDirectoryName(Settings.FilePath), "recommendations.json");
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<RecFile>(File.ReadAllText(file), options) is { } mine && mine.Version >= shipped.Version)
                return mine;
            if (File.Exists(file)) File.Copy(file, Path.ChangeExtension(file, ".old.json"), true);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, RecommendationsJson);
        }
        catch { /* a broken edit falls back to the shipped table */ }
        return shipped;
    }

    string RecFamilyOf(string model) => model != null && separate.TryGetValue(model, out var s) ? s.Family : model?.Contains("flux", StringComparison.OrdinalIgnoreCase) == true ? "flux" : "sdxl";

    Rec ModelRec => WithTurboLora(RecFor(ModelBox.SelectedItem as string));

    // a MiniMax H3 turbo LoRA (minimax_h3_fl2v_turbo_4step…) changes what the model needs: few steps at CFG 1.
    // Measured on a 5 s clip: 4 steps looked as good as 6 and took 184 s instead of 222 s.
    Rec WithTurboLora(Rec r)
    {
        if (!H3 || CurrentLoras().FirstOrDefault(l => l.Name.Contains("turbo", StringComparison.OrdinalIgnoreCase)) is not { } t) return r;
        bool four = Regex.IsMatch(t.Name, @"4[-_ ]?step", RegexOptions.IgnoreCase);
        return r with
        {
            Label = r.Label + " + Turbo LoRA", Steps = four ? new[] { 4, 6, 8 } : new[] { 6, 8, 10 }, StepsRange = four ? new[] { 4, 8 } : new[] { 6, 12 },
            Cfg = 1, CfgRange = new[] { 1.0, 1.0 }, Negative = false,
            Why = $"With {t.Name} the model needs only a few steps at CFG 1: {(four ? "4" : "8")} is what it was made for, and more steps only cost time.",
        };
    }

    // the family's values, overridden by the first known model whose pattern matches the name
    Rec RecFor(string model)
    {
        {
            recommendations ??= LoadRecommendations();
            var family = RecFamilyOf(model);
            var fam = recommendations.Families.GetValueOrDefault(family) ?? recommendations.Families["sdxl"];
            var name = (model ?? "") + " " + (model != null && separate.TryGetValue(model, out var s) ? s.Unet : "");
            var known = recommendations.Models.FirstOrDefault(x => x.Family == (family == "wan_i2v" ? "wan" : family) && Regex.IsMatch(name, x.Match, RegexOptions.IgnoreCase));
            if (known == null) return fam;
            return fam with
            {
                Label = known.Label ?? fam.Label, Sampler = known.Sampler ?? fam.Sampler, Scheduler = known.Scheduler ?? fam.Scheduler,
                Steps = known.Steps ?? fam.Steps, StepsRange = known.StepsRange ?? fam.StepsRange, Cfg = known.Cfg ?? fam.Cfg, CfgRange = known.CfgRange ?? fam.CfgRange,
                Negative = known.Negative ?? fam.Negative, DefaultNegative = known.DefaultNegative ?? fam.DefaultNegative,
                BadScheduler = known.BadScheduler ?? fam.BadScheduler, Why = known.Why ?? fam.Why,
            };
        }
    }

    void ResetRecommended_Click(object sender, RoutedEventArgs e)
    {
        var r = ModelRec;
        Pick(QualityGroup, "30", "30");
        StepsSlider.Value = StepsFor("30");
        CfgSlider.Value = r.Cfg;
        SamplerBox.SelectedItem = r.Sampler;
        SchedulerBox.SelectedItem = r.Scheduler;
        RefreshGuard();
    }

    // ---------------- the Quality Guard ----------------
    List<Issue> CheckSettings()
    {
        var r = ModelRec;
        var issues = new List<Issue>();
        if (ModelBox.SelectedItem == null) return issues;

        int steps = Steps;
        if (steps < r.StepsRange[0] || steps > r.StepsRange[1])
            issues.Add(new(QualityDot, steps > r.StepsRange[1] * 1.5 || steps < r.StepsRange[0] * 0.6 ? 2 : 1,
                $"{steps} steps is outside {r.StepsRange[0]}–{r.StepsRange[1]} for {r.Label}: " + (steps > r.StepsRange[1] ? "it may look over-cooked." : "it may look mushy and unfinished."),
                () => { Pick(QualityGroup, "30", "30"); StepsSlider.Value = StepsFor("30"); }));

        var (w, h) = PictureSize();
        double mp = w * h / 1e6;
        if (!VideoOn && r.MaxMp > 0 && mp > r.MaxMp * 1.15)
            issues.Add(new(SizeDot, mp > r.MaxMp * 1.6 ? 2 : 1,
                $"{w} × {h} is well above the size {r.Label} was trained at (about {r.MaxMp:0.#} megapixels): duplicated or stretched bodies and extra limbs get likely. " +
                "Use a preset size and the Hi-res fix instead.", () => CustomSizeBox.IsChecked = false));

        double cfg = CfgSlider.Value, lo = r.CfgRange[0], hi = r.CfgRange[1];
        if (cfg < lo - 0.01 || cfg > hi + 0.01)
        {
            bool turboTime = r.Cfg <= 1 && cfg > 1 && (Krea || Anima || Qwen);
            issues.Add(new(CfgDot, cfg > hi * 1.6 + 0.5 || cfg < lo * 0.6 ? 2 : 1,
                $"CFG {cfg:0.#} is outside {lo:0.#}–{hi:0.#} for {r.Label}: " + (cfg > hi ? "colours can burn and look over-contrasted" : "it may ignore the prompt") +
                (turboTime ? ", and above 1 every step takes twice as long." : "."),
                () => CfgSlider.Value = r.Cfg));
        }

        if (SchedulerBox.SelectedItem is string sched && r.BadScheduler != null && sched == r.BadScheduler)
            issues.Add(new(SamplerDot, 2, $"The {sched} scheduler breaks {r.Label} pictures.", () => SchedulerBox.SelectedItem = r.Scheduler));
        else if (SamplerBox.SelectedItem is string smp && (smp != r.Sampler || SchedulerBox.SelectedItem as string != r.Scheduler))
            issues.Add(new(SamplerDot, 1, $"{r.Label} is made for {r.Sampler} / {r.Scheduler}; others change the look and can soften detail.",
                () => { SamplerBox.SelectedItem = r.Sampler; SchedulerBox.SelectedItem = r.Scheduler; }));

        var active = CurrentLoras();
        foreach (var l in active)
        {
            if (loraInfo.GetValueOrDefault(l.Name)?.Strength is not { Length: > 0 } rec) continue;
            if (l.Strength <= rec[^1] * 1.2 && l.Strength >= rec[0] * 0.6) continue;
            issues.Add(new(LoraDot, l.Strength > rec[^1] * 1.8 ? 2 : 1,
                $"{System.IO.Path.GetFileNameWithoutExtension(l.Name)} is at {l.Strength:0.##}; its page recommends {StrengthText(rec)}.",
                () => SetLoraStrength(l.Name, RecommendedStrength(l.Name) ?? rec[0])));
        }
        foreach (var l in active.Where(l => l.Strength > r.LoraEach * 1.25 && loraInfo.GetValueOrDefault(l.Name)?.Strength is not { Length: > 0 }))
            issues.Add(new(LoraDot, l.Strength > r.LoraEach * 2 ? 2 : 1,
                $"{System.IO.Path.GetFileNameWithoutExtension(l.Name)} at {l.Strength:0.##} is strong for one LoRA (up to {r.LoraEach:0.#} is usual): faces and hands can warp.",
                () => SetLoraStrength(l.Name, r.LoraEach * 0.8)));
        double total = active.Sum(l => l.Strength);
        if (active.Count > 1 && total > r.LoraTotal)
            issues.Add(new(LoraDot, total > r.LoraTotal * 1.6 ? 2 : 1,
                $"Your LoRAs add up to {total:0.##}; above about {r.LoraTotal:0.#} together they fight each other and over-cook the picture.",
                () => { foreach (var l in active) SetLoraStrength(l.Name, l.Strength * r.LoraTotal / total); }));
        var realism = active.Where(l => Regex.IsMatch(l.Name + " " + loraInfo.GetValueOrDefault(l.Name)?.Name, "realis|real |photo|skin", RegexOptions.IgnoreCase) && l.Strength >= 0.8).ToList();
        if (realism.Count > 1)
            issues.Add(new(LoraDot, 1, "Two realism LoRAs at full strength stack the same effect: skin goes plastic or over-sharp. About 0.5–0.6 each, or keep one.",
                () => { foreach (var l in realism) SetLoraStrength(l.Name, 0.55); }));

        if (cfg <= 1 && !string.IsNullOrWhiteSpace(NegativeBox.Text) && EditMode.IsChecked != true)
            issues.Add(new(null, 1, "The negative prompt is ignored at CFG 1, so it does nothing here.", null));
        return issues;
    }

    void SetLoraStrength(string name, double value)
    {
        foreach (var row in loraRows.Where(r => r.Box?.SelectedItem as string == name && r.Slider != null))
            row.Slider.Value = Math.Round(Math.Clamp(value, row.Slider.Minimum, row.Slider.Maximum), 2);
    }

    bool refreshingGuard;

    void RefreshGuard()
    {
        if (refreshingGuard || GuardSummary == null) return;
        refreshingGuard = true;
        try
        {
            var issues = CheckSettings();
            foreach (var dot in new[] { QualityDot, CfgDot, SamplerDot, LoraDot, SizeDot })
            {
                var mine = issues.Where(i => i.Dot == dot).ToList();
                int level = mine.Count == 0 ? 0 : mine.Max(i => i.Level);
                dot.Fill = Brush(level == 0 ? "Ok" : level == 1 ? "Accent" : "Danger");
                dot.ToolTip = level == 0 ? "Inside the recommended range for this model" : string.Join("\n", mine.Select(i => i.Text));
            }
            var r = ModelRec;
            GuardSummary.Text = issues.Count == 0 ? $"Recommended settings for {r.Label}." : $"{issues.Count} setting{(issues.Count == 1 ? "" : "s")} outside the recommendation for {r.Label}.";
            GuardSummary.Foreground = Brush(issues.Any(i => i.Level == 2) ? "Danger" : issues.Count > 0 ? "Accent" : "Ok");
            GuardSummary.ToolTip = r.Why;
            if (GuardList.Visibility == Visibility.Visible) ShowGuardList(issues);
        }
        finally { refreshingGuard = false; }
    }

    void CheckSettings_Click(object sender, RoutedEventArgs e)
    {
        GuardList.Visibility = GuardList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (GuardList.Visibility == Visibility.Visible) ShowGuardList(CheckSettings());
    }

    void ShowGuardList(List<Issue> issues)
    {
        GuardList.Children.Clear();
        GuardList.Children.Add(new TextBlock { Text = ModelRec.Why, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var i in issues)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush(i.Level == 2 ? "Danger" : "Accent"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0) });
            row.Children.Add(new TextBlock { Text = i.Text, Style = (Style)FindResource("Note"), Foreground = Brush("Ink"), TextWrapping = TextWrapping.Wrap });
            GuardList.Children.Add(row);
        }
        if (issues.Any(i => i.Fix != null))
        {
            var fix = new Button { Content = "Fix all", Style = (Style)FindResource("Ghost"), Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            fix.Click += (_, _) => { foreach (var i in CheckSettings().Where(i => i.Fix != null)) i.Fix(); RefreshGuard(); ShowGuardList(CheckSettings()); };
            GuardList.Children.Add(fix);
        }
        else if (issues.Count == 0) GuardList.Children.Add(new TextBlock { Text = "Everything is inside the recommended range.", Style = (Style)FindResource("Note"), Foreground = Brush("Ok") });
    }
}
