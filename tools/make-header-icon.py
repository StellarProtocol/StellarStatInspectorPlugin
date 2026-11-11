"""StatInspector Settings header icon: flat white bar-chart glyph (4 bars), pre-drawn + pixel-hinted per size (no master scaled down).
Every bar edge sits on a whole pixel; only the small top-corner rounding is anti-aliased (4x supersample)."""
import sys
from PIL import Image, ImageDraw

# size: (bar width, gap, left x, baseline y (exclusive), max height, corner radius)
SPECS = {
    16:  (2, 2, 1, 14, 12, 0),
    24:  (4, 2, 1, 22, 20, 1),
    32:  (5, 3, 1, 29, 26, 1),
    64:  (10, 6, 3, 58, 52, 2),
    128: (20, 12, 6, 116, 104, 4),
}
FRACTIONS = (0.45, 0.80, 0.62, 1.00)   # the prototype's 10 / 18 / 14 / 22 bars

def draw(size, out):
    w, g, left, base, hmax, r = SPECS[size]
    ss = 4
    img = Image.new("L", (size * ss, size * ss), 0)
    d = ImageDraw.Draw(img)
    for i, f in enumerate(FRACTIONS):
        h = max(2, round(hmax * f))
        x0 = left + i * (w + g)
        y0 = base - h
        box = [x0 * ss, y0 * ss, (x0 + w) * ss - 1, base * ss - 1]
        if r > 0:
            d.rounded_rectangle(box, radius=r * ss, fill=255, corners=(True, True, False, False))
        else:
            d.rectangle(box, fill=255)
    alpha = img.resize((size, size), Image.BOX)
    rgba = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    rgba.putalpha(alpha)
    rgba.save(out)

if __name__ == "__main__":
    outdir = sys.argv[1]   # e.g. Resources
    for s in SPECS:
        draw(s, f"{outdir}/stat-header-{s}.png")
    print("ok")
