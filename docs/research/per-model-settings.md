# Per-model settings (Phase 0 research, fills the brief's missing table)

Researched 2026-10-10 for ComfyUI 0.39.0.

**Confidence marks**
- **O**: official, i.e. the model card, a ComfyUI template or docs, or the ComfyUI source.
- **A**: the model author's own text.
- **U**: user reports.
- **I**: inference.

**How to read the steps column:** "rec (Fast / Balanced / Best; safe range)". The preset splits are **I** unless noted.

## Facts that change how graphs are built

1. **Built-in shifts in 0.39.0** [S1, O]
   - Krea 2: mu 1.15. Qwen-Image 2.1: a fixed 0.69, which suits about 1 MP. Anima: 3.0. Wan 2.1: 8.0.
   - None of these need a ModelSampling node.
   - The Wan templates add `ModelSamplingSD3` themselves: 8 for text-to-video, 5 for Fun InP.
2. **CFG 1 skips the negative pass** [S4, O]
   - Official cards write "CFG 0" because they use diffusers. In ComfyUI, use **1.0**.
3. **NoobAI Vpred 1.0** carries `v_pred` and `ztsnr` marker tensors.
   - `CheckpointLoaderSimple` reads them and sets v-prediction plus zero-terminal SNR automatically [S1 model_detection.py, O].
   - Add `ModelSamplingDiscrete(v_prediction, zsnr)` only for files without those keys.
4. **Pony "clip skip 2" is already the default.** ComfyUI's SDXL text encoder uses the penultimate layer [S1 sdxl_clip.py, O].
   - `CLIPSetLastLayer -2` therefore does nothing.
   - Never set -1: Pony's author warns of "low quality blobs" without clip skip 2 [S10].

## SDXL family

| Model | Sampler / sched | Steps | CFG (safe) | Neg? | Sizes |
|---|---|---|---|---|---|
| SDXL base 1.0 | dpmpp_2m / karras | 25 (18/25/40; 15–50) | 7 (5–9) | yes | 1024², 1152x896, 1216x832, 1344x768, 1536x640 + flips |
| Pony V6 XL | euler_ancestral / normal | 25 (18/25/35; 15–40) | 7 (5–8) | optional | SDXL buckets |
| Illustrious / WAI | euler_ancestral / normal | 28 (20/28/35; 15–40) | 5.5 (4–7.5) | yes, short | 1024 buckets (Illustrious v1.0: up to 1536²) |
| NoobAI-XL 1.1 (eps) | euler_ancestral / normal | 28 (25/28/32; 22–35) | 5.5 (4.5–6.5) | yes | 768x1344, 832x1216, 896x1152, 1024² + flips |
| NoobAI-XL Vpred 1.0 | **euler** / normal or simple, **not karras** | 30 (28/30/35; 26–40) | 4.5 (4–5) | yes | as 1.1 |
| Animagine XL 4.0 | euler_ancestral / normal | 28 (25/28/30; 22–32) | 5 (4–7) | yes | SDXL buckets incl. 1536x640 |
| Juggernaut XI / Ragnarok | dpmpp_2m_sde / karras | 35 (30/35/40; 25–45) | 4.5 (3–6) | start without | 832x1216, 1216x832 |
| RealVisXL V5 | dpmpp_sde / karras | 35 (30/35/50; 30–50) | 5 [I] (4–7) | yes | SDXL buckets |
| CyberRealistic XL | dpmpp_2m_sde / karras | 32 (30/32/40; 28–45) | 4 (3–5) | short | 832x1216, 896x1152 |
| SDXL-Lightning | euler / sgm_uniform | 2, 4 or 8, to match the checkpoint | 1 | no | 1024-class |
| Juggernaut / RealVis Lightning | dpmpp_sde / karras | 6 (5/6/7; 4–8) | 1.5 (1–2) | short | ≥1024 |
| Hyper (Juggernaut X Hyper) | dpmpp_sde or TCD | 6 (4–8) | 1.5 (1–2) | short | ≥1024 |
| DMD2 / CyberRealistic DMD2 | lcm / sgm_uniform or exponential | 8 (4/8/12; 4–12) | 1 (1–1.5) | no | 1024-class |
| SDXL-Turbo | euler_ancestral / simple | 1–4 | 1 | no | 512 |

### SDXL base 1.0 [S5, S6, S7]
- **Settings:** the template uses 25 steps, CFG 7 and dpmpp_2m/karras at 1024² [S5, O].
- **Card disagrees:** its diffusers example uses 40 steps with the refiner [S6, O].
- **Sizes:** any size with the same pixel count as 1024² [S7, O].

