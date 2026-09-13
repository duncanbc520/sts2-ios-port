extends Control

const PORT_BUILD_INFO_PATH := "res://port_build.json"
const MOBILE_CACHE_MANIFEST_PATH := "res://sts2_mobile_cache_manifest.json"
const PORT_LOG_PATH := "user://sts2_port.log"
const EXPECTED_GAME_PCK_BYTES := 1990705700
const EXPECTED_GAME_PCK_SHA256 := "C60F672EE7804E6AEFA1E19A582FA1C80B126A7B0EEF4D084D3ABF110DF2EAB7"
const EXPECTED_CACHE_SCHEMA := 3
const EXPECTED_TEXTURE_COUNT := 3564

@onready var title_label: Label = %TitleLabel
@onready var build_label: Label = %BuildLabel
@onready var status_label: Label = %StatusLabel
@onready var progress_bar: ProgressBar = %ProgressBar
var _mobile_texture_loader: ResourceFormatLoader
var _port_build_id := "unknown"
var _mobile_cache_ready := false

func _ready() -> void:
	_initialize_port_log()
	_load_build_identity()
	_log_stage("bootstrap_ready")
	register_mobile_texture_loader()
	callable_init.call_deferred()

func register_mobile_texture_loader() -> void:
	if OS.get_name() != "iOS":
		return
	var loader_script := load("res://MobileTextureLoader.gd") as Script
	if loader_script == null:
		printerr("[STS2 Bootstrap] MobileTextureLoader.gd was not found.")
		return
	_mobile_texture_loader = loader_script.new() as ResourceFormatLoader
	if _mobile_texture_loader == null:
		printerr("[STS2 Bootstrap] Failed to instantiate mobile texture loader.")
		return
	ResourceLoader.add_resource_format_loader(_mobile_texture_loader, true)
	# Keep a strong reference after Bootstrap.tscn is replaced by the game scene.
	get_tree().root.set_meta("sts2_mobile_texture_loader", _mobile_texture_loader)
	_log("Registered iOS mobile texture loader and pinned it to the root window.")

