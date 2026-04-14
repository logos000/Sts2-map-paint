using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

[HarmonyPatch(typeof(NMapScreen), "_Ready")]
internal static class NMapScreenReadyPatch
{
    private static void Postfix(NMapScreen __instance)
    {
        ModLog.Info("Harmony postfix hit: NMapScreen._Ready");
        MapImportPanel.EnsureInstalled();
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class NMapScreenOpenPatch
{
    private static void Postfix(NMapScreen __instance)
    {
        ModLog.Info("Harmony postfix hit: NMapScreen.Open");
        DrawingHistory.Clear();
        MapImportPanel.Show(__instance);
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Close))]
internal static class NMapScreenClosePatch
{
    private static void Postfix()
    {
        ModLog.Info("Harmony postfix hit: NMapScreen.Close");
        MapImportPanel.Hide();
    }
}

