"""Clay Workshop, map and booster sprites. Run: blender -b -P ArtSource/blender/build_home_kit.py

Uses the UI kit's pre-lit scene and alpha shadow, never its build entry point.
All canvases use 200 PPU; paired states share their canvas and centre pivot.
"""
import os
import json
import math
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# Import definitions only, just as build_ui_kit imports build_parts without main().
with open(os.path.join(HERE, 'build_ui_kit.py')) as source:
    helpers = source.read().split('\nos.makedirs(UI_OUT, exist_ok=True)')[0]
exec(compile(helpers, 'build_ui_kit', 'exec'))
UI_OUT = os.path.join(ROOT, 'Assets', 'Orcub', 'Art', 'Home')
os.makedirs(UI_OUT, exist_ok=True)
META = {}


def colour(hex_colour):
    m = clay_material()
    rgb = [int(hex_colour[i:i+2], 16) / 255 for i in (0, 2, 4)]
    rgb = [c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4 for c in rgb]
    m.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (*rgb, 1)
    return m


def paint(ob, mat):
    ob.data.materials.append(mat)
    return ob


def box(name, size, loc, mat, bevel=.12):
    ob = rounded_box(name, size, loc, bevel)
    # Keep broad faces flat and rolled edges smooth without inflated/remeshed ridges.
    for p in ob.data.polygons:
        p.use_smooth = True
    normal = ob.modifiers.new('Weighted corner normals', 'WEIGHTED_NORMAL')
    normal.keep_sharp = True
    return paint(ob, mat)


def ball(name, radii, loc, mat):
    ob = ellipsoid(name, radii, loc)
    for p in ob.data.polygons:
        p.use_smooth = True
    return paint(ob, mat)


def disc(name, r, depth, loc, mat):
    ob = puck(name, r, depth, min(depth * .45, r * .3), loc)
    for p in ob.data.polygons:
        p.use_smooth = True
    ob.modifiers.new('Weighted normals', 'WEIGHTED_NORMAL')
    return paint(ob, mat)


def stroke(name, points, radius, mat):
    curve = bpy.data.curves.new(name, 'CURVE')
    curve.dimensions = '3D'
    curve.resolution_u = 16
    curve.bevel_depth = radius
    curve.bevel_resolution = 12
    curve.use_fill_caps = True
    spline = curve.splines.new('POLY')
    spline.points.add(len(points)-1)
    for p, co in zip(spline.points, points):
        p.co = (*co, 1)
    ob = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.object.convert(target='MESH')
    ob.select_set(False)
    paint(ob, mat)
    for pt in (points[0], points[-1]):
        ball(name + ' rounded end', (radius, radius, radius), pt, mat)
    return ob


def arc(name, cx, cy, r, start, end, tube, mat, z=0):
    return stroke(name, [(cx+r*math.cos(math.radians(a)), cy+r*math.sin(math.radians(a)), z)
                        for a in [start+(end-start)*i/96 for i in range(97)]], tube, mat)


def begin():
    scene = ui_scene()
    scene.view_settings.exposure = -0.55
    return white_clay()


def save(name, width, height, note, broken=False):
    scene = bpy.context.scene
    if name == 'lock' or name.startswith('icon_'):
        parts = [ob for ob in scene.objects if ob.type == 'MESH']
        mat = parts[0].data.materials[0]
        ob = fuse(name, parts, voxel=.008, smooth_repeat=12)
        ob.data.materials.clear()
        ob.data.materials.append(mat)
    # Rotate every visible piece about the shared centre, so broken states feel askew.
    if broken:
        from mathutils import Matrix
        turn = Matrix.Rotation(math.radians(-6), 4, 'Z')
        for ob in list(scene.objects):
            if ob.type == 'MESH':
                ob.matrix_world = turn @ ob.matrix_world
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.camera.data.ortho_scale = max(width, height) / PPU
    path = os.path.join(UI_OUT, name + '.png')
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    add_drop_shadow(path, blur_px=12, offset=(6, -10), strength=.34)
    META[name] = dict(file=name+'.png', width=width, height=height, ppu=PPU,
                      pivot=[.5, .5], tintable=name.startswith(('gear_', 'bead', 'lock', 'icon_')), note=note)


def gear():
    mat = begin()
    parts = [swept_band('closed band', .775, 0, 360, .45, .32, .15)]
    for i in range(10):
        a = math.tau*i/10
        tooth = rounded_box('rounded tooth', (.38, .34, .30), (1.08*math.cos(a), 1.08*math.sin(a), 0), .12)
        tooth.rotation_euler.z = a
        parts.append(tooth)
    paint(fuse('closed gear', parts, voxel=.009, smooth_repeat=6), mat)
    save('gear_ring', 600, 600, 'Closed ten-tooth ring; hole radius 55% of band outer radius.')


