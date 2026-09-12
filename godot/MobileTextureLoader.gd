extends ResourceFormatLoader

const MOBILE_RESOURCE_SUFFIX := ".mobile.res"
var _converted_count := 0

func _get_recognized_extensions() -> PackedStringArray:
	return PackedStringArray(["ctex"])

func _get_resource_type(_path: String) -> String:
	return "Texture2D"

func _handles_type(type: StringName) -> bool:
	return type == "Texture2D" or type == ""

func _load(path: String, _original_path: String, _use_sub_threads: bool, _cache_mode: int) -> Variant:
	# A preconverted resource is used when available. It has a .res suffix so the
	# built-in binary resource loader can read it without recursing back here.
	var cached_path := path + MOBILE_RESOURCE_SUFFIX
	if FileAccess.file_exists(cached_path):
		var cached := ResourceLoader.load(cached_path, "Texture2D", ResourceLoader.CACHE_MODE_REUSE)
		if cached is Texture2D:
			return cached

	# Fall back to converting on demand. This keeps the app usable if the cache
	# was not copied to Documents, although the first launch will take longer.
	var source := CompressedTexture2D.new()
	var load_error: Error = source.load(path)
	if load_error != OK:
		printerr("custom source load failed: ", load_error)
		return load_error
	var image := source.get_image()
	if image.is_compressed():
		var decompress_error: Error = image.decompress()
		if decompress_error != OK:
			printerr("custom image decompress failed: ", decompress_error)
			return decompress_error
	# ASTC 8x8 is supported by iOS and uses one quarter of the storage/VRAM of
	# ASTC 4x4. The original desktop S3TC/BPTC textures can otherwise expand to
	# RGBA8 on iOS and trigger a Jetsam high-water kill during atlas loading.
	var compress_error: Error = image.compress(Image.COMPRESS_ASTC, Image.COMPRESS_SOURCE_GENERIC, 1)
	if compress_error != OK:
		printerr("custom image ASTC compress failed: ", compress_error)
		return compress_error
	_converted_count += 1
	if _converted_count == 1 or _converted_count % 25 == 0:
		printerr("[MobileTextureLoader] Converted ", _converted_count, " texture(s) on demand")
	return ImageTexture.create_from_image(image)
