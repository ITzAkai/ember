<img src="logo.png" width="96" alt="Ember logo">

# Ember

**Make pictures and short videos with AI, on your own Windows PC.**

Type an idea, pick a style, and Ember does the rest: it runs [ComfyUI](https://github.com/comfyanonymous/ComfyUI) for you, picks good settings for each model, and lets you watch the picture form. Keep a character across pictures, compare models side by side, and fix faces and details with one switch. Everything stays on your computer: no account, no cloud, nothing to pay per picture.

**Works with all models.** Use the ones already in your ComfyUI folder, download them from the Models page, or paste a Hugging Face or Civitai link. Models are not included.

Ember was called Image Forge until version 1.8.8; your settings, gallery and models carry over.

## What it does

- **Create** pictures from a prompt, with styles, aspect ratios and up to 4 at once
- **Edit photo**: remove objects, expand the picture, fix details, cut out the background, upscale
- **Video**: text-to-video and picture-to-video
- **Reference pictures**: drop or paste a character or a place, and new pictures keep the same face or place. Save it as a named character to use again
- **Compare models**: the same prompt and seed on several models, one picture each
- **Queue and live view**: click a picture being made to watch it form; right-click a waiting one to cancel it or make it next
- **Improve prompt** and **Describe image** with a local text model
- **LoRA add-ons**: several at once, with descriptions and trigger words looked up automatically
- **Models page**: download models, or paste a Hugging Face / Civitai link, and each file goes in the right folder
- **Settings per model, and tabs**: each model keeps its own settings; save a setup as a tab, export it and share it
- **Quality guard**: warns when a setting is off for the model, with **Fix all** to put it right
- **Quality boosters**: Fix faces, Hi-res fix, refine with a second model, PAG, RescaleCFG and a colour finish
- **Plugins**: optional ComfyUI plugins at pinned, checked versions (Settings > Plugins), and ComfyUI's own editor under **ComfyUI** for experts
- Gallery with favourites, search, and the settings used for each picture, grouped by day (Ctrl+scroll for bigger or smaller pictures)
- **Secret gallery** (the lock tab): right-click a picture > **Move to secret gallery**. It is encrypted with your password and only shows after you type it; it locks again when you leave the tab. There is no password reset: forget it and those pictures can't be opened.

## Install

1. Download `Ember.exe` from [Releases](../../releases/latest) and run it.
   If Windows says it protected your PC, click **More info** → **Run anyway** (the app isn't code-signed).
2. Press **Set up ComfyUI** and pick a folder with about 12 GB free. The app downloads the official [ComfyUI](https://github.com/Comfy-Org/ComfyUI/releases) portable build (2 GB) and sets it up there.
   Already have the ComfyUI portable build? Pick its folder instead.
3. Open **Models** and download a model, or use the ones you already have.

The app updates itself: when a new version is out, it downloads in the background and installs when you close the app, or right away with the **Update** button.

Needs Windows 10/11 x64 and an NVIDIA GPU. If Windows can't unpack ComfyUI by itself (older Windows 10), the app asks you to install [7-Zip](https://www.7-zip.org/).

## Plugins (optional, Settings > Plugins)

| Plugin | Adds |
|---|---|
| Impact Pack + Subpack | Fix faces |
| Inpaint Crop & Stitch | Sharper edits of small painted areas, and Fix faces on more models |
| Ultimate SD Upscale | Upscale > 2× with added detail |
| ComfyUI-GGUF | Smaller GGUF model files for 6–8 GB graphics cards |

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish -c Release -o publish
```

This makes one self-contained `publish\Ember.exe`.