def glyphs():
    m = begin()
    ball('bead', (.25, .25, .17), (0, 0, 0), m)
    save('bead', 160, 160, 'Round path bead with a soft dome.')
    m = begin()
    arc('shackle', 0, .27, .29, 0, 180, .105, m)
    stroke('shackle left', [(-.29,.27,0),(-.29,-.05,0)], .105, m)
    stroke('shackle right',[(.29,.27,0),(.29,-.05,0)], .105, m)
    box('lock body',(.88,.69,.32),(0,-.22,.03),m,.15)
    # Actual dark recessed keyhole, not an externally coloured accent.
    body = bpy.context.scene.objects['lock body']
    cutter = fuse('keyhole cutter', [ellipsoid('round',(.10,.10,.3),(0,-.18,.19)),
                                   rounded_box('slot',(.10,.18,.6),(0,-.28,.19),.04)], voxel=.008, smooth_repeat=2)
    bpy.context.view_layer.objects.active = body
    mod = body.modifiers.new('Inset keyhole', 'BOOLEAN'); mod.operation='DIFFERENCE'; mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter, do_unlink=True)
    save('lock',320,360,'Chunky padlock with an open shackle centre and recessed keyhole.')
    m = begin()
    ball('bulb globe',(.43,.46,.22),(0,.23,0),m)
    box('bulb neck',(.44,.38,.28),(0,-.25,0),m)
    box('bulb base',(.42,.18,.30),(0,-.49,.01),m,.08)
    for a in (25,90,155):
        rad=math.radians(a)
        stroke('ray',[(r*math.cos(rad),.23+r*math.sin(rad),0) for r in (.62,.78)],.065,m)
    save('icon_hint',380,460,'Puffy light bulb with three short rays.')
    m = begin()
    arc('return sweep',-.05,-.15,.52,-65,150,.13,m)
    # Rounded bent chevron: a soft sculpted arrowhead rather than a triangular wedge.
    tip=(-.63,.14,0)
    stroke('arrow chevron',[(-.59,.55,0),tip,(-.20,.12,0)],.14,m)
    save('icon_undo',400,360,'Back-left curved arrow with a rounded chevron head.')
    m = begin()
    arc('U base',0,-.15,.43,180,360,.14,m)
    for x in (-.43,.43):
        stroke('upright',[(x,-.15,0),(x,.48,0)],.14,m)
        box('open jaw',(.37,.22,.29),(x,.48,0),m,.10)
    save('icon_unclamp',360,380,'Open U clamp with broad separated jaws.')


def palette(broken):
    if broken:
        return [colour(c) for c in ('999184','BBB1A0','756C62','C6BCAB','685F56')]
    return [colour(c) for c in ('F08E7D','F7D396','9E6550','FFF0C9','593442')]


def crack(points, mat):
    stroke('clay fracture',points,.022,mat)


def bench():
    begin()
    wood, edge, dark = [colour(c) for c in ('BC8058','D69B6B','986242')]
    for x in (-1.38,1.38):
        leg=box('splayed leg',(.36,1.48,.35),(x,-.35,-.10),wood,.14)
        leg.rotation_euler.z=math.radians(-7 if x<0 else 7)
    box('cross brace',(2.86,.22,.25),(0,-.61,-.18),dark,.09)
    box('apron',(3.04,.39,.30),(0,.24,-.02),wood)
    box('thick worktop',(3.64,.42,.65),(0,.57,.17),edge,.18)
    for x in (-1.37,1.37):
        disc('wood plug',.075,.06,(x,.24,.17),edge)
    save('bench',840,560,'Warm tan workbench with chunky legs, apron and two wooden plugs.')


def lamp(broken):
    begin(); coral,gold,dark,cream,ink=palette(broken)
    box('lamp foot',(1.10,.25,.42),(-.16,-.83,0),coral)
    joints=[(-.20,-.65),(-.53,.18),(.17,.69)]
    for a,b in zip(joints,joints[1:]):
        stroke('articulated arm',[(a[0],a[1],0),(b[0],b[1],0)],.095,gold)
    for x,y in joints:
        disc('hinge',.15,.18,(x,y,.10),gold)
        disc('hinge pin',.065,.08,(x,y,.22),cream)
    # Broad domed shade, flared lip and inset cream light, no tiny parts.
    shade=paint(extrude_outline('shade',[(-.22,.45),(-.08,.82),(.31,.87),(.64,.33)],.34,.13),coral)
    stroke('shade lip',[(-.18,.39,.20),(.56,.29,.20)],.12,coral)
    if not broken:
        ball('warm diffuser',(.29,.10,.06),(.21,.20,.24),cream)
    else:
        crack([(.10,.80,.19),(.17,.62,.19),(.09,.52,.19),(.24,.37,.19)],ink)
    save('lamp'+('_broken' if broken else ''),440,520,'Coral articulated desk lamp.' if not broken else 'Desaturated askew lamp with fractured shade and missing diffuser.',broken)


