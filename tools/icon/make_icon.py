"""Builds Assets/app.ico (16..256) – a K13-style macropad glyph with the orange accent.
Each size is drawn separately at 8x and downsampled; small sizes use a simplified layout."""
from PIL import Image, ImageDraw
import os, sys
OUT = sys.argv[1] if len(sys.argv) > 1 else 'app.ico'
ORANGE = (240, 100, 35, 255); BODY = (38, 41, 48, 255); BODY_EDGE = (20, 22, 26, 255)
KEY = (226, 229, 234, 255); PLATE = (28, 30, 35, 255)

def draw(size):
    S = 8; W = size * S
    im = Image.new('RGBA', (W, W), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    u = W / 32.0  # design grid 32x32
    def rr(x0, y0, x1, y1, r, fill): d.rounded_rectangle([x0 * u, y0 * u, x1 * u, y1 * u], radius=r * u, fill=fill)
    # orange tile background (app-tile look, reads well on any taskbar)
    rr(0.5, 0.5, 31.5, 31.5, 7.5, ORANGE)
    # keypad body (a bit larger on tiny sizes)
    if size <= 24:
        rr(2.4, 7.4, 29.6, 25.4, 3.4, BODY_EDGE); rr(2.6, 7.0, 29.4, 24.8, 3.2, BODY)
    else:
        rr(3.2, 8.2, 28.8, 24.6, 3.4, BODY_EDGE); rr(3.6, 8.0, 28.4, 24.0, 3.2, BODY)
    if size <= 24:
        # 2x2 keys + joystick dot
        for cx in (0, 1):
            for cy in (0, 1):
                x0 = 5.0 + cx * 6.0; y0 = 9.6 + cy * 6.0
                rr(x0, y0, x0 + 4.8, y0 + 4.8, 1.0, KEY)
        d.ellipse([18.4 * u, 10.2 * u, 27.2 * u, 19.0 * u], fill=PLATE, outline=ORANGE, width=max(1, int(1.8 * u)))
        d.ellipse([21.0 * u, 12.8 * u, 24.6 * u, 16.4 * u], fill=KEY)
    else:
        # 3x2 keys, wide key, joystick ring
        for cx in range(3):
            for cy in range(2):
                x0 = 5.4 + cx * 4.3; y0 = 10.2 + cy * 4.3
                rr(x0, y0, x0 + 3.5, y0 + 3.5, 0.8, ORANGE if (cx, cy) == (0, 0) else KEY)
        rr(5.4, 18.8, 16.6, 21.9, 0.8, KEY)
        d.ellipse([19.0 * u, 10.4 * u, 26.6 * u, 18.0 * u], fill=PLATE, outline=ORANGE, width=max(1, int(1.1 * u)))
        d.ellipse([21.2 * u, 12.6 * u, 24.4 * u, 15.8 * u], fill=KEY)
        rr(20.0, 19.4, 25.6, 21.6, 1.0, (70, 74, 82, 255))  # wheel
    return im.resize((size, size), Image.LANCZOS)

sizes = [16, 20, 24, 32, 40, 48, 64, 256]
imgs = [draw(s) for s in sizes]
imgs[-1].save(OUT, format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
# preview strip
strip = Image.new('RGBA', (sum(sizes) + 12 * len(sizes), 256 + 24), (0, 0, 0, 0)); x = 6
for s, im in zip(sizes, imgs): strip.paste(im, (x, 256 + 12 - s), im); x += s + 12
strip.save(os.path.splitext(OUT)[0] + '-preview.png')
print('ok', OUT)
