"""Export the official female mannequin from the isolated baseline project."""
import unreal, json, hashlib
from pathlib import Path

output = Path(__file__).resolve().parents[1] / 'BuiltinAssets' / 'Baselines'
asset_path = '/Game/Mannequin/Character/Mesh/SK_Mannequin_Female'
asset = unreal.load_asset(asset_path)
if asset is None:
    raise RuntimeError('Missing official template: ' + asset_path)
for slot in asset.get_editor_property('materials'):
    slot.material_interface = unreal.load_asset('/Engine/EngineMaterials/DefaultMaterial')
task = unreal.AssetExportTask()
task.object = asset
task.filename = str(output / 'UE4_Mannequin_Female.fbx')
task.automated = True
task.prompt = False
task.replace_identical = True
options = unreal.FbxExportOption()
options.level_of_detail = False
options.collision = False
options.export_preview_mesh = True
options.export_source_mesh = False
options.bake_material_inputs = unreal.FbxMaterialBakeMode.DISABLED
task.options = options
if not unreal.Exporter.run_asset_export_task(task):
    raise RuntimeError(str(task.errors))
file = Path(task.filename)
(output / 'female-provenance.json').write_text(json.dumps({
    'source': 'Epic Games official locally installed UE template',
    'engine': unreal.SystemLibrary.get_engine_version(),
    'asset': asset_path, 'file': file.name,
    'sha256': hashlib.sha256(file.read_bytes()).hexdigest(),
    'geometry': 'Original female mannequin geometry, skeleton and skin; no retargeting'
}, indent=2), encoding='utf-8')
unreal.log('ANIMANAGER_FEMALE_EXPORTED')