def clock(broken):
    begin(); _,gold,_,cream,ink=palette(broken)
    mint=colour('92988A' if broken else '8DD9B3')
    disc('clock casing',.92,.33,(0,0,0),mint)
    disc('cream dial',.73,.16,(0,0,.20),cream)
    for i in range(12):
        a=math.tau*i/12
        ball('hour dot',(.033,.033,.025),(.61*math.sin(a),.61*math.cos(a),.30),ink)
    stroke('hour hand',[(0,0,.33),(-.29,.29,.33)],.048,ink)
    if not broken:
        stroke('minute hand',[(0,0,.34),(.38,.32,.34)],.042,ink)
    else:
        crack([(-.14,.70,.31),(-.03,.41,.31),(-.12,.23,.31),(.12,-.10,.31),(.05,-.39,.31),(.19,-.68,.31)],ink)
    disc('centre pin',.083,.08,(0,0,.39),gold)
    save('clock'+('_broken' if broken else ''),460,460,'Mint clock with cream dial and raised hands.' if not broken else 'Grey-tan tilted clock with cracked dial and missing minute hand.',broken)


def toolbox(broken):
    begin(); _,gold,_,cream,ink=palette(broken)
    lavender=colour('9B9297' if broken else 'B5A0D6')
    # Handle is an open arch, with broad feet hidden behind the lid.
    stroke('handle',[(-.42,.43,0),(-.42,.72,0),(-.30,.82,0),(.30,.82,0),(.42,.72,0),(.42,.43,0)],.085,lavender)
    box('case',(1.76,.96,.46),(0,-.13,0),lavender,.17)
    box('lid',(1.88,.31,.51),(0,.36,.04),lavender,.14)
    for x in (-.58,.58):
        box('corner foot',(.25,.20,.12),(x,-.56,.25),gold,.08)
    if not broken:
        box('latch',(.26,.34,.16),(0,.16,.34),gold,.08)
        box('latch inset',(.10,.10,.03),(0,.17,.43),ink,.035)
    else:
        crack([(.18,.48,.31),(.06,.29,.31),(.19,.08,.25),(.10,-.13,.25)],ink)
    save('toolbox'+('_broken' if broken else ''),500,400,'Lavender carry toolbox with open handle and brass latch.' if not broken else 'Dull tilted toolbox with cracked lid and missing latch.',broken)


def musicbox(broken):
    begin(); _,gold,_,cream,ink=palette(broken)
    butter=colour('AAA08B' if broken else 'EFD47E')
    box('music case',(1.34,.92,.50),(-.13,-.14,0),butter,.17)
    box('music lid',(1.47,.25,.54),(-.13,.38,.06),gold,.11)
    for x in (-.57,.31):
        ball('little foot',(.13,.12,.14),(x,-.64,-.01),gold)
    stroke('crank',[(.55,-.15,0),(.85,-.15,0),(.85,.16,0),(1.02,.16,0)],.065,gold)
    if not broken:
        ball('crank knob',(.14,.115,.14),(1.04,.16,0),butter)
    else:
        crack([(-.45,.25,.28),(-.30,.05,.28),(-.40,-.08,.28),(-.22,-.37,.28)],ink)
    # One large raised musical note, readable without lettering.
    stroke('note stem',[(-.22,-.29,.29),(-.22,.12,.29),(.10,.18,.29),(.10,-.22,.29)],.045,ink)
    ball('note left',(.105,.075,.04),(-.29,-.29,.29),ink)
    ball('note right',(.105,.075,.04),(.03,-.22,.29),ink)
    save('musicbox'+('_broken' if broken else ''),520,400,'Butter-yellow music box with raised note and little crank.' if not broken else 'Grey-tan askew music box with cracked case and missing crank knob.',broken)


def main():
    gear(); glyphs(); bench()
    for builder in (lamp,clock,toolbox,musicbox):
        for broken in (False,True):
            builder(broken)
    layout = dict(reference_size=[1000,900],origin='bottom-left',position='sprite centre',
                  background='#2C101A',pieces=[
                      dict(name='bench',x=500,y=290,width=840,height=560,layer=0),
                      dict(name='lamp',x=290,y=580,width=308,height=364,layer=1),
                      dict(name='clock',x=530,y=760,width=276,height=276,layer=0),
                      dict(name='toolbox',x=325,y=225,width=300,height=240,layer=1),
                      dict(name='musicbox',x=735,y=542,width=312,height=240,layer=1)])
    with open(os.path.join(UI_OUT,'home_kit.json'),'w') as f:
        json.dump(dict(samples=SAMPLES,ppu=PPU,sprites=META,workshop_layout=layout),f,indent=2)
    print('Home kit complete:',len(META),'sprites')


if __name__ == '__main__':
    main()
