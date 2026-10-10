// Settings per model, and model tabs. Every model remembers its own steps, CFG, sampler, size, count, strength, LoRAs, boosters,
// negative prompt and style: switching models saves the old one's and restores the new one's. A tab is a named preset (a model plus
// those settings) that switches everything at once, saves itself as you change things, and exports to JSON for sharing.
// Models without a saved profile start from the older per-family LoRAs and boosters, so nothing from before is lost.
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageForge;

public class ModelProfile
{
    public string Name { get; set; }                // a tab's name; empty for a model's own profile
    public string Model { get; set; }
    public string Quality { get; set; }
    public int Steps { get; set; }
    public double Cfg { get; set; }
    public string Sampler { get; set; }
    public string Scheduler { get; set; }
    public string Size { get; set; }
    public int Count { get; set; } = 1;
    public double Strength { get; set; } = 0.6;
    public string Negative { get; set; } = "";
    public string Style { get; set; } = "none";
    public List<LoraItemSetting> Loras { get; set; } = new();
    public Boosters Boosters { get; set; }
    public string TextEncoder { get; set; }         // null: the automatic choice for the family
    public string Vae { get; set; }
    public bool Saved { get; set; }                 // false: only its boosters are set so far
}

public partial class MainWindow
{
    string profileModel;            // the model whose settings are on screen
    bool applyingProfile;

