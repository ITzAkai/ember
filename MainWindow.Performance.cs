// ComfyUI launch options for speed (docs/research/boosters-and-comfyui.md, section 6): int8 attention where the card supports it
// (ComfyUI refuses to start otherwise), fp16 accumulation (fast on RTX 30 and 40, but linked to black pictures with some LoRAs), and how much
// graphics memory to keep free. Never a bare --fast, which turns on everything at once. Applied when ComfyUI next starts.
using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace ImageForge;

public partial class MainWindow
{
    static double? computeCap;

    // the NVIDIA card's compute capability (8.6 for an RTX 3060, 8.9 for RTX 40, 12.0 for RTX 50), or 0 when there's none
    static double ComputeCap()
    {
        if (computeCap is double c) return c;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=compute_cap --format=csv,noheader") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit();
            computeCap = double.Parse(line.Trim(), CultureInfo.InvariantCulture);
        }
        catch { computeCap = 0; }
        return computeCap.Value;
    }

    bool CkAttentionPossible => ComputeCap() >= 7.5;

    string PerformanceFlags()
    {
        var flags = "";
        if (settings.CkAttention && CkAttentionPossible) flags += " --use-ck-attention";
        if (settings.Fp16Accumulation) flags += " --fast fp16_accumulation";
        if (settings.ReserveVram > 0) flags += " --reserve-vram " + settings.ReserveVram.ToString("0.#", CultureInfo.InvariantCulture);
        if (settings.CacheRam > 0) flags += " --cache-ram " + settings.CacheRam;
        return flags;
    }

    bool fillingPerf;

    void ShowPerformance()
    {
        fillingPerf = true;
        CkAttentionSwitch.IsChecked = settings.CkAttention && CkAttentionPossible;
        CkAttentionSwitch.IsEnabled = CkAttentionPossible;
        Fp16AccSwitch.IsChecked = settings.Fp16Accumulation;
        ReserveVramBox.Text = settings.ReserveVram.ToString("0.#", CultureInfo.InvariantCulture);
        fillingPerf = false;
        double cc = ComputeCap();
        PerfNote.Text = (cc == 0 ? "No NVIDIA card found." : $"Your graphics card has compute level {cc:0.0}. ") +
            (cc >= 10 ? "It runs nvfp4 and mxfp8 model files fast." : cc >= 8.9 ? "nvfp4 and mxfp8 files need an RTX 50 card to be fast; fp8 and int8 files are the quick ones here."
                : cc > 0 ? "nvfp4 and mxfp8 files need an RTX 50 card, and fp8 maths an RTX 40: int8 files are the quick ones here." : "") +
            (CkAttentionPossible ? "" : " Faster attention needs a GTX 16 / RTX card.") + " Changes apply the next time ComfyUI starts.";
    }

    void Perf_Changed(object sender, RoutedEventArgs e)
    {
        if (fillingPerf) return;
        settings.CkAttention = CkAttentionSwitch.IsChecked == true;
        settings.Fp16Accumulation = Fp16AccSwitch.IsChecked == true;
        if (double.TryParse(ReserveVramBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb)) settings.ReserveVram = Math.Clamp(gb, 0, 8);
        settings.Save();
        ShowPerformance();
    }

    // the brief's tested profile for RTX 40 laptops with 6 GB: int8 attention, fp16 accumulation, 0.5 GB kept free, a 4 GB RAM cache
    void PerfPreset_Click(object sender, RoutedEventArgs e)
    {
        settings.CkAttention = CkAttentionPossible;
        settings.Fp16Accumulation = true;
        settings.ReserveVram = 0.5;
        settings.CacheRam = 4;
        settings.Save();
        ShowPerformance();
        PerfNote.Text += " If pictures come out black with a LoRA, switch fp16 accumulation off.";
    }

    void PerfDefault_Click(object sender, RoutedEventArgs e)
    {
        settings.CkAttention = settings.Fp16Accumulation = false;
        settings.ReserveVram = 0;
        settings.CacheRam = 0;
        settings.Save();
        ShowPerformance();
    }
}
