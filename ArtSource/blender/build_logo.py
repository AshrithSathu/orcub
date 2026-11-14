"""
Orcub - clay logo letters + app icon.

    blender -b -P ArtSource/blender/build_logo.py -- [--samples N] [--only icon|letters]

Letters: the word "orcub" set in Fredoka Bold (SIL OFL, ArtSource/logo/font), extruded and fused into soft clay.
The "o" is NOT rendered as a letter: in game it is our real gear-ring sprite (ring_r100) plus a real clamp, so the
splash can animate it. Each remaining letter is rendered separately (white albedo + normal map, tinted in Unity),
and logo.json records every letter's centre plus the "o" slot so Unity can lay the word out exactly.

Icon: 1024x1024 opaque PNG - coral gear "o" on plum felt, its tan clamp popping off through the gap with crumbs.
"""
import json
import math
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

# Reuse the part builders (clay material, normal pass, fuse, ring, clamp, render) without running their main().
_src = open(os.path.join(HERE, "build_parts.py")).read().replace("\nmain()\n", "\n")
exec(compile(_src, "build_parts", "exec"))

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SAMPLES = int(argv[argv.index("--samples") + 1]) if "--samples" in argv else 96
ONLY = argv[argv.index("--only") + 1] if "--only" in argv else None

FONT = os.path.join(ROOT, "ArtSource", "logo", "font", "Fredoka-Bold.ttf")
LOGO_OUT = os.path.join(ROOT, "Assets", "Orcub", "Art", "Logo")
ICON_OUT = os.path.join(ROOT, "ArtSource", "logo", "icon_1024.png")
FELT = os.path.join(ROOT, "ArtSource", "backgrounds", "bg_2.png")

TEXT_SIZE = 4.4
EXTRUDE = 0.08
BEVEL = 0.12


def hex_rgba(h):
    h = h.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]
    return (*lin, 1.0)


def bbox_world(ob):
    vs = [ob.matrix_world @ v.co for v in ob.data.vertices]
    xs, ys, zs = [v.x for v in vs], [v.y for v in vs], [v.z for v in vs]
    return min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)


def build_letters():
    global OUT_DIR, NORMAL_MAT
    scene = reset_scene()
    scene.cycles.samples = SAMPLES
    mat = clay_material()
    NORMAL_MAT = normal_material()
    globals()["NORMAL_MAT"] = NORMAL_MAT
    OUT_DIR = LOGO_OUT
    globals()["OUT_DIR"] = LOGO_OUT
    os.makedirs(LOGO_OUT, exist_ok=True)

    curve = bpy.data.curves.new("word", type="FONT")
    curve.body = "orcub"
    curve.font = bpy.data.fonts.load(FONT)
    curve.size = TEXT_SIZE
    curve.extrude = EXTRUDE
    curve.bevel_depth = BEVEL
    curve.bevel_resolution = 5
    curve.offset = -BEVEL * 0.92   # shrink the outline by the bevel so strokes keep the font's real weight
    curve.space_character = 1.08
    word = bpy.data.objects.new("word", curve)
    scene.collection.objects.link(word)
    bpy.context.view_layer.objects.active = word
    word.select_set(True)
    bpy.ops.object.convert(target="MESH")
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    pieces = [o for o in scene.objects if o.type == "MESH" and o.name.startswith("word")]
    # Converting text yields many loose pieces (faces, bevels, hole rims). Group them into letters by
    # overlapping horizontal extents, then join each group.
    pieces.sort(key=lambda o: bbox_world(o)[0])
    groups = []
    for pc in pieces:
        x0, x1 = bbox_world(pc)[:2]
        if groups and x0 < groups[-1]["x1"] - 1e-4:
            groups[-1]["objs"].append(pc)
            groups[-1]["x1"] = max(groups[-1]["x1"], x1)
        else:
            groups.append({"x1": x1, "objs": [pc]})
    if len(groups) != 5:
        raise RuntimeError(f"expected 5 letters, got {len(groups)} groups from {len(pieces)} pieces")
    parts = []
    for g in groups:
        bpy.ops.object.select_all(action="DESELECT")
        for o in g["objs"]:
            o.select_set(True)
        bpy.context.view_layer.objects.active = g["objs"][0]
        if len(g["objs"]) > 1:
            bpy.ops.object.join()
        parts.append(bpy.context.view_layer.objects.active)

    letters = []
    o_info = None
    for ch, part in zip("orcub", parts):
        x0, x1, y0, y1, z0, z1 = bbox_world(part)
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        if ch == "o":
            o_info = dict(cx=cx, cy=cy, width=x1 - x0, height=y1 - y0)
            bpy.data.objects.remove(part, do_unlink=True)
            continue
        # Centre the letter on the origin and sit it on the "tube level" so lighting matches the rings.
        part.location = (-cx, -cy, -(z0 + z1) / 2)
        bpy.ops.object.select_all(action="DESELECT")
        part.select_set(True)
        bpy.context.view_layer.objects.active = part
        bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
        clay = fuse(f"letter_{ch}", [part], voxel=0.012, smooth_repeat=4)
        clay.data.materials.clear()
        clay.data.materials.append(mat)
        half_w = (x1 - x0) / 2 + SHADOW_MARGIN
        half_h = (y1 - y0) / 2 + SHADOW_MARGIN
        z_floor = -(z1 - z0) / 2 - 0.005
        fname = f"letter_{ch}.png"
        render(clay, fname, half_w, half_h, z_floor=z_floor)
        letters.append(dict(file=fname, normal=fname.replace(".png", "_n.png"), letter=ch, cx=cx, cy=cy, ppu=PPU))

    with open(os.path.join(LOGO_OUT, "logo.json"), "w") as f:
        json.dump({"letters": letters, "o": o_info, "ppu": PPU, "textSize": TEXT_SIZE}, f, indent=2)
    print("letters done", o_info)