### Pony V6 XL [S10, A]
- **Author's settings:** Euler a, 25 steps, clip skip 2.
- **Prompt prefix:** `score_9, score_8_up, score_7_up, score_6_up, score_5_up, score_4_up`.
- **Negative:** the author says it is "designed to not need negative prompts".
- **CFG:** not published. 6–8 is common [U].

### Illustrious [S11, O]
- Euler a, 20–28 steps, CFG 5–7.5.
- **Negative from the card:** `worst quality, comic, multiple views, bad quality, low quality, lowres, displeasing, very displeasing, bad anatomy, bad hands, scan artifacts, monochrome, greyscale, signature, twitter username, jpeg artifacts, 2koma, 4koma, guro, extra digits, fewer digits`.

### WAI-Illustrious v17 [S12, A]
- Euler a, 15–30 steps, CFG 5–7.
- **Positive:** `masterpiece,best quality,amazing quality`.
- **Negative:** `bad quality,worst quality,worst detail,sketch,censor,`.
- **Author's warning:** too many quality tags or a long negative make images blurrier.
- **Hi-res:** 1.5×, 20 steps, denoise 0.35–0.5.

### NoobAI 1.1 [S14, O]
- Euler a, CFG 5–6, 25–30 steps.
- **Prefix:** `masterpiece, best quality, newest, absurdres, highres, safe,`.
- **Negative:** `nsfw, worst quality, old, early, low quality, lowres, signature, username, logo, bad hands, mutated hands, mammal, anthro, furry, ambiguous form, feral, semi-anthro`.

### NoobAI Vpred 1.0 [S15, O; S16, A]
- **Euler only:** "other samplers will not work properly". Karras schedulers are unsupported.
- CFG 4–5, 28–35 steps.

### Animagine XL 4.0 [S17, O]
- CFG 4–7 (5 recommended), 28 steps, Euler a.
- **Suffix:** `masterpiece, high score, great score, absurdres`.
- **Negative:** `lowres, bad anatomy, bad hands, text, error, missing finger, extra digits, fewer digits, cropped, worst quality, low quality, low score, bad score, average score, signature, watermark, username, blurry`.

### Juggernaut [S18, A; S19, O]
- **Ragnarok:** DPM++ 2M SDE, 30–40 steps, CFG 3–6 ("less is a bit more realistic"), 832x1216. Start without a negative.
- **Hi-res:** 1.5×, denoise 0.3.
- **Sources disagree:** the XI card says DPM++ 2M Karras.

### RealVisXL V5 [S20, A]
- **Sampler:** DPM++ SDE Karras with 30+ steps, or DPM++ 2M Karras with 50+ steps.
- **Negative:** `bad hands, bad anatomy, ugly, deformed, (face asymmetry, eyes asymmetry, deformed eyes, deformed mouth, open mouth)`.
- **Hi-res:** 1.1–1.5×, denoise 0.1–0.3.

### CyberRealistic XL [S22, A]
- DPM++ 2M SDE Karras, 30+ steps, CFG 3–5, clip skip 1.
- **Hi-res:** 1.5×, denoise 0.3–0.35.
- **Negative:** `cartoon, illustration, anime, painting, CGI, 3D render, low quality, watermark, logo, label`.
- **DMD2 variant:** LCM / exponential, 8–12 steps, CFG 1–1.5.

### Few-step SDXL [S23–S28, O]
- **Lightning:** Euler + sgm_uniform. Use the checkpoint that matches the step count.
- **Juggernaut Lightning:** DPM++ SDE, 5–7 steps, CFG 1.5–2.
- **Hyper:** 4–8 steps, CFG 1–2.
- **RealVis V5 Lightning:** 5 steps, CFG 1–2. Hi-res 1.1–1.5× at denoise 0.5.
- **DMD2:** LCM, 4 steps, CFG 1. Lower the LoRA strength on community models.
- **SDXL-Turbo:** 1 step at 512².

**Default hi-res for the SDXL class [I]:** 1.5×, denoise 0.35, about 20 steps. Use 0.25 for realistic checkpoints.

## Krea 2

| Model | Sampler / sched | Steps | CFG | Neg? | Sizes |
|---|---|---|---|---|---|
| Turbo | euler / simple | 8 (6/8/10; 6–10) | 1 only | no (zero-out) | 1K–2K |
| Raw | euler / simple [I] | 52 (30/40/52; 28–60) | 3.5 (3–4.5) | yes | ≤1K |
| Raw + turbo LoRA [I] | euler / simple | 8 (6/8/10) | 1 | no | ≤1K |

