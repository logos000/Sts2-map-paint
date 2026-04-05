using System;
using System.IO;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;

namespace cielo.Scripts;

internal static class ModLog
{
    private static readonly object Sync = new();

    public static string LogPath =>
        Path.Combine(GetModDirectory(), "map_paint.log");

    public static void Info(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Log.Debug(line);

        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(GetModDirectory());
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    File.Delete(LogPath);
                }
            }
            catch
            {
            }
        }
    }

    private static string GetModDirectory()
    {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
    }
}
