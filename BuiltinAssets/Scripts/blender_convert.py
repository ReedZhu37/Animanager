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
configured_export=False
if source.suffix.lower() in ('.gltf','.glb'):
    if source.suffix.lower()=='.glb':
        with source.open('rb') as stream:
            header=stream.read(20)
            if len(header)!=20 or header[:4]!=b'glTF' or header[16:20]!=b'JSON': raise RuntimeError('Invalid GLB header')
            size=struct.unpack_from('<I',header,12)[0]
            if size>64*1024*1024: raise RuntimeError('GLB JSON exceeds metadata limit')
            metadata=json.loads(stream.read(size))
    else: metadata=json.loads(source.read_text(encoding='utf-8'))
    extras=metadata.get('extras',{})
    value=extras.get('animanagerSourceFps')
    independent_props=extras.get('animanagerIndependentProps') is True
    configured_export=extras.get('animanagerConfiguredExport') is True
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
    import_options={}
    if configured_export and 'disable_bone_shape' in bpy.ops.import_scene.gltf.get_rna_type().properties:
        import_options['disable_bone_shape']=True
    if configured_export and not independent_props:
        # Keep the neutral transforms while the importer creates its muted per-clip action stashes.
        from io_scene_gltf2.blender.imp.animation import BlenderAnimation
        restore_animation=BlenderAnimation.restore_animation
        BlenderAnimation.restore_animation=staticmethod(lambda *_:None)
        try:
            bpy.ops.import_scene.gltf(filepath=str(source),**import_options)
        finally:
            BlenderAnimation.restore_animation=staticmethod(restore_animation)
    else:
        bpy.ops.import_scene.gltf(filepath=str(source),**import_options)
    if source_rate is not None:
        # FBX export uses fps / fps_base. Keep imported frame coordinates and restore that convention.
        bpy.context.scene.render.fps_base=bpy.context.scene.render.fps/source_rate
        print('ANIMANAGER_SOURCE_FPS '+str(source_rate))
else: raise RuntimeError('Unsupported input format '+source.suffix)
actions=list(bpy.data.actions)
if not any(o.type in ('MESH','ARMATURE') for o in bpy.context.scene.objects) and not (configured_export and actions):
    raise RuntimeError('No model or skeleton in input')
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
action_bounds=None
if actions:
    action_bounds=(min(a.frame_range[0] for a in actions),max(a.frame_range[1] for a in actions))
    bpy.context.scene.frame_start=int(action_bounds[0])
    bpy.context.scene.frame_end=int(round(action_bounds[1])) if source_rate is not None else int(action_bounds[1])
if independent_props and target.suffix.lower()=='.fbx':
    # glTF converts object-local axes to Blender Z-up as well as converting the world.
    # FBX empties expose that object basis to consumers. Restore the authored prop
    # frame, compensating its children so rendered geometry remains unchanged.
    # The App has already baked this prop into scene space; no body rig is modified.
    from mathutils import Matrix
    basis=Matrix.Rotation(math.pi/2,4,'X')
    props=[o for o in bpy.context.scene.objects if o.name=='PrimaryWeaponMotion']
    if len(props)!=1: raise RuntimeError('Independent prop frame is missing or ambiguous')
    frames=range(bpy.context.scene.frame_start,bpy.context.scene.frame_end+1)
    if configured_export and action_bounds is not None:
        first,last=action_bounds
        frames=[first+i for i in range(math.ceil(last-first))]+[last]
        bpy.context.scene.name=metadata['animations'][0]['name']
    for prop in props:
        samples=[]
        for frame in frames:
            integer_frame=math.floor(frame)
            bpy.context.scene.frame_set(integer_frame,subframe=frame-integer_frame)
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

