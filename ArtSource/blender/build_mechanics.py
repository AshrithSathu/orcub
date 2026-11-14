"""Orcub mechanics kit. Blender 5.3, same geometry/200 PPU/normal pass as build_parts.

blender -b -P ArtSource/blender/build_mechanics.py -- --samples 48
Then run: python build_mechanics.py --contact (host Python with Pillow).
This required finalisation shares albedo alpha with normals and composites every deliverable.
Board sprites use the original white dome, no directional lights or shadow catcher.
UI icons alone use a baked upper-left area light. No Unity or .meta writes.
"""
import os, sys, json, math
def contact_sheet():
    """Reproducible host-side contact/alpha QA; Pillow, no Unity imports required."""
    from PIL import Image, ImageDraw, ImageFont, ImageChops
    root=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
    out=os.path.join(root,'Assets/Orcub/Art/Mechanics')
    data=json.load(open(os.path.join(out,'mechanics.json')))
    items=data['sprites']; ppu=70; cw,ch=440,390
    for e in items:
        e['width'],e['height']=e['px']
        e['pivotX'],e['pivotY']=e['pivot']
        if '_r' in e['file'] and e['referenceRingRadius'] in (1.0,1.2,1.4) and e['anchor']=='tube':
            e['anchor']='ring_surface'
        if e['anchor']=='tube_badge':e['anchorOffset']=[0,.48]
        if e['anchor']=='clamp_badge':e['anchorOffset']=[0,-1.22]
    felt=Image.open(os.path.join(root,'Assets/Orcub/Art/Backgrounds/bg_2.png')).convert('RGB')
    sheet=Image.new('RGB',(cw*4,100+ch*math.ceil(len(items)/4)), '#421027')
    draw=ImageDraw.Draw(sheet)
    try:
        font=ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf',16)
        small=ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf',13)
    except OSError: font=small=ImageFont.load_default()
    draw.text((24,16),'ORCUB / APPROVED MECHANICS KIT',font=font,fill='#ffedc7')
    draw.text((24,43),'Real felt + shipped ring / clamp / mount / stem. Board scale: 70 px per world unit. Normal swatches at lower right.',font=small,fill='#ffedc7')
    draw.text((24,65),'Normal-based preview light; not a Unity capture. Board alpha checked. UI icons are pre-lit. Labels and counts remain runtime UI.',font=small,fill='#ffedc7')
    cache={}
    def load(name, tint=None):
        path=os.path.join(root,'Assets/Orcub/Art/Sprites',name) if name.startswith(('ring_r','clamp.png','mount.png','stem.png')) else os.path.join(out,name)
        ck=(path,tint)
        if ck in cache:return cache[ck].copy()
        im=Image.open(path).convert('RGBA')
        np=path.replace('.png','_n.png')
        if not name.endswith('_n.png') and os.path.exists(np):
            normal=Image.open(np).convert('RGB')
            shade=Image.new('L',normal.size)
            shade.putdata([round(255*min(1,.35+.65*max(0,-.35*(r/127.5-1)+.45*(g/127.5-1)+.82*(b/127.5-1)))) for r,g,b in normal.getdata()])
            a=im.getchannel('A');im=ImageChops.multiply(im,Image.merge('RGBA',(shade,shade,shade,Image.new('L',shade.size,255))));im.putalpha(a)
        if tint:
            a=im.getchannel('A');im=ImageChops.multiply(im,Image.new('RGBA',im.size,tint));im.putalpha(a)
        cache[ck]=im.copy()
        return im
    errors=[]
    for i,e in enumerate(items):
        x=(i%4)*cw;y=100+(i//4)*ch
        cell=Image.new('RGBA',(cw,ch),(0,0,0,0));bg=Image.Image.resize(felt,(cw,ch));cell.alpha_composite(bg.convert('RGBA'))
        d=ImageDraw.Draw(cell);d.text((14,12),e['file'],font=small,fill='#fff0cf')
        d.text((14,32),f"{e['px'][0]} x {e['px'][1]} / "+('UI pre-lit' if not e['normal'] else 'albedo + normal'),font=small,fill='#e6bfaa')
        im=load(e['file']);a=im.getchannel('A');bbox=a.getbbox()
        if bbox and (bbox[0]==0 or bbox[1]==0 or bbox[2]==im.width or bbox[3]==im.height): errors.append(e['file']+' clipped')
        if e['normal']:
            normal=load(e['normal'])
            # Cycles uses 16 normal samples vs 48 albedo samples; share exact antialiased coverage.
            normal.putalpha(a); normal.save(os.path.join(out,e['normal']))
            if ImageChops.difference(a,normal.getchannel('A')).getbbox():errors.append(e['file']+' normal alpha differs')
            # No rectangular background alpha or side-lit shadows are introduced by these passes.
            if any(a.getpixel(p) for p in [(0,0),(im.width-1,0),(0,im.height-1),(im.width-1,im.height-1)]):errors.append(e['file']+' opaque corners')
        def put(image,wx,wy,source_ppu=200):
            image=image.resize((max(1,round(image.width*ppu/source_ppu)),max(1,round(image.height*ppu/source_ppu))),Image.Resampling.LANCZOS)
            cell.alpha_composite(image,(round(cw/2+wx*ppu-image.width/2),round(200-wy*ppu-image.height/2)))
        name=e['file']
        R=e['referenceRingRadius'] if '_r' in name else 1.0
        ring=load(f'ring_r{round(R*100):03d}.png',(242,114,152,255))
        if e['normal']:
            if name.startswith('linked_'):
                put(ring.rotate(180),-1.36,0);mate=load('ring_r100.png',(171,133,239,255)).rotate(18);put(mate,1.36,0)
                if 'teeth' in name:put(im,0,0)
                else:put(im,-1.36,0);put(im,1.36,0)
            else:
                put(ring,0,0)
                if e['anchor']=='clamp':
                    if name.startswith('clamp_'):
                        shape=name.removeprefix('clamp_').removesuffix('.png')
                        put(load('ring_stamp_'+shape+'_r100.png'),0,-R)
                        im=load(name,(242,114,152,255))
                    put(load('stem.png',(244,215,153,255)).rotate(90,expand=True),0,R-.85)
                    put(load('mount.png',(244,215,153,255)),0,R-1.48)
                    if name.startswith('clay_'):put(load('clamp.png',(244,215,153,255)),0,R)
                    put(im,0,R)
                elif name.startswith('spring_countdown'):
                    put(load('spring_open.png'),0,R);put(im,.7,-.3)
                elif name.startswith(('moves_badge','identifier_badge')):put(im,1.4,1.0)
                else:
                    put(load('clamp.png',(244,215,153,255)),0,-R)
                    put(im,0,R)
            sw=normal.copy();sw.thumbnail((60,60));cell.alpha_composite(sw,(cw-70,ch-74));d.text((14,ch-25),'GAME SCALE / same tube width and pivot',font=small,fill='#d8bca7')
        else:
            im.thumbnail((240,240));cell.alpha_composite(im,((cw-im.width)//2,58))
            d.text((14,ch-25),'INTRO / UI    alpha, baked upper-left light',font=small,fill='#d8bca7')
        sheet.paste(cell.convert('RGB'),(x,y))
    path=os.path.join(root,'ArtSource/ui_concepts/features_v1/mech_kit_contact.png');sheet.save(path)
    data['validation']=dict(contact='ArtSource/ui_concepts/features_v1/mech_kit_contact.png',boardPixelsPerUnit=ppu,normalAlpha='exactly copied from paired albedo after Cycles rendering',errors=errors)
    with open(os.path.join(out,'mechanics.json'),'w') as f:json.dump(data,f,indent=2)
    print(json.dumps(dict(contact=path,sprites=len(items),errors=errors),indent=2))
    if errors:raise SystemExit(1)


if '--contact' in sys.argv:
    contact_sheet()
    raise SystemExit(0)

HERE = os.path.dirname(os.path.abspath(__file__))
exec(compile(open(os.path.join(HERE, 'build_parts.py')).read().replace('\nmain()\n', '\n'), 'build_parts', 'exec'))
OUT_DIR = os.path.join(ROOT, 'Assets/Orcub/Art/Mechanics')
ONLY=set(argv[argv.index('--only')+1].split(',')) if '--only' in argv else set()
META = json.load(open(os.path.join(OUT_DIR,'mechanics.json')))['sprites'] if ONLY else []
SHAPES = ('triangle','square','circle','star','diamond','heart')


def material(name, rgb):
    m = clay_material(); m.name = name
    m.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (*rgb,1)
    return m


def paint(ob, mat):
    ob.data.materials.clear(); ob.data.materials.append(mat)
    return ob


def join(parts):
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active=parts[0]; bpy.ops.object.join()
    return bpy.context.object


def line(points, width, mat):
    curve=bpy.data.curves.new('soft clay stroke','CURVE'); curve.dimensions='3D'
    curve.bevel_depth=width; curve.bevel_resolution=4; curve.resolution_u=16
    poly=curve.splines.new('POLY'); poly.points.add(len(points)-1)
    for p,co in zip(poly.points,points): p.co=(*co,1)
    ob=bpy.data.objects.new('stroke',curve); bpy.context.collection.objects.link(ob)
    bpy.context.view_layer.objects.active=ob; ob.select_set(True)
    bpy.ops.object.convert(target='MESH'); ob.select_set(False)
    return paint(ob,mat)


def outline(points, z=.17, depth=.035, mat=None):
    verts=[(x,y,z+h) for h in (0,depth) for x,y in points]; n=len(points)
    faces=[tuple(reversed(range(n))),tuple(range(n,2*n))]
    faces += [(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    mesh=bpy.data.meshes.new('stamp'); mesh.from_pydata(verts,[],faces);mesh.update()
    ob=bpy.data.objects.new('stamp',mesh);bpy.context.collection.objects.link(ob)
    bevel=ob.modifiers.new('soft stamp edges','BEVEL');bevel.width=.012;bevel.segments=3
    bpy.context.view_layer.objects.active=ob; bpy.ops.object.modifier_apply(modifier=bevel.name)
    return paint(ob,mat or DARK)


def stamp(shape, size=.13, pos=(0,0), z=.25, mat=None):
    if shape=='heart':
        pts=[]
        for i in range(64):
            t=math.tau*i/64;pts.append((math.sin(t)**3, (13*math.cos(t)-5*math.cos(2*t)-2*math.cos(3*t)-math.cos(4*t))/16))
        pts.reverse()
    else:
        n={'triangle':3,'square':4,'circle':48,'star':10,'diamond':4}[shape]
        start=math.pi/4 if shape=='square' else math.pi/2
        pts=[((.46 if shape=='star' and i%2 else 1)*math.cos(start+math.tau*i/n),(.46 if shape=='star' and i%2 else 1)*math.sin(start+math.tau*i/n)) for i in range(n)]
    return outline([(pos[0]+x*size,pos[1]+y*size) for x,y in pts],z,mat=mat)


def arrow(R):
    pts=[(-.23,0),(-.03,-.105),(-.03,-.045),(.23,-.045),(.23,.045),(-.03,.045),(-.03,.105)]
    # Canonical top-of-ring tangent points left: positive / anticlockwise.
    return outline([(x,y+math.sqrt(R*R-x*x)-R) for x,y in pts],.152,.022,CREAM)


def key():
    parts=[paint(swept_band('key bow',.115,0,360,.065,.09,.03),CREAM)]
    parts += [paint(rounded_box('shaft',(.09,.38,.09),(0,-.27,0),.035),CREAM)]
    for y in (-.30,-.41): parts.append(paint(rounded_box('bit',(.17,.065,.09),(.065,y,0),.025),CREAM))
    ob=join(parts);ob.rotation_euler.z=-.55; return ob


def lock(opened=False):
    parts=[paint(rounded_box('lock body',(.38,.30,.16),(0,-.12,.10),.075),CREAM)]
    pts=[(-.13,0,.14),(-.13,.18,.14)]
    pts += [(.13*math.cos(math.pi-i*math.pi/24),.18+.13*math.sin(math.pi-i*math.pi/24),.14) for i in range(25)]
    pts.append((.13,.08 if opened else 0,.14))
    if opened: pts=[(x-.07,y+.08,z) for x,y,z in pts]
    parts.append(line(pts,.04,CREAM))
    parts.append(paint(puck('keyhole',.037,.012,.005,(0,-.105,.19)),DARK))
    parts.append(paint(rounded_box('key slot',(.035,.07,.012),(0,-.15,.19),.009),DARK))
    return join(parts)


def oil():
    parts=[paint(rounded_box('oil can',(.43,.36,.21),(0,0,.04),.075),GOLD)]
    parts.append(line([(-.18,.06,.06),(-.35,.21,.06),(-.46,.23,.06)],.045,GOLD))
    parts.append(line([(.21,.11,.04),(.34,.09,.04),(.35,-.10,.04),(.21,-.14,.04)],.035,GOLD))
    parts.append(paint(rounded_box('lid',(.16,.07,.10),(0,.23,.04),.025),CREAM))
    parts.append(outline([(0,.08),(-.065,-.025),(-.045,-.085),(.045,-.085),(.065,-.025)],.15,.025,CREAM))
    return join(parts)


def rust(R, seized=False, variant=0):
    parts=[]
    for i in range(19 if seized else 9):
        x=-.28+i*.031; y=.065*math.sin(i*2.4+variant)
        ob=ellipsoid('rust flake',(.035+.014*(i%3),.028+.011*(i%2),.002),(x,y+math.sqrt(R*R-x*x)-R,.152))
        parts.append(paint(ob,RUST if i%3 else RUST_DARK))
    if seized:
        parts += [line([(-.19,-.12,.18),(.19,.12,.18)],.035,RUST_DARK),line([(-.19,.12,.18),(.19,-.12,.18)],.035,RUST_DARK)]
    return join(parts)


def spring(opened=False):
    ob,_=build_clamp();paint(ob,CREAM)
    if opened:
        for v in ob.data.vertices:
            p=ob.matrix_world @ v.co
            if p.y>-.25: v.co.x += .10 if p.x>0 else -.10
    pts=[]
    for i in range(241):
        t=i/240; a=t*math.tau*5
        pts.append((.09*math.cos(a),-.40-t*.42,.075+.09*math.sin(a)))
    return join([ob,line(pts,.025,CREAM),paint(rounded_box('spring foot',(.29,.12,.22),(0,-.89,.06),.05),CREAM)])


def shell(cracked=False):
    ob,_=build_clamp();ob.scale=(1.065,1.055,1.12);paint(ob,COAT)
    if cracked:
        # Real fissures remove shell material, revealing the underlying clamp sprite.
        for sign in (-1,1):
            cut=rounded_box('crack cutter',(.035,.8,.8),(sign*.165,-.07,.15),.008)
            cut.rotation_euler.z=sign*.18
            mod=ob.modifiers.new('split coating','BOOLEAN');mod.operation='DIFFERENCE';mod.object=cut
            bpy.context.view_layer.objects.active=ob;bpy.ops.object.modifier_apply(modifier=mod.name)
            bpy.data.objects.remove(cut,do_unlink=True)
        for v in ob.data.vertices:
            if v.co.x>0: v.co.x+=.016
    return ob


def badge():
    return paint(rounded_box('counter base',(.47,.36,.14),(0,0,0),.065),CREAM)


def pair_symbol():
    parts=[]
    for x in (-.085,.085):
        o=paint(swept_band('pair loop',.105,0,360,.04,.06,.025),CREAM);o.location.x=x;parts.append(o)
    return join(parts)


def save(ob,name,half=.55,anchor='tube',note='',radius=None,tint=False,ui=False):
    if ONLY and name not in ONLY:return
    META[:]=[e for e in META if e['file']!=name+'.png']
    if ui:
        for o in bpy.context.scene.objects:
            if o.type=='MESH':o.hide_render=o!=ob
        scene=bpy.context.scene;scene.render.resolution_x=scene.render.resolution_y=240
        scene.camera.location=(0,0,10);scene.camera.data.ortho_scale=half*2
        scene.render.filepath=os.path.join(OUT_DIR,name+'.png');bpy.ops.render.render(write_still=True)
        ob.hide_render=True;w=h=240
    else: w,h=render(ob,name+'.png',half,half,shadow=False)
    META.append(dict(file=name+'.png',normal=None if ui else name+'_n.png',px=[w,h],ppu=240/(2*half) if ui else PPU,pivot=[.5,.5],anchor='ring_surface' if radius else anchor,anchorOffset=[0,radius or (.48 if anchor=='tube_badge' else -1.22 if anchor=='clamp_badge' else 0)],referenceRingRadius=radius or 1.0,scaleRelativeToRingRadius=2*half/(radius or 1.0),tintable=tint,lighting='prelit UI' if ui else 'white dome albedo + tangent normal; Unity URP 2D',description=note))


def main():
    os.makedirs(OUT_DIR,exist_ok=True)
    reset_scene()
    global NORMAL_MAT,CREAM,DARK,GOLD,RUST,RUST_DARK,COAT
    NORMAL_MAT=normal_material();CREAM=clay_material();DARK=material('deep plum stamp',(.12,.025,.045));GOLD=material('oil ochre',(.94,.51,.035));RUST=material('rust terracotta',(.39,.12,.035));RUST_DARK=material('rust umber',(.17,.045,.015));COAT=material('raw clay coating',(.66,.43,.24))
    for R in RING_RADII:
        suffix=f'_r{round(R*100):03d}'
        save(arrow(R),'ratchet_arrow'+suffix,.4,note='Flush curved inlay; left arrow at top = CCW. Flip X for CW; also flip normal X.',radius=R,tint=True)
        for v in range(2):save(rust(R,variant=v),'rust_patch'+str(v+1)+suffix,.4,note='Curved rust freckles, does not imply frozen yet.',radius=R)
        save(rust(R,True),'rust_seized'+suffix,.4,note='Dense rust with crossed seizure mark; frozen is irreversible.',radius=R)
        for shape in SHAPES:
            save(stamp(shape,.115,z=.153,mat=CREAM),'ring_stamp_'+shape+suffix,.35,note='Matching ring shape; assign same symbol to equal palette index.',radius=R,tint=True)
    for shape in SHAPES:
        ob,_=build_clamp();paint(ob,CREAM)
        # Stamp on the central yoke, clear of both tube cuffs.
        save(join([ob,stamp(shape,.061,(0,-.33),.203)]),'clamp_'+shape,.55,'clamp',note='Replacement original clamp with dark identity stamp; tint pale base.',tint=True)
    save(pair_symbol(),'linked_pair_mark',.35,'ring_center',note='Repeat on both paired rings; positive group identifier may be UI text.',tint=True)
    # Existing ring teeth already mesh. This extra short tooth bank seats radially at contact.
    parts=[paint(rounded_box('coupler spine',(.13,.54,.17),(0,0,0),.05),CREAM)]
    for y in (-.2,0,.2):parts.append(paint(rounded_box('mesh tooth',(.30,.11,.16),(.09,y,0),.045),CREAM))
    save(join(parts),'linked_teeth',.4,'pair_contact',note='Contact midpoint; +X points from first ring toward partner. Use where original teeth do not touch.',tint=True)
    save(key(),'key',.65,note='Carrier pickup; draw matching KeyId on identifier base.',tint=True)
    for opened in (False,True):save(lock(opened),'padlock_'+('unlocked' if opened else 'locked'),.55,note='Unlocked when matching key carrier is removed.',tint=True)
    save(oil(),'oil_can',.65,note='Oil collected only when carrier is pulled; matching OilId drawn by UI.')
    save(badge(),'moves_badge',.36,'tube_badge',note='Draw max(0,RustAfter-Moves) on centre; no baked numeral.',tint=True)
    save(badge(),'identifier_badge',.36,'tube_badge',note='Shared key/padlock and oil/rust ID base; UI draws positive ID.',tint=True)
    for opened in (False,True):save(spring(opened),'spring_'+('open' if opened else 'closed'),1.06,'clamp',note='Replacement clamp; coil extends down stem. Open while inactive until K further moves.',tint=True)
    save(badge(),'spring_countdown',.36,'clamp_badge',note='Draw max(0,SpringMoves-(Moves-ReleasedAt)); release gesture is move zero.',tint=True)
    for cracked in (False,True):save(shell(cracked),'clay_'+('cracked' if cracked else 'intact'),.65,'clamp',note='Overlay original clamp: Passes=1 cracked; Passes=0 intact.')
    # An explicit empty state preserves bounds/pivot when swapping sprite references.
    empty=paint(rounded_box('gone',(.01,.01,.01),(0,0,-1),.001),CREAM);empty.hide_render=True
    empty.scale=(0,0,0)
    save(empty,'clay_gone',.65,'clamp',note='Transparent shell; show bare original clamp after second distinct gap entry.')
    # UI lighting follows build_ui_kit; board render passes above have no area lights.
    scene=bpy.context.scene;scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.6
    light=bpy.data.lights.new('UI upper left','AREA');light.energy=420;light.size=4
    lo=bpy.data.objects.new('UI upper left',light);scene.collection.objects.link(lo);lo.location=(-2.5,3,5);lo.rotation_euler=(math.radians(-30),math.radians(-25),0)
    slab=paint(rounded_box('Boss badge',(1.65,.65,.20),(0,0,0),.095),material('boss coral',(.75,.13,.08)))
    bpy.ops.object.text_add(location=(0,-.17,.14));txt=bpy.context.object;txt.data.body='Boss';txt.data.align_x='CENTER';txt.data.size=.48;txt.data.extrude=.018;txt.data.bevel_depth=.008;txt.data.materials.append(CREAM);bpy.ops.object.convert(target='MESH')
    save(join([slab,txt]),'boss_badge',1.0,'ui',note='Top-bar Boss badge. Approved boss board uses ordinary mechanic pieces, no extra accent.',ui=True)
    pink=material('icon coral',(.76,.24,.35));purple=material('icon lilac',(.48,.27,.72))
    for mechanic in ('ratchet','linked','colour_clamp','key_padlock','rust_oil','spring','clay','mix'):
        ring,_=build_ring(1.0);paint(ring,purple if mechanic=='spring' else pink);parts=[ring]
        if mechanic=='ratchet': o=arrow(1);o.location.y=1;parts.append(o)
        elif mechanic=='linked':
            ring.scale=(.65,)*3;ring.location.x=-.86;ring.rotation_euler.z=math.pi
            mate,_=build_ring(1);paint(mate,purple);mate.scale=(.65,)*3;mate.location.x=.86;mate.rotation_euler.z=math.radians(18);parts += [mate,pair_symbol()]
        elif mechanic=='colour_clamp':
            o=stamp('triangle',.15,(0,1),.17,CREAM);c,_=build_clamp();paint(c,GOLD);c.location.y=-1;mark=stamp('triangle',.061,(0,-1.33),.203,DARK);parts += [o,c,mark]
        elif mechanic=='key_padlock': parts += [key(),lock()];parts[-2].location=(-.35,.2,.3);parts[-1].location=(.4,-.25,.3)
        elif mechanic=='rust_oil': o=rust(1);o.location.y=1;parts += [o,oil()]
        elif mechanic=='spring':o=spring(False);o.rotation_euler.z=math.pi/2;o.location=(1,0,.1);parts.append(o)
        elif mechanic=='clay':o=shell(True);o.location=(0,-1,.1);parts.append(o)
        else:parts += [key(),oil(),pair_symbol()];parts[-3].location.x=-.4;parts[-2].location.x=.4
        save(join(parts),'intro_'+mechanic,2.05,'ui',note='Pre-lit intro-card illustration, no baked UI text.',ui=True)
    data=dict(version=1,blender=bpy.app.version_string,conventions=dict(ppu=200,units='1 Blender unit = 1 Unity world unit',tubeWidth=TUBE_W,tubeHeight=TUBE_H,ringRadii=RING_RADII,tube='At top of ring: anchor=center+(0,R), local X=tangent right, local Y=radial out. Rotate by angle-90 degrees. Decals follow exact radius; do not scale tube width with radius.',clamp='Exact build_parts pivot, tube X, stem -Y. Replacement variants use original clamp scale. Coating is overlay at same pivot.',badges='Offset moves/ID base radially outward by .48 world units; clamp countdown at (0,-1.22). Numbers and IDs supplied by UI.',normal='Raw tangent RGB=n*.5+.5; alpha silhouette identical to albedo. Unity normal texture: sRGB off. When flipping arrow X, flip normal red too.',boss='No unique board accent in approved concept; compose existing mechanics.'),sprites=META)
    with open(os.path.join(OUT_DIR,'mechanics.json'),'w') as f:json.dump(data,f,indent=2)


if __name__=='__main__': main()
