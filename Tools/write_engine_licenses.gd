extends SceneTree

func _initialize() -> void:
	var output := ""
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--out-dir="):
			output = argument.substr(10)
	if output.is_empty() or DirAccess.make_dir_recursive_absolute(output) != OK:
		quit(1)
		return
	var notices := {
		"Godot-MIT.txt": Engine.get_license_text(),
		"Godot-third-party-copyright.json": JSON.stringify(Engine.get_copyright_info(), "\t"),
		"Godot-third-party-licenses.json": JSON.stringify(Engine.get_license_info(), "\t"),
	}
	for filename in notices:
		var file := FileAccess.open(output.path_join(filename), FileAccess.WRITE)
		if file == null:
			quit(1)
			return
		file.store_string(notices[filename])
	quit(0)
