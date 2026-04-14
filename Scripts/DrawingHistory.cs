using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace cielo.Scripts;

/// <summary>已完成绘制的图片记录，包含 net-space 笔画数据，支持选择性移除与持久化。</summary>
internal sealed class DrawnImage
{
    public string ImagePath { get; set; } = "";
    public List<List<Vector2>> NetSpaceStrokes { get; set; } = [];
}

internal static class DrawingHistory
{
    private static readonly string FilePath =
        Path.Combine(MapPaintSettings.GetModDirectoryPath(), "config", "map_paint.history.json");

    private static List<DrawnImage> _items = [];

    public static int Count => _items.Count;

    public static IReadOnlyList<DrawnImage> Items => _items;

    public static bool Contains(string imagePath)
    {
        return _items.Any(d => string.Equals(d.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase));
    }

    public static void Load()
    {
        _items = [];
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<List<DrawnImage>>(json, ReadOptions);
            if (parsed is not null)
            {
                _items = parsed;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"DrawingHistory: load failed: {ex.Message}");
            _items = [];
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_items, WriteOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Debug($"DrawingHistory: save failed: {ex.Message}");
        }
    }

    /// <summary>添加一张完成的绘制记录（自然画完后调用）。如果同名图片已存在则先移除旧的。</summary>
    public static void Add(string imagePath, List<Vector2[]> netSpaceStrokes)
    {
        _items.RemoveAll(d => string.Equals(d.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase));
        _items.Add(new DrawnImage
        {
            ImagePath = imagePath,
            NetSpaceStrokes = netSpaceStrokes.Select(s => s.ToList()).ToList(),
        });
        Save();
    }

    /// <summary>移除指定图片，返回剩余所有 strokes 合并列表（net-space）。如果不存在返回 null。</summary>
    public static List<Vector2[]>? Remove(string imagePath)
    {
        var removed = _items.RemoveAll(d => string.Equals(d.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return null;
        }

        Save();
        return GetAllStrokes();
    }

    /// <summary>清空历史，返回清空前所有 strokes 合并列表。</summary>
    public static List<Vector2[]> ClearAndGetAllStrokes()
    {
        var all = GetAllStrokes();
        _items.Clear();
        Save();
        return all;
    }

    /// <summary>清空历史，不做其他操作。</summary>
    public static void Clear()
    {
        _items.Clear();
        Save();
    }

    /// <summary>获取历史中所有图片的 net-space strokes 合并列表。</summary>
    public static List<Vector2[]> GetAllStrokes()
    {
        var result = new List<Vector2[]>();
        foreach (var item in _items)
        {
            foreach (var stroke in item.NetSpaceStrokes)
            {
                if (stroke.Count >= 2)
                {
                    result.Add(stroke.ToArray());
                }
            }
        }

        return result;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };
}
