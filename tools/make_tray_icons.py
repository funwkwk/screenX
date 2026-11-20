"""Draws the screenX tray / menu bar icon: four rounded corner brackets (a capture frame) around the
screenX X logo.

Windows and Linux get the colour version (blue gradient frame, two-tone blue X, transparent background).
macOS gets a template image that the menu bar tints itself; the light half of the X is drawn
half-transparent there so the two-tone logo still reads. Every size is drawn on its own, with stroke
widths tuned for that size, so the 16 px tray icon stays sharp instead of being a blurry shrink of the
large one.

Needs Pillow (pip install pillow). Run from the repository root:
    python tools/make_tray_icons.py
Writes:
    Assets/screenX-tray.ico           Windows notification area (16-256 px)
    Assets/tray/screenX-tray-<n>.png  Linux system tray
    macos/MenuIcon.png, @2x.png       macOS menu bar (18 pt template image)
"""
from __future__ import annotations

import io
import os
import re
import struct

from PIL import Image, ImageChops, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SS = 8  # supersampling factor

# frame gradient, top-left to bottom-right
FRAME_FROM = (0x2F, 0x6B, 0xFF)
FRAME_TO = (0x5C, 0x9C, 0xFF)

# The screenX X logo (viewBox 396 -1 105.5 100.5): two light and two dark leaves, drawn in this order.
X_VIEWBOX = (396.0, -1.0, 105.5, 100.5)
X_LIGHT = (0xA4, 0xCB, 0xFF)
X_DARK = (0x37, 0x71, 0xFF)
X_LIGHT_SMALL = (0x7F, 0xB0, 0xFF)  # light half on 32 px and smaller, so it does not fade on a light taskbar
X_PATHS = [
    (X_LIGHT, "M439.197 49.125H466.005C466.005 49.125 499.513 86.9996 500.633 90.7496C501.753 94.4996 499.142 96.3746 496.536 97.4996C493.93 98.6246 481.276 98.9996 475.687 96.7496C470.098 94.4996 461.883 87.2516 453.718 77.6246C438.453 59.625 439.197 49.125 439.197 49.125Z"),
    (X_LIGHT, "M439.301 49.2696H466.109C466.109 49.2696 499.617 11.3946 500.736 7.6446C501.856 3.89457 499.246 2.01957 496.64 0.894571C494.034 -0.230427 481.38 -0.605427 475.791 1.64457C470.202 3.89457 461.987 11.1427 453.822 20.7696C438.556 38.7696 439.301 49.2696 439.301 49.2696Z"),
    (X_DARK, "M457.702 49.2715L430.998 49.125C430.998 49.125 397.49 86.9996 396.37 90.7496C395.25 94.4996 397.861 96.3746 400.466 97.4996C403.072 98.6246 415.726 98.9996 421.316 96.7496C426.905 94.4996 435.12 87.2516 443.284 77.6246C458.55 59.625 457.702 49.2715 457.702 49.2715Z"),
    (X_DARK, "M457.702 49.2696H430.894C430.894 49.2696 397.386 11.3946 396.266 7.6446C395.147 3.89457 397.757 2.01957 400.363 0.894571C402.969 -0.230427 415.623 -0.605427 421.212 1.64457C426.801 3.89457 435.016 11.1427 443.181 20.7696C458.447 38.7696 457.702 49.2696 457.702 49.2696Z"),
]


def _svg_polygon(d: str, steps: int = 24) -> list[tuple[float, float]]:
    """Flattens an absolute SVG path made of M, L, H, V, C and Z into a polygon."""
    tokens = re.findall(r"[MLHVCZ]|-?\d*\.?\d+(?:e-?\d+)?", d)
    pts: list[tuple[float, float]] = []
    i, cmd, x, y = 0, "", 0.0, 0.0

    def num() -> float:
        nonlocal i
        i += 1
        return float(tokens[i - 1])

    while i < len(tokens):
        if tokens[i] in "MLHVCZ":
            cmd = tokens[i]
            i += 1
            if cmd == "Z":
                continue
        if cmd in "ML":
            x, y = num(), num()
            pts.append((x, y))
        elif cmd == "H":
            x = num()
            pts.append((x, y))
        elif cmd == "V":
            y = num()
            pts.append((x, y))
        elif cmd == "C":
            x1, y1, x2, y2, x3, y3 = (num() for _ in range(6))
            for k in range(1, steps + 1):
                t = k / steps
                m = 1 - t
                pts.append((m ** 3 * x + 3 * m * m * t * x1 + 3 * m * t * t * x2 + t ** 3 * x3,
                            m ** 3 * y + 3 * m * m * t * y1 + 3 * m * t * t * y2 + t ** 3 * y3))
            x, y = x3, y3
        else:
            i += 1
    return pts


