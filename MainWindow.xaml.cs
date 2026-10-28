// Ember: a Windows app for making images with the ComfyUI + SDXL on this PC.
// "Ember.exe" sits in the ComfyUI folder, starts ComfyUI hidden when needed,
// and stops it again on close so the GPU is free.
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        ErrorReport.Watch(this);
        // tests on a hidden desktop: drawn in software, so PrintWindow can take pictures of the window
        if (Environment.GetEnvironmentVariable("IMAGEFORGE_SOFTWARE_RENDER") == "1") System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
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
        WatchExtras();
        WatchGenerateText();
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
        WatchTabChanges();
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
        // never shown in its normal size (opened maximized): no bounds to remember, only Rect.Empty's infinities
        if (!b.IsEmpty && double.IsFinite(b.Left) && double.IsFinite(b.Top) && double.IsFinite(b.Width) && double.IsFinite(b.Height))
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

    // whatever goes wrong reading the window, what was read is still saved, and the problem is written down where it can be found
    // (an empty choice once threw here, before the save, so nothing was remembered and nothing said why)
    void SaveSettings()
    {
        try { ReadSettings(); }
        catch (Exception e) { try { File.WriteAllText(Settings.FilePath + ".error.txt", $"{DateTime.Now}: reading the window: {e}"); } catch { } }
        settings.Save();
    }

    // an option group with nothing picked keeps the value it had
    static int PickedInt(Panel group, int now) => int.TryParse(Picked(group), out var v) ? v : now;

    void ReadSettings()
    {
        if (profileModel != null && !loadingOptions && ModelBox.SelectedItem as string == profileModel) Capture(ProfileOf(profileModel));
        var size = Picked(SizeGroup).Split('x');      // (an open tab keeps what was saved in it: changes go in only with Save changes)
        settings.Prompt = PromptBox.Text; settings.Negative = NegativeBox.Text;
        if (size.Length == 2 && int.TryParse(size[0], out var sw) && int.TryParse(size[1], out var sh)) (settings.Width, settings.Height) = (sw, sh);
        settings.Count = PickedInt(CountGroup, settings.Count); settings.Steps = PickedInt(QualityGroup, settings.Steps); settings.StepsValue = Steps;
        settings.CustomSize = CustomSizeBox.IsChecked == true;
        if (int.TryParse(CustomW.Text, out var cw)) settings.CustomWidth = cw;
        if (int.TryParse(CustomH.Text, out var ch)) settings.CustomHeight = ch;
        settings.Cfg = CfgSlider.Value;
        if (ModelBox.SelectedItem is string model) { if (showingVideoModels) settings.VideoModel = model; else settings.Model = model; }
        if (Picked(VideoSizeGroup) is { Length: > 0 } vs) settings.VideoSize = vs;
        settings.VideoFrames = PickedInt(LengthGroup, settings.VideoFrames);
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
        LoraRowText.Text = on ? $"{active.Count} on" : "None";
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
            Margin = new Thickness(0, 0, 0, 6), ItemTemplate = LoraItemTemplate,
        };
        var items = new[] { "None" }.Concat(LorasForModel).ToList();
        if (loras.Count == 0 && !string.IsNullOrEmpty(loraName) && loraName != "None" && !items.Contains(loraName))
            items.Add(loraName);
        combo.ItemsSource = items;
        combo.SelectedItem = items.Contains(loraName) ? loraName : "None";
        ApplySearch(combo, LoraSearch.Text, LoraSearchText);

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
            tile.ToolTip = StyleTip(tile, tile.IsEnabled ? st.Apply("…") : $"Needs {st.Lora} in ComfyUI\\models\\loras");
        }
    }

    // a style's tooltip: its example picture over what it adds to the prompt
    static StackPanel StyleTip(RadioButton tile, string text) => new()
    {
        Width = 220,
        Children =
        {
            new Border { Height = 130, CornerRadius = new CornerRadius(6), Background = new ImageBrush((ImageSource)tile.DataContext) { Stretch = Stretch.UniformToFill } },
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
        },
    };

    StylePreset CurrentStyle => StylePreset.All.FirstOrDefault(s => s.Key == Picked(StyleGroup)) ?? StylePreset.All[0];

    void BuildStyleTiles()
    {
        foreach (var st in StylePreset.All)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("StyleTile"), GroupName = "style", Tag = st.Key, Content = st.Label,
                DataContext = new BitmapImage(new Uri($"pack://application:,,,/styles/{st.Key}.jpg")),
            };
            tile.Checked += (_, _) =>
            {
                StyleText.Text = st.Label;
                if (st.Key == "logo" && ModelBox.SelectedItem != null && Sdxl)
                    Note("Tip: for lettering in a logo, pick Qwen-Image 2.1 in Advanced settings; this model can't spell.");
            };
            tile.ToolTip = StyleTip(tile, st.Key == "none" ? "No style: only your prompt" : st.Apply("…"));
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
        if ((SettingsPanel.IsVisible || ModelsPanel.IsVisible || BundlesPanel.IsVisible) && e.Key == Key.Escape) { CloseOverlays_Click(null, null); e.Handled = true; return; }
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
    const string Private = "";
    const string KeepAsked = "";
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
        // a mannequin pose adds its words to the picture's prompt; a different pose written here drew two people (one lying, one standing)
        if (!edit && UsingPosePicture && Moss && posePicture.Words is string pose) style += $"\n(The pose, write exactly this one: {pose})";
        // in Edit photo the model sees the photo: Qwen edits by instruction, the other models repaint from a description of the result
        bool fromPicture = VideoOn && (PictureVideo || H3) && startPicture != null;        // picture-to-video: it looks at the start picture
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
    bool plainHelper;   // Qwen3-VL even when MossHelper would swap it

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
                return answer;
            }
            catch (Exception ex) { Note("Ollama couldn't write it (" + ex.Message + "). Is Ollama still running?", true); return null; }
            finally { writing = false; EnhanceBtn.IsEnabled = DescribeFileBtn.IsEnabled = DescribePasteBtn.IsEnabled = true; }
        }
        string te = qwenTe, teType = "qwen_image", plainSystem = system;
        if (!plainHelper && settings.HelperModel == HelperFast && kreaTe != null) (te, teType) = (kreaTe, "krea2");
        int plainMax = maxTokens;
        if (!plainHelper) MossHelper(prompt, png, ref te, ref teType, ref system, ref maxTokens);
        if (!ready || te == null) { Note("This needs ComfyUI running and the Qwen-Image 2.1 text encoder.", true); return null; }
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
                ["1"] = Node("CLIPLoader", new() { ["clip_name"] = te, ["type"] = teType }),
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
            if (te == qwenTe || !Declined.IsMatch(text)) return text;
        }
        catch (Exception ex) { Note("Couldn't write it: " + ex.Message, true); return null; }
        finally
        {
            if (local != null) try { File.Delete(local); } catch { }
            writing = false;
            EnhanceBtn.IsEnabled = DescribeFileBtn.IsEnabled = DescribePasteBtn.IsEnabled = true;
        }
        // a swapped-in text encoder refused: ask the usual helper instead
        plainHelper = true;
        try { return await AskTextModel(prompt, plainSystem, png, waiting, plainMax); }
        finally { plainHelper = false; }
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
