"""Shop clay sprites, Blender 5.3.

Run: blender -b -P ArtSource/blender/build_shop_kit.py -- [--samples 96]
Reuses Home/UI geometry, pigments, pre-lighting and numpy alpha shadows.
No shadow catcher, scene/C# changes, Unity imports or .meta writes.
Borders are pixels in Unity order [left, bottom, right, top].
"""
import os
import json
import math

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, 'build_home_kit.py')) as source:
    exec(compile(source.read().split("\nif __name__ == '__main__':")[0],
                 'build_home_kit', 'exec'))
UI_OUT = os.path.join(ROOT, 'Assets', 'Orcub', 'Art', 'Shop')
os.makedirs(UI_OUT, exist_ok=True)
META = {}


def gold_material():
    mat = colour('F6B936')
    bsdf = mat.node_tree.nodes['Principled BSDF']
    bsdf.inputs['Roughness'].default_value = .64
    bsdf.inputs['Specular IOR Level'].default_value = .28
    # Pigmented clay, not reflective metal; retain the kit's subtle grain and sheen.
    return mat


def bar(x=0, y=0, z=0, scale=1, angle=0, mat=None):
    """Broad trapezoidal face, rounded shoulders, a visible soft top and side."""
    ob = rounded_box('soft gold ingot', (.94,.52,.40), (0,0,0), .09)
    for v in ob.data.vertices:
        v.co.x *= 1 - .24 * (v.co.y / .52 + .5)
    for p in ob.data.polygons:
        p.use_smooth = True
    normal = ob.modifiers.new('Weighted corner normals', 'WEIGHTED_NORMAL')
    normal.keep_sharp = True
    paint(ob, mat or gold_material())
    ob.scale = (scale,)*3
    ob.rotation_euler = (math.radians(24),math.radians(-8),math.radians(angle))
    ob.location = (x,y,z)
    return ob


def stack(x=0,y=0,z=0,scale=1):
    mat=gold_material()
    for bx,by,bz in ((-.44,-.22,0),(.44,-.22,0),(0,.25,.14)):
        bar(x+bx*scale,y+by*scale,z+bz*scale,scale,mat=mat)