The shift (mu 1.15) is built in for all three.

**Turbo** [S2, S29, S30, O]
- The template uses 8 steps, CFG 1, euler/simple and a zero-out negative.
- The card says "8-step distilled" and shows 1024² and 2048² examples.
- **Above CFG 1:** it "tends to burn out" [S31, U].

**Raw** [S33, O]
- The card gives 52 steps and guidance 3.5.
- The card itself says Raw is "not recommended for inference use"; it is a base for finetuning.

**Turbo LoRA (`krea2_turbo_lora_rank_64_bf16`)**
- From its header: 535 tensors, rank 64, no alpha tensors, no metadata. In other words, an extract of Turbo minus Raw.
- **Strength:** no published value. 1.0, safe range 0.8–1.2 [I].
- A community extract reports 40–46% reconstruction error [S34, U].

**Style LoRAs**
- Comfy-Org says 1.0, but the template's `LoraLoaderModelOnly` defaults to 0.8 [S2].

**File variants** [S35]
- Raw comes as bf16, fp8_scaled and int8_convrot. Turbo adds mxfp8 and nvfp4.
- Text encoder: qwen3vl_4b, loaded with `CLIPLoader` type `krea2`. VAE: `qwen_image_vae`.
- GGUF exists only from the community and needs a forked loader (see `custom-nodes.md`).

**Sizes**
- Krea doesn't publish a list of trained buckets.
- The core `ResolutionSelector` offers 1:1, 2:3, 3:2, 3:4, 4:3, 9:16, 16:9 and 21:9 at a megapixel target.

**Test on this PC (2026-10-10): reference pictures don't work on Krea 2 Turbo**
- ComfyUI's Krea 2 code accepts reference latents only when a method is set (`FluxKontextMultiReferenceLatentMethod`).
- With `index` or `offset`, the model ignored the prompt and reproduced the reference, tinted red.
- With `index_timestep_zero`, it ignored the reference and added blocky artifacts.

## Qwen-Image 2.1

| Model | Sampler / sched | Steps | CFG | Neg? | Sizes |
|---|---|---|---|---|---|
| Base | euler / simple | 25 (15/25/40; 12–50) | 1 (1–2) | only if CFG > 1 | 1 MP default; native up to 2048² |
| Edit / reference pictures | euler / simple | 25 (local edits hold at 4–8) | 1 | no | follows image_1; ~1 MP budget |
| Turbo, or base + turbo LoRA | euler / simple [I] | 8 (6–10) | 1 | no | as base |
| Noct Q Anime | euler / simple | 25 (18/25/35; 15–40) | **3** (2.5–4) | **yes** | 1024x1536 in the author's workflow |

**Base** [S4, S36, O]
- **CFG behaviour per the docs:**
  - CFG 2 follows dense text better but sharpens edges.
  - Higher values drift in exposure.
  - 5 "degrades quality badly".
  - 0.5 breaks the image.
- **Steps:** the official pipeline uses about 40, and going from 25 to 40 reduces fizzle in fine detail.
- **Sizes:**
  - Use multiples of 32.
  - Official sizes: 2048², 2400x1792, 2528x1696 and 2752x1536, plus their flips.
  - At 4K and above it "loses prompt adherence".
- **Shift:** ComfyUI's fixed mu of 0.69 suits about 1 MP. Whether 2K needs more is unverified [I].

**Edit / reference pictures** [S4, S37, O]
- **Images:** up to 16 slots, and the card says up to 10. The prompt can refer to them as `<image1>`, `<image2>` and so on.
- **VAE:** connect it, for the strongest reference.
- **Resolution:** this input is a pixel budget. A 12 MP reference costs about 6 s per step, against 0.3 s at 1 MP, so cap it near 1024.

**Turbo** [S38, S39, O; S40, U]
- 8 steps and CFG 1.
- The official LoRA is a lossy SVD extract. Use strength 1.0, range 0.8–1.2 [I].

**Noct Q Anime** [S41, A]
- 25 steps, euler / simple, **CFG 3**, with a negative.
- **Negative:** `low quality, low resolution, blurry, jpeg artifacts, washed-out colors, sloppy lines, messy line art, extra fingers, missing fingers, badly drawn hands, deformed anatomy, 3d render, photograph, photorealistic`.
- **Prompt:** start with "An anime illustration of…" and write full sentences.

