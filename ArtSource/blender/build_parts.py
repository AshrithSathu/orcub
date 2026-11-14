"""
Orcub - procedural clay parts.

Builds the gear-rings and holder parts in the soft matte clay style (concept 2) and renders them as
top-down sprites with transparent backgrounds for Unity.

    blender -b -P ArtSource/blender/build_parts.py -- [--preview] [--samples N]

Units: 1 Blender unit = 1 Unity world unit. Sprites are rendered at PPU px per unit.
Conventions (must match Orcub.Game):
  - ring: gap centred on +X, pivot at ring centre, tube centre-line radius = R
  - clamp / mount: tube runs along X through the origin; clamp's stem leaves toward -Y, mount's toward +Y
  - stem: capsule along +X starting at the origin (sliced sprite, pivot at the left edge)
All parts are near-white so the game can tint them.
"""
import json
import math
import os
import sys

import bmesh
import bpy
from mathutils import Vector

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(ROOT, "Assets", "Orcub", "Art", "Sprites")
BLEND_OUT = os.path.join(ROOT, "ArtSource", "blender", "orcub_parts.blend")

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
PREVIEW = "--preview" in argv
SAMPLES = int(argv[argv.index("--samples") + 1]) if "--samples" in argv else (24 if PREVIEW else 96)

PPU = 200
GAP_HALF_DEG = 32.0         # must match PuzzleModel.GapHalfAngle
TUBE_W = 0.46               # band width (radial)
TUBE_H = 0.30               # band thickness (Z)
TOOTH_H = 0.22              # how far teeth stick out past the band
TOOTH_W = 0.29              # tooth width along the rim
TOOTH_PITCH = 0.74          # arc length between teeth (at the outer rim)
SHADOW_MARGIN = 0.14
RING_RADII = [1.0, 1.2, 1.4]
VOXEL = 0.011
NORMAL_MAT = None


# ---------------------------------------------------------------- scene setup

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = SAMPLES
    scene.cycles.use_denoising = True
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0.0
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        prefs.compute_device_type = "METAL"
        prefs.get_devices()
        for d in prefs.devices:
            d.use = True
        scene.cycles.device = "GPU"
    except Exception as exc:  # CPU fallback is fine, just slower
        print("GPU unavailable, using CPU:", exc)

    world = bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs["Color"].default_value = (1, 1, 1, 1)
    # Even white dome only: gives ambient occlusion and a soft contact shadow that stay correct when a
    # ring rotates. Directional light is added in Unity by 2D lights reading the normal maps.
    bg.inputs["Strength"].default_value = 1.0

    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = "ORTHO"
    cam = bpy.data.objects.new("Cam", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (0, 0, 10)
    cam.rotation_euler = (0, 0, 0)
    scene.camera = cam
    return scene


def clay_material():
    mat = bpy.data.materials.new("Clay")
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (0.90, 0.89, 0.87, 1)
    bsdf.inputs["Roughness"].default_value = 0.78
    for name, value in (("Specular IOR Level", 0.22), ("Subsurface Weight", 0.06), ("Sheen Weight", 0.25)):
        if name in bsdf.inputs:
            bsdf.inputs[name].default_value = value
    # Fine fingerprint-ish clay grain.
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 55.0
    noise.inputs["Detail"].default_value = 6.0
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.06
    bump.inputs["Distance"].default_value = 0.01
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def normal_material():
    """Emits the bumped surface normal encoded as RGB = n * 0.5 + 0.5 (camera looks down -Z, so this is
    exactly the sprite's tangent space)."""
    mat = bpy.data.materials.new("NormalOut")
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    emit = nt.nodes.new("ShaderNodeEmission")
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 55.0
    noise.inputs["Detail"].default_value = 6.0
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.06
    bump.inputs["Distance"].default_value = 0.01
    enc = nt.nodes.new("ShaderNodeVectorMath")
    enc.operation = "MULTIPLY_ADD"
    enc.inputs[1].default_value = (0.5, 0.5, 0.5)
    enc.inputs[2].default_value = (0.5, 0.5, 0.5)
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], enc.inputs[0])
    nt.links.new(enc.outputs["Vector"], emit.inputs["Color"])
    nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    return mat


