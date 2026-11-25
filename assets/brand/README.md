# Knovara brand

<p>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="knovara-logo-dark.svg">
    <img src="knovara-logo.svg" alt="Knovara" width="360">
  </picture>
</p>

## The idea

Knovara turns documents into a hierarchy and retrieves only the branch that answers the question. The mark is a **K built as a small tree**:
a stem (the source) branching into two paths that end in nodes. The **orange node is the evidence that was retrieved**, the same role orange plays
in the architecture diagrams ("where repeated work is avoided"). Blue is the product's own colour, used by the desktop app and the dashboard.

The wordmark is drawn from the same vocabulary: monoline strokes with round ends and no ornament, so the name and the mark read as one drawing.

## Files

| File | Use |
|---|---|
| `knovara-logo.svg`, `knovara-logo-dark.svg` | Horizontal lockup for light / dark backgrounds. Preferred in documents and the README |
| `knovara-mark.svg`, `knovara-mark-dark.svg` | The K alone, for light / dark backgrounds, where space is tight |
| `knovara-mark-mono.svg` | Single colour (`currentColor`), for stamps, one-colour print and embossing |
| `knovara-icon.svg` | App icon: the mark on a rounded blue tile |
| `knovara.ico` | Windows icon with 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames (the desktop app and installer) |
| `favicon.ico` | 16, 32 and 48 px, for websites |
| `png/` | Rasters of the above, plus `social-preview-1280x640.png` (upload it under the repository's *Settings > Social preview*) |

## Colours

| Name | Hex | Role |
|---|---|---|
| Knovara blue | `#2563EB` | The mark on light backgrounds; the product's primary colour |
| Light blue | `#60A5FA` | The mark on dark backgrounds |
| Evidence orange | `#F26B1D` | The retrieved node. One per mark, never decorative |
| Ink | `#0F172A` | Wordmark on light backgrounds |
| Paper | `#F1F5F9` | Wordmark on dark backgrounds |
| Tile gradient | `#1E40AF` to `#0B1B4D` | The app-icon tile |

## Using it

- **Clear space:** keep at least the width of the orange node free on every side of the mark or lockup.
- **Minimum size:** the lockup stays legible down to 120 px wide; the mark down to 20 px; the app icon down to 16 px (it has its own thicker drawing at small sizes).
- **Backgrounds:** use the light version on light backgrounds and the dark version on dark ones. On photographs or busy backgrounds use the mono mark.
- **Do not** recolour the orange node, add a second orange element, stretch or rotate the mark, outline it, add shadows or gradients to it, or set the wordmark in a typeface.

## Regenerating

Every file here is produced by `generate.py` from one set of geometry, so the SVG and raster versions cannot drift apart. It needs Pillow.

```powershell
python assets/brand/generate.py             # rewrites this folder
python assets/brand/generate.py --install   # also copies what each consumer needs
```

`--install` copies the icon and mark into `src/Knovara.Desktop/Assets`, the web assets into `src/Knovara.ControlPlane/wwwroot/brand` (the control plane's
Docker build only sees its own folder), and the icon plus the installer's wizard bitmaps into `installer/assets`. Change the geometry in `generate.py`,
rerun with `--install`, and commit the results.

## Licence and trademark

The code in this repository is licensed under Apache-2.0, and the files in this folder are part of it for copyright purposes. **The Apache licence does not grant
trademark rights** (section 6 of the licence, and `NOTICE`): the Knovara name and these logos identify the official project and its products.

- You may use them, unmodified, to refer to Knovara: in an article, a talk, a README that says your tool works with Knovara, or a link to the project.
- You may not use them in a way that suggests your product, fork or service is the official one, or is endorsed by the project.
- A modified fork should use its own name and mark.

A formal trademark policy and registration are still open items (see `business_model_and_monetization_policy.md`, section 3.4); this note is the interim rule.
