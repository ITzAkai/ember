// Text encoders and VAEs, told apart by what's inside them (tensor names and sizes) rather than by file name, so renamed files
// such as "qwen3-vl-4b-heretic_fp8_e4m3fn" still work. Each model can also pick its own compatible text encoder and VAE.
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace ImageForge;

public partial class MainWindow
{
    static readonly Dictionary<string, string> partKinds = new(StringComparer.OrdinalIgnoreCase);
    List<string> textEncoders = new(), vaeFiles = new();

    static string PlainName(string name) => Regex.Replace(name.ToLowerInvariant(), @"[-_ .]", "");

    // "qwen3vl_8b" (Qwen-Image 2.1, the prompt helper), "qwen3vl_4b" (Krea 2), "qwen3_06b" (Anima), "umt5" (Wan),
    // "qwen3vl_32b" (MiniMax H3), "wan_vae" (the Wan 2.1-type VAE that Krea 2, Anima and Wan share), "qwen21_vae" (Qwen-Image 2.1's own),
    // "h3_video_vae" and "h3_audio_vae" (MiniMax H3's two), or null
    static string PartKind(string folder, string name)
    {
        var file = new[] { folder, folder == "text_encoders" ? "clip" : folder }.Select(d => Path.Combine(ModelsDir, d, name)).FirstOrDefault(File.Exists);
        if (file == null) return KindFromName(name);
        var fi = new FileInfo(file);
        var key = $"{file}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (partKinds.TryGetValue(key, out var known)) return known;
        string kind = null;
        try
        {
            using var f = File.OpenRead(file);
            if (file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                // an H3 text encoder works only with its picture reader (the deepstack merger) and 50 layers: ComfyUI tells it apart by those
                var start = new byte[Math.Min(f.Length, 8 << 20)]; f.ReadExactly(start);
                var names = Encoding.Latin1.GetString(start);
                if (names.StartsWith("GGUF") && names.Contains("deepstack_merger_list.") && names.Contains("blk.49.")) kind = "qwen3vl_32b";
                partKinds[key] = kind ??= KindFromName(name);
                return kind;
            }
            var len = new byte[8]; f.ReadExactly(len);
            long n = BinaryPrimitives.ReadInt64LittleEndian(len);
            if (n > 0 && n < 100_000_000)
            {
                var head = new byte[n]; f.ReadExactly(head);
                using var doc = JsonDocument.Parse(head);
                int[] Shape(string k) => doc.RootElement.TryGetProperty(k, out var t) && t.TryGetProperty("shape", out var s) ? s.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;
                var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                // H3's VAEs first: the audio one's encoder (DAC) also has encoder.block.* tensors, like umT5
                if (keys.Contains("decoder.transformer_blocks.0.scale1")) kind = "h3_video_vae";
                else if (keys.Contains("pre_block.attn.zero_k_bias")) kind = "h3_audio_vae";
                else if (keys.Contains("shared.weight") || keys.Any(k => k.StartsWith("encoder.block."))) kind = "umt5";
                else if (keys.FirstOrDefault(k => k.EndsWith("embed_tokens.weight")) is string emb && Shape(emb) is [_, var hidden])
                {
                    bool vision = keys.Any(k => k.Contains("visual."));
                    kind = hidden == 4096 && vision ? "qwen3vl_8b" : hidden == 5120 && vision ? "qwen3vl_32b" : hidden == 2560 ? "qwen3vl_4b" : hidden == 1024 ? "qwen3_06b" : null;
                }
                else if (Shape("decoder.head.2.weight") is { } h) kind = h is [3, ..] ? "wan_vae" : "qwen21_vae";
            }
        }
        catch { }
        partKinds[key] = kind ??= KindFromName(name);
        return kind;
    }

    // for files without a readable safetensors header (GGUF and the like): the name, ignoring case, '-', '_' and spaces
    static string KindFromName(string name)
    {
        var n = PlainName(name);
        return n.Contains("qwen3vl8b") ? "qwen3vl_8b" : n.Contains("qwen3vl4b") ? "qwen3vl_4b" : n.Contains("qwen306b") || n.Contains("anima") && n.Contains("txt") ? "qwen3_06b"
            : n.Contains("umt5") ? "umt5" : n.Contains("qwenimage21vae") ? "qwen21_vae" : n.Contains("qwenimagevae") || n.Contains("wan21vae") || n.Contains("wanvae") ? "wan_vae" : null;
    }

    static string TeKind(string family) => family switch { "qwen_image" => "qwen3vl_8b", "krea2" => "qwen3vl_4b", "anima" => "qwen3_06b", "minimax_h3" => "qwen3vl_32b", _ => "umt5" };
    static string VaeKind(string family) => family switch { "qwen_image" => "qwen21_vae", "minimax_h3" => "h3_video_vae", _ => "wan_vae" };

