# Brand assets

Everything in this folder except `source/` is **generated** by `tools/brand` from `source/logo.jpg`
(the company's own logo, 3481×2449 JPEG, white background). Do not edit the PNG/ICO/SVG files by hand;
change the generator and run it again:

```
dotnet run -c Release --project tools/brand
```

Two runs on the same machine produce byte-identical files. The 16–40 px icon letters use the installed
**Segoe UI** font (Black weight), so a machine with a different Segoe UI version can produce slightly
different small icons.

## Colours

`brand-colors.json` records what the generator measured. The truck body was sampled (median of a solid
area) and came out at **#2E3391**, identical to the spec value (ΔE76 = 0.00), so the spec value is used.
Dark-theme accent: **#8C93F0**. Dark splash background: **#1C1C1C**.

## Files

| File | What it is |
|---|---|
| `logo-full.png`, `logo-full-512/1024/2048.png` | Full logo with wordmark, transparent background (`logo-full.png` = 1024 wide) |
| `logo-full-dark*.png` | Reversed for dark backgrounds: black → white, blue → #8C93F0 |
| `logo-mark.png`, `logo-mark-256/1024.png` | Truck + CTA monogram only, cropped at the gap above the wordmark |
| `logo-mark-dark*.png` | Reversed mark |
| `logo-mark.svg` | Hand-drawn simplified vector of the mark — **DRAFT, needs user approval** |
| `cta.ico` | App icon: 16, 20, 24, 32, 40, 48, 64, 128, 256 px |
| `icons/cta-<size>.png` | Each icon size as a PNG, for review |
| `icons/review-sheet.png` | All icon sizes on light and dark, plus the small sizes magnified 6× |
| `splash.png` / `splash-dark.png` | 620×300 splash: full logo on white / reversed logo on #1C1C1C |

## How the background is removed

The logo is flat ink (black and blue) on white paper. For every pixel the generator solves how much black
ink and how much blue ink, mixed with white, produce the scanned colour (least squares). The ink amount
becomes alpha and the colour becomes the pure ink colour. Anti-aliased edges therefore get partial alpha
**without a white halo**, and JPEG noise on the paper is dropped. Thin frame lines along the image border are
removed. The white details inside the logo (wheel rings, the gap around the C) become transparent, so on a
dark background they show the background colour.

## For approval

Please review and approve or send changes for:

1. **`logo-mark.svg` (draft).** Redrawn from scratch with rectangles, circles, one arc and a few polygons;
   it is not a trace. It is simplified: the C has an even stroke, the rear frame and mud-flap are plain bars,
   the white strokes through the wheels are left out, and the wordmark is not included (its exact font is
   unknown).
2. **Small icon (16–40 px).** Bold white "CTA" on a brand-blue rounded square, because the truck is not readable
   at these sizes. The spec asks for this at 16–32 px; 40 px uses it too, because the truck is still unreadable
   at 40 px. See `icons/review-sheet.png`.
3. **Large icon (48 px and up).** The mark on a transparent background with a thin white outline, so the black
   monogram stays visible on a dark taskbar.
4. **Simplifications at small sizes:**
   - The side mirror is dropped below 64 px (removed in the 48 px icon).
   - The thin blue outline of the "A" is not drawn separately below 64 px; at 48 px it is thinner than a pixel and
     blends into the letter.
   - The wheel rings merge into plain discs below 32 px. In practice no icon below 48 px shows the truck at all,
     because those sizes use the "CTA" letters.
