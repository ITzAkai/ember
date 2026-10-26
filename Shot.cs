// A finished picture: what ComfyUI recorded in it (Meta), its looks (StylePreset), and its gallery card (Shot).
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
