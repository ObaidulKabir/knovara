"""Generates every Knovara brand asset from one set of geometry definitions.

    python assets/brand/generate.py

Requires Pillow. The same shapes are written out as SVG and rasterised with Pillow, so the two formats cannot drift apart.
All coordinates are in a 100-unit design grid for the mark and in glyph units (y up, baseline 0) for the wordmark.
"""
import math
import os
import struct
import sys
from io import BytesIO

from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))

# ---------------------------------------------------------------------------- palette
BLUE = "#2563EB"          # the product blue used by the desktop app and the dashboard
BLUE_LIGHT = "#60A5FA"    # blue on dark backgrounds
BLUE_PALE = "#BFDBFE"     # secondary node on the dark tile
ORANGE = "#F26B1D"        # "evidence": the same role orange plays in the architecture diagrams
INK = "#0F172A"           # text on light backgrounds
PAPER = "#F1F5F9"         # text on dark backgrounds
TILE_TOP, TILE_BOTTOM = "#1E40AF", "#0B1B4D"   # app-icon tile gradient


def rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


# ---------------------------------------------------------------------------- shapes
# ('line', (x0,y0), (x1,y1), width, color)   round caps
# ('arc',  (cx,cy), r, a0, a1, width, color) degrees, y down, a0 -> a1 clockwise on screen, round caps
# ('dot',  (cx,cy), r, color)
def line(p0, p1, w, c): return ("line", p0, p1, w, c)
def arc(c0, r, a0, a1, w, c): return ("arc", c0, r, a0, a1, w, c)
def dot(c0, r, c): return ("dot", c0, r, c)


def mark_shapes(stem, arm, node_main, node_side, c_stem, c_hot, c_side, scale=1.0, ox=0.0, oy=0.0):
    """The K-as-a-tree mark in the 100-unit grid. `c_hot` is the retrieved-evidence node."""
    def P(x, y): return (ox + x * scale, oy + y * scale)
    j = P(30, 52)
    up = P(70, 20)
    down = P(70, 84)
    top = P(30, 14)
    bottom = P(30, 86)
    return [
        line(top, bottom, stem * scale, c_stem),
        line(j, up, arm * scale, c_stem),
        line(j, down, arm * scale, c_stem),
        dot(down, node_side * scale, c_side),
        dot(up, node_main * scale, c_hot),
    ]


# ---------------------------------------------------------------------------- wordmark (monoline, round ends)
# glyphs are lists of segments in glyph units: y up, baseline y=0, x-height 36, ascender 56.
XH = 36.0


def g_k():  return [("l", (0, 0), (0, 56)), ("l", (0, 15), (20, 36)), ("l", (7, 23), (21, 0))], 21
def g_n():  return [("l", (0, 0), (0, 36)), ("a", (13, 23), 13, 180, 0), ("l", (26, 23), (26, 0))], 26
def g_o():  return [("c", (18, 18), 18)], 36
def g_v():  return [("l", (0, 36), (11, 0)), ("l", (11, 0), (22, 36))], 22
def g_a():  return [("c", (18, 18), 18), ("l", (36, 36), (36, 0))], 36
def g_r():  return [("l", (0, 0), (0, 36)), ("a", (10, 26), 10, 180, 45)], 19

WORD = [g_k, g_n, g_o, g_v, g_a, g_r, g_a]
GAP = 11.0   # between glyph centerlines, in glyph units


def wordmark_shapes(scale, x0, baseline, weight, color):
    """Wordmark as shapes; `weight` is the stroke width in output units."""
    shapes = []
    x = 0.0
    for g in WORD:
        segs, width = g()
        def T(px, py): return (x0 + (x + px) * scale, baseline - py * scale)
        for s in segs:
            if s[0] == "l":
                shapes.append(line(T(*s[1]), T(*s[2]), weight, color))
            elif s[0] == "a":
                (cx, cy), r, a0, a1 = s[1], s[2], s[3], s[4]
                # glyph angles are math angles (y up); convert to screen angles (y down) -> negate
                shapes.append(arc(T(cx, cy), r * scale, -a0, -a1, weight, color))
            elif s[0] == "c":
                (cx, cy), r = s[1], s[2]
                shapes.append(("ring", T(cx, cy), r * scale, weight, color))
        x += width + GAP
    return shapes, (x - GAP) * scale


# ---------------------------------------------------------------------------- SVG
def f(v): return f"{v:.2f}".rstrip("0").rstrip(".")


