"""Account / privacy sprites (approved account_v1 + account_v2). Blender 5.3:
    blender -b -P ArtSource/blender/build_account_kit.py

Clay icons reuse the nav kit's scene, pigments and alpha shadow. Flat platform marks (Google G, the white
provider button, the system spinner, the inline info ring and the check) are rasterised with numpy so they
stay flat, as Google's branding guidance and the approved images require. Output is loaded at runtime from
Resources/OrcubAccount (see Game/AccountArt.cs). No Unity imports or .meta writes.
"""
import os
import json
import math

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, 'build_nav_kit.py')) as source:
    exec(compile(source.read().split("\nif __name__=='__main__':")[0], 'build_nav_kit', 'exec'))
UI_OUT = os.path.join(ROOT, 'Assets', 'Orcub', 'Art', 'Account', 'Resources', 'OrcubAccount')
os.makedirs(UI_OUT, exist_ok=True)
META = {}

CREAM = 'F4E4C7'
PLUM = '4A1730'


def cloud_parts(m):
    base = rounded_box('cloud base', (.98, .30, .30), (0, -.12, 0), .14)
    paint(base, m)
    parts = [base]
    for x, y, r in [(-.27, .02, .21), (.06, .12, .29), (.33, -.02, .18)]:
        parts.append(ball('cloud puff', (r, r, r * .78), (x, y, 0), m))
    return parts


def cloud():
    m = begin(); m = colour(CREAM)
    paint(fuse('cloud', cloud_parts(m), voxel=.006, smooth_repeat=14), m)
    save_nav('cloud', 300, 220, 'Cream clay cloud (account badge, save nudge, sign-out).', shadow=.26)


def cloud_check():
    m = begin(); m = colour(CREAM)
    paint(fuse('cloud', cloud_parts(m), voxel=.006, smooth_repeat=14), m)
    stroke('check', [(-.17, .00, .2), (-.05, -.11, .2), (.19, .15, .2)], .048, colour('6CC487'))
    save_nav('cloud_check', 300, 220, 'Cream cloud carrying a mint check (welcome back).', shadow=.26)


def phone():
    m = begin()
    body = rounded_rect('phone', .54, .86, .13, .14, .05)
    paint(body, colour('B9A28C'))
    screen = rounded_rect('screen', .40, .66, .06, .05, .02)
    screen.location = (0, .03, .08)
    paint(screen, colour('E2D3BD'))
    ball('home dot', (.035, .035, .02), (0, -.35, .08), colour('E2D3BD'))
    save_nav('phone', 200, 260, 'Tan clay phone (this phone save).', shadow=.26)


def trash():
    m = begin(); tan = colour('C3A98D'); dark = colour('A58A70')
    body = rounded_box('bin', (.50, .56, .30), (0, -.12, 0), .1); paint(body, tan)
    lid = rounded_box('lid', (.66, .11, .32), (0, .24, 0), .05); paint(lid, tan)
    arc('handle', 0, .29, .1, 0, 180, .035, tan)
    for x in (-.12, 0, .12):
        stroke('rib', [(x, -.30, .16), (x, .06, .16)], .03, dark)
    save_nav('trash', 220, 240, 'Neutral tan clay bin (delete confirmation).', shadow=.26)


def info_disc():
    m = begin()
    disc('info disc', .40, .2, (0, 0, 0), colour(CREAM))
    ink = colour(PLUM)
    ball('dot', (.052, .052, .04), (0, .17, .12), ink)
    stroke('stem', [(0, -.19, .12), (0, .04, .12)], .046, ink)
    save_nav('info', 220, 220, 'Cream clay disc with a plum i (tracking sheet title).', shadow=.24)


# ------------------------------------------------------------------ flat marks (numpy)

def write_png(name, rgba, note, border=None):
    import numpy as np
    h, w = rgba.shape[:2]
    im = bpy.data.images.new(name, width=w, height=h, alpha=True)
    im.pixels[:] = np.flipud(rgba).astype(np.float32).ravel()
    im.filepath_raw = os.path.join(UI_OUT, name + '.png'); im.file_format = 'PNG'; im.save()
    bpy.data.images.remove(im)
    META[name] = dict(file=name + '.png', width=w, height=h, ppu=PPU, pivot=[.5, .5], tintable=False,
                      border=border or [0, 0, 0, 0], note=note)


def grid(n, ss=4):
    import numpy as np
    m = n * ss
    c = (np.arange(m) + .5) / m * 2 - 1
    x, y = np.meshgrid(c, -c)          # y up, row 0 at the top
    return x, y, ss


def down(a, ss):
    n = a.shape[0] // ss
    return a.reshape(n, ss, n, ss, *a.shape[2:]).mean(axis=(1, 3))


def srgb(hexs):
    return [int(hexs[i:i + 2], 16) / 255 for i in (0, 2, 4)]


