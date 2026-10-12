// Ember: a Windows app for making images with the ComfyUI + SDXL on this PC.
// "Ember.exe" sits in the ComfyUI folder, starts ComfyUI hidden when needed,
// and stops it again on close so the GPU is free.
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageForge;

public partial class App : Application
{
    // the light palette swaps the shared brushes' colours before any window uses them (a theme change needs a restart)
    protected override void OnStartup(StartupEventArgs e)
    {
        // no window for a self-test: after Shutdown, StartupUri would still open one (loading and saving settings, starting ComfyUI)
        if (e.Args.Contains("--selftest")) Environment.Exit(ImageForge.MainWindow.SelfTest());
        if (Settings.Load().Theme == "light")
            foreach (var (key, hex) in new[] {
                ("Bg", "#F4F5F7"), ("Surface", "#FFFFFF"), ("Raised", "#F0F1F4"), ("Raised2", "#E3E6EB"), ("Field", "#FFFFFF"),
                ("Line", "#E0E3E8"), ("LineStrong", "#C9CED6"), ("Ink", "#16181D"), ("Soft", "#3A3E46"), ("Muted", "#666C77"),
                ("Faint", "#9AA0AA"), ("Accent", "#E08A2E"), ("AccentHover", "#EB9B47"), ("Danger", "#D4473A"), ("Ok", "#2E9E5E") })
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                if (Resources[key] is SolidColorBrush { IsFrozen: false } b) b.Color = color;
                else Resources[key] = new SolidColorBrush(color);
            }
        base.OnStartup(e);
    }
}

public class Settings
{
    public string Prompt { get; set; } = "";
    public string Negative { get; set; } = "text, letters, watermark, signature, blurry, low quality";
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Count { get; set; } = 2;
    public int Steps { get; set; } = 30;             // the Fast / Balanced / Best preset
    public int StepsValue { get; set; }              // the exact steps (0: the preset's)
    public bool CustomSize { get; set; }
    public int CustomWidth { get; set; } = 1024;
    public int CustomHeight { get; set; } = 1024;
    public double Cfg { get; set; } = 7;
    public string Model { get; set; } = "";
    public string Sampler { get; set; } = "dpmpp_2m";
    public string Scheduler { get; set; } = "karras";
    public long Seed { get; set; }
    public bool RandomSeed { get; set; } = true;
    public double Strength { get; set; } = 0.6;
    public HashSet<string> Favorites { get; set; } = new();     // file names in output/forge
    public int CardSize { get; set; } = 250;                     // gallery picture width to aim for (Ctrl+scroll)
    public string Style { get; set; } = "none";
    public List<string> History { get; set; } = new();          // recent prompts, newest first
    public double WinLeft { get; set; }
    public double WinTop { get; set; }
    public double WinWidth { get; set; }
    public double WinHeight { get; set; }
    public bool WinMax { get; set; }
    public string Lora { get; set; } = "";
    public double LoraStrength { get; set; } = 0.8;
    public List<LoraItemSetting> SavedLoras { get; set; } = new();
    public Dictionary<string, List<LoraItemSetting>> LorasByFamily { get; set; } = new();   // "krea2", "qwen_image", "wan", "sdxl" -> its rows
    public bool InpaintEdits { get; set; } = true;               // painted edits and expansions use the inpainting model, if installed
    public bool SharpPreviewAll { get; set; }                    // sharp live preview on every model (Qwen and Krea 2 always get it)
    public double StyleStrength { get; set; } = 0.4;             // how strongly a style picture's look carries over (IPAdapter weight)
    public string Theme { get; set; } = "dark";
    public bool KeepComfyRunning { get; set; }
    public string OutputDir { get; set; } = "";                  // empty: ComfyUI's own output folder
    public string CivitaiKey { get; set; } = "";                 // encrypted for this Windows user (DPAPI), base64
    public string VideoModel { get; set; } = "";
    public string VideoSize { get; set; } = "832x480";
    public int VideoFrames { get; set; } = 49;
    public string ComfyDir { get; set; } = "";
    public bool FastStart { get; set; } = true;                  // ComfyUI loads only the recommended plugins Ember uses
    public bool HidePluginBanner { get; set; }
    public double PromptTextSize { get; set; } = 14.5;
    public string HelperModel { get; set; } = "";
    public double ReferenceStyle { get; set; } = 0.35;     // how much a non-Qwen model redraws a reference picture
    public bool CkAttention { get; set; }
    public bool Fp16Accumulation { get; set; }
    public double ReserveVram { get; set; }
    public int CacheRam { get; set; }                // "": Qwen3-VL in ComfyUI; "Ollama: <name>" for a local Ollama model
    public Dictionary<string, Boosters> BoostersByFamily { get; set; } = new();   // before per-model profiles: the starting point for models without one
    public Dictionary<string, ModelProfile> Profiles { get; set; } = new();        // each model's own settings
    public List<ModelProfile> Tabs { get; set; } = new();
    public string ActiveTab { get; set; }                   // the ComfyUI portable folder, when the exe doesn't sit in one

    // IMAGEFORGE_SETTINGS: another folder for settings, LoRA info and recommendations (test copies leave the real ones alone)
    static readonly string File = Path.Combine(Environment.GetEnvironmentVariable("IMAGEFORGE_SETTINGS") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageForge"), "settings.json");
    public static string FilePath => File;
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings(); }
        catch { return new Settings(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(File)); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this)); }
        catch { /* settings are a convenience */ }
    }
}

public class LoraItemSetting
{
    public string Name { get; set; } = "None";
    public double Strength { get; set; } = 0.8;
}

// What ComfyUI recorded inside the PNG
public record Meta(string Prompt, string Negative, long? Seed, long? Steps, double? Cfg, string Sampler, string Scheduler, string Model, string Upscaled = null, string From = null, string Loras = null);

// A look added around the user's prompt; {p} is where the prompt goes. Previews: styles\<Key>.jpg (same fox, same seed).
public record StylePreset(string Key, string Label, string Template, string Negative, string Lora = null)
{
    public string Apply(string prompt) => Template.Replace("{p}", prompt);

    // anime models only draw: a style's negatives against drawn looks would fight the model itself, so they're left out there
    static readonly string[] Drawn = { "anime", "cartoon", "drawing", "illustration", "painting", "sketch" };
    public string NegativeFor(bool anime) => anime ? string.Join(", ", Negative.Split(", ").Where(w => !Drawn.Contains(w))) : Negative;

    public static readonly StylePreset[] All =
    {
        new("none", "None", "{p}", ""),
        new("photo", "Photo", "professional photograph of {p}, 35mm, natural lighting, sharp focus, highly detailed, realistic", "drawing, painting, illustration, cartoon, anime, 3d render"),
        new("cinematic", "Cinematic", "cinematic movie still of {p}, dramatic low-key lighting, volumetric fog, god rays, anamorphic widescreen, moody color grade", "anime, cartoon, drawing, painting, sketch, flat lighting, bright"),
        new("anime", "Anime", "anime artwork of {p}, anime style, key visual, vibrant colors, studio anime, highly detailed", "photo, realistic, 3d render"),
        new("digital", "Digital art", "digital painting of {p}, artstation concept art, bold vivid colors, visible brush strokes, stylized illustration", "photo, photorealistic, realism, 35mm"),
        new("fantasy", "Fantasy", "fantasy storybook illustration of {p}, glowing magical particles, bioluminescent plants, dreamy purple and teal light, enchanted", "photo, realistic, mundane, plain"),
        new("oil", "Oil paint", "oil painting of {p}, thick brush strokes, impasto, canvas texture, rich colors, fine art", "photo, 3d render, anime"),
        new("watercolor", "Watercolor", "watercolor painting of {p}, soft washes, paper texture, delicate, flowing colors", "photo, 3d render, dark"),
        new("sketch", "Sketch", "pencil sketch of {p}, graphite, detailed linework, cross hatching, monochrome, on paper", "color, photo, 3d render"),
        new("pixel", "Pixel art", "pixel art, {p}, 16-bit retro game art, crisp pixels, limited palette", "blurry, photo, realistic, 3d render, smooth shading", "pixel-art-xl.safetensors"),
        new("comic", "Comic", "comic book illustration of {p}, bold ink outlines, halftone shading, flat vibrant colors, graphic novel", "photo, realistic, 3d render, blurry"),
        new("render3d", "3D", "3d render of {p}, cute stylized pixar style character, soft clay materials, octane render, soft studio lighting", "photo, realistic fur, drawing, painting, sketch"),
        new("logo", "Logo", "minimalist flat vector logo of {p}, simple bold shapes, clean lines, centered, plain background, app icon", "photo, realistic, 3d, clutter, mockup"),
    };

    // a saved image's prompt -> the user's own words and the style that wrapped them
    public static (string Prompt, string Negative, StylePreset Style) Unwrap(string prompt, string negative)
    {
        foreach (var st in All.Skip(1))
        {
            var parts = st.Template.Split("{p}");
            if (prompt.Length >= parts[0].Length + parts[1].Length && prompt.StartsWith(parts[0]) && prompt.EndsWith(parts[1]))
            {
                var neg = negative ?? "";
                foreach (var n in new[] { st.Negative, st.NegativeFor(true) }.Where(n => n != ""))
                {
                    if (neg.EndsWith(", " + n)) { neg = neg[..^(n.Length + 2)]; break; }
                    if (neg == n) { neg = ""; break; }
                }
                return (prompt[parts[0].Length..^parts[1].Length], neg, st);
            }
        }
        return (prompt, negative, All[0]);
    }
}

// One gallery card: a finished image, or a placeholder while it is being made
public class Shot : INotifyPropertyChanged
{
    public string FilePath { get; init; }
    public ImageSource Thumb { get; init; }
    public Meta Meta { get; init; }
    public bool Pending { get; init; }
    public DateTime Made { get; init; } = DateTime.UtcNow;    // when this card was made from its file
    public string Caption => Meta?.Prompt ?? Path.GetFileName(FilePath);
    // from the file name ComfyUI was given: img_ (prompt), edit_ (edits, variations, expansions), up_ (upscales), cut_ (cut-outs), vid_ (a clip's poster)
    public string Kind => Path.GetFileName(FilePath ?? "") switch
    {
        var n when n.StartsWith("edit_") || n.StartsWith("cut_") => "edited",
        var n when n.StartsWith("up_") => "upscaled",
        var n when n.StartsWith("vid_") => "video",
        _ => "created",
    };
    public bool IsVideo => Kind == "video";
    public bool Secret => FilePath?.EndsWith(".locked") == true;     // encrypted, in output/forge/secret
    public bool Plain => !Secret;                                      // safe to send to ComfyUI (edit, cut out)
    public string SecretLabel => Secret ? "Move out of secret gallery" : "Move to secret gallery";
    public DateTime Date { get; init; } = DateTime.Now;               // the file's time: the gallery sorts and groups by it
    public string Day => (DateTime.Today - Date.Date).Days switch
    {
        <= 0 => "Today",
        1 => "Yesterday",
        < 7 => Date.ToString("dddd"),
        _ => Date.ToString(Date.Year == DateTime.Today.Year ? "MMMM" : "MMMM yyyy"),
    };
    public string VideoPath => Path.Combine(Path.GetDirectoryName(FilePath), "clips", Path.ChangeExtension(Path.GetFileName(FilePath), ".mp4"));
    bool selected, selecting;
    public bool Selected { get => selected; set { selected = value; Changed(nameof(Selected)); } }
    public bool Selecting { get => selecting; set { selecting = value; Changed(nameof(Selecting)); } }
    string progress = "";
    public string Progress { get => progress; set { progress = value; Changed(nameof(Progress)); } }
    double progressValue;
    public double ProgressValue { get => progressValue; set { progressValue = value; Changed(nameof(ProgressValue)); } }    // 0 to 1, for the bars
    ImageSource preview;
    public ImageSource Preview { get => preview; set { preview = value; Changed(nameof(Preview)); } }     // ComfyUI's live preview while it's being made
    bool favorite;
    public bool Favorite { get => favorite; set { favorite = value; Changed(nameof(Favorite)); Changed(nameof(FavoriteLabel)); } }
    public string FavoriteLabel => favorite ? "Remove from favourites" : "Add to favourites";
    void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler PropertyChanged;
}