def svg_shape(s, mono=None):
    col = lambda c: mono if mono else c
    k = s[0]
    if k == "line":
        _, p0, p1, w, c = s
        return f'<line x1="{f(p0[0])}" y1="{f(p0[1])}" x2="{f(p1[0])}" y2="{f(p1[1])}" stroke="{col(c)}" stroke-width="{f(w)}" stroke-linecap="round"/>'
    if k == "dot":
        _, c0, r, c = s
        return f'<circle cx="{f(c0[0])}" cy="{f(c0[1])}" r="{f(r)}" fill="{col(c)}"/>'
    if k == "ring":
        _, c0, r, w, c = s
        return f'<circle cx="{f(c0[0])}" cy="{f(c0[1])}" r="{f(r)}" fill="none" stroke="{col(c)}" stroke-width="{f(w)}"/>'
    if k == "arc":
        _, c0, r, a0, a1, w, c = s
        x0, y0 = c0[0] + r * math.cos(math.radians(a0)), c0[1] + r * math.sin(math.radians(a0))
        x1, y1 = c0[0] + r * math.cos(math.radians(a1)), c0[1] + r * math.sin(math.radians(a1))
        sweep = 1 if a1 > a0 else 0
        large = 1 if abs(a1 - a0) > 180 else 0
        return (f'<path d="M{f(x0)} {f(y0)}A{f(r)} {f(r)} 0 {large} {sweep} {f(x1)} {f(y1)}" fill="none" '
                f'stroke="{col(c)}" stroke-width="{f(w)}" stroke-linecap="round"/>')
    raise ValueError(k)


def svg_doc(w, h, body, title, defs="", bg=None):
    rect = f'<rect width="{f(w)}" height="{f(h)}" fill="{bg}"/>' if bg else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {f(w)} {f(h)}" width="{f(w)}" height="{f(h)}" role="img" aria-label="{title}">'
            f'<title>{title}</title>{defs}{rect}{"".join(body)}</svg>\n')


# ---------------------------------------------------------------------------- raster
SS = 8  # supersampling factor


