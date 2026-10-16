<img src="logo.png" width="96" alt="Ember logo">

# Ember

**Make pictures and short videos with AI, on your own Windows PC.**

Type an idea, pick a style, and Ember does the rest: it runs [ComfyUI](https://github.com/comfyanonymous/ComfyUI) for you, picks good settings for each model, and lets you watch the picture form. Keep a character across pictures, compare models side by side, and fix faces and details with one switch. Everything stays on your computer: no account, no cloud, nothing to pay per picture.

**Works with all models.** Use the ones already in your ComfyUI folder, download them from the Models page, or paste a Hugging Face or Civitai link. Models are not included.

## What it does

### Create
- **Pictures from a few words**: type an idea, pick a style and a shape, and get up to 4 pictures at once
- **Improve prompt**: turns a short idea into a rich, detailed prompt
- **Describe image**: drop or paste any picture and get the prompt that recreates it
- **Short videos**: from a prompt, or bring one of your pictures to life; pick a camera move, and with MiniMax H3 set an end picture, the amount of motion and up to 10 s
- **Two people, described apart**: give each person their own description, and their looks never mix

### Characters and style
- **Reference pictures**: drop or paste a character or a place, and new pictures keep the same face, hair and clothes, in any model's style
- **Saved characters**: keep them under a name and bring them back in one click
- **Style picture**: new pictures take on the colours, brushwork and light of a picture you love

### Edit
- **Paint to change**: brush over part of a picture and describe what goes there, or remove things
- **Expand** a picture past its edges, **cut out the background**, and **upscale** 2× or 4×
- **More like this**: fresh variations close to a picture you love

### Watch and steer
- **Live view**: click a picture while it's being made and watch it take shape
- **Queue**: line up as many as you like; right-click one to cancel it or make it next
- **Compare models**: the same prompt and seed on several models, side by side

### Better pictures, without the guesswork
- **Settings that fit each model**: Ember knows good steps, sampler and size for each model, warns when something is off, and fixes it in one click
- **Quality boosters**: fix faces, hi-res fix, refine with a second model, sharper structure and a colour finish, one switch each
- **LoRA add-ons**: mix several at once; Ember looks up what each one does and its trigger words
- **Your setups, saved**: every model remembers its own settings; save favourite setups as tabs and share them
- **Bundles**: a whole setup in one click (the model, its LoRAs and parts, and its settings, with showcase pictures). Get & set up downloads what's missing, checks every file against its published checksum and opens it as a tab. Make your own from the setup on screen and share it as an `.emberbundle` file; adult bundles stay blurred until the viewer confirms they're 18 or older

### Your gallery, your PC
- **Gallery** grouped by day, with favourites, search, and the exact settings behind every picture
- **Secret gallery**: encrypted with your password and hidden until you unlock it (there's no password reset, so don't forget it)
- **Private by design**: every model runs on your PC, your prompts and pictures never leave it, and Ember keeps itself up to date

### For experts
- ComfyUI's own node editor is built in, and optional plugins install at pinned, checked versions

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
| Attention Couple | Two people, described apart |
| IPAdapter Plus | Style picture (brings its own models, 3.4 GB) |
| Anima NAG | A working negative prompt at CFG 1 (the NAG booster) |
| ComfyUI-GGUF | Smaller GGUF model files for 6–8 GB graphics cards |

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish -c Release -o publish
```

This makes one self-contained `publish\Ember.exe`.
