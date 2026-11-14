"""
Orcub - clay UI kit (approved direction: #1 top bar + buttons + stars, #4 gear-wreath cards, decluttered).

    blender -b -P ArtSource/blender/build_ui_kit.py -- [--samples N]

Part A (world sprites, lit live by Unity's 2D lights - same pipeline as the rings):
  wreath_arc_a / wreath_arc_b : the two big toothed clay arcs that frame the win / out-of-moves cards
                                (white albedo + normal map, tinted coral / amber in Unity)
Part B (UI images, light baked in from the top-left, rendered near-white so the UI can tint them):
  ui_slab   : cream clay slab for the top bar (9-sliced horizontally)
  ui_pill   : puffy clay pill for every button (9-sliced horizontally)
  ui_round  : round puffy clay button (restart)
  ui_star   : chunky clay star (tinted gold when earned, tan when not)
  ui_restart: clay circular-arrow glyph
  ui_film   : clay film/play glyph for the watch-ad button
Writes Assets/Orcub/Art/UI/ui_kit.json with pixel borders for slicing.
"""
import json
import math
import os
import sys

import bmesh
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
_src = open(os.path.join(HERE, "build_parts.py")).read().replace("\nmain()\n", "\n")
exec(compile(_src, "build_parts", "exec"))

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SAMPLES = int(argv[argv.index("--samples") + 1]) if "--samples" in argv else 96
UI_OUT = os.path.join(ROOT, "Assets", "Orcub", "Art", "UI")
META = {}


# ---------------------------------------------------------------- Part A: wreath arcs (normal-mapped)

def build_arc(name, radius, a0, a1, tube_w, tube_h, tooth_pitch):
    cap_r = tube_w / 2
    parts = [swept_band(name + "_band", radius, a0, a1, tube_w, tube_h, tube_h * 0.48)]
    for ang in (a0, a1):
        a = math.radians(ang)
        parts.append(puck(name + "_cap", cap_r, tube_h, tube_h * 0.48, (radius * math.cos(a), radius * math.sin(a), 0)))
    outer = radius + tube_w / 2
    span = a1 - a0
    n = max(2, int(round(math.radians(span) * outer / tooth_pitch)))
    for k in range(n):
        ang = a0 + span * (k + 0.5) / n
        a = math.radians(ang)
        depth = 0.26
        rc = outer - 0.08 + depth / 2
        t = rounded_box("tooth", (depth, 0.30, tube_h * 0.82), (rc * math.cos(a), rc * math.sin(a), -0.01), 0.08)
        t.rotation_euler = (0, 0, a)
        parts.append(t)
    return fuse(name, parts, voxel=0.014, smooth_repeat=6)


def part_a():
    global OUT_DIR, NORMAL_MAT
    reset_scene()
    bpy.context.scene.cycles.samples = SAMPLES
    mat = clay_material()
    NORMAL_MAT = normal_material()
    globals()["NORMAL_MAT"] = NORMAL_MAT
    globals()["OUT_DIR"] = UI_OUT
    R, W, H = 3.0, 0.42, 0.30
    # Two open arcs that together read as a gear ring with a gap at the bottom, joined at the upper right.
    arcs = {"wreath_arc_a": (35.0, 248.0), "wreath_arc_b": (-68.0, 35.0)}
    extent = R + W / 2 + 0.3
    for name, (a0, a1) in arcs.items():
        ob = build_arc(name, R, a0, a1, W, H, tooth_pitch=1.05)
        ob.data.materials.append(mat)
        half = extent + SHADOW_MARGIN
        render(ob, name + ".png", half, half)
        META[name] = dict(file=name + ".png", normal=name + "_n.png", radius=R, ppu=PPU)


# ---------------------------------------------------------------- Part B: pre-lit UI images

