using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace StrokePreview;

static class Program
{
    // ======================== 可调参数 ========================
    // 图像预处理
    static int MaxImageSize = 768;
    static bool ContrastEnhance = true;

    // 模式: "xdog" = XDoG (适合彩图/照片), "threshold" = 直接阈值 (适合线画/素描)
    static string Mode = "auto";         // auto 会根据图像内容自动选择

    // XDoG (Extended Difference of Gaussians) — 彩图模式
    static float BlurSigma = 0.5f;       // σ: 越小保留越多细节
    static float SigmaRatio = 1.6f;      // k: 两次高斯的 σ 比
    static float Tau = 0.99f;            // τ: DoG 灵敏度
    static float Epsilon = -0.1f;        // ε: 黑/白分界阈值
    static float Phi = 200f;             // φ: 线条锐度 (越大越细)

    // 阈值模式 — 线画/素描专用
    static float DarkThreshold = 0.62f;  // 亮度低于此值的像素视为"笔迹"
    static float PreBlur = 1.2f;         // 阈值化前的轻微模糊 (降噪)

    // 笔画追踪
    static int MinStrokeLen = 2;
    static int MaxStrokes = 8000;
    static float SimplifyTol = 0.5f;

    // Catmull-Rom 平滑
    static int SmoothSubdiv = 8;         // 每段插值点数

    // 渲染 (仿游戏 Line2D 效果)
    // 游戏 SubViewport = 960×1620, Line2D width=4, round caps/joints
    static int CanvasW = 960;
    static int CanvasH = 1620;
    static float ViewPadding = 40f;
    static float LineWidth = 4f;
    static float CurveTension = 0.5f;    // DrawCurve 张力 (0.5 = Catmull-Rom)

    static readonly Color LineColor = Color.FromArgb(220, 50, 105, 45);
    static readonly Color BgColor = Color.FromArgb(255, 210, 192, 160);