public partial class MainWindow : Window
{
    // a second copy (for example a test) can run its own ComfyUI next to the usual one
    static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("IMAGEFORGE_PORT"), out var port) ? port : 8188;
    static readonly string Api = $"http://127.0.0.1:{Port}";
    static string Root = AppContext.BaseDirectory;                          // the ComfyUI portable folder: the exe's own, or the one set up from the app
    string Output => string.IsNullOrWhiteSpace(settings.OutputDir) ? Path.Combine(Root, "ComfyUI", "output") : settings.OutputDir;
    static readonly Dictionary<string, string> Stages = new() { ["4"] = "Loading model", ["11"] = "Reading photo", ["3"] = "Painting", ["8"] = "Decoding", ["15"] = "Blending", ["9"] = "Saving", ["20"] = "Reading image", ["22"] = "Upscaling", ["23"] = "Resizing", ["40"] = "Making the video", ["41"] = "Saving the video" };

    readonly HttpClient http = new() { BaseAddress = new Uri(Api), Timeout = TimeSpan.FromSeconds(30) };
    readonly string clientId = Guid.NewGuid().ToString("N");
    readonly Settings settings = Settings.Load();
    readonly ObservableCollection<Shot> shots = new();
    Process server;                     // ComfyUI, when this app started it
    bool ready, closing, restarting;
    DateTime stepTime;
    int stepFrom;
    Shot viewing;
    BitmapSource photo;                 // edit mode: the photo, resized for SDXL
    readonly ListCollectionView galleryView;   // the gallery's own view, so its filter doesn't touch the editor's strip

    class LoraRow
    {
        public Border Container;
        public TextBlock TitleText;
        public ComboBox Box;
        public Slider Slider;
        public TextBox ValueBox;
        public Button RemoveBtn;
        public TextBlock Info;          // what the picked LoRA does, and its trigger words
        public Button TriggerBtn;
        public Button FixBtn;           // Convert, for files ComfyUI can't read
    }
    readonly List<LoraRow> loraRows = new();

    public MainWindow()
    {
        InitializeComponent();
        if (!IsComfy(Root) && FindComfy(settings.ComfyDir) is string comfy) Root = comfy;
        galleryView = new ListCollectionView(shots) { Filter = o => InView((Shot)o), CustomSort = Comparer<Shot>.Create((a, b) => b.Date.CompareTo(a.Date)) };
        galleryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Shot.Day)));
        Gallery.ItemsSource = galleryView;
        Strip.ItemsSource = new ListCollectionView(shots) { Filter = o => !((Shot)o).Secret };
        shots.CollectionChanged += (_, _) => UpdateCount();
        BuildStyleTiles();
        ApplySettings();
        ApplyPromptSize();
        ShowTabs();
        AllView.IsChecked = true;
        CreateMode.IsChecked = true;
        BrushTool.IsChecked = true;
        Ink.StrokeCollected += (_, e) => marks.Add(e.Stroke);
        SetState("Starting", Brush("Accent"));
        RestoreWindow();
        EditorView.SizeChanged += (_, _) => LayoutCrop();
        CfgSlider.ValueChanged += (_, _) => { ShowBoosters(); RefreshGuard(); UpdateNegative(); };
        SamplerBox.SelectionChanged += (_, _) => RefreshGuard();
        SchedulerBox.SelectionChanged += (_, _) => RefreshGuard();
        NegativeBox.TextChanged += (_, _) => RefreshGuard();
        QualityGroup.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => { StepsSlider.Value = StepsFor(Picked(QualityGroup)); RefreshGuard(); }));
        StepsSlider.ValueChanged += (_, e) => { StepsBox.Text = ((int)e.NewValue).ToString(); RefreshGuard(); };
        StepsBox.LostFocus += (_, _) => TypedSteps();
        StepsBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TypedSteps(); };
        StepsBox.Text = Steps.ToString();
        Loaded += async (_, _) =>
        {
            PromptBox.TextChanged += (_, _) => UpdateLoraChip();
            UpdateLoraChip();
            // a photo dropped on the shortcut / "Open with" opens straight in the editor;
            // --adopt <pid>: a restarted copy takes over the ComfyUI its predecessor started (and stops it on close)
            var args = Environment.GetCommandLineArgs();
            int adopt = Array.IndexOf(args, "--adopt");
            if (adopt > 0 && adopt + 1 < args.Length && int.TryParse(args[adopt + 1], out var pid))
                try { server = Process.GetProcessById(pid); } catch { }
            if (args.Length > 1 && File.Exists(args[1])) { EditMode.IsChecked = true; LoadPhoto(args[1]); }
            _ = LoadGallery();
            _ = CheckForUpdate();
            await StartUp();
        };
        Closing += (_, _) =>
        {
            closing = true; RememberWindow(); SaveSettings();
            if (!settings.KeepComfyRunning && !restarting) { AskAboutOtherComfy(); StopServer(); }
            ApplyUpdate();
        };
    }

    // using a ComfyUI this window didn't start (left running before, or opened from its own .bat): ask rather than leave it holding memory
    void AskAboutOtherComfy()
    {
        if (server != null || !ready) return;
        var other = Task.Run(ComfyOnPort).Result;           // off the UI thread, which its awaits would otherwise wait for
        if (other != null && MessageBox.Show(this, "ComfyUI was already running when Ember opened, so this window didn't start it.\n\nClose ComfyUI too?",
                "Ember", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            server = other;
    }

    // reopen where it was closed, unless that spot is no longer on any screen
    void RestoreWindow()
    {
        if (settings.WinWidth < MinWidth || settings.WinHeight < MinHeight) return;
        var spot = new Rect(settings.WinLeft, settings.WinTop, settings.WinWidth, settings.WinHeight);
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screens.Contains(new Point(spot.Left + 100, spot.Top + 20))) return;     // the title bar must be reachable
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = spot.Left; Top = spot.Top; Width = spot.Width; Height = spot.Height;
        if (settings.WinMax) WindowState = WindowState.Maximized;
    }

    void RememberWindow()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        (settings.WinLeft, settings.WinTop, settings.WinWidth, settings.WinHeight) = (b.Left, b.Top, b.Width, b.Height);
        settings.WinMax = WindowState == WindowState.Maximized;
    }

    // taskbar button flashes when an image finishes while you're in another window
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
    struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }
    void Flash()
    {
        if (IsActive) return;
        var f = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = new WindowInteropHelper(this).Handle, Flags = 3 | 12 };   // all, until focused
        FlashWindowEx(ref f);
    }

    // dark title bar that blends into the header (Windows 11; ignored elsewhere)
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;     // COLORREF is 0x00BBGGRR
        int dark = settings.Theme == "light" ? 0 : 1, caption = ColorRef(Brush("Surface").Color), text = ColorRef(Brush("Muted").Color);
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);
    }

    static SolidColorBrush Brush(string key) => (Application.Current?.TryFindResource(key) as SolidColorBrush) ?? Brushes.Transparent;

    void SetState(string text, SolidColorBrush c)
    {
        PillText.Text = text; PillText.Foreground = c; PillDot.Fill = c;
        Pill.Background = new SolidColorBrush(Color.FromArgb(0x1A, c.Color.R, c.Color.G, c.Color.B));
        Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, c.Color.R, c.Color.G, c.Color.B));
    }

    void Note(string text, bool error = false)
    {
        Hint.Text = text;
        Hint.Foreground = Brush(error ? "Danger" : "Muted");
        Hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void Stage(string text, string eta) { StageText.Text = text; EtaText.Text = eta; }

    // ---------------- settings <-> controls ----------------
    void ApplySettings()
    {
        PromptBox.Text = settings.Prompt;
        NegativeBox.Text = settings.Negative;
        Pick(SizeGroup, $"{settings.Width}x{settings.Height}", "1024x1024");
        Pick(VideoSizeGroup, settings.VideoSize, "832x480");
        Pick(LengthGroup, settings.VideoFrames.ToString(), "49");
        Pick(CountGroup, settings.Count.ToString(), "2");
        Pick(QualityGroup, settings.Steps.ToString(), "30");
        if (settings.StepsValue > 0) StepsSlider.Value = settings.StepsValue;
        CustomW.Text = settings.CustomWidth.ToString(); CustomH.Text = settings.CustomHeight.ToString();
        CustomSizeBox.IsChecked = settings.CustomSize;
        Pick(StyleGroup, settings.Style, "none");
        CfgSlider.Value = settings.Cfg;
        SeedBox.Text = settings.Seed.ToString();
        RandomSeed.IsChecked = settings.RandomSeed;
        StrengthSlider.Value = settings.Strength;

        LoraRowsPanel.Children.Clear();
        loraRows.Clear();
        if (settings.SavedLoras != null && settings.SavedLoras.Count > 0)
        {
            foreach (var s in settings.SavedLoras)
                CreateLoraRow(s.Name, s.Strength);
        }
        else if (!string.IsNullOrEmpty(settings.Lora) && settings.Lora != "None")
        {
            CreateLoraRow(settings.Lora, settings.LoraStrength);
        }
    }

    static void Pick(Panel group, string tag, string fallback)
    {
        var all = group.Children.OfType<RadioButton>().ToList();
        (all.FirstOrDefault(r => (string)r.Tag == tag) ?? all.First(r => (string)r.Tag == fallback)).IsChecked = true;
    }
    static string Picked(Panel group) => (string)(group?.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag ?? "");

    void SaveSettings()
    {
        if (profileModel != null && !loadingOptions && ModelBox.SelectedItem as string == profileModel) Capture(ProfileOf(profileModel));
        SaveActiveTab();
        var size = Picked(SizeGroup).Split('x');
        settings.Prompt = PromptBox.Text; settings.Negative = NegativeBox.Text;
        settings.Width = int.Parse(size[0]); settings.Height = int.Parse(size[1]);
        settings.Count = int.Parse(Picked(CountGroup)); settings.Steps = int.Parse(Picked(QualityGroup)); settings.StepsValue = Steps;
        settings.CustomSize = CustomSizeBox.IsChecked == true;
        if (int.TryParse(CustomW.Text, out var cw)) settings.CustomWidth = cw;
        if (int.TryParse(CustomH.Text, out var ch)) settings.CustomHeight = ch;
        settings.Cfg = CfgSlider.Value;
        if (ModelBox.SelectedItem is string model) { if (showingVideoModels) settings.VideoModel = model; else settings.Model = model; }
        settings.VideoSize = Picked(VideoSizeGroup); settings.VideoFrames = int.Parse(Picked(LengthGroup));
        settings.Sampler = SamplerBox.SelectedItem as string ?? settings.Sampler;
        settings.Scheduler = SchedulerBox.SelectedItem as string ?? settings.Scheduler;
        if (long.TryParse(SeedBox.Text, out var seed)) settings.Seed = seed;
        settings.RandomSeed = RandomSeed.IsChecked == true;
        settings.Strength = StrengthSlider.Value;
        settings.Style = Picked(StyleGroup);

        settings.SavedLoras = loraRows
            .Where(r => r.Box?.SelectedItem is string s && s != "None")
            .Select(r => new LoraItemSetting
            {
                Name = r.Box.SelectedItem as string,
                Strength = r.Slider?.Value ?? 0.8
            })
            .ToList();

        if (loraFamilyShown != null) settings.LorasByFamily[loraFamilyShown] = settings.SavedLoras;
        var firstActive = settings.SavedLoras.FirstOrDefault();
        settings.Lora = firstActive?.Name ?? "";
        settings.LoraStrength = firstActive?.Strength ?? 0.8;

        settings.Save();
    }

    // a picked LoRA changes every image, so it shows next to Generate with a one-click off
    void UpdateLoraChip()
    {
        Dispatcher.BeginInvoke(new Action(RefreshGuard));
        if (LoraChip == null || PromptBox == null) return;
        List<(string Name, double Strength)> active;
        try { active = AllActiveLoras(PromptBox?.Text, out _); } catch { return; }
        bool on = active != null && active.Count > 0;
        LoraChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            if (active.Count == 1)
                LoraChipText.Text = $"LoRA on: {Path.GetFileNameWithoutExtension(active[0].Name)} ({active[0].Strength:0.00})";
            else
                LoraChipText.Text = $"{active.Count} LoRAs active: {string.Join(", ", active.Select(a => $"{Path.GetFileNameWithoutExtension(a.Name)} ({a.Strength:0.00})"))}";
        }
    }
    void Lora_Changed(object sender, EventArgs e) => UpdateLoraChip();
    void LoraOff_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in loraRows)
        {
            if (r.Box != null) r.Box.SelectedItem = "None";
        }
        if (PromptBox != null && PromptBox.Text.Contains("<lora:"))
        {
            PromptBox.Text = Regex.Replace(PromptBox.Text, @"\s*<lora:[^>]+>", "");
        }
        SaveSettings();
        UpdateLoraChip();
    }

    void AddLoraToPrompt_Click(object sender, RoutedEventArgs e)
    {
        var first = loraRows.FirstOrDefault(r => r.Box?.SelectedItem is string l && l != "None");
        if (first == null) { Note("Pick a LoRA first, then click Add.", true); return; }
        string l = first.Box.SelectedItem as string;
        double strength = Math.Round(first.Slider?.Value ?? 0.8, 2);
        string shortName = Path.GetFileNameWithoutExtension(l);
        string tag = $" <lora:{shortName}:{strength:0.00}> ";
        int idx = PromptBox.CaretIndex;
        if (idx >= 0 && idx <= PromptBox.Text.Length)
        {
            PromptBox.Text = PromptBox.Text.Insert(idx, tag);
            PromptBox.CaretIndex = idx + tag.Length;
        }
        else
        {
            PromptBox.Text += tag;
            PromptBox.CaretIndex = PromptBox.Text.Length;
        }
        PromptBox.Focus();
        UpdateLoraChip();
        Note($"Added <lora:{shortName}:{strength:0.00}> to prompt.");
    }

    void AddLoraRow_Click(object sender, RoutedEventArgs e)
    {
        CreateLoraRow("None", 0.8);
    }

    LoraRow CreateLoraRow(string loraName = "None", double strength = 0.8)
    {
        var row = new LoraRow();
        var card = new Border
        {
            Background = Brush("Raised"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("Line")
        };

        var sp = new StackPanel();

        var topDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var removeBtn = new Button
        {
            Content = "✕",
            Style = TryFindResource("Ghost") as Style,
            Padding = new Thickness(6, 1, 6, 1),
            FontSize = 11,
            ToolTip = "Remove this LoRA",
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(removeBtn, Dock.Right);

        var valBox = new TextBox
        {
            Text = strength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Width = 42,
            Height = 22,
            FontSize = 11,
            Padding = new Thickness(2, 1, 2, 1),
            TextAlignment = TextAlignment.Right,
            Background = Brush("Field"),
            Foreground = Brush("Ink"),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("Line"),
            ToolTip = "Type exact strength or drag slider (−4.0 to 4.0)",
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(valBox, Dock.Right);

        var titleBlock = new TextBlock
        {
            Text = $"LoRA {loraRows.Count + 1}",
            Style = TryFindResource("Note") as Style,
            Foreground = Brush("Soft"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };

        topDock.Children.Add(removeBtn);
        topDock.Children.Add(valBox);
        topDock.Children.Add(titleBlock);

        var combo = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 6)
        };
        var items = new[] { "None" }.Concat(LorasForModel).ToList();
        if (loras.Count == 0 && !string.IsNullOrEmpty(loraName) && loraName != "None" && !items.Contains(loraName))
            items.Add(loraName);
        combo.ItemsSource = items;
        combo.SelectedItem = items.Contains(loraName) ? loraName : "None";

        var slider = new Slider
        {
            Minimum = -4.0,
            Maximum = 4.0,
            Value = Math.Clamp(strength, -4.0, 4.0),
            TickFrequency = 0.05,
            IsMoveToPointEnabled = true,
            Margin = new Thickness(0, 0, 0, 2)
        };

        bool updatingVal = false;
        slider.ValueChanged += (_, _) =>
        {
            if (updatingVal) return;
            updatingVal = true;
            valBox.Text = slider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            updatingVal = false;
            UpdateLoraChip();
        };

        valBox.TextChanged += (_, _) =>
        {
            if (updatingVal) return;
            if (double.TryParse(valBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                updatingVal = true;
                slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
                updatingVal = false;
                UpdateLoraChip();
            }
        };

        combo.SelectionChanged += (_, _) =>
        {
            UpdateLoraTitles();
            UpdateLoraChip();
            if (ready) SaveSettings();
        };

        removeBtn.Click += (_, _) =>
        {
            RemoveLoraRow(row);
        };

        var info = new TextBlock
        {
            Style = TryFindResource("Note") as Style, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 52, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed,
        };
        var triggerBtn = new Button
        {
            Content = "Use trigger words", Style = TryFindResource("Ghost") as Style, Padding = new Thickness(8, 2, 8, 2), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed,
            ToolTip = "Add this LoRA's trigger words to the prompt: most LoRAs only kick in with them",
        };
        System.Windows.Automation.AutomationProperties.SetName(triggerBtn, "Use trigger words");
        triggerBtn.Click += (_, _) => AddTriggerWords(row);
        var fixBtn = new Button
        {
            Content = "Convert for ComfyUI", Style = TryFindResource("Ghost") as Style, Padding = new Thickness(8, 2, 8, 2), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed,
            ToolTip = "Make a copy with layer names ComfyUI can read; the original stays",
        };
        System.Windows.Automation.AutomationProperties.SetName(fixBtn, "Convert LoRA for ComfyUI");
        fixBtn.Click += (_, _) => _ = ConvertLoraRow(row);
        combo.SelectionChanged += (_, _) =>
        {
            ShowLoraInfo(row);
            if (RecommendedStrength(combo.SelectedItem as string) is double rs) slider.Value = rs;   // a fresh pick starts at what its page recommends
        };

        sp.Children.Add(topDock);
        sp.Children.Add(combo);
        sp.Children.Add(info);
        sp.Children.Add(triggerBtn);
        sp.Children.Add(fixBtn);
        sp.Children.Add(slider);

        card.Child = sp;

        row.Container = card;
        row.TitleText = titleBlock;
        row.Box = combo;
        row.Slider = slider;
        row.ValueBox = valBox;
        row.RemoveBtn = removeBtn;
        row.Info = info;
        row.TriggerBtn = triggerBtn;
        row.FixBtn = fixBtn;
        ShowLoraInfo(row);

        loraRows.Add(row);
        LoraRowsPanel.Children.Add(card);

        UpdateLoraTitles();
        if (ready) UpdateLoraChip();
        return row;
    }

    void RemoveLoraRow(LoraRow row)
    {
        LoraRowsPanel.Children.Remove(row.Container);
        loraRows.Remove(row);       // the last one too: no LoRAs is fine, and + Add LoRA brings a row back
        UpdateLoraTitles();
        UpdateLoraChip();
        if (ready) SaveSettings();
    }

    void UpdateLoraTitles()
    {
        for (int i = 0; i < loraRows.Count; i++)
        {
            var r = loraRows[i];
            // names for screen readers (and UI tests): the rows are built in code, so nothing else names them
            if (r.Box != null) System.Windows.Automation.AutomationProperties.SetName(r.Box, $"LoRA {i + 1}");
            if (r.Slider != null) System.Windows.Automation.AutomationProperties.SetName(r.Slider, $"LoRA {i + 1} strength");
            if (r.ValueBox != null) System.Windows.Automation.AutomationProperties.SetName(r.ValueBox, $"LoRA {i + 1} strength value");
            if (r.RemoveBtn != null) System.Windows.Automation.AutomationProperties.SetName(r.RemoveBtn, $"Remove LoRA {i + 1}");
            if (r.TitleText != null)
            {
                var sel = r.Box?.SelectedItem as string;
                if (!string.IsNullOrEmpty(sel) && sel != "None")
                    r.TitleText.Text = $"LoRA {i + 1}: {Path.GetFileNameWithoutExtension(sel)}";
                else
                    r.TitleText.Text = $"LoRA {i + 1}";
            }
        }
    }

    void RefreshStyleTiles()
    {
        foreach (var tile in StyleGroup.Children.OfType<RadioButton>())
        {
            var st = StylePreset.All.First(s => s.Key == (string)tile.Tag);
            if (st.Lora == null) continue;
            tile.IsEnabled = loras.Contains(st.Lora);
            tile.ToolTip = tile.IsEnabled ? st.Apply("…") : $"Needs {st.Lora} in ComfyUI\\models\\loras";
        }
    }

    StylePreset CurrentStyle => StylePreset.All.FirstOrDefault(s => s.Key == Picked(StyleGroup)) ?? StylePreset.All[0];

    void BuildStyleTiles()
    {
        foreach (var st in StylePreset.All)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("StyleTile"), GroupName = "style", Tag = st.Key, Content = st.Label,
                DataContext = new BitmapImage(new Uri($"pack://application:,,,/styles/{st.Key}.jpg")),
                ToolTip = st.Key == "none" ? "No style: only your prompt" : st.Apply("…"),
            };
            tile.Checked += (_, _) =>
            {
                StyleText.Text = st.Label;
                if (st.Key == "logo" && ModelBox.SelectedItem != null && Sdxl)
                    Note("Tip: for lettering in a logo, pick Qwen-Image 2.1 in Advanced settings; this model can't spell.");
            };
            if (st.Lora != null) tile.IsEnabled = false;          // until RefreshStyleTiles finds its LoRA
            StyleGroup.Children.Add(tile);
        }
    }

    // "Use prompt" on a styled image gives back the plain words and re-selects the style, so it isn't applied twice
    void UsePromptOf(Meta m)
    {
        if (m?.Prompt == null) return;
        var saved = m.Negative;                                  // Anima's own quality negative is added again when it's made
        if (saved != null && (saved == AnimaNegative || saved.EndsWith(", " + AnimaNegative))) saved = saved[..Math.Max(0, saved.Length - AnimaNegative.Length - 2)];
        var (prompt, negative, style) = StylePreset.Unwrap(m.Prompt, saved);
        PromptBox.Text = prompt;
        if (negative != null) NegativeBox.Text = negative;
        Pick(StyleGroup, style.Key, "none");
    }

    void Size_Checked(object sender, RoutedEventArgs e) => ShowSize();

    // the picture's size: an aspect preset, or the custom one rounded to what the model takes (SDXL works in 64s, the others in 32s)
    (int W, int H) PictureSize()
    {
        if (CustomSizeBox?.IsChecked == true && int.TryParse(CustomW.Text, out var w) && int.TryParse(CustomH.Text, out var h))
        {
            int step = Sdxl ? 64 : 32;
            int Snap(int v) => Math.Clamp((int)Math.Round(v / (double)step) * step, 256, 4096);
            return (Snap(w), Snap(h));
        }
        var preset = (Picked(SizeGroup) is { Length: > 0 } tag ? tag : "1024x1024").Split('x');
        return (int.Parse(preset[0]), int.Parse(preset[1]));
    }

    void CustomSize_Changed(object sender, RoutedEventArgs e)
    {
        CustomSizePanel.Visibility = CustomSizeBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (CustomSizeBox.IsChecked == true) { var (w, h) = PictureSize(); CustomW.Text = w.ToString(); CustomH.Text = h.ToString(); }
        ShowSize();
        RefreshGuard();
    }

    void ShowSize() { if (SizeText == null) return; var (w, h) = PictureSize(); SizeText.Text = $"{w} × {h}"; }

    // typing a seed means "use this one"
    void Seed_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
        if (!e.Handled) RandomSeed.IsChecked = false;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((SettingsPanel.IsVisible || ModelsPanel.IsVisible) && e.Key == Key.Escape) { CloseOverlays_Click(null, null); e.Handled = true; return; }
        if (LivePanel.IsVisible && e.Key == Key.Escape) { CloseLive(); e.Handled = true; return; }
        if (ViewerPanel.IsVisible)
        {
            if (e.Key == Key.Escape) CloseViewer();
            else if (e.Key == Key.Left) Step(-1);
            else if (e.Key == Key.Right) Step(1);
            else if (e.Key == Key.Delete) DeleteViewed_Click(null, null);
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None) ToggleFavorite(viewing);
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.None) Compare_Click(null, null);
            else return;
            e.Handled = true;
        }
        else if (selectMode && e.Key == Key.Escape) { SetSelectMode(false); e.Handled = true; }
        else if (selectMode && e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { SelectAll_Click(null, null); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = Generate();
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            if (ReferenceSection.IsKeyboardFocusWithin && ClipboardPicture() != null) { PasteReference(); e.Handled = true; }      // after clicking Add or Paste there
            else if (PastePhoto()) { EditMode.IsChecked = true; e.Handled = true; }
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && EditorPanel.IsVisible && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            Undo_Click(null, null);
        }
    }

    // ---------------- ComfyUI ----------------
    async Task<bool> Alive()
    {
        try { using var r = await http.GetAsync("/system_stats"); return r.IsSuccessStatusCode; } catch { return false; }
    }

    bool listening;

    void Offline(string why)
    {
        SetState("Offline", Brush("Danger"));
        if (why != null) Note(why, true);
        RestartPanel.Visibility = Visibility.Visible;
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        RestartPanel.Visibility = Visibility.Collapsed;
        ready = false;
        GenerateBtn.IsEnabled = false;
        SetState("Starting", Brush("Accent"));
        StopServer();                           // ours, if it is hung rather than gone
        await StartUp();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var log = Path.Combine(Root, "comfy_server.log");
        if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    bool startingServer;        // while ComfyUI (re)starts, a quiet websocket isn't "Offline"

    async Task StartUp()
    {
        startingServer = true;
        try { await StartComfy(); }
        finally { startingServer = false; }
    }

    async Task StartComfy()
    {
        if (!await Alive())
        {
            var python = Path.Combine(Root, "python_embeded", "python.exe");
            if (!File.Exists(python) || !File.Exists(Path.Combine(Root, "ComfyUI", "main.py")))
            {
                SetState("Not set up", Brush("Danger"));
                Note("ComfyUI isn't set up yet. Press Set up ComfyUI and pick your ComfyUI folder, or any folder to download it into (2 GB).", true);
                SetupPanel.Visibility = Visibility.Visible;
                return;
            }
            Note("Starting ComfyUI on your graphics card…");
            WriteFinishNode();
            // through cmd so ComfyUI writes its own log file: it can then outlive this app ("Keep ComfyUI running")
            var args = "-s ComfyUI\\main.py --windows-standalone-build --listen 127.0.0.1 --port " + Port + " --disable-auto-launch --preview-method auto" + LaunchFlags();
            if (!string.IsNullOrWhiteSpace(settings.OutputDir)) args += $" --output-directory \"{settings.OutputDir.TrimEnd('\\')}\"";
            server = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/s /c \"\"{python}\" {args} > comfy_server.log 2>&1\"")
                { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true }
            };
            server.Start();
            for (int i = 0; i < 240 && !await Alive(); i++)
            {
                if (server.HasExited) { Offline("ComfyUI stopped while starting."); return; }
                // how long so far, rather than a guess: with many plugins it can take a minute or two
                Note($"Starting ComfyUI on your graphics card… {i} s" + (settings.FastStart ? "" : " (loading all your plugins)"));
                await Task.Delay(1000);
            }
            if (!await Alive()) { Offline("ComfyUI didn't start in time."); return; }
        }
        await LoadOptions();
        await LoadSystem();
        await LoadNodeNames();
        ShowPluginBanner();
        ShowBoosters();
        if (!listening) { listening = true; _ = Listen(); }
        ready = true;
        GenerateBtn.IsEnabled = ModelBox.Items.Count > 0;
        SetState("Ready", Brush("Ok"));
        if (ModelBox.Items.Count > 0) Note(setupNote ?? "");
    }

    async Task LoadOptions()
    {
        loadingOptions = true;
        bool otherModel = false;
        try
        {
            var ck = JsonNode.Parse(await http.GetStringAsync("/object_info/CheckpointLoaderSimple"));
            var ks = JsonNode.Parse(await http.GetStringAsync("/object_info/KSampler"));
            var unets = ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/UNETLoader"))?["UNETLoader"]?["input"]?["required"]?["unet_name"]).ToList();
            // smaller GGUF files, when a GGUF loader plugin is there (an empty answer otherwise)
            unets.AddRange(ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/UnetLoaderGGUF"))?["UnetLoaderGGUF"]?["input"]?["required"]?["unet_name"])
                .Where(u => u.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)));
            var tes = ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/CLIPLoader"))?["CLIPLoader"]?["input"]?["required"]?["clip_name"]).ToList();
            var vaes = ComboOptions(JsonNode.Parse(await http.GetStringAsync("/object_info/VAELoader"))?["VAELoader"]?["input"]?["required"]?["vae_name"]).ToList();
            FindParts(tes, vaes);      // by what's inside each file, not its name
            var videos = new List<string>();
            var ckpts = ComboOptions(ck["CheckpointLoaderSimple"]["input"]["required"]["ckpt_name"]).ToList();
            // plain SDXL base stays installed for the inpainting model, but every other model makes better pictures
            var models = ckpts.Where(c => !c.StartsWith("sd_xl_base", StringComparison.OrdinalIgnoreCase) && IsFullCheckpoint(c)).ToList();
            separate.Clear(); setupNote = null;
            foreach (var u in unets)
            {
                var family = FamilyOf(u);
                if (family == null) continue;
                var (te, vae, label) = family switch
                {
                    "qwen_image" => (qwenTe, qwenVae, "Qwen-Image 2.1"), "krea2" => (kreaTe, kreaVae, "Krea 2"), "anima" => (animaTe, kreaVae, "Anima"), _ => (wanTe, wanVae, "Wan 2.1"),
                };
                if (te == null || vae == null)
                {
                    setupNote = $"{Path.GetFileNameWithoutExtension(u)} needs the {label} text encoder and VAE: get them on the Models page.";
                    continue;
                }
                var name = u.Contains("qwen_image_2.1", StringComparison.OrdinalIgnoreCase) ? QwenName : u;   // the original keeps the name saved settings know
                separate[name] = (u, family);
                (family.StartsWith("wan") ? videos : models).Add(name);
            }
            if (models.Count == 0) models.AddRange(ckpts.Where(IsFullCheckpoint));        // SDXL base only when there's nothing else
            (imageModels, videoModels) = (models, videos);
            FillModels();
            otherModel = ModelBox.SelectedItem as string != (showingVideoModels ? settings.VideoModel : settings.Model);   // the saved one is gone or hidden: its guidance and sampler don't fit
            Fill(SamplerBox, ComboOptions(ks["KSampler"]["input"]["required"]["sampler_name"]), settings.Sampler, "dpmpp_2m");
            Fill(SchedulerBox, ComboOptions(ks["KSampler"]["input"]["required"]["scheduler"]), settings.Scheduler, "karras");
            if (ModelBox.Items.Count == 0) Note("No model yet. Open Models and download one: Krea 2 Turbo is a good start.", true);
            var up = JsonNode.Parse(await http.GetStringAsync("/object_info/UpscaleModelLoader"))?["UpscaleModelLoader"]?["input"]?["required"]?["model_name"];
            upscaleModel = ComboOptions(up).FirstOrDefault();
            var lo = JsonNode.Parse(await http.GetStringAsync("/object_info/LoraLoader"))?["LoraLoader"]?["input"]?["required"]?["lora_name"];
            loras = ComboOptions(lo).ToList();
            loraFamily = loras.ToDictionary(l => l, l => LoraFamily(Path.Combine(ModelsDir, "loras", l)));
            FillLoras();
            _ = LoadLoraInfo();                 // what each LoRA does, looked up in the background
            inpaintModel = unets.FirstOrDefault(n => n.Contains("inpaint", StringComparison.OrdinalIgnoreCase));
            baseCheckpoint = ckpts.FirstOrDefault(c => c.Contains("sd_xl_base", StringComparison.OrdinalIgnoreCase))
                ?? ckpts.FirstOrDefault(c => !c.Contains("flux", StringComparison.OrdinalIgnoreCase));
            RefreshStyleTiles();
            UpdateLoraChip();
        }
        catch (Exception e) { Note("Couldn't read ComfyUI's models: " + e.Message, true); }
        loadingOptions = false;
        // the settings on screen belong to the saved model, not the one picked instead: don't keep them as its profile
        if (otherModel) { profileModel = null; Model_SelectionChanged(null, null); }
    }

    bool loadingOptions;
    bool Flux => Named("flux");
    bool Named(string part) => ModelBox.SelectedItem is string m && m.Contains(part, StringComparison.OrdinalIgnoreCase);
    // models in separate files (diffusion model + text encoder + VAE): Qwen-Image 2.1, Krea 2 and their fine-tunes
    const string QwenName = "Qwen-Image 2.1";
    string qwenTe, qwenVae, kreaTe, kreaVae, animaTe, setupNote;
    readonly Dictionary<string, (string Unet, string Family)> separate = new();
    string Family => ModelBox.SelectedItem is string m && separate.TryGetValue(m, out var s) ? s.Family : null;   // also its CLIPLoader type
    bool Qwen => Family == "qwen_image";
    bool Krea => Family == "krea2";
    bool Anima => Family == "anima";
    bool AnimeModel => Anima || Named("noct") || ModelRec.Label.Contains("Illustrious") || ModelRec.Label == "NoobAI";
    // Anima's own quality negative, from ComfyUI's template (its training used these score tags)
    const string AnimaNegative = "worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, sepia";
    bool Sdxl => Family == null && !Flux;

    // steps for Fast / Standard / Best, guidance and sampler, from each model's recommended settings
    (int Fast, int Standard, int Best, double Cfg, string Sampler, string Scheduler) Preset
    {
        get { var r = ModelRec; return (r.Steps[0], r.Steps[1], r.Steps[2], r.Cfg, r.Sampler, r.Scheduler); }
    }
    int StepsFor(string quality) => quality switch { "20" => Preset.Fast, "30" => Preset.Standard, _ => Preset.Best };
    int Steps => (int)StepsSlider.Value;

    void TypedSteps()
    {
        if (int.TryParse(StepsBox.Text, out var v)) StepsSlider.Value = Math.Clamp(v, 1, 100);
        StepsBox.Text = Steps.ToString();
    }

    // which family a diffusion model is, told from its tensor names the way ComfyUI does, so renamed and fine-tuned files work too
    // a real checkpoint carries its own text encoder and VAE; a model-only file in checkpoints can't be loaded from there
    static bool IsFullCheckpoint(string name)
    {
        try
        {
            using var f = File.OpenRead(Path.Combine(ModelsDir, "checkpoints", name));
            var len = new byte[8]; f.ReadExactly(len);
            long n = BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n <= 0 || n > 100_000_000) return true;             // not a safetensors header (.ckpt): can't tell, leave it listed
            var head = new byte[n]; f.ReadExactly(head);
            var keys = Encoding.UTF8.GetString(head);
            return keys.Contains("first_stage_model.") || keys.Contains("\"vae.") || keys.Contains("conditioner.") || keys.Contains("cond_stage_model.") || keys.Contains("text_encoders.");
        }
        catch { return true; }
    }

    static string FamilyOf(string name)
    {
        foreach (var dir in new[] { "diffusion_models", "unet" })
        {
            var file = Path.Combine(ModelsDir, dir, name);
            if (File.Exists(file)) return FamilyOfFile(file);
        }
        return null;
    }

    static string FamilyOfFile(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            string keys;
            if (file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                // GGUF keeps the original tensor names (length-prefixed) near the start of the file
                var start = new byte[Math.Min(f.Length, 8 << 20)]; f.ReadExactly(start);
                keys = Encoding.Latin1.GetString(start);
                if (!keys.StartsWith("GGUF")) return null;
            }
            else
            {
                var len = new byte[8]; f.ReadExactly(len);
                long n = BinaryPrimitives.ReadInt64LittleEndian(len);
                if (n <= 0 || n > 100_000_000) return null;
                var head = new byte[n]; f.ReadExactly(head);
                keys = Encoding.UTF8.GetString(head);
            }
            if (keys.Contains("llm_adapter.")) return "anima";           // Anima (Cosmos-based, a Qwen3 0.6B adapter)
            if (Regex.IsMatch(keys, @"txtfusion\.projector\.weight(?![\w.])")) return "krea2";
            if (Regex.IsMatch(keys, @"txt_in\.text_norm\.weight(?![\w.])")) return "qwen_image";
            // Wan 2.1 text-to-video takes 16 latent channels in; picture-to-video (Fun InP) 36: the start picture's latent and mask too.
            // Wan 2.2 5B (48, its own VAE) isn't supported.
            var wan = Regex.Match(keys, @"patch_embedding\.weight""\s*:\s*\{[^}]*""shape""\s*:\s*\[\s*\d+\s*,\s*(\d+)");
            return !wan.Success ? null : wan.Groups[1].Value switch { "16" => "wan", "36" => "wan_i2v", _ => null };
        }
        catch { return null; }
    }

    // --selftest part: a GGUF file's family comes from the tensor names in its header, like a safetensors file's
    static bool SelfTestGguf()
    {
        var file = Path.Combine(Path.GetTempPath(), "imageforge-selftest.gguf");
        try
        {
            using (var w = new BinaryWriter(File.Create(file)))
            {
                w.Write(Encoding.ASCII.GetBytes("GGUF")); w.Write(3u); w.Write(2UL); w.Write(0UL);
                foreach (var name in new[] { "txtfusion.projector.weight", "blocks.0.attn.qkv.weight" })
                {
                    w.Write((ulong)name.Length); w.Write(Encoding.UTF8.GetBytes(name)); w.Write(2u); w.Write(64UL); w.Write(64UL); w.Write(0u); w.Write(0UL);
                }
            }
            return FamilyOfFile(file) == "krea2";
        }
        finally { try { File.Delete(file); } catch { } }
    }

    List<string> loras = new();
    string inpaintModel, baseCheckpoint;  // the SDXL inpainting UNet (diffusion_models) and a plain SDXL checkpoint for its CLIP and VAE

    // the Pixel art style's own (SDXL) LoRA wins over the picked one
    (string Name, double Strength)? ActiveLora()
    {
        if (Sdxl && CurrentStyle.Lora is string own && loras.Contains(own)) return (own, 1.0);
        var first = loraRows.FirstOrDefault(r => r.Box?.SelectedItem is string l && l != "None" && LorasForModel.Contains(l));
        if (first != null)
        {
            double str = Math.Round(first.Slider?.Value ?? 0.8, 2);
            if (first.ValueBox != null && double.TryParse(first.ValueBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
                str = Math.Round(p, 2);
            return (first.Box.SelectedItem as string, str);
        }
        return null;
    }

    // Collects all active LoRAs: UI dropdown LoRA + any <lora:filename:strength> specified in the prompt
    List<(string Name, double Strength)> AllActiveLoras(string promptText, out string cleanedPrompt)
    {
        var list = new List<(string Name, double Strength)>();
        var fit = LorasForModel.ToList();          // a LoRA for another model family would do nothing (or worse)
        try
        {
            if (Sdxl && CurrentStyle.Lora is string own && loras.Contains(own))
                list.Add((own, 1.0));

            foreach (var row in loraRows)
            {
                if (row.Box?.SelectedItem is string l && l != "None" && fit.Contains(l))
                {
                    double str = Math.Round(row.Slider?.Value ?? 0.8, 2);
                    if (row.ValueBox != null && double.TryParse(row.ValueBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
                        str = Math.Round(p, 2);
                    if (!list.Any(x => x.Name.Equals(l, StringComparison.OrdinalIgnoreCase)))
                        list.Add((l, str));
                }
            }
        }
        catch { }

        cleanedPrompt = promptText ?? "";
        if (string.IsNullOrEmpty(promptText)) return list;

        var matches = Regex.Matches(promptText, @"<lora:([^:>]+)(?::([0-9.-]+))?>", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Value.Trim();
            double str = 1.0;
            if (m.Groups[2].Success && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                str = parsed;

            // match against installed loras in ComfyUI\models\loras
            var matched = fit.FirstOrDefault(l =>          // exact names only: a loose match could pick the wrong file
                l.Equals(raw, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(l).Equals(raw, StringComparison.OrdinalIgnoreCase)
                || l.Replace('\\', '/').EndsWith("/" + raw.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || l.Replace('\\', '/').EndsWith("/" + raw.Replace('\\', '/') + ".safetensors", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(l).Equals(raw + ".safetensors", StringComparison.OrdinalIgnoreCase)
            );

            if (matched != null)
            {
                int existingIdx = list.FindIndex(x => x.Name.Equals(matched, StringComparison.OrdinalIgnoreCase));
                if (existingIdx >= 0)
                    list[existingIdx] = (matched, str);
                else
                    list.Add((matched, str));
            }
        }

        cleanedPrompt = Regex.Replace(promptText, @"<lora:[^>]+>", "").Trim();
        cleanedPrompt = Regex.Replace(cleanedPrompt, @",\s*,+", ",");
        cleanedPrompt = Regex.Replace(Regex.Replace(cleanedPrompt, @"\s+(?=[,.])", ""), @"\s{2,}", " ").Trim(' ', ',');   // no " ," or double spaces where a tag was
        return list;
    }

    // a LoRA only fits the model family it was trained on: "sdxl", "qwen_image", "krea2", or null when the file doesn't say
    Dictionary<string, string> loraFamily = new();
    string ModelFamily => Family == "wan_i2v" ? "wan" : Family ?? (Flux ? "flux" : "sdxl");      // picture-to-video takes the 1.3B text-to-video LoRAs
    IEnumerable<string> LorasForModel => loras.Where(l => loraFamily.GetValueOrDefault(l) is not string f || f == ModelFamily);

    string loraFamilyShown;     // the family whose LoRA rows are on screen

    void FillLoras()
    {
        if (loras.Count == 0 && loraFamily.Count == 0) return;      // the list hasn't loaded yet: keep the saved rows as they are
        var modelLoras = LorasForModel.ToList();
        if (ModelFamily != loraFamilyShown)
        {
            if (loraFamilyShown != null) settings.LorasByFamily[loraFamilyShown] = CurrentLoras();
            var wanted = settings.LorasByFamily.TryGetValue(ModelFamily, out var saved) ? saved
                : loraFamilyShown == null ? CurrentLoras() : new List<LoraItemSetting>();       // first load: the rows saved last time
            loraFamilyShown = ModelFamily;
            LoraRowsPanel.Children.Clear();
            loraRows.Clear();
            foreach (var l in wanted.Where(l => modelLoras.Contains(l.Name))) CreateLoraRow(l.Name, l.Strength);
        }
        else
        {
            var items = new[] { "None" }.Concat(modelLoras).ToList();
            foreach (var r in loraRows.Where(r => r.Box != null))
            {
                var current = r.Box.SelectedItem as string ?? "None";
                r.Box.ItemsSource = items;
                r.Box.SelectedItem = items.Contains(current) ? current : "None";
            }
        }
        UpdateLoraTitles();
        UpdateLoraChip();
    }

    List<LoraItemSetting> CurrentLoras() => loraRows.Where(r => r.Box?.SelectedItem is string n && n != "None")
        .Select(r => new LoraItemSetting { Name = (string)r.Box.SelectedItem, Strength = r.Slider?.Value ?? 0.8 }).ToList();

    // the trainer's own label when there is one, else the layer names: SDXL's U-Net blocks, Qwen-Image's img/txt streams, Krea 2's wk/gate attention
    static string LoraFamily(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            var len = new byte[8]; f.ReadExactly(len);
            long n = BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n <= 0 || n > 100_000_000) return null;
            var head = new byte[n]; f.ReadExactly(head);
            var json = JsonNode.Parse(head).AsObject();
            var meta = json["__metadata__"];
            var label = $"{meta?["modelspec.architecture"]} {meta?["ss_base_model_version"]}".ToLowerInvariant();
            if (label.Contains("krea")) return "krea2";
            if (label.Contains("qwen")) return "qwen_image";
            if (label.Contains("anima")) return "anima";
            if (label.Contains("wan")) return "wan";
            if (label.Contains("sdxl") || label.Contains("stable-diffusion-xl")) return "sdxl";
            var keys = string.Join('\n', json.Select(k => k.Key));
            if (keys.Contains(".attn.wk.") || keys.Contains(".attn.gate.") || keys.Contains("txtfusion") || keys.Contains("text_fusion")) return "krea2";
            if (keys.Contains("img_mlp") || keys.Contains("txt_mlp") || keys.Contains("img_mod")) return "qwen_image";
            if (keys.Contains("llm_adapter") || keys.Contains("adaln_modulation")) return "anima";
            // Anima's blocks: attention with q_proj .. output_proj and mlp.layer1/2 (any one is enough: slider LoRAs often train only
            // cross- or only self-attention); Wan's attention has .q / .k / .v / .o and an ffn
            if (Regex.IsMatch(keys, @"(self_attn|cross_attn)[._](q|k|v|output)_proj|mlp[._]layer[12]")) return "anima";
            if (keys.Contains("self_attn") && keys.Contains("cross_attn")) return "wan";
            if (keys.Contains("input_blocks") || keys.Contains("output_blocks") || keys.Contains("down_blocks") || keys.Contains("up_blocks")) return "sdxl";
            return null;
        }
        catch { return null; }
    }

    // --selftest part: on anime models a style keeps no negatives against drawn looks, and "Use prompt" unwraps either version
    static bool SelfTestStyles()
    {
        var cine = StylePreset.All.First(s => s.Key == "cinematic");
        var anime = cine.NegativeFor(true);
        return anime == "flat lighting, bright" && cine.NegativeFor(false) == cine.Negative
            && StylePreset.Unwrap(cine.Apply("a fox"), "blurry, " + anime) is ("a fox", "blurry", var a) && a == cine
            && StylePreset.Unwrap(cine.Apply("a fox"), "blurry, " + cine.Negative) is ("a fox", "blurry", _);
    }

    // --selftest part: slider LoRAs that train only cross- or only self-attention are still Anima's; Wan's and SDXL's stay theirs
    static bool SelfTestLoraFamily()
    {
        var file = Path.Combine(Path.GetTempPath(), "imageforge-selftest-family.safetensors");
        string Family(params string[] keys)
        {
            var head = Encoding.UTF8.GetBytes("{" + string.Join(",", keys.Select((k, i) => $"\"{k}\":{{\"dtype\":\"F32\",\"shape\":[1],\"data_offsets\":[{i * 4},{i * 4 + 4}]}}")) + "}");
            using (var w = new BinaryWriter(File.Create(file))) { w.Write((long)head.Length); w.Write(head); w.Write(new byte[keys.Length * 4]); }
            return LoraFamily(file);
        }
        try
        {
            return Family("lora_unet_blocks_0_cross_attn_k_proj.lora_down.weight") == "anima"
                && Family("lora_unet_blocks_0_self_attn_q_proj.lora_down.weight", "lora_unet_blocks_0_mlp_layer1.lora_up.weight") == "anima"
                && Family("diffusion_model.blocks.0.self_attn.q.lora_A.weight", "diffusion_model.blocks.0.cross_attn.k.lora_A.weight", "diffusion_model.blocks.0.ffn.0.lora_A.weight") == "wan"
                && Family("lora_unet_input_blocks_4_1_transformer_blocks_0_attn1_to_q.lora_down.weight") == "sdxl";
        }
        finally { try { File.Delete(file); } catch { } }
    }

    void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LeavingModel();
        negativeUnlocked = false;
        foreach (var r in QualityGroup.Children.OfType<RadioButton>())
            ((TextBlock)((StackPanel)r.Content).Children[1]).Text = $"{StepsFor((string)r.Tag)} steps";
        if (LoraSection != null) { LoraSection.IsEnabled = !Flux; FillLoras(); }
        if (StartSection != null) StartSection.Visibility = VideoOn && PictureVideo ? Visibility.Visible : Visibility.Collapsed;
        UpdateLoraChip();
        if (ModelNote != null)
        {
            ModelNote.Text = Qwen ? "Qwen Research Licence: personal and research use only, not commercial. In Edit photo, describe the change (\"make it sunset\")."
                    + (Named("noctq") ? " Start prompts with \"An anime illustration of…\"." : "")
                : Krea ? "Krea 2: realistic photos and art; the negative prompt isn't used. Check the model's licence before commercial use."
                : Anima ? "Anima: anime and illustration. Tags (1girl, masterpiece) or plain sentences both work; a quality negative prompt is added for you."
                : PictureVideo ? "Wan 2.1 picture-to-video: the clip starts on your picture. Describe what moves and how the camera moves."
                : Wan ? "Wan 2.1 video, 16 frames a second. A clip takes a few minutes; describe the movement and the camera."
                : Flux ? "Good at readable text. Slower; the negative prompt isn't used." : "";
            ModelNote.Visibility = ModelNote.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowBoosters();
        ShowParts();
        if (loadingOptions || ModelBox.SelectedItem == null) { profileModel = ModelBox.SelectedItem as string ?? profileModel; return; }   // on startup keep the saved guidance and sampler
        var p = Preset;
        CfgSlider.Value = p.Cfg; SamplerBox.SelectedItem = p.Sampler; SchedulerBox.SelectedItem = p.Scheduler;
        StepsSlider.Value = StepsFor(Picked(QualityGroup));
        ArrivedAtModel();       // a model's own saved settings win over the recommendation
        ShowBoosters();
        ShowParts();
        RefreshGuard();
        UpdateNegative();
        ShowRefStyle();
    }

    static void Fill(ComboBox box, IEnumerable<string> list, string saved, string prefer)
    {
        var items = list.ToArray();
        box.ItemsSource = items;
        box.SelectedItem = items.Contains(saved) ? saved : items.FirstOrDefault(i => i.Contains(prefer, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
    }

    // graphics card shown in the header, e.g. "NVIDIA GeForce RTX 3060 · 12 GB"
    async Task LoadSystem()
    {
        try
        {
            var dev = JsonNode.Parse(await http.GetStringAsync("/system_stats"))?["devices"]?[0];
            var name = (dev?["name"]?.GetValue<string>() ?? "").Split(" : ")[0];   // "cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync"
            if (name.StartsWith("cuda:")) name = name[(name.IndexOf(' ') + 1)..];
            double gb = (dev?["vram_total"]?.GetValue<double>() ?? 0) / (1L << 30);
            GpuText.Text = gb > 0 ? $"{name}  ·  {gb:0} GB" : name;
        }
        catch { }
    }

    // create: empty latent -> sampler. edit: photo -> latent (-> noise mask) -> sampler at partial denoise,
    // and with a mask the result is blended back so unpainted pixels stay exactly as they were.
    // plain: no style, no LoRA. count: images to make (0 = the Number of images setting)
    JsonObject Graph(long seed, string image = null, bool masked = false, double? denoise = null, bool plain = false, int count = 0, string[] refs = null, string looks = null)
    {
        int n = count > 0 ? count : int.Parse(Picked(CountGroup));
        var g = new JsonObject();
        JsonArray model, clip, vae;
        if (Family is string family)
        {
            var (te, vaeFile, type) = family switch
            {
                "anima" => (TeFor(family), VaeFor(family), "stable_diffusion"), _ => (TeFor(family), VaeFor(family), family),   // Anima's loader type, per ComfyUI's template
            };
            g["4"] = UnetNode(separate[(string)ModelBox.SelectedItem].Unet);
            g["44"] = Node("CLIPLoader", new() { ["clip_name"] = te, ["type"] = type });
            g["45"] = Node("VAELoader", new() { ["vae_name"] = vaeFile });
            (model, clip, vae) = (Link("4", 0), Link("44", 0), Link("45", 0));
        }
        else
        {
            g["4"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = (string)ModelBox.SelectedItem });
            (model, clip, vae) = (Link("4", 0), Link("4", 1), Link("4", 2));
        }
        var words = PromptBox.Text.Trim() + (looks is { Length: > 0 } ? ", " + looks : "");        // looks: what the reference character looks like
        var rawPrompt = plain ? words : CurrentStyle.Apply(words);
        var activeLoras = plain ? new List<(string, double)>() : AllActiveLoras(rawPrompt, out rawPrompt);

        int loraNo = 1;
        foreach (var (loraName, strength) in activeLoras)
        {
            var idStr = $"lora{loraNo++}";          // named apart from the numbered nodes, however many LoRAs there are
            g[idStr] = Node("LoraLoader", new() { ["model"] = model, ["clip"] = clip, ["lora_name"] = loraName, ["strength_model"] = strength, ["strength_clip"] = strength });
            (model, clip) = (Link(idStr, 0), Link(idStr, 1));
        }

        g["6"] = Node("CLIPTextEncode", new() { ["text"] = rawPrompt, ["clip"] = clip });
        g["7"] = Node("CLIPTextEncode", new() { ["text"] = Flux ? "" : string.Join(", ", new[] { NegativeBox.Text.Trim(), CurrentStyle.NegativeFor(AnimeModel), Anima ? AnimaNegative : "" }.Where(t => t != "")), ["clip"] = clip.DeepClone() });
        g["3"] = Node("KSampler", new()
        {
            ["seed"] = seed, ["steps"] = Steps, ["cfg"] = CfgSlider.Value,
            ["sampler_name"] = (string)SamplerBox.SelectedItem, ["scheduler"] = (string)SchedulerBox.SelectedItem,
            ["denoise"] = image == null ? 1 : Math.Round(denoise ?? StrengthSlider.Value, 2),
            ["model"] = model, ["positive"] = Link("6", 0), ["negative"] = Link("7", 0), ["latent_image"] = Link(image == null ? "5" : "12", 0)
        });
        g["8"] = Node("VAEDecode", new() { ["samples"] = Link("3", 0), ["vae"] = vae.DeepClone() });
        g["9"] = Node("SaveImage", new() { ["filename_prefix"] = image == null ? "forge/img" : "forge/edit", ["images"] = Link(masked ? "15" : "8", 0) });
        if (image == null)
        {
            var (width, height) = PictureSize();
            g["5"] = Node(Flux ? "EmptySD3LatentImage" : "EmptyLatentImage", new() { ["width"] = width, ["height"] = height, ["batch_size"] = n });
            if (refs is { Length: > 0 }) UseReferences(g, refs, clip, vae, rawPrompt);
            return g;
        }
        g["10"] = Node("LoadImage", new() { ["image"] = image });                 // output 1 = mask from the PNG's alpha
        g["11"] = Node("VAEEncode", new() { ["pixels"] = Link("10", 0), ["vae"] = vae.DeepClone() });
        if (Qwen && !masked && !plain)
        {
            // an instruction ("make it sunset") stays an instruction: the style is a short hint, not its wording wrapped around it
            AllActiveLoras(PromptBox.Text.Trim(), out var instruction);
            if (CurrentStyle.Key != "none") instruction += $" ({CurrentStyle.Label} style)";
            // Qwen-Image 2.1 edits by instruction: the photo goes in as a reference, and the picture is redrawn from it
            g["6"] = Node("TextEncodeQwenImage21", new()
            {
                ["clip"] = clip.DeepClone(), ["prompt"] = instruction, ["negative_prompt"] = NegativeBox.Text.Trim(),
                ["vae"] = vae.DeepClone(), ["resolution"] = 1024, ["images.image_1"] = Link("10", 0),
            });
            g.Remove("7");
            var k = (JsonObject)g["3"]["inputs"];
            k["positive"] = Link("6", 0); k["negative"] = Link("6", 1); k["denoise"] = 1.0;
            g["12"] = Node("RepeatLatentBatch", new() { ["samples"] = Link("6", 2), ["amount"] = n });
            g.Remove("11");
            return g;
        }
        g["12"] = Node("RepeatLatentBatch", new() { ["samples"] = Link(masked ? "13" : "11", 0), ["amount"] = n });
        if (masked)
        {
            g["13"] = Node("SetLatentNoiseMask", new() { ["samples"] = Link("11", 0), ["mask"] = Link("10", 1) });
            g["14"] = Node("RepeatImageBatch", new() { ["image"] = Link("10", 0), ["amount"] = n });
            g["15"] = Node("ImageCompositeMasked", new()
            {
                ["destination"] = Link("14", 0), ["source"] = Link("8", 0), ["x"] = 0, ["y"] = 0, ["resize_source"] = false, ["mask"] = Link("10", 1)
            });
        }
        return g;
    }
    static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    static JsonObject UnetNode(string unet) => unet.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        ? Node("UnetLoaderGGUF", new() { ["unet_name"] = unet })
        : Node("UNETLoader", new() { ["unet_name"] = unet, ["weight_dtype"] = "default" });
    static JsonArray Link(string node, int slot) => new() { node, slot };

    void StopServer()
    {
        try { if (server != null && !server.HasExited) server.Kill(true); } catch { }
    }

    // a dropdown's choices in /object_info: ["a", "b"] first (older nodes), or ["COMBO", {"options": [...]}] (newer nodes)
    static IEnumerable<string> ComboOptions(JsonNode input) =>
        (input?[0] as JsonArray ?? input?[1]?["options"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(n => n != null) ?? [];

    // ---------------- gallery ----------------
    static Shot MakeShot(string path)
    {
        try { return new Shot { FilePath = path, Thumb = LoadImage(path, 360), Meta = ReadMeta(path), Date = File.GetLastWriteTime(path) }; }
        catch { return null; }
    }

    // --selftest: checks that need no ComfyUI; exit code 0 when they pass. A picture saved again under the same name
    // must load as the new picture (ComfyUI reuses file names after deletes; WPF's image cache used to show the old one).
    public static int SelfTest()
    {
        var file = Path.Combine(Path.GetTempPath(), "imageforge-selftest.png");
        static void Save(string f, Color c)
        {
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, px, 16)));
            using var s = File.Create(f);
            enc.Save(s);
        }
        static Color First(BitmapSource b)
        {
            var px = new byte[4];
            new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }
        try
        {
            Save(file, Colors.Red);
            var first = First(LoadImage(file, 0));
            Save(file, Colors.Blue);
            var second = First(LoadImage(file, 0));
            return (first == Colors.Red && second == Colors.Blue ? 0 : 1) | (SelfTestLoraConvert() ? 0 : 4) | (SelfTestStrength() ? 0 : 8) | (SelfTestGguf() ? 0 : 16) | (SelfTestSecret(file) ? 0 : 32) | (SelfTestMinorBlock() ? 0 : 64) | (SelfTestLoraFamily() ? 0 : 128) | (SelfTestStyles() ? 0 : 256);
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }      // seen when run with stderr redirected (a build script)
        finally { try { File.Delete(file); } catch { } }
    }

    // decoded into memory (the file isn't locked) and frozen so it can cross threads
    static BitmapSource LoadImage(string path, int width)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        if (path.EndsWith(".locked")) bi.StreamSource = OpenPicture(path);     // (the image cache is for files only: it throws on a stream)
        else
        {
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;     // ComfyUI reuses file names after deletes: never show a stale picture
            bi.UriSource = new Uri(path);
        }
        if (width > 0) bi.DecodePixelWidth = width;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    // ---------------- favourites and delete ----------------
    void ToggleFavorite(Shot s)
    {
        if (s == null || s.Pending) return;
        s.Favorite = !s.Favorite;
        var name = Path.GetFileName(s.FilePath);
        if (s.Favorite) settings.Favorites.Add(name); else settings.Favorites.Remove(name);
        settings.Save();
        if (ViewKind == "favourites") galleryView.Refresh();
        UpdateCount();
        if (s == viewing) ShowFavorite();
    }

    // to the Recycle Bin, never a permanent delete
    bool Delete(Shot s)
    {
        if (s == null || s.Pending) return false;
        if (s == viewing) StopClip();
        if (s.IsVideo && File.Exists(s.VideoPath))
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(s.VideoPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch (Exception ex) { Note("Couldn't delete: " + ex.Message, true); return false; }
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(s.FilePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { Note("Couldn't delete: " + ex.Message, true); return false; }
        if (File.Exists(OriginalOf(s.FilePath)))
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(OriginalOf(s.FilePath), Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch { }
        if (settings.Favorites.Remove(Path.GetFileName(s.FilePath))) settings.Save();
        shots.Remove(s);
        return true;
    }

    void Star_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;                        // on mouse-down, before the card button takes the click: don't also open the viewer
        ToggleFavorite(ShotOf(sender));
    }
    void MenuFavorite_Click(object sender, RoutedEventArgs e) => ToggleFavorite(ShotOf(sender));
    void MenuDelete_Click(object sender, RoutedEventArgs e) { if (Delete(ShotOf(sender))) Note("Moved to the Recycle Bin."); }

    void FavViewed_Click(object sender, RoutedEventArgs e) => ToggleFavorite(viewing);

    void ShowFavorite()
    {
        FavBtn.Content = viewing?.Favorite == true ? "" : "";
        FavBtn.Foreground = Brush(viewing?.Favorite == true ? "Accent" : "Muted");
    }

    void DeleteViewed_Click(object sender, RoutedEventArgs e) => RemoveViewed(Delete, "Moved to the Recycle Bin.");

    // the image on screen leaves this view (deleted, hidden…): show the next one
    void RemoveViewed(Func<Shot, bool> remove, string note)
    {
        var list = Browsable().ToList();
        int i = list.IndexOf(viewing);
        if (!remove(viewing)) return;
        list.RemoveAt(i);
        if (list.Count == 0) { CloseViewer(); Note(note); return; }
        OpenViewer(list[Math.Min(i, list.Count - 1)]);
        ViewerNote.Text = note;
    }

    void Gallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (GalleryScroll.VerticalOffset + GalleryScroll.ViewportHeight > GalleryScroll.ExtentHeight - 700) _ = LoadMore(60);   // older images as you near the end
        if (e.ViewportWidthChange != 0) SizeCards();
    }

    // Ctrl+scroll: bigger or smaller pictures
    void Gallery_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        settings.CardSize = Math.Clamp(settings.CardSize + Math.Sign(e.Delta) * 60, 130, 370);
        SizeCards();
    }

    // cards share the row evenly instead of leaving a ragged gap on the right
    void SizeCards()
    {
        double w = GalleryScroll.ViewportWidth;
        int cols = Math.Max(1, (int)(w / settings.CardSize));
        Resources["Card"] = Math.Floor(w / cols) - 12;
    }

    // ComfyUI writes its graph into the PNG's "prompt" text chunk (before the image data).
    // Upscales also carry a "forge" chunk: the source image's details, which win over the (prompt-less) upscale graph.
    static Meta ReadMeta(string path)
    {
        Meta fromGraph = null;
        try
        {
            using var fs = OpenPicture(path);
            var head = new byte[8];
            fs.ReadExactly(head);
            while (true)
            {
                fs.ReadExactly(head);
                int len = BinaryPrimitives.ReadInt32BigEndian(head);
                var kind = Encoding.ASCII.GetString(head, 4, 4);
                if (kind is "IDAT" or "IEND") return fromGraph;
                if (kind != "tEXt") { fs.Seek(len + 4, SeekOrigin.Current); continue; }
                var chunk = new byte[len];
                fs.ReadExactly(chunk);
                fs.Seek(4, SeekOrigin.Current);
                var t = Encoding.UTF8.GetString(chunk);
                int z = t.IndexOf('\0');
                if (z <= 0) continue;
                if (t[..z] == "forge") return JsonSerializer.Deserialize<Meta>(t[(z + 1)..]);
                if (t[..z] == "prompt") fromGraph = ParseMeta(JsonNode.Parse(t[(z + 1)..]) as JsonObject);
            }
        }
        catch { return fromGraph; }
    }

    static Meta ParseMeta(JsonObject g)
    {
        if (g == null) return null;
        var nodes = g.Select(kv => kv.Value).ToList();
        JsonNode First(params string[] types) => nodes.FirstOrDefault(n => types.Contains(Str(n?["class_type"])))?["inputs"];
        string TextOf(JsonNode link) => link is JsonArray a && a.Count > 0 && Str(a[0]) is string id ? Str(g[id]?["inputs"]?["text"]) : null;
        var ks = First("KSampler", "KSamplerAdvanced");
        var prompt = TextOf(ks?["positive"]) ?? Str(First("CLIPTextEncode")?["text"]);
        var loraNodes = nodes.Where(n => Str(n?["class_type"]) == "LoraLoader").Select(n => n?["inputs"]).Where(i => i != null).ToList();
        string loras = loraNodes.Count == 0 ? null : string.Join("\n", loraNodes.Select(i =>
        {
            var name = Path.GetFileNameWithoutExtension(Str(i["lora_name"]) ?? "?");
            var str = Dbl(i["strength_model"]) ?? 1;
            return $"{name} ({str:0.##})";
        }));
        return new Meta(prompt, TextOf(ks?["negative"]), Long(ks?["seed"] ?? ks?["noise_seed"]), Long(ks?["steps"]), Dbl(ks?["cfg"]),
            Str(ks?["sampler_name"]), Str(ks?["scheduler"]),
            Str(g["4"]?["inputs"]?["ckpt_name"]) ?? Str(g["4"]?["inputs"]?["unet_name"]) ?? Str(First("CheckpointLoaderSimple")?["ckpt_name"]),
            Loras: loras);   // node 4 is the picked model; Krea, Qwen and Anima load theirs with UNETLoader
    }
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) ? s : null;
    static long? Long(JsonNode n) => n is JsonValue v && v.TryGetValue(out long x) ? x : null;
    static double? Dbl(JsonNode n) => n is JsonValue v && v.TryGetValue(out double x) ? x : null;

    static Shot ShotOf(object sender) => (sender as FrameworkElement)?.DataContext as Shot;

    void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenViewer(ShotOf(sender));
    void MenuPrompt_Click(object sender, RoutedEventArgs e) => UsePromptOf(ShotOf(sender)?.Meta);
    void MenuCopy_Click(object sender, RoutedEventArgs e) { if (ShotOf(sender) is { Pending: false } s) Clipboard.SetImage(LoadImage(s.FilePath, 0)); }
    void MenuFolder_Click(object sender, RoutedEventArgs e) { if (ShotOf(sender) is { Pending: false } s) ShowInFolder(s.FilePath); }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Output, "forge");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }
    static void ShowInFolder(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    // ---------------- photo editor ----------------
    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        bool edit = EditMode.IsChecked == true, video = VideoMode.IsChecked == true;
        GalleryPanel.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;
        SizeSection.Visibility = ReferenceSection.Visibility = CoupleSection.Visibility = edit || video ? Visibility.Collapsed : Visibility.Visible;
        VideoSection.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        StartSection.Visibility = video && PictureVideo ? Visibility.Visible : Visibility.Collapsed;
        CountSection.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        EditorPanel.Visibility = PhotoSection.Visibility = StrengthSection.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        PromptHeading.Text = edit ? "Describe the result" : video ? "Describe the video" : "Prompt";
        TabsBar.Visibility = edit || video ? Visibility.Collapsed : Visibility.Visible;
        PromptBox.Tag = edit ? "e.g. the same scene in winter, snow on the roofs, golden hour"
            : video ? "e.g. a fox running through snowy woods, the camera following it" : "Describe the image: subject, style, lighting, mood…";
        GenerateText.Text = edit ? "Apply edit" : video ? "Make video" : "Generate";
        EnhanceBtn.ToolTip = edit ? "Looks at your photo and turns a short change into a clear, detailed one"
            : video ? "Turn a short idea into a detailed video prompt, with movement and camera" : "Turn a short idea into a detailed prompt, written by the local Qwen model";
        if (ready && video != showingVideoModels) FillModels();
        ShowBoosters();
    }

    void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a photo to edit", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif" };
        if (dlg.ShowDialog(this) == true) LoadPhoto(dlg.FileName);
    }

    void Photo_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) LoadPhoto(files[0]);
    }

    void EditThis_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        var shot = viewing;
        CloseViewer();
        EditMode.IsChecked = true;
        LoadPhoto(shot.FilePath);
        UsePromptOf(shot.Meta);
    }

    // from the viewer: open the image in the editor with the Expand choices showing
    void ExpandViewed_Click(object sender, RoutedEventArgs e)
    {
        EditThis_Click(sender, e);
        if (photo != null) Dispatcher.BeginInvoke(() => Expand_Click(ExpandBtn, null), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ShotOf(sender) is not { Pending: false } s) return;
        EditMode.IsChecked = true;
        LoadPhoto(s.FilePath);
        UsePromptOf(s.Meta);
    }

    void LoadPhoto(string path)
    {
        try { ShowPhoto(Oriented(path), Path.GetFileName(path)); }
        catch (Exception ex) { Note("Couldn't open that photo: " + ex.Message, true); }
    }

    // the photo three ways: as loaded (Reset), after crop/rotate/flip, and `photo` = that plus the adjust sliders
    BitmapSource photoOriginal, photoBase;
    bool quietAdjust;

    void ShowPhoto(BitmapSource src, string name)
    {
        photoOriginal = Fit(src);
        EndCrop();
        ResetAdjust();
        SetPhotoBase(photoOriginal);
        EditorView.Visibility = Toolbar.Visibility = ImageBar.Visibility = PhotoCard.Visibility = Visibility.Visible;
        DropHint.Visibility = ChoosePhotoBtn.Visibility = Visibility.Collapsed;
        PhotoName.Text = name;
        Note("");
    }

    // a new shape or orientation: the painting no longer lines up, so it goes
    void SetPhotoBase(BitmapSource b)
    {
        photoBase = b;
        ClearMarks();
        RenderPhoto();
    }

    void RenderPhoto()
    {
        if (photoBase == null) return;
        photo = Adjust(photoBase, BrightSlider.Value, ContrastSlider.Value, SatSlider.Value);
        EditorImage.Source = PhotoThumb.ImageSource = photo;
        EditorCanvas.Width = photo.PixelWidth;
        EditorCanvas.Height = photo.PixelHeight;
        PhotoSize.Text = $"{photo.PixelWidth} × {photo.PixelHeight}";
    }

    // brightness and contrast through one lookup table, then saturation pulls each pixel toward or away from its grey
    static BitmapSource Adjust(BitmapSource src, double brightness, double contrast, double saturation)
    {
        if (brightness == 0 && contrast == 0 && saturation == 0) return src;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0).CopyPixels(px, stride, 0);
        double c = (100 + contrast) / 100, s = (100 + saturation) / 100;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp((i - 128) * c + 128 + brightness * 1.28, 0, 255);
        Parallel.For(0, h, y =>
        {
            for (int i = y * stride, end = i + stride; i < end; i += 4)
            {
                double b = lut[px[i]], g = lut[px[i + 1]], r = lut[px[i + 2]];
                double grey = 0.299 * r + 0.587 * g + 0.114 * b;
                px[i] = (byte)Math.Clamp(grey + (b - grey) * s, 0, 255);
                px[i + 1] = (byte)Math.Clamp(grey + (g - grey) * s, 0, 255);
                px[i + 2] = (byte)Math.Clamp(grey + (r - grey) * s, 0, 255);
            }
        });
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    void Adjust_Click(object sender, RoutedEventArgs e) => AdjustPopup.IsOpen = true;
    void Adjust_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!quietAdjust) RenderPhoto(); }
    void AdjustReset_Click(object sender, RoutedEventArgs e) { ResetAdjust(); RenderPhoto(); }
    void ResetAdjust()
    {
        quietAdjust = true;
        BrightSlider.Value = ContrastSlider.Value = SatSlider.Value = 0;
        quietAdjust = false;
    }

    void RotateLeft_Click(object sender, RoutedEventArgs e) => Reshape(b => new TransformedBitmap(b, new RotateTransform(270)));
    void RotateRight_Click(object sender, RoutedEventArgs e) => Reshape(b => new TransformedBitmap(b, new RotateTransform(90)));
    void Flip_Click(object sender, RoutedEventArgs e) => Reshape(b => new TransformedBitmap(b, new ScaleTransform(-1, 1)));
    void ResetPhoto_Click(object sender, RoutedEventArgs e) { EndCrop(); ResetAdjust(); SetPhotoBase(photoOriginal); }

    void Reshape(Func<BitmapSource, BitmapSource> change)
    {
        if (photoBase == null) return;
        try { SetPhotoBase(Fit(change(photoBase))); }
        catch (Exception ex) { Note("Couldn't change the photo: " + ex.Message, true); }
    }

    // ---------------- expand (outpainting) ----------------
    void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (photo == null) return;
        double here = (double)photo.PixelWidth / photo.PixelHeight;
        var menu = new ContextMenu { PlacementTarget = ExpandBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (label, aspect) in new[] { ("Zoom out", 0.0), ("Square 1:1", 1.0), ("Portrait 2:3", 2 / 3.0), ("Tall 9:16", 9 / 16.0), ("Landscape 3:2", 1.5), ("Wide 16:9", 16 / 9.0) })
        {
            if (aspect != 0 && Math.Abs(Math.Log(aspect / here)) < 0.08) continue;      // already about that shape
            var (w, h, _) = ExpandLayout(aspect);
            var item = new MenuItem { Header = $"{label}   ({w} × {h})", Tag = "" };
            item.Click += (_, _) => _ = Generate(expand: aspect);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // the bigger canvas (about one megapixel, sides in 64s) and where the photo sits on it, centred.
    // aspect 0 = zoom out: half the photo's size again, a quarter on every side
    (int W, int H, Rect At) ExpandLayout(double aspect)
    {
        double w = photo.PixelWidth, h = photo.PixelHeight, cw, ch;
        if (aspect == 0) (cw, ch) = (w * 1.5, h * 1.5);
        else if (w / h < aspect) (cw, ch) = (h * aspect, h);
        else (cw, ch) = (w, w / aspect);
        double k = Math.Sqrt(1024.0 * 1024 / (cw * ch));
        int W = Math.Max(256, (int)Math.Round(cw * k / 64) * 64), H = Math.Max(256, (int)Math.Round(ch * k / 64) * 64);
        double f = Math.Min(W / cw, H / ch);
        return (W, H, new Rect((W - w * f) / 2, (H - h * f) / 2, w * f, h * f));
    }

    // the canvas to paint (new area transparent = repaint) and a "before" for compare (the photo on plain grey)
    (byte[] Canvas, byte[] Before) ExpandCanvas(double aspect)
    {
        var (W, H, at) = ExpandLayout(aspect);
        Image Photo() => new() { Source = photo, Stretch = Stretch.Fill, Width = at.Width, Height = at.Height,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(at.X, at.Y, 0, 0) };

        // starting colours for the new area: the photo's edges carried outwards (FillHoles), so the model keeps its palette
        var canvas = new Grid { Width = W, Height = H, Background = Brushes.Black };
        canvas.Children.Add(Photo());
        var canvasPx = RenderPixels(canvas, W, H);
        var outside = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                outside[y * W + x] = at.Contains(x + 0.5, y + 0.5) ? (byte)0 : (byte)255;
        FillHoles(canvasPx, outside, W, H);

        // repaint the new area plus a soft band 64 px into the photo, so the join doesn't show
        // (tried on the anime viking: 24 px left hard vertical seams); sides already at the edge are left alone
        const double band = 64;
        double l = at.X > 1 ? at.X + band : -50, t = at.Y > 1 ? at.Y + band : -50;
        double r = at.Right < W - 1 ? at.Right - band : W + 50, b = at.Bottom < H - 1 ? at.Bottom - band : H + 50;
        var mask = new Canvas { Width = W, Height = H, Background = Brushes.White, ClipToBounds = true };
        var keep = new Border { Background = Brushes.Black, Width = r - l, Height = b - t, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 32 } };
        Canvas.SetLeft(keep, l); Canvas.SetTop(keep, t);
        mask.Children.Add(keep);

        var before = new Grid { Width = W, Height = H, Background = Brush("Raised") };
        before.Children.Add(Photo());

        var maskPx = RenderPixels(mask, W, H);
        var repaint = new byte[W * H];
        for (int i = 0; i < repaint.Length; i++) repaint[i] = maskPx[i * 4 + 2];
        return (MaskedPng(canvasPx, W, H, repaint), MaskedPng(RenderPixels(before, W, H), W, H, null));
    }

    static byte[] RenderPixels(FrameworkElement root, int w, int h)
    {
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    // ---------------- crop ----------------
    Rect crop;                          // in photo pixels

    void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (photo == null) return;
        crop = new Rect(photo.PixelWidth * 0.1, photo.PixelHeight * 0.1, photo.PixelWidth * 0.8, photo.PixelHeight * 0.8);
        CropLayer.Visibility = CropTools.Visibility = Visibility.Visible;
        ImageTools.Visibility = BrushRing.Visibility = BrushRingDark.Visibility = Visibility.Collapsed;
        Ink.IsHitTestVisible = Toolbar.IsEnabled = ShapeInput.IsHitTestVisible = false;
        Dispatcher.BeginInvoke(LayoutCrop, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void EndCrop()
    {
        CropLayer.Visibility = CropTools.Visibility = Visibility.Collapsed;
        ImageTools.Visibility = Visibility.Visible;
        Ink.IsHitTestVisible = Toolbar.IsEnabled = true;
        UpdateBrush();
    }

    void CropCancel_Click(object sender, RoutedEventArgs e) => EndCrop();

    void CropApply_Click(object sender, RoutedEventArgs e)
    {
        var r = new Int32Rect((int)crop.X, (int)crop.Y, (int)crop.Width, (int)crop.Height);
        EndCrop();
        Reshape(b => new CroppedBitmap(b, r));    // ponytail: Fit() rounds sides to multiples of 64, so the crop's shape can shift by a few percent
    }

    void LayoutCrop()
    {
        if (CropLayer.Visibility != Visibility.Visible || photo == null) return;
        double px = ScreenPixel(), handle = 14 * px;
        CropShade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, photo.PixelWidth, photo.PixelHeight)), new RectangleGeometry(crop) },
        };
        Canvas.SetLeft(CropBox, crop.X); Canvas.SetTop(CropBox, crop.Y);
        CropBox.Width = crop.Width; CropBox.Height = crop.Height;
        CropBox.Tag = new Thickness(1.5 * px);
        foreach (var (thumb, x, y) in new[] { (CropTL, crop.Left, crop.Top), (CropTR, crop.Right, crop.Top), (CropBL, crop.Left, crop.Bottom), (CropBR, crop.Right, crop.Bottom) })
        {
            thumb.Width = thumb.Height = handle;
            thumb.Tag = new Thickness(px);
            Canvas.SetLeft(thumb, x - handle / 2);
            Canvas.SetTop(thumb, y - handle / 2);
        }
    }

    void CropMove_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        crop.X = Math.Clamp(crop.X + e.HorizontalChange, 0, photo.PixelWidth - crop.Width);
        crop.Y = Math.Clamp(crop.Y + e.VerticalChange, 0, photo.PixelHeight - crop.Height);
        LayoutCrop();
    }

    // corner names say which edges they move: CropTL = top + left
    void CropCorner_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var name = ((FrameworkElement)sender).Name;
        const double min = 64;
        double l = crop.Left, t = crop.Top, r = crop.Right, b = crop.Bottom;
        if (name.EndsWith('L')) l = Math.Clamp(l + e.HorizontalChange, 0, r - min); else r = Math.Clamp(r + e.HorizontalChange, l + min, photo.PixelWidth);
        if (name[4] == 'T') t = Math.Clamp(t + e.VerticalChange, 0, b - min); else b = Math.Clamp(b + e.VerticalChange, t + min, photo.PixelHeight);
        crop = new Rect(new Point(l, t), new Point(r, b));
        LayoutCrop();
    }

    // one screen pixel, in photo pixels: keeps outlines and handles the same size at any zoom
    double ScreenPixel() => 1 / Math.Max(0.01, EditorCanvas.TransformToAncestor(this).TransformBounds(new Rect(0, 0, 1, 1)).Width);

    // a copied file from Explorer, a browser's PNG, or a plain bitmap (screenshots); null when there's no picture
    static (BitmapSource Image, string Name)? ClipboardPicture()
    {
        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => File.Exists(f)) is string file)
            return (Oriented(file), Path.GetFileName(file));
        if (Clipboard.GetData("PNG") is MemoryStream png)
            return (BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), "Pasted image");
        if (Clipboard.ContainsImage())
            return (new FormatConvertedBitmap(Clipboard.GetImage(), PixelFormats.Bgr32, null, 0), "Pasted image");   // Bgr32: bitmaps often arrive with an empty alpha channel
        return null;
    }

    // Ctrl+V outside a text box: the copied picture becomes the photo to edit
    bool PastePhoto()
    {
        try
        {
            if (ClipboardPicture() is not { } p) return false;
            ShowPhoto(p.Image, p.Name);
        }
        catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); }
        return true;
    }

    // phone photos store their rotation in EXIF instead of rotating the pixels
    static BitmapSource Oriented(string path)
    {
        var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        ushort o = 1;
        try { if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort v) o = v; } catch { }
        int angle = o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };      // ponytail: mirrored orientations (2,4,5,7) are rare and ignored
        return angle == 0 ? frame : new TransformedBitmap(frame, new RotateTransform(angle));
    }

    // SDXL works best near one megapixel, with sides in multiples of 64
    static BitmapSource Fit(BitmapSource src)
    {
        double k = Math.Sqrt(1024.0 * 1024 / ((double)src.PixelWidth * src.PixelHeight));
        int w = Math.Max(256, (int)Math.Round(src.PixelWidth * k / 64) * 64), h = Math.Max(256, (int)Math.Round(src.PixelHeight * k / 64) * 64);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    void Tool_Checked(object sender, RoutedEventArgs e) => UpdateBrush();
    void BrushSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateBrush();

    void UpdateBrush()
    {
        if (Ink == null || EraserTool == null || LassoTool == null) return;      // still loading the window
        double s = BrushSlider.Value;
        Ink.DefaultDrawingAttributes = new DrawingAttributes { Color = MarkColor, Width = s, Height = s, FitToCurve = true };
        Ink.EraserShape = new EllipseStylusShape(s, s);
        Ink.EditingMode = InkCanvasEditingMode.None;        // a new eraser shape only applies when the mode is set again
        if (!ShapeTool) Ink.EditingMode = EraserTool.IsChecked == true ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
        ShapeInput.IsHitTestVisible = ShapeTool && CropLayer.Visibility != Visibility.Visible;
        BrushRing.StrokeDashArray = EraserTool.IsChecked == true ? new DoubleCollection { 3, 2 } : null;
    }

    // the outline sits where the brush will paint, at the brush's size
    void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (CropLayer.Visibility == Visibility.Visible || ShapeTool) { Canvas_MouseLeave(null, null); return; }
        var p = e.GetPosition(EditorCanvas);
        double s = BrushSlider.Value, px = ScreenPixel();
        foreach (var (ring, thickness) in new[] { (BrushRingDark, 3 * px), (BrushRing, 1.2 * px) })
        {
            ring.Width = ring.Height = s;
            ring.StrokeThickness = thickness;
            Canvas.SetLeft(ring, p.X - s / 2);
            Canvas.SetTop(ring, p.Y - s / 2);
            ring.Visibility = Visibility.Visible;
        }
    }
    void Canvas_MouseLeave(object sender, MouseEventArgs e) => BrushRing.Visibility = BrushRingDark.Visibility = Visibility.Collapsed;

    void Strip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        StripScroll.ScrollToHorizontalOffset(StripScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // the photo as PNG; ComfyUI's LoadImage turns transparency into its mask (transparent = repaint)
    byte[] EditPng(byte[] mask)
    {
        int w = photo.PixelWidth, h = photo.PixelHeight;
        var px = new byte[w * h * 4];
        photo.CopyPixels(px, w * 4, 0);
        return MaskedPng(px, w, h, mask);
    }

    static byte[] MaskedPng(byte[] px, int w, int h, byte[] mask)
    {
        if (mask != null) for (int i = 0; i < mask.Length; i++) px[i * 4 + 3] = (byte)(255 - mask[i]);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // into ComfyUI's input/forge folder, overwriting the previous edit's photo
    async Task<string> Upload(byte[] png, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new("image/png");
        form.Add(file, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");
        form.Add(new StringContent("forge"), "subfolder");
        using var r = await http.PostAsync("/upload/image", form);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
        if (!r.IsSuccessStatusCode || j?["name"] == null) throw new Exception($"photo upload failed (HTTP {(int)r.StatusCode})");
        var sub = j["subfolder"]?.GetValue<string>();
        return string.IsNullOrEmpty(sub) ? j["name"].GetValue<string>() : $"{sub}/{j["name"].GetValue<string>()}";
    }

    // ---------------- viewer ----------------
    void OpenViewer(Shot s)
    {
        if (s == null || s.Pending) return;
        BitmapSource img;
        try { img = LoadImage(s.FilePath, 0); } catch (Exception ex) { Note("Couldn't open it: " + ex.Message, true); return; }
        viewing = s;
        ViewerImage.Source = img;
        var m = s.Meta;
        VPrompt.Text = m?.Prompt ?? "Not recorded in this image";
        VNegative.Text = string.IsNullOrWhiteSpace(m?.Negative) ? "None" : m.Negative;
        static KeyValuePair<string, string> Row(string k, string v) => new(k, v);
        VMeta.ItemsSource = new[]
        {
            Row("Size", $"{img.PixelWidth} × {img.PixelHeight}"),
            Row("Seed", m?.Seed?.ToString()),
            Row("Steps", m?.Steps?.ToString()),
            Row("Guidance", m?.Cfg?.ToString("0.0")),
            Row("Sampler", m?.Sampler == null ? null : $"{m.Sampler} · {m.Scheduler}"),
            Row("LoRAs", m?.Loras),
            Row("Model", m?.Model == null ? null : Path.GetFileNameWithoutExtension(m.Model)),
            Row("Created", File.GetLastWriteTime(s.FilePath).ToString("d MMM yyyy, HH:mm")),
            Row("Upscaled", m?.Upscaled),
            Row("Made", m?.From),
            Row("File", Path.GetFileName(s.FilePath)),
        }.Where(r => r.Value != null).ToList();
        ViewerNote.Text = "";
        ShowFavorite();
        SetCompare(false);
        BeforeImage.Source = null;
        CompareBtn.Visibility = File.Exists(OriginalOf(s.FilePath)) ? Visibility.Visible : Visibility.Collapsed;
        ShowClip(s);
        ViewerActions.Visibility = s.Plain ? Visibility.Visible : Visibility.Collapsed;     // a secret picture stays out of ComfyUI and other files
        SecretViewedBtn.ToolTip = s.SecretLabel;
        ViewerPanel.Visibility = Visibility.Visible;
        ViewerPanel.Focus();
    }

    void CloseViewer() { SetCompare(false); StopClip(); ViewerPanel.Visibility = Visibility.Collapsed; ViewerImage.Source = BeforeImage.Source = null; viewing = null; }

    // ---------------- before / after ----------------
    // an edit's original photo lives in output/forge/originals under the same file name (not shown in the gallery)
    static string OriginalOf(string path) => Path.Combine(Path.GetDirectoryName(path), "originals", Path.GetFileName(path));

    double split = 0.5;
    bool dragging;

    void Compare_Click(object sender, RoutedEventArgs e) => SetCompare(CompareLayer.Visibility != Visibility.Visible);

    void SetCompare(bool on)
    {
        if (on && (viewing == null || CompareBtn.Visibility != Visibility.Visible)) return;
        if (on && BeforeImage.Source == null)
            try { BeforeImage.Source = LoadImage(OriginalOf(viewing.FilePath), 0); } catch { return; }
        split = 0.5;
        CompareLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CompareText.Text = on ? "Hide comparison" : "Compare with original";
        if (on) Dispatcher.BeginInvoke(LayoutCompare, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void BeforeImage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCompare();

    void LayoutCompare()
    {
        double w = BeforeImage.ActualWidth, h = BeforeImage.ActualHeight;
        if (w == 0 || CompareLayer.Visibility != Visibility.Visible) return;
        BeforeImage.Clip = new RectangleGeometry(new Rect(0, 0, w * split, h));
        var o = BeforeImage.TranslatePoint(new Point(0, 0), CompareLayer);
        double x = o.X + w * split;
        Canvas.SetLeft(Divider, x - 1); Canvas.SetTop(Divider, o.Y); Divider.Height = h;
        Canvas.SetLeft(Handle, x - 18); Canvas.SetTop(Handle, o.Y + h / 2 - 18);
        Canvas.SetLeft(BeforeTag, o.X + 12); Canvas.SetTop(BeforeTag, o.Y + 12);
        Canvas.SetLeft(AfterTag, o.X + w - 12 - AfterTag.ActualWidth); Canvas.SetTop(AfterTag, o.Y + 12);
    }

    void Compare_MouseDown(object sender, MouseButtonEventArgs e) { dragging = true; CompareLayer.CaptureMouse(); MoveSplit(e); e.Handled = true; }
    void Compare_MouseMove(object sender, MouseEventArgs e) { if (dragging) MoveSplit(e); }
    void Compare_MouseUp(object sender, MouseButtonEventArgs e) { dragging = false; CompareLayer.ReleaseMouseCapture(); e.Handled = true; }
    void MoveSplit(MouseEventArgs e)
    {
        split = Math.Clamp(e.GetPosition(BeforeImage).X / Math.Max(1, BeforeImage.ActualWidth), 0, 1);
        LayoutCompare();
    }

    void Step(int d)
    {
        var list = Browsable().ToList();
        int i = list.IndexOf(viewing);
        if (i >= 0 && list.Count > 1) OpenViewer(list[(i + d + list.Count) % list.Count]);
    }

    // arrows walk what you were looking at: the gallery tab (all or favourites), or every image from the editor
    IEnumerable<Shot> Browsable() => (EditMode.IsChecked == true ? shots.Where(s => !s.Secret) : galleryView.Cast<Shot>()).Where(s => !s.Pending);

    void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Step(1);
    void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseViewer();
    void Backdrop_Click(object sender, MouseButtonEventArgs e) => CloseViewer();

    void UsePrompt_Click(object sender, RoutedEventArgs e)
    {
        UsePromptOf(viewing?.Meta);
        CloseViewer();
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    // variations: this image becomes the photo, half-strength repaint with its own prompt and a fresh seed
    void MoreLikeThis_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        if (!ready) { ViewerNote.Text = "ComfyUI isn't ready yet."; return; }
        var s = viewing;
        CloseViewer();
        EditMode.IsChecked = true;
        LoadPhoto(s.FilePath);
        // variations in the picture's own look: the model it was made with, when it's still installed
        if (!VideoOn && imageModels.FirstOrDefault(x => x == s.Meta?.Model || separate.TryGetValue(x, out var u) && u.Unet == s.Meta?.Model) is string own) ModelBox.SelectedItem = own;
        UsePromptOf(s.Meta);
        StrengthSlider.Value = 0.5;
        _ = Generate(newSeed: true);
    }

    // ---------------- prompt history ----------------
    void Remember(string prompt)
    {
        settings.History.Remove(prompt);
        settings.History.Insert(0, prompt);
        if (settings.History.Count > 25) settings.History.RemoveRange(25, settings.History.Count - 25);
    }

    // ---------------- the text model: Improve prompt and Describe image ----------------
    // Qwen-Image 2.1's text encoder (Qwen3-VL 8B) is a whole chat model that can also see a picture, so ComfyUI's TextGenerate
    // writes prompts with it: no extra download.
    // Adult content is the user's call on their own PC. The model gets no "refuse" answer to hide behind (it used one to refuse all sex);
    // anything sexual involving minors is stopped by SexualWithMinor instead, a fixed rule rather than the model's judgement.
    const string Private = "This is a private tool on the user's own computer; the user is an adult and every character is an adult. ";
    const string KeepAsked = "Keep everything the user asked for, including nudity and sex, described plainly and without softening or refusing; " +
        "don't add people or objects that change the idea. Keep any quoted text exactly. ";
    const string VideoSystem = Private + "You write prompts for an AI video generator that makes short clips. Turn the user's idea into one vivid prompt: " +
        "the subject and what it does, how things move, how the camera moves, the setting, lighting, colours and mood. " + KeepAsked +
        "Write one paragraph of 40 to 70 words. Reply with the prompt only.";
    const string VideoFromPictureSystem = Private + "An AI video generator will animate the given picture: the clip starts exactly on it. Look at the picture " +
        "and the user's idea, and write one prompt describing what happens in the clip: how the subject moves, what else moves, and how the camera moves, " +
        "keeping the picture's subject, setting and style. " + KeepAsked + "Write one paragraph of 40 to 70 words. Reply with the prompt only.";
    const string EditInstructionSystem = Private + "An AI photo editor changes the given photo by following an instruction. Look at the photo and the user's " +
        "request, and write one clear, specific instruction: what to change, where it is in the picture, how it should look afterwards, and that everything " +
        "else stays the same. " + KeepAsked + "Write 15 to 45 words. Reply with the instruction only.";
    const string EditResultSystem = Private + "An AI image generator will repaint the given photo from a text prompt. Look at the photo and the user's " +
        "requested change, and write the prompt for the picture as it should look afterwards: keep the photo's subject, pose, composition, setting and style, " +
        "and apply the change. " + KeepAsked + "Write one paragraph of 40 to 80 words. Reply with the prompt only.";

    // ponytail: word lists, so they can be fooled by spelling; they only ever block, never write anything
    static readonly Regex MinorWords = new(@"\b(child|children|kids?|minors?|underage|under-age|teens?|teenage|teenagers?|pre-?teens?|tweens?|loli(s|con)?|shota(con)?|" +
        @"toddlers?|infants?|school ?(girl|boy)s?|little (girl|boy)s?|young (girl|boy)s?|elementary school|middle school|junior high)\b|\b([1-9]|1[0-7]) ?(-| )?(years?[ -]?old|yo|y/o|yrs?)\b",
        RegexOptions.IgnoreCase);
    static readonly Regex SexualWords = new(@"\b(sex|sexual|sexy|nude|nudes|nudity|naked|nsfw|porn|porno|erotic|explicit|topless|bottomless|lingerie|breasts?|boobs?|" +
        @"nipples?|genitals?|penis|cock|dick|vagina|pussy|anus|anal|oral|blowjob|handjob|orgasm|cum|cumshot|fuck\w*|intercourse|cowgirl|doggy ?style|missionary|" +
        @"aroused|horny|undress\w*|seductive|sensual)\b", RegexOptions.IgnoreCase);
    static bool SexualWithMinor(string text) => MinorWords.IsMatch(text) && SexualWords.IsMatch(text);

    // --selftest part: the under-18 block must refuse these and let ordinary prompts through, so a build where it was weakened fails
    static bool SelfTestMinorBlock() =>
        new[] { "nude teen girl", "sexy 16 year old", "naked schoolgirl", "loli lingerie", "explicit photo of a 15yo" }.All(SexualWithMinor)
        && !new[] { "a teen reading a book in a cafe", "nude woman on a beach", "two adults dancing in a park" }.Any(SexualWithMinor);

    static readonly Regex Declined = new(@"^\W*(sorry|i'?m sorry|i am sorry|i can(no|')t|i'?m unable|i am unable|as an ai)", RegexOptions.IgnoreCase);
    const string MinorNote = "Ember won't make sexual content involving anyone under 18.";

    async void Enhance_Click(object sender, RoutedEventArgs e)
    {
        var idea = PromptBox.Text.Trim();
        if (idea == "") { Note("Type a short idea first, then press Improve prompt."); return; }
        if (SexualWithMinor(idea)) { Note(MinorNote, true); return; }
        bool edit = EditMode.IsChecked == true;
        if (edit && photo == null) { Note("Choose a photo first, then describe the change.", true); return; }
        var style = CurrentStyle.Key == "none" ? "" : $"\n(The style: {CurrentStyle.Label}.)";
        // in Edit photo the model sees the photo: Qwen edits by instruction, the other models repaint from a description of the result
        bool fromPicture = VideoOn && PictureVideo && startPicture != null;        // picture-to-video: it looks at the start picture
        var system = edit ? (Qwen ? EditInstructionSystem : EditResultSystem) : fromPicture ? VideoFromPictureSystem : VideoOn ? VideoSystem : EnhanceSystemFor();
        EnhanceText.Text = "Writing…";
        var text = await AskTextModel(idea + style, system, edit ? EditPng(null) : fromPicture ? startPicture : null,
            edit || fromPicture ? "Looking at your picture and writing it out…" : "Writing a detailed prompt…");
        EnhanceText.Text = "Improve prompt";
        if (text == null) return;
        if (PromptBox.Text.Trim() != idea) { Remember(text); Note("The prompt changed while writing, so the new one went to Recent prompts (clock button)."); return; }
        Remember(idea);
        PromptBox.Text = text;
        SaveSettings();
        Note("Prompt improved. Your original is in Recent prompts (clock button).");
    }

    // a picture -> a prompt that recreates it: from a file, a picture dropped on the prompt box, or the viewer
    async Task Describe(string path)
    {
        BitmapSource img;
        try { img = LoadImage(path, 0); } catch { Note("Couldn't open that picture.", true); return; }
        await Describe(img);
    }

    // the Paste image button, or Ctrl+V of a picture in the prompt box
    async Task DescribeClipboard()
    {
        BitmapSource img;
        try { img = ClipboardPicture()?.Image; } catch (Exception ex) { Note("Couldn't paste that: " + ex.Message, true); return; }
        if (img == null) { Note("Copy a picture first: right-click one in your browser and pick Copy image, or take a screenshot.", true); return; }
        await Describe(img);
    }

    async void DescribePaste_Click(object sender, RoutedEventArgs e) => await DescribeClipboard();

    async Task Describe(BitmapSource img)
    {
        double aspect = (double)img.PixelWidth / img.PixelHeight;
        double scale = Math.Min(1, 1024.0 / Math.Max(img.PixelWidth, img.PixelHeight));      // plenty for the model, and quicker
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        var text = await AskTextModel("Write the prompt for this picture.", DescribeSystemFor(), PngOf(img), "Looking at the picture…", maxTokens: 400);
        if (text == null) return;
        if (PromptBox.Text.Trim() is { Length: > 0 } old) Remember(old);
        PromptBox.Text = text;
        Pick(StyleGroup, "none", "none");           // the description already says photo, painting or anime
        // the preset closest in shape to the picture, so the recreation is framed the same way (a custom size is left alone)
        if (CustomSizeBox.IsChecked != true)
            Pick(SizeGroup, SizeGroup.Children.OfType<RadioButton>().Select(r => (string)r.Tag)
                .MinBy(t => { var p = t.Split('x'); return Math.Abs(Math.Log(double.Parse(p[0]) / double.Parse(p[1]) / aspect)); }), "1024x1024");
        SaveSettings();
        Note("Prompt written from the picture, and the size set to match its shape. Your previous prompt is in Recent prompts (clock button).");
    }

    bool writing;       // one request to the text model at a time

    // ComfyUI's TextGenerate with Qwen3-VL 8B, looking at a picture too when given one.
    // Returns the checked answer, or null after saying why not.
    async Task<string> AskTextModel(string prompt, string system, byte[] png, string waiting, int maxTokens = 200)
    {
        // a chosen Ollama model handles text-only requests; anything with a picture needs Qwen3-VL's eyes
        if (png == null && settings.HelperModel?.StartsWith("Ollama: ") == true && !writing)
        {
            writing = true;
            EnhanceBtn.IsEnabled = DescribeFileBtn.IsEnabled = DescribePasteBtn.IsEnabled = false;
            Note(waiting);
            try
            {
                var answer = await AskOllama(settings.HelperModel["Ollama: ".Length..], prompt, system);
                if (answer == "") throw new Exception("the model gave an empty answer");
                if (SexualWithMinor(answer)) { Note(MinorNote, true); return null; }
                if (Declined.IsMatch(answer)) { Note("The text model declined this one. Write the details yourself; your prompt is unchanged.", true); return null; }
                return answer;
            }
            catch (Exception ex) { Note("Ollama couldn't write it (" + ex.Message + "). Is Ollama still running?", true); return null; }
            finally { writing = false; EnhanceBtn.IsEnabled = DescribeFileBtn.IsEnabled = DescribePasteBtn.IsEnabled = true; }
        }
        if (!ready || qwenTe == null) { Note("This needs ComfyUI running and the Qwen-Image 2.1 text encoder.", true); return null; }
        if (writing) return null;
        writing = true;
        EnhanceBtn.IsEnabled = DescribeFileBtn.IsEnabled = DescribePasteBtn.IsEnabled = false;
        Note(waiting + " The first time takes about 30 s.");
        string local = null;
        try
        {
            var gen = new JsonObject
            {
                ["clip"] = Link("1", 0), ["prompt"] = prompt, ["max_length"] = maxTokens, ["system_prompt"] = system,
                ["sampling_mode"] = "on", ["sampling_mode.temperature"] = 0.7, ["sampling_mode.top_k"] = 64, ["sampling_mode.top_p"] = 0.95,
                ["sampling_mode.min_p"] = 0.05, ["sampling_mode.repetition_penalty"] = 1.05, ["sampling_mode.seed"] = Random.Shared.NextInt64(1L << 48),
            };
            var g = new JsonObject
            {
                ["1"] = Node("CLIPLoader", new() { ["clip_name"] = qwenTe, ["type"] = "qwen_image" }),
                ["2"] = Node("TextGenerate", gen),
                ["3"] = Node("PreviewAny", new() { ["source"] = Link("2", 0) }),
            };
            if (png != null)
            {
                var stored = await Upload(png, $"forge_{Guid.NewGuid():N}.png");
                local = Path.Combine(Root, "ComfyUI", "input", stored.Replace('/', Path.DirectorySeparatorChar));
                g["10"] = Node("LoadImage", new() { ["image"] = stored });
                gen["image"] = Link("10", 0);
            }
            using var r = await http.PostAsync("/prompt", new StringContent(new JsonObject { ["prompt"] = g, ["client_id"] = clientId }.ToJsonString(), Encoding.UTF8, "application/json"));
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            if (!r.IsSuccessStatusCode || j?["error"] != null) throw new Exception(j?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {(int)r.StatusCode}");
            var id = j["prompt_id"].GetValue<string>();
            string text = null;
            for (var until = DateTime.Now.AddMinutes(5); text == null; )      // it queues behind any images still being made
            {
                await Task.Delay(1000);
                var h = JsonNode.Parse(await http.GetStringAsync($"/history/{id}"))?[id];
                switch (h?["status"]?["status_str"]?.GetValue<string>())
                {
                    case "success": text = h["outputs"]?["3"]?["text"]?[0]?.GetValue<string>()?.Trim().Trim('"') ?? ""; break;
                    case "error": throw new Exception("ComfyUI couldn't run the text model");
                }
                if (text == null && DateTime.Now > until) throw new Exception("it took too long");
            }
            if (text == "") throw new Exception("the model gave an empty answer");
            if (SexualWithMinor(text)) { Note(MinorNote, true); return null; }
            if (Declined.IsMatch(text)) { Note("The text model declined this one. Write the details yourself; your prompt is unchanged.", true); return null; }
            return text;
        }
        catch (Exception ex) { Note("Couldn't write it: " + ex.Message, true); return null; }
        finally
        {
            if (local != null) try { File.Delete(local); } catch { }
            writing = false;
            EnhanceBtn.IsEnabled = DescribeFileBtn.IsEnabled = DescribePasteBtn.IsEnabled = true;
        }
    }

    static byte[] PngOf(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static readonly string[] PictureTypes = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" };
    static string DroppedPicture(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f && PictureTypes.Contains(Path.GetExtension(f[0]).ToLowerInvariant()) ? f[0] : null;

    // a picture dropped on the prompt box (from Explorer, or a gallery card) is described into it
    void Prompt_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedPicture(e) == null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    async void Prompt_PreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedPicture(e) is not string path) return;
        e.Handled = true;
        await Describe(path);
    }

    async void DescribeFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture to describe", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) == true) await Describe(dlg.FileName);
    }

    async void DescribeViewed_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        var path = viewing.FilePath;
        CloseViewer();
        await Describe(path);
    }

    void History_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = HistoryBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxWidth = 420 };
        if (settings.History.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent prompts yet", IsEnabled = false, Tag = "" });
        foreach (var p in settings.History)
        {
            // a TextBlock header, so underscores in a prompt aren't eaten as keyboard shortcuts
            var item = new MenuItem { Header = new TextBlock { Text = p.Length > 60 ? p[..57] + "…" : p }, ToolTip = p, Tag = "" };
            item.Click += (_, _) => { PromptBox.Text = p; PromptBox.Focus(); PromptBox.CaretIndex = p.Length; };
            menu.Items.Add(item);
        }
        if (settings.History.Count > 0)
        {
            var clear = new MenuItem { Header = "Clear history", Tag = "" };
            clear.Click += (_, _) => { settings.History.Clear(); settings.Save(); };
            menu.Items.Add(clear);
        }
        menu.IsOpen = true;
    }

    void ReuseSeed_Click(object sender, RoutedEventArgs e)
    {
        if (viewing?.Meta?.Seed is not long seed) { ViewerNote.Text = "This image has no seed recorded."; return; }
        SeedBox.Text = seed.ToString();
        RandomSeed.IsChecked = false;
        AdvancedToggle.IsChecked = true;
        ViewerNote.Text = $"Seed {seed} will be used for the next image.";
    }

    void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (viewing == null) return;
        if (viewing.IsVideo)
        {
            var save = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(viewing.FilePath), Filter = "MP4 video|*.mp4" };
            if (save.ShowDialog(this) != true) return;
            try { File.Copy(viewing.VideoPath, save.FileName, true); ViewerNote.Text = "Saved."; }
            catch (Exception ex) { ViewerNote.Text = "Couldn't save: " + ex.Message; }
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(viewing.FilePath),
            Filter = "PNG image (best quality, keeps the prompt)|*.png|JPEG image (smaller file)|*.jpg",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var jpg = new JpegBitmapEncoder { QualityLevel = 92 };
                jpg.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(LoadImage(viewing.FilePath, 0), PixelFormats.Bgr24, null, 0)));
                using var f = File.Create(dlg.FileName);
                jpg.Save(f);
            }
            else File.Copy(viewing.FilePath, dlg.FileName, true);
            ViewerNote.Text = "Saved.";
        }
        catch (Exception ex) { ViewerNote.Text = "Couldn't save: " + ex.Message; }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerImage.Source is BitmapSource b) { Clipboard.SetImage(b); ViewerNote.Text = "Copied to the clipboard."; }
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e) { if (viewing != null) ShowInFolder(viewing.FilePath); }
}