def ui_scene():
    scene = reset_scene()
    scene.cycles.samples = SAMPLES
    scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.6
    key = bpy.data.lights.new("Key", type="AREA")
    key.energy = 420
    key.size = 4
    ko = bpy.data.objects.new("Key", key)
    scene.collection.objects.link(ko)
    ko.location = (-2.5, 3.0, 5.0)
    ko.rotation_euler = (math.radians(-30), math.radians(-25), 0)
    return scene


def white_clay():
    m = clay_material()
    m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.92, 0.91, 0.89, 1)
    return m


def capsule(name, length, height, depth):
    """Rounded slab: rounded-rect footprint with fully rounded caps and a puffy top."""
    r = height / 2
    pts = []
    for cx, start in ((length / 2 - r, -90), (-length / 2 + r, 90)):
        for k in range(25):
            a = math.radians(start + 180 * k / 24)
            pts.append((cx + r * math.cos(a), r * math.sin(a)))
    return extrude_outline(name, pts, depth, bevel=depth * 0.48)


def rounded_rect(name, length, height, corner, depth, bevel):
    pts = []
    for cx, cy, start in ((length / 2 - corner, height / 2 - corner, 0), (-length / 2 + corner, height / 2 - corner, 90),
                          (-length / 2 + corner, -height / 2 + corner, 180), (length / 2 - corner, -height / 2 + corner, 270)):
        for k in range(13):
            a = math.radians(start + 90 * k / 12)
            pts.append((cx + corner * math.cos(a), cy + corner * math.sin(a)))
    return extrude_outline(name, pts, depth, bevel=bevel)


