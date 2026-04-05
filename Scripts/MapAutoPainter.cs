using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.MapDrawing;

namespace cielo.Scripts;

internal readonly record struct MapAutoPaintResult(bool Success, string Message);

internal static class MapAutoPainter
{
    /// <summary>
    /// 直接 <see cref="NMapDrawings.LoadDrawings"/>，仅适合参数预览（滑块），联机队友通常看不到。
    /// </summary>
    public static MapAutoPaintResult TryApplyLocal(NMapScreen? mapScreen)
    {
        if (mapScreen is null)
        {
            return new MapAutoPaintResult(false, "地图界面不可用。");
        }

        var settings = MapPaintSettings.Load();

        var imagePath = settings.ResolveImagePath();
        if (string.IsNullOrWhiteSpace(imagePath) || !Godot.FileAccess.FileExists(imagePath))
        {
            Log.Debug($"MapAutoPainter: image path is empty or missing: {imagePath}");
            return new MapAutoPaintResult(
                false,
                "未选择图片，请将图片放入图库文件夹后用翻页选择。");
        }

        var strokes = ImageStrokeExtractor.Extract(imagePath, settings);
        if (strokes.Count == 0)
        {
            Log.Debug("MapAutoPainter: no strokes were extracted from the source image.");
            return new MapAutoPaintResult(false, "未能从图片中提取到可绘制的笔画。");
        }

        var drawings = BuildDrawings(mapScreen, strokes, settings);
        mapScreen.Drawings.ClearAllLines();
        mapScreen.Drawings.LoadDrawings(drawings);
        Log.Debug(
            $"MapAutoPainter: loaded {drawings.drawings.Sum(player => player.lines.Count)} lines " +
            $"into the official map drawing system from {imagePath}.");
        return new MapAutoPaintResult(
            true,
            $"已导入 {drawings.drawings.Sum(player => player.lines.Count)} 条线条，来自 {Path.GetFileName(imagePath)}。");
    }

    /// <summary>
    /// 通过模拟鼠标事件逐笔绘制（联机同步）。开始前会清空画布；期间会暂时隐藏本面板。
    /// </summary>
    public static bool TryBeginSimulatedPaint(
        NMapScreen? mapScreen,
        MapImportPanelLayer uiLayer,
        Action<MapAutoPaintResult> onComplete)
    {
        if (mapScreen is null)
        {
            onComplete(new MapAutoPaintResult(false, "地图界面不可用。"));
            return false;
        }

        var settings = MapPaintSettings.Load();
        var imagePath = settings.ResolveImagePath();
        if (string.IsNullOrWhiteSpace(imagePath) || !Godot.FileAccess.FileExists(imagePath))
        {
            Log.Debug($"MapAutoPainter: image path is empty or missing: {imagePath}");
            onComplete(new MapAutoPaintResult(
                false,
                "未选择图片，请将图片放入图库文件夹后用翻页选择。"));
            return false;
        }

        var strokes = ImageStrokeExtractor.Extract(imagePath, settings);
        if (strokes.Count == 0)
        {
            onComplete(new MapAutoPaintResult(false, "未能从图片中提取到可绘制的笔画。"));
            return false;
        }

        var polylines = MapToScreenPolylines(mapScreen, strokes, settings);
        if (polylines.Count == 0)
        {
            onComplete(new MapAutoPaintResult(false, "没有可绘制的线段。"));
            return false;
        }

        mapScreen.Drawings.ClearAllLines();
        uiLayer.Visible = false;
        var driver = new MapPaintInputDriver();
        uiLayer.AddChild(driver);
        driver.Start(
            mapScreen,
            polylines,
            settings,
            uiLayer,
            polylines.Count,
            onComplete);
        ModLog.Info($"MapAutoPainter: simulated paint started, {polylines.Count} polylines.");
        return true;
    }

    private static SerializableMapDrawings BuildDrawings(
        NMapScreen mapScreen,
        IReadOnlyList<Vector2[]> strokes,
        MapPaintSettings settings)
    {
        var netId = RunManager.Instance.NetService.NetId;
        var player = FindPlayer(mapScreen, netId);
        if (player is null)
        {
            throw new InvalidOperationException($"MapAutoPainter: could not resolve local player for net id {netId}.");
        }

        var mappedStrokes = MapToNetSpace(mapScreen, strokes, settings);
        var result = mapScreen.Drawings.GetSerializableMapDrawings();
        result.drawings.RemoveAll(drawing => drawing.playerId == player.NetId);

        var playerDrawings = new SerializablePlayerMapDrawings
        {
            playerId = player.NetId,
        };

        foreach (var stroke in mappedStrokes)
        {
            if (stroke.Length < 2)
            {
                continue;
            }

            playerDrawings.lines.Add(new SerializableMapDrawingLine
            {
                isEraser = false,
                mapPoints = stroke.ToList(),
            });
        }

        result.drawings.Add(playerDrawings);
        return result;
    }

    private static Player? FindPlayer(NMapScreen mapScreen, ulong netId)
    {
        var initializeField = typeof(NMapScreen).GetField("_runState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (initializeField?.GetValue(mapScreen) is IRunState runState)
        {
            return runState.Players.FirstOrDefault(player => player.NetId == netId);
        }

        return null;
    }

    /// <summary>地图控件本地坐标下的折线（与玩家鼠标同一套坐标系）。</summary>
    private static List<Vector2[]> MapToScreenPolylines(
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

        var list = new List<Vector2[]>();
        foreach (var stroke in strokes)
        {
            list.Add(stroke
                .Select(point => offset + new Vector2(point.X * sx, point.Y * sy))
                .ToArray());
        }

        return list;
    }

    private static IEnumerable<Vector2[]> MapToNetSpace(
        NMapScreen mapScreen,
        IReadOnlyList<Vector2[]> strokes,
        MapPaintSettings settings)
    {
        var screenPolylines = MapToScreenPolylines(mapScreen, strokes, settings);
        var drawings = mapScreen.Drawings;
        var drawingsInv = drawings.GetGlobalTransformWithCanvas().Inverse();

        foreach (var stroke in screenPolylines)
        {
            yield return stroke
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
