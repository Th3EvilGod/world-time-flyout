#!/usr/bin/env python3
"""Draws the app icon (clock face over a globe) as a multi-size .ico."""
import math, os, sys
from PIL import Image, ImageDraw

def draw(size):
    s = size * 8  # supersample
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pad = s * 0.06
    box = [pad, pad, s - pad, s - pad]
    # face: vertical gradient blue
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    top, bottom = (54, 146, 245), (0, 90, 200)
    for y in range(s):
        t = y / s
        gd.line([(0, y), (s, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse(box, fill=255)
    im.paste(grad, (0, 0), mask)
    c = s / 2
    r = (s - 2 * pad) / 2
    w = max(1, int(s * 0.035))
    light = (255, 255, 255, 70)
    # globe lines on their own layer, clipped to the face
    overlay = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    d, face = od, d
    d.ellipse([c - r * 0.42, pad + w, c + r * 0.42, s - pad - w], outline=light, width=w)
    d.line([(pad + w, c), (s - pad - w, c)], fill=light, width=w)
    for k in (0.5,):
        yy = c - r * k
        hw = math.sqrt(max(r * r - (r * k) ** 2, 0)) * 0.98
        d.line([(c - hw, yy), (c + hw, yy)], fill=light, width=w)
        yy = c + r * k
        d.line([(c - hw, yy), (c + hw, yy)], fill=light, width=w)
    alpha = Image.composite(overlay.getchannel("A"), Image.new("L", (s, s), 0), mask)
    overlay.putalpha(alpha)
    im.alpha_composite(overlay)
    d = ImageDraw.Draw(im)
    # hands (10:10 is cliche; use 2:00 style: hour to ~2, minute to 12)
    hw2 = int(s * 0.075)
    d.line([(c, c), (c, c - r * 0.68)], fill=(255, 255, 255, 255), width=hw2)
    ang = math.radians(60)
    d.line([(c, c), (c + math.sin(ang) * r * 0.48, c - math.cos(ang) * r * 0.48)], fill=(255, 255, 255, 255), width=hw2)
    d.ellipse([c - hw2 * 0.9, c - hw2 * 0.9, c + hw2 * 0.9, c + hw2 * 0.9], fill=(255, 255, 255, 255))
    return im.resize((size, size), Image.LANCZOS)

out = sys.argv[1] if len(sys.argv) > 1 else "app.ico"
sizes = [16, 20, 24, 32, 40, 48, 64, 256]
imgs = [draw(z) for z in sizes]
imgs[-1].save(out, sizes=[(z, z) for z in sizes], append_images=imgs[:-1])
imgs[-1].save(os.path.splitext(out)[0] + ".png")
print("wrote", out)