def extrude_outline(name, pts, depth, bevel):
    bm = bmesh.new()
    verts = [bm.verts.new((x, y, 0)) for x, y in pts]
    face = bm.faces.new(verts)
    res = bmesh.ops.extrude_face_region(bm, geom=[face])
    top = [v for v in res["geom"] if isinstance(v, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, verts=top, vec=(0, 0, depth))
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    ob = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(ob)
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    mod = ob.modifiers.new("Bevel", "BEVEL")
    mod.width = bevel
    mod.segments = 8
    bpy.ops.object.modifier_apply(modifier=mod.name)
    ob.location.z = -depth / 2
    return fuse(name, [ob], voxel=0.008, smooth_repeat=4)


def star_shape():
    pts = []
    for i in range(10):
        a = math.radians(90 + i * 36)
        r = 0.95 if i % 2 == 0 else 0.46
        pts.append((r * math.cos(a), r * math.sin(a)))
    return extrude_outline("star", pts, 0.36, bevel=0.16)


def restart_glyph():
    """Circular arrow: 290-degree tube arc with a cone arrowhead at the open end."""
    r, tube = 0.52, 0.15
    a0, a1 = 60.0, 350.0
    parts = [swept_band("arc", r, a0, a1, tube * 2, tube * 1.6, tube * 0.75)]
    a = math.radians(a0)
    parts.append(puck("tail", tube, tube * 1.6, tube * 0.75, (r * math.cos(a), r * math.sin(a), 0)))
    b = math.radians(a1)
    head = [(0.0, 0.30), (-0.24, -0.08), (0.24, -0.08)]
    hx, hy = r * math.cos(b), r * math.sin(b)
    tip = extrude_outline("head", head, tube * 1.6, bevel=0.06)
    tip.rotation_euler = (0, 0, b + math.radians(90 + 180))
    tip.location = (hx + 0.05 * math.cos(b - math.pi / 2), hy + 0.05 * math.sin(b - math.pi / 2), 0)
    parts.append(tip)
    return fuse("restart", parts, voxel=0.008, smooth_repeat=3)


def film_glyph():
    """Open rounded frame (a screen) with a play triangle inside - reads as 'video' at any size."""
    w, h, t = 1.5, 1.04, 0.16
    parts = [
        rounded_box("top", (w, t, 0.2), (0, h / 2 - t / 2, 0), 0.07),
        rounded_box("bottom", (w, t, 0.2), (0, -h / 2 + t / 2, 0), 0.07),
        rounded_box("left", (t, h, 0.2), (-w / 2 + t / 2, 0, 0), 0.07),
        rounded_box("right", (t, h, 0.2), (w / 2 - t / 2, 0, 0), 0.07),
    ]
    tri = extrude_outline("play", [(0.26, 0.0), (-0.16, 0.25), (-0.16, -0.25)], 0.22, bevel=0.06)
    parts.append(tri)
    return fuse("filmplay", parts, voxel=0.008, smooth_repeat=2)


def render_ui(ob, name, half_w, half_h, border_px=None):
    scene = bpy.context.scene
    ob.data.materials.clear()
    ob.data.materials.append(white_clay())
    for o in scene.objects:
        if o.type == "MESH" and o is not ob:
            o.hide_render = True
    ob.hide_render = False
    scene.render.resolution_x = int(round(half_w * 2 * PPU))
    scene.render.resolution_y = int(round(half_h * 2 * PPU))
    scene.camera.data.ortho_scale = max(half_w, half_h) * 2
    scene.camera.location = (0, 0, 10)
    scene.render.filepath = os.path.join(UI_OUT, name + ".png")
    bpy.ops.render.render(write_still=True)
    add_drop_shadow(scene.render.filepath)
    ob.hide_render = True
    META[name] = dict(file=name + ".png", ppu=PPU, border=border_px or 0)
    print("rendered", name)


def add_drop_shadow(path, blur_px=10, offset=(6, -10), strength=0.42):
    """Soft drop shadow from the sprite's own alpha (blurred + offset), composited underneath."""
    import numpy as np
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
    a = px[..., 3].copy()

    def box(arr, r, axis):
        k = 2 * r + 1
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r, r)
        p = np.pad(arr, pad, mode="edge")
        c = np.cumsum(p, axis=axis, dtype=np.float64)
        if axis == 0:
            c = np.vstack([np.zeros((1, c.shape[1])), c])
            return ((c[k:, :] - c[:-k, :]) / k).astype(np.float32)
        c = np.hstack([np.zeros((c.shape[0], 1)), c])
        return ((c[:, k:] - c[:, :-k]) / k).astype(np.float32)

    sh = a
    for _ in range(3):
        sh = box(box(sh, blur_px // 2, 0), blur_px // 2, 1)
    sh = np.roll(np.roll(sh, offset[0], axis=1), offset[1], axis=0) * strength
    out = np.empty_like(px)
    out[..., 3] = a + sh * (1 - a)
    safe = np.maximum(out[..., 3], 1e-6)
    for c in range(3):
        out[..., c] = (px[..., c] * a) / safe   # shadow is black: colour comes only from the sprite
    img.pixels[:] = out.ravel()
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def part_b():
    ui_scene()
    m = 0.18  # margin for the soft shadow
    slab = rounded_rect("slab", 5.6, 1.1, 0.48, 0.34, 0.15)
    render_ui(slab, "ui_slab", 2.8 + m, 0.55 + m, border_px=int((0.55 + m) * PPU))
    pill = capsule("pill", 3.2, 1.0, 0.46)
    render_ui(pill, "ui_pill", 1.6 + m, 0.5 + m, border_px=int((0.5 + m) * PPU))
    roundb = capsule("round", 1.0, 1.0, 0.46)
    render_ui(roundb, "ui_round", 0.5 + m, 0.5 + m)
    render_ui(star_shape(), "ui_star", 1.0 + m, 1.0 + m)
    render_ui(restart_glyph(), "ui_restart", 0.8, 0.8)
    render_ui(film_glyph(), "ui_film", 0.85 + m, 0.6 + m)


os.makedirs(UI_OUT, exist_ok=True)
if "--only-ui" in argv:
    _prev = os.path.join(UI_OUT, "ui_kit.json")
    if os.path.exists(_prev):
        META.update(json.load(open(_prev)))
else:
    part_a()
part_b()
with open(os.path.join(UI_OUT, "ui_kit.json"), "w") as f:
    json.dump(META, f, indent=2)
print("done")