## Anima

| Model | Sampler / sched | Steps | CFG | Neg? |
|---|---|---|---|---|
| Base v1.0 | er_sde / simple (card); euler / simple (template) | 30 (24/30/40; 24–50) | 4 (3.5–5; the author allows up to 6) | yes |
| Aesthetic v1.1 | euler or er_sde / simple | 30 (24/30/40) | 3.5 (3–5) | yes, but no `score_*` tags |
| Turbo v1.1 | euler / simple | 10 (8/10/12; 8–12) | 1 | no |
| Base + turbo LoRA v0.2 | euler / simple | 8 | 1 | no |

Shift is a built-in 3.0. Use 512²–1536², best near 1 MP.

**Prompts** [S42, O; S43, A; S2, O]
- **Positive:** `masterpiece, best quality, score_7, safe,`.
- **Negative:** `worst quality, low quality, score_1, score_2, score_3, artist name, blurry, jpeg artifacts, chromatic aberration`. The template adds `sepia`.
- **Weights:** need to be stronger than on SDXL, e.g. `(chibi:2)`.

**Hi-res** [S46, S47, U]
- At most 1.5× per pass, at denoise 0.3–0.35.
- At higher denoise, users get pixelated or blurry results.
- The template itself lists "weak high-res" as a limitation.

**Community sampler notes** [S45, U]
- uni_pc + simple/ddim_uniform is liked.
- CFG++ samplers need CFG below 2.

## Wan 2.1 video

| Model | Sampler / sched | Steps | CFG | Shift | Size / frames |
|---|---|---|---|---|---|
| T2V 1.3B | uni_pc / simple | 30 (20/30/50; 20–50) | 6 (5–7) | 8 (8–12) | 832x480 or 480x832; 4n+1 frames (33 in the example, 81 official); 16 fps |
| Fun InP 1.3B | uni_pc / simple | 20 (15/20/40; 15–50) | 6 (5–7) | 5 (3–5) | template 480x768, 81 frames, 16 fps; trained at 512/768/1024 |

- **T2V** [S8, S9, O]
  - 480p is best; 720p is "less stable".
  - **Sources disagree on shift:** the official diffusers snippet uses flow_shift 3.0 at 480p and 5.0 at 720p. The ComfyUI example uses 8, and the app follows ComfyUI.
- **Fun InP** [S3, S48, S49, O]
  - Official VideoX-Fun uses 480x832, 81 frames, 50 steps, CFG 6, shift 3 at 480p.

## LoRA budgets and stacking

| Family | Per LoRA | Soft total (not counting speed LoRAs) | Basis |
|---|---|---|---|
| SDXL / Pony / Illustrious / NoobAI / Animagine | 0.6–1.0 (0.7–0.8 typical) | ≤1.6, at most 3 LoRAs | I from U |
| Realistic SDXL | 0.5–0.8 | ≤1.2 | I |
| Krea 2 / Qwen 2.1 / Anima | official style LoRAs ship at 1.0 | ≤1.5 | O for the 1.0; I for the cap |
| Wan 1.3B | 1.0–1.5 | ≤1.5 | U |
| Speed LoRA | 1.0 (lower for DMD2 on community checkpoints) | not counted | O |

**Stacking two LoRAs of the same kind** [S50, S51, U]
- Their effects add up in the same direction, so two realism LoRAs go burnt or plastic.
- Typical advice: about 0.5–0.6 each when running two, against 0.8 for one alone. Or keep one main LoRA and put the second at 0.3–0.5.
- Conflicting styles give halos and odd textures.
- If a LoRA dominates the prompt, lower `strength_clip` first.
- One thread argues there's no fixed sum, because LoRA scales aren't normalised.

**App rule [I]**
- Group LoRAs by role: realism, detail, style, character, speed.
- Within a role, scale them so their sum stays at or below the largest single recommended value. Two LoRAs in the same role get about ×0.55 each.

## Gaps

- No official sampler or scheduler for Krea 2 or Qwen 2.1 beyond the ComfyUI templates.
- No official ComfyUI template for Qwen 2.1 Turbo.
- No strength guidance for either extracted turbo LoRA.
- No author CFG for Pony.
- Hi-res numbers for the anime SDXL models are inferred from sibling models.
- Many Krea 2, Qwen 2.1 and Anima community sources are from mid or late 2026 and unverified.
- **All of this still needs the Phase 3b calibration run.**

