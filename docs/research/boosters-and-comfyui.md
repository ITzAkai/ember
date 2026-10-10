# Quality boosters and ComfyUI internals (Phase 0 research)

Researched 2026-10-10 against the local ComfyUI 0.39.0 source (torch 2.14 cu130, Python 3.13) on an RTX 3060 12 GB, plus primary sources.
Confidence: **H** = read in code or a primary source, **M** = secondary or inferred, **L** = thin evidence.

## 1. PerturbedAttentionGuidance (PAG): SDXL family only

**It only works on UNet models. On DiT models it changes nothing but still costs an extra model pass on every step.** (H)

- `comfy_extras/nodes_pag.py` hard-codes `unet_block="middle"`, id 0 (lines 26-27).
- It registers `set_model_options_patch_replace(..., "attn1", "middle", 0)` (line 50). Line 51 runs an extra `calc_cond_batch` per step.
- The only reader of `patches_replace["attn1"]` is the UNet `BasicTransformerBlock` (`comfy/ldm/modules/attention.py:1076-1089`, key set at `openaimodel.py:893`).
- Krea 2 has only `attn1_patch` / `attn1_output_patch` hooks (`comfy/ldm/krea2/model.py:86,102`). Qwen-Image 2.1, Wan and SD3 read only `patches_replace["dit"]`. Anima has no such hook.
- So on those models the result is identical to running without PAG.
- **Cost of the wasted pass:** about +50% time at CFG > 1, and +100% at CFG 1. (M)
- **Scale:**
  - Core default 3.0. Diffusers: "`pag_scale=3.0` ... works well in most of the use cases" — https://huggingface.co/docs/diffusers/main/en/using-diffusers/pag
  - pamparamm: higher values "oversaturate/fry" — https://github.com/pamparamm/sd-perturbed-attention
  - Start at 2-3. Consider CFG about 1-2 lower when PAG is on. (M)

## 2. RescaleCFG: only meaningful when CFG > 1

**At CFG 1 it does nothing.** (H)