def build_icon():
    """Static hero render, lit directionally (it never rotates), opaque."""
    scene = reset_scene()
    scene.cycles.samples = max(SAMPLES, 128)
    # Clay + its shadows render over a transparent shadow catcher; the result is composited onto the real
    # felt backdrop image afterwards so the plum is exactly the in-game colour.
    scene.render.film_transparent = True
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"

    world = scene.world.node_tree.nodes["Background"]
    world.inputs["Strength"].default_value = 0.55
    key_data = bpy.data.lights.new("Key", type="AREA")
    key_data.energy = 900
    key_data.size = 6
    key = bpy.data.objects.new("Key", key_data)
    scene.collection.objects.link(key)
    key.location = (-5, 6, 9)
    key.rotation_euler = (math.radians(-38), math.radians(-30), 0)

    shadow_catcher(-TUBE_H / 2 - 0.004)

    def clay(color_hex, name):
        m = clay_material()
        m.name = name
        m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = hex_rgba(color_hex)
        return m

    # The "o": coral gear-ring, gap facing lower-right where the clamp is escaping.
    ring, _ = build_ring(1.0)
    ring.data.materials.append(clay("F2836B", "Coral"))
    gap_angle = -50.0
    ring.rotation_euler = (0, 0, math.radians(gap_angle))

    # Clamp on the tube right at the gap's edge, about to slip out; a few crumbs spill into the gap.
    clamp, _ = build_clamp()
    clamp.data.materials.append(clay("D9AE86", "Tan"))
    edge = math.radians(gap_angle + 47)          # just past the cap end, on the tube
    clamp.scale = (1.15, 1.15, 1.15)
    clamp.location = (math.cos(edge), math.sin(edge), 0.0)
    # local X runs along the tube (tangent); local -Y (yoke + dome) points out of the ring
    clamp.rotation_euler = (0, 0, edge + math.radians(90))
    accent, _ = build_clamp_accent()
    accent.data.materials.append(clay("7CCB8E", "Mint"))
    accent.scale = clamp.scale
    accent.location = clamp.location
    accent.rotation_euler = clamp.rotation_euler

    a = math.radians(gap_angle)
    crumb_mat = clay("D9AE86", "TanCrumb")
    coral_crumb = clay("F2836B", "CoralCrumb")
    for i, (rad, ang, r, m) in enumerate([(1.02, -14, 0.07, crumb_mat), (1.18, -2, 0.05, coral_crumb),
                                          (0.9, 6, 0.045, crumb_mat), (1.32, -18, 0.04, crumb_mat)]):
        p = math.radians(gap_angle + ang)
        c = ellipsoid(f"crumb{i}", (r, r * 0.9, r * 0.8), (rad * math.cos(p), rad * math.sin(p), -0.08 + r))
        c.data.materials.append(m)

    # Frame: ring slightly up-left of centre so the escaping clamp has room.
    scene.render.resolution_x = 1024
    scene.render.resolution_y = 1024
    cam = scene.camera
    cam.data.ortho_scale = 3.75
    cam.location = (0.0, 0.0, 10)
    layer_png = ICON_OUT.replace(".png", "_layer.png")
    scene.render.filepath = layer_png
    bpy.ops.render.render(write_still=True)

    # Composite onto the felt (centre crop of the backdrop), flatten to opaque RGB for the App Store.
    felt_img = bpy.data.images.load(FELT)
    fw, fh = felt_img.size
    side = min(fw, fh)
    x0, y0 = (fw - side) // 2, (fh - side) // 2
    import numpy as np
    felt_px = np.array(felt_img.pixels[:], dtype=np.float32).reshape(fh, fw, 4)[y0:y0 + side, x0:x0 + side, :3]
    lay = bpy.data.images.load(layer_png)
    lw, lh = lay.size
    lay_px = np.array(lay.pixels[:], dtype=np.float32).reshape(lh, lw, 4)
    # nearest-neighbour resample of the felt crop to the icon size (soft texture, so this is fine)
    iy = (np.arange(lh) * side / lh).astype(int)
    ix = (np.arange(lw) * side / lw).astype(int)
    felt_rs = felt_px[iy][:, ix]
    a = lay_px[..., 3:4]
    rgb = lay_px[..., :3] * a + felt_rs * (1 - a)  # Blender writes straight (unpremultiplied) alpha
    out = bpy.data.images.new("icon", lw, lh, alpha=False)
    out.pixels[:] = np.concatenate([rgb, np.ones((lh, lw, 1), np.float32)], axis=2).ravel()
    out.filepath_raw = ICON_OUT
    out.file_format = "PNG"
    out.save()
    print("icon done", ICON_OUT)


if ONLY in (None, "letters"):
    build_letters()
if ONLY in (None, "icon"):
    build_icon()