def shadow_catcher(z):
    bpy.ops.mesh.primitive_plane_add(size=40, location=(0, 0, z))
    plane = bpy.context.active_object
    plane.name = "ShadowCatcher"
    plane.is_shadow_catcher = True
    return plane


# ---------------------------------------------------------------- mesh helpers

def rounded_box(name, size, location, bevel):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    mod = ob.modifiers.new("Bevel", "BEVEL")
    mod.width = min(bevel, min(size) * 0.49)
    mod.segments = 5
    mod.limit_method = "NONE"
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return ob


def ellipsoid(name, radii, location):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24, radius=1, location=location)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = radii
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return ob


def puck(name, radius, height, corner, location):
    """Cylinder with the same rounded-rect cross-section as the band, used to cap the gap ends seamlessly."""
    bpy.ops.mesh.primitive_cylinder_add(vertices=72, radius=radius, depth=height, location=location)
    ob = bpy.context.active_object
    ob.name = name
    mod = ob.modifiers.new("Bevel", "BEVEL")
    mod.width = corner
    mod.segments = 6
    mod.limit_method = "ANGLE"
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return ob


def swept_band(name, radius, a0_deg, a1_deg, width, height, corner):
    """Sweep a rounded-rectangle cross-section (radial x Z) around an arc."""
    prof = []
    hw, hh = width / 2, height / 2
    c = min(corner, hw * 0.95, hh * 0.95)
    for cx, cy, start in ((hw - c, hh - c, 0), (-hw + c, hh - c, 90), (-hw + c, -hh + c, 180), (hw - c, -hh + c, 270)):
        for k in range(7):
            a = math.radians(start + 90 * k / 6)
            prof.append((cx + c * math.cos(a), cy + c * math.sin(a)))
    bm = bmesh.new()
    steps = max(8, int(abs(a1_deg - a0_deg) / 1.5))
    rings = []
    for i in range(steps + 1):
        a = math.radians(a0_deg + (a1_deg - a0_deg) * i / steps)
        ca, sa = math.cos(a), math.sin(a)
        rings.append([bm.verts.new(((radius + dr) * ca, (radius + dr) * sa, dz)) for dr, dz in prof])
    n = len(prof)
    for i in range(steps):
        for j in range(n):
            bm.faces.new((rings[i][j], rings[i][(j + 1) % n], rings[i + 1][(j + 1) % n], rings[i + 1][j]))
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    ob = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def fuse(name, parts, voxel=VOXEL, smooth_repeat=6):
    """Join parts, voxel-remesh them into one clay blob, soften the seams."""
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    ob = bpy.context.active_object
    ob.name = name
    rem = ob.modifiers.new("Remesh", "REMESH")
    rem.mode = "VOXEL"
    rem.voxel_size = voxel
    rem.adaptivity = 0.0
    bpy.ops.object.modifier_apply(modifier=rem.name)
    sm = ob.modifiers.new("Smooth", "CORRECTIVE_SMOOTH")
    sm.factor = 0.5
    sm.iterations = smooth_repeat
    sm.smooth_type = "LENGTH_WEIGHTED"
    sm.use_only_smooth = True
    bpy.ops.object.modifier_apply(modifier=sm.name)
    bpy.ops.object.shade_smooth()
    return ob


# ---------------------------------------------------------------- parts

