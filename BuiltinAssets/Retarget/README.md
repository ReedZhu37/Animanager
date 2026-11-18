# Retarget reference assets

These are fixed runtime copies of the project's existing CUE4Parse standard rig baselines:

- `Tools/CueImporter/Baseline/ue4-mannequin.json`
- `Tools/CueImporter/Baseline/ue5-mannequin.json`
- `Tools/CueImporter/Baseline/UE4_Mannequin.glb`
- `Tools/CueImporter/Baseline/UE5_Manny.glb`

The GLBs retain their mesh, weights and inverse binds. The JSON definitions supply standard unweighted bones, including UE5's full 161-bone hierarchy. Runtime code never overwrites these assets. `RigCatalog.Hash` binds saved settings to the fixed files and algorithm version. The existing official `BuiltinAssets/Baselines/UE5_Manny_Idle.fbx` is also used for recognition of its known reference-pose variant.

The copied `profile-provenance.json` records SHA-256 values for these exact rig definitions and GLBs. See `BuiltinAssets/Baselines/README.md` and `BuiltinAssets/Baselines/provenance.json` for the original Epic baseline provenance, and `Docs/RETARGET.md` for the supported workflow. Build.ps1 ships this directory as raw files beside the executable; local user libraries are not required.
