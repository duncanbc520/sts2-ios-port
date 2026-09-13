extends ResourceFormatLoader

const MOBILE_RESOURCE_SUFFIX := ".mobile.res"
const PORT_LOG_PATH := "user://sts2_port.log"
const PORT_BUILD_INFO_PATH := "res://port_build.json"
const DIAGNOSTIC_BUILD_PREFIX := "diag-"
const TRACE_MAX_REQUESTS := 4096
const TRACE_PAYLOAD_PROBE_COUNT := 16
var _converted_count := 0
var _cache_hit_count := 0
var _cache_miss_count := 0
var _missing_texture: Texture2D
var _trace_lock := Mutex.new()
var _trace_initialized := false
var _trace_enabled := false
var _trace_sequence := 0

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
	var trace_sequence := _trace_begin(path, _original_path, cached_path)
	if FileAccess.file_exists(cached_path):
		# Do not retain a second cache entry under the .mobile.res alias. The outer
		# ResourceLoader request caches this resource under the original .ctex path.
		var cached := ResourceLoader.load(cached_path, "Texture2D", ResourceLoader.CACHE_MODE_IGNORE)
		if cached is Texture2D:
			_cache_hit_count += 1
			if _cache_hit_count == 1 or _cache_hit_count % 100 == 0:
				_log("cache hits=%d misses=%d latest=%s" % [_cache_hit_count, _cache_miss_count, path])
			_trace_return(trace_sequence, "cache_hit", path, _original_path, cached_path, cached)
			return cached
		_log("ERROR cached resource is not Texture2D: " + cached_path)
		if OS.get_name() == "iOS":
			var invalid_missing := _get_missing_texture()
			_trace_return(trace_sequence, "cache_invalid_placeholder", path, _original_path, cached_path, invalid_missing)
			return invalid_missing
		_trace_return(trace_sequence, "cache_invalid", path, _original_path, cached_path, cached)
		return ERR_FILE_CORRUPT

	_cache_miss_count += 1
	_log("ERROR cache miss=%d path=%s" % [_cache_miss_count, path])
	# Loading a desktop BPTC/DXT texture on iOS expands it to RGBA8 and can push
	# this game beyond the device's Jetsam high-water limit. ResourceLoader tries
	# later loaders after an error, so return one shared 1x1 texture here instead
	# of allowing the built-in loader to decode the desktop source.
	if OS.get_name() == "iOS":
		var miss_placeholder := _get_missing_texture()
		_trace_return(trace_sequence, "cache_miss_placeholder", path, _original_path, cached_path, miss_placeholder)
		return miss_placeholder

	# Desktop-only fallback used by local diagnostics.
	var source := CompressedTexture2D.new()
	var load_error: Error = source.load(path)
	if load_error != OK:
		_log("desktop fallback source load failed=%d path=%s" % [load_error, path])
		_trace_return(trace_sequence, "desktop_source_error", path, _original_path, cached_path, null)
		return load_error
	var image := source.get_image()
	if image.is_compressed():
		var decompress_error: Error = image.decompress()
		if decompress_error != OK:
			_log("desktop fallback decompress failed=%d path=%s" % [decompress_error, path])
			_trace_return(trace_sequence, "desktop_decompress_error", path, _original_path, cached_path, null)
			return decompress_error
	# ASTC 8x8 is supported by iOS and uses one quarter of the storage/VRAM of
	# ASTC 4x4. The original desktop S3TC/BPTC textures can otherwise expand to
	# RGBA8 on iOS and trigger a Jetsam high-water kill during atlas loading.
	if _is_hdr_format(image.get_format()):
		image.convert(Image.FORMAT_RGBA8)
	var compress_error: Error = image.compress(Image.COMPRESS_ASTC, Image.COMPRESS_SOURCE_GENERIC, Image.ASTC_FORMAT_8x8)
	if compress_error != OK:
		_log("desktop fallback ASTC compress failed=%d path=%s" % [compress_error, path])
		_trace_return(trace_sequence, "desktop_compress_error", path, _original_path, cached_path, null)
		return compress_error
	_converted_count += 1
	if _converted_count == 1 or _converted_count % 25 == 0:
		_log("desktop fallback conversions=%d" % _converted_count)
	var converted_texture := ImageTexture.create_from_image(image)
	_trace_return(trace_sequence, "desktop_fallback", path, _original_path, cached_path, converted_texture)
	return converted_texture

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

func _is_hdr_format(format: int) -> bool:
	return format == Image.FORMAT_RF \
		or format == Image.FORMAT_RGF \
		or format == Image.FORMAT_RGBF \
		or format == Image.FORMAT_RGBAF \
		or format == Image.FORMAT_RH \
		or format == Image.FORMAT_RGH \
		or format == Image.FORMAT_RGBH \
		or format == Image.FORMAT_RGBAH