def build_ring(R):
    cap_deg = GAP_HALF_DEG + math.degrees((TUBE_W / 2) / R)
    parts = [swept_band(f"band_{R}", R, cap_deg, 360 - cap_deg, TUBE_W, TUBE_H, 0.145)]
    for sign in (1, -1):
        a = math.radians(sign * cap_deg)
        parts.append(puck("cap", TUBE_W / 2, TUBE_H, 0.145, (R * math.cos(a), R * math.sin(a), 0)))

    outer = R + TUBE_W / 2
    n = max(8, round(2 * math.pi * outer / TOOTH_PITCH))
    tooth_half = math.degrees((TOOTH_W / 2) / outer)
    clear = cap_deg + math.degrees((TUBE_W / 2) / R) * 0.2 + tooth_half
    for k in range(n):
        a_deg = 180 + k * 360 / n
        if abs(((a_deg + 180) % 360) - 180) < clear:
            continue  # keep teeth off the gap ends
        a = math.radians(a_deg)
        depth = TOOTH_H + 0.07
        rc = outer - 0.07 + depth / 2
        t = rounded_box("tooth", (depth, TOOTH_W, TUBE_H * 0.82), (rc * math.cos(a), rc * math.sin(a), -0.01), 0.075)
        t.rotation_euler = (0, 0, a)  # about the tooth's own centre; join() bakes it
        parts.append(t)
    ob = fuse(f"Ring_{R:.2f}", parts)
    return ob, R + TUBE_W / 2 + TOOTH_H


def build_clamp():
    """Tube runs along X through the origin (|y| <= TUBE_W/2). Two cuffs strap over the tube; a yoke runs
    along the -Y side, outside the tube, and joins them to the stem neck. Footprint along X is +-0.24,
    matching PuzzleModel.ClampHalfWidth, so the clamp fits through the ring gap."""
    top = TUBE_H / 2
    edge = TUBE_W / 2
    parts = [
        rounded_box("cuffL", (0.15, TUBE_W + 0.22, TUBE_H + 0.12), (-0.165, -0.03, 0.03), 0.065),
        rounded_box("cuffR", (0.15, TUBE_W + 0.22, TUBE_H + 0.12), (0.165, -0.03, 0.03), 0.065),
        rounded_box("yoke", (0.48, 0.15, 0.26), (0, -edge - 0.10, top - 0.08), 0.07),
        rounded_box("neck", (0.20, 0.18, 0.22), (0, -edge - 0.22, top - 0.10), 0.07),
    ]
    return fuse("Clamp", parts, voxel=0.006, smooth_repeat=4), 0.55


def build_clamp_accent():
    """Owner-colour dome on the yoke (same canvas / pivot as the clamp)."""
    edge = TUBE_W / 2
    dome = ellipsoid("accent", (0.075, 0.075, 0.05), (0, -edge - 0.10, TUBE_H / 2 + 0.06))
    return fuse("ClampAccent", [dome], voxel=0.004, smooth_repeat=1), 0.55


def build_mount():
    """Welded plate on the owner's tube (tube along X); the stem neck leaves toward +Y."""
    top = TUBE_H / 2
    edge = TUBE_W / 2
    parts = [
        rounded_box("plate", (0.50, TUBE_W + 0.12, 0.20), (0, 0, top + 0.02), 0.08),
        rounded_box("neck", (0.20, 0.22, 0.20), (0, edge + 0.12, top - 0.02), 0.07),
        ellipsoid("rivetL", (0.075, 0.075, 0.055), (-0.14, -0.04, top + 0.12)),
        ellipsoid("rivetR", (0.075, 0.075, 0.055), (0.14, -0.04, top + 0.12)),
    ]
    return fuse("Mount", parts, voxel=0.006, smooth_repeat=3), 0.55


def build_stem():
    r = 0.12
    bpy.ops.mesh.primitive_cylinder_add(vertices=48, radius=r, depth=1.0, location=(0.5, 0, 0), rotation=(0, math.radians(90), 0))
    body = bpy.context.active_object
    a = ellipsoid("capA", (r, r, r), (0, 0, 0))
    b = ellipsoid("capB", (r, r, r), (1.0, 0, 0))
    ob = fuse("Stem", [body, a, b], voxel=0.006, smooth_repeat=2)
    ob.scale = (1, 1, 0.75)
    return ob


# ---------------------------------------------------------------- render