def export_configured_fbx(settings):
    from io_scene_fbx import export_fbx_bin as writer
    # Blender 5.2 bakes whole frame steps and stops before the fractional last frame.
    # Keep the same sample interval and append that endpoint within each private take bake.
    numpy=getattr(writer,'np',None)
    original_bake=writer.fbx_animations_do
    original_animations=writer.fbx_animations

    def bake_with_endpoint(scene_data,ref_id,first,last,*args,**kwargs):
        if independent_props and ref_id is None and action_bounds is not None:
            first,last=action_bounds
        if numpy is None:
            return original_bake(scene_data,ref_id,first,last,*args,**kwargs)

        class BakeSamples:
            def __getattr__(self,name):
                return getattr(numpy,name)

            def arange(self,*arguments,**keywords):
                frames=numpy.arange(*arguments,**keywords)
                if len(arguments)==2 and 'step' in keywords and arguments==(first,numpy.nextafter(last,numpy.inf)):
                    if not len(frames) or frames[-1]<last-1e-6:
                        frames=numpy.append(frames,last)
                    else:
                        frames[-1]=last
                return frames

        writer.np=BakeSamples()
        try:
            return original_bake(scene_data,ref_id,first,last,*args,**kwargs)
        finally:
            writer.np=numpy

    def bake_scene_clips(scene_data):
        scene=scene_data.scene
        targets={}
        clips={}
        transforms=[]
        weights={}
        for obj in scene.objects:
            if obj.type not in settings['object_types']:
                continue
            keys=getattr(obj.data,'shape_keys',None)
            if obj.animation_data:
                transforms.append((obj,obj.matrix_basis.copy()))
                if obj.type=='ARMATURE':
                    transforms.extend((bone,bone.matrix_basis.copy()) for bone in obj.pose.bones)
            if keys and keys.animation_data:
                weights.update((block,block.value) for block in keys.key_blocks)
            for animated in (obj,keys):
                if not animated or not animated.animation_data or animated in targets:
                    continue
                data=animated.animation_data
                targets[animated]=data
                for track in data.nla_tracks:
                    if track.strips and track.strips[0].action:
                        clips.setdefault(track.name,[]).append((data,track.strips[0]))

        if len(clips)!=len(metadata.get('animations',[])):
            raise RuntimeError('Blender could not map all selected animation clips to exportable objects')

        def reset_pose():
            for data in targets.values():
                data.action=None
            for transform,matrix in transforms:
                transform.matrix_basis=matrix
            for key,value in weights.items():
                key.value=value

        takes=[]
        animated_properties=set()
        current=scene.frame_current
        try:
            for name,bindings in clips.items():
                reset_pose()
                for data,strip in bindings:
                    data.action=strip.action
                    if hasattr(data,'action_slot'):
                        data.action_slot=strip.action_slot

                # One glTF clip may animate a rig, independent nodes and morph weights together.
                # Bake the entire scene once with only the stashed slots for that clip active.
                action=bindings[0][1].action
                first=min(strip.action.frame_range[0] for _,strip in bindings)
                last=max(strip.action.frame_range[1] for _,strip in bindings)
                integer_frame=math.floor(first)
                scene.frame_set(integer_frame,subframe=first-integer_frame)
                take=bake_with_endpoint(scene_data,action,first,last,True,force_keep=True)
                if take is None:
                    raise RuntimeError('Blender could not bake animation clip '+name)
                stack,channels,layer,_,start,end=take
                takes.append((stack,channels,layer,name.encode('utf-8'),start,end))
                animated_properties.update((element,prop) for element,(_,curves) in channels.items() for prop in curves)
        finally:
            reset_pose()
            scene.frame_set(current)
        return takes,animated_properties,min(take[4] for take in takes),max(take[5] for take in takes)

    writer.fbx_animations_do=bake_with_endpoint
    if not independent_props:
        writer.fbx_animations=bake_scene_clips
    try:
        bpy.ops.export_scene.fbx(**settings)
    finally:
        writer.fbx_animations_do=original_bake
        writer.fbx_animations=original_animations

if target.suffix.lower()=='.fbx':
    settings=dict(filepath=str(target),use_selection=False,object_types={'MESH','ARMATURE','EMPTY'} if independent_props or configured_export else {'MESH','ARMATURE'},
        add_leaf_bones=False,use_armature_deform_only=False,
        armature_nodetype='NULL',
        axis_forward='X',axis_up='Z',apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',
        bake_anim=True,bake_anim_use_all_actions=not independent_props,bake_anim_use_nla_strips=False,
        bake_anim_simplify_factor=0.0,bake_anim_step=1.0,path_mode='COPY',embed_textures=True)
    if configured_export and actions:
        export_configured_fbx(settings)
    else:
        bpy.ops.export_scene.fbx(**settings)
elif target.suffix.lower()=='.glb':
    bpy.ops.export_scene.gltf(filepath=str(target),export_format='GLB',export_animations=True,
        export_animation_mode='ACTIONS',export_force_sampling=True,export_yup=True)
else: raise RuntimeError('Unsupported output format '+target.suffix)
print('ANIMANAGER_CONVERT_OK '+json.dumps({'file':str(target),'meshes':sum(o.type=='MESH' for o in bpy.context.scene.objects),
    'armatures':sum(o.type=='ARMATURE' for o in bpy.context.scene.objects),'actions':len(actions)}))
