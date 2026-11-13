using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CursorFade;

/// <summary>
/// 读取当前皮肤的光标文件，生成"仅角色动图淡化"的副本（.ani / .cur 均支持）。
/// 分离原理：帧间持续变化的像素 = 角色动图，帧间静止的像素 = 指针本体；
/// 淡出时只降低角色像素的 alpha，指针保持实心可读。
/// 生成的副本放在 LocalAppData 缓存里，按源文件内容哈希命名。
/// </summary>
internal static class AniFader
{
    private const int DeviationThreshold = 16;   // 帧间差异阈值：超过视为动画区域
    private const int AlphaSolidThreshold = 16;  // 非透明阈值（指针连通判定用）
    private const string CacheVersion = "_c5";   // 缓存算法版本，升级算法时改这里强制重建

    private sealed class AnimationMask
    {
        public byte[] Data = Array.Empty<byte>();
        public int W;
        public int H;
    }
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorFade", "cache");

    /// <summary>已生成缓存的内存索引：避免每次触发都读源文件算哈希。</summary>
    private static readonly Dictionary<string, string> MemoryCache = new();
    private static readonly object Gate = new();

    private static string CacheKey(string sourcePath, int alphaPercent) => $"{sourcePath}|{alphaPercent}";

    /// <summary>只查已生成的缓存，绝不触发生成（供主循环高频调用）。</summary>
    public static string? GetCachedPath(string sourcePath, int alphaPercent)
    {
        lock (Gate)
        {
            return MemoryCache.TryGetValue(CacheKey(sourcePath, alphaPercent), out var p) && File.Exists(p) ? p : null;
        }
    }

    public static string? EnsureFaded(string sourcePath, int alphaPercent)
    {
        lock (Gate)
        {
            return EnsureFadedCore(sourcePath, alphaPercent);
        }
    }

    private static string? EnsureFadedCore(string sourcePath, int alphaPercent)
    {
        try
        {
            var key = CacheKey(sourcePath, alphaPercent);
            if (MemoryCache.TryGetValue(key, out var cached) && File.Exists(cached)) return cached;

            if (!File.Exists(sourcePath)) return null;
            var bytes = File.ReadAllBytes(sourcePath);
            var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 8);
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext != ".ani" && ext != ".cur") return null;

