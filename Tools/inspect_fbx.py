import bpy, sys, json
from pathlib import Path
bpy.ops.wm.read_factory_settings(use_empty=True)
source=sys.argv[sys.argv.index('--')+1]
bpy.ops.import_scene.fbx(filepath=source,automatic_bone_orientation=False)
for o in bpy.context.scene.objects:
    if o.type=='ARMATURE':
        print('RIG_INSPECT '+json.dumps({'object':o.name,'transform':[list(row) for row in o.matrix_world],
          'bones':len(o.data.bones),'roots':[(b.name,[list(row) for row in b.matrix_local]) for b in o.data.bones if b.parent is None],
          'action':o.animation_data.action.name if o.animation_data and o.animation_data.action else None}))
