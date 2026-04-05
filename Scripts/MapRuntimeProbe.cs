using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace cielo.Scripts;

internal static class MapRuntimeProbe
{
    private const BindingFlags DeclaredFlags =
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.DeclaredOnly;

    private static readonly string[] KnownTypeNames =
    [
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapDrawings",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapDrawingInput",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMouseHeldMapDrawingInput",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMouseModeMapDrawingInput",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NControllerMapDrawingInput",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapDrawButton",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapEraseButton",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapClearButton",
        "MegaCrit.Sts2.Core.Saves.MapDrawing.SerializableMapDrawingLine",
        "MegaCrit.Sts2.Core.Saves.MapDrawing.SerializableMapDrawings",
        "MegaCrit.Sts2.Core.Saves.MapDrawing.SerializablePlayerMapDrawings",
    ];

    public static void Initialize()
    {
        ModLog.Info("MapRuntimeProbe: starting type scan.");

        foreach (var typeName in KnownTypeNames)
        {
            LogTypeSummary(typeName);
        }
    }

    private static void LogTypeSummary(string typeName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            ModLog.Info($"MapRuntimeProbe: type not found: {typeName}");
            return;
        }

        var methods = type
            .GetMethods(DeclaredFlags)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        var fields = type
            .GetFields(DeclaredFlags)
            .Select(field => field.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        ModLog.Info(
            $"MapRuntimeProbe: found {type.FullName}, base={type.BaseType?.FullName ?? "<none>"}, " +
            $"methods=[{JoinPreview(methods)}], fields=[{JoinPreview(fields)}]");
    }

    private static string JoinPreview(IEnumerable<string> values, int maxItems = 12)
    {
        var items = values.Take(maxItems + 1).ToList();
        if (items.Count == 0)
        {
            return "<none>";
        }

        var hasMore = items.Count > maxItems;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var preview = string.Join(", ", items);
        return hasMore ? $"{preview}, ..." : preview;
    }
}
