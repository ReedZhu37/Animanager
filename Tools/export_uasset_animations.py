"""UE Python commandlet bridge; converts AnimSequence assets in an isolated project.

Set ANIMANAGER_UE_BRIDGE_CONFIG to a JSON file with asset_root, output,
preview_mesh and optional names (relative paths). Never saves source assets.
"""
import unreal
import os, json, hashlib, time, traceback
from pathlib import Path
from collections import Counter

config = json.loads(Path(os.environ['ANIMANAGER_UE_BRIDGE_CONFIG']).read_text(encoding='utf-8-sig'))
output = Path(config['output']).resolve()
output.mkdir(parents=True, exist_ok=True)
registry = unreal.AssetRegistryHelpers.get_asset_registry()
registry.scan_paths_synchronous([config['asset_root']], True)
assets = sorted(registry.get_assets_by_path(config['asset_root'], recursive=True), key=lambda a: str(a.package_name))
mesh = unreal.load_asset(config['preview_mesh'])
if not isinstance(mesh, unreal.SkeletalMesh):
    raise RuntimeError('Preview mesh is not available: ' + config['preview_mesh'])
neutral = unreal.AssetToolsHelpers.get_asset_tools().create_asset('M_AnimanagerPreview', '/Game/AnimanagerBridge', unreal.Material, unreal.MaterialFactoryNew())
slots = mesh.get_editor_property('materials')
for slot in slots:
    slot.material_interface = neutral
mesh.set_editor_property('materials', slots)

report = {'engine': unreal.SystemLibrary.get_engine_version(), 'config': config,
          'classes': dict(Counter(str(a.asset_class_path.asset_name) for a in assets)), 'items': []}
report_path = output / 'conversion-report.json'

def save_report():
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')

for data in assets:
    package = str(data.package_name)
    relative = package[len(config['asset_root']):].lstrip('/')
    if config.get('names') and relative not in config['names']:
        continue
    record = {'package': package, 'relative': relative, 'class': str(data.asset_class_path.asset_name)}
    report['items'].append(record)
    if record['class'] != 'AnimSequence':
        record.update(status='unsupported', reason='Only AnimSequence is converted; montage / blend graphs require a separate bake policy.')
        save_report()
        continue
    try:
        start = time.monotonic()
        asset = data.get_asset()
        skeleton = asset.get_editor_property('skeleton')
        if skeleton is None:
            raise RuntimeError('Missing source skeleton dependency; no automatic retargeting was attempted.')
        asset.set_preview_skeletal_mesh(mesh)
        record.update(skeleton=skeleton.get_path_name(), duration=asset.get_editor_property('sequence_length'),
                      frames=asset.get_editor_property('number_of_sampled_frames'),
                      additive=str(asset.get_editor_property('additive_anim_type')),
                      root_motion=asset.get_editor_property('enable_root_motion'),
                      force_root_lock=asset.get_editor_property('force_root_lock'),
                      tracks=[str(n) for n in unreal.AnimationLibrary.get_animation_track_names(asset)])
        target = output / (relative.replace('/', '__') + '.fbx')
        if target.exists():
            raise RuntimeError('Output already exists; select a fresh output directory: ' + str(target))
        task = unreal.AssetExportTask()
        task.object = asset
        task.filename = str(target)
        task.automated = True
        task.prompt = False
        task.replace_identical = False
        options = unreal.FbxExportOption()
        options.ascii = False
        options.level_of_detail = False
        options.collision = False
        options.export_preview_mesh = True
        options.export_morph_targets = False
        options.export_source_mesh = False
        options.map_skeletal_motion_to_root = False
        options.bake_material_inputs = unreal.FbxMaterialBakeMode.DISABLED
        task.options = options
        if not unreal.Exporter.run_asset_export_task(task):
            raise RuntimeError('FBX exporter failed: ' + str(task.errors))
        if not target.is_file() or target.stat().st_size < 1000:
            raise RuntimeError('Exporter did not produce a usable file')
        record.update(status='exported', output=str(target), bytes=target.stat().st_size,
                      sha256=hashlib.sha256(target.read_bytes()).hexdigest(), seconds=round(time.monotonic()-start, 3))
        unreal.log('ANIMANAGER_UASSET_EXPORTED ' + relative)
    except Exception as error:
        record.update(status='failed', error=str(error))
        unreal.log_warning('ANIMANAGER_UASSET_FAILED ' + relative + ': ' + str(error))
    save_report()
report['summary'] = dict(Counter(i['status'] for i in report['items']))
save_report()
unreal.log('ANIMANAGER_UASSET_COMPLETE ' + json.dumps(report['summary']))