func callable_init() -> void:
	await get_tree().process_frame
	update_status("Checking game assets...", 0.2)
	await get_tree().process_frame
	
	# Pre-create standard save directories in user://
	DirAccess.make_dir_recursive_absolute("user://Mods")
	DirAccess.make_dir_recursive_absolute("user://Saves")
	DirAccess.make_dir_recursive_absolute("user://MegaCrit/SlayTheSpire2")
	DirAccess.make_dir_recursive_absolute("user://MegaCrit/SlayTheSpire2/saves")
	DirAccess.make_dir_recursive_absolute("user://MegaCrit/SlayTheSpire2/preferences")
	_mobile_cache_ready = mount_mobile_texture_pack()
	if OS.get_name() == "iOS" and not _mobile_cache_ready:
		show_fatal_error("Mobile Texture Cache Missing or Mismatched", "Copy the newly rebuilt SlayTheSpire2-Mobile.pck into this app's Files folder, then reopen the app.")
		return
	_log_stage("mobile_cache_mounted")
	
	# Initialize / verify Spine GDExtension
	if ClassDB.class_exists("SpineSprite"):
		printerr("[STS2 Bootstrap] SpineSprite is already registered in ClassDB!")
	else:
		printerr("[STS2 Bootstrap] SpineSprite NOT registered yet. Checking GDExtension files...")
		if FileAccess.file_exists("res://addons/spine/spine_godot_extension.gdextension"):
			var err = GDExtensionManager.load_extension("res://addons/spine/spine_godot_extension.gdextension")
			printerr("[STS2 Bootstrap] GDExtensionManager.load_extension returned: ", err)
			if ClassDB.class_exists("SpineSprite"):
				printerr("[STS2 Bootstrap] SUCCESS: SpineSprite is now registered in ClassDB!")
			else:
				printerr("[STS2 Bootstrap] WARNING: SpineSprite still not registered after load_extension.")
		else:
			printerr("[STS2 Bootstrap] res://addons/spine/spine_godot_extension.gdextension not found.")

	# Initialize / verify FMOD GDExtension
	if ClassDB.class_exists("FmodServer"):
		printerr("[STS2 Bootstrap] FmodServer is already registered in ClassDB!")
	else:
		printerr("[STS2 Bootstrap] FmodServer NOT registered yet. Checking GDExtension files...")
		if FileAccess.file_exists("res://addons/fmod/fmod.gdextension"):
			var err = GDExtensionManager.load_extension("res://addons/fmod/fmod.gdextension")
			printerr("[STS2 Bootstrap] GDExtensionManager.load_extension for Fmod returned: ", err)
			if ClassDB.class_exists("FmodServer"):
				printerr("[STS2 Bootstrap] SUCCESS: FmodServer is now registered in ClassDB!")
			else:
				printerr("[STS2 Bootstrap] WARNING: FmodServer still not registered after load_extension.")
		else:
			printerr("[STS2 Bootstrap] res://addons/fmod/fmod.gdextension not found.")

	# 1. Check if assets are already mounted
	if ResourceLoader.exists("res://scenes/game.tscn"):
		_log("Game assets already mounted.")
		if OS.get_name() == "iOS" and not validate_mobile_texture_route():
			return
		initialize_fmod_banks()
		launch_game()
		return
		
	# 2. Check user documents folder (Documents/SlayTheSpire2.pck)
	if FileAccess.file_exists("user://SlayTheSpire2.pck"):
		if not validate_game_pck("user://SlayTheSpire2.pck"):
			return
		update_status("Loading SlayTheSpire2.pck from Documents...", 0.5)
		await get_tree().process_frame
		if ProjectSettings.load_resource_pack("user://SlayTheSpire2.pck"):
			_log("Loaded user://SlayTheSpire2.pck successfully.")
			_log_stage("game_pck_mounted")
			if OS.get_name() == "iOS" and not validate_mobile_texture_route():
				return
			update_status("Mounting game resources...", 0.85)
			await get_tree().process_frame
			initialize_fmod_banks()
			launch_game()
			return
		else:
			printerr("[STS2 Bootstrap] FAILED to load user://SlayTheSpire2.pck!")
			
	# 3. Check bundled pck
	if FileAccess.file_exists("res://SlayTheSpire2.pck"):
		if not validate_game_pck("res://SlayTheSpire2.pck"):
			return
		update_status("Loading bundled SlayTheSpire2.pck...", 0.5)
		await get_tree().process_frame
		if ProjectSettings.load_resource_pack("res://SlayTheSpire2.pck"):
			_log("Loaded res://SlayTheSpire2.pck successfully.")
			_log_stage("game_pck_mounted")
			if OS.get_name() == "iOS" and not validate_mobile_texture_route():
				return
			update_status("Mounting game resources...", 0.85)
			await get_tree().process_frame
			initialize_fmod_banks()
			launch_game()
			return
		else:
			printerr("[STS2 Bootstrap] FAILED to load res://SlayTheSpire2.pck!")

	# Missing PCK: Show instructions
	show_missing_pck_instructions()

func update_status(message: String, progress: float) -> void:
	_log(message + " (" + str(int(progress * 100)) + "%)")
	if status_label:
		status_label.text = message
	if progress_bar:
		progress_bar.value = progress * 100.0