def g_logo():
    """Google's four-colour G, drawn from its published geometry: open ring + blue crossbar."""
    import numpy as np
    x, y, ss = grid(256)
    r = np.sqrt(x * x + y * y); a = (np.degrees(np.arctan2(y, x)) + 360) % 360
    R, t = .94, .36
    ring = (r <= R) & (r >= R - t)
    bar = (x >= -.02) & (x <= R) & (y <= t * .5) & (y >= -t * .5)
    cols = np.zeros(x.shape + (4,))
    def put(mask, hexs):
        cols[mask] = [*srgb(hexs), 1]
    put(ring & (a >= 45) & (a < 145), 'EA4335')                 # red, top
    put(ring & (a >= 145) & (a < 215), 'FBBC05')                # yellow, left
    put(ring & (a >= 215) & (a < 322), '34A853')                # green, bottom
    put((ring & ((a >= 322) | (a < 1))) | bar, '4285F4')        # blue, lower right + bar
    out = down(cols, ss)
    rgb = np.where(out[..., 3:4] > 0, out[..., :3] / np.maximum(out[..., 3:4], 1e-6), 0)
    write_png('g_logo', np.concatenate([rgb, out[..., 3:4]], -1), 'Flat four-colour Google G (branding guidelines).')


def g_button():
    """White provider button, #747775 1px-equivalent stroke, modest radius. 9-slice."""
    import numpy as np
    n, ss = 128, 4
    x, y, _ = grid(n, ss)
    px = (x + 1) * .5 * n; py = (1 - y) * .5 * n
    rad, inset = 30., 2.
    def sdf(rr, ins):
        qx = np.maximum(np.abs(px - n / 2) - (n / 2 - ins - rr), 0)
        qy = np.maximum(np.abs(py - n / 2) - (n / 2 - ins - rr), 0)
        return np.sqrt(qx * qx + qy * qy) - rr
    outer = (sdf(rad, inset) <= 0).astype(float)
    inner = (sdf(rad - 3.2, inset + 3.2) <= 0).astype(float)
    grey = np.array(srgb('747775')); white = np.ones(3)
    rgb = grey[None, None, :] * (outer - inner)[..., None] + white[None, None, :] * inner[..., None]
    a = down(outer, ss); rgb = down(rgb, ss)
    rgb = rgb / np.maximum(a[..., None], 1e-6)
    write_png('g_button', np.concatenate([rgb, a[..., None]], -1),
              'Flat white provider button with grey stroke; slice 40px each side.', border=[40, 40, 40, 40])


def spinner():
    """System-style activity indicator: 12 rounded spokes with a baked alpha tail. Rotate in 30 degree steps."""
    import numpy as np
    x, y, ss = grid(160)
    alpha = np.zeros(x.shape)
    for i in range(12):
        th = math.radians(90 - 30 * i)
        ux, uy = math.cos(th), math.sin(th)
        along = x * ux + y * uy; across = -x * uy + y * ux
        seg = np.clip(along, .44, .86)
        d = np.sqrt((along - seg) ** 2 + across ** 2)
        alpha = np.maximum(alpha, (d <= .085) * (1 - i * .065))
    a = down(alpha, ss)
    write_png('spinner', np.dstack([np.ones_like(a)] * 3 + [a]), 'White 12-spoke spinner; tint grey; step-rotate.')


def info_ring():
    import numpy as np
    x, y, ss = grid(96)
    r = np.sqrt(x * x + y * y)
    ring = (r <= .94) & (r >= .78)
    dot = np.sqrt(x * x + (y - .36) ** 2) <= .13
    seg = np.clip(y, -.42, .02)
    stem = np.sqrt(x * x + (y - seg) ** 2) <= .1
    a = down((ring | dot | stem).astype(float), ss)
    write_png('info_ring', np.dstack([np.ones_like(a)] * 3 + [a]), 'White outline info mark; tint coral for inline errors.')


def check():
    import numpy as np
    x, y, ss = grid(96)
    pts = [(-.62, .02), (-.2, -.4), (.66, .46)]
    alpha = np.zeros(x.shape)
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        dx, dy = x1 - x0, y1 - y0
        t = np.clip(((x - x0) * dx + (y - y0) * dy) / (dx * dx + dy * dy), 0, 1)
        d = np.sqrt((x - x0 - t * dx) ** 2 + (y - y0 - t * dy) ** 2)
        alpha = np.maximum(alpha, (d <= .15).astype(float))
    a = down(alpha, ss)
    write_png('check', np.dstack([np.ones_like(a)] * 3 + [a]), 'White rounded check; tint ink.')


def main():
    cloud(); cloud_check(); phone(); trash(); info_disc()
    g_logo(); g_button(); spinner(); info_ring(); check()
    with open(os.path.join(UI_OUT, '..', '..', 'account_kit.json'), 'w') as f:
        json.dump(dict(samples=SAMPLES, ppu=PPU, border_order=['left', 'bottom', 'right', 'top'], sprites=META), f, indent=2)
    print('Account kit complete:', len(META), 'sprites')


if __name__ == '__main__':
    main()
