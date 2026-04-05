using System.IO;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace cielo.Scripts;

internal sealed class MapPaintSettings
{
    public string ImagePath { get; set; } = string.Empty;
    public string SelectedImageName { get; set; } = string.Empty;
    public int MaxImageSize { get; set; } = 1536;
    public string EdgeMode { get; set; } = "auto";
    /// <summary>
    /// 线条提取算法：<c>skeleton</c> = XDoG/阈值 + 细化 + 骨架跟踪；<c>canny</c> = Canny 边缘 + 跟踪 + 近邻排序（接近杀戮尖塔自动绘图脚本）。
    /// </summary>
    public string ExtractAlgorithm { get; set; } = "canny";
    public float DarkThreshold { get; set; } = 0.80f;
    public float PreBlur { get; set; } = 1.2f;
    /// <summary>Canny 专用：预模糊后对灰度做线性对比（1=不变）。参考 auto-painter 线稿的对比度拉伸，略大于 1 可让弱边更明显。</summary>
    public float CannyContrast { get; set; } = 1.30f;
    /// <summary>近邻排序后，若上一笔终点与下一笔起点距离不超过此像素则合并为一笔（参考 auto-painter join_dist）。0 表示不合并。</summary>
    public int StrokeJoinPixels { get; set; } = 3;
    /// <summary>
    /// 二值线稿在细化/跟踪前的形态学闭运算次数（0～2）。0 关闭；1～2 可弥合小断口，略增线宽。
    /// </summary>
    public int MorphCloseIterations { get; set; } = 1;
    public int MinStrokeLength { get; set; } = 2;
    public int MaxStrokes { get; set; } = 5000;
    public float SimplifyTolerance { get; set; } = 0.5f;
    public int SmoothSubdivisions { get; set; } = 8;
    public float ViewPadding { get; set; } = 80f;
    public int PointsPerFrame { get; set; } = 24;
    public float DrawScale { get; set; } = 0.8f;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float BlurSigma { get; set; } = 0.5f;
    public bool ContrastEnhance { get; set; } = true;
    public float XDoGSigmaRatio { get; set; } = 1.6f;
    public float XDoGTau { get; set; } = 0.99f;
    public float XDoGEpsilon { get; set; } = -0.1f;
    public float XDoGPhi { get; set; } = 200f;
    public float WindowX { get; set; } = float.NaN;
    public float WindowY { get; set; } = float.NaN;
    public float WindowWidth { get; set; } = float.NaN;
    /// <summary>地图绘制面板是否展开「高级设置」区块。</summary>
    public bool ShowAdvancedPanel { get; set; }

    public static string ConfigPath =>
        Path.Combine(GetModDirectoryPath(), "config", "map_paint.settings.txt");

    public static MapPaintSettings Load()
    {
        var path = ConfigPath;
        TryMigrateLegacyConfig(path);
        if (!File.Exists(path))
        {
            Log.Debug($"MapPaintSettings: config not found at {path}.");
            return new MapPaintSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<MapPaintSettings>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            return settings ?? new MapPaintSettings();
        }
        catch (Exception ex)
        {
            Log.Debug($"MapPaintSettings: failed to load config from {path}: {ex}");
            return new MapPaintSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(
                this,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Log.Debug($"MapPaintSettings: failed to save config to {ConfigPath}: {ex}");
        }
    }

    public string ResolveImagePath()
    {
        return MapImportLibrary.ResolveSelectedImage(this) ?? string.Empty;
    }

    public string ResolveConfiguredImagePath()
    {
        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(ImagePath)
            ? ImagePath
            : Path.GetFullPath(Path.Combine(GetModDirectoryPath(), ImagePath));
    }

    public static string GetModDirectoryPath()
    {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
    }

    private static void TryMigrateLegacyConfig(string newPath)
    {
        foreach (var legacyPath in new[]
        {
            Path.Combine(GetModDirectoryPath(), "map_paint.settings.json"),
            Path.Combine(GetModDirectoryPath(), "config", "map_paint.settings.json"),
        })
        {
            if (!File.Exists(legacyPath) || File.Exists(newPath))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                File.Move(legacyPath, newPath);
            }
            catch (Exception ex)
            {
                Log.Debug($"MapPaintSettings: failed to migrate legacy config from {legacyPath} to {newPath}: {ex}");
            }
        }
    }
}
