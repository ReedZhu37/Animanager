# Epic humanoid baselines

Source: **locally installed Unreal Engine 5.8.4 official Epic template content**. No third-party re-rig was used. These files were extracted from the installed official templates rather than downloading a second copy.

| File | Source asset | Imported bones |
|---|---|---:|
| UE4_Mannequin.fbx | High/Mannequin, SK_Mannequin | 68 |
| UE5_Manny.fbx | High/Characters, SKM_Manny_Simple | 89 |
| UE5_Quinn.fbx | High/Characters, SKM_Quinn_Simple | 89 |
| UE5_Manny_Idle.fbx | High/Characters, MM_Idle with preview mesh | 89 |

The UE5 meshes are the official **Simple** variants, not the high-density corrective-bone variants. This distinction matters when comparing other Manny rigs.

`provenance.json` records engine version, original asset paths, raw export SHA-256 and final SHA-256. UE material texture/video links were stripped for a neutral test appearance; geometry, model, deformer and animation objects were compared structurally before and after and remained identical. No manual bone renaming or pose changes were applied to the baseline files.

Preparation: `Tools/export_ue_baselines.py` runs in an isolated UE project populated from the installed template resources; `Tools/clean_baseline_materials.py` uses Blender's FBX parser/writer to remove texture references without reimporting the rig. The `.runtime/ue-baseline` project is only a preparation workspace, not a runtime dependency.

These assets remain Epic Games content, not original Animanager artwork. Preserve their source attribution and review the applicable Unreal content terms before redistribution. The generated StudioBot files elsewhere remain small deterministic test fixtures, not human baseline rigs.
