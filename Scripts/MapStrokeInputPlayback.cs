using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

/// <summary>
/// 直接调用 <see cref="NMapDrawings"/> 的 Local API 逐帧推进笔画，
/// 内部自动发联机消息，无需模拟鼠标/触摸事件。
/// 支持开始/停止与进度文件续画（<c>config/map_paint.playback.json</c>）。
/// </summary>
internal static class MapStrokeInputPlayback
{
    public static bool IsActive => ActiveNode is not null && GodotObject.IsInstanceValid(ActiveNode);

    /// <summary>最近一次开始播放的时间戳（<see cref="Godot.Time.GetTicksMsec"/>），用于暂停检测宽限期。</summary>
    public static ulong PlaybackStartTicks { get; private set; }

    /// <summary>上一段播放是否自然画完（非手动停止、非异常）。下次 <see cref="TryStart"/> 时清零。</summary>
    public static bool LastSessionEndedNaturally { get; private set; }

    /// <summary>上一段是否由 F5/停止按钮结束。下次 <see cref="TryStart"/> 时清零。</summary>
    public static bool LastSessionStoppedManually { get; private set; }

    /// <summary>上一段是否因地图不可用等异常结束。下次 <see cref="TryStart"/> 时清零。</summary>
    public static bool LastSessionAborted { get; private set; }

    internal static event Action? PlaybackFinished;

    internal static MapStrokePlaybackNode? ActiveNode { get; private set; }

    internal static void NotifyPlaybackFinishedNaturally()
    {
        LastSessionEndedNaturally = true;
        LastSessionStoppedManually = false;
        LastSessionAborted = false;
    }

    internal static void NotifyPlaybackAborted()
    {
        LastSessionEndedNaturally = false;
        LastSessionStoppedManually = false;
        LastSessionAborted = true;
    }

    private static void ResetSessionOutcomeFlags()
    {
        LastSessionEndedNaturally = false;
        LastSessionStoppedManually = false;
        LastSessionAborted = false;
    }

    /// <summary>按笔画总长估算的完成百分比（绘制中）；未在绘制时为 0。</summary>
    public static int GetApproximateProgressPercent()
    {
        if (!IsActive || ActiveNode is null)
        {
            return 0;
        }

        var f = ActiveNode.GetApproximateProgressFraction();
        return Mathf.Clamp(Mathf.RoundToInt(f * 100f), 0, 100);
    }

    internal static void ClearActiveNode()
    {
        ActiveNode = null;
        PlaybackStartTicks = 0;
        PlaybackFinished?.Invoke();
    }

    /// <summary>开始绘制：若存在与当前图片/参数/视口匹配的进度文件，则从断点续画且不清空画布。</summary>
    public static MapAutoPaintResult TryStart(NMapScreen? mapScreen)
    {
        if (mapScreen is null)
        {
            return new MapAutoPaintResult(false, "地图界面不可用。");
        }

        if (IsActive)
        {
            return new MapAutoPaintResult(false, "请先停止当前绘制。");
        }

        var settings = MapPaintSettings.Load();
        var imagePath = settings.ResolveImagePath();
        if (string.IsNullOrWhiteSpace(imagePath) || !Godot.FileAccess.FileExists(imagePath))
        {
            Log.Debug($"MapStrokeInputPlayback: missing image: {imagePath}");
            return new MapAutoPaintResult(false, "未选择图片，请将图片放入图库文件夹后用翻页选择。");
        }

        var raw = ImageStrokeExtractor.Extract(imagePath, settings);
        if (raw.Count == 0)
        {
            return new MapAutoPaintResult(false, "未能从图片中提取到可绘制的笔画。");
        }

        var netStrokes = MapAutoPainter.MapStrokesToNetSpace(mapScreen, raw, settings);
        netStrokes.RemoveAll(s => s.Length < 2);
        if (netStrokes.Count == 0)
        {
            return new MapAutoPaintResult(false, "没有可用的笔画（每笔至少 2 个点）。");
        }

        var fingerprint = MapStrokePlaybackProgress.ComputeFingerprint(mapScreen, settings, imagePath);
        var saved = MapStrokePlaybackProgress.TryLoad();

        var canResume = false;
        if (saved is not null
            && saved.Fingerprint == fingerprint
            && saved.TotalStrokes == netStrokes.Count)
        {
            saved.ClampToStrokes(netStrokes);
            canResume = saved.StrokeIndex < netStrokes.Count;
        }

        if (!canResume)
        {
            // 与 NMapDrawings.ClearDrawnLinesLocal 一致：清本地玩家笔迹并通知联机，而非 ClearAllLines（会误清所有玩家且不同步）。
            mapScreen.Drawings.ClearDrawnLinesLocal();
            MapStrokePlaybackProgress.DeleteFile();
        }

        ResetSessionOutcomeFlags();

        var node = new MapStrokePlaybackNode();
        node.Start(
            mapScreen,
            netStrokes,
            settings,
            fingerprint,
            canResume ? saved : null);
        mapScreen.AddChild(node);
        ActiveNode = node;
        PlaybackStartTicks = Time.GetTicksMsec();

        var msg = canResume && saved is not null
            ? $"从断点继续绘制（约 {netStrokes.Count} 笔，当前第 {saved.StrokeIndex + 1} 笔起）…"
            : $"开始绘制（约 {netStrokes.Count} 笔），联机队友将实时可见。请稍候…";

        Log.Debug($"MapStrokeInputPlayback: started, resume={canResume}, strokes={netStrokes.Count}");
        return new MapAutoPaintResult(true, msg);
    }

    /// <summary>停止绘制；默认保存进度以便下次「开始绘制」续画。</summary>
    public static MapAutoPaintResult TryStop(bool saveProgress = true)
    {
        if (!IsActive)
        {
            return new MapAutoPaintResult(false, "当前未在绘制。");
        }

        LastSessionStoppedManually = true;
        LastSessionEndedNaturally = false;
        LastSessionAborted = false;
        ActiveNode!.RequestStop(saveProgress);
        return new MapAutoPaintResult(
            true,
            saveProgress ? "已停止，进度已保存。下次点「开始绘制」可续画。" : "已停止，未保存进度。");
    }

    /// <summary>绘制过程中随时将进度写入磁盘（不停止）。</summary>
    public static MapAutoPaintResult TrySaveProgressSnapshot()
    {
        if (!IsActive)
        {
            return new MapAutoPaintResult(false, "当前没有在绘制，无法保存进度。");
        }

        var n = ActiveNode!;
        n.WriteProgressToDisk();
        return new MapAutoPaintResult(
            true,
            $"已保存进度：第 {n.StrokeIndex + 1}/{n.TotalStrokes} 笔附近。");
    }
}
