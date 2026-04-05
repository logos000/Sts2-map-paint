using System;
using System.IO;
using System.Linq;
using Godot;

namespace cielo.Scripts;

internal static class MapImportLibrary
{
    public static readonly string[] SupportedExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
    ];

    private static string[]? _cachedImages;
    private static string? _importFolder;

    public static bool IsMobile => OS.GetName() is "Android" or "iOS";

    public static string GetImportFolder()
    {
        if (_importFolder is not null)
            return _importFolder;

        if (IsMobile)
        {
            _importFolder = ResolveMobileImportFolder();
        }
        else
        {
            string pictures;
            try
            {
                pictures = OS.GetSystemDir(OS.SystemDir.Pictures);
            }
            catch
            {
                pictures = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            }

            if (string.IsNullOrEmpty(pictures))
                pictures = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Pictures");

            _importFolder = Path.Combine(pictures, "MapPaint");
        }

        return _importFolder;
    }

    private static string ResolveMobileImportFolder()
    {
        var privateDir = Path.Combine(OS.GetUserDataDir(), "MapPaint");

        foreach (var candidate in GetMobileCandidateFolders())
        {
            if (string.IsNullOrEmpty(candidate) || candidate == privateDir)
                continue;

            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                ModLog.Info($"MapImportLibrary: using public folder {candidate}");
                MigrateFromPrivate(privateDir, candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                ModLog.Info($"MapImportLibrary: {candidate} not writable: {ex.Message}");
            }
        }

        ModLog.Info($"MapImportLibrary: using private folder {privateDir}");
        return privateDir;
    }

    private static string?[] GetMobileCandidateFolders()
    {
        string? pictures = null;
        try { pictures = OS.GetSystemDir(OS.SystemDir.Pictures); } catch { }

        return
        [
            GetExternalDataFolder(),
            string.IsNullOrEmpty(pictures) ? null : Path.Combine(pictures, "MapPaint"),
            "/sdcard/Pictures/MapPaint",
            "/storage/emulated/0/Pictures/MapPaint",
        ];
    }

    private static string? GetExternalDataFolder()
    {
        try
        {
            var internalDir = OS.GetUserDataDir();
            var idx = internalDir.IndexOf("/data/", StringComparison.Ordinal);
            if (idx < 0) return null;
            var afterData = internalDir[(idx + 6)..];
            var slash = afterData.IndexOf('/');
            if (slash < 0) return null;
            var rest = afterData[(slash + 1)..];
            var pkgEnd = rest.IndexOf('/');
            var package = pkgEnd >= 0 ? rest[..pkgEnd] : rest;
            if (string.IsNullOrEmpty(package)) return null;
            return $"/storage/emulated/0/Android/data/{package}/files/MapPaint";
        }
        catch
        {
            return null;
        }
    }

    private static void MigrateFromPrivate(string oldDir, string newDir)
    {
        if (!Directory.Exists(oldDir)) return;
        try
        {
            foreach (var src in Directory.GetFiles(oldDir))
            {
                if (!Array.Exists(SupportedExtensions,
                        ext => src.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var dst = Path.Combine(newDir, Path.GetFileName(src));
                if (File.Exists(dst)) continue;
                File.Copy(src, dst);
                ModLog.Info($"MapImportLibrary: migrated {Path.GetFileName(src)} to public folder");
            }
        }
        catch (Exception ex)
        {
            ModLog.Info($"MapImportLibrary: migration failed: {ex.Message}");
        }
    }

    private static readonly string[] BundledImages = ["cielo.png"];

    public static void EnsureImportFolder()
    {
        var folder = GetImportFolder();
        try
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            ModLog.Info($"MapImportLibrary: import folder ready at {folder}");
        }
        catch (Exception ex)
        {
            ModLog.Info($"MapImportLibrary: failed to create import folder {folder}: {ex.Message}");
            return;
        }

        CopyBundledImages(folder);
    }

    private static void CopyBundledImages(string targetFolder)
    {
        var modDir = MapPaintSettings.GetModDirectoryPath();
        var pictureDir = Path.Combine(modDir, "config");

        foreach (var name in BundledImages)
        {
            var src = Path.Combine(pictureDir, name);
            var dst = Path.Combine(targetFolder, name);

            if (File.Exists(dst) || !File.Exists(src))
                continue;

            try
            {
                File.Copy(src, dst);
                ModLog.Info($"MapImportLibrary: copied bundled image {name} to {targetFolder}");
            }
            catch (Exception ex)
            {
                ModLog.Info($"MapImportLibrary: failed to copy {name}: {ex.Message}");
            }
        }
    }

    public static string[] GetAvailableImages()
    {
        var folder = GetImportFolder();
        if (!Directory.Exists(folder))
        {
            _cachedImages = [];
            return [];
        }

        try
        {
            var files = Directory.GetFiles(folder)
                .Where(f => Array.Exists(SupportedExtensions,
                    ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _cachedImages = files;
            return files;
        }
        catch (Exception ex)
        {
            ModLog.Info($"MapImportLibrary: failed to scan folder: {ex.Message}");
            _cachedImages = [];
            return [];
        }
    }

    public static string[] CachedImages => _cachedImages ?? GetAvailableImages();

    public static void RefreshCache() => _cachedImages = null;

    public static string? ResolveSelectedImage(MapPaintSettings settings)
    {
        var path = settings.ResolveConfiguredImagePath();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    public static int FindCurrentIndex(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return -1;
        var images = CachedImages;
        for (var i = 0; i < images.Length; i++)
        {
            if (string.Equals(images[i], currentPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public static string? CycleSelection(string? currentPath, int delta)
    {
        var images = CachedImages;
        if (images.Length == 0) return null;

        var idx = FindCurrentIndex(currentPath);
        if (idx < 0)
            idx = delta > 0 ? 0 : images.Length - 1;
        else
            idx = ((idx + delta) % images.Length + images.Length) % images.Length;

        return images[idx];
    }
}
