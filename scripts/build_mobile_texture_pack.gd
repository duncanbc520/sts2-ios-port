extends SceneTree

const MANIFEST_PACK_PATH := "res://sts2_mobile_cache_manifest.json"
const CACHE_SCHEMA := 2

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 2:
		printerr("usage: build_mobile_texture_pack.gd INPUT_PCK OUTPUT_PCK [MAX_TEXTURES]")
		quit(2)
		return
	var input_pck := args[0]
	var output_pck := args[1]
	var cache_dir := output_pck.get_base_dir().path_join(".mobile_texture_cache_work")
	var input_file := FileAccess.open(input_pck, FileAccess.READ)
	if input_file == null:
		printerr("failed to open input PCK: ", input_pck)
		quit(3)
		return
	var source_pck_bytes := input_file.get_length()
	input_file = null
	var source_pck_sha256 := FileAccess.get_sha256(input_pck).to_upper()
	print("source PCK bytes=", source_pck_bytes, " sha256=", source_pck_sha256)
	var max_textures := 0
	if args.size() >= 3:
		max_textures = int(args[2])
	if not ProjectSettings.load_resource_pack(input_pck, true):
		printerr("failed to mount input PCK: ", input_pck)
		quit(3)
		return
	DirAccess.make_dir_recursive_absolute(cache_dir)
	var texture_paths: Array[String] = []
	_collect_texture_paths("res://", texture_paths)
	texture_paths.sort()
	if max_textures > 0 and texture_paths.size() > max_textures:
		texture_paths = texture_paths.slice(0, max_textures)
	print("found ", texture_paths.size(), " texture(s) to convert")

	var packer := PCKPacker.new()
	var start_error: Error = packer.pck_start(output_pck)
	if start_error != OK:
		printerr("failed to start output PCK: ", start_error)
		quit(4)
		return

	var converted := 0
	var failures := 0
	for path in texture_paths:
		var source := CompressedTexture2D.new()
		var load_error: Error = source.load(path)
		if load_error != OK:
			printerr("load failed (", load_error, "): ", path)
			failures += 1
			continue
		var image := source.get_image()
		if image.is_compressed():
			var decompress_error: Error = image.decompress()
			if decompress_error != OK:
				printerr("decompress failed (", decompress_error, "): ", path)
				failures += 1
				continue
		var compress_error: Error = image.compress(Image.COMPRESS_ASTC, Image.COMPRESS_SOURCE_GENERIC, 1)
		if compress_error != OK:
			printerr("ASTC compress failed (", compress_error, "): ", path)
			failures += 1
			continue
		var mobile_texture := ImageTexture.create_from_image(image)
		var cache_file := cache_dir.path_join("texture_%05d.res" % converted)
		var save_error: Error = ResourceSaver.save(mobile_texture, cache_file)
		if save_error != OK:
			printerr("save failed (", save_error, "): ", path)
			failures += 1
			continue
		var add_error: Error = packer.add_file(path + ".mobile.res", cache_file, false)
		if add_error != OK:
			printerr("pack failed (", add_error, "): ", path)
			failures += 1
			continue
		converted += 1
		if converted == 1 or converted % 25 == 0:
			print("converted ", converted, "/", texture_paths.size(), ": ", path)
		mobile_texture = null
		image = null
		source = null

	if failures == 0 and converted == texture_paths.size():
		var manifest := {
			"schema": CACHE_SCHEMA,
			"cache_build_id": "astc8x8-v2-" + source_pck_sha256.left(12).to_lower(),
			"source_pck_bytes": source_pck_bytes,
			"source_pck_sha256": source_pck_sha256,
			"game_version": "v0.111.0",
			"game_commit": "41cef1ea",
			"steam_build_id": "24724944",
			"compression": "ASTC_8x8",
			"texture_count": converted,
			"generated_utc": Time.get_datetime_string_from_system(true, true),
		}
		var manifest_file_path := cache_dir.path_join("sts2_mobile_cache_manifest.json")
		var manifest_file := FileAccess.open(manifest_file_path, FileAccess.WRITE)
		if manifest_file == null:
			printerr("failed to create cache manifest: ", manifest_file_path)
			failures += 1
		else:
			manifest_file.store_string(JSON.stringify(manifest, "  "))
			manifest_file = null
			var manifest_add_error := packer.add_file(MANIFEST_PACK_PATH, manifest_file_path, false)
			if manifest_add_error != OK:
				printerr("failed to add cache manifest: ", manifest_add_error)
				failures += 1
			else:
				print("manifest ", JSON.stringify(manifest))

	var flush_error: Error = packer.flush(false)
	if flush_error != OK:
		printerr("failed to finalize output PCK: ", flush_error)
		quit(5)
		return
	print("mobile texture pack complete: converted=", converted, " failures=", failures, " output=", output_pck)
	quit(0 if failures == 0 else 6)

func _collect_texture_paths(path: String, result: Array[String]) -> void:
	var dir := DirAccess.open(path)
	if dir == null:
		return
	for file_name in dir.get_files():
		if file_name.to_lower().ends_with(".ctex"):
			result.append(path.path_join(file_name))
	for subdir in dir.get_directories():
		_collect_texture_paths(path.path_join(subdir), result)
