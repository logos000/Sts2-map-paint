using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

/// <summary>
/// 通过 <see cref="Viewport.PushInput"/> 注入鼠标按下/移动/抬起，走与真实画笔相同的输入路径以便联机同步。
/// </summary>
internal partial class MapPaintInputDriver : Node
{
    private NMapScreen? _map;
    private Viewport? _vp;
    private readonly List<Vector2[]> _strokes = new();
    private int _strokeIdx;
    private int _nextPointIndex;
    private bool _isDown;
    private MapPaintSettings _settings = null!;
    private Action<MapAutoPaintResult>? _onDone;
    private MapImportPanelLayer? _ui;
    private int _totalLines;

    public void Start(
        NMapScreen mapScreen,
        IReadOnlyList<Vector2[]> polylines,
        MapPaintSettings settings,
        MapImportPanelLayer uiLayer,
        int lineCount,
        Action<MapAutoPaintResult> onDone)
    {
        ProcessMode = ProcessModeEnum.Always;
        _map = mapScreen;
        _vp = mapScreen.GetViewport();
        _settings = settings;
        _ui = uiLayer;
        _totalLines = lineCount;
        _onDone = onDone;

        _strokes.Clear();
        foreach (var p in polylines)
        {
            if (p.Length >= 2)
                _strokes.Add(p);
            else if (p.Length == 1)
                _strokes.Add([p[0], p[0]]);
        }

        _strokeIdx = 0;
        _nextPointIndex = 1;
        _isDown = false;
    }

    public override void _Process(double delta)
    {
        if (_map is null || _vp is null || _onDone is null)
            return;

        if (!_map.IsInsideTree() || !IsInstanceValid(_map))
        {
            Finish(new MapAutoPaintResult(false, "绘制中断：地图界面已关闭。"));
            return;
        }

        int budget = Math.Max(1, _settings.PointsPerFrame);

        while (budget > 0)
        {
            if (_strokeIdx >= _strokes.Count)
            {
                Finish(new MapAutoPaintResult(
                    true,
                    $"已模拟绘制 {_totalLines} 条线（联机可见）。来源: {Path.GetFileName(_settings.ResolveImagePath())}"));
                return;
            }

            var s = _strokes[_strokeIdx];

            if (!_isDown)
            {
                PushButton(true, s[0]);
                _isDown = true;
                _nextPointIndex = 1;
                budget--;
                continue;
            }

            if (_nextPointIndex < s.Length)
            {
                var prev = s[_nextPointIndex - 1];
                var cur = s[_nextPointIndex];
                if (cur.DistanceTo(prev) > 0.25f)
                    PushMotion(cur);
                _nextPointIndex++;
                budget--;
                continue;
            }

            PushButton(false, s[^1]);
            _isDown = false;
            _strokeIdx++;
            budget--;
        }
    }

    private void PushButton(bool pressed, Vector2 mapLocal)
    {
        var g = MapLocalToCanvasGlobal(_map!, mapLocal);
        var e = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = pressed,
            GlobalPosition = g,
        };
        ApplyViewportPos(e, g);
        _vp!.PushInput(e);
    }

    private void PushMotion(Vector2 mapLocal)
    {
        var g = MapLocalToCanvasGlobal(_map!, mapLocal);
        var e = new InputEventMouseMotion
        {
            GlobalPosition = g,
            ButtonMask = MouseButtonMask.Left,
        };
        ApplyViewportPos(e, g);
        _vp!.PushInput(e);
    }

    private static Vector2 MapLocalToCanvasGlobal(NMapScreen map, Vector2 mapLocal)
    {
        if (map is CanvasItem ci)
            return ci.GetGlobalTransformWithCanvas() * mapLocal;
        return mapLocal;
    }

    private void ApplyViewportPos(InputEventMouseButton e, Vector2 globalCanvas)
    {
        try
        {
            e.Position = _vp!.GetCanvasTransform().AffineInverse() * globalCanvas;
        }
        catch
        {
            e.Position = globalCanvas;
        }
    }

    private void ApplyViewportPos(InputEventMouseMotion e, Vector2 globalCanvas)
    {
        try
        {
            e.Position = _vp!.GetCanvasTransform().AffineInverse() * globalCanvas;
        }
        catch
        {
            e.Position = globalCanvas;
        }
    }

    private void Finish(MapAutoPaintResult result)
    {
        var cb = _onDone;
        _onDone = null;
        _map = null;
        _vp = null;
        if (_ui is not null)
            _ui.Visible = true;
        cb?.Invoke(result);
        QueueFree();
    }
}
