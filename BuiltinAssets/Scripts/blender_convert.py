"""Headless interchange conversion. No retargeting or implicit skeleton renaming."""
import argparse, sys, json, struct, math
from pathlib import Path
import bpy

parser=argparse.ArgumentParser()
parser.add_argument('--source',required=True)
parser.add_argument('--out',required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
source=Path(args.source).resolve(); target=Path(args.out).resolve()
if target.exists(): raise RuntimeError('Refusing to overwrite '+str(target))
bpy.ops.wm.read_factory_settings(use_empty=True)
source_rate=None
independent_props=False
if source.suffix.lower() in ('.gltf','.glb'):
    if source.suffix.lower()=='.glb':
        with source.open('rb') as stream:
            header=stream.read(20)
            if len(header)!=20 or header[:4]!=b'glTF' or header[16:20]!=b'JSON': raise RuntimeError('Invalid GLB header')
            size=struct.unpack_from('<I',header,12)[0]
            if size>64*1024*1024: raise RuntimeError('GLB JSON exceeds metadata limit')
            metadata=json.loads(stream.read(size))
    else: metadata=json.loads(source.read_text(encoding='utf-8'))
    value=metadata.get('extras',{}).get('animanagerSourceFps')
    independent_props=metadata.get('extras',{}).get('animanagerIndependentProps') is True
    if value is not None:
        if isinstance(value,bool) or not isinstance(value,(float,int)) or not math.isfinite(value) or not 1<=value<=600: raise RuntimeError('Invalid source frame rate')
        source_rate=float(value)
        if abs(source_rate-round(source_rate))<.0001: source_rate=float(round(source_rate))
        bpy.context.scene.render.fps=math.ceil(source_rate)
        # Blender 5.2's glTF importer multiplies by fps_base when mapping seconds to frames.
        bpy.context.scene.render.fps_base=source_rate/bpy.context.scene.render.fps
if source.suffix.lower()=='.fbx':
    bpy.ops.import_scene.fbx(filepath=str(source),use_anim=True,automatic_bone_orientation=False)
elif source.suffix.lower() in ('.gltf','.glb'):
    bpy.ops.import_scene.gltf(filepath=str(source))
    if source_rate is not None:
        # FBX export uses fps / fps_base. Keep imported frame coordinates and restore that convention.
        bpy.context.scene.render.fps_base=bpy.context.scene.render.fps/source_rate
        print('ANIMANAGER_SOURCE_FPS '+str(source_rate))
else: raise RuntimeError('Unsupported input format '+source.suffix)
if not any(o.type in ('MESH','ARMATURE') for o in bpy.context.scene.objects):
    raise RuntimeError('No model or skeleton in input')
actions=list(bpy.data.actions)
# Blender represents an FBX skeleton's root node as the armature object. Godot
# does not count that object as a bone on export. Restore an explicit root bone
# and move the object's animated delta onto it, retaining the object's rest
# transform (including the FBX centimetre scale). Other bone names stay intact.
if source.suffix.lower()=='.fbx':
    for rig in [o for o in bpy.context.scene.objects if o.type=='ARMATURE']:
        if rig.name.lower()!='root' or 'root' in rig.data.bones: continue
        bpy.context.view_layer.objects.active=rig
        rig.select_set(True)
        bpy.ops.object.mode_set(mode='EDIT')
        children=[b for b in rig.data.edit_bones if b.parent is None]
        root=rig.data.edit_bones.new('root');root.head=(0,0,0);root.tail=(0,1,0)
        for child in children: child.parent=root
        bpy.ops.object.mode_set(mode='OBJECT')
        original_action=rig.animation_data.action if rig.animation_data else None
        rest=rig.matrix_world.copy()
        root_pose=rig.pose.bones['root'];root_pose.rotation_mode='QUATERNION'
        for action in actions:
            rig.animation_data_create();rig.animation_data.action=action
            slots=list(action.slots) if hasattr(action,'slots') else []
            if slots: rig.animation_data.action_slot=slots[0]
            frames=range(int(action.frame_range[0]),int(action.frame_range[1])+1)
            sampled=[]
            for frame in frames:
                bpy.context.scene.frame_set(frame)
                sampled.append((frame,rest.inverted() @ rig.matrix_world))
            channels=[]
            if hasattr(action,'fcurves'): channels.append(action.fcurves)
            else:
                for layer in action.layers:
                    for strip in layer.strips:
                        channels.extend(bag.fcurves for bag in strip.channelbags)
            for curves in channels:
                for curve in list(curves):
                    if curve.data_path in ('location','rotation_euler','rotation_quaternion','scale'):
                        curves.remove(curve)
            rig.matrix_world=rest
            for frame,delta in sampled:
                root_pose.matrix_basis=delta
                root_pose.keyframe_insert('location',frame=frame)
                root_pose.keyframe_insert('rotation_quaternion',frame=frame)
                root_pose.keyframe_insert('scale',frame=frame)
        if rig.animation_data: rig.animation_data.action=original_action
        rig.matrix_world=rest;rig.name='Armature'
        bpy.context.scene.frame_set(bpy.context.scene.frame_start)
        print('ANIMANAGER_RESTORED_ROOT')
if actions:
    bpy.context.scene.frame_start=int(min(a.frame_range[0] for a in actions))
    end=max(a.frame_range[1] for a in actions)
    bpy.context.scene.frame_end=int(round(end)) if source_rate is not None else int(end)
if independent_props and target.suffix.lower()=='.fbx':
    # glTF converts object-local axes to Blender Z-up as well as converting the world.
    # FBX empties expose that object basis to consumers. Restore the authored prop
    # frame, compensating its children so rendered geometry remains unchanged.
    # The App has already baked this prop into scene space; no body rig is modified.
    from mathutils import Matrix
    basis=Matrix.Rotation(math.pi/2,4,'X')
    props=[o for o in bpy.context.scene.objects if o.name=='PrimaryWeaponMotion']
    if len(props)!=1: raise RuntimeError('Independent prop frame is missing or ambiguous')
    for prop in props:
        samples=[]
        for frame in range(bpy.context.scene.frame_start,bpy.context.scene.frame_end+1):
            bpy.context.scene.frame_set(frame)
            samples.append((frame,prop.matrix_world.copy() @ basis))
        for child in list(prop.children):
            child.matrix_parent_inverse=basis.inverted() @ child.matrix_parent_inverse
        prop.animation_data_clear();prop.parent=None;prop.rotation_mode='QUATERNION'
        for frame,matrix in samples:
            prop.matrix_world=matrix
            prop.keyframe_insert('location',frame=frame)
            prop.keyframe_insert('rotation_quaternion',frame=frame)
            prop.keyframe_insert('scale',frame=frame)
        bpy.context.scene.frame_set(bpy.context.scene.frame_start)
    print('ANIMANAGER_PROP_FRAME_NORMALIZED')
if target.suffix.lower()=='.fbx':
    bpy.ops.export_scene.fbx(filepath=str(target),use_selection=False,object_types={'MESH','ARMATURE','EMPTY'} if independent_props else {'MESH','ARMATURE'},
        add_leaf_bones=False,use_armature_deform_only=False,
        armature_nodetype='NULL',
        axis_forward='X',axis_up='Z',apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',
        bake_anim=True,bake_anim_use_all_actions=not independent_props,bake_anim_use_nla_strips=False,
        bake_anim_simplify_factor=0.0,bake_anim_step=1.0,path_mode='COPY',embed_textures=True)
elif target.suffix.lower()=='.glb':
    bpy.ops.export_scene.gltf(filepath=str(target),export_format='GLB',export_animations=True,
        export_animation_mode='ACTIONS',export_force_sampling=True,export_yup=True)
else: raise RuntimeError('Unsupported output format '+target.suffix)
print('ANIMANAGER_CONVERT_OK '+json.dumps({'file':str(target),'meshes':sum(o.type=='MESH' for o in bpy.context.scene.objects),
    'armatures':sum(o.type=='ARMATURE' for o in bpy.context.scene.objects),'actions':len(actions)}))
