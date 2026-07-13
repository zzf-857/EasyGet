namespace EasyGet.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        DirectoryPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EasyGetTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string Path(params string[] segments)
    {
        var result = DirectoryPath;
        foreach (var segment in segments)
            result = System.IO.Path.Combine(result, segment);

        return result;
    }

    public void Touch(string relativePath)
    {
        var filePath = Path(relativePath);
        CreateParentDirectory(filePath);

        using var _ = File.Create(filePath);
    }

    public async Task WriteAsync(string relativePath, string content)
    {
        var filePath = Path(relativePath);
        CreateParentDirectory(filePath);
        await File.WriteAllTextAsync(filePath, content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CreateParentDirectory(string filePath)
    {
        var parentDirectory = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDirectory))
            Directory.CreateDirectory(parentDirectory);
    }
}
