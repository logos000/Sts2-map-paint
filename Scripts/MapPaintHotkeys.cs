using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

internal static class MapPaintHotkeys
{
    private const string NodeName = "MapPaintHotkeys";

    public static void EnsureInstalled()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        if (tree.Root.GetNodeOrNull<Node>(NodeName) is not null)
        {
            return;
        }

        tree.Root.CallDeferred(Node.MethodName.AddChild, new MapPaintHotkeyListener { Name = NodeName });
    }
}

internal partial class MapPaintHotkeyListener : Node
{
    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcessUnhandledInput(true);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey k || !k.Pressed || k.Echo)
        {
            return;
        }

        var map = NMapScreen.Instance;
        if (map is null || !map.IsOpen || !map.IsVisibleInTree())
        {
            return;
        }

        if (k.Keycode != Key.F5)
        {
            return;
        }

        if (MapStrokeInputPlayback.IsActive)
        {
            MapImportPanel.NotifyHotkeyStopPlayback();
        }
        else
        {
            MapImportPanel.NotifyHotkeyStartPlayback();
        }

        GetViewport().SetInputAsHandled();
    }
}