func launch_game() -> void:
	_log("Entering launch_game().")
	_log_stage("before_ensure_registered")
	update_status("Launching Slay the Spire 2...", 1.0)
	await get_tree().process_frame
	ProjectSettings.set_setting("input_devices/pointing/emulate_mouse_from_touch", true)
	ProjectSettings.set_setting("input_devices/pointing/emulate_touch_from_mouse", true)
	ProjectSettings.set_setting("display/window/ios/suppress_ui_gesture", true)
	ProjectSettings.set_setting("display/window/ios/hide_home_indicator", true)
	ProjectSettings.set_setting("display/window/ios/hide_status_bar", true)
	
	if has_node("/root/STS2Bootstrapper"):
		_log("Calling EnsureRegistered on /root/STS2Bootstrapper.")
		get_node("/root/STS2Bootstrapper").call("EnsureRegistered")
	_log_stage("after_ensure_registered")
	
	var candidate_scenes = [
		"res://scenes/game.tscn",
		"res://scenes/screens/main_menu.tscn",
		"res://main.tscn",
		"res://src/main.tscn"
	]
	for sc in candidate_scenes:
		if ResourceLoader.exists(sc):
			_log("Transitioning to scene: " + sc)
			_log_stage("before_scene_transition")
			get_tree().change_scene_to_file(sc)
			return
	printerr("[STS2 Bootstrap] ERROR: No candidate game scene found in mounted PCK!")
	show_missing_pck_instructions()

func show_missing_pck_instructions() -> void:
	if status_label:
		status_label.text = "Game Data Not Found!\n\nTo play Slay the Spire 2 on your iPhone:\n1. Open the 'Files' app on this iPhone (or connect to PC via iTunes/3uTools).\n2. Go to: 'On My iPhone' -> 'Slay the Spire 2'.\n3. Copy your 'SlayTheSpire2.pck' file into that folder.\n4. Close and re-open this app."
	if progress_bar:
		progress_bar.visible = false

func show_fatal_error(title: String, detail: String) -> void:
	_log("FATAL " + title + ": " + detail)
	if title_label:
		title_label.text = title
	if status_label:
		status_label.text = detail + "\n\nBuild: " + _port_build_id
	if progress_bar:
		progress_bar.visible = false

func initialize_fmod_banks() -> void:
	if OS.get_name() == "iOS":
		printerr("[STS2 Bootstrap] Skipping desktop FMOD bank preload on iOS to reduce memory pressure.")
		return
	if ClassDB.class_exists("FmodServer"):
		var fmod_server = Engine.get_singleton("FmodServer")
		if fmod_server:
			printerr("[STS2 Bootstrap] Initializing FMOD and loading master banks...")
			var banks = [
				"res://banks/desktop/Master.strings.bank",
				"res://banks/desktop/Master.bank",
				"res://banks/desktop/sfx.bank",
				"res://banks/desktop/ambience.bank"
			]
			for b in banks:
				if FileAccess.file_exists(b):
					fmod_server.call("load_bank", b, 0)
					printerr("[STS2 Bootstrap] Loaded bank: ", b)
				else:
					printerr("[STS2 Bootstrap] Bank file not found: ", b)

func mount_mobile_texture_pack() -> bool:
	var candidates := [
		"user://SlayTheSpire2-Mobile.pck",
		"res://SlayTheSpire2-Mobile.pck"
	]
	for pack_path in candidates:
		if FileAccess.file_exists(pack_path):
			if ProjectSettings.load_resource_pack(pack_path, true):
				if validate_mobile_cache_manifest(pack_path):
					_log("Loaded and validated mobile texture cache: " + pack_path)
					return true
				return false
			_log("Failed to load mobile texture cache: " + pack_path)
	return OS.get_name() != "iOS"

func validate_mobile_cache_manifest(pack_path: String) -> bool:
	if not FileAccess.file_exists(MOBILE_CACHE_MANIFEST_PATH):
		_log("Mobile cache has no manifest: " + pack_path)
		return false
	var manifest = JSON.parse_string(FileAccess.get_file_as_string(MOBILE_CACHE_MANIFEST_PATH))
	if not manifest is Dictionary:
		_log("Mobile cache manifest is invalid JSON: " + pack_path)
		return false
	var source_hash := str(manifest.get("source_pck_sha256", "")).to_upper()
	var schema := int(manifest.get("schema", 0))
	var texture_count := int(manifest.get("texture_count", 0))
	var compression := str(manifest.get("compression", ""))
	if source_hash != EXPECTED_GAME_PCK_SHA256 or schema != EXPECTED_CACHE_SCHEMA or texture_count != EXPECTED_TEXTURE_COUNT or compression != "ASTC_8x8":
		_log("Mobile cache manifest mismatch: " + JSON.stringify(manifest))
		return false
	_log("Mobile cache manifest: " + JSON.stringify(manifest))
	return true