            Directory.CreateDirectory(CacheDir);
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var target = Path.Combine(CacheDir, $"{name}_{hash}_{alphaPercent}{CacheVersion}{ext}");
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                MemoryCache[key] = target;
                return target;
            }

            float alpha = Math.Clamp(alphaPercent, 0, 100) / 100f;
            var result = ext == ".ani" ? FadeAni(bytes, alpha) : FadeCur(bytes, alpha, null);
            if (result == null) return null;

            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, result);
            File.Move(tmp, target, true);
            MemoryCache[key] = target;
            return target;
        }
        catch (Exception ex)
        {
            Log.Write($"生成淡化光标失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private static byte[]? FadeAni(byte[] src, float alpha)
    {
        if (src.Length < 20) return null;
        if (Encoding.ASCII.GetString(src, 0, 4) != "RIFF" || Encoding.ASCII.GetString(src, 8, 4) != "ACON") return null;

        var chunks = new List<byte[]>();
        int p = 12;
        while (p + 8 <= src.Length)
        {
            string id = Encoding.ASCII.GetString(src, p, 4);
            int size = BitConverter.ToInt32(src, p + 4);
            int dataStart = p + 8;
            if (size < 0 || dataStart + size > src.Length) break;

            byte[] chunk;
            if (id == "LIST" && size >= 4 && Encoding.ASCII.GetString(src, dataStart, 4) == "fram")
            {
                var inner = new List<(string Id, byte[] Data)>();
                int q = dataStart + 4;
                int end = dataStart + size;
                while (q + 8 <= end)
                {
                    string iid = Encoding.ASCII.GetString(src, q, 4);
                    int isz = BitConverter.ToInt32(src, q + 4);
                    if (isz < 0 || q + 8 + isz > end) break;

                    var idata = new byte[isz];
                    Buffer.BlockCopy(src, q + 8, idata, 0, isz);
                    inner.Add((iid, idata));
                    q += 8 + isz + (isz & 1);
                }

                // 逐尺寸独立构建"角色区域"掩码（各尺寸布局并非严格等比，不能互相套用），
                // 再逐帧按对应尺寸的掩码淡化
                var iconFrames = new List<byte[]>();
                foreach (var (innerId, innerData) in inner)
                {
                    if (innerId == "icon") iconFrames.Add(innerData);
                }
                var entryMasks = BuildEntryMasks(iconFrames);

                var outInner = new List<byte[]>(inner.Count);
                foreach (var (iid, idata) in inner)
                {
                    byte[] dataOut = idata;
                    if (iid == "icon")
                    {
                        var faded = FadeCur(idata, alpha, entryMasks);
                        if (faded != null) dataOut = faded;
                    }
                    outInner.Add(MakeChunk(iid, dataOut));
                }
                chunk = MakeListChunk("fram", outInner);
            }
            else
            {
                chunk = new byte[8 + size];
                Buffer.BlockCopy(src, p, chunk, 0, 8 + size);
            }

            chunks.Add(chunk);
            p = dataStart + size + (size & 1);
        }

        if (chunks.Count == 0) return null;

        int total = 12 + chunks.Sum(c => c.Length);
        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(total - 8);
        w.Write(Encoding.ASCII.GetBytes("ACON"));
        foreach (var c in chunks) w.Write(c);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[]? FadeCur(byte[] cur, float alpha, AnimationMask?[]? entryMasks)
    {
        if (cur.Length < 6) return null;
        if (BitConverter.ToUInt16(cur, 2) != 2) return null; // 2 = cursor
        int count = BitConverter.ToUInt16(cur, 4);
        if (count <= 0 || cur.Length < 6 + count * 16) return null;

        var heads = new List<byte[]>(count);
        var images = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int e = 6 + i * 16;
            var head = new byte[16];
            Buffer.BlockCopy(cur, e, head, 0, 16);
            int size = BitConverter.ToInt32(cur, e + 8);
            int off = BitConverter.ToInt32(cur, e + 12);
            if (size <= 0 || off <= 0 || off + size > cur.Length) return null;

            var img = new byte[size];
            Buffer.BlockCopy(cur, off, img, 0, size);
            heads.Add(head);
            AnimationMask? mask = (entryMasks != null && i < entryMasks.Length) ? entryMasks[i] : null;
            images.Add(FadeImage(img, alpha, mask));
        }

        using var ms = new MemoryStream();
        var hdr = new byte[6];
        hdr[2] = 2;
        BitConverter.GetBytes((ushort)count).CopyTo(hdr, 4);
        ms.Write(hdr);

        int offset = 6 + count * 16;
        for (int i = 0; i < count; i++)
        {
            var head = (byte[])heads[i].Clone();
            BitConverter.GetBytes(images[i].Length).CopyTo(head, 8);
            BitConverter.GetBytes(offset).CopyTo(head, 12);
            ms.Write(head);
            offset += images[i].Length;
        }
        foreach (var img in images) ms.Write(img);

        return ms.ToArray();
    }

    /// <summary>
    /// 淡化单帧图片。
    /// 角色像素由 GDI+ ColorMatrix 路径生成（该路产出的 PNG 经 Windows 光标加载器验证可加载），
    /// 指针像素直接取自原图；mask 为 null（单帧 .cur 或无法分离）时整体走 ColorMatrix 淡出。
    /// 两部分拼装后的图像已实测可被 LoadCursorFromFile 接受。
    /// </summary>
    private static byte[] FadeImage(byte[] data, float alpha, AnimationMask? mask)
    {
        try
        {
            bool isPng = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
            if (!isPng) return data; // 非 PNG 帧原样保留

            using var input = new MemoryStream(data);
            using var src = new Bitmap(input);
            int w = src.Width;
            int h = src.Height;
            var rect = new Rectangle(0, 0, w, h);

            // 角色淡化版：GDI+ ColorMatrix 渲染
            using var faded = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(faded))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                var cm = new ColorMatrix { Matrix33 = alpha };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(src, rect, 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }

            byte[] pixels;
            if (mask == null)
            {
                // 整体淡出（回退路径）
                pixels = ExtractBgra(faded, rect, w, h);
            }
            else
            {
                // 指针(掩码=0)取原图，角色(掩码≠0)取淡化版；二值分区保证像素值来自两条已验证路径
                var srcPx = ExtractBgra(src, rect, w, h);
                var fadedPx = ExtractBgra(faded, rect, w, h);
                var scaled = ScaleMaskBox(mask, w, h);
                pixels = new byte[srcPx.Length];
                for (int i = 0; i < scaled.Length; i++)
                {
                    int b4 = i * 4;
                    if (scaled[i] >= 128)
                    {
                        pixels[b4] = fadedPx[b4];
                        pixels[b4 + 1] = fadedPx[b4 + 1];
                        pixels[b4 + 2] = fadedPx[b4 + 2];
                        pixels[b4 + 3] = fadedPx[b4 + 3];
                    }
                    else
                    {
                        pixels[b4] = srcPx[b4];
                        pixels[b4 + 1] = srcPx[b4 + 1];
                        pixels[b4 + 2] = srcPx[b4 + 2];
                        pixels[b4 + 3] = srcPx[b4 + 3];
                    }
                }
            }

            using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var dstData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(pixels, 0, dstData.Scan0, pixels.Length);
            }
            finally
            {
                dst.UnlockBits(dstData);
            }

            using var output = new MemoryStream();
            dst.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch
        {
            return data;
        }
    }

    private static byte[] ExtractBgra(Bitmap bmp, Rectangle rect, int w, int h)
    {
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            if (data.Stride != w * 4)
            {
                var tight = new byte[w * 4 * h];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(pixels, y * data.Stride, tight, y * w * 4, w * 4);
                return tight;
            }
            return pixels;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// 逐尺寸构建掩码：对每个 entry（32/48/…/256px），用全部帧在该尺寸上的图像做帧间差异，
    /// 得到该尺寸专属的"角色区域"（帧间变化 = 角色动图，静止 = 指针），再做孔洞填充
    /// 把角色内部"恰好全帧静止"的斑块并入角色区。
    /// 某尺寸构建失败时该尺寸返回 null → 该尺寸退回整体淡出。
    /// </summary>
    private static AnimationMask?[]? BuildEntryMasks(List<byte[]> frames)
    {
        if (frames.Count < 2) return null;
        int entryCount = ReadEntryCount(frames[0]);
        if (entryCount <= 0) return null;
        foreach (var f in frames)
        {
            if (ReadEntryCount(f) != entryCount) return null; // 各帧条目结构不一致，放弃分离
        }

        var masks = new AnimationMask?[entryCount];
        var ratios = new List<string>();
        for (int i = 0; i < entryCount; i++)
        {
            var decoded = new List<DecodedBgra>(frames.Count);
            int w = 0, h = 0;
            bool ok = true;
            foreach (var f in frames)
            {
                var png = TryEntryPng(f, i);
                if (png == null) { ok = false; break; }
                var d = DecodePngBgra(png);
                if (d == null) { ok = false; break; }
                if (decoded.Count == 0) { w = d.Width; h = d.Height; }
                else if (d.Width != w || d.Height != h) { ok = false; break; }
                decoded.Add(d);
            }
            if (!ok || decoded.Count < 2) continue;

            var mask = ComputeMask(decoded, w, h);
            if (mask == null) continue;

            // 指针归属：从热点（指针尖）在"非透明静止区"内泛洪——指针本体非透明且直接连着热点；
            // 角色内部的全帧静止斑块（如手中物品）与指针之间隔着透明背景，连不通 → 判为角色淡化。
            // 透明背景既不参与连通、也不计入统计（淡化与否视觉无差）。
            if (TryEntryHotspot(frames[0], i, out int hx, out int hy))
                KeepPointerRegion(mask, decoded[0].Pixels, w, h, hx, hy);

            int totalSolid = 0, lit = 0;
            var refPx = decoded[0].Pixels;
            for (int px = 0; px < mask.Length; px++)
            {
                if (refPx[px * 4 + 3] <= AlphaSolidThreshold) continue;
                totalSolid++;
                if (mask[px] != 0) lit++;
            }
            double ratio = totalSolid > 0 ? lit / (double)totalSolid : 0;
            if (ratio < 0.02 || ratio > 0.98)
            {
                ratios.Add($"{w}={ratio:P0}(放弃)");
                continue;
            }
            masks[i] = new AnimationMask { Data = mask, W = w, H = h };
            ratios.Add($"{w}={ratio:P0}");
        }

        Log.Write($"角色分离(逐尺寸): {string.Join(" ", ratios)}");
        return masks;
    }

    private static int ReadEntryCount(byte[] cur)
    {
        if (cur.Length < 6 || BitConverter.ToUInt16(cur, 2) != 2) return -1;
        int count = BitConverter.ToUInt16(cur, 4);
        if (count <= 0 || cur.Length < 6 + count * 16) return -1;
        return count;
    }

    /// <summary>读取指定条目自身的热点（指针尖）坐标。</summary>
    private static bool TryEntryHotspot(byte[] cur, int index, out int hx, out int hy)
    {
        hx = -1;
        hy = -1;
        int count = ReadEntryCount(cur);
        if (count <= 0 || index < 0 || index >= count) return false;
        int e = 6 + index * 16;
        hx = BitConverter.ToUInt16(cur, e + 4);
        hy = BitConverter.ToUInt16(cur, e + 6);
        return true;
    }

    /// <summary>帧间任一通道极差 ≥ 阈值 → 角色区域(255)，否则指针区域(0)。</summary>
    private static byte[]? ComputeMask(List<DecodedBgra> frames, int w, int h)
    {
        int total = w * h;
        var mask = new byte[total];
        int n = frames.Count;
        for (int i = 0; i < total; i++)
        {
            int b4 = i * 4;
            int minA = 255, maxA = 0, minR = 255, maxR = 0;
            int minG = 255, maxG = 0, minB = 255, maxB = 0;
            for (int f = 0; f < n; f++)
            {
                var p = frames[f].Pixels;
                int a = p[b4 + 3], r = p[b4 + 2], g = p[b4 + 1], b = p[b4];
                if (a < minA) minA = a; if (a > maxA) maxA = a;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }
            int dev = Math.Max(Math.Max(maxA - minA, maxR - minR), Math.Max(maxG - minG, maxB - minB));
            if (dev >= DeviationThreshold) mask[i] = 255;
        }
        return mask;
    }

    /// <summary>
    /// 从热点在"非透明静止区"内泛洪：指针本体非透明且与热点直接相连 → 保留；
    /// 其余静止像素（含角色内部全帧静止斑块、以及透明背景）→ 归入淡化区。
    /// 透明背景不作为连通桥梁（角色静止斑块与指针之间必隔透明背景），杜绝误吞角色。
    /// </summary>
    private static void KeepPointerRegion(byte[] mask, byte[] refPixels, int w, int h, int sx, int sy)
    {
        bool Solid(int idx) => mask[idx] == 0 && refPixels[idx * 4 + 3] > AlphaSolidThreshold;

        int seed = -1;
        if (sx >= 0 && sy >= 0 && sx < w && sy < h && Solid(sy * w + sx))
        {
            seed = sy * w + sx;
        }
        else
        {
            // 热点落在透明/动画像素上：半径内找最近的非透明静止像素作种子
            int radius = Math.Max(8, w / 6);
            long bestD = long.MaxValue;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = sy + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = sx + dx;
                    if (x < 0 || x >= w) continue;
                    int idx = y * w + x;
                    if (!Solid(idx)) continue;
                    long d = (long)dx * dx + (long)dy * dy;
                    if (d < bestD) { bestD = d; seed = idx; }
                }
            }
            if (seed < 0) return; // 找不到种子，保持原掩码
        }

        var visited = new bool[w * h];
        var queue = new Queue<int>();
        visited[seed] = true;
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int x = idx % w, y = idx / w;
            if (x > 0) TryVisit(idx - 1);
            if (x < w - 1) TryVisit(idx + 1);
            if (y > 0) TryVisit(idx - w);
            if (y < h - 1) TryVisit(idx + w);

            void TryVisit(int n)
            {
                if (!visited[n] && Solid(n))
                {
                    visited[n] = true;
                    queue.Enqueue(n);
                }
            }
        }

        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0 && !visited[i]) mask[i] = 255;
        }
    }

    /// <summary>从 CUR 数据里取出指定条目的 PNG 图像。</summary>
    private static byte[]? TryEntryPng(byte[] cur, int index)
    {
        if (cur.Length < 6 || BitConverter.ToUInt16(cur, 2) != 2) return null;
        int count = BitConverter.ToUInt16(cur, 4);
        if (count <= 0 || index < 0 || index >= count || cur.Length < 6 + count * 16) return null;

        int e = 6 + index * 16;
        int size = BitConverter.ToInt32(cur, e + 8);
        int off = BitConverter.ToInt32(cur, e + 12);
        if (size <= 0 || off <= 0 || off + size > cur.Length) return null;
        var png = new byte[size];
        Buffer.BlockCopy(cur, off, png, 0, size);
        return png;
    }

    private sealed class DecodedBgra
    {
        public byte[] Pixels = Array.Empty<byte>();
        public int Width;
        public int Height;
    }

    private static DecodedBgra? DecodePngBgra(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var bmp = new Bitmap(ms);
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                if (data.Stride != w * 4)
                {
                    // 去掉行尾填充，统一为紧密排列
                    var tight = new byte[w * 4 * h];
                    for (int y = 0; y < h; y++)
                        Buffer.BlockCopy(pixels, y * data.Stride, tight, y * w * 4, w * 4);
                    pixels = tight;
                }
                return new DecodedBgra { Pixels = pixels, Width = w, Height = h };
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>掩码按目标尺寸做盒式缩放（降采样时自然羽化边缘）。</summary>
    private static byte[] ScaleMaskBox(AnimationMask mask, int tw, int th)
    {
        if (mask.W == tw && mask.H == th) return mask.Data;
        var res = new byte[tw * th];
        double sx = (double)mask.W / tw;
        double sy = (double)mask.H / th;
        for (int ty = 0; ty < th; ty++)
        {
            int y0 = (int)(ty * sy);
            int y1 = Math.Min(mask.H, Math.Max(y0 + 1, (int)Math.Ceiling((ty + 1) * sy)));
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = (int)(tx * sx);
                int x1 = Math.Min(mask.W, Math.Max(x0 + 1, (int)Math.Ceiling((tx + 1) * sx)));
                int sum = 0, cnt = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * mask.W;
                    for (int x = x0; x < x1; x++)
                    {
                        sum += mask.Data[row + x];
                        cnt++;
                    }
                }
                res[ty * tw + tx] = (byte)(cnt > 0 ? sum / cnt : 0);
            }
        }
        return res;
    }

    private static byte[] MakeChunk(string id, byte[] data)
    {
        int pad = data.Length & 1;
        var buf = new byte[8 + data.Length + pad];
        Encoding.ASCII.GetBytes(id, 0, 4, buf, 0);
        BitConverter.GetBytes(data.Length).CopyTo(buf, 4);
        Buffer.BlockCopy(data, 0, buf, 8, data.Length);
        return buf;
    }

    private static byte[] MakeListChunk(string type, List<byte[]> inner)
    {
        int innerSize = 4 + inner.Sum(c => c.Length);
        int pad = innerSize & 1;
        var buf = new byte[8 + innerSize + pad];
        Encoding.ASCII.GetBytes("LIST", 0, 4, buf, 0);
        BitConverter.GetBytes(innerSize).CopyTo(buf, 4);
        Encoding.ASCII.GetBytes(type, 0, 4, buf, 8);
        int p = 12;
        foreach (var c in inner)
        {
            Buffer.BlockCopy(c, 0, buf, p, c.Length);
            p += c.Length;
        }
        return buf;
    }
}
