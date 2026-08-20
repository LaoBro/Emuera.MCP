using System;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// 纯 C# 图片尺寸探针——无 System.Drawing、无新依赖、Android 安全（issue 01）。
/// 读取 PNG/JPEG 文件头解析像素尺寸，供无头模式下 <see cref="ConsoleImagePart"/>
/// 的几何计算使用（sprite 表不可用时按原图纵横比推算宽度）。
/// 解析失败返回 false（调用方回退到参数或 0）。
/// </summary>
internal static class ImageSizeProbe
{
    /// <summary>解析 PNG 头（IHDR chunk）。成功时返回像素宽高。</summary>
    public static bool TryParsePng(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        // signature(8) + length(4) + "IHDR"(4) + width(4) + height(4) = 24 字节
        if (header.Length < 24)
            return false;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(signature))
            return false;
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
            return false;
        width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 解析 JPEG 尺寸——扫描段流直到 SOF（帧头）marker。
    /// SOF0-3/5-7/9-11/13-15（排除 DHT/DAC/JPG）段内 offset 3 起为 height(2)/width(2)。
    /// 跳过 APPn/COM/DQT 等带长度段与 RST/TEM/SOI/EOI/填充等独立 marker。
    /// </summary>
    public static bool TryParseJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return false;
        int i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
                return false; // 段必须以 FF 开头
            byte marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // 填充字节
                continue;
            }
            // 独立 marker（无长度字段）：TEM(01)、RST(FFD0-D7)、SOI(D8)、EOI(D9)、填充(00)
            if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                i += 2;
                continue;
            }
            if (i + 4 > data.Length)
                return false;
            int len = (data[i + 2] << 8) | data[i + 3];
            if (len < 2 || i + 2 + len > data.Length)
                return false;
            // SOF：C0-CF 内排除 DHT(C4)、JPG(C8)、DAC(CC)
            bool isSof = (marker & 0xF0) == 0xC0 && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                if (len < 7)
                    return false;
                height = (data[i + 5] << 8) | data[i + 6];
                width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0;
            }
            i += 2 + len;
        }
        return false;
    }

    /// <summary>
    /// 经 <see cref="IGameDirAccessor"/> 读取文件头部并解析尺寸（PNG/JPEG 分发）。
    /// 只读前 64KB（PNG 头 24B 足够；JPEG SOF 段在 SOS 之前，实际位于文件前部）。
    /// 文件缺失、非图片、解析失败均返回 false。
    /// </summary>
    public static bool TryGetPixelSize(IGameDirAccessor accessor, string fullPath, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (accessor == null || string.IsNullOrEmpty(fullPath))
            return false;
        using var stream = accessor.OpenRead(fullPath);
        if (stream == null)
            return false;
        Span<byte> buffer = stackalloc byte[64 * 1024];
        // Stream.Read 不保证一次填满（SAF 内容流可能部分读）——循环读至 EOF 或缓冲区满
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0)
                break;
            read += n;
        }
        if (read < 2)
            return false;
        // PNG signature: 89 50 4E 47
        if (read >= 8 && buffer[0] == 0x89 && buffer[1] == 0x50)
            return TryParsePng(buffer[..read], out width, out height);
        // JPEG SOI: FF D8
        if (buffer[0] == 0xFF && buffer[1] == 0xD8)
            return TryParseJpeg(buffer[..read], out width, out height);
        return false;
    }
}
