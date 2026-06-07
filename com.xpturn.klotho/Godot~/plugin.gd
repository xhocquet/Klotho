# plugin.gd
@tool
extends EditorPlugin

const MENU_LABEL := "Klotho: Convert DataAsset JSON -> bytes"
var _tool         # KlothoDataAssetConvertTool (C# [Tool][GlobalClass]) — requires the C# assembly built once.
var _context_menu # KlothoJsonContextMenu (C# [Tool][GlobalClass])

func _enter_tree() -> void:
    _tool = KlothoDataAssetConvertTool.new()
    add_tool_menu_item(MENU_LABEL, Callable(self, "_on_convert"))

    _context_menu = KlothoJsonContextMenu.new()
    _context_menu.Init(_tool)
    add_context_menu_plugin(EditorContextMenuPlugin.ContextMenuSlot.CONTEXT_SLOT_FILESYSTEM, _context_menu)

func _exit_tree() -> void:
    remove_tool_menu_item(MENU_LABEL)
    remove_context_menu_plugin(_context_menu)
    _tool = null
    _context_menu = null

func _on_convert() -> void:
    if _tool:
        _tool.ConvertSelected()
