extends SceneTree

func _initialize() -> void:
    var baseline := "UE5_Manny"
    var input := "res://BuiltinAssets/Baselines/" + baseline + ".fbx"
    var output := ProjectSettings.globalize_path("res://.runtime/cue-baseline/" + baseline + ".glb")
    var fbx := FBXDocument.new()
    var state := FBXState.new()
    assert(fbx.append_from_file(input, state) == OK)
    var model := fbx.generate_scene(state)
    root.add_child(model)
    var gltf := GLTFDocument.new()
    var result := GLTFState.new()
    assert(gltf.append_from_scene(model, result) == OK)
    assert(gltf.write_to_filesystem(result, output) == OK)
    print("CUE_BASELINE_READY ", output)
    model.queue_free()
    quit()
