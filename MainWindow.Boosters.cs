// Quality boosters (Advanced settings). Each is only added to a graph for model families where it changes something,
// and shows why when it's off (docs/research/boosters-and-comfyui.md). Unset toggles follow the recommendation for the model.
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageForge;

public class Boosters
{
    public bool? Pag { get; set; }                      // null: the recommendation for this model
    public double PagScale { get; set; } = 3.0;
    public bool? Rescale { get; set; }
    public double RescaleMultiplier { get; set; } = 0.7;
    public bool? Faces { get; set; }
    public double FaceDenoise { get; set; } = 0.4;
    public bool? Finish { get; set; }
    public double Temperature { get; set; }
    public double Hue { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 5;
    public double Saturation { get; set; } = 3;
    public double Gamma { get; set; } = 1;
}

public partial class MainWindow
{
    Boosters CurrentBoosters
    {
        get
        {
            if (!settings.BoostersByFamily.TryGetValue(ModelFamily, out var b)) settings.BoostersByFamily[ModelFamily] = b = new Boosters();
            return b;
        }
    }

    static readonly Dictionary<string, bool> vpredCache = new();
    // NoobAI-style v-prediction checkpoints carry a "v_pred" marker tensor; ComfyUI reads the same key
    bool VPred => Sdxl && ModelBox.SelectedItem is string m && (vpredCache.TryGetValue(m, out var v) ? v : vpredCache[m] = HeaderHas(Path.Combine(ModelsDir, "checkpoints", m), "\"v_pred\""));

    static bool HeaderHas(string file, string text)
    {
        try
        {
            using var f = File.OpenRead(file);
            var len = new byte[8]; f.ReadExactly(len);
            long n = BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n <= 0 || n > 100_000_000) return false;
            var head = new byte[n]; f.ReadExactly(head);
            return Encoding.UTF8.GetString(head).Contains(text);
        }
        catch { return false; }
    }

    string FamilyLabelNow => Krea ? "Krea 2" : Qwen ? "Qwen-Image" : Anima ? "Anima" : Wan ? "Wan" : Flux ? "FLUX" : "this model";

    // what each booster does for the selected model: available, on, and the one-line why
    (bool Can, bool On, string Why) PagState()
    {
        var b = CurrentBoosters;
        if (!Sdxl || VideoOn) return (false, false, $"Off: PAG only changes SDXL-type models. On {FamilyLabelNow} it does nothing but still doubles the time.");
        bool on = b.Pag ?? false;
        return (true, on, on ? "Sharper structure and composition. Too high looks harsh; try a slightly lower CFG with it." : "Not on by default until it's been tested on this model.");
    }

    (bool Can, bool On, string Why) RescaleState()
    {
        var b = CurrentBoosters;
        if (CfgSlider.Value <= 1) return (false, false, "Off: at CFG 1 the negative pass is skipped, so there's nothing to rescale.");
        bool on = b.Rescale ?? VPred;
        return (true, on, b.Rescale == null && VPred ? "On for this v-prediction model: it prevents washed-out or burnt colours." : "Stops burnt or washed-out colours at higher CFG. Too high looks flat.");
    }

    (bool Can, bool On, string Why) FacesState()
    {
        var b = CurrentBoosters;
        if (VideoOn) return (false, false, "Pictures only.");
        if (!nodeNames.Contains("FaceDetailer") || !nodeNames.Contains("UltralyticsDetectorProvider"))
            return (false, false, "Needs the Impact Pack and its face finder (Settings > Plugins).");
        if (Anima) return (false, false, "Off: the face fixer currently crashes on Anima models (Impact Pack issue 1186).");
        bool on = b.Faces ?? false;
        return (true, on, Sdxl ? "Finds each face and redraws it at higher detail." : $"Finds each face and redraws it. Experimental on {FamilyLabelNow}.");
    }

