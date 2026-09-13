extends ResourceFormatLoader

const MOBILE_RESOURCE_SUFFIX := ".mobile.res"
const PORT_LOG_PATH := "user://sts2_port.log"
var _converted_count := 0
var _cache_hit_count := 0
var _cache_miss_count := 0
var _missing_texture: Texture2D

func _get_recognized_extensions() -> PackedStringArray:
	return PackedStringArray(["ctex"])

func _get_resource_type(_path: String) -> String:
	return "Texture2D"

func _handles_type(type: StringName) -> bool:
	# Imported PNG resources declare CompressedTexture2D in their .import files.
	# AssetCache loads them as Resource, so that hint must route here as well.
	return type == "Resource" or type == "CompressedTexture2D" or type == "Texture2D" or type == ""

func _load(path: String, _original_path: String, _use_sub_threads: bool, _cache_mode: int) -> Variant:
	# A preconverted resource is used when available. It has a .res suffix so the
	# built-in binary resource loader can read it without recursing back here.
	var cached_path := path + MOBILE_RESOURCE_SUFFIX
	if FileAccess.file_exists(cached_path):
		# Do not retain a second cache entry under the .mobile.res alias. The outer
		# ResourceLoader request caches this resource under the original .ctex path.
		var cached := ResourceLoader.load(cached_path, "Texture2D", ResourceLoader.CACHE_MODE_IGNORE)
		if cached is Texture2D:
			_cache_hit_count += 1
			if _cache_hit_count == 1 or _cache_hit_count % 100 == 0:
				_log("cache hits=%d misses=%d latest=%s" % [_cache_hit_count, _cache_miss_count, path])
			return cached
		_log("ERROR cached resource is not Texture2D: " + cached_path)
		if OS.get_name() == "iOS":
			return _get_missing_texture()
		return ERR_FILE_CORRUPT

	_cache_miss_count += 1
	_log("ERROR cache miss=%d path=%s" % [_cache_miss_count, path])
	# Loading a desktop BPTC/DXT texture on iOS expands it to RGBA8 and can push
	# this game beyond the device's Jetsam high-water limit. ResourceLoader tries
	# later loaders after an error, so return one shared 1x1 texture here instead
	# of allowing the built-in loader to decode the desktop source.
	if OS.get_name() == "iOS":
		return _get_missing_texture()

	# Desktop-only fallback used by local diagnostics.
	var source := CompressedTexture2D.new()
	var load_error: Error = source.load(path)
	if load_error != OK:
		_log("desktop fallback source load failed=%d path=%s" % [load_error, path])
		return load_error
	var image := source.get_image()
	if image.is_compressed():
		var decompress_error: Error = image.decompress()
		if decompress_error != OK:
			_log("desktop fallback decompress failed=%d path=%s" % [decompress_error, path])
			return decompress_error
	# ASTC 8x8 is supported by iOS and uses one quarter of the storage/VRAM of
	# ASTC 4x4. The original desktop S3TC/BPTC textures can otherwise expand to
	# RGBA8 on iOS and trigger a Jetsam high-water kill during atlas loading.
	var compress_error: Error = image.compress(Image.COMPRESS_ASTC, Image.COMPRESS_SOURCE_GENERIC, 1)
	if compress_error != OK:
		_log("desktop fallback ASTC compress failed=%d path=%s" % [compress_error, path])
		return compress_error
	_converted_count += 1
	if _converted_count == 1 or _converted_count % 25 == 0:
		_log("desktop fallback conversions=%d" % _converted_count)
	return ImageTexture.create_from_image(image)

func get_stats() -> Dictionary:
	return {
		"cache_hits": _cache_hit_count,
		"cache_misses": _cache_miss_count,
		"desktop_conversions": _converted_count,
	}

func _get_missing_texture() -> Texture2D:
	if _missing_texture == null:
		var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
		image.fill(Color(0, 0, 0, 0))
		_missing_texture = ImageTexture.create_from_image(image)
	return _missing_texture

func _log(message: String) -> void:
	var line := "[MobileTextureLoader] " + message
	printerr(line)
	var file := FileAccess.open(PORT_LOG_PATH, FileAccess.READ_WRITE)
	if file == null:
		file = FileAccess.open(PORT_LOG_PATH, FileAccess.WRITE)
	if file != null:
		file.seek_end()
		file.store_line("[%s] %s" % [Time.get_datetime_string_from_system(true, true), line])