    // the first file of that kind, preferring a name that is a hint, then one that contains it
    string FindPart(List<string> files, string folder, string kind, params string[] prefer) =>
        files.Where(f => PartKind(folder, f) == kind)
             .OrderByDescending(f => prefer.Select(p => PlainName(f) == p ? 2 : PlainName(f).Contains(p) ? 1 : 0).DefaultIfEmpty(0).Max()).FirstOrDefault();

    void FindParts(List<string> tes, List<string> vaes)
    {
        textEncoders = tes; vaeFiles = vaes;
        qwenTe = FindPart(tes, "text_encoders", "qwen3vl_8b");
        qwenVae = FindPart(vaes, "vae", "qwen21_vae");
        kreaTe = FindPart(tes, "text_encoders", "qwen3vl_4b");
        kreaVae = FindPart(vaes, "vae", "wan_vae", "qwenimagevaesafetensors", "qwenimagevae");         // Krea 2 and Anima ship with Qwen-Image's copy of the Wan 2.1 VAE
        animaTe = FindPart(tes, "text_encoders", "qwen3_06b");
        wanTe = FindPart(tes, "text_encoders", "umt5");
        wanVae = FindPart(vaes, "vae", "wan_vae", "wan");
        h3Te = FindPart(tes, "text_encoders", "qwen3vl_32b", "minimaxh3");
        h3Vae = FindPart(vaes, "vae", "h3_video_vae", "int8");              // the int8 copy decodes about twice as fast, as in ComfyUI's template
        h3AudioVae = FindPart(vaes, "vae", "h3_audio_vae", "fp32");         // in fp16 or bf16 the sound comes out as a buzz
    }

    string AutoTe(string family) => family switch { "qwen_image" => qwenTe, "krea2" => kreaTe, "anima" => animaTe, "minimax_h3" => h3Te, _ => wanTe };
    string AutoVae(string family) => family switch { "qwen_image" => qwenVae, "krea2" or "anima" => kreaVae, "minimax_h3" => h3Vae, _ => wanVae };

    // this model's own choice when it's still installed and fits the family, else the automatic one
    string TeFor(string family, string model = null) =>
        (model ?? ModelBox.SelectedItem) is string m && settings.Profiles.GetValueOrDefault(m)?.TextEncoder is string te
        && textEncoders.Contains(te) && PartKind("text_encoders", te) == TeKind(family) ? te : AutoTe(family);

    string VaeFor(string family, string model = null) =>
        (model ?? ModelBox.SelectedItem) is string m && settings.Profiles.GetValueOrDefault(m)?.Vae is string vae
        && vaeFiles.Contains(vae) && PartKind("vae", vae) == VaeKind(family) ? vae : AutoVae(family);

    // what a text encoder or VAE belongs to, for the Models page
    static string PartFamily(string folder, string name) => PartKind(folder, name) switch
    {
        "qwen3vl_8b" or "qwen21_vae" => "qwen_image", "qwen3vl_4b" => "krea2", "qwen3_06b" => "anima", "umt5" => "wan",
        "qwen3vl_32b" or "h3_video_vae" or "h3_audio_vae" => "minimax_h3",
        "wan_vae" => PlainName(name).Contains("wan") ? "wan" : "krea2", _ => null,
    };

    // ---------------- the pickers in Advanced ----------------
    bool fillingParts;

    void ShowParts()
    {
        if (PartsSection == null) return;
        var family = Family is "wan_i2v" ? "wan" : Family;
        PartsSection.Visibility = family == null || ModelBox.SelectedItem == null ? Visibility.Collapsed : Visibility.Visible;
        if (family == null) return;
        fillingParts = true;
        TeBox.ItemsSource = textEncoders.Where(f => PartKind("text_encoders", f) == TeKind(family)).ToList();
        TeBox.SelectedItem = TeFor(family);
        VaeBox.ItemsSource = vaeFiles.Where(f => PartKind("vae", f) == VaeKind(family)).ToList();
        VaeBox.SelectedItem = VaeFor(family);
        fillingParts = false;
    }

    void Parts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (fillingParts || ModelBox.SelectedItem is not string m) return;
        var p = ProfileOf(m);
        var family = Family is "wan_i2v" ? "wan" : Family;
        p.TextEncoder = TeBox.SelectedItem as string is string te && te != AutoTe(family) ? te : null;
        p.Vae = VaeBox.SelectedItem as string is string vae && vae != AutoVae(family) ? vae : null;
        settings.Save();
    }
}
