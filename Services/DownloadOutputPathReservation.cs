using System.IO;

namespace EasyGet.Services;

internal sealed class DownloadOutputPathReservation : IDisposable
{
    private static readonly object ReservationLock = new();
    private static readonly HashSet<string> ReservedPaths = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private string? _reservedPath;

    private DownloadOutputPathReservation(string path)
    {
        Path = path;
        _reservedPath = path;
    }

    internal string Path { get; }

    internal static DownloadOutputPathReservation Reserve(
        string outputDirectory,
        string requestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedFileName);

        var fullOutputDirectory = System.IO.Path.GetFullPath(outputDirectory);
        var extension = System.IO.Path.GetExtension(requestedFileName);
        var fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(
            requestedFileName);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            fileNameWithoutExtension = "video";

        lock (ReservationLock)
        {
            for (var suffix = 1; ; suffix++)
            {
                var candidateFileName = suffix == 1
                    ? $"{fileNameWithoutExtension}{extension}"
                    : $"{fileNameWithoutExtension} ({suffix}){extension}";
                var candidatePath = System.IO.Path.Combine(
                    fullOutputDirectory,
                    candidateFileName);
                if (File.Exists(candidatePath)
                    || Directory.Exists(candidatePath)
                    || ReservedPaths.Contains(candidatePath))
                {
                    continue;
                }

                ReservedPaths.Add(candidatePath);
                return new DownloadOutputPathReservation(candidatePath);
            }
        }
    }

    public void Dispose()
    {
        var path = Interlocked.Exchange(ref _reservedPath, null);
        if (path is null)
            return;

        lock (ReservationLock)
            ReservedPaths.Remove(path);
    }
}
