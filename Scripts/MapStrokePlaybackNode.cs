using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

/// <summary>
/// 直接调用 <see cref="NMapDrawings"/> 的 Local API（BeginLineLocal / UpdateCurrentLinePositionLocal / StopLineLocal）
/// 按帧推进笔画。Local API 内部会发 <see cref="MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Flavor.MapDrawingMessage"/>，
/// 联机队友可实时看到笔迹，无需模拟鼠标/触摸事件。
/// </summary>
internal sealed partial class MapStrokePlaybackNode : Node
{
    private NMapDrawings? _drawings;
    private List<Vector2[]> _strokes = [];
    private MapPaintSettings _settings = null!;
    private string _fingerprint = "";
    private string _imagePath = "";
    private int _strokeIndex;
    private int _pointIndex;
    private bool _penDown;
    private float[] _strokePolylineLengths = [];
    private float _totalPolylineLength;

    public int StrokeIndex => _strokeIndex;
    public int TotalStrokes => _strokes.Count;

    /// <summary>按各笔折线长度加权估算 0～1，用于 UI 进度（非简单笔数比例）。</summary>
    public float GetApproximateProgressFraction()
    {
        if (_strokes.Count == 0)
        {
            return 1f;
        }

        if (_totalPolylineLength < 1e-5f)
        {
            if (_strokeIndex >= _strokes.Count)
            {
                return 1f;
            }

            return (_strokeIndex + (_pointIndex > 0 ? 0.5f : 0f)) / _strokes.Count;
        }

        float done = 0f;
        for (var i = 0; i < _strokeIndex && i < _strokes.Count; i++)
        {
            done += _strokePolylineLengths[i];
        }

        if (_strokeIndex < _strokes.Count)
        {
            var stroke = _strokes[_strokeIndex];
            if (stroke.Length >= 2)
            {
                var p = _pointIndex;
                if (p >= 2)
                {
                    var maxI = stroke.Length - 2;
                    var last = Math.Min(p - 2, maxI);
                    for (var i = 0; i <= last; i++)
                    {
                        done += stroke[i].DistanceTo(stroke[i + 1]);
                    }
                }
            }
        }

        return Mathf.Clamp(done / _totalPolylineLength, 0f, 1f);
    }

    public void Start(
        NMapScreen mapScreen,
        List<Vector2[]> strokes,
        MapPaintSettings settings,
        string fingerprint,
        MapStrokePlaybackProgress? resume,
        string imagePath)
    {
        _drawings = mapScreen.Drawings;
        _strokes = strokes;
        _settings = settings;
        _fingerprint = fingerprint;
        _imagePath = imagePath;

        if (resume is not null)
        {
            resume.ClampToStrokes(strokes);
            _strokeIndex = resume.StrokeIndex;
            _pointIndex = resume.PointIndex;
            _penDown = resume.PenDown;
        }
        else
        {
            _strokeIndex = 0;
            _pointIndex = 0;
            _penDown = false;
        }

        _strokePolylineLengths = new float[strokes.Count];
        _totalPolylineLength = 0f;
        for (var i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (s.Length < 2)
            {
                continue;
            }

            float len = 0f;
            for (var j = 1; j < s.Length; j++)
            {
                len += s[j - 1].DistanceTo(s[j]);
            }

            _strokePolylineLengths[i] = len;
            _totalPolylineLength += len;
        }

        ProcessMode = ProcessModeEnum.Always;
    }

    /// <summary>随时将当前索引写入磁盘（不停止绘制）。</summary>
    public void WriteProgressToDisk()
    {
        SaveProgressCore();
    }

    /// <summary>请求停止：抬笔并可选保存进度，然后释放节点。</summary>
    public void RequestStop(bool saveProgress)
    {
        SetProcess(false);
        if (saveProgress)
        {
            CompleteGracefulStop();
        }
        else
        {
            CompleteAbortWithoutSave();
        }
    }

    private void CompleteAbortWithoutSave()
    {
        ReleasePenIfNeeded();
        MapStrokePlaybackProgress.DeleteFile();
        MapStrokeInputPlayback.NotifyPlaybackAborted();
        Finish();
    }

    private void CompleteGracefulStop()
    {
        ReleasePenIfNeeded();
        SaveProgressCore();
        Finish();
    }

    private void ReleasePenIfNeeded()
    {
        if (_drawings is null || !_penDown)
        {
            _penDown = false;
            return;
        }

        try
        {
            _drawings.StopLineLocal();
        }
        catch (Exception ex)
        {
            Log.Debug($"MapStrokePlayback: StopLineLocal on release failed: {ex.Message}");
        }

        _penDown = false;
    }

