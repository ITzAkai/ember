# Calibration (2026-10-10)

Contact sheets from `tools/calibrate.py`: the same non-explicit prompts and seed (20261010) at 832 × 1216, one column per setting. Run on an RTX 3060 12 GB.

| Model | Seen on the sheet | Default now |
|---|---|---|
| Krea 2 Turbo (`krea2.jpg`) | 6, 8 and 10 steps all good; 10 drifted off the yoga pose. CFG 1.5: no gain, twice the time, stray text. Fix faces 0.4–0.5 over-ages skin; 0.3 adds detail without it. Colour finish is subtle. | 6 / 8 / 10 steps, CFG 1, Fix faces 0.3 |
| Qwen-Image 2.1 (`qwen.jpg`) | 15 steps clearly worse (faces, limbs). 25 ≈ 40 steps; 40 takes about 60 % longer. CFG 2: different, not better, twice the time. Fix faces fails (Impact Pack error). | 20 / 25 / 30 steps (follow-up: 20 ≈ 25 ≈ 30), CFG 1, Fix faces off |
| Anima (`anima.jpg`) | 24 ≈ 30 ≈ 40 steps; 40 added stray hand fragments. CFG 3.5–4 alike; CFG 5 harsher; RescaleCFG at CFG 5 flattens colour and changes poses. | 24 / 30 / 34 steps, CFG 4 |
| Noct Q Anime (`noct.jpg`) | 18 steps breaks the objects prompt (two cups, two books, no cat). 25 ≈ 35 steps. CFG 4 slightly cleaner than 3; CFG 2.5 adds scribbles. Follow-up: 22 steps fine. | 22 / 25 / 35 steps, CFG 3 |
| FinePorn v5 (sheet not published) | Tested with the same prompts plus "fully clothed". 8, 10, 12 and 14 steps all good and alike; 12 slightly crisper. Fix faces 0.3 is gentle. About 50 s at 8 steps, 74 s at 12. | 8 / 10 / 12 steps (12 is its author's advice), CFG 1 |
| SDXL base (`sdxl.jpg`) | PAG clearly cleaner bodies and composition (fewer merged people); PAG 2 a little gentler than 3; about 40 % longer (23 → 32 s). CFG 5 vs 7 without PAG: little difference. | PAG on at 2 |

## Anime model comparison (2026-10-11)

Six anime prompts (adult characters, "safe"/"general" tags), seed 20261010, each model at its author's settings. Sheets not published.

| Model | Seen | Settings |
|---|---|---|
| Anima Base v1.0 | Plain; fused the two hands on the teacup; cut the knight's head off | 30 steps, CFG 4, euler / simple |
| Anima Aesthetic v1.1 | Close to Base but cleaner; the best hands of all four | CFG 3.5 |
| Nova Anime AM 2.9B | Most polished classic anime look; framed the knight's whole face; the most detailed armour; about 75 s a picture | Euler a / normal, 28 steps, CFG 5 |
| WAI-illustrious v17 | The richest backgrounds and lighting, about 26 s a picture; weaker at following the prompt (the man and woman at a cafe became a girl in a crowd). PAG 2 added an extra shield and a floating cup without a clear gain | Euler a / normal, 28 steps, CFG 5.5; PAG now off by default for Illustrious |