    static T Clone<T>(T x) where T : class => x == null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(x));

    ModelProfile ProfileOf(string model)
    {
        if (!settings.Profiles.TryGetValue(model, out var p)) settings.Profiles[model] = p = new ModelProfile { Model = model };
        return p;
    }

    // what's on screen, written into a profile (everything but the model, which may already have changed in the drop-down)
    void Capture(ModelProfile p)
    {
        p.Quality = Picked(QualityGroup); p.Steps = Steps; p.Cfg = CfgSlider.Value;
        p.Sampler = SamplerBox.SelectedItem as string ?? p.Sampler; p.Scheduler = SchedulerBox.SelectedItem as string ?? p.Scheduler;
        p.Size = Picked(SizeGroup); p.Count = int.TryParse(Picked(CountGroup), out var n) ? n : 1; p.Strength = StrengthSlider.Value;
        p.Negative = NegativeBox.Text; p.Style = Picked(StyleGroup); p.Loras = CurrentLoras(); p.Saved = true;
    }

    void Apply(ModelProfile p)
    {
        Pick(QualityGroup, p.Quality, "30");
        if (p.Steps > 0) StepsSlider.Value = p.Steps;
        CfgSlider.Value = p.Cfg;
        if (p.Sampler != null && SamplerBox.Items.Contains(p.Sampler)) SamplerBox.SelectedItem = p.Sampler;
        if (p.Scheduler != null && SchedulerBox.Items.Contains(p.Scheduler)) SchedulerBox.SelectedItem = p.Scheduler;
        Pick(SizeGroup, p.Size, "1024x1024");
        Pick(CountGroup, p.Count.ToString(), "1");
        StrengthSlider.Value = p.Strength;
        NegativeBox.Text = p.Negative ?? "";
        Pick(StyleGroup, p.Style, "none");
        LoraRowsPanel.Children.Clear();
        loraRows.Clear();
        var usable = LorasForModel.ToHashSet();
        foreach (var l in p.Loras.Where(l => usable.Contains(l.Name))) CreateLoraRow(l.Name, l.Strength);
        UpdateLoraTitles();
        UpdateLoraChip();
    }

    // called from the model drop-down: before its defaults are applied (save the old model) and after (restore the new one)
    void LeavingModel()
    {
        if (applyingProfile || profileModel == null || loadingOptions) return;
        var old = ProfileOf(profileModel);
        Capture(old);
        // leaving a tab's model by the drop-down: the tab keeps what was on screen and stops being the open one
        if (ActiveTab is { } tab && tab.Model == profileModel && ModelBox.SelectedItem as string != profileModel)
        {
            Capture(tab);
            tab.Boosters = Clone(old.Boosters);
            settings.ActiveTab = null;
            ShowTabs();
        }
    }

    void ArrivedAtModel()
    {
        if (ModelBox.SelectedItem is not string model) return;
        if (!applyingProfile && !loadingOptions && settings.Profiles.TryGetValue(model, out var p) && p.Saved) Apply(p);
        profileModel = model;
    }

    // ---------------- tabs ----------------
    ModelProfile ActiveTab => settings.Tabs.FirstOrDefault(t => t.Name == settings.ActiveTab);

    void SaveActiveTab()
    {
        if (ActiveTab is not { } tab || ModelBox.SelectedItem as string != tab.Model) return;
        Capture(tab);
        tab.Boosters = Clone(CurrentBoosters);
    }

    void ShowTabs()
    {
        TabsBar.Children.Clear();
        foreach (var tab in settings.Tabs)
        {
            bool active = tab.Name == settings.ActiveTab;
            var b = new Button { Content = new TextBlock { Text = tab.Name }, Style = (Style)FindResource("Ghost"), Height = 28, Padding = new Thickness(10, 0, 10, 0), FontSize = 12,
                Margin = new Thickness(0, 0, 6, 6), ToolTip = $"{tab.Model}\nRight-click to rename, duplicate, export or delete" };
            if (active) { b.BorderBrush = Brush("Accent"); b.Foreground = Brush("Accent"); }
            b.Click += (_, _) => OpenTab(tab);
            var menu = new ContextMenu();
            void Item(string text, Action act) { var i = new MenuItem { Header = text }; i.Click += (_, _) => act(); menu.Items.Add(i); }
            Item("Rename…", () => { if (Ask("Rename tab", tab.Name) is string name) { if (active) settings.ActiveTab = name; tab.Name = Unique(name, tab); settings.Save(); ShowTabs(); } });
            Item("Duplicate", () => { var copy = Clone(tab); copy.Name = Unique(tab.Name + " copy", null); settings.Tabs.Add(copy); settings.Save(); ShowTabs(); });
            Item("Export…", () => ExportTabs([tab]));
            Item("Delete", () => { settings.Tabs.Remove(tab); if (active) settings.ActiveTab = null; settings.Save(); ShowTabs(); });
            b.ContextMenu = menu;
            TabsBar.Children.Add(b);
        }
        var add = new Button { Content = "+ Save as tab", Style = (Style)FindResource("Ghost"), Height = 28, Padding = new Thickness(10, 0, 10, 0), FontSize = 12,
            Margin = new Thickness(0, 0, 6, 6), ToolTip = "Keep this model and all its settings as a tab. Right-click to import tabs." };
        System.Windows.Automation.AutomationProperties.SetName(add, "Save as tab");
        add.Click += (_, _) => NewTab();
        var addMenu = new ContextMenu();
        var import = new MenuItem { Header = "Import tabs…" };
        import.Click += (_, _) => ImportTabs();
        addMenu.Items.Add(import);
        if (settings.Tabs.Count > 0)
        {
            var all = new MenuItem { Header = "Export all tabs…" };
            all.Click += (_, _) => ExportTabs(settings.Tabs);
            addMenu.Items.Add(all);
        }
        add.ContextMenu = addMenu;
        TabsBar.Children.Add(add);
    }

    void NewTab()
    {
        if (ModelBox.SelectedItem is not string model) return;
        SaveActiveTab();
        var name = Path.GetFileNameWithoutExtension(model);
        var tab = new ModelProfile { Name = Unique(name.Length > 24 ? name[..24] : name, null), Model = model };
        Capture(tab);
        tab.Boosters = Clone(CurrentBoosters);
        settings.Tabs.Add(tab);
        settings.ActiveTab = tab.Name;
        settings.Save();
        ShowTabs();
        Note($"Saved as the tab \"{tab.Name}\". Changes you make are kept in it.");
    }

    void OpenTab(ModelProfile tab)
    {
        if (!ModelBox.Items.Contains(tab.Model)) { Note($"{tab.Model} isn't installed, so this tab can't open.", true); return; }
        SaveActiveTab();
        LeavingModel();
        applyingProfile = true;
        try
        {
            ModelBox.SelectedItem = tab.Model;
            Apply(tab);
            ProfileOf(tab.Model).Boosters = Clone(tab.Boosters) ?? new Boosters();
            ShowBoosters();
            RefreshGuard();
        }
        finally { applyingProfile = false; }
        profileModel = tab.Model;
        settings.ActiveTab = tab.Name;
        settings.Save();
        ShowTabs();
    }

    string Unique(string name, ModelProfile self)
    {
        var taken = settings.Tabs.Where(t => t != self).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = name;
        for (int i = 2; taken.Contains(result); i++) result = $"{name} {i}";
        return result;
    }

    void ExportTabs(List<ModelProfile> tabs)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export tabs", FileName = tabs.Count == 1 ? tabs[0].Name : "Image Forge tabs", DefaultExt = ".json", Filter = "Image Forge tabs|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(tabs, new JsonSerializerOptions { WriteIndented = true }));
        Note($"Exported {tabs.Count} tab{(tabs.Count == 1 ? "" : "s")}.");
    }

    void ImportTabs()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import tabs", Filter = "Image Forge tabs|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dlg.FileName).TrimStart();
            var tabs = text.StartsWith('[') ? JsonSerializer.Deserialize<List<ModelProfile>>(text) : [JsonSerializer.Deserialize<ModelProfile>(text)];
            foreach (var t in tabs.Where(t => !string.IsNullOrWhiteSpace(t?.Model)))
            {
                t.Name = Unique(string.IsNullOrWhiteSpace(t.Name) ? Path.GetFileNameWithoutExtension(t.Model) : t.Name, null);
                t.Loras ??= new();
                settings.Tabs.Add(t);
            }
            settings.Save();
            ShowTabs();
            var missing = tabs.Where(t => t != null && !ModelBox.Items.Contains(t.Model)).Select(t => t.Model).Distinct().ToList();
            Note($"Imported {tabs.Count} tab{(tabs.Count == 1 ? "" : "s")}." + (missing.Count > 0 ? $" Not installed here: {string.Join(", ", missing)}." : ""));
        }
        catch (Exception ex) { Note("Couldn't read those tabs: " + ex.Message, true); }
    }

    // a one-line question; null when cancelled
    string Ask(string title, string text)
    {
        var box = new TextBox { Text = text, Margin = new Thickness(16, 16, 16, 8), Padding = new Thickness(6, 4, 6, 4), MinWidth = 280 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16), Children = { ok, cancel } };
        var w = new Window { Title = title, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { box, buttons } } };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}
