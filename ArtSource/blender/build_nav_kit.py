"""Navigation v3 clay sprites. Blender 5.3: blender -b -P ArtSource/blender/build_nav_kit.py.

Reuses the UI/Home scene, linearised sRGB pigments, real game parts and alpha-only
shadows. No shadow catcher, Unity imports or .meta writes. Borders are [L,B,R,T].
"""
import os
import json
import math

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, 'build_home_kit.py')) as source:
    exec(compile(source.read().split("\nif __name__ == '__main__':")[0], 'build_home_kit', 'exec'))
UI_OUT = os.path.join(ROOT, 'Assets', 'Orcub', 'Art', 'Nav')
os.makedirs(UI_OUT, exist_ok=True)
META = {}



def sheet_shadow(path):
    """Pad before the shared alpha blur, so a flush bottom cannot wrap onto the top."""
    import numpy as np
    im=bpy.data.images.load(path,check_existing=False)
    w,h=im.size
    px=np.array(im.pixels[:],dtype=np.float32).reshape(h,w,4)
    bpy.data.images.remove(im)
    padded=np.pad(px,((32,32),(32,32),(0,0)))
    im=bpy.data.images.new('Padded sheet',width=w+64,height=h+64,alpha=True)
    im.pixels[:]=padded.ravel();im.filepath_raw=path;im.file_format='PNG';im.save()
    bpy.data.images.remove(im)
    add_drop_shadow(path,blur_px=10,offset=(4,-6),strength=.18)
    im=bpy.data.images.load(path,check_existing=False)
    px=np.array(im.pixels[:],dtype=np.float32).reshape(h+64,w+64,4)[32:-32,32:-32].copy()
    bpy.data.images.remove(im)
    im=bpy.data.images.new('Sheet',width=w,height=h,alpha=True)
    im.pixels[:]=px.ravel();im.filepath_raw=path;im.file_format='PNG';im.save()
    bpy.data.images.remove(im)


def save_nav(name, width, height, note, tintable=False, border=None, shadow=.34):
    scene = bpy.context.scene
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.camera.data.ortho_scale = max(width, height) / PPU
    path = os.path.join(UI_OUT, name + '.png')
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    if name=='sheet':
        sheet_shadow(path)
    elif shadow:
        add_drop_shadow(path, blur_px=10, offset=(4, -6), strength=shadow)
    META[name] = dict(file=name+'.png', width=width, height=height, ppu=PPU,
                      pivot=[.5,.5], tintable=tintable, border=border or [0,0,0,0], note=note)


def finish_glyph(name, mat):
    parts = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    ob = fuse(name, parts, voxel=.006, smooth_repeat=16)
    ob.data.materials.clear()
    paint(ob, mat)
    return ob


def basics():
    m = begin()
    paint(rounded_rect('button', .84,.84,.24,.24,.115),m)
    save_nav('btn_square',220,220,'Soft rounded square; near-white cream for runtime tint.',True)
    for name in ('glyph_back','glyph_close','glyph_chevron'):
        m = begin()
        if name == 'glyph_back':
            m = colour('4A1730')
            stroke('arrow shaft',[(-.28,0,0),(.29,0,0)],.085,m)
            stroke('arrow head',[(-.03,.24,0),(-.29,0,0),(-.03,-.24,0)],.09,m)
        elif name == 'glyph_close':
            for sign in (-1,1):
                stroke('cross',[(-.22,sign*.22,0),(.22,-sign*.22,0)],.085,m)
        else:
            stroke('chevron',[(-.14,.26,0),(.12,0,0),(-.14,-.26,0)],.085,m)
        finish_glyph(name,m)
        save_nav(name,180,180,'Puffy rounded '+name.removeprefix('glyph_')+' glyph.',name!='glyph_back',shadow=.24)
    begin(); m=colour('EF8274')
    for x,y,r in [(-.49,-.17,.085),(-.26,-.075,.072),(-.04,-.16,.065),(.17,-.13,.078)]:
        ball('trail bead',(r,r,r*.68),(x,y,0),m)
    stroke('flagpole',[(.18,-.015,0),(.18,.40,0)],.038,m)
    paint(extrude_outline('flag',[(.20,.39),(.55,.28),(.20,.15)],.12,.05),m)
    save_nav('glyph_path',300,200,'Four coral beads ending at a coral flag.')


def nodes():
    m=begin()
    # Broad rounded teeth and continuous outer rim; centre is a separate untinted layer.
    parts=[swept_band('node rim',.44,0,360,.28,.25,.115)]
    for i in range(10):
        a=math.tau*i/10
        t=rounded_box('tooth',(.24,.23,.22),(.59*math.cos(a),.59*math.sin(a),0),.095)
        t.rotation_euler.z=a; parts.append(t)
    paint(fuse('map node',parts,voxel=.006,smooth_repeat=12),m)
    save_nav('map_node',320,320,'Ten-tooth tintable gear rim. Layer map_node_center at identical bounds.',True)
    begin(); disc('number disc',.386,.18,(0,0,.10),colour('FBF0DA'))
    save_nav('map_node_center',320,320,'Untinted cream number disc; aligned with map_node.',shadow=.12)
    # An actual depressed star floor surrounded by a rolled cream lip, not a raised star.
    begin(); m=colour('F4E4C7')
    def points(r):
        return [(r*(1 if i%2==0 else .51)*math.cos(math.radians(90+i*36)),
                 r*(1 if i%2==0 else .51)*math.sin(math.radians(90+i*36))) for i in range(10)]
    outer=paint(extrude_outline('socket clay',points(.43),.21,.075),m)
    cutter=extrude_outline('pressed star',points(.355),.35,.065)
    cutter.location.z=-.055
    bpy.context.view_layer.objects.active=outer
    mod=outer.modifiers.new('Pressed star cavity','BOOLEAN');mod.operation='DIFFERENCE';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True)
    save_nav('star_socket',220,220,'Recessed star cavity with a cream rolled lip and lower clay floor.',shadow=.16)