    (bool Can, bool On, string Why) FinishState()
    {
        var b = CurrentBoosters;
        if (VideoOn) return (false, false, "Pictures only.");
        if (!nodeNames.Contains("ImageForgeFinish")) return (false, false, "Restart ComfyUI once to load Image Forge's colour finish.");
        bool on = b.Finish ?? false;
        return (true, on, "A last colour touch on the finished picture. Keep it subtle.");
    }

    // ---------------- the Advanced section ----------------
    bool fillingBoosters;

    void ShowBoosters()
    {
        if (BoostersPanel == null || ModelBox.SelectedItem == null) return;
        fillingBoosters = true;
        BoostersPanel.Children.Clear();
        var b = CurrentBoosters;
        BoostersPanel.Children.Add(BoosterRow("PAG: sharper structure", PagState(), v => b.Pag = v,
            [("Scale", 0, 10, 0.1, b.PagScale, x => b.PagScale = x)], () => { b.Pag = null; b.PagScale = 3.0; }));
        BoostersPanel.Children.Add(BoosterRow("RescaleCFG: no burnt colours", RescaleState(), v => b.Rescale = v,
            [("Strength", 0, 1, 0.05, b.RescaleMultiplier, x => b.RescaleMultiplier = x)], () => { b.Rescale = null; b.RescaleMultiplier = 0.7; }));
        var faces = FacesState();
        BoostersPanel.Children.Add(BoosterRow("Fix faces", faces, v => b.Faces = v,
            [("Strength", 0.1, 0.8, 0.05, b.FaceDenoise, x => b.FaceDenoise = x)], () => { b.Faces = null; b.FaceDenoise = 0.4; },
            faces.Why.StartsWith("Needs") ? () => ChoosePlugins_Click(null, null) : null));
        BoostersPanel.Children.Add(BoosterRow("Colour finish", FinishState(), v => b.Finish = v,
            [("Contrast", -50, 50, 1, b.Contrast, x => b.Contrast = x), ("Saturation", -50, 50, 1, b.Saturation, x => b.Saturation = x),
             ("Brightness", -50, 50, 1, b.Brightness, x => b.Brightness = x), ("Warmth", -50, 50, 1, b.Temperature, x => b.Temperature = x),
             ("Hue", -30, 30, 1, b.Hue, x => b.Hue = x), ("Gamma", 0.5, 1.5, 0.05, b.Gamma, x => b.Gamma = x)],
            () => { b.Finish = null; b.Temperature = b.Hue = b.Brightness = 0; b.Contrast = 5; b.Saturation = 3; b.Gamma = 1; }));
        fillingBoosters = false;
    }

