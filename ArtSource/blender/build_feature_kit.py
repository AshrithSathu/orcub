"""Daily-loop and tutorial clay sprites (approved features_v1 daily_puzzle_B / daily_gift_B / notifications_B and
tutorial_v1). Blender 5.3:
    blender -b -P ArtSource/blender/build_feature_kit.py

Reuses the nav kit's scene, pigments and alpha-only shadow (exactly like build_account_kit.py). Output is loaded at
runtime from Resources/OrcubFeatures (Game/FeatureArt.cs; import settings in Editor/FeatureArtImporter.cs).
No Unity imports or .meta writes.
"""
import os
import json
import math

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, 'build_nav_kit.py')) as source:
    exec(compile(source.read().split("\nif __name__=='__main__':")[0], 'build_nav_kit', 'exec'))
UI_OUT = os.path.join(ROOT, 'Assets', 'Orcub', 'Art', 'Features', 'Resources', 'OrcubFeatures')
os.makedirs(UI_OUT, exist_ok=True)
META = {}

CREAM = 'F4E4C7'
PLUM = '4A1730'
CORAL = 'EF8274'
TAN = 'C9A27E'
MINT = '6CC487'
GOLD = 'F6B936'


def calendar_body(m_cream, m_coral, m_tan):
    body = rounded_rect('calendar body', 1.10, .92, .16, .24, .08)
    body.location = (0, -.08, -.12)   # extrude_outline centres depth at z=-depth/2
    paint(body, m_cream)
    band = rounded_rect('calendar band', 1.10, .30, .14, .26, .07)
    band.location = (0, .30, -.10)
    paint(band, m_coral)
    for x in (-.30, .30):
        stroke('ring ' + str(x), [(x, .30, .2), (x, .56, .2)], .065, m_tan)


def calendar():
    m = begin()
    calendar_body(colour(CREAM), colour(CORAL), colour(TAN))
    ink = colour(PLUM)
    for row, y in enumerate((-.04, -.30)):
        for x in (-.30, 0, .30):
            box('day %d %.2f' % (row, x), (.17, .15, .08), (x, y, .14), ink, .04)
    save_nav('calendar', 300, 280, 'Cream clay calendar: coral header, tan binding rings, plum day squares.', shadow=.26)


def calendar_check():
    m = begin()
    calendar_body(colour(CREAM), colour(CORAL), colour(TAN))
    stroke('check', [(-.26, -.12, .2), (-.07, -.32, .2), (.30, .06, .2)], .07, colour(MINT))
    save_nav('calendar_check', 300, 280, 'Cream clay calendar carrying a mint check (daily streak).', shadow=.26)


def gift_open():
    m = begin()
    cream = colour(CREAM); coral = colour(CORAL)
    dx, dy = .14, -.14
    base = rounded_box('gift base', (.86, .50, .56), (.16 + dx, -.24 + dy, 0), .09)
    paint(base, cream)
    rim = rounded_box('gift rim', (.92, .08, .60), (.16 + dx, .03 + dy, 0), .04)
    paint(rim, colour('E9D2B1'))
    box('ribbon base', (.16, .52, .60), (.16 + dx, -.24 + dy, .02), coral, .04)
    lid = rounded_box('gift lid', (.74, .20, .56), (-.30 + dx, .30 + dy, .02), .08)
    lid.rotation_euler = (0, 0, math.radians(22))
    paint(lid, cream)
    rb = rounded_box('ribbon lid', (.15, .22, .60), (-.30 + dx, .30 + dy, .04), .04)
    rb.rotation_euler = (0, 0, math.radians(22))
    paint(rb, coral)
    for side in (-1, 1):
        loop = ball('bow loop', (.16, .10, .10), (-.36 + dx + side * .14, .50 + dy + side * .06, .1), coral)
        loop.rotation_euler = (0, 0, math.radians(22 + side * 30))
    ball('bow knot', (.07, .07, .07), (-.36 + dx, .50 + dy, .14), coral)
    save_nav('gift_open', 360, 300, 'Cream gift box opening: lid with coral bow tipped up to the left.', shadow=.26)


def bell():
    m = begin()
    gold = colour(GOLD)
    ball('bell dome', (.32, .40, .28), (.06, .0, 0), gold)
    box('bell skirt', (.80, .16, .40), (.06, -.30, 0), gold, .07)
    ball('bell clapper', (.09, .09, .09), (.06, -.44, .04), colour('D9962A'))
    ball('bell knob', (.07, .07, .07), (.06, .42, 0), gold)
    rays = colour(CREAM)
    for a in (125, 150, 175):
        r = math.radians(a)
        stroke('ray', [(.06 + .50 * math.cos(r), .04 + .50 * math.sin(r), 0), (.06 + .64 * math.cos(r), .04 + .64 * math.sin(r), 0)], .04, rays)
    save_nav('bell', 300, 280, 'Gold clay bell with three short cream rays (notification opt-in).', shadow=.26)


def hand():
    """Cream clay pointing hand (index finger up-left), the tutorial ghost/guide."""
    m = begin()
    skin = colour('F7E1C4')
    ball('palm', (.30, .26, .16), (.10, -.18, 0), skin)
    stroke('index', [(.02, -.02, .04), (-.30, .34, .04)], .095, skin)
    for i, (x, y) in enumerate(((.20, .06), (.32, .00), (.40, -.10))):
        ball('knuckle %d' % i, (.10, .085, .1), (x, y, .02), skin)
    stroke('thumb', [(-.06, -.20, .06), (-.24, -.08, .06)], .085, skin)
    bpy.context.view_layer.update()
    save_nav('hand', 300, 300, 'Cream clay pointing hand, index finger to the upper left.', shadow=.30)


def main():
    calendar(); calendar_check(); gift_open(); bell(); hand()
    with open(os.path.join(UI_OUT, '..', '..', 'feature_kit.json'), 'w') as f:
        json.dump(dict(samples=SAMPLES, ppu=PPU, border_order=['left', 'bottom', 'right', 'top'], sprites=META), f, indent=2)
    print('Feature kit complete:', len(META), 'sprites')


if __name__ == '__main__':
    main()
