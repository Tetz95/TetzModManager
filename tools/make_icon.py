"""Draws the app icon (src/App/app.ico): a track running into the distance with a crate on it.

python tools/make_icon.py [preview.png]
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
BG_TOP, BG_BOTTOM = (36, 92, 70), (20, 52, 40)
RAIL, TIE = (226, 230, 234), (139, 94, 60)
CRATE, CRATE_DARK, CRATE_EDGE = (238, 160, 52), (196, 118, 28), (110, 62, 14)


def draw(size: int) -> Image.Image:
    """One size, drawn at 8x and scaled down; small sizes get fewer, bolder details."""
    k = 8
    s = size * k
    small = size <= 32
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # rounded square with a vertical gradient
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)) + (255,))
    img.paste(grad, (0, 0))
    d = ImageDraw.Draw(img)

    # track in perspective: rails from the bottom corners to a point high in the middle
    horizon_y, bottom_y = s * 0.16, s * 1.0
    cx = s / 2
    half_bottom, half_top = s * 0.40, s * 0.035

    def rail_x(y: float, side: int) -> float:
        t = (y - horizon_y) / (bottom_y - horizon_y)
        return cx + side * (half_top + (half_bottom - half_top) * t)

    ties = 5 if small else 8
    for i in range(ties):
        # ties closer together toward the horizon
        t = ((i + 0.6) / ties) ** 1.7
        y = horizon_y + (bottom_y - horizon_y) * t
        w = (s * 0.012 + s * 0.05 * t) * (1.6 if small else 1.0)
        over = s * (0.03 + 0.07 * t)
        d.rectangle([rail_x(y, -1) - over, y - w / 2, rail_x(y, 1) + over, y + w / 2], fill=TIE)
    rail_w_top, rail_w_bottom = s * (0.012 if not small else 0.02), s * (0.045 if not small else 0.065)
    for side in (-1, 1):
        x0, x1 = rail_x(horizon_y, side), rail_x(bottom_y, side)
        d.polygon([(x0 - rail_w_top / 2, horizon_y), (x0 + rail_w_top / 2, horizon_y),
                   (x1 + rail_w_bottom / 2, bottom_y), (x1 - rail_w_bottom / 2, bottom_y)], fill=RAIL)

    # crate on the track: front face, top and side for depth
    c = s * (0.42 if small else 0.36)
    left, top = cx - c / 2 - s * 0.03, s * 0.40
    depth = c * 0.22
    front = [left, top, left + c, top + c]
    # shadow on the ties under the crate
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).ellipse([left - c * 0.08, top + c * 0.86, left + c + depth + c * 0.12, top + c * 1.12], fill=(0, 0, 0, 110))
    img.alpha_composite(shadow)
    d = ImageDraw.Draw(img)
    d.polygon([(left, top), (left + depth, top - depth), (left + c + depth, top - depth), (left + c, top)], fill=CRATE)
    d.polygon([(left + c, top), (left + c + depth, top - depth), (left + c + depth, top + c - depth), (left + c, top + c)], fill=CRATE_DARK)
    d.rectangle(front, fill=CRATE)
    edge = max(k, round(s * (0.022 if small else 0.016)))
    d.rectangle(front, outline=CRATE_EDGE, width=edge)
    if not small:
        # planks and a cross brace
        for f in (1 / 3, 2 / 3):
            y = top + c * f
            d.line([(left, y), (left + c, y)], fill=CRATE_EDGE, width=max(k, round(edge * 0.6)))
        d.line([(left, top), (left + c, top + c)], fill=CRATE_EDGE, width=edge)
    else:
        d.line([(left, top), (left + c, top + c)], fill=CRATE_EDGE, width=edge)
    d.polygon([(left, top), (left + depth, top - depth), (left + c + depth, top - depth), (left + c + depth, top + c - depth),
               (left + c, top + c), (left + c, top)], outline=CRATE_EDGE, width=max(k, edge // 2))

    # clip everything to the rounded square
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 0.2, fill=255)
    out = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    return out.resize((size, size), Image.LANCZOS)


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    images = [draw(n) for n in SIZES]
    out = root / "src" / "App" / "app.ico"
    images[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
    print(f"wrote {out}")
    if len(sys.argv) > 1:
        # a sheet of every size, each on a light and a dark background
        w = sum(SIZES) + 10 * (len(SIZES) + 1)
        sheet = Image.new("RGBA", (w, 2 * 256 + 30), (245, 245, 245, 255))
        ImageDraw.Draw(sheet).rectangle([0, 256 + 20, w, 2 * 256 + 30], fill=(40, 40, 40, 255))
        x = 10
        for n, im in zip(SIZES, images):
            sheet.paste(im, (x, 10), im)
            sheet.paste(im, (x, 256 + 25), im)
            x += n + 10
        sheet.save(sys.argv[1])
        print(f"preview {sys.argv[1]}")


if __name__ == "__main__":
    main()
