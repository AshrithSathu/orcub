"""
Orcub - clay UI icons (star, heart, cog coin), pre-lit for the UI.

    blender -b -P ArtSource/blender/build_ui_icons.py -- [--samples N]

UI images never rotate, so unlike the board pieces these are rendered WITH a soft top-left key light baked in
and in their final colours (no runtime tint, no normal maps). Transparent background, 256 x 256 px.
"""
import math
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
_src = open(os.path.join(HERE, "build_parts.py")).read().replace("\nmain()\n", "\n")
exec(compile(_src, "build_parts", "exec"))

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SAMPLES = int(argv[argv.index("--samples") + 1]) if "--samples" in argv else 96
OUT = os.path.join(ROOT, "Assets", "Orcub", "Art", "UI")
SIZE = 256


def hex_rgba(h):
    h = h.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return (*[c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb], 1.0)


def colored_clay(hex_color):
    m = clay_material()
    m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = hex_rgba(hex_color)
    return m


def setup():
    scene = reset_scene()
    scene.cycles.samples = SAMPLES
    scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.55
    key = bpy.data.lights.new("Key", type="AREA")
    key.energy = 260
    key.size = 3
    ko = bpy.data.objects.new("Key", key)
    scene.collection.objects.link(ko)
    ko.location = (-2.2, 2.6, 4.5)
    ko.rotation_euler = (math.radians(-30), math.radians(-25), 0)
    scene.render.resolution_x = scene.render.resolution_y = SIZE
    scene.camera.data.ortho_scale = 2.4
    return scene


def extrude_polygon(name, pts, depth):
    import bmesh
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
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    mod = ob.modifiers.new("Bevel", "BEVEL")
    mod.width = depth * 0.45
    mod.segments = 6
    bpy.ops.object.modifier_apply(modifier=mod.name)
    ob.location.z = -depth / 2
    return ob


def star():
    pts = []
    for i in range(10):
        a = math.radians(90 + i * 36)
        r = 0.95 if i % 2 == 0 else 0.44
        pts.append((r * math.cos(a), r * math.sin(a)))
    return fuse("Star", [extrude_polygon("star", pts, 0.34)], voxel=0.01, smooth_repeat=8)


def heart():
    pts = []
    for i in range(96):
        t = 2 * math.pi * i / 96
        x = 16 * math.sin(t) ** 3
        y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        pts.append((x / 17.5, y / 17.5 + 0.1))
    return fuse("Heart", [extrude_polygon("heart", pts, 0.36)], voxel=0.01, smooth_repeat=8)


def cog():
    parts = [swept_band("cog_band", 0.62, 0, 360, 0.46, 0.26, 0.11)]
    for k in range(9):
        a = math.radians(k * 40)
        t = rounded_box("tooth", (0.26, 0.24, 0.22), (0.95 * math.cos(a), 0.95 * math.sin(a), -0.01), 0.06)
        t.rotation_euler = (0, 0, a)
        parts.append(t)
    return fuse("Cog", parts, voxel=0.01, smooth_repeat=6)


def render_icon(ob, name, color):
    ob.data.materials.append(colored_clay(color))
    scene = bpy.context.scene
    for o in scene.objects:
        if o.type == "MESH" and o is not ob:
            o.hide_render = True
    ob.hide_render = False
    catcher = shadow_catcher(-0.2)
    scene.render.filepath = os.path.join(OUT, name + ".png")
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(catcher, do_unlink=True)
    ob.hide_render = True
    print("rendered", name)


os.makedirs(OUT, exist_ok=True)
setup()
render_icon(star(), "star", "F6C35B")
render_icon(heart(), "heart", "F2836B")
render_icon(cog(), "cog", "E3B261")
print("done")
