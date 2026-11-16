"""Run in an isolated UE project populated from Epic's installed template content."""
import unreal, json, hashlib
from pathlib import Path

output = Path(__file__).resolve().parents[1] / 'BuiltinAssets' / 'Baselines'
output.mkdir(parents=True, exist_ok=True)
assets = {
    'UE4_Mannequin': '/Game/Mannequin/Character/Mesh/SK_Mannequin',
    'UE5_Manny': '/Game/Characters/Mannequins/Meshes/SKM_Manny_Simple',
    'UE5_Quinn': '/Game/Characters/Mannequins/Meshes/SKM_Quinn_Simple',
    'UE5_Manny_Idle': '/Game/Characters/Mannequins/Anims/Unarmed/MM_Idle',
}
records = []
neutral = unreal.AssetToolsHelpers.get_asset_tools().create_asset('M_BaselineNeutral', '/Game/Animanager', unreal.Material, unreal.MaterialFactoryNew())
color = unreal.MaterialEditingLibrary.create_material_expression(neutral, unreal.MaterialExpressionConstant3Vector)
color.constant = unreal.LinearColor(0.55, 0.6, 0.65, 1)
unreal.MaterialEditingLibrary.connect_material_property(color, '', unreal.MaterialProperty.MP_BASE_COLOR)
unreal.MaterialEditingLibrary.recompile_material(neutral)
for name, path in assets.items():
    asset = unreal.load_asset(path)
    if asset is None:
        raise RuntimeError('Missing official template asset: ' + path)
    # Baselines measure geometry / skeleton / animation, not UE shader baking.
    # Replace materials only in this temporary project's in-memory mesh copies.
    if isinstance(asset, unreal.SkeletalMesh):
        materials = asset.get_editor_property('materials')
        for slot in materials:
            slot.material_interface = neutral
        asset.set_editor_property('materials', materials)
    task = unreal.AssetExportTask()
    task.object = asset
    task.filename = str(output / (name + '.fbx'))
    task.automated = True
    task.prompt = False
    task.replace_identical = True
    options = unreal.FbxExportOption()
    options.ascii = False
    options.level_of_detail = False
    options.collision = False
    options.export_preview_mesh = True
    options.export_morph_targets = True
    options.export_source_mesh = False
    options.bake_material_inputs = unreal.FbxMaterialBakeMode.DISABLED
    task.options = options
    if not unreal.Exporter.run_asset_export_task(task):
        raise RuntimeError('Export failed: ' + path + ': ' + str(task.errors))
    file = Path(task.filename)
    records.append({'name': name, 'asset': path, 'file': file.name,
                    'bytes': file.stat().st_size, 'sha256': hashlib.sha256(file.read_bytes()).hexdigest()})
    unreal.log('ANIMANAGER_BASELINE_EXPORTED ' + name)
(output / 'provenance.json').write_text(json.dumps({
    'source': 'Epic Games official templates from locally installed Unreal Engine 5.8',
    'engine_version': unreal.SystemLibrary.get_engine_version(),
    'ue4_template': 'Templates/TemplateResources/High/Mannequin/Content',
    'ue5_template': 'Templates/TemplateResources/High/Characters/Content',
    'export': 'Unreal AssetExportTask / FbxExportOption, LOD0, no skeleton edits or retargeting',
    'materials': 'Neutral engine material in exported copy; original UE shaders and texture references are not baseline data',
    'assets': records}, indent=2), encoding='utf-8')
unreal.log('ANIMANAGER_BASELINES_COMPLETE')