def save_shop(name, width, height, note, occupancy=.80, border=None, tintable=False):
    """Fit evaluated bounds including bevels; leave clear space for alpha-only shadows."""
    from mathutils import Matrix, Vector
    scene=bpy.context.scene
    bpy.context.view_layer.update()
    meshes=[o for o in scene.objects if o.type=='MESH']
    deps=bpy.context.evaluated_depsgraph_get()
    corners=[o.matrix_world @ Vector(c) for o in meshes for c in o.evaluated_get(deps).bound_box]
    xmin,xmax=min(c.x for c in corners),max(c.x for c in corners)
    ymin,ymax=min(c.y for c in corners),max(c.y for c in corners)
    factor=min(width/PPU*occupancy/(xmax-xmin),height/PPU*occupancy/(ymax-ymin))
    transform=Matrix.Scale(factor,4) @ Matrix.Translation((-(xmin+xmax)/2,-(ymin+ymax)/2,0))
    for ob in meshes:
        ob.matrix_world=transform @ ob.matrix_world
    scene.render.resolution_x=width
    scene.render.resolution_y=height
    scene.render.resolution_percentage=100
    scene.camera.data.ortho_scale=max(width,height)/PPU
    path=os.path.join(UI_OUT,name+'.png')
    scene.render.filepath=path
    bpy.ops.render.render(write_still=True)
    blur=max(2,round(min(width,height)*.022))
    add_drop_shadow(path,blur_px=blur,offset=(max(1,blur//2),-max(2,blur)),strength=.25)
    META[name]=dict(file=name+'.png',width=width,height=height,ppu=PPU,pivot=[.5,.5],
                    tintable=tintable,border=border or [0,0,0,0],note=note)


def currency():
    for name,size in (('gold_bar',256),('gold_bar_small',96)):
        begin();stack()
        save_shop(name,size,size,'Three gold clay ingots, two below one; independently rendered at native resolution.',.82)
    begin();bar()
    save_shop('gold_particle',64,64,'Single rounded clay gold ingot for bursts.',.76)


def pile(x=0,y=0,scale=1):
    mat=gold_material()
    for bx,by,bz,a in [(-.80,-.35,0,-8),(0,-.37,.03,3),(.80,-.32,0,12),
                         (-.48,.12,.16,-10),(.40,.15,.18,8),(0,.59,.30,-5)]:
        bar(x+bx*scale,y+by*scale,bz*scale,scale,a,mat)


def packs():
    begin();stack()
    save_shop('pack_100',280,280,'One small pyramid stack of three bars.',.55)
    begin();pile()
    save_shop('pack_600',280,280,'Loose six-bar gold pile.',.70)
    begin()
    wood,edge=[colour(c) for c in ('BA805B','D6A373')]
    # Rear wall and gold sit behind the low front slats, keeping the contents legible.
    box('crate rear',(2.45,1.18,.22),(0,.20,-.34),wood)
    for y in (-.22,.17,.56):
        box('rear slat',(2.30,.075,.08),(0,y,-.19),edge,.035)
    pile(0,.24,.86)
    box('crate front',(2.45,.76,.24),(0,-.52,.46),wood)
    for y in (-.72,-.39):
        box('front slat',(2.35,.055,.055),(0,y,.60),edge,.025)
    for x in (-1.05,1.05):
        box('crate corner',(.23,1.06,.30),(x,-.37,.52),edge,.07)
        for y in (-.71,-.06):
            disc('wood peg',.045,.04,(x,y,.69),wood)
    save_shop('pack_1400',280,280,'Open tan clay crate filled with stacked gold ingots.',.80)
    begin()
    wood,trim,inside=[colour(c) for c in ('A16A50','E3B46D','704536')]
    box('open chest lid',(2.60,.90,.26),(0,.87,-.38),wood,.18)
    box('lid inset',(2.13,.55,.10),(0,.88,-.21),inside,.13)
    for x in (-.91,.91):
        box('lid strap',(.19,.86,.12),(x,.88,-.18),trim,.065)
    box('chest inner',(2.66,1.14,.34),(0,-.25,-.10),inside,.19)
    pile(0,.16,.95)
    bar(-1.08,-.71,.47,.69,-18)
    bar(1.10,-.67,.48,.69,15)
    box('chest bowl',(2.68,.83,.32),(0,-.62,.49),wood,.18)
    box('rolled chest lip',(2.77,.20,.33),(0,-.23,.58),trim,.085)
    for x in (-.93,.93):
        box('front strap',(.20,.81,.12),(x,-.64,.69),trim,.065)
    box('clasp',(.34,.38,.18),(0,-.47,.79),trim,.10)
    disc('clasp pin',.065,.05,(0,-.46,.90),inside)
    # A few spilling bars in front complete the overflowing silhouette.
    bar(-.54,-1.15,.77,.72,-12)
    bar(.42,-1.17,.77,.72,8)
    save_shop('pack_3200',280,280,'Open overflowing clay treasure chest, gold straps and spilled bars.',.85)


def unlimited(x=0,y=0,scale=1):
    from mathutils import Matrix
    before=set(bpy.context.scene.objects)
    paint(heart_shape(),colour('EF8274'))
    cream=colour('FFF1D5')
    # Continuous raised figure eight, with a slight depth crossing; no font dependency.
    points=[]
    for i in range(193):
        t=math.tau*i/192
        points.append((.52*math.cos(t),.23*math.sin(2*t)-.015,.54+.025*math.sin(t)))
    stroke('infinity',points,.067,cream)
    bpy.context.view_layer.update()
    for ob in set(bpy.context.scene.objects)-before:
        ob.matrix_world=Matrix.Translation((x,y,0)) @ Matrix.Scale(scale,4) @ ob.matrix_world


def bundle():
    from mathutils import Matrix
    begin();pile(-.67,.55,.65);unlimited(.93,.57,.54)
    original_begin,original_save=globals()['begin'],globals()['save']
    current=set()
    placements={'icon_hint':(-1.12,-.81,'EEC76B'), 'icon_undo':(0,-.81,'B5A0D6'),
                'icon_unclamp':(1.12,-.81,'8DD9B3')}
    def collect_begin():
        nonlocal current
        current=set(bpy.context.scene.objects)
        return white_clay()
    def collect_save(name,*args,**kwargs):
        parts=[o for o in set(bpy.context.scene.objects)-current if o.type=='MESH']
        if name not in placements:
            for ob in parts:
                bpy.data.objects.remove(ob,do_unlink=True)
            return
        x,y,pigment=placements[name]
        if name == 'icon_unclamp':
            # Retain Home's U sculpt, but use its rounded stroke caps at this small size.
            for part in parts[:]:
                if part.name.startswith('open jaw'):
                    parts.remove(part)
                    bpy.data.objects.remove(part,do_unlink=True)
        ob=fuse(name,parts,voxel=.008,smooth_repeat=12)
        ob.data.materials.clear();paint(ob,colour(pigment))
        bpy.context.view_layer.update()
        ob.matrix_world=Matrix.Translation((x,y,.16)) @ Matrix.Scale(.60,4) @ ob.matrix_world
    try:
        globals()['begin'],globals()['save']=collect_begin,collect_save
        glyphs()
    finally:
        globals()['begin'],globals()['save']=original_begin,original_save
    save_shop('starter_bundle',420,360,'Gold pile, Home hint/undo and rounded-cap unclamp sculpts, plus unlimited-lives heart.',.83)


def extras():
    begin()
    lavender,cream,coral=[colour(c) for c in ('B5A0D6','FFF1D5','EF9A81')]
    box('friendly television',(1.65,1.18,.29),(0,-.05,0),lavender,.20)
    box('cream glass',(1.28,.82,.15),(0,-.05,.20),cream,.17)
    paint(extrude_outline('play',[(-.14,-.28),(.25,-.05),(-.14,.18)],.12,.055),lavender).location.z=.33
    for x in (-.48,.48):
        box('television foot',(.27,.20,.24),(x,-.68,0),lavender,.09)
    stroke('antenna left',[(-.38,.89,0),(0,.57,0)],.055,lavender)
    stroke('antenna right',[(0,.57,0),(.35,.87,0)],.055,lavender)
    stroke('gentle no ads slash',[(-.79,-.73,.49),(.79,.79,.49)],.11,coral)
    save_shop('remove_ads',240,240,'Friendly lavender television with a peach clay slash; no red prohibition ring.',.79)
    begin();unlimited()
    save_shop('infinity_heart',160,160,'Exact inflated ui_heart sculpt in coral with a raised cream infinity.',.81)
    begin();paint(capsule('tintable ribbon',2.70,.64,.25),white_clay())
    # Native canvas and fitted silhouette leave 22 px margin and ~32 px cap radius.
    save_shop('ribbon',360,120,'Near-white puffy badge; 9-slice badge; horizontal stretching is preferred. Text is a separate Unity layer.',
              .88,[68,48,68,48],True)


def main():
    currency();packs();bundle();extras()
    with open(os.path.join(UI_OUT,'shop_kit.json'),'w') as target:
        json.dump(dict(samples=SAMPLES,ppu=PPU,border_order=['left','bottom','right','top'],
                       sprites=META),target,indent=2)
    print('Shop kit complete:',len(META),'sprites')


if __name__=='__main__':
    main()
