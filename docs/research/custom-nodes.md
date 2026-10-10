# Custom node packs: what's worth installing (Phase 0 research)

Researched 2026-10-10 for ComfyUI 0.39.0, Python 3.13 and torch 2.14 cu130.

**What the labels mean.** "User report" means a GitHub issue, PR or Hugging Face thread. "Author claim" means a README or model card.

**Limits.** Reddit and Civitai weren't reachable, and nothing was run on a GPU. Pins are commit short SHAs, because most repos have no tags.

**Left out on purpose:** face-swap tools, sexual-content tools, and anything marketed for a youthful look.

## 0. Already in ComfyUI 0.39.0 core (no pack needed)

**Detection and pose** (https://github.com/comfyanonymous/ComfyUI/tree/v0.39.0/comfy_extras)
- **MediaPipe Face Landmarker:** pure PyTorch, with face bbox and face-mask nodes. 5.4 MB, Apache-2.0. https://huggingface.co/Comfy-Org/mediapipe
- **SDPose whole-body pose:** multi-person. 1.92 GB, plus 124 MB for RT-DETRv4. Includes `SDPoseFaceBBoxes` and `CropByBBoxes`. https://docs.comfy.org/tutorials/utility/pose-detection-sdpose.md
- **`SAM3_Detect`:** detects things from a text prompt. 1.75 GB. https://huggingface.co/Comfy-Org/sam3.1

**Guidance and image nodes**
- **NAGuidance:** hooks Flux-code models, Chroma, HunyuanVideo and **Krea 2**. Anima and SDXL have no hook.
- **Also included:** APG, CFGZeroStar, PerpNeg, TCFG, DifferentialDiffusion, ImageSharpen.

**Control models**
- **Anima-LLLite:** pose 23 MB, plus depth and inpaint. Non-commercial licence. https://huggingface.co/Comfy-Org/Anima-LLLite
- **Qwen-Image 2.1 Fun ControlNet Union:** 3.78 GB int8. https://huggingface.co/Comfy-Org/Qwen-Image-2.1

**What this means:** a face-detail pass (detect the face, crop, re-sample, paste back) and pose extraction can be built without any packs. Untested: whether that matches FaceDetailer.

## Ranked candidates

### 1. ComfyUI-Inpaint-CropAndStitch
**Repo:** https://github.com/lquesada/ComfyUI-Inpaint-CropAndStitch. Pin `8584b08d85` (v3.0.17, 2026-09-23). GPL-3.0, 1,191 stars, 0 open issues.

**Works with:** any image model, since it only handles images and masks.
- **Qwen 2.1:** a stitch crash was fixed by the author within 2 days (#179).
- **Krea 2:** a reported blur came from the output-resize option (#176).

**Cost:** no dependencies, no downloads, under 1 MB. Adds no VRAM, and since only the crop gets sampled, it helps on 6 GB cards.

### 2. ComfyUI-Impact-Pack + Impact-Subpack (FaceDetailer, RegionalSampler)
**Repos and pins:**
- https://github.com/ltdrdata/ComfyUI-Impact-Pack: `429d0159ad` (v8.28.3, 2026-04-19).
- https://github.com/ltdrdata/ComfyUI-Impact-Subpack: `50c7b71a6a` (v1.3.5).

**By model family:**
- **SDXL family:** the standard use case.
- **Krea 2:** a user shares working detailer workflows (#1212).
- **Qwen 2.1:** a bug exists only in DetailerDebug (#1240).
- **Anima:** FaceDetailer crashes ("H,W should be divisible by spatial_patch_size 2", #1186, open since February). The workaround of snapping sizes to multiples of 16 isn't merged.
- **Wan:** not useful.
- **RegionalSampler:** works on SDXL and Anima, but Euler a gives noise (#1206).

**Dependencies:**
- **Pack:** segment-anything, scikit-image, piexif, transformers, opencv-python-headless, scipy, matplotlib, dill, and `git+…/sam2`.
- **Subpack:** ultralytics ≥ 8.3.162.
- **sam2** is an optional import, so drop the git line; portable ComfyUI has no git.
- **Size:** about 100–200 MB of pip packages.
- **Risk:** one report of pip pulling torch below 2.10 on cu130 (Subpack #78, open).

**Detection models:** https://huggingface.co/Bingsu/adetailer (Apache-2.0)
- `face_yolov8m.pt` (52 MB), `hand_yolov8s.pt` (22.5 MB), `person_yolov8m-seg.pt` (55 MB).
- They go in `models/ultralytics/bbox` and `models/ultralytics/segm`. Users often get this wrong (#79).

**Problems:**
- Intermittent NaN or black images with Qwen after many runs (#1144).
- No independent evidence that the hand detector fixes finger counts.

**Maintenance is weak:** 5 commits in 2026 and 260 open issues. Its JavaScript breaks on frontend 1.47+ (#1230); that only matters in ComfyUI's own web UI.

**Licences:** GPL-3.0 (pack) and AGPL-3.0 (subpack and ultralytics).

### 3. ComfyUI-ppm (Attention Couple, NegPiP)
**Repo:** https://github.com/pamparamm/ComfyUI-ppm. Pin `6c6c360155` (v1.1.10, 2026-08-09). AGPL-3.0, 271 stars, active.

**Works with:** SD1, SDXL (NoobAI examples) and Anima. Not Krea 2, Qwen or Wan.

**What it does:** Attention Couple keeps two characters apart. It also includes RescaleCFG, a guidance limiter and CFG++ samplers.

**Evidence:**
- The author reproduced Anima coupling (#48).
- A user saw an inconsistent collapse to one character when using LoRAs.
- NegPiP crashes with the Anima-Regional-Conditioning pack (#55).

**Cost:** no dependencies, no downloads.

### 4. ComfyUI_UltimateSDUpscale
**Repo:** https://github.com/ssitu/ComfyUI_UltimateSDUpscale. Pin `a5547db9e1` (v1.7.2, 2026-06-22). GPL-3.0, 66 open issues.

**Works with:** any model in principle. No reports for Krea 2, Anima or Qwen text-to-image.

**Problems:**
- Edit-model inputs fail (#163).
- One unexplained "unusable on 0.30+" report (#188). Treat 0.39 as unverified.
- On first start it downloads A1111 script files from GitHub master, unpinned.
- Name clashes with other packs over `modules` (#184).

### 5. comfyui_controlnet_aux + OpenPose ControlNets
**Repo:** https://github.com/Fannovel16/comfyui_controlnet_aux. Pin `0cd2904771` (v1.1.6, 2026-09-28). Apache-2.0, 276 open issues.

**ControlNet per model family:**
- **SDXL family:** xinsir/controlnet-openpose-sdxl-1.0 (2.5 GB, Apache-2.0).
- **Anima:** core LLLite pose instead.
- **Qwen 2.1:** core Fun ControlNet Union instead.
- **Krea 2:** only a 229 MB pose LoRA that needs extra packs, with no multi-person support.

**Dependencies are heavy:** about 350 MB, including opencv-python (clashes with headless), mediapipe, onnxruntime-gpu, scikit-learn, albumentationsx, trimesh and rtree. DWPose falls back to slow CPU when onnxruntime lacks CUDA (#614).

**Models download on first use:** about 700 MB.

**VRAM:** an SDXL ControlNet adds 2.5 GB, so a 6 GB card needs offloading.

**Two-person scenes:** thin evidence. **Prefer core SDPose.**

### 6. ComfyUI-Anima-NAG
**Repo:** https://github.com/hybskgks28275/ComfyUI-Anima-NAG. Pin `1590401343` (v1.0.1). MIT, 11 stars, no dependencies.

**How it works:** wraps `optimized_attention_override`, which still exists in 0.39. No 0.39 reports either way.

**Author's settings (no benchmarks):** Turbo LoRA, CFG 1, 8–12 steps, scale 2.0, tau 2.5, alpha 0.5.

**Only relevant for Anima Turbo at CFG 1.** Anima base at CFG 4–5 already uses real negatives.

**Alternative, not recommended:** BigStationW/ComfyUI-NAG-Extended. A user reports it about halves speed (#3).

### 7. ComfyUI-post-processing-nodes (ColorCorrect)
**Repo:** https://github.com/EllangoK/ComfyUI-post-processing-nodes. Pin `c49a052547` (v1.0.1, 2025-01-20). GPL-3.0. Stale but simple.

**Problem:** it imports `cv2` without a requirements file, so it **fails to import unless OpenCV is installed**. That's the case on this PC.

**Alternative:** do the same adjustment in Image Forge itself.

### 8. ComfyUI_IPAdapter_plus
**Repo:** https://github.com/cubiq/ComfyUI_IPAdapter_plus. Pin `a0f451a511` (v2.0.0). **"Maintenance only" since 2025-04-14.**

**Works with:** SDXL, SD1.5 and Kolors only.

**Models:** about 3.4 GB (https://huggingface.co/h94/IP-Adapter). That's heavy on 6 GB.

**What it's for:** style reference, not accuracy.

### 9. ComfyUI-GGUF
**Upstream, city96:** https://github.com/city96/ComfyUI-GGUF (`6ea2651e7d`). Loads neither Krea 2 (#464) nor Qwen-Image 2.1 (PR #483). No commits since January.

**Fork, leejet:** https://github.com/leejet/ComfyUI-GGUF (`373048b840`, 2026-09-25). Adds `krea2` and `qwen_image21`.
- **Qwen 2.1 GGUF fails on ComfyUI 0.39** (`input_act` TypeError; leejet #4, city96 #487).
- **GGUF text encoders also break** (city96 #488).
- **Krea 2 DiT GGUF** should load with a safetensors text encoder, but that's untested.

**Files:**
- **Qwen 2.1:** Q4_K 4.20 GB up to Q8_0 7.69 GB (https://huggingface.co/leejet/Qwen-Image-2.1-GGUF).
- **Krea 2:** Q4_0 7.74 GB up to Q8_0 13.56 GB (https://huggingface.co/molbal/krea2-gguf).
- **SDXL** can't be usefully GGUF-quantised.

**Native alternative:** the official int8 and w4a8 files, which need no pack.

### 10–11. Spectrum-sdxl / Spectrum-Anima-v2
**Repos:** https://github.com/ruwwww/ComfyUI-Spectrum-sdxl and https://github.com/Erehr/ComfyUI-Spectrum-Anima-v2

**What they do:** speed only. They skip model calls by forecasting features. They don't improve accuracy.

**Problems:**
- The SDXL one changes the output when ComfyUI splits batches on low VRAM (#4).
- The Anima one is 3 days old, with only the author's benchmark (10–24% faster, PSNR about 29 dB against normal output).

**Skip both.**

### 12. Rebalance-Pack
**Repo:** https://github.com/nova452/Rebalance-Pack. Pin `53c147c72c` (v2.3.0).

**Problems:** black-output reports (#3) and a missing-function report (#15). It's a grab-bag that also includes an "Omni Node" that runs pasted code. See `boosters-and-comfyui.md` for its effect.

### Also seen
**Detail Daemon** (https://github.com/Jonseed/ComfyUI-Detail-Daemon, MIT): lists SDXL and Qwen as working. Krea 2 is an open question (#25). No accuracy evidence.

## Recommendation

**Checked by default**
- **Inpaint-CropAndStitch:** no dependencies, a responsive maintainer, and every image family. It's the building block for re-doing faces and hands.
- **Impact Pack + Subpack + `face_yolov8m.pt`:** the only turnkey face fix.
  - Skip the `sam2` git line and pin the commit.
  - Enable it for SDXL. Allow Krea 2 and Qwen with a warning.
  - Hide it for Anima until #1186 is closed.
  - `hand_yolov8s.pt` stays optional.
- **ComfyUI-ppm:** keeps characters apart on SDXL and Anima, with no dependencies.

**Optional**
- **UltimateSDUpscale:** unverified on 0.39, and it downloads a script on first run.
- **Anima-NAG:** only if Anima Turbo is offered at CFG 1.
- **IPAdapter_plus:** large models, maintenance only.
- **leejet ComfyUI-GGUF:** Krea 2 only, experimental.
- **controlnet_aux:** only for extracting poses from photos; prefer core SDPose.

**Don't include**
- **city96 ComfyUI-GGUF:** can't load Krea 2 or Qwen 2.1.
- **Spectrum packs:** speed only, and they change output on low VRAM.
- **controlnet_aux as a default:** heavy dependencies and an OpenCV conflict.
- **Rebalance-Pack:** no evidence of benefit, and it can run pasted code.
- **IPAdapter FaceID / insightface:** out of scope, and a brittle install.
- **NAG-Extended:** about half the speed.
- **The post-processing pack:** do the colour finish in the app instead.