    private void SaveProgressCore()
    {
        var p = new MapStrokePlaybackProgress
        {
            Fingerprint = _fingerprint,
            StrokeIndex = _strokeIndex,
            PointIndex = _pointIndex,
            PenDown = _penDown,
            TotalStrokes = _strokes.Count,
        };
        p.Save();
        Log.Debug(
            $"MapStrokePlayback: saved progress stroke={_strokeIndex}/{_strokes.Count} point={_pointIndex} penDown={_penDown}");
    }

    public override void _Process(double delta)
    {
        if (_drawings is null || !GodotObject.IsInstanceValid(_drawings))
        {
            CompleteNaturalOrAbandon();
            return;
        }

        var budget = Mathf.Max(1, _settings.PointsPerFrame);

        while (budget > 0 && _strokeIndex < _strokes.Count)
        {
            var stroke = _strokes[_strokeIndex];
            if (stroke.Length < 2)
            {
                _strokeIndex++;
                _penDown = false;
                _pointIndex = 0;
                continue;
            }

            if (!_penDown)
            {
                if (_pointIndex == 0)
                {
                    var local = NetToDrawingsLocal(stroke[0]);
                    _drawings.BeginLineLocal(local, DrawingMode.Drawing);
                    _penDown = true;
                    _pointIndex = 1;
                    budget--;
                    continue;
                }

                if (_pointIndex < stroke.Length)
                {
                    var local = NetToDrawingsLocal(stroke[_pointIndex]);
                    _drawings.BeginLineLocal(local, DrawingMode.Drawing);
                    _penDown = true;
                    _pointIndex++;
                    budget--;
                    continue;
                }

                _strokeIndex++;
                _pointIndex = 0;
                continue;
            }

            if (_pointIndex < stroke.Length)
            {
                var local = NetToDrawingsLocal(stroke[_pointIndex]);
                _drawings.UpdateCurrentLinePositionLocal(local);
                _pointIndex++;
                budget--;
                continue;
            }

            _drawings.StopLineLocal();
            _penDown = false;
            _strokeIndex++;
            _pointIndex = 0;
            budget--;
        }

        if (_strokeIndex >= _strokes.Count && !_penDown)
        {
            CompleteNaturalFinish();
        }
    }

    /// <summary>
    /// 网络坐标 → NMapDrawings 控件本地坐标（与真实输入 <c>drawings.GetGlobalTransform().Inverse() * globalPos</c> 等价）。
    /// 本体 <see cref="NMapDrawings"/> 内部 <c>FromNetPosition</c> 产生 SubViewport 空间坐标（半宽半高），
    /// 而 <c>BeginLineLocal</c> / <c>UpdateCurrentLinePositionLocal</c> 在添加 Line2D 点时执行 <c>position * 0.5f</c>，
    /// 因此需传入 SubViewport 坐标的两倍。
    /// </summary>
    private Vector2 NetToDrawingsLocal(Vector2 net)
    {
        var size = _drawings!.Size;
        var sub = net * new Vector2(960f, size.Y);
        sub.X += size.X * 0.5f;
        return sub * 2f;
    }

    private void CompleteNaturalFinish()
    {
        MapStrokePlaybackProgress.DeleteFile();
        if (!string.IsNullOrWhiteSpace(_imagePath))
        {
            DrawingHistory.Add(_imagePath, _strokes);
            Log.Debug($"DrawingHistory: recorded completed image {_imagePath} ({_strokes.Count} strokes).");
        }

        Log.Debug("MapStrokePlayback: natural finish, progress file cleared.");
        MapStrokeInputPlayback.NotifyPlaybackFinishedNaturally();
        Finish();
    }

    private void CompleteNaturalOrAbandon()
    {
        MapStrokePlaybackProgress.DeleteFile();
        MapStrokeInputPlayback.NotifyPlaybackAborted();
        Finish();
    }

    private void Finish()
    {
        if (GodotObject.IsInstanceValid(this))
        {
            QueueFree();
        }
    }

    public override void _ExitTree()
    {
        if (!ReferenceEquals(MapStrokeInputPlayback.ActiveNode, this))
        {
            return;
        }

        MapStrokeInputPlayback.ClearActiveNode();
        Log.Debug("MapStrokeInputPlayback: node exited.");
    }
}