def panels():
    m=begin()
    body=capsule('bubble pill',1.35,.52,.20)
    tail=extrude_outline('downward tail',[(-.28,-.17),(-.04,-.17),(-.195,-.40)],.18,.065)
    paint(fuse('bubble',[body,tail],voxel=.006,smooth_repeat=16),m)
    save_nav('bubble',340,200,'Stretch horizontally; downward tail protected inside the left slice cap.',True,[170,0,100,0])
    # Flat lower edge continues beyond the canvas, so the sheet sits flush at screen bottom.
    begin(); m=colour('FBF0DA')
    pts=[(-1.60,-1.6),(1.60,-1.6),(1.60,.68)]
    for cx,start in ((1.12,0),(-1.12,90)):
        for i in range(25):
            a=math.radians(start+i*90/24)
            pts.append((cx+.48*math.cos(a),.68+.48*math.sin(a)))
    paint(extrude_outline('sheet',pts,.30,.10),m)
    save_nav('sheet',680,500,'Cream sheet; large top corners and flat flush bottom. Bottom alpha intentionally reaches canvas edge.',False,[132,0,132,132],shadow=.18)


def hero():
    from mathutils import Matrix, Vector
    begin()
    tan=colour('DEB887')
    def bake(ob):
        bpy.ops.object.select_all(action='DESELECT')
        ob.select_set(True);bpy.context.view_layer.objects.active=ob
        bpy.ops.object.transform_apply(location=True,rotation=True,scale=True)
    centres=[(0,.94),(-.77,-.12),(.76,-.15),(-.02,-1.05)]
    for (x,y),hexcol,angle in zip(centres,['F39CC4','B5A0E4','F6B462','EEE46D'],[-130,90,70,-10]):
        ob,_=build_ring(1.0)
        ob.scale=(.43,)*3; ob.rotation_euler.z=math.radians(angle);ob.location=(x,y,0)
        paint(ob,colour(hexcol))
    for ia,ib in ((0,2),(1,2),(1,3)):
        a,b=Vector((*centres[ia],0)),Vector((*centres[ib],0))
        d=(b-a).normalized();theta=math.atan2(d.y,d.x)
        p=a+d*.43;q=b-d*.43
        for builder,loc,rot in ((build_clamp,p,theta+math.pi/2),(build_mount,q,theta+math.pi/2)):
            ob,_=builder();bake(ob);ob.scale=(.43,)*3;ob.rotation_euler.z=rot;ob.location=loc+Vector((0,0,.06));paint(ob,tan)
        ob=build_stem();bake(ob);ob.scale=((q-p).length,.43,.43);ob.rotation_euler.z=theta;ob.location=p+Vector((0,0,.035));paint(ob,tan)
    # Flush assigned transforms before reading matrix_world (especially the final stem).
    bpy.context.view_layer.update()
    # Keep the bottom teeth and every soft shadow well inside the canvas.
    for ob in list(bpy.context.scene.objects):
        if ob.type=='MESH':
            ob.matrix_world=Matrix.Scale(.92,4) @ ob.matrix_world
    save_nav('home_hero',700,700,'Four real build_parts C-rings joined with real tan clamps, mounts and stems.')


def chapter_lamp():
    # Reuse the restored workshop sculpt without rendering or modifying Home outputs.
    original_save=globals()['save']
    globals()['save']=lambda *args,**kwargs: None
    lamp(False)
    globals()['save']=original_save
    for ob in list(bpy.context.scene.objects):
        if ob.type=='MESH':
            ob.scale*=.62;ob.location*=.62;ob.location.x-=.12
    m=colour('EF8274')
    gear=cog_shape();gear.scale=(.255,)*3;gear.rotation_euler=(0,math.radians(-12),math.radians(-12));gear.location=(.37,-.36,.18);paint(gear,m)
    save_nav('chapter_lamp',320,320,'Restored coral articulated lamp with a small coral gear leaning beside its foot.')


def glow():
    import numpy as np
    n=420
    y,x=np.mgrid[-1:1:complex(n),-1:1:complex(n)]
    r=np.sqrt(x*x+y*y)
    alpha=np.exp(-r*r*5)*np.maximum(0,1-r*r)**2*.68
    px=np.ones((n,n,4),dtype=np.float32);px[:,:,3]=alpha
    im=bpy.data.images.new('Radial halo',width=n,height=n,alpha=True)
    im.pixels[:]=px.ravel();im.filepath_raw=os.path.join(UI_OUT,'node_glow.png');im.file_format='PNG';im.save()
    bpy.data.images.remove(im)
    META['node_glow']=dict(file='node_glow.png',width=n,height=n,ppu=PPU,pivot=[.5,.5],tintable=True,border=[0,0,0,0],note='White radial halo; smooth alpha reaches zero before the canvas edge; no shadow.')


def main():
    basics();nodes();panels();hero();chapter_lamp();glow()
    with open(os.path.join(UI_OUT,'nav_kit.json'),'w') as f:
        json.dump(dict(samples=SAMPLES,ppu=PPU,border_order=['left','bottom','right','top'],sprites=META),f,indent=2)
    print('Nav kit complete:',len(META),'sprites')


if __name__=='__main__':
    main()
