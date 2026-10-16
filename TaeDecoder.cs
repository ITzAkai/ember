// madebyollin's taeqi2_1.safetensors holds an encoder and a decoder, its decoder layers named decoder.layers.N. ComfyUI's live preview
// for Qwen-Image loads a decoder-only file named taeqi2_1_decoder*, whose first layer is a parameter-free clamp: layers.N becomes N+1.
// Written the way the safetensors library writes files (tensors in name order, packed, the header padded to 8 bytes), so the result
// is byte for byte what its own save_file makes.
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ImageForge;

static class TaeDecoder
{
    public static void Convert(string source, string target)
    {
        using var src = File.OpenRead(source);
        var len = new byte[8];
        src.ReadExactly(len);
        long n = BinaryPrimitives.ReadInt64LittleEndian(len);
        if (n <= 0 || n > 100_000_000) throw new InvalidDataException("Not a safetensors file.");
        var raw = new byte[n];
        src.ReadExactly(raw);
        var head = JsonNode.Parse(raw)!.AsObject();
        long start = 8 + n;

        var picked = new SortedDictionary<string, (string Dtype, string Shape, long Begin, long End)>(StringComparer.Ordinal);
        foreach (var (key, value) in head)
        {
            var m = Regex.Match(key, @"^decoder\.layers\.(\d+)\.(.+)$");
            if (!m.Success || value?["data_offsets"] is not JsonArray off) continue;
            picked[$"{int.Parse(m.Groups[1].Value) + 1}.{m.Groups[2].Value}"] =
                (value["dtype"]!.ToString(), value["shape"]!.ToJsonString(), (long)off[0]!, (long)off[1]!);
        }
        if (picked.Count == 0) throw new InvalidDataException("No decoder layers in that file.");

        var json = new StringBuilder("{");
        long pos = 0;
        foreach (var (name, t) in picked)
        {
            if (json.Length > 1) json.Append(',');
            json.Append($"\"{name}\":{{\"dtype\":\"{t.Dtype}\",\"shape\":{t.Shape},\"data_offsets\":[{pos},{pos + t.End - t.Begin}]}}");
            pos += t.End - t.Begin;
        }
        json.Append('}');
        while (json.Length % 8 != 0) json.Append(' ');
        var header = Encoding.UTF8.GetBytes(json.ToString());

        var tmp = target + ".part";
        using (var dst = File.Create(tmp))
        {
            var size = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(size, header.Length);
            dst.Write(size);
            dst.Write(header);
            var buffer = new byte[1 << 20];
            foreach (var t in picked.Values)
            {
                src.Position = start + t.Begin;
                for (long left = t.End - t.Begin; left > 0;)
                {
                    int read = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) throw new EndOfStreamException();
                    dst.Write(buffer, 0, read);
                    left -= read;
                }
            }
        }
        File.Move(tmp, target, true);
    }
}
