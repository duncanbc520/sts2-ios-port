extends SceneTree

var _failures := 0

func _init() -> void:
	var loader_script := load("res://MobileTextureLoader.gd") as Script
	_check(loader_script != null, "MobileTextureLoader.gd should load")
	if loader_script != null:
		var loader := loader_script.new() as ResourceFormatLoader
		_check(loader != null, "MobileTextureLoader.gd should instantiate a ResourceFormatLoader")
		if loader != null:
			_check(loader._get_recognized_extensions().has("ctex"), "loader should recognize imported .ctex resources")
			_check(loader._get_resource_type("asset.ctex") == "Texture2D", "loader should report Texture2D resources")
			_check(loader._handles_type(""), "loader should accept an untyped texture request")
			_check(loader._handles_type("Texture2D"), "loader should accept Texture2D requests")
			_check(loader._handles_type("CompressedTexture2D"), "loader should accept imported texture requests")
			_check(loader._handles_type("Resource"), "loader should accept AssetCache's generic Resource requests")
			_check(not loader._handles_type("PackedScene"), "loader should not claim non-texture resources")
			_check(not loader._handles_type("AudioStream"), "loader should not claim audio resources")
			var missing_texture: Texture2D = loader._get_missing_texture()
			_check(missing_texture != null, "iOS cache-miss fallback should create a texture")
			_check(missing_texture.get_width() == 1 and missing_texture.get_height() == 1, "cache-miss fallback should stay 1x1")
			_check(missing_texture == loader._get_missing_texture(), "cache-miss fallback should reuse one texture")

	if _failures == 0:
		print("MobileTextureLoaderTest passed")
	quit(1 if _failures > 0 else 0)

func _check(condition: bool, message: String) -> void:
	if not condition:
		_failures += 1
		push_error(message)