- `comfy/samplers.py:610` drops the uncond pass at `cond_scale == 1`.
- RescaleCFG (`comfy_extras/nodes_model_advanced.py:276-331`) then returns plain cond.
- **Default multiplier:** 0.7 (range 0-1). Source: Lin et al., "w=7.5, phi=0.7 works great", for zero-terminal-SNR v-prediction models (Section 3.4) — https://ar5iv.labs.arxiv.org/html/2305.08891
- **Where it helps:** v-prediction / zero-terminal-SNR SDXL models (NoobAI v-pred, CFG 4-5 per https://huggingface.co/Laxhar/noobai-XL-Vpred-1.0), and high CFG that burns colours. (M)
- **Flow models** take an x0-space branch with no published validation, so treat it as experimental there.
- **Gotcha:** `model_patcher.py:639-643` has a single `sampler_cfg_function` slot, so the last such patch wins.

## 3. Rebalance-Pack (formerly ComfyUI-ConditioningKrea2Rebalance)

**Repo:** https://github.com/nova452/Rebalance-Pack. Apache-2.0, 533 stars, created 2026-06-23, last push 2026-09-26.

**No independent evidence it improves prompt accuracy; several reports of harm. Don't enable it as a default booster.** (H on code, M on reports)

Krea 2 conditioning is 12 Qwen3-VL taps × 2560 (`comfy/text_encoders/krea2.py:17`).

**Nodes:**
- **ConditioningKrea2Rebalance**
  - Inputs: `multiplier` (default 4.0) and `per_layer_weights` (12 floats, default `1,1,1,1,1,1,1,2.5,5.0,1.1,4.0,1.0`).
  - The defaults scale magnitude about 8.7×.
- **Krea2EncodeRebalance:** encodes with an image-edit template. It doesn't rebalance anything.
- **Krea2EditRebalance:** hard-coded refocusing, with a step schedule ramping up to 20.5× at the end.
- **RebalanceGuider:** returns CONDITIONING, not a guider. It amplifies the positive-minus-negative direction per band.
- **RebalanceCFG:** no CFG involved. It applies a per-step-segment conditioning scale schedule.
- **StepRebalance:** switches between two conditionings at a given step. It does no tensor maths.

**Issues:**
- #1: "Krea2 doesnt know the learnt characters anymore".
- #14: "makes terrible images" from the example workflow.
- #3: black images with the edit nodes, on several GPUs, sometimes tied to fp16 accumulation.
- #13: what users actually use it for is un-filtering, not prompt accuracy.
- The fork https://github.com/huwhitememes/comfyui-krea2-conditioning reports skin artifacts and likeness drift at the default (self-report).
- There are no A/B tests in the repo.

## 4. ColorCorrect (EllangoK/ComfyUI-post-processing-nodes)

**Inputs:**

| Input | Default | Range | Step |
|---|---|---|---|
| temperature | 0 | -100 to 100 | 5 |
| hue | 0 | -90 to 90 | 5 |
| brightness | 0 | -100 to 100 | 5 |
| contrast | 0 | -100 to 100 | 5 |
| saturation | 0 | -100 to 100 | 5 |
| gamma | 1 | 0.2 to 2.2 | 0.1 |

The node runs on the CPU with Pillow and OpenCV, through uint8. (H)

**Status:** last push 2025-01-20, GPL-3.0. https://github.com/EllangoK/ComfyUI-post-processing-nodes

**Blocker:** the pack does `import cv2` at the top level and ships no `requirements.txt`. This install's `python_embeded` has no OpenCV, so the whole pack fails to import. **Doing the colour finish inside Image Forge is simpler and safer.**

## 5. ComfyUI-Manager in ComfyUI 0.39

**Bundling (H):**
- `manager_requirements.txt` pins `comfyui_manager==4.2.2`.
- `--enable-manager` is at `comfy/cli_args.py:161`. Also `--disable-manager-ui` and `--enable-manager-legacy-ui`.
- A fresh portable install doesn't have it. Per https://docs.comfy.org/manager/install, run `python_embeded\python.exe -m pip install -r ComfyUI\manager_requirements.txt`, then start with `--enable-manager`.
- Once v4 is enabled, it blocks legacy `custom_nodes\*comfyui-manager*` folders.

**HTTP API** (https://github.com/Comfy-Org/ComfyUI-Manager/blob/manager-v4/openapi.yaml): (H)
- **Queue a task:** `POST /v2/manager/queue/task` with `{ui_id, client_id, kind, params}`.
  - `kind` is one of install / uninstall / update / fix / disable / enable / install-model.
  - install params: `{id, selected_version, mode, channel}`.
- **Other routes:** `GET /v2/manager/queue/status`, `POST /v2/manager/queue/start`, `GET /v2/customnode/installed`.
- **Installing from a git URL:**
  - Only via the legacy UI route `POST /v2/customnode/install/git_url`.
  - Needs `allow_git_url_install=True`, which is off by default.
  - Nightly and unknown installs need security level "middle" or higher.
- Send `application/json`; simple-form POSTs are rejected.
- `POST /v2/manager/reboot` re-execs without `--windows-standalone-build`, so Image Forge should restart ComfyUI itself.

## 6. Start-up flags (all in `comfy/cli_args.py`) (H)

- **`--disable-all-custom-nodes` + `--whitelist-custom-nodes`** (lines 215-216)
  - Exact, case-sensitive folder-name match (`nodes.py:2379`, `main.py:218`). Single-file nodes need `.py`.
  - With an empty whitelist, prestartup scripts are skipped too.
  - The pip Manager still works under it.
- **`--fast`**
  - Bare `--fast` enables all of `fp16_accumulation`, `fp8_matrix_mult`, `cublas_ops` and `autotune`.
  - `fp16_accumulation` also prefers fp16 over bf16 (`model_management.py:555-557`), which is a likely cause of black images.
- **`--reserve-vram GB`** overrides the default 600 MB reserve on Windows.
- **`--cache-ram`** RAM-pressure caching is already the default.
- **`--use-ck-attention`**
  - INT8 attention: lossy, like SageAttention. Needs SM75+.
  - ComfyUI **exits** if it's unavailable (`attention.py:913-919`).
  - Available on the RTX 3060.

## 7. Krea 2 LoRAs with PEFT/fal key prefixes (H on mapping, M on real files)

**Problem:** `comfy/lora.py` maps Krea 2 keys with these prefixes: `diffusion_model.`, `lora_unet_`, `transformer.` / `lycoris_` / bare (diffusers names). It does **not** map `base_model.model.` for Krea 2; that prefix is only mapped for SD3, PixArt and Hunyuan.

**Fix:** renaming `base_model.model.` to `diffusion_model.` makes the keys load. Verified with a synthetic key map.

**Caveat:** PEFT keeps `lora_alpha` in `adapter_config.json`. Without `.alpha` tensors, ComfyUI uses scale 1.0, so a LoRA trained with alpha ≠ rank needs a strength adjustment.

## 8. nvfp4 / mxfp8 (H on gating, M on speed)

**Fast only on compute capability major ≥ 10** (RTX 50 / Blackwell) (`model_management.py:2044,2054`).

**Older cards:**
- They fall back to "emulated ops", dequantising every forward (`ops.py:1764-1767`, `1415-1416`).
- That keeps the memory saving but gives no speed-up, and can be slower.
- The RTX 30 series (8.6) lacks fp8 compute too; that needs 8.9 (RTX 40).

**App rule:** offer nvfp4/mxfp8 only on compute ≥ 10. Prefer fp8 on 8.9+, and bf16/fp16 or int8 below that.

Source: https://blog.comfy.org/p/new-comfyui-optimizations-for-nvidia

## Consequences for Image Forge

- **PAG:** offer for SDXL-family models only; grey it out with the reason elsewhere.
- **RescaleCFG:** offer only when CFG > 1; on by default for v-prediction models.
- **Krea 2 Rebalance:** don't ship it as a booster. At most an opt-in "Lab" item, off by default, with the issues above shown.
- **Color finish:** do it inside Image Forge, not through the post-processing pack.
- **Launch arguments:** never pass bare `--fast`. Only add `--use-ck-attention` after checking support.
- **Fast start:** use `--disable-all-custom-nodes`, plus an exact-name whitelist when a feature needs a pack.
- **LoRA convert:** `base_model.model.` → `diffusion_model.`, with a strength note when there are no alpha tensors.
