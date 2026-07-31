namespace EasyGet.Services;

internal static class MediaFileClassifier
{
    private static readonly HashSet<string> PreviewVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv"
    };

    private static readonly HashSet<string> ThumbnailOnlyVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4v", ".ts", ".mts", ".m2ts", ".mpeg", ".mpg", ".3gp", ".m3u8"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".wav", ".flac", ".aac", ".opus", ".ogg"
    };

    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif"
    };

    private static readonly HashSet<string> ThumbnailOnlyDirectImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".tif", ".tiff"
    };

    private static readonly HashSet<string> FfmpegImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp"
    };

    public static bool IsDirectImageExtension(string extension)
        => PreviewImageExtensions.Contains(extension)
           || ThumbnailOnlyDirectImageExtensions.Contains(extension);

    public static bool IsFfmpegThumbnailExtension(string extension)
        => PreviewVideoExtensions.Contains(extension)
           || ThumbnailOnlyVideoExtensions.Contains(extension)
           || FfmpegImageExtensions.Contains(extension);

    public static bool IsPreviewExtension(string extension)
        => PreviewVideoExtensions.Contains(extension)
           || AudioExtensions.Contains(extension)
           || PreviewImageExtensions.Contains(extension)
           || FfmpegImageExtensions.Contains(extension);
}