## Sources
- **S1** ComfyUI source: https://github.com/comfyanonymous/ComfyUI/blob/master/comfy/supported_models.py ; …/model_detection.py ; …/sdxl_clip.py
- **S2** https://github.com/Comfy-Org/workflow_templates/tree/main/templates (`image_krea2_turbo_t2i`, `image_anima_base_v1`, `image_anima_preview`, `image_qwen_image_2_1_t2i`, `image_qwen_image_2_1_image_edit`)
- **S3** https://github.com/Comfy-Org/workflow_templates/blob/main/templates/wan2.1_fun_inp.json
- **S4** https://docs.comfy.org/tutorials/image/qwen/qwen-image-2-1
- **S5** `image_sdxl_simple.json` in S2
- **S6** https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0
- **S7** https://comfyanonymous.github.io/ComfyUI_examples/sdxl/
- **S8** https://comfyanonymous.github.io/ComfyUI_examples/wan/
- **S9** https://huggingface.co/Wan-AI/Wan2.1-T2V-1.3B ; https://github.com/Wan-Video/Wan2.1
- **S10** https://civitai.com/models/257749
- **S11** https://huggingface.co/OnomaAIResearch/Illustrious-xl-early-release-v0
- **S12** https://civitai.com/models/827184
- **S13** https://huggingface.co/OnomaAIResearch/Illustrious-XL-v1.0
- **S14** https://huggingface.co/Laxhar/noobai-XL-1.1
- **S15** https://huggingface.co/Laxhar/noobai-XL-Vpred-1.0
- **S16** https://civitai.com/models/833294
- **S17** https://huggingface.co/cagliostrolab/animagine-xl-4.0
- **S18** https://civitai.com/models/133005
- **S19** https://huggingface.co/RunDiffusion/Juggernaut-XI-v11
- **S20** https://huggingface.co/SG161222/RealVisXL_V5.0
- **S22** https://civitai.com/models/312530
- **S23** https://huggingface.co/ByteDance/SDXL-Lightning
- **S24** https://huggingface.co/RunDiffusion/Juggernaut-XL-Lightning
- **S25** https://huggingface.co/RunDiffusion/Juggernaut-X-Hyper
- **S26** https://huggingface.co/SG161222/RealVisXL_V5.0_Lightning
- **S27** https://huggingface.co/tianweiy/DMD2
- **S28** https://huggingface.co/stabilityai/sdxl-turbo
- **S29** https://huggingface.co/krea/Krea-2-Turbo
- **S30** https://docs.comfy.org/tutorials/image/krea/krea-2
- **S31** https://krea2ai.com/blog/krea-2-lora-training (third-party)
- **S32** https://huggingface.co/lvladikov/Krea2-Turbo-Distill-4step-LoRA
- **S33** https://huggingface.co/krea/Krea-2-Raw
- **S34** https://huggingface.co/TheDivergentAI/krea2-turbo-distill-lora
- **S35** https://huggingface.co/Comfy-Org/Krea-2 ; https://huggingface.co/vantagewithai/Krea-2-Turbo-GGUF
- **S36** https://huggingface.co/Qwen/Qwen-Image-2.1
- **S37** https://docs.comfy.org/built-in-nodes/TextEncodeQwenImage21.md
- **S38** https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo
- **S39** https://huggingface.co/Comfy-Org/Qwen-Image-2.1
- **S40** https://comfyui-wiki.com/en/news/2026-09-27-qwen-image-2-1-turbo8-lora
- **S41** https://huggingface.co/Noctaluna/Noct-Q-Anime-Uncensored-Qwen-Image-2.1
- **S42** https://huggingface.co/circlestone-labs/Anima
- **S43** https://civitai.com/models/2458426/anima
- **S44** https://developer.civitai.com/orchestration/recipes/anima
- **S45** https://huggingface.co/circlestone-labs/Anima/discussions/165
- **S46** https://civitai.com/models/2665798/anima-hiresfix-15x-high-resolution-refinement-workflow
- **S47** https://huggingface.co/circlestone-labs/Anima/discussions/163
- **S48** https://github.com/aigc-apps/VideoX-Fun ; https://huggingface.co/alibaba-pai/Wan2.1-Fun-1.3B-InP
- **S49** https://docs.comfy.org/tutorials/video/wan/fun-inp
- **S50** https://comfylab.dev/blog/guides-pro/how-to-use-loras-comfyui/
- **S51** https://neurocanvas.net/blog/multi-lora-workflows-comfyui/
