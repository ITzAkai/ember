"""Calibration run (developer tool): fixed non-explicit test prompts and seeds, swept over the settings that matter per family,
saved as labelled contact sheets for review. Mirrors Image Forge's own graphs. Re-runnable: finished images are reused.

  python_embeded\\python.exe tools\\calibrate.py --api http://127.0.0.1:8190 --comfy "<ComfyUI folder>" --out docs\\calibration [--only krea2,anima]

Pauses while a game (Steam or Unreal) is in the foreground, so it can run in the background."""
import argparse, ctypes, json, os, time, urllib.request
from PIL import Image, ImageDraw

PROMPTS = [
    ("dance", "two adults dancing together in a sunlit park, full body, natural photo"),
    ("handshake", "two adults shaking hands in a modern office, full body, photo"),
    ("yoga", "a woman holding a yoga warrior pose on a beach at sunrise, full body photo"),
    ("hands", "close-up of two hands holding a ceramic coffee cup, natural window light"),
    ("sofa", "three adult friends sitting on a sofa and laughing, living room, photo"),
    ("sun", "portrait of an elderly man in harsh midday sunlight, photo"),
    ("objects", "a red cup on the left, a blue book on the right, a cat under the table, product photo"),
]
FACES = ["dance", "yoga", "sofa", "sun"]

# family: files, loader type, base settings, and the sweep (label -> overrides)
FAMILIES = {
    "krea2": dict(unet="krea2_turbo_fp8_scaled.safetensors", clip="qwen3vl_4b_fp8_scaled.safetensors", type="krea2", vae="qwen_image_vae.safetensors",
                  base=dict(steps=8, cfg=1.0, sampler="euler", scheduler="simple"), prompts=[p for p, _ in PROMPTS],
                  sweep={"6 steps": dict(steps=6), "8 steps": {}, "10 steps": dict(steps=10), "CFG 1.5": dict(cfg=1.5),
                         "faces 0.3": dict(faces=0.3), "faces 0.4": dict(faces=0.4), "faces 0.5": dict(faces=0.5), "finish": dict(finish=True)}),
    "qwen": dict(unet="qwen_image_2.1_fp8_scaled.safetensors", clip="qwen3vl_8b_qwen_image_2.1_fp8_scaled.safetensors", type="qwen_image", vae="qwen_image_2.1_vae.safetensors",
                 base=dict(steps=25, cfg=1.0, sampler="euler", scheduler="simple"), prompts=["dance", "hands", "sofa", "objects"],
                 sweep={"15 steps": dict(steps=15), "20 steps": dict(steps=20), "25 steps": {}, "30 steps": dict(steps=30), "40 steps": dict(steps=40), "CFG 2": dict(cfg=2.0)}),   # Fix faces fails on Qwen-Image (Impact Pack)
    "anima": dict(unet="anima-base-v1.0.safetensors", clip="anima_baseV10_txt.safetensors", type="stable_diffusion", vae="qwen_image_vae.safetensors",
                  base=dict(steps=30, cfg=4.0, sampler="euler", scheduler="simple"), prompts=[p for p, _ in PROMPTS], style="anime illustration, ",
                  negative="worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, sepia",
                  sweep={"24 steps": dict(steps=24), "30 steps": {}, "40 steps": dict(steps=40), "CFG 3.5": dict(cfg=3.5), "CFG 5": dict(cfg=5.0),
                         "CFG 5 + rescale 0.7": dict(cfg=5.0, rescale=0.7)}),
    "noct": dict(unet="NoctQA_V1_int8_convrot.safetensors", clip="qwen3vl_8b_qwen_image_2.1_fp8_scaled.safetensors", type="qwen_image", vae="qwen_image_2.1_vae.safetensors",
                 base=dict(steps=25, cfg=3.0, sampler="euler", scheduler="simple"), prompts=["dance", "hands", "sofa", "objects"], style="An anime illustration of ",
                 negative="low quality, low resolution, blurry, jpeg artifacts, washed-out colors, sloppy lines, messy line art, extra fingers, missing fingers, badly drawn hands, deformed anatomy, 3d render, photograph, photorealistic",
                 sweep={"18 steps": dict(steps=18), "22 steps": dict(steps=22), "25 steps": {}, "35 steps": dict(steps=35), "CFG 2.5": dict(cfg=2.5), "CFG 4": dict(cfg=4.0)}),
    "sdxl": dict(ckpt="sd_xl_base_1.0.safetensors", base=dict(steps=25, cfg=7.0, sampler="dpmpp_2m", scheduler="karras"),
                 prompts=[p for p, _ in PROMPTS], negative="blurry, low quality",
                 sweep={"25 steps": {}, "CFG 5": dict(cfg=5.0), "PAG 2": dict(pag=2.0), "PAG 3": dict(pag=3.0), "PAG 3 + CFG 5": dict(pag=3.0, cfg=5.0)}),
}
SEED = 20261010
W, H = 832, 1216


def api(base, path, body=None):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None, headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=60).read())


def game_in_front():
    try:
        import psutil
        pid = ctypes.c_ulong()
        ctypes.windll.user32.GetWindowThreadProcessId(ctypes.windll.user32.GetForegroundWindow(), ctypes.byref(pid))
        exe = psutil.Process(pid.value).exe().lower()
        return "steamapps\\common" in exe or "shipping" in os.path.basename(exe)
    except Exception:
        return False


