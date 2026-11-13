"""Builds Assets/app.ico – a K13-style macropad glyph with the orange accent (v5.1).

Every size is laid out on the integer pixel grid of that size (all straight edges fall on pixel
boundaries, so they stay razor sharp); only round corners / the joystick circle get antialiasing
(rendered at 8x and box-filtered down, which keeps grid-aligned edges exact).
Small sizes use a simplified 2x2-key layout."""
from PIL import Image, ImageDraw
import os, sys

OUT = sys.argv[1] if len(sys.argv) > 1 else 'app.ico'
ORANGE = (240, 100, 35, 255); BODY = (36, 39, 46, 255); EDGE = (20, 22, 26, 255)
KEY = (232, 234, 238, 255); PLATE = (24, 26, 31, 255); WHEEL = (78, 82, 90, 255)
SS = 8
SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 64, 72, 96, 256]

def render(S):
    W = S * SS
    im = Image.new('RGBA', (W, W), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    def rect(x0, y0, x1, y1, r, fill):  # integer pixel coords, x1/y1 exclusive
        r = max(0, r)
        if r == 0: d.rectangle([x0 * SS, y0 * SS, x1 * SS - 1, y1 * SS - 1], fill=fill)
        else: d.rounded_rectangle([x0 * SS, y0 * SS, x1 * SS - 1, y1 * SS - 1], radius=r * SS, fill=fill)
    def circle(cx2, cy2, dia, fill, outline=None, width=0):  # center given in half-pixels
        x0 = cx2 * SS / 2 - dia * SS / 2; y0 = cy2 * SS / 2 - dia * SS / 2
        d.ellipse([x0, y0, x0 + dia * SS - 1, y0 + dia * SS - 1], fill=fill, outline=outline, width=width * SS)

    R = max(3, round(S * 0.22))
    rect(0, 0, S, S, R, ORANGE)
    m = max(1, round(S * 0.09)) if S < 20 else max(2, round(S * 0.09))
    by0 = round(S * 0.25) if S >= 30 else round(S * 0.2)
    by1 = S - (round(S * 0.22) if S >= 30 else round(S * 0.2))
    rb = max(1, round(S * 0.08))
    if S >= 30: rect(m, by0 + 1, S - m, by1 + 1, rb, EDGE)          # 1 px bottom edge
    rect(m, by0, S - m, by1, rb, BODY)
    p = max(1, round(S * 0.06))
    ix0, iy0, ix1, iy1 = m + p, by0 + p, S - m - p, by1 - p
    h = iy1 - iy0; w = ix1 - ix0
    gap = max(1, round(S * 0.07))                                     # keys ↔ joystick
    g = max(1, round(S / 48))                                         # gap between keys
    kr = 0 if S < 40 else max(1, round(S * 0.02))
    if S < 30:
        k = min((h - g) // 2, (w - gap - g - 2) // 3)
        blk = 2 * k + g
        dia = min(blk + 2, w - blk - gap, h)
        if (dia - blk) % 2: dia -= 1
        iy0 = iy0 + (h - blk) // 2
        ix0 = ix0 + (w - (blk + gap + dia)) // 2
        ix1 = ix0 + blk + gap + dia
        for cx in range(2):
            for cy in range(2):
                x = ix0 + cx * (k + g); y = iy0 + cy * (k + g)
                rect(x, y, x + k, y + k, 0, KEY)
        jx = ix1 - dia
        cy2 = 2 * iy0 + blk                     # joystick centred on the key block (half-pixel units)
        ring = 1 if S < 24 else 2
        circle(2 * jx + dia, cy2, dia, ORANGE)
        circle(2 * jx + dia, cy2, dia - 2 * ring, PLATE)
        cd = max(1, dia - 2 * ring - 2)
        if (dia - cd) % 2: cd -= 1
        if cd >= 1: circle(2 * jx + dia, cy2, cd, KEY)
    else:
        k = min((h - 2 * g) // 3, (w - 3 * g - gap) // 5)
        top = iy0 + (h - (3 * k + 2 * g)) // 2
        ix0 = ix0 + (w - (5 * k + 3 * g + gap)) // 2
        ix1 = ix0 + 5 * k + 3 * g + gap
        for cx in range(3):
            for cy in range(2):
                x = ix0 + cx * (k + g); y = top + cy * (k + g)
                rect(x, y, x + k, y + k, kr, ORANGE if (cx, cy) == (0, 0) else KEY)
        y = top + 2 * (k + g); rect(ix0, y, ix0 + 3 * k + 2 * g, y + k, kr, KEY)   # wide key
        dia = 2 * k + g
        jx = ix1 - dia
        ring = max(1, round(S / 28))
        circle(2 * jx + dia, 2 * top + dia, dia, ORANGE)
        circle(2 * jx + dia, 2 * top + dia, dia - 2 * ring, PLATE)
        cd = dia - 2 * ring - 2 * max(1, round(dia * 0.18))
        if (dia - cd) % 2: cd -= 1
        circle(2 * jx + dia, 2 * top + dia, max(2, cd), KEY)
        wy = top + 2 * (k + g); rect(jx + max(1, dia // 6), wy + max(0, k // 5), ix1 - max(1, dia // 6), wy + k - max(0, k // 5), max(0, k // 4), WHEEL)
    return im.reduce(SS)   # exact box filter: grid-aligned edges stay 100 % opaque

imgs = {s: render(s) for s in SIZES}
big = imgs[256]
big.save(OUT, format='ICO', sizes=[(s, s) for s in SIZES], append_images=[imgs[s] for s in SIZES if s != 256])
pv = os.environ.get('ICON_PREVIEW')
if pv:
    W = sum(SIZES) + 16 * (len(SIZES) + 1)
    out = Image.new('RGB', (W, 2 * 284))
    for row, bg in enumerate([(246, 247, 249), (28, 30, 35)]):
        out.paste(bg, (0, row * 284, W, row * 284 + 284)); x = 16
        for s in SIZES:
            out.paste(imgs[s], (x, row * 284 + 270 - s), imgs[s]); x += s + 16
    out.save(pv)
print('ok', OUT)
