#!/usr/bin/env python3
"""Generates src/AccountManager.App/Assets/app.ico (multi-size) for DshAccountManager.

Design: DeepSeek-blue rounded tile (brand blue #4D6BFE), a large white "D", and a gold key inside the D's
counter. Needs Pillow.

    python3 scripts/make-icon.py [--out src/AccountManager.App/Assets/app.ico] [--preview /tmp/icon.png]
"""
import argparse
from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZES = (16, 24, 32, 48, 64, 128, 256)
M = 1024
TOP_LEFT = (96, 133, 255)       # lighter DeepSeek blue
BOTTOM_RIGHT = (52, 84, 238)    # deeper DeepSeek blue
D_COLOR = (255, 255, 255, 255)
KEY = (255, 196, 46, 255)       # gold


def tile():
    mask = Image.new("L", (M, M), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, M - 1, M - 1), radius=round(M * 57 / 256), fill=255)
    diag = Image.linear_gradient("L").resize((M, M))
    diag = ImageChops.add(diag, diag.rotate(90), scale=2)                 # (x + y) / 2 diagonal ramp
    fill = Image.merge("RGB", [diag.point(lambda v, a=a, b=b: round(a + (b - a) * v / 255)) for a, b in zip(TOP_LEFT, BOTTOM_RIGHT)])
    img = Image.new("RGBA", (M, M), (0, 0, 0, 0)); img.paste(fill, (0, 0), mask)
    return img, mask


def big_d(img, box, stem, color):
    """Geometric bold D (stem + semicircular bowl) with a D-shaped counter; returns the counter box."""
    x0, y0, x1, y1 = box
    half = lambda xm: _rect_mask((xm, 0, M, M))
    def d_shape(left, top, xm, bottom, corner):
        r = (bottom - top) / 2
        stem_part = Image.new("L", (M, M), 0)
        ImageDraw.Draw(stem_part).rounded_rectangle((left, top, xm + corner + 1, bottom), radius=corner, fill=255)
        bowl = Image.new("L", (M, M), 0); ImageDraw.Draw(bowl).ellipse((xm - r, top, xm + r, bottom), fill=255)
        return ImageChops.lighter(stem_part, ImageChops.multiply(bowl, half(xm))), r
    xm = x1 - (y1 - y0) / 2
    outer, _ = d_shape(x0, y0, xm, y1, round((y1 - y0) * 0.07))
    inner, cr = d_shape(x0 + stem, y0 + stem, xm, y1 - stem, round((y1 - y0) * 0.02))
    shape = ImageChops.subtract(outer, inner)
    shadow = shape.filter(ImageFilter.GaussianBlur(16)).point(lambda v: v * 55 // 255)
    img.paste(Image.new("RGBA", (M, M), (10, 20, 70, 255)), (0, 12), shadow)
    img.paste(Image.new("RGBA", (M, M), color), (0, 0), shape)
    return (x0 + stem, y0 + stem, xm + cr, y1 - stem)


def _rect_mask(box):
    m = Image.new("L", (M, M), 0); ImageDraw.Draw(m).rectangle(box, fill=255); return m


def classic_key(img, center, length, color, angle=45):
    """Ring bow, thick shaft, two teeth, round caps, rotated 45 degrees."""
    u = length / 170.0
    W = round(260 * u); layer = Image.new("RGBA", (W, W), (0, 0, 0, 0)); d = ImageDraw.Draw(layer)
    c = W / 2
    def P(x, y): return (c + x * u, c + y * u)
    def line(a, b, width):
        w = round(width * u); d.line([P(*a), P(*b)], fill=color, width=w)
        for p in (a, b):
            x, y = P(*p); r = w / 2; d.ellipse((x - r, y - r, x + r, y + r), fill=color)
    cx, cy = P(-38, 0); ro = (34 + 13) * u; ri = (34 - 13) * u
    d.ellipse((cx - ro, cy - ro, cx + ro, cy + ro), fill=color)
    d.ellipse((cx - ri, cy - ri, cx + ri, cy + ri), fill=(0, 0, 0, 0))
    line((-6, 0), (66, 0), 26)
    line((34, 2), (34, 30), 20)
    line((58, 2), (58, 30), 20)
    layer = layer.rotate(-angle, resample=Image.BICUBIC)
    sh = layer.split()[3].filter(ImageFilter.GaussianBlur(8)).point(lambda v: v * 70 // 255)
    img.paste(Image.new("RGBA", layer.size, (10, 20, 70, 255)), (round(center[0] - W / 2), round(center[1] - W / 2) + 8), sh)
    img.alpha_composite(layer, (round(center[0] - W / 2), round(center[1] - W / 2)))


def render():
    img, mask = tile()
    cb = big_d(img, (128, 96, 904, 928), 176, D_COLOR)
    classic_key(img, ((cb[0] + cb[2]) / 2 - 24, (cb[1] + cb[3]) / 2), 380, KEY)
    out = Image.new("RGBA", (M, M), (0, 0, 0, 0)); out.paste(img, (0, 0), mask)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="src/AccountManager.App/Assets/app.ico")
    parser.add_argument("--preview", default=None)
    args = parser.parse_args()
    master = render()
    frames = [master.resize((s, s), Image.LANCZOS) for s in SIZES]
    frames[-1].save(args.out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    if args.preview: frames[-1].save(args.preview, format="PNG")
    print("wrote", args.out)


if __name__ == "__main__":
    main()
