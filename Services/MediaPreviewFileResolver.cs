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
            string? bestMedia = null;
            string? bestNonText = null;
            string? bestAny = null;

            foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                bestAny = PreferEarlierFileName(filePath, bestAny);

                var extension = Path.GetExtension(filePath);
                if (MediaExtensions.Contains(extension))
                {
                    bestMedia = PreferEarlierFileName(filePath, bestMedia);
                }
                else if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    bestNonText = PreferEarlierFileName(filePath, bestNonText);
                }
            }

            return bestMedia
                ?? bestNonText
                ?? bestAny
                ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static string PreferEarlierFileName(string candidate, string? current)
    {
        if (current is null)
            return candidate;

        return Comparer<string>.Default.Compare(Path.GetFileName(candidate), Path.GetFileName(current)) < 0
            ? candidate
            : current;
    }
}
