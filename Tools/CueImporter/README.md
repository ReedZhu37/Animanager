# Animanager CUE4Parse helper

Build from the workspace root with `Tools/Build-CueImporter.ps1`. The script checks the pinned official upstream revision and publishes a self-contained Windows executable. The local source checkout lives under `.runtime/CUE4Parse`; it is not a runtime dependency. App code communicates by child process / JSON and does not reference the parser assembly.

CLI report protocol 3 (App also accepts protocol 2):

```text
CueImporter --version
CueImporter inspect <source-file-directory-or-zip> <empty-output-directory> auto
CueImporter convert <source-file-directory-or-zip> <empty-output-directory> auto
CueImporter validate-motion <converted-motion-directory> <legacy-nodachi-library>
```

Exit 0: parsed inputs with no failures (unsupported entries remain explicit); 2: per-item parse failures or empty input, inspect `conversion-report.json`; 1: invalid invocation / fatal container failure. Never overwrite an existing output directory. Cancellation is process termination by the parent.

The baseline GLBs are Godot exports of the official Epic FBXs in `BuiltinAssets/Baselines`; `ue4-mannequin.json` and `ue5-mannequin.json` define the standard reference skeletons. Preserve Epic attribution. Apache-2.0 CUE4Parse LICENSE and NOTICE are copied into published output. Original Movement Animset Pro and Nodachi source content is not shipped with the application.

Validated readers cover UE4.23 raw tracks, UE5.0 tagged AnimDataModel and UE5.6 Direct FK Sequencer data. Standard UE4/UE5 profiles validate the source hierarchy and bind pose; arbitrary UE packages and cross-rig retargeting remain unsupported. Details: `Docs/CUE4PARSE_IMPORT.md`.

The verified Nodachi rule (all standard UE5 bones plus `Nodachi_Weapon_R` under `hand_r`) emits motion schema 1: a `.motion.json` manifest and shared `objects/`. The preset GLB is copied byte-for-byte. Source, body and prop animation GLBs contain no mesh or skin; raw AnimSequence and Skeleton inputs are archived. Contact is explicitly unknown/observe-only and the source-bone-origin grip is unverified. Other source rigs retain the existing GLB path. Shared format code lives in `Core/Motion/MotionDocument.cs` and `MotionGlb.cs`; only those engine-independent files are linked into this helper. See `Docs/MOTION_STORAGE.md`.
