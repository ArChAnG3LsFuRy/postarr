using SkiaSharp;

namespace Postarr.Services;

/// <summary>
/// Checks image bytes before anything decodes them, and re-saves uploads as plain JPEG.
/// - A tiny file can declare an enormous picture (a "decompression bomb": e.g. 50,000 × 50,000 pixels, ~10 GB once
///   decoded), so dimensions are read from the header first and anything implausibly large is refused.
/// - Uploads are re-encoded, so only a real raster image ever reaches the media server: never an SVG or HTML file
///   wearing an image content type (which the image proxy would otherwise serve from Postarr's own address).
/// </summary>
public static class ImageGuard
{
    public const int  MaxSide   = 12_000;
    public const long MaxPixels = 80_000_000;      // ~8K × 10K; real posters and backgrounds are far smaller
    public const long MaxUploadBytes = 25 * 1024 * 1024;

    /// <summary>True when the bytes are a decodable raster image of a sane size.</summary>
    public static bool IsSafe(byte[] bytes, out string? reason)
    {
        reason = null;
        using var data  = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null) { reason = "That file isn't an image Postarr can read (use JPEG, PNG or WebP)."; return false; }
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide || (long)w * h > MaxPixels)
        { reason = $"That image is too large ({w} × {h})."; return false; }
        return true;
    }

    /// <summary>Validates an upload and returns it re-encoded as JPEG, or null with a reason.</summary>
    public static byte[]? NormaliseUpload(byte[] bytes, out string? reason)
    {
        if (bytes.Length == 0 || bytes.Length > MaxUploadBytes) { reason = "Images up to 25 MB, please."; return null; }
        if (!IsSafe(bytes, out reason)) return null;
        using var bmp = SKBitmap.Decode(bytes);
        if (bmp == null) { reason = "That file isn't an image Postarr can read."; return null; }
        using var img  = SKImage.FromBitmap(bmp);
        using var jpeg = img.Encode(SKEncodedImageFormat.Jpeg, 92);
        reason = null;
        return jpeg.ToArray();
    }
}
