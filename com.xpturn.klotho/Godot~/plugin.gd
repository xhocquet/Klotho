# plugin.gd
@tool
extends EditorPlugin

const MENU_LABEL := "Klotho: Convert DataAsset JSON -> bytes"
const NAVMESH_MENU := "Klotho: Export FPNavMesh"
const VISUALIZER_MENU := "Klotho: NavMesh Visualizer"
var _tool         # KlothoDataAssetConvertTool (C# [Tool][GlobalClass]) — requires the C# assembly built once.
var _context_menu # KlothoJsonContextMenu (C# [Tool][GlobalClass])
var _vis          # GodotFPNavMeshVisualizer (C# [Tool][GlobalClass]) — owns the 3D overlay + dock.

func _enter_tree() -> void:
    _tool = KlothoDataAssetConvertTool.new()
    add_tool_menu_item(MENU_LABEL, Callable(self, "_on_convert"))
    add_tool_menu_item(NAVMESH_MENU, Callable(self, "_export_navmesh"))

    _context_menu = KlothoJsonContextMenu.new()
    _context_menu.Init(_tool)
    add_context_menu_plugin(EditorContextMenuPlugin.ContextMenuSlot.CONTEXT_SLOT_FILESYSTEM, _context_menu)

    # NavMesh visualizer: controller owns the dock + 3D overlay. Force-forwarding is required
    # because the tool is data-driven (no selected node).
    _vis = GodotFPNavMeshVisualizer.new()
    _vis.Init(self)
    add_tool_menu_item(VISUALIZER_MENU, Callable(self, "_toggle_visualizer"))
    set_input_event_forwarding_always_enabled()
    set_force_draw_over_forwarding_enabled()
    # _handles() is intentionally NOT overridden — force lists are _handles-independent,
    # and overriding it on this shared plugin would claim to edit every selected object.

func _exit_tree() -> void:
    remove_tool_menu_item(MENU_LABEL)
    remove_tool_menu_item(NAVMESH_MENU)
    remove_context_menu_plugin(_context_menu)
    remove_tool_menu_item(VISUALIZER_MENU)
    if _vis:
        _vis.Shutdown()
    _tool = null
    _context_menu = null
    _vis = null

func _on_convert() -> void:
    if _tool:
        _tool.ConvertSelected()

func _export_navmesh() -> void:
    var region = null
    for n in EditorInterface.get_selection().get_selected_nodes():
        if n is NavigationRegion3D:
            region = n
            break
    if region == null:
        push_error("[Klotho] Select a NavigationRegion3D in the scene.")
        return
    var exporter = GodotFPNavMeshExporter.new()
    exporter.ExportNavMesh(region)

func _toggle_visualizer() -> void:
    if _vis:
        _vis.ToggleActive()

func _forward_3d_gui_input(camera, event) -> int:
    if _vis and _vis.IsActive():
        return _vis.HandleInput(camera, event)
    return AFTER_GUI_INPUT_PASS

func _forward_3d_force_draw_over_viewport(overlay) -> void:
    if _vis and _vis.IsActive():
        _vis.DrawLabels(overlay)

func _process(delta) -> void:
    if _vis:
        _vis.OnProcess(delta)
