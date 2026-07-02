using System.IO;

namespace EasyGet.Services;

internal static class MediaPreviewFileResolver
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv",
        ".mp3", ".m4a", ".wav", ".flac", ".aac", ".opus", ".ogg",
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    public static string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";

        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return path;

        try
        {
            var files = new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories);

            return files.Where(file => MediaExtensions.Contains(file.Extension)).MinBy(file => file.Name)?.FullName
                ?? files.Where(file => !file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)).MinBy(file => file.Name)?.FullName
                ?? files.MinBy(file => file.Name)?.FullName
                ?? path;
        }
        catch
        {
            return path;
        }
    }
}
