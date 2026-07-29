namespace EasyGet.Services;

public sealed record VideoFormatInfo(
    string FormatId,
    string Extension,
    string VideoCodec,
    string AudioCodec,
    int Width,
    int Height,
    double FramesPerSecond,
    double TotalBitrateKilobytesPerSecond,
    double AudioBitrateKilobytesPerSecond,
    long FileSize,
    string FormatNote)
{
    public bool HasVideo => !IsMissingCodec(VideoCodec);
    public bool HasAudio => !IsMissingCodec(AudioCodec);
    public bool IsCombined => HasVideo && HasAudio;

    private static bool IsMissingCodec(string value)
        => string.IsNullOrWhiteSpace(value)
           || value.Equals("none", StringComparison.OrdinalIgnoreCase);
}
