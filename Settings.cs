// Everything Ember remembers between runs, saved as settings.json (see Settings.FilePath).
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
    public bool InpaintEdits { get; set; }                       // painted edits and Expand use the inpainting model, if installed (Remove always does)
    public bool OwnModelEditsSet { get; set; }
    public bool SharpPreviewAll { get; set; }                    // sharp live preview on every model (Qwen and Krea 2 always get it)
    public double StyleStrength { get; set; } = 0.4;             // how strongly a style picture's look carries over (IPAdapter weight)
    public double PoseStrength { get; set; } = 0.8;              // how closely a pose picture's pose is followed (ControlNet strength)
    public string VideoCamera { get; set; } = "As the prompt says";       // the Camera choice for videos
    public double VideoMotion { get; set; } = 12;                         // MiniMax H3 Motion: its sampling shift (12 is its own)
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
    public string HelperModel { get; set; } = "";             // "": Qwen3-VL in ComfyUI; "Ollama: <name>" for a local Ollama model
    public double ReferenceStyle { get; set; } = 0.35;     // how much a non-Qwen model redraws a reference picture
    public bool CkAttention { get; set; }
    public bool Fp16Accumulation { get; set; }
    public bool SpeedOptionsSet { get; set; }
    public double ReserveVram { get; set; }
    public int CacheRam { get; set; }                            // ComfyUI --cache-ram in GB (0: off)
    public Dictionary<string, double> Took { get; set; } = new();   // seconds one picture took, by "model|size|steps": Generate's estimate
    public bool LowMemory { get; set; }                          // ComfyUI keeps no results between runs and pins no RAM
    public Dictionary<string, Boosters> BoostersByFamily { get; set; } = new();   // before per-model profiles: the starting point for models without one
    public Dictionary<string, ModelProfile> Profiles { get; set; } = new();        // each model's own settings
    public List<ModelProfile> Tabs { get; set; } = new();
    public string ActiveTab { get; set; }
    public bool AdultAgreed { get; set; }                   // confirmed 18+ once, so adult bundles show their pictures
    public string BundleAuthor { get; set; }                // the name "Make a bundle" fills in

    // IMAGEFORGE_SETTINGS: another folder for settings, LoRA info and recommendations (test copies leave the real ones alone)
    static readonly string File = Path.Combine(Environment.GetEnvironmentVariable("IMAGEFORGE_SETTINGS") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageForge"), "settings.json");
    public static string FilePath => File;
    // NaN and infinity allowed: one such number (a window that never had a normal size reports infinite bounds) used to make
    // every save throw, silently, so nothing was remembered
    public static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File), JsonOptions) ?? new Settings(); }
        catch { s = new Settings(); }
        // the two speed options, on once for everyone (together they halved every step of Krea 2 on an RTX 3060); switching them off sticks
        if (!s.SpeedOptionsSet) { s.CkAttention = s.Fp16Accumulation = s.SpeedOptionsSet = true; }
        // painted edits with your own model, once for everyone: the SDXL inpainting model follows descriptions poorly; switching it back on sticks
        if (!s.OwnModelEditsSet) { s.InpaintEdits = false; s.OwnModelEditsSet = true; }
        return s;
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(File)); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, JsonOptions)); }
        catch (Exception e) { try { System.IO.File.WriteAllText(File + ".error.txt", $"{DateTime.Now}: {e}"); } catch { } }   // settings are a convenience, but a failure must be findable
    }
}

public class LoraItemSetting
{
    public string Name { get; set; } = "None";
    public double Strength { get; set; } = 0.8;
}