def raster(shapes, w, h, size_px, bg=None, tile=None):
    """Render shapes (in a w x h unit canvas) to a size_px-wide RGBA image (supersampled)."""
    k = size_px / w
    W, H = int(round(w * k)), int(round(h * k))
    img = Image.new("RGBA", (W * SS, H * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if bg:
        d.rectangle([0, 0, W * SS, H * SS], fill=rgb(bg) + (255,))
    if tile:
        paint_tile(img, tile, W * SS)
    m = k * SS

    def disc(c0, r, col):
        d.ellipse([(c0[0] - r) * m, (c0[1] - r) * m, (c0[0] + r) * m, (c0[1] + r) * m], fill=rgb(col) + (255,))

    def thick(p0, p1, wd, col):
        (x0, y0), (x1, y1) = p0, p1
        dx, dy = x1 - x0, y1 - y0
        L = math.hypot(dx, dy) or 1
        nx, ny = -dy / L * wd / 2, dx / L * wd / 2
        d.polygon([((x0 + nx) * m, (y0 + ny) * m), ((x1 + nx) * m, (y1 + ny) * m),
                   ((x1 - nx) * m, (y1 - ny) * m), ((x0 - nx) * m, (y0 - ny) * m)], fill=rgb(col) + (255,))
        disc(p0, wd / 2, col)
        disc(p1, wd / 2, col)

    for s in shapes:
        kind = s[0]
        if kind == "line":
            thick(s[1], s[2], s[3], s[4])
        elif kind == "dot":
            disc(s[1], s[2], s[3])
        elif kind in ("arc", "ring"):
            if kind == "ring":
                _, c0, r, wd, col = s
                a0, a1 = 0, 360          # a ring is a closed stroke; erasing a disc would leave transparent holes
            else:
                _, c0, r, a0, a1, wd, col = s
            steps = max(24, int(abs(a1 - a0) / 2))
            pts = [(c0[0] + r * math.cos(math.radians(a0 + (a1 - a0) * i / steps)),
                    c0[1] + r * math.sin(math.radians(a0 + (a1 - a0) * i / steps))) for i in range(steps + 1)]
            for p, q in zip(pts, pts[1:]):
                thick(p, q, wd, col)
    return img.resize((W, H), Image.LANCZOS)


def paint_tile(img, tile, size):
    """Rounded-square gradient tile covering the whole image."""
    radius = int(size * tile["radius"])
    grad = Image.new("RGBA", (size, size))
    gd = ImageDraw.Draw(grad)
    t, b = rgb(tile["top"]), rgb(tile["bottom"])
    for y in range(size):
        a = y / max(1, size - 1)
        gd.line([(0, y), (size, y)], fill=tuple(int(t[i] + (b[i] - t[i]) * a) for i in range(3)) + (255,))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)


# ---------------------------------------------------------------------------- the assets
def icon_params(px):
    """Optical sizing: thicker strokes and larger nodes at small sizes so the K survives 16 px."""
    if px <= 24:   return dict(stem=17, arm=15, node_main=15, node_side=12.5)
    if px <= 48:   return dict(stem=14.5, arm=13, node_main=13, node_side=10.5)
    return dict(stem=12, arm=11, node_main=11.5, node_side=9)


def icon_shapes(px):
    p = icon_params(px)
    # inset the K so the tile keeps a margin
    return mark_shapes(p["stem"], p["arm"], p["node_main"], p["node_side"], "#FFFFFF", ORANGE, BLUE_PALE,
                       scale=0.8, ox=10, oy=10)


TILE = dict(radius=0.225, top=TILE_TOP, bottom=TILE_BOTTOM)


def make_icon_png(px):
    return raster(icon_shapes(px), 100, 100, px, tile=TILE)


def make_mark(color_stem=BLUE, hot=ORANGE, side=None, stem=11, arm=10, nm=10.5, ns=8.5):
    return mark_shapes(stem, arm, nm, ns, color_stem, hot, side or color_stem)


def lockup(dark=False):
    stem = BLUE_LIGHT if dark else BLUE
    ink = PAPER if dark else INK
    weight = 8.0
    m = mark_shapes(weight, weight, 9.5, 7.5, stem, ORANGE, stem)
    scale = weight / 6.0           # wordmark weight 6 glyph units -> same stroke as the mark
    ws, width = wordmark_shapes(scale, x0=118, baseline=86, weight=weight, color=ink)
    return m + ws, 118 + width + 14, 100


def write(name, text):
    with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def make_ico(sizes, path):
    """Multi-size .ico: BMP frames below 256 px (maximum compatibility), PNG for 256."""
    frames = []
    for s in sizes:
        im = make_icon_png(s)
        if s >= 256:
            buf = BytesIO(); im.save(buf, "PNG"); frames.append((s, buf.getvalue()))
        else:
            px = im.convert("RGBA")
            w = h = s
            xor = bytearray()
            for y in range(h - 1, -1, -1):
                for x in range(w):
                    r, g, b, a = px.getpixel((x, y))
                    xor += bytes((b, g, r, a))
            mask_row = ((w + 31) // 32) * 4
            andmask = bytes(mask_row * h)
            hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(andmask), 0, 0, 0, 0)
            frames.append((s, hdr + bytes(xor) + andmask))
    out = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    for s, data in frames:
        out += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in frames:
        out += data
    with open(path, "wb") as fh:
        fh.write(out)


def gradient_image(w, h, top, bottom):
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    t, b = rgb(top), rgb(bottom)
    for y in range(h):
        a = y / max(1, h - 1)
        d.line([(0, y), (w, y)], fill=tuple(int(t[i] + (b[i] - t[i]) * a) for i in range(3)))
    return img


def find_font(size):
    """A UI font for the one line of text in the social image; the image is skipped-text if none is found."""
    from PIL import ImageFont
    for path in (r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fontsrial.ttf",
                 "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/System/Library/Fonts/Helvetica.ttc"):
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return None


def make_social_preview(path):
    """1280x640 repository social preview (GitHub 'Social preview' setting): dark gradient, the lockup, one line of text."""
    W, H = 1280, 640
    canvas = gradient_image(W, H, "#0B1220", "#111C33").convert("RGBA")
    shapes, w, h = lockup(True)
    target_w = 860
    logo = raster(shapes, w, h, target_w)
    lx = (W - logo.width) // 2
    ly = 175
    canvas.paste(logo, (lx, ly), logo)
    font = find_font(34)
    if font:
        text = "Context infrastructure for AI agents"
        d = ImageDraw.Draw(canvas)
        tw = d.textlength(text, font=font)
        d.text(((W - tw) / 2, ly + logo.height + 46), text, font=font, fill=rgb("#94A3B8") + (255,))
    canvas.convert("RGB").save(path, "PNG")


def make_wizard_images(dirpath):
    """Inno Setup wizard bitmaps: 164x314 side panel and 55x58 corner image (24-bit BMP, as Inno requires)."""
    os.makedirs(dirpath, exist_ok=True)
    big = gradient_image(164, 314, TILE_TOP, TILE_BOTTOM).convert("RGBA")
    mark = raster(mark_shapes(12, 11, 11.5, 9, "#FFFFFF", ORANGE, BLUE_PALE), 100, 100, 112)
    big.paste(mark, ((164 - 112) // 2, 70), mark)
    big.convert("RGB").save(os.path.join(dirpath, "wizard-large.bmp"), "BMP")
    small = Image.new("RGBA", (55, 58), (255, 255, 255, 255))
    ic = make_icon_png(48)
    small.paste(ic, (3, 5), ic)
    small.convert("RGB").save(os.path.join(dirpath, "wizard-small.bmp"), "BMP")


def install(root):
    """Copy assets to the places that cannot reference assets/brand directly (Docker build context, MSBuild resources, Inno)."""
    import shutil
    targets = {
        os.path.join(root, "src", "Knovara.Desktop", "Assets"): ["knovara.ico", "png/knovara-mark-256.png"],
        os.path.join(root, "src", "Knovara.ControlPlane", "wwwroot", "brand"):
            ["knovara-mark.svg", "knovara-mark-dark.svg", "knovara-icon.svg", "knovara-logo.svg", "knovara-logo-dark.svg", "favicon.ico"],
    }
    for dest, files in targets.items():
        os.makedirs(dest, exist_ok=True)
        for rel in files:
            shutil.copyfile(os.path.join(OUT, rel), os.path.join(dest, os.path.basename(rel)))
    make_wizard_images(os.path.join(root, "installer", "assets"))
    shutil.copyfile(os.path.join(OUT, "knovara.ico"), os.path.join(root, "installer", "assets", "knovara.ico"))
    print("installed")


def main():
    os.makedirs(os.path.join(OUT, "png"), exist_ok=True)

    # --- SVG: color mark, mono mark, light/dark lockups, app icon
    mark = make_mark()
    write("knovara-mark.svg", svg_doc(100, 100, [svg_shape(s) for s in mark], "Knovara"))
    write("knovara-mark-mono.svg", svg_doc(100, 100, [svg_shape(s, mono="currentColor") for s in mark], "Knovara"))
    mark_dark = make_mark(color_stem=BLUE_LIGHT)
    write("knovara-mark-dark.svg", svg_doc(100, 100, [svg_shape(s) for s in mark_dark], "Knovara"))

    for dark, name in ((False, "knovara-logo.svg"), (True, "knovara-logo-dark.svg")):
        shapes, w, h = lockup(dark)
        write(name, svg_doc(w, h, [svg_shape(s) for s in shapes], "Knovara"))

    ic = icon_shapes(256)
    grad = ('<defs><linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="%s"/><stop offset="1" stop-color="%s"/></linearGradient></defs>'
            % (TILE_TOP, TILE_BOTTOM))
    tile_rect = f'<rect width="100" height="100" rx="{f(100 * TILE["radius"])}" fill="url(#t)"/>'
    write("knovara-icon.svg", svg_doc(100, 100, [tile_rect] + [svg_shape(s) for s in ic], "Knovara", defs=grad))

    # --- PNG
    for px in (16, 24, 32, 48, 64, 128, 256, 512):
        make_icon_png(px).save(os.path.join(OUT, "png", f"knovara-icon-{px}.png"))
    for px in (256, 512):
        raster(mark, 100, 100, px).save(os.path.join(OUT, "png", f"knovara-mark-{px}.png"))
    shapes, w, h = lockup(False)
    raster(shapes, w, h, 1200, bg="#FFFFFF").save(os.path.join(OUT, "png", "knovara-logo-1200.png"))
    shapes, w, h = lockup(True)
    raster(shapes, w, h, 1200, bg="#0F172A").save(os.path.join(OUT, "png", "knovara-logo-dark-1200.png"))

    # --- ICO
    make_ico([16, 20, 24, 32, 40, 48, 64, 128, 256], os.path.join(OUT, "knovara.ico"))
    make_ico([16, 32, 48], os.path.join(OUT, "favicon.ico"))
    make_social_preview(os.path.join(OUT, "png", "social-preview-1280x640.png"))
    print("ok")


if __name__ == "__main__":
    main()
    if "--install" in sys.argv:
        install(os.path.abspath(os.path.join(OUT, "..", "..")))