    static readonly (int X, int Y)[] N8 =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),           (1, 0),
        (-1, 1),  (0, 1),  (1, 1),
    ];

    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: dotnet run -- <输入图片> [输出前缀]");
            Console.WriteLine("  输出: {前缀}.edges.png  {前缀}.skeleton.png  {前缀}.preview.png");
            Console.WriteLine();
            Console.WriteLine("可选参数 (--key=value):");
            Console.WriteLine("  --sigma   XDoG σ (默认 0.5)");
            Console.WriteLine("  --phi     XDoG φ 锐度 (默认 200)");
            Console.WriteLine("  --epsilon XDoG ε 阈值 (默认 -0.1)");
            Console.WriteLine("  --tau     XDoG τ 灵敏度 (默认 0.99)");
            Console.WriteLine("  --width   线宽 (默认 4)");
            Console.WriteLine("  --smooth  平滑细分 (默认 8, 0=禁用)");
            Console.WriteLine("  --size    最大图像尺寸 (默认 768)");
            return;
        }

        ParseArgs(args);

        var inputPath = args[0];
        var prefix = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : Path.Combine(
                Path.GetDirectoryName(inputPath) ?? ".",
                Path.GetFileNameWithoutExtension(inputPath));

        Console.WriteLine($"[1/6] 加载: {inputPath}");
        using var source = new Bitmap(inputPath);

        var resized = ResizeIfNeeded(source, MaxImageSize);
        var w = resized.Width;
        var h = resized.Height;
        Console.WriteLine($"      处理尺寸: {w}×{h}");

        Console.WriteLine("[2/6] 灰度 → 边缘检测...");
        var gray = ToGrayscale(resized);

        var mode = ResolveMode(Mode, gray);
        Console.WriteLine($"      检测模式: {mode}");

        // threshold 模式下跳过直方图均衡（均衡会打乱亮度分布）
        if (ContrastEnhance && mode != "threshold")
            gray = HistogramEqualize(gray);

        bool[] edges;
        if (mode == "threshold")
        {
            var blurred = GaussianBlur(gray, w, h, PreBlur);
            edges = ThresholdDark(blurred, DarkThreshold);
        }
        else
        {
            var xdog = ApplyXDoG(gray, w, h, BlurSigma, SigmaRatio, Tau, Epsilon, Phi);
            edges = Binarize(xdog);
        }
        SaveMask(edges, w, h, $"{prefix}.edges.png");
        Console.WriteLine($"      已保存: {prefix}.edges.png");

        Console.WriteLine("[3/6] Zhang-Suen 骨架细化...");
        edges = ZhangSuenThin(edges, w, h);
        SaveMask(edges, w, h, $"{prefix}.skeleton.png");
        Console.WriteLine($"      已保存: {prefix}.skeleton.png");

        Console.WriteLine("[4/6] 笔画追踪 + 简化...");
        var strokes = TraceStrokes(edges, w, h);
        Console.WriteLine($"      提取了 {strokes.Count} 条笔画");

        Console.WriteLine("[5/6] Catmull-Rom 曲线平滑...");
        var smoothed = strokes.Select(s => CatmullRomSmooth(s, SmoothSubdiv)).ToList();

        Console.WriteLine("[6/6] 渲染预览 (仿游戏 Line2D)...");
        RenderPreview(smoothed, w, h, $"{prefix}.preview.png");
        Console.WriteLine($"      已保存: {prefix}.preview.png");

        Console.WriteLine("完成。");
    }

    // ======================== 图像预处理 ========================

    static Bitmap ResizeIfNeeded(Bitmap bmp, int maxSize)
    {
        if (maxSize <= 0) return bmp;
        var largest = Math.Max(bmp.Width, bmp.Height);
        if (largest <= maxSize) return bmp;
        var scale = maxSize / (float)largest;
        var nw = Math.Max(1, (int)Math.Round(bmp.Width * scale));
        var nh = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        var resized = new Bitmap(nw, nh);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(bmp, 0, 0, nw, nh);
        return resized;
    }

    static float[] ToGrayscale(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var output = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = bmp.GetPixel(x, y);
            output[y * w + x] = 0.299f * (c.R / 255f) + 0.587f * (c.G / 255f) + 0.114f * (c.B / 255f);
        }
        return output;
    }

    static float[] HistogramEqualize(float[] gray)
    {
        const int bins = 256;
        var hist = new int[bins];
        foreach (var v in gray)
            hist[Math.Clamp((int)(v * (bins - 1)), 0, bins - 1)]++;

        var cdf = new float[bins];
        cdf[0] = hist[0];
        for (var i = 1; i < bins; i++) cdf[i] = cdf[i - 1] + hist[i];

        var cdfMin = 0f;
        for (var i = 0; i < bins; i++) { if (cdf[i] > 0) { cdfMin = cdf[i]; break; } }
        var denom = gray.Length - cdfMin;
        if (denom <= 0f) return gray;

        var output = new float[gray.Length];
        for (var i = 0; i < gray.Length; i++)
        {
            var bin = Math.Clamp((int)(gray[i] * (bins - 1)), 0, bins - 1);
            output[i] = (cdf[bin] - cdfMin) / denom;
        }
        return output;
    }

    // ======================== 模式选择 ========================

    static string ResolveMode(string mode, float[] gray)
    {
        if (mode is "xdog" or "threshold") return mode;

        // 统计亮度分布
        var brightCount = 0;  // 明亮像素 (纸/背景)
        var darkCount = 0;    // 暗色像素 (墨/笔迹)
        var midCount = 0;     // 中间灰度 (阴影/渐变)
        foreach (var v in gray)
        {
            if (v > 0.75f) brightCount++;
            else if (v < 0.45f) darkCount++;
            else midCount++;
        }
        var total = (float)gray.Length;
        var brightRatio = brightCount / total;
        var darkRatio = darkCount / total;
        var midRatio = midCount / total;

        // 线画特征: 大面积亮背景 + 较少暗线条 + 较少中间灰度
        // 照片特征: 亮暗中间灰度分布较均匀
        if (brightRatio > 0.45f && darkRatio > 0.01f && midRatio < 0.35f)
            return "threshold";
        return "xdog";
    }

    static bool[] ThresholdDark(float[] gray, float threshold)
    {
        var mask = new bool[gray.Length];
        for (var i = 0; i < gray.Length; i++)
            mask[i] = gray[i] < threshold;
        return mask;
    }

    // ======================== XDoG 算法 ========================

    static float[] GaussianBlur(float[] src, int w, int h, float sigma)
    {
        if (sigma <= 0f) return src;
        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 3f));
        var kSize = radius * 2 + 1;
        var kernel = new float[kSize];
        var kSum = 0f;
        for (var i = 0; i < kSize; i++)
        {
            var d = i - radius;
            kernel[i] = MathF.Exp(-(d * d) / (2f * sigma * sigma));
            kSum += kernel[i];
        }
        for (var i = 0; i < kSize; i++) kernel[i] /= kSum;

        var temp = new float[src.Length];
        var output = new float[src.Length];

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var k = -radius; k <= radius; k++)
                sum += src[y * w + Math.Clamp(x + k, 0, w - 1)] * kernel[k + radius];
            temp[y * w + x] = sum;
        }

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var k = -radius; k <= radius; k++)
                sum += temp[Math.Clamp(y + k, 0, h - 1) * w + x] * kernel[k + radius];
            output[y * w + x] = sum;
        }
        return output;
    }

    static float[] ApplyXDoG(float[] gray, int w, int h,
        float sigma, float k, float tau, float eps, float phi)
    {
        var fine = GaussianBlur(gray, w, h, sigma);
        var coarse = GaussianBlur(gray, w, h, sigma * k);
        var output = new float[gray.Length];
        for (var i = 0; i < gray.Length; i++)
        {
            var dog = fine[i] - tau * coarse[i];
            output[i] = dog >= eps ? 1f : 1f + MathF.Tanh(phi * (dog - eps));
        }
        return output;
    }

    static bool[] Binarize(float[] xdog, float threshold = 0.5f)
    {
        var mask = new bool[xdog.Length];
        for (var i = 0; i < xdog.Length; i++) mask[i] = xdog[i] < threshold;
        return mask;
    }

    // ======================== Zhang-Suen 骨架细化 ========================

    static bool[] ZhangSuenThin(bool[] mask, int w, int h)
    {
        var cur = (bool[])mask.Clone();
        var rem = new bool[mask.Length];
        bool changed;
        do
        {
            changed = false;
            ZhangSuenPass(cur, rem, w, h, step1: true, ref changed);
            Apply(cur, rem);
            Array.Clear(rem);
            ZhangSuenPass(cur, rem, w, h, step1: false, ref changed);
            Apply(cur, rem);
            Array.Clear(rem);
        } while (changed);
        return cur;
    }

    static void ZhangSuenPass(bool[] img, bool[] rem, int w, int h, bool step1, ref bool changed)
    {
        for (var y = 1; y < h - 1; y++)
        for (var x = 1; x < w - 1; x++)
        {
            var i = y * w + x;
            if (!img[i]) continue;
            var p2 = img[(y - 1) * w + x];
            var p3 = img[(y - 1) * w + x + 1];
            var p4 = img[y * w + x + 1];
            var p5 = img[(y + 1) * w + x + 1];
            var p6 = img[(y + 1) * w + x];
            var p7 = img[(y + 1) * w + x - 1];
            var p8 = img[y * w + x - 1];
            var p9 = img[(y - 1) * w + x - 1];

            var b = (p2?1:0)+(p3?1:0)+(p4?1:0)+(p5?1:0)+(p6?1:0)+(p7?1:0)+(p8?1:0)+(p9?1:0);
            if (b < 2 || b > 6) continue;

            var a = 0;
            if (!p2 && p3) a++; if (!p3 && p4) a++; if (!p4 && p5) a++;
            if (!p5 && p6) a++; if (!p6 && p7) a++; if (!p7 && p8) a++;
            if (!p8 && p9) a++; if (!p9 && p2) a++;
            if (a != 1) continue;

            if (step1)
            {
                if (p2 && p4 && p6) continue;
                if (p4 && p6 && p8) continue;
            }
            else
            {
                if (p2 && p4 && p8) continue;
                if (p2 && p6 && p8) continue;
            }
            rem[i] = true;
            changed = true;
        }
    }

    static void Apply(bool[] cur, bool[] rem)
    {
        for (var i = 0; i < cur.Length; i++)
            if (rem[i]) cur[i] = false;
    }

    // ======================== 笔画追踪 ========================

    static List<Vector2[]> TraceStrokes(bool[] mask, int w, int h)
    {
        var visited = new bool[mask.Length];
        var strokes = new List<Vector2[]>();

        foreach (var seed in EnumerateSeeds(mask, w, h))
        {
            if (visited[seed]) continue;
            var pts = FollowStroke(seed, mask, visited, w, h);
            if (pts.Count < MinStrokeLen) continue;
            var simplified = DouglasPeucker(pts, SimplifyTol);
            if (simplified.Length >= 2)
                strokes.Add(simplified);
            if (strokes.Count >= MaxStrokes) break;
        }
        return strokes;
    }

    static IEnumerable<int> EnumerateSeeds(bool[] mask, int w, int h)
    {
        var endpoints = new List<int>();
        var others = new List<int>();
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            var x = i % w; var y = i / w;
            var deg = 0;
            foreach (var (dx, dy) in N8)
            {
                var nx = x + dx; var ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && mask[ny * w + nx]) deg++;
            }
            (deg <= 1 ? endpoints : others).Add(i);
        }
        foreach (var i in endpoints) yield return i;
        foreach (var i in others) yield return i;
    }

    static List<Vector2> FollowStroke(int start, bool[] mask, bool[] visited, int w, int h)
    {
        var pts = new List<Vector2>();
        var cur = start;
        var prev = -1;
        while (true)
        {
            if (visited[cur]) break;
            visited[cur] = true;
            pts.Add(new Vector2(cur % w, cur / w));
            var next = NextNeighbor(cur, prev, mask, visited, w, h);
            if (next < 0) break;
            prev = cur;
            cur = next;
        }
        return pts;
    }

    static int NextNeighbor(int cur, int prev, bool[] mask, bool[] visited, int w, int h)
    {
        var x = cur % w; var y = cur / w;
        var prevDir = prev >= 0 ? new Vector2(x - prev % w, y - prev / w) : Vector2.Zero;
        var best = -1; var bestScore = float.NegativeInfinity;

        foreach (var (dx, dy) in N8)
        {
            var nx = x + dx; var ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
            var ci = ny * w + nx;
            if (!mask[ci] || visited[ci]) continue;
            var dir = Vector2.Normalize(new Vector2(dx, dy));
            var score = prev < 0 ? 0f : Vector2.Dot(Vector2.Normalize(prevDir), dir);
            if (Math.Abs(dx) + Math.Abs(dy) == 2) score -= 0.05f;
            if (score > bestScore) { bestScore = score; best = ci; }
        }
        return best;
    }

    // ======================== Douglas-Peucker 简化 ========================

    static Vector2[] DouglasPeucker(List<Vector2> pts, float tol)
    {
        if (pts.Count <= 2 || tol <= 0f) return pts.ToArray();
        var keep = new bool[pts.Count];
        keep[0] = true; keep[^1] = true;
        DPSegment(pts, 0, pts.Count - 1, tol * tol, keep);
        var result = new List<Vector2>();
        for (var i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result.ToArray();
    }

    static void DPSegment(List<Vector2> pts, int s, int e, float tol2, bool[] keep)
    {
        if (e <= s + 1) return;
        var a = pts[s]; var b = pts[e];
        var maxD = -1f; var idx = -1;
        for (var i = s + 1; i < e; i++)
        {
            var d = DistSqToSegment(pts[i], a, b);
            if (d > maxD) { maxD = d; idx = i; }
        }
        if (idx < 0 || maxD <= tol2) return;
        keep[idx] = true;
        DPSegment(pts, s, idx, tol2, keep);
        DPSegment(pts, idx, e, tol2, keep);
    }

    static float DistSqToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        if (len2 < 1e-8f) return Vector2.DistanceSquared(p, a);
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    // ======================== Catmull-Rom 样条平滑 ========================

    static Vector2[] CatmullRomSmooth(Vector2[] pts, int subdiv)
    {
        if (subdiv <= 0 || pts.Length < 3) return pts;

        var result = new List<Vector2>(pts.Length * subdiv);
        for (var i = 0; i < pts.Length - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[Math.Min(pts.Length - 1, i + 1)];
            var p3 = pts[Math.Min(pts.Length - 1, i + 2)];

            for (var j = 0; j < subdiv; j++)
            {
                var t = j / (float)subdiv;
                var t2 = t * t;
                var t3 = t2 * t;
                var pt = 0.5f * (
                    2f * p1
                    + (-p0 + p2) * t
                    + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                    + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                result.Add(pt);
            }
        }
        result.Add(pts[^1]);
        return result.ToArray();
    }

    // ======================== 渲染器 (仿游戏 Line2D) ========================

    static void RenderPreview(List<Vector2[]> strokes, int imgW, int imgH, string outPath)
    {
        using var bmp = new Bitmap(CanvasW, CanvasH);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.Clear(BgColor);

        var availW = CanvasW - 2 * ViewPadding;
        var availH = CanvasH - 2 * ViewPadding;
        var fitScale = Math.Min(availW / Math.Max(1f, imgW), availH / Math.Max(1f, imgH));
        var contentW = imgW * fitScale;
        var contentH = imgH * fitScale;
        var offX = (CanvasW - contentW) / 2f;
        var offY = (CanvasH - contentH) / 2f;

        using var pen = new Pen(LineColor, LineWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };

        foreach (var stroke in strokes)
        {
            if (stroke.Length < 2) continue;
            var points = stroke
                .Select(p => new PointF(offX + p.X * fitScale, offY + p.Y * fitScale))
                .ToArray();

            if (points.Length == 2)
            {
                g.DrawLine(pen, points[0], points[1]);
            }
            else
            {
                g.DrawCurve(pen, points, CurveTension);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        bmp.Save(outPath);
    }

    // ======================== 辅助方法 ========================

    static void SaveMask(bool[] mask, int w, int h, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var bmp = new Bitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            bmp.SetPixel(x, y, mask[y * w + x] ? Color.Black : Color.White);
        bmp.Save(path);
    }

    static void ParseArgs(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--") || !arg.Contains('=')) continue;
            var parts = arg[2..].Split('=', 2);
            var key = parts[0].ToLowerInvariant();
            var val = parts[1];
            switch (key)
            {
                case "sigma":   BlurSigma = float.Parse(val); break;
                case "phi":     Phi = float.Parse(val); break;
                case "epsilon": Epsilon = float.Parse(val); break;
                case "tau":     Tau = float.Parse(val); break;
                case "k":       SigmaRatio = float.Parse(val); break;
                case "width":   LineWidth = float.Parse(val); break;
                case "smooth":  SmoothSubdiv = int.Parse(val); break;
                case "size":    MaxImageSize = int.Parse(val); break;
                case "minstroke": MinStrokeLen = int.Parse(val); break;
                case "maxstroke": MaxStrokes = int.Parse(val); break;
                case "simplify":  SimplifyTol = float.Parse(val); break;
                case "padding":   ViewPadding = float.Parse(val); break;
                case "contrast":  ContrastEnhance = val == "1" || val.ToLower() == "true"; break;
                case "mode":      Mode = val.ToLower(); break;
                case "dark":      DarkThreshold = float.Parse(val); break;
                case "preblur":   PreBlur = float.Parse(val); break;
            }
        }
    }
}