func _trace_begin(path: String, original_path: String, cached_path: String) -> int:
	if not _diagnostic_trace_is_enabled():
		return -1
	_trace_lock.lock()
	if _trace_sequence >= TRACE_MAX_REQUESTS:
		_trace_lock.unlock()
		return -1
	_trace_sequence += 1
	var sequence := _trace_sequence
	_write_log_line("[MobileTextureTrace] request seq=%d thread=%d original=%s remapped=%s cache=%s cache_exists=%s" % [
		sequence,
		OS.get_thread_caller_id(),
		original_path,
		path,
		cached_path,
		FileAccess.file_exists(cached_path),
	])
	_trace_lock.unlock()
	return sequence

func _trace_return(sequence: int, kind: String, path: String, original_path: String, cached_path: String, resource: Variant) -> void:
	if sequence < 0:
		return
	var resource_class := "<null>"
	var width := -1
	var height := -1
	var format := -1
	var mip_count := -1
	var payload_bytes := -1
	if resource is Texture2D:
		var texture := resource as Texture2D
		resource_class = texture.get_class()
		width = texture.get_width()
		height = texture.get_height()
		if texture is ImageTexture:
			format = (texture as ImageTexture).get_format()
			# get_image() is intentionally bounded: it can create a second image
			# buffer, so probe only the first few requests and HDR/malformed formats.
			if sequence <= TRACE_PAYLOAD_PROBE_COUNT or format == Image.FORMAT_ASTC_4x4_HDR or format == Image.FORMAT_ASTC_8x8_HDR:
				var image := texture.get_image()
				if image != null:
					format = image.get_format()
					mip_count = image.get_mipmap_count()
					payload_bytes = image.get_data_size()
				image = null
	var expected_payload := _expected_astc_payload_bytes(width, height, format, mip_count)
	var serialized_bytes := _file_length(cached_path)
	_trace_lock.lock()
	_write_log_line("[MobileTextureTrace] return seq=%d thread=%d kind=%s original=%s remapped=%s cache=%s class=%s width=%d height=%d format=%d mips=%d payload=%d expected_payload=%d serialized_bytes=%d" % [
		sequence,
		OS.get_thread_caller_id(),
		kind,
		original_path,
		path,
		cached_path,
		resource_class,
		width,
		height,
		format,
		mip_count,
		payload_bytes,
		expected_payload,
		serialized_bytes,
	])
	_trace_lock.unlock()

func _expected_astc_payload_bytes(width: int, height: int, format: int, mip_count: int = -1) -> int:
	var block_size := 0
	if format == Image.FORMAT_ASTC_4x4 or format == Image.FORMAT_ASTC_4x4_HDR:
		block_size = 4
	elif format == Image.FORMAT_ASTC_8x8 or format == Image.FORMAT_ASTC_8x8_HDR:
		block_size = 8
	else:
		return -1
	if width <= 0 or height <= 0:
		return -1
	var total := 0
	var mip_width := width
	var mip_height := height
	var levels := 1 if mip_count < 0 else mip_count + 1
	for _level in levels:
		total += int(ceil(float(mip_width) / block_size)) * int(ceil(float(mip_height) / block_size)) * 16
		mip_width = max(1, int(mip_width / 2))
		mip_height = max(1, int(mip_height / 2))
	return total

func _file_length(path: String) -> int:
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return -1
	var length := file.get_length()
	file = null
	return length

func _diagnostic_trace_is_enabled() -> bool:
	if _trace_initialized:
		return _trace_enabled
	_trace_initialized = true
	if OS.get_name() != "iOS" or not FileAccess.file_exists(PORT_BUILD_INFO_PATH):
		return false
	var info = JSON.parse_string(FileAccess.get_file_as_string(PORT_BUILD_INFO_PATH))
	if info is Dictionary:
		_trace_enabled = str(info.get("port_build_id", "")).begins_with(DIAGNOSTIC_BUILD_PREFIX)
	return _trace_enabled

func _log(message: String) -> void:
	var line := "[MobileTextureLoader] " + message
	_write_log_line(line)

func _write_log_line(line: String) -> void:
	printerr(line)
	var file := FileAccess.open(PORT_LOG_PATH, FileAccess.READ_WRITE)
	if file == null:
		file = FileAccess.open(PORT_LOG_PATH, FileAccess.WRITE)
	if file != null:
		file.seek_end()
		file.store_line("[%s] %s" % [Time.get_datetime_string_from_system(true, true), line])
		file.flush()
		file = null
