using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

/// <summary>模拟画笔绘制的断点进度（与当前图片 + 参数 + 视口绑定）。</summary>
internal sealed class MapStrokePlaybackProgress
{
    /// <summary>由 <see cref="ComputeFingerprint"/> 生成，用于判断是否可以续画。</summary>
    public string Fingerprint { get; set; } = "";

    public int StrokeIndex { get; set; }
    public int PointIndex { get; set; }
    public bool PenDown { get; set; }
    public int TotalStrokes { get; set; }

    public static string FilePath =>
        Path.Combine(MapPaintSettings.GetModDirectoryPath(), "config", "map_paint.playback.json");

    public static string ComputeFingerprint(NMapScreen mapScreen, MapPaintSettings settings, string resolvedImagePath)
    {
        var vr = mapScreen.GetViewportRect().Size;
        static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return string.Join('|', new[]
        {
            resolvedImagePath,
            settings.MaxImageSize.ToString(),
            settings.ExtractAlgorithm,
            F(settings.DarkThreshold),
            F(settings.PreBlur),
            F(settings.CannyContrast),
            settings.StrokeJoinPixels.ToString(),
            settings.MorphCloseIterations.ToString(),
            settings.MinStrokeLength.ToString(),
            settings.MaxStrokes.ToString(),
            F(settings.SimplifyTolerance),
            settings.SmoothSubdivisions.ToString(),
            F(settings.ViewPadding),
            F(settings.DrawScale),
            F(settings.OffsetX),
            F(settings.OffsetY),
            F(settings.BlurSigma),
            settings.ContrastEnhance ? "1" : "0",
            F(settings.XDoGSigmaRatio),
            F(settings.XDoGTau),
            F(settings.XDoGEpsilon),
            F(settings.XDoGPhi),
            settings.EdgeMode,
            F(vr.X),
            F(vr.Y),
        });
    }

    public static MapStrokePlaybackProgress? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<MapStrokePlaybackProgress>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Log.Debug($"MapStrokePlaybackProgress: load failed: {ex.Message}");
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Debug($"MapStrokePlaybackProgress: save failed: {ex.Message}");
        }
    }

    public static void DeleteFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"MapStrokePlaybackProgress: delete failed: {ex.Message}");
        }
    }

    /// <summary>在已知笔画列表下校验并钳位索引。</summary>
    public void ClampToStrokes(IReadOnlyList<Vector2[]> strokes)
    {
        if (strokes.Count == 0)
        {
            StrokeIndex = 0;
            PointIndex = 0;
            PenDown = false;
            return;
        }

        StrokeIndex = Math.Clamp(StrokeIndex, 0, strokes.Count - 1);
        var len = strokes[StrokeIndex].Length;
        if (len < 2)
        {
            PointIndex = 0;
            PenDown = false;
            return;
        }

        PointIndex = Math.Clamp(PointIndex, 0, len);
    }
}
