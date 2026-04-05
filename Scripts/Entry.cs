using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace cielo.Scripts;

[ModInitializer("Init")]
public static class Entry
{
    public static void Init()
    {
        ModLog.Clear();
        ModLog.Info("Entry.Init starting.");

        var harmony = new Harmony("sts2.cielo.mappaint");
        harmony.PatchAll();
        ModLog.Info("Harmony.PatchAll completed.");

        MapImportLibrary.EnsureImportFolder();
        MapRuntimeProbe.Initialize();
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        MapImportPanel.EnsureInstalled();
        MapPaintHotkeys.EnsureInstalled();
        MapStrokeUserInputWatcher.EnsureInstalled();

        ModLog.Info("Entry.Init finished.");
    }
}
