// LoRAs ComfyUI silently ignores. Files from PEFT-based trainers (fal and others) name their layers "base_model.model.…", which
// ComfyUI doesn't map for Krea 2, Qwen-Image, Anima or Wan: every key is skipped with "lora key not loaded" and the LoRA does
// nothing while looking active (docs/research/boosters-and-comfyui.md, section 7). Such files can be rewritten with ComfyUI's own
// "diffusion_model." prefix: the header is renamed, the weights are copied unchanged.
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ImageForge;

public partial class MainWindow
{
    const string PeftPrefix = "base_model.model.";
    readonly HashSet<string> loraNotApplied = new(StringComparer.OrdinalIgnoreCase);     // seen in ComfyUI's log as "lora key not loaded"

    static JsonObject SafetensorsHeader(string path, out long headerBytes)
    {
        using var f = File.OpenRead(path);
        var len = new byte[8]; f.ReadExactly(len);
        headerBytes = BinaryPrimitives.ReadInt64LittleEndian(len);
        if (headerBytes <= 0 || headerBytes > 100_000_000) throw new InvalidDataException("not a safetensors file");
        var head = new byte[headerBytes]; f.ReadExactly(head);
        return JsonNode.Parse(head).AsObject();
    }

    static bool HasPeftKeys(string path)
    {
        try
        {
            var keys = SafetensorsHeader(path, out _).Select(kv => kv.Key).Where(k => k != "__metadata__").ToList();
            return keys.Any(k => k.StartsWith(PeftPrefix)) && !keys.Any(k => k.StartsWith("diffusion_model."));
        }
        catch { return false; }
    }

    // why a LoRA won't apply to the model it's meant for, or null
    string LoraProblem(string lora)
    {
        var family = loraFamily.GetValueOrDefault(lora);
        if (loraNotApplied.Contains(lora)) return "Not applied: ComfyUI skipped this LoRA's layers on the last picture (unsupported format).";
        if (family is "krea2" or "qwen_image" or "anima" or "wan" && HasPeftKeys(Path.Combine(ModelsDir, "loras", lora)))
            return "Not applied: ComfyUI can't read this file's layer names (base_model.model.…), so it would do nothing. Convert makes a copy it can read.";
        return null;
    }

    // "<name> (ComfyUI).safetensors" next to the original, with base_model.model. → diffusion_model.; returns its file name
    static string ConvertLora(string path)
    {
        var header = SafetensorsHeader(path, out long oldBytes);
        var renamed = new JsonObject();
        foreach (var (key, value) in header.ToList())
        {
            header.Remove(key);
            renamed[key.StartsWith(PeftPrefix) ? "diffusion_model." + key[PeftPrefix.Length..] : key] = value;
        }
        var json = Encoding.UTF8.GetBytes(renamed.ToJsonString());
        int pad = (8 - json.Length % 8) % 8;                            // safetensors keeps the data 8-byte aligned
        var dest = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + " (ComfyUI).safetensors");
        using (var to = File.Create(dest + ".part"))
        using (var from = File.OpenRead(path))
        {
            var len = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(len, json.Length + pad);
            to.Write(len);
            to.Write(json);
            to.Write(Enumerable.Repeat((byte)' ', pad).ToArray());
            from.Seek(8 + oldBytes, SeekOrigin.Begin);
            from.CopyTo(to);
        }
        File.Move(dest + ".part", dest, true);
        return Path.GetFileName(dest);
    }

    async Task ConvertLoraRow(LoraRow row)
    {
        if (row.Box?.SelectedItem is not string lora) return;
        try
        {
            var made = await Task.Run(() => ConvertLora(Path.Combine(ModelsDir, "loras", lora)));
            Note($"Made {made}. If its effect is too strong or weak, adjust the strength: PEFT files often don't carry their scale.");
            await LoadOptions();                    // ComfyUI lists the new file
            if (LoraSearch.Text != "") LoraSearch.Text = "";       // a search could hide the new file
            if (row.Box.Items.Contains(made)) row.Box.SelectedItem = made;
        }
        catch (Exception ex) { Note("Couldn't convert it: " + ex.Message, true); }
    }

    // after a picture: LoRA layers ComfyUI reported as not loaded mark their LoRA as not applied
    void CheckLoraLog(long from)
    {
        try
        {
            using var s = new FileStream(ServerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (s.Length <= from) return;
            s.Seek(from, SeekOrigin.Begin);
            var skipped = Regex.Matches(new StreamReader(s).ReadToEnd(), @"lora key not loaded:\s*(\S+)").Select(m => m.Groups[1].Value).ToHashSet();
            var used = CurrentLoras().Select(l => l.Name).ToList();
            foreach (var l in used)
            {
                var keys = SafetensorsHeader(Path.Combine(ModelsDir, "loras", l), out _).Select(kv => kv.Key);
                if (keys.Any(skipped.Contains)) loraNotApplied.Add(l);
                else loraNotApplied.Remove(l);          // applied this time: a warning from another model or picture no longer holds
            }
            foreach (var r in loraRows) ShowLoraInfo(r);
            if (used.Any(loraNotApplied.Contains)) Note("A LoRA wasn't applied: ComfyUI couldn't read its layers. See the LoRA under Advanced.", true);
        }
        catch { }
    }

    // --selftest part: a PEFT-named LoRA is spotted, and its converted copy has ComfyUI's names and identical weights
    static bool SelfTestLoraConvert()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imageforge-selftest-lora");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "peft.safetensors");
        try
        {
            var header = Encoding.UTF8.GetBytes("""{"__metadata__":{"format":"pt"},"base_model.model.blocks.0.attn.gate.lora_A.weight":{"dtype":"F32","shape":[1,2],"data_offsets":[0,8]}}""");
            var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            using (var f = File.Create(file))
            {
                var len = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(len, header.Length); f.Write(len); f.Write(header); f.Write(data);
            }
            if (!HasPeftKeys(file)) return false;
            var made = Path.Combine(dir, ConvertLora(file));
            var h = SafetensorsHeader(made, out long n);
            var bytes = File.ReadAllBytes(made);
            return !HasPeftKeys(made) && h.ContainsKey("diffusion_model.blocks.0.attn.gate.lora_A.weight") && (8 + n) % 8 == 0
                && bytes.AsSpan((int)(8 + n)).SequenceEqual(data) && h["__metadata__"]?["format"]?.ToString() == "pt";
        }
        catch { return false; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