def render(ob, filename, half_w, half_h, center=(0.0, 0.0), shadow=True, z_floor=-TUBE_H / 2 - 0.005):
    scene = bpy.context.scene
    for other in list(scene.objects):
        if other.type == "MESH" and other is not ob:
            other.hide_render = True
    ob.hide_render = False
    catcher = shadow_catcher(z_floor) if shadow else None

    w_px = int(round(half_w * 2 * PPU))
    h_px = int(round(half_h * 2 * PPU))
    scene.render.resolution_x = w_px
    scene.render.resolution_y = h_px
    scene.render.resolution_percentage = 100
    cam = scene.camera
    cam.data.ortho_scale = max(half_w, half_h) * 2
    cam.location = (center[0], center[1], 10)
    scene.render.filepath = os.path.join(OUT_DIR, filename)
    bpy.ops.render.render(write_still=True)
    if catcher is not None:
        bpy.data.objects.remove(catcher, do_unlink=True)

    # Normal pass: raw (untransformed) emission of the encoded normal, no denoise.
    layer = bpy.context.view_layer
    layer.material_override = NORMAL_MAT
    scene.view_settings.view_transform = "Raw"
    scene.cycles.use_denoising = False
    prev_samples = scene.cycles.samples
    scene.cycles.samples = 16
    scene.render.filepath = os.path.join(OUT_DIR, filename.replace(".png", "_n.png"))
    bpy.ops.render.render(write_still=True)
    layer.material_override = None
    scene.view_settings.view_transform = "Standard"
    scene.cycles.use_denoising = True
    scene.cycles.samples = prev_samples
    ob.hide_render = True
    print(f"rendered {filename} {w_px}x{h_px}")
    return w_px, h_px


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    scene = reset_scene()
    mat = clay_material()
    global NORMAL_MAT
    NORMAL_MAT = normal_material()
    metas = []

    for R in RING_RADII:
        ob, extent = build_ring(R)
        ob.data.materials.append(mat)
        half = extent + SHADOW_MARGIN
        fname = f"ring_r{int(round(R * 100)):03d}.png"
        render(ob, fname, half, half)
        metas.append(dict(file=fname, normal=fname.replace(".png", "_n.png"), role="ring", radius=R, ppu=PPU, pivotX=0.5, pivotY=0.5))

    clamp, half = build_clamp()
    clamp.data.materials.append(mat)
    render(clamp, "clamp.png", half, half, z_floor=-0.02)
    metas.append(dict(file="clamp.png", normal="clamp_n.png", role="clamp", ppu=PPU, pivotX=0.5, pivotY=0.5))

    accent, half = build_clamp_accent()
    accent.data.materials.append(mat)
    render(accent, "clamp_accent.png", half, half, shadow=False)
    metas.append(dict(file="clamp_accent.png", normal="clamp_accent_n.png", role="clamp_accent", ppu=PPU, pivotX=0.5, pivotY=0.5))

    mount, half = build_mount()
    mount.data.materials.append(mat)
    render(mount, "mount.png", half, half, z_floor=-0.02)
    metas.append(dict(file="mount.png", normal="mount_n.png", role="mount", ppu=PPU, pivotX=0.5, pivotY=0.5))

    stem = build_stem()
    stem.data.materials.append(mat)
    # Render a 1.2 x 0.24 strip: [-0.1-0.01 .. 1.1+0.01] x [-0.12 .. 0.12]; square ortho -> use wide aspect.
    margin = 0.02
    x0, x1 = -0.12 - margin, 1.12 + margin
    half_w, half_h = (x1 - x0) / 2, 0.14
    w_px, h_px = render(stem, "stem.png", half_w, half_h, center=((x0 + x1) / 2, 0.0), shadow=False)
    cap_px = int(round((0.12 + margin + 0.04) * PPU))
    metas.append(dict(file="stem.png", normal="stem_n.png", role="stem", ppu=PPU, pivotX=0.0, pivotY=0.5,
                      borderL=cap_px, borderR=cap_px, borderB=0, borderT=0))

    with open(os.path.join(OUT_DIR, "sprites.json"), "w") as f:
        json.dump({"sprites": metas}, f, indent=2)
    os.makedirs(os.path.dirname(BLEND_OUT), exist_ok=True)
    for o in scene.objects:
        o.hide_render = False
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT)
    print("done")


main()
