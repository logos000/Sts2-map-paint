using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

internal readonly record struct MapAutoPaintResult(bool Success, string Message);

internal static class MapAutoPainter
{
    internal static List<Vector2[]> MapStrokesToNetSpace(
        NMapScreen mapScreen,
        IReadOnlyList<Vector2[]> strokes,
        MapPaintSettings settings)
    {
        return MapToNetSpace(mapScreen, strokes, settings).ToList();
    }

    internal static IEnumerable<Vector2[]> MapToNetSpace(
        NMapScreen mapScreen,
        IReadOnlyList<Vector2[]> strokes,
        MapPaintSettings settings)
    {
        var visibleRect = mapScreen.GetViewportRect();
        var bounds = ComputeBounds(strokes);
        var availableWidth = Math.Max(1f, visibleRect.Size.X - (settings.ViewPadding * 2f));
        var availableHeight = Math.Max(1f, visibleRect.Size.Y - (settings.ViewPadding * 2f));

        var drawings = mapScreen.Drawings;
        var drawingsInv = drawings.GetGlobalTransformWithCanvas().Inverse();

        var probeCenter = visibleRect.GetCenter();
        var probeRight = probeCenter + new Vector2(100, 0);
        var probeDown = probeCenter + new Vector2(0, 100);
        var dlC = drawingsInv * probeCenter;
        var dlR = drawingsInv * probeRight;
        var dlD = drawingsInv * probeDown;
        var dFinalX = Math.Abs(dlR.X - dlC.X);
        var dFinalY = Math.Abs(dlD.Y - dlC.Y);
        var yStretch = dFinalX > 1e-4f && dFinalY > 1e-4f ? dFinalY / dFinalX : 1f;

        var fitScale = Math.Min(
            availableWidth / Math.Max(1f, bounds.Size.X),
            (availableHeight * yStretch) / Math.Max(1f, bounds.Size.Y));

        var drawScale = Math.Clamp(settings.DrawScale, 0.1f, 5f);
        var sx = fitScale * drawScale;
        var sy = fitScale * drawScale / yStretch;

        var contentSize = new Vector2(bounds.Size.X * sx, bounds.Size.Y * sy);
        var offset = visibleRect.Position
                     + ((visibleRect.Size - contentSize) * 0.5f)
                     - new Vector2(bounds.Position.X * sx, bounds.Position.Y * sy)
                     + new Vector2(settings.OffsetX, settings.OffsetY);

        foreach (var stroke in strokes)
        {
            yield return stroke
                .Select(point => offset + new Vector2(point.X * sx, point.Y * sy))
                .Select(screenPos => ScreenToDrawingsNet(drawings, drawingsInv, screenPos))
                .ToArray();
        }
    }

    private static Vector2 ScreenToDrawingsNet(NMapDrawings drawings, Transform2D drawingsInv, Vector2 screenPos)
    {
        var local = drawingsInv * screenPos;
        // SubViewport is exactly half the size of NMapDrawings (960x1620 vs 1920x3240).
        // Game code uses `position * 0.5f` when adding Line2D points, and
        // ToNetPosition/FromNetPosition operate in this half-size SubViewport space.
        local *= 0.5f;
        local.X -= drawings.Size.X * 0.5f;
        local /= new Vector2(960f, drawings.Size.Y);
        return local;
    }

    private static Rect2 ComputeBounds(IReadOnlyList<Vector2[]> strokes)
    {
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

        foreach (var stroke in strokes)
        {
            foreach (var point in stroke)
            {
                min = min.Min(point);
                max = max.Max(point);
            }
        }

        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y))
        {
            return new Rect2(Vector2.Zero, Vector2.One);
        }

        return new Rect2(min, (max - min).Max(Vector2.One));
    }
}