func validate_game_pck(path: String) -> bool:
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		show_fatal_error("Game Data Unreadable", "Could not open " + path)
		return false
	var byte_count := file.get_length()
	file = null
	_log("Game PCK size=" + str(byte_count) + " expected=" + str(EXPECTED_GAME_PCK_BYTES))
	if byte_count != EXPECTED_GAME_PCK_BYTES:
		show_fatal_error("Game Data Version Mismatch", "SlayTheSpire2.pck is not the v0.111.0/public-beta file required by this build.")
		return false
	return true

func validate_mobile_texture_route() -> bool:
	var probe_path := "res://images/atlases/era_atlas.png"
	var texture := ResourceLoader.load(probe_path, "Texture2D", ResourceLoader.CACHE_MODE_IGNORE) as Texture2D
	if texture == null:
		show_fatal_error("Mobile Texture Check Failed", "The ASTC cache could not load the startup probe texture.")
		return false
	var image := texture.get_image()
	var format := image.get_format() if image != null else -1
	var passed := image != null and format == Image.FORMAT_ASTC_8x8
	_log("Texture route probe class=%s format=%s bytes=%s" % [texture.get_class(), str(format), str(image.get_data_size() if image != null else 0)])
	texture = null
	image = null
	if not passed:
		show_fatal_error("Unsafe Desktop Textures Detected", "This build would expand BPTC/DXT textures and exceed the iPhone memory limit, so launch was stopped.")
		return false
	return true

func _load_build_identity() -> void:
	if FileAccess.file_exists(PORT_BUILD_INFO_PATH):
		var info = JSON.parse_string(FileAccess.get_file_as_string(PORT_BUILD_INFO_PATH))
		if info is Dictionary:
			_port_build_id = str(info.get("port_build_id", _port_build_id))
	if build_label:
		build_label.text = "Port build: " + _port_build_id + "  |  Game: v0.111.0 (41cef1ea)"
	_log("Build identity=" + _port_build_id + " expected_pck_sha256=" + EXPECTED_GAME_PCK_SHA256)

func _initialize_port_log() -> void:
	var file := FileAccess.open(PORT_LOG_PATH, FileAccess.WRITE)
	if file != null:
		file.store_line("=== STS2 iOS port session %s ===" % Time.get_datetime_string_from_system(true, true))

func _log(message: String) -> void:
	var line := "[STS2 Bootstrap] " + message
	printerr(line)
	var file := FileAccess.open(PORT_LOG_PATH, FileAccess.READ_WRITE)
	if file == null:
		file = FileAccess.open(PORT_LOG_PATH, FileAccess.WRITE)
	if file != null:
		file.seek_end()
		file.store_line("[%s] %s" % [Time.get_datetime_string_from_system(true, true), line])

func _log_stage(stage: String) -> void:
	var static_mb := float(OS.get_static_memory_usage()) / 1048576.0
	var peak_mb := float(OS.get_static_memory_peak_usage()) / 1048576.0
	var vram_mb := float(RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_VIDEO_MEM_USED)) / 1048576.0
	_log("MEM stage=%s static=%.1fMB peak=%.1fMB vram=%.1fMB" % [stage, static_mb, peak_mb, vram_mb])
	var bootstrapper := get_node_or_null("/root/STS2Bootstrapper")
	if bootstrapper != null:
		bootstrapper.call("RecordMemoryStage", stage)
