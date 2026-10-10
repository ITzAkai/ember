# Calibration (2026-10-10)

Contact sheets from `tools/calibrate.py`: the same non-explicit prompts and seed (20261010) at 832 × 1216, one column per setting. Run on an RTX 3060 12 GB.

| Model | Seen on the sheet | Default now |
|---|---|---|
| Krea 2 Turbo (`krea2.jpg`) | 6, 8 and 10 steps all good; 10 drifted off the yoga pose. CFG 1.5: no gain, twice the time, stray text. Fix faces 0.4–0.5 over-ages skin; 0.3 adds detail without it. Colour finish is subtle. | 6 / 8 / 10 steps, CFG 1, Fix faces 0.3 |
| Qwen-Image 2.1 (`qwen.jpg`) | 15 steps clearly worse (faces, limbs). 25 ≈ 40 steps; 40 takes about 60 % longer. CFG 2: different, not better, twice the time. Fix faces fails (Impact Pack error). | 20 / 25 / 30 steps (follow-up: 20 ≈ 25 ≈ 30), CFG 1, Fix faces off |
| Anima (`anima.jpg`) | 24 ≈ 30 ≈ 40 steps; 40 added stray hand fragments. CFG 3.5–4 alike; CFG 5 harsher; RescaleCFG at CFG 5 flattens colour and changes poses. | 24 / 30 / 34 steps, CFG 4 |
| Noct Q Anime (`noct.jpg`) | 18 steps breaks the objects prompt (two cups, two books, no cat). 25 ≈ 35 steps. CFG 4 slightly cleaner than 3; CFG 2.5 adds scribbles. Follow-up: 22 steps fine. | 22 / 25 / 35 steps, CFG 3 |
| SDXL base (`sdxl.jpg`) | PAG clearly cleaner bodies and composition (fewer merged people); PAG 2 a little gentler than 3; about 40 % longer (23 → 32 s). CFG 5 vs 7 without PAG: little difference. | PAG on at 2 |
