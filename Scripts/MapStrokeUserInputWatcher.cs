using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

/// <summary>
/// 自动绘制进行中若检测到「明确的」玩家指针意图，则保存进度并停止注入。
/// PC：硬件光标由 <see cref="MapStrokePlaybackNode"/> 与注入点对齐；此处用阈值、两次明显移动与按住防抖减少误触。
/// </summary>
internal static class MapStrokeUserInputWatcher
{
    private const string NodeName = "MapStrokeUserInputWatcher";

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

        tree.Root.CallDeferred(Node.MethodName.AddChild, new MapStrokeUserInputWatcherNode { Name = NodeName });
    }
}

internal partial class MapStrokeUserInputWatcherNode : Node
{
    private readonly ulong?[] _mouseButtonDownSince = new ulong?[32];
    private ulong _debouncedPauseAt;
    private int _bigMotionCount;
    private ulong _firstBigMotionTick;
    private ulong _touch0DownSince;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetProcessInput(true);
        SetProcess(true);
        if (GetParent() is { } p)
        {
            p.MoveChild(this, 0);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!MapStrokeInputPlayback.IsActive)
        {
            ResetStateIfIdle();
            return;
        }

        var s = MapPaintSettings.Load();
        if (s.PlaybackStopOnlyWithHotkey)
        {
            return;
        }

        var now = Time.GetTicksMsec();
        if (!AfterGrace(s, now))
        {
            return;
        }

        if (MapImportLibrary.IsMobile)
        {
            HandleMobileInput(@event, s, now);
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton mb:
                TrackMouseButton(mb, now);
                break;
            case InputEventMouseMotion mm:
                ConsiderBigMotion(mm.Relative.Length(), s, now);
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (!MapStrokeInputPlayback.IsActive)
        {
            ResetStateIfIdle();
            return;
        }

        var s = MapPaintSettings.Load();
        if (s.PlaybackStopOnlyWithHotkey)
        {
            return;
        }

        var now = Time.GetTicksMsec();
        if (!AfterGrace(s, now))
        {
            return;
        }

        if (_debouncedPauseAt > 0 && now >= _debouncedPauseAt)
        {
            PausePlaybackDueToUserPointer(
                $"检测到明显指针移动（{s.PlaybackPauseMotionConfirmCount} 次确认+防抖），已暂停自动绘制并保存进度。");
            return;
        }

        if (!MapImportLibrary.IsMobile)
        {
            for (var i = 0; i < _mouseButtonDownSince.Length; i++)
            {
                if (i == (int)MouseButton.Left)
                {
                    continue;
                }

                if (_mouseButtonDownSince[i] is ulong t && now - t >= (ulong)s.PlaybackPauseButtonHoldMs)
                {
                    PausePlaybackDueToUserPointer("检测到非左键长按，已暂停自动绘制并保存进度。");
                    return;
                }
            }
        }
        else if (_touch0DownSince > 0 && now - _touch0DownSince >= (ulong)s.PlaybackPauseButtonHoldMs)
        {
            PausePlaybackDueToUserPointer("检测到触摸长按，已暂停自动绘制并保存进度。");
        }
    }

    private static bool AfterGrace(MapPaintSettings s, ulong now)
    {
        var start = MapStrokeInputPlayback.PlaybackStartTicks;
        return start == 0 || now - start >= (ulong)s.PlaybackPauseGraceMs;
    }

    private void HandleMobileInput(InputEvent @event, MapPaintSettings s, ulong now)
    {
        switch (@event)
        {
            case InputEventScreenTouch t when t.Index == 0:
                if (t.Pressed)
                {
                    _touch0DownSince = now;
                }
                else
                {
                    _touch0DownSince = 0;
                }

                break;
            case InputEventScreenDrag d:
                ConsiderBigMotion(d.Relative.Length(), s, now);
                break;
        }
    }

    private void TrackMouseButton(InputEventMouseButton mb, ulong now)
    {
        var idx = (int)mb.ButtonIndex;
        if (idx < 0 || idx >= _mouseButtonDownSince.Length)
        {
            return;
        }

        if (mb.Pressed)
        {
            _mouseButtonDownSince[idx] = now;
        }
        else
        {
            _mouseButtonDownSince[idx] = null;
        }
    }

    private void ConsiderBigMotion(float relativeLength, MapPaintSettings s, ulong now)
    {
        if (relativeLength < s.PlaybackPauseMotionThresholdPx)
        {
            return;
        }

        if (_bigMotionCount == 0 || now - _firstBigMotionTick > (ulong)s.PlaybackPauseMotionWindowMs)
        {
            _bigMotionCount = 1;
            _firstBigMotionTick = now;
            return;
        }

        _bigMotionCount++;
        var need = Mathf.Max(2, s.PlaybackPauseMotionConfirmCount);
        if (_bigMotionCount >= need)
        {
            _debouncedPauseAt = now + (ulong)s.PlaybackPauseDebounceMs;
            _bigMotionCount = 0;
        }
    }

    private void ResetStateIfIdle()
    {
        if (MapStrokeInputPlayback.IsActive)
        {
            return;
        }

        _debouncedPauseAt = 0;
        _bigMotionCount = 0;
        _firstBigMotionTick = 0;
        _touch0DownSince = 0;
        for (var i = 0; i < _mouseButtonDownSince.Length; i++)
        {
            _mouseButtonDownSince[i] = null;
        }
    }

    private static void PausePlaybackDueToUserPointer(string status)
    {
        var r = MapStrokeInputPlayback.TryStop(saveProgress: true);
        var msg = r.Success ? status : $"{status} ({r.Message})";
        MapImportPanel.TryRefresh(NMapScreen.Instance, msg);
    }
}