def _round_line(d: ImageDraw.ImageDraw, a, b, w: float, fill) -> None:
    d.line([a, b], fill=fill, width=max(1, round(w)))
    r = w / 2
    for x, y in (a, b):
        d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def _bracket(d, cx, cy, sx, sy, arm, rad, w, fill) -> None:
    """One corner bracket with its corner at (cx, cy); sx/sy = +1/-1 is the direction the arms point."""
    _round_line(d, (cx + sx * rad, cy), (cx + sx * arm, cy), w, fill)
    _round_line(d, (cx, cy + sy * rad), (cx, cy + sy * arm), w, fill)
    # quarter circle joining them (Pillow draws arc strokes inwards from the box, so the box is grown
    # by half the stroke to centre the stroke on the radius, like the straight arms)
    ox, oy, R = cx + sx * rad, cy + sy * rad, rad + w / 2
    start = {(1, 1): 180, (-1, 1): 270, (-1, -1): 0, (1, -1): 90}[(sx, sy)]
    d.arc([ox - R, oy - R, ox + R, oy + R], start, start + 90, fill=fill, width=max(1, round(w)))


def draw(size: int, colour: bool) -> Image.Image:
    S = size * SS
    u = S / 512  # design units: the artwork is laid out on a 512 grid
    small = size <= 24

    # --- frame: four corner brackets, drawn as a mask
    if colour:
        lo, hi = 40 * u, 472 * u
        arm, rad = (104 if small else 116) * u, (48 if small else 48) * u
        fw = (60 if size <= 16 else 54 if small else 46 if size <= 48 else 40) * u
        xw = (272 if small else 236 if size <= 48 else 216) * u   # width of the X logo
    else:
        lo, hi = 34 * u, 478 * u
        arm, rad = 124 * u, 54 * u
        fw = 52 * u
        xw = 232 * u
    frame = Image.new("L", (S, S), 0)
    fd = ImageDraw.Draw(frame)
    for cx, sx in ((lo, 1), (hi, -1)):
        for cy, sy in ((lo, 1), (hi, -1)):
            _bracket(fd, cx, cy, sx, sy, arm, rad, fw, 255)

    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    if colour:
        grad = Image.new("RGBA", (S, S))
        gd = ImageDraw.Draw(grad)
        for k in range(2 * S):  # diagonal gradient, one anti-diagonal line per step
            t = k / (2 * S - 1)
            c = tuple(round(FRAME_FROM[i] + (FRAME_TO[i] - FRAME_FROM[i]) * t) for i in range(3)) + (255,)
            gd.line([(k, 0), (0, k)], fill=c, width=2)
        img.paste(grad, (0, 0), frame)
    else:
        img.paste(Image.new("RGBA", (S, S), (0, 0, 0, 255)), (0, 0), frame)

    # --- the X logo, centred
    vx, vy, vw, vh = X_VIEWBOX
    scale = xw / vw
    ox, oy = (S - vw * scale) / 2, (S - vh * scale) / 2
    for fill, d in X_PATHS:
        poly = [(ox + (px - vx) * scale, oy + (py - vy) * scale) for px, py in _svg_polygon(d)]
        if colour:
            if fill == X_LIGHT and size <= 32:
                fill = X_LIGHT_SMALL
            ImageDraw.Draw(img).polygon(poly, fill=fill + (255,))
        else:
            # template: dark half solid, light half at 55 % so the two-tone shape still shows
            layer = Image.new("L", (S, S), 0)
            ImageDraw.Draw(layer).polygon(poly, fill=255 if fill == X_DARK else 140)
            img.putalpha(ImageChops.lighter(img.getchannel("A"), layer))

    return img.resize((size, size), Image.LANCZOS)


def write_ico(path: str, images: list[Image.Image]) -> None:
    """ICO with one PNG-compressed entry per size (supported since Windows Vista)."""
    blobs = []
    for im in images:
        b = io.BytesIO()
        im.save(b, "PNG", optimize=True)
        blobs.append(b.getvalue())
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for im, blob in zip(images, blobs):
        w = im.width if im.width < 256 else 0
        out += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(out + b"".join(blobs))


def main() -> None:
    assets = os.path.join(ROOT, "Assets")
    write_ico(os.path.join(assets, "screenX-tray.ico"), [draw(n, True) for n in (16, 20, 24, 32, 40, 48, 64, 256)])

    tray = os.path.join(assets, "tray")
    os.makedirs(tray, exist_ok=True)
    for n in (16, 22, 24, 32, 48, 64, 128):
        draw(n, True).save(os.path.join(tray, f"screenX-tray-{n}.png"), optimize=True)

    macos = os.path.join(ROOT, "macos")
    draw(18, False).save(os.path.join(macos, "MenuIcon.png"), optimize=True)
    draw(36, False).save(os.path.join(macos, "MenuIcon@2x.png"), optimize=True)
    print("tray icons written")


if __name__ == "__main__":
    main()
