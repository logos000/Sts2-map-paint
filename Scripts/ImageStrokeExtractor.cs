using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace cielo.Scripts;

internal static class ImageStrokeExtractor
{
    private static readonly (int X, int Y)[] Neighbors8 =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),           (1, 0),
        (-1, 1),  (0, 1),  (1, 1),
    ];

    public static IReadOnlyList<Vector2[]> Extract(string imagePath, MapPaintSettings settings)
    {
        var image = Image.LoadFromFile(imagePath);
        if (image is null)
        {
            Log.Debug($"ImageStrokeExtractor: failed to load image from {imagePath}.");
            return [];
        }

        var resized = ResizeIfNeeded(image, settings.MaxImageSize);
        var width = resized.GetWidth();
        var height = resized.GetHeight();
        var grayscale = ToGrayscale(resized);

        var useCanny = string.Equals(settings.ExtractAlgorithm, "canny", StringComparison.OrdinalIgnoreCase);
        Log.Debug($"ImageStrokeExtractor: algorithm = {(useCanny ? "canny" : "skeleton")}");

        bool[] edgeMask;
        if (useCanny)
        {
            var blurred = GaussianBlur(grayscale, width, height, settings.PreBlur);
            if (MathF.Abs(settings.CannyContrast - 1f) > 1e-4f)
            {
                ApplyLinearContrast(blurred, settings.CannyContrast);
            }

            edgeMask = CannyEdgeMask(blurred, width, height, settings.DarkThreshold);
        }
        else
        {
            var mode = ResolveEdgeMode(settings.EdgeMode, grayscale);
            Log.Debug($"ImageStrokeExtractor: edge mode = {mode}");

            if (mode == "threshold")
            {
                var blurred = GaussianBlur(grayscale, width, height, settings.PreBlur);
                edgeMask = ThresholdDark(blurred, settings.DarkThreshold);
            }
            else
            {
                var xdogInput = grayscale;
                if (settings.ContrastEnhance)
                {
                    xdogInput = HistogramEqualize(xdogInput);
                }

                // 与阈值模式一致：先轻度高斯平滑再算 XDoG，抑制均衡化带来的颗粒噪声（参考常见线稿预处理流程）。
                if (settings.PreBlur > 0f)
                {
                    xdogInput = GaussianBlur(xdogInput, width, height, settings.PreBlur);
                }

                var xdog = ApplyXDoG(xdogInput, width, height,
                    settings.BlurSigma, settings.XDoGSigmaRatio,
                    settings.XDoGTau, settings.XDoGEpsilon, settings.XDoGPhi);
                // 面板「边缘」阈值：固定 0.5 会忽略用户调节；低响应视为线条（与 ThresholdDark 语义一致：值越大越易保留弱线）。
                edgeMask = Binarize(xdog, settings.DarkThreshold);
            }

            if (settings.MorphCloseIterations > 0)
            {
                edgeMask = MorphologicalClose(edgeMask, width, height, settings.MorphCloseIterations);
            }

            edgeMask = ZhangSuenThin(edgeMask, width, height);
        }

        if (useCanny && settings.MorphCloseIterations > 0)
        {
            edgeMask = MorphologicalClose(edgeMask, width, height, settings.MorphCloseIterations);
        }

        var strokes = TraceStrokes(edgeMask, width, height, settings);
        ReorderStrokesNearestNeighbor(strokes);
        MergeStrokesByEndpointGap(strokes, settings.StrokeJoinPixels);

        if (settings.SmoothSubdivisions > 0)
        {
            for (var i = 0; i < strokes.Count; i++)
            {
                strokes[i] = CatmullRomSmooth(strokes[i], settings.SmoothSubdivisions);
            }
        }

        Log.Debug(
            $"ImageStrokeExtractor: extracted {strokes.Count} strokes from {imagePath} " +
            $"at {width}x{height}.");
        return strokes;
    }

    private static string ResolveEdgeMode(string mode, float[] grayscale)
    {
        if (mode is "xdog" or "threshold")
        {
            return mode;
        }

        var brightCount = 0;
        var darkCount = 0;
        var midCount = 0;
        foreach (var v in grayscale)
        {
            if (v > 0.75f) brightCount++;
            else if (v < 0.45f) darkCount++;
            else midCount++;
        }

        var total = (float)grayscale.Length;
        var brightRatio = brightCount / total;
        var midRatio = midCount / total;

        return brightRatio > 0.45f && darkCount > 0.01f * total && midRatio < 0.35f
            ? "threshold"
            : "xdog";
    }

    private static bool[] ThresholdDark(float[] grayscale, float threshold)
    {
        var mask = new bool[grayscale.Length];
        for (var i = 0; i < grayscale.Length; i++)
        {
            mask[i] = grayscale[i] < threshold;
        }

        return mask;
    }

    private static Vector2[] CatmullRomSmooth(Vector2[] points, int subdivisions)
    {
        if (subdivisions <= 0 || points.Length < 3)
        {
            return points;
        }

        var result = new List<Vector2>(points.Length * subdivisions);
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[Math.Min(points.Length - 1, i + 1)];
            var p3 = points[Math.Min(points.Length - 1, i + 2)];

            for (var j = 0; j < subdivisions; j++)
            {
                var t = j / (float)subdivisions;
                var t2 = t * t;
                var t3 = t2 * t;
                result.Add(0.5f * (
                    (2f * p1)
                    + ((-p0 + p2) * t)
                    + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2)
                    + ((-p0 + (3f * p1) - (3f * p2) + p3) * t3)));
            }
        }

        result.Add(points[^1]);
        return result.ToArray();
    }

    private static Image ResizeIfNeeded(Image image, int maxImageSize)
    {
        if (maxImageSize <= 0)
        {
            return image;
        }

        var width = image.GetWidth();
        var height = image.GetHeight();
        var largestSide = Math.Max(width, height);
        if (largestSide <= maxImageSize)
        {
            return image;
        }

        var scale = maxImageSize / (float)largestSide;
        var resized = (Image)image.Duplicate();
        resized.Resize(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
        return resized;
    }

    private static float[] ToGrayscale(Image image)
    {
        var width = image.GetWidth();
        var height = image.GetHeight();
        var output = new float[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = image.GetPixel(x, y);
                output[(y * width) + x] =
                    (0.299f * color.R) +
                    (0.587f * color.G) +
                    (0.114f * color.B);
            }
        }

        return output;
    }

    private static float[] HistogramEqualize(float[] grayscale)
    {
        const int bins = 256;
        var histogram = new int[bins];
        foreach (var value in grayscale)
        {
            histogram[Math.Clamp((int)(value * (bins - 1)), 0, bins - 1)]++;
        }

        var cdf = new float[bins];
        cdf[0] = histogram[0];
        for (var i = 1; i < bins; i++)
        {
            cdf[i] = cdf[i - 1] + histogram[i];
        }

        var cdfMin = 0f;
        for (var i = 0; i < bins; i++)
        {
            if (cdf[i] > 0)
            {
                cdfMin = cdf[i];
                break;
            }
        }

        var total = (float)grayscale.Length;
        var denominator = total - cdfMin;
        if (denominator <= 0f)
        {
            return grayscale;
        }

        var output = new float[grayscale.Length];
        for (var i = 0; i < grayscale.Length; i++)
        {
            var bin = Math.Clamp((int)(grayscale[i] * (bins - 1)), 0, bins - 1);
            output[i] = (cdf[bin] - cdfMin) / denominator;
        }

        return output;
    }

    private static float[] GaussianBlur(float[] source, int width, int height, float sigma)
    {
        if (sigma <= 0f)
        {
            return source;
        }

        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 3f));
        var kernelSize = (radius * 2) + 1;
        var kernel = new float[kernelSize];
        var kernelSum = 0f;
        for (var i = 0; i < kernelSize; i++)
        {
            var d = i - radius;
            kernel[i] = MathF.Exp(-(d * d) / (2f * sigma * sigma));
            kernelSum += kernel[i];
        }

        for (var i = 0; i < kernelSize; i++)
        {
            kernel[i] /= kernelSum;
        }

        var temp = new float[source.Length];
        var output = new float[source.Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    sum += source[(y * width) + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
                }

                temp[(y * width) + x] = sum;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    sum += temp[(Math.Clamp(y + k, 0, height - 1) * width) + x] * kernel[k + radius];
                }

                output[(y * width) + x] = sum;
            }
        }

        return output;
    }

    private static float[] ApplyXDoG(
        float[] grayscale, int width, int height,
        float sigma, float k, float tau, float epsilon, float phi)
    {
        var blurFine = GaussianBlur(grayscale, width, height, sigma);
        var blurCoarse = GaussianBlur(grayscale, width, height, sigma * k);
        var output = new float[grayscale.Length];

        for (var i = 0; i < grayscale.Length; i++)
        {
            var dog = blurFine[i] - (tau * blurCoarse[i]);
            output[i] = dog >= epsilon
                ? 1f
                : 1f + MathF.Tanh(phi * (dog - epsilon));
        }

        return output;
    }

    private static bool[] Binarize(float[] xdog, float threshold = 0.5f)
    {
        var mask = new bool[xdog.Length];
        for (var i = 0; i < xdog.Length; i++)
        {
            mask[i] = xdog[i] < threshold;
        }

        return mask;
    }

    /// <summary>
    /// Canny 边缘检测：Sobel → 梯度幅值 → 非极大值抑制 → 双阈值滞后（从强边沿 8 邻域 BFS，避免多轮全图扫描）。
    /// </summary>
    private static bool[] CannyEdgeMask(float[] grayscale, int width, int height, float darkThreshold)
    {
        var w = width;
        var h = height;
        var len = grayscale.Length;
        var gx = new float[len];
        var gy = new float[len];
        var mag = new float[len];

        for (var y = 1; y < h - 1; y++)
        {
            var row = y * w;
            for (var x = 1; x < w - 1; x++)
            {
                var i = row + x;
                float p00 = grayscale[i - w - 1] * 255f;
                float p01 = grayscale[i - w] * 255f;
                float p02 = grayscale[i - w + 1] * 255f;
                float p10 = grayscale[i - 1] * 255f;
                float p12 = grayscale[i + 1] * 255f;
                float p20 = grayscale[i + w - 1] * 255f;
                float p21 = grayscale[i + w] * 255f;
                float p22 = grayscale[i + w + 1] * 255f;

                var gxv = (-p00 + p02 - (2f * p10) + (2f * p12) - p20 + p22);
                var gyv = (-p00 - (2f * p01) - p02 + p20 + (2f * p21) + p22);
                gx[i] = gxv;
                gy[i] = gyv;
                mag[i] = MathF.Sqrt((gxv * gxv) + (gyv * gyv));
            }
        }

        var nms = new float[len];
        for (var y = 1; y < h - 1; y++)
        {
            var row = y * w;
            for (var x = 1; x < w - 1; x++)
            {
                var i = row + x;
                var angle = MathF.Atan2(gy[i], gx[i]) * (57.29578f);
                if (angle < 0f)
                {
                    angle += 180f;
                }

                var m = mag[i];
                float m1;
                float m2;
                if (angle < 22.5f || angle >= 157.5f)
                {
                    m1 = mag[i - 1];
                    m2 = mag[i + 1];
                }
                else if (angle < 67.5f)
                {
                    m1 = mag[i - w + 1];
                    m2 = mag[i + w - 1];
                }
                else if (angle < 112.5f)
                {
                    m1 = mag[i - w];
                    m2 = mag[i + w];
                }
                else
                {
                    m1 = mag[i - w - 1];
                    m2 = mag[i + w + 1];
                }

                nms[i] = m >= m1 && m >= m2 ? m : 0f;
            }
        }

        var t1 = 10f + ((1f - Math.Clamp(darkThreshold, 0.05f, 0.99f)) * 190f);
        t1 = Math.Clamp(t1, 10f, 200f);
        var low = t1;
        var high = t1 * 2f;

        var edge = new bool[len];
        var queue = new Queue<int>(256);
        for (var i = 0; i < len; i++)
        {
            if (nms[i] < high)
            {
                continue;
            }

            edge[i] = true;
            queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var xi = i % w;
            var yi = i / w;
            foreach (var (dx, dy) in Neighbors8)
            {
                var nx = xi + dx;
                var ny = yi + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                {
                    continue;
                }

                var j = (ny * w) + nx;
                if (nms[j] < low || edge[j])
                {
                    continue;
                }

                edge[j] = true;
                queue.Enqueue(j);
            }
        }

        return edge;
    }

    /// <summary>
    /// 按上一笔终点与下一段起点/终点的距离贪心排序，减少绘制时空移（参考 SlayTheSpire2AutoDrawing）。
    /// </summary>
    private static void ReorderStrokesNearestNeighbor(List<Vector2[]> strokes)
    {
        if (strokes.Count <= 1)
        {
            return;
        }

        strokes.Sort((a, b) => b.Length.CompareTo(a.Length));
        var ordered = new List<Vector2[]>(strokes.Count) { strokes[0] };
        strokes.RemoveAt(0);

        while (strokes.Count > 0)
        {
            var end = ordered[^1][^1];
            var bestIdx = 0;
            var bestD = float.MaxValue;
            var reverse = false;

            for (var i = 0; i < strokes.Count; i++)
            {
                var s = strokes[i];
                var d0 = end.DistanceSquaredTo(s[0]);
                var d1 = end.DistanceSquaredTo(s[^1]);
                if (d0 < bestD)
                {
                    bestD = d0;
                    bestIdx = i;
                    reverse = false;
                }

                if (d1 < bestD)
                {
                    bestD = d1;
                    bestIdx = i;
                    reverse = true;
                }
            }

            var pick = strokes[bestIdx];
            strokes.RemoveAt(bestIdx);
            if (reverse)
            {
                var rev = new Vector2[pick.Length];
                for (var j = 0; j < pick.Length; j++)
                {
                    rev[j] = pick[^(j + 1)];
                }

                pick = rev;
            }

            ordered.Add(pick);
        }

        strokes.Clear();
        strokes.AddRange(ordered);
    }

    /// <summary> Canny 输入：线性拉伸对比度（类似 auto-painter 线稿里的 contrast / scale_abs）。</summary>
    private static void ApplyLinearContrast(float[] buffer, float contrast)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = Math.Clamp(buffer[i] * contrast, 0f, 1f);
        }
    }

    /// <summary>
    /// 近邻排序后，若上一笔终点与下一笔起点间距不超过 <paramref name="maxGapPx"/> 则合并为一条（参考 auto-painter <c>reorder_and_merge_paths</c> 的 join）。
    /// </summary>
    private static void MergeStrokesByEndpointGap(List<Vector2[]> strokes, int maxGapPx)
    {
        if (maxGapPx <= 0 || strokes.Count <= 1)
        {
            return;
        }

        for (var i = strokes.Count - 1; i >= 0; i--)
        {
            if (strokes[i].Length == 0)
            {
                strokes.RemoveAt(i);
            }
        }

        if (strokes.Count <= 1)
        {
            return;
        }

        var gap2 = maxGapPx * maxGapPx;
        var merged = new List<Vector2[]>(strokes.Count);
        var cur = strokes[0];
        for (var i = 1; i < strokes.Count; i++)
        {
            var next = strokes[i];
            if (cur.Length >= 1 && next.Length >= 1 && cur[^1].DistanceSquaredTo(next[0]) <= gap2)
            {
                var combined = new Vector2[(cur.Length + next.Length) - 1];
                for (var j = 0; j < cur.Length; j++)
                {
                    combined[j] = cur[j];
                }

                for (var j = 1; j < next.Length; j++)
                {
                    combined[cur.Length + j - 1] = next[j];
                }

                cur = combined;
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }

        merged.Add(cur);
        strokes.Clear();
        strokes.AddRange(merged);
    }

    /// <summary>
    /// 3×3 二值闭运算（先膨胀再腐蚀），用于弥合细小断口；次数过多会使线条粘连。
    /// </summary>
    private static bool[] MorphologicalClose(bool[] mask, int width, int height, int iterations)
    {
        if (iterations <= 0)
        {
            return mask;
        }

        var current = mask;
        for (var k = 0; k < iterations; k++)
        {
            current = Dilate3x3(current, width, height);
            current = Erode3x3(current, width, height);
        }

        return current;
    }

    private static bool[] Dilate3x3(bool[] src, int w, int h)
    {
        var dst = new bool[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w) + x;
                var on = false;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var ny = y + dy;
                        var nx = x + dx;
                        if ((uint)ny >= (uint)h || (uint)nx >= (uint)w)
                        {
                            continue;
                        }

                        if (!src[(ny * w) + nx])
                        {
                            continue;
                        }

                        on = true;
                        goto DilateNextPixel;
                    }
                }

                DilateNextPixel:
                dst[i] = on;
            }
        }

        return dst;
    }

    private static bool[] Erode3x3(bool[] src, int w, int h)
    {
        var dst = new bool[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w) + x;
                var all = true;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var ny = y + dy;
                        var nx = x + dx;
                        if ((uint)ny >= (uint)h || (uint)nx >= (uint)w)
                        {
                            all = false;
                            goto ErodeSet;
                        }

                        if (!src[(ny * w) + nx])
                        {
                            all = false;
                            goto ErodeSet;
                        }
                    }
                }

                ErodeSet:
                dst[i] = all;
            }
        }

        return dst;
    }

    private static bool[] ZhangSuenThin(bool[] mask, int width, int height)
    {
        var current = (bool[])mask.Clone();
        var toRemove = new bool[mask.Length];
        bool changed;

        do
        {
            changed = false;

            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var i = (y * width) + x;
                    if (!current[i])
                    {
                        continue;
                    }

                    GetZhangSuenNeighbors(current, width, x, y,
                        out var p2, out var p3, out var p4, out var p5,
                        out var p6, out var p7, out var p8, out var p9);
                    var b = B(p2, p3, p4, p5, p6, p7, p8, p9);
                    if (b < 2 || b > 6)
                    {
                        continue;
                    }

                    if (A(p2, p3, p4, p5, p6, p7, p8, p9) != 1)
                    {
                        continue;
                    }

                    if (p2 && p4 && p6)
                    {
                        continue;
                    }

                    if (p4 && p6 && p8)
                    {
                        continue;
                    }

                    toRemove[i] = true;
                    changed = true;
                }
            }

            for (var i = 0; i < current.Length; i++)
            {
                if (toRemove[i])
                {
                    current[i] = false;
                }
            }

            Array.Clear(toRemove);

            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var i = (y * width) + x;
                    if (!current[i])
                    {
                        continue;
                    }

                    GetZhangSuenNeighbors(current, width, x, y,
                        out var p2, out var p3, out var p4, out var p5,
                        out var p6, out var p7, out var p8, out var p9);
                    var b = B(p2, p3, p4, p5, p6, p7, p8, p9);
                    if (b < 2 || b > 6)
                    {
                        continue;
                    }

                    if (A(p2, p3, p4, p5, p6, p7, p8, p9) != 1)
                    {
                        continue;
                    }

                    if (p2 && p4 && p8)
                    {
                        continue;
                    }

                    if (p2 && p6 && p8)
                    {
                        continue;
                    }

                    toRemove[i] = true;
                    changed = true;
                }
            }

            for (var i = 0; i < current.Length; i++)
            {
                if (toRemove[i])
                {
                    current[i] = false;
                }
            }

            Array.Clear(toRemove);
        } while (changed);

        return current;
    }

    private static void GetZhangSuenNeighbors(
        bool[] img, int w, int x, int y,
        out bool p2, out bool p3, out bool p4, out bool p5,
        out bool p6, out bool p7, out bool p8, out bool p9)
    {
        p2 = img[((y - 1) * w) + x];
        p3 = img[((y - 1) * w) + x + 1];
        p4 = img[(y * w) + x + 1];
        p5 = img[((y + 1) * w) + x + 1];
        p6 = img[((y + 1) * w) + x];
        p7 = img[((y + 1) * w) + x - 1];
        p8 = img[(y * w) + x - 1];
        p9 = img[((y - 1) * w) + x - 1];
    }

    private static int B(bool p2, bool p3, bool p4, bool p5,
        bool p6, bool p7, bool p8, bool p9)
    {
        return (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0)
            + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
    }

    private static int A(bool p2, bool p3, bool p4, bool p5,
        bool p6, bool p7, bool p8, bool p9)
    {
        var count = 0;
        if (!p2 && p3) count++;
        if (!p3 && p4) count++;
        if (!p4 && p5) count++;
        if (!p5 && p6) count++;
        if (!p6 && p7) count++;
        if (!p7 && p8) count++;
        if (!p8 && p9) count++;
        if (!p9 && p2) count++;
        return count;
    }

    private static List<Vector2[]> TraceStrokes(bool[] edgeMask, int width, int height, MapPaintSettings settings)
    {
        var visited = new bool[edgeMask.Length];
        var strokes = new List<Vector2[]>();

        foreach (var start in EnumerateSeeds(edgeMask, width, height))
        {
            if (visited[start])
            {
                continue;
            }

            var stroke = FollowStroke(start, edgeMask, visited, width, height);
            if (stroke.Count < settings.MinStrokeLength)
            {
                continue;
            }

            var simplified = Simplify(stroke, settings.SimplifyTolerance);
            if (simplified.Length < 2)
            {
                continue;
            }

            strokes.Add(simplified);
            if (strokes.Count >= settings.MaxStrokes)
            {
                break;
            }
        }

        return strokes;
    }

    private static IEnumerable<int> EnumerateSeeds(bool[] edgeMask, int width, int height)
    {
        var endpoints = new List<int>();
        var others = new List<int>();

        for (var i = 0; i < edgeMask.Length; i++)
        {
            if (!edgeMask[i])
            {
                continue;
            }

            var x = i % width;
            var y = i / width;
            var degree = CountNeighbors(edgeMask, width, height, x, y);
            if (degree <= 1)
            {
                endpoints.Add(i);
            }
            else
            {
                others.Add(i);
            }
        }

        foreach (var i in endpoints)
        {
            yield return i;
        }

        foreach (var i in others)
        {
            yield return i;
        }
    }

    private static List<Vector2> FollowStroke(int start, bool[] edgeMask, bool[] visited, int width, int height)
    {
        var stroke = new List<Vector2>();
        var current = start;
        var previous = -1;

        while (true)
        {
            if (visited[current])
            {
                break;
            }

            visited[current] = true;
            stroke.Add(new Vector2(current % width, current / width));

            var next = SelectNextNeighbor(current, previous, edgeMask, visited, width, height);
            if (next < 0)
            {
                break;
            }

            previous = current;
            current = next;
        }

        return stroke;
    }

    private static int SelectNextNeighbor(int current, int previous, bool[] edgeMask, bool[] visited, int width, int height)
    {
        var x = current % width;
        var y = current / width;
        var previousDirection = previous >= 0
            ? new Vector2(x - (previous % width), y - (previous / width))
            : Vector2.Zero;

        var best = -1;
        var bestScore = float.NegativeInfinity;

        foreach (var (dx, dy) in Neighbors8)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
            {
                continue;
            }

            var candidate = (ny * width) + nx;
            if (!edgeMask[candidate] || visited[candidate])
            {
                continue;
            }

            var direction = new Vector2(dx, dy).Normalized();
            var score = previous < 0 ? 0f : previousDirection.Normalized().Dot(direction);
            score -= MathF.Abs(dx) + MathF.Abs(dy) == 2 ? 0.05f : 0f;
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = candidate;
        }

        return best;
    }

    private static int CountNeighbors(bool[] edgeMask, int width, int height, int x, int y)
    {
        var count = 0;
        foreach (var (dx, dy) in Neighbors8)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
            {
                continue;
            }

            if (edgeMask[(ny * width) + nx])
            {
                count++;
            }
        }

        return count;
    }

    private static Vector2[] Simplify(List<Vector2> points, float tolerance)
    {
        if (points.Count <= 2 || tolerance <= 0f)
        {
            return points.ToArray();
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifySegment(points, 0, points.Count - 1, tolerance * tolerance, keep);

        var output = new List<Vector2>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                output.Add(points[i]);
            }
        }

        return output.ToArray();
    }

    private static void SimplifySegment(List<Vector2> points, int start, int end, float toleranceSquared, bool[] keep)
    {
        if (end <= start + 1)
        {
            return;
        }

        var a = points[start];
        var b = points[end];
        var maxDistance = -1f;
        var index = -1;

        for (var i = start + 1; i < end; i++)
        {
            var distance = DistanceSquaredToSegment(points[i], a, b);
            if (distance <= maxDistance)
            {
                continue;
            }

            maxDistance = distance;
            index = i;
        }

        if (index < 0 || maxDistance <= toleranceSquared)
        {
            return;
        }

        keep[index] = true;
        SimplifySegment(points, start, index, toleranceSquared, keep);
        SimplifySegment(points, index, end, toleranceSquared, keep);
    }

    private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared <= Mathf.Epsilon)
        {
            return point.DistanceSquaredTo(a);
        }

        var t = Math.Clamp((point - a).Dot(ab) / lengthSquared, 0f, 1f);
        var projection = a + (ab * t);
        return point.DistanceSquaredTo(projection);
    }
}