def graph(fam, text, s, prefix):
    g = {}
    if "ckpt" in fam:
        g["4"] = {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": fam["ckpt"]}}
        model, clip, vae = ["4", 0], ["4", 1], ["4", 2]
    else:
        g["4"] = {"class_type": "UNETLoader", "inputs": {"unet_name": fam["unet"], "weight_dtype": "default"}}
        g["44"] = {"class_type": "CLIPLoader", "inputs": {"clip_name": fam["clip"], "type": fam["type"]}}
        g["45"] = {"class_type": "VAELoader", "inputs": {"vae_name": fam["vae"]}}
        model, clip, vae = ["4", 0], ["44", 0], ["45", 0]
    if s.get("pag"):
        g["pag"] = {"class_type": "PerturbedAttentionGuidance", "inputs": {"model": model, "scale": s["pag"]}}; model = ["pag", 0]
    if s.get("rescale"):
        g["rescale"] = {"class_type": "RescaleCFG", "inputs": {"model": model, "multiplier": s["rescale"]}}; model = ["rescale", 0]
    g["6"] = {"class_type": "CLIPTextEncode", "inputs": {"clip": clip, "text": fam.get("style", "") + text}}
    g["7"] = {"class_type": "CLIPTextEncode", "inputs": {"clip": clip, "text": fam.get("negative", "") if s["cfg"] > 1 else ""}}
    g["5"] = {"class_type": "EmptyLatentImage", "inputs": {"width": W, "height": H, "batch_size": 1}}
    g["3"] = {"class_type": "KSampler", "inputs": {"model": model, "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0], "seed": SEED,
              "steps": s["steps"], "cfg": s["cfg"], "sampler_name": s["sampler"], "scheduler": s["scheduler"], "denoise": 1.0}}
    g["8"] = {"class_type": "VAEDecode", "inputs": {"samples": ["3", 0], "vae": vae}}
    image = ["8", 0]
    if s.get("faces"):
        g["finder"] = {"class_type": "UltralyticsDetectorProvider", "inputs": {"model_name": "bbox/face_yolov8m.pt"}}
        g["faces"] = {"class_type": "FaceDetailer", "inputs": {"image": image, "model": model, "clip": clip, "vae": vae, "positive": ["6", 0], "negative": ["7", 0],
                      "bbox_detector": ["finder", 0], "seed": SEED, "steps": s["steps"], "cfg": s["cfg"], "sampler_name": s["sampler"], "scheduler": s["scheduler"],
                      "denoise": s["faces"], "guide_size": 768, "guide_size_for": True, "max_size": 1024, "feather": 5, "noise_mask": True, "force_inpaint": True,
                      "bbox_threshold": 0.5, "bbox_dilation": 10, "bbox_crop_factor": 3.0, "sam_detection_hint": "center-1", "sam_dilation": 0, "sam_threshold": 0.93,
                      "sam_bbox_expansion": 0, "sam_mask_hint_threshold": 0.7, "sam_mask_hint_use_negative": "False", "drop_size": 10, "wildcard": "", "cycle": 1}}
        image = ["faces", 0]
    if s.get("finish"):
        g["finish"] = {"class_type": "ImageForgeFinish", "inputs": {"image": image, "temperature": 0, "hue": 0, "brightness": 0, "contrast": 5, "saturation": 3, "gamma": 1}}
        image = ["finish", 0]
    g["9"] = {"class_type": "SaveImage", "inputs": {"images": image, "filename_prefix": prefix}}
    return g


def run(base, comfy, out, only):
    texts = dict(PROMPTS)
    for name, fam in FAMILIES.items():
        if only and name not in only:
            continue
        cells = {}
        for label, change in fam["sweep"].items():
            for p in fam["prompts"]:
                if (change.get("faces") or change.get("finish")) and p not in FACES:
                    continue
                key = f"{name}_{label.replace(' ', '').replace('+', '_').replace('.', '')}_{p}"
                done = os.path.join(comfy, "output", "calib", key + "_00001_.png")
                if not os.path.exists(done):
                    while game_in_front():
                        print("game in front: waiting", flush=True); time.sleep(60)
                    s = dict(fam["base"]); s.update(change)
                    t = time.time()
                    pid = api(base, "/prompt", {"prompt": graph(fam, texts[p], s, "calib/" + key)})["prompt_id"]
                    while True:
                        time.sleep(2)
                        h = api(base, f"/history/{pid}")
                        if pid in h:
                            status = h[pid]["status"].get("status_str")
                            break
                    print(f"{key}: {status} in {time.time() - t:.0f} s", flush=True)
                cells[(label, p)] = done
        sheet(name, fam, cells, out)


def sheet(name, fam, cells, out):
    labels = list(fam["sweep"])
    prompts = fam["prompts"]
    tw, th = 240, int(240 * H / W)
    img = Image.new("RGB", (120 + tw * len(labels), 30 + th * len(prompts)), "white")
    d = ImageDraw.Draw(img)
    for c, label in enumerate(labels):
        d.text((120 + c * tw + 6, 8), label, fill="black")
    for r, p in enumerate(prompts):
        d.text((6, 30 + r * th + th // 2), p, fill="black")
        for c, label in enumerate(labels):
            f = cells.get((label, p))
            if f and os.path.exists(f):
                im = Image.open(f).convert("RGB"); im.thumbnail((tw - 4, th - 4)); img.paste(im, (122 + c * tw, 32 + r * th))
    os.makedirs(out, exist_ok=True)
    img.save(os.path.join(out, f"{name}.jpg"), quality=85)
    print("sheet", os.path.join(out, f"{name}.jpg"), flush=True)


if __name__ == "__main__":
    a = argparse.ArgumentParser()
    a.add_argument("--api", default="http://127.0.0.1:8188")
    a.add_argument("--comfy", required=True, help="the ComfyUI folder (holds output\\calib)")
    a.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "docs", "calibration"))
    a.add_argument("--only", default="")
    args = a.parse_args()
    run(args.api, args.comfy, args.out, [x for x in args.only.split(",") if x])