    FrameworkElement BoosterRow(string title, (bool Can, bool On, string Why) state, Action<bool?> set,
        (string Label, double Min, double Max, double Step, double Value, Action<double> Set)[] sliders, Action reset, Action install = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var head = new DockPanel();
        var resetBtn = new Button { Style = (Style)FindResource("IconBtn"), Content = "", FontSize = 11, Width = 24, Height = 24, ToolTip = "Back to the recommendation" };
        System.Windows.Automation.AutomationProperties.SetName(resetBtn, "Reset " + title);
        resetBtn.Click += (_, _) => { reset(); BoostersChanged(); };
        DockPanel.SetDock(resetBtn, Dock.Right);
        head.Children.Add(resetBtn);
        if (install != null)
        {
            var get = new Button { Content = "Install", Style = (Style)FindResource("Ghost"), Height = 24, Padding = new Thickness(8, 0, 8, 0), FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
            get.Click += (_, _) => install();
            DockPanel.SetDock(get, Dock.Right);
            head.Children.Add(get);
        }
        var toggle = new CheckBox { Style = (Style)FindResource("Switch"), Content = title, IsChecked = state.On, IsEnabled = state.Can, FontSize = 13, Foreground = Brush("Ink") };
        System.Windows.Automation.AutomationProperties.SetName(toggle, title);
        // Checked/Unchecked rather than Click: screen readers and other UI Automation clients toggle without a click
        RoutedEventHandler flip = (_, _) => { if (fillingBoosters) return; set(toggle.IsChecked == true); Dispatcher.BeginInvoke(new Action(BoostersChanged)); };
        toggle.Checked += flip;
        toggle.Unchecked += flip;
        head.Children.Add(toggle);
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock { Text = state.Why, Style = (Style)FindResource("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (!state.On) return panel;
        foreach (var s in sliders)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var box = new TextBox { Width = 48, Height = 24, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center, Text = s.Value.ToString("0.##") };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{title} {s.Label}");
            DockPanel.SetDock(box, Dock.Right);
            var label = new TextBlock { Text = s.Label, Width = 74, Style = (Style)FindResource("Note"), VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = s.Min, Maximum = s.Max, Value = s.Value, TickFrequency = s.Step, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var set1 = s.Set;
            slider.ValueChanged += (_, e) => { box.Text = e.NewValue.ToString("0.##"); if (!fillingBoosters) { set1(e.NewValue); settings.Save(); } };
            void Typed() { if (double.TryParse(box.Text, out var v)) slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum); box.Text = slider.Value.ToString("0.##"); }
            box.LostFocus += (_, _) => Typed();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(); };
            row.Children.Add(box);
            row.Children.Add(label);
            row.Children.Add(slider);
            panel.Children.Add(row);
        }
        return panel;
    }

    void BoostersChanged() { settings.Save(); ShowBoosters(); }

    // ---------------- into the graph ----------------
    // model boosters wrap the sampler's model; Fix faces and the colour finish sit between decoding and saving (new pictures only)
    void ApplyBoosters(JsonObject g, bool newPicture)
    {
        if (g["3"]?["inputs"] is not JsonObject k) return;
        var b = CurrentBoosters;
        if (PagState().On)
        {
            g["pag"] = Node("PerturbedAttentionGuidance", new() { ["model"] = k["model"].DeepClone(), ["scale"] = b.PagScale });
            k["model"] = Link("pag", 0);
        }
        if (RescaleState().On)
        {
            g["rescale"] = Node("RescaleCFG", new() { ["model"] = k["model"].DeepClone(), ["multiplier"] = b.RescaleMultiplier });
            k["model"] = Link("rescale", 0);
        }
        if (!newPicture || g["9"]?["inputs"] is not JsonObject save) return;
        if (FacesState().On && g["6"]?["inputs"]?["clip"] is JsonNode clip && g["8"]?["inputs"]?["vae"] is JsonNode vae)
        {
            g["facefinder"] = Node("UltralyticsDetectorProvider", new() { ["model_name"] = "bbox/face_yolov8m.pt" });
            g["faces"] = Node("FaceDetailer", new()
            {
                ["image"] = save["images"].DeepClone(), ["model"] = k["model"].DeepClone(), ["clip"] = clip.DeepClone(), ["vae"] = vae.DeepClone(),
                ["positive"] = k["positive"].DeepClone(), ["negative"] = k["negative"].DeepClone(), ["bbox_detector"] = Link("facefinder", 0),
                ["seed"] = k["seed"].DeepClone(), ["steps"] = k["steps"].DeepClone(), ["cfg"] = k["cfg"].DeepClone(),
                ["sampler_name"] = k["sampler_name"].DeepClone(), ["scheduler"] = k["scheduler"].DeepClone(), ["denoise"] = b.FaceDenoise,
                ["guide_size"] = 768, ["guide_size_for"] = true, ["max_size"] = 1024, ["feather"] = 5, ["noise_mask"] = true, ["force_inpaint"] = true,
                ["bbox_threshold"] = 0.5, ["bbox_dilation"] = 10, ["bbox_crop_factor"] = 3.0, ["sam_detection_hint"] = "center-1", ["sam_dilation"] = 0,
                ["sam_threshold"] = 0.93, ["sam_bbox_expansion"] = 0, ["sam_mask_hint_threshold"] = 0.7, ["sam_mask_hint_use_negative"] = "False",
                ["drop_size"] = 10, ["wildcard"] = "", ["cycle"] = 1,
            });
            save["images"] = Link("faces", 0);
        }
        if (FinishState().On)
        {
            g["finish"] = Node("ImageForgeFinish", new()
            {
                ["image"] = save["images"].DeepClone(), ["temperature"] = b.Temperature, ["hue"] = b.Hue, ["brightness"] = b.Brightness,
                ["contrast"] = b.Contrast, ["saturation"] = b.Saturation, ["gamma"] = b.Gamma,
            });
            save["images"] = Link("finish", 0);
        }
    }

    // ---------------- Image Forge's own colour-finish node ----------------
    // a small node of ours (plain torch, no extra packages), written into ComfyUI's custom_nodes so the finish happens before the PNG
    // and its saved settings are written; the post-processing pack that has one needs OpenCV, which portable ComfyUI lacks
    const string FinishFolder = "ImageForge-Finish";
    const string FinishNode = """
        # Image Forge colour finish: brightness, contrast, saturation, warmth, hue and gamma, in plain torch.
        # Written by Image Forge; changes here are overwritten when Image Forge starts ComfyUI.
        import math
        import torch


        class ImageForgeFinish:
            @classmethod
            def INPUT_TYPES(cls):
                def f(default, lo, hi, step):
                    return ("FLOAT", {"default": default, "min": lo, "max": hi, "step": step})
                return {"required": {"image": ("IMAGE",), "temperature": f(0.0, -100.0, 100.0, 1.0), "hue": f(0.0, -90.0, 90.0, 1.0),
                                     "brightness": f(0.0, -100.0, 100.0, 1.0), "contrast": f(0.0, -100.0, 100.0, 1.0),
                                     "saturation": f(0.0, -100.0, 100.0, 1.0), "gamma": f(1.0, 0.2, 2.2, 0.05)}}

            RETURN_TYPES = ("IMAGE",)
            FUNCTION = "apply"
            CATEGORY = "image/postprocessing"

            def apply(self, image, temperature, hue, brightness, contrast, saturation, gamma):
                x = image[..., :3].clone()
                weights = torch.tensor([0.299, 0.587, 0.114], device=x.device, dtype=x.dtype)
                def luma(t):
                    return (t * weights).sum(-1, keepdim=True)
                if temperature:
                    x = x * torch.tensor([1 + temperature / 500, 1.0, 1 - temperature / 500], device=x.device, dtype=x.dtype)
                if hue:
                    c, s = math.cos(math.radians(hue)), math.sin(math.radians(hue))
                    yiq = torch.tensor([[0.299, 0.587, 0.114], [0.596, -0.274, -0.322], [0.211, -0.523, 0.312]], dtype=torch.float64)
                    turn = torch.tensor([[1, 0, 0], [0, c, -s], [0, s, c]], dtype=torch.float64)
                    m = (torch.linalg.inv(yiq) @ turn @ yiq).to(x.device, x.dtype)
                    x = x @ m.T
                if brightness:
                    x = x * (1 + brightness / 100)
                if contrast:
                    mean = luma(x).mean(dim=(1, 2), keepdim=True)
                    x = mean + (x - mean) * (1 + contrast / 100)
                if saturation:
                    grey = luma(x)
                    x = grey + (x - grey) * (1 + saturation / 100)
                x = x.clamp(0, 1)
                if gamma != 1:
                    x = x ** (1 / gamma)
                return (torch.cat([x, image[..., 3:]], -1) if image.shape[-1] > 3 else x,)


        NODE_CLASS_MAPPINGS = {"ImageForgeFinish": ImageForgeFinish}
        NODE_DISPLAY_NAME_MAPPINGS = {"ImageForgeFinish": "Image Forge colour finish"}
        """;

    void WriteFinishNode()
    {
        try
        {
            var dir = Path.Combine(CustomNodesDir, FinishFolder);
            var file = Path.Combine(dir, "__init__.py");
            if (File.Exists(file) && File.ReadAllText(file) == FinishNode) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, FinishNode);
        }
        catch { /* read-only ComfyUI folder: the finish stays unavailable */ }
    }
}
