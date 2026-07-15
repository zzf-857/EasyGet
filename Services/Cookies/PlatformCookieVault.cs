using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EasyGet.Services.Cookies;

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "EasyGet.PlatformCookieVault.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}

public sealed class PlatformCookieVault
{
    private readonly string _vaultDirectory;
    private readonly ISecretProtector _protector;

    public PlatformCookieVault()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyGet"),
            new DpapiSecretProtector())
    {
    }

    public PlatformCookieVault(string applicationDataRoot)
        : this(applicationDataRoot, new DpapiSecretProtector())
    {
    }

    internal PlatformCookieVault(string applicationDataRoot, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        ArgumentNullException.ThrowIfNull(protector);
        _vaultDirectory = Path.Combine(applicationDataRoot, "manual-cookies");
        _protector = protector;
    }

    public async Task SaveAsync(string platformId, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = GetPath(platformId);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_vaultDirectory);
        var temporaryPath = Path.Combine(
            _vaultDirectory,
            $"{platformId}-{Guid.NewGuid():N}.tmp");
        byte[]? plaintext = null;
        byte[]? ciphertext = null;

        try
        {
            plaintext = Encoding.UTF8.GetBytes(content);
            ciphertext = _protector.Protect(plaintext);
            await File.WriteAllBytesAsync(temporaryPath, ciphertext, cancellationToken);
            CookieFilePermissions.RestrictToCurrentUser(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public async Task<string?> LoadAsync(string platformId, CancellationToken cancellationToken)
    {
        var path = GetPath(platformId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return null;

        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = await File.ReadAllBytesAsync(path, cancellationToken);
            plaintext = _protector.Unprotect(ciphertext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task<bool> ExistsAsync(string platformId, CancellationToken cancellationToken)
    {
        var path = GetPath(platformId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(path));
    }

    public Task DeleteAsync(string platformId, CancellationToken cancellationToken)
    {
        var path = GetPath(platformId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        return Task.CompletedTask;
    }

    private string GetPath(string platformId)
    {
        CookieStorageKey.ValidatePlatformId(platformId);
        return Path.Combine(_vaultDirectory, $"{platformId}.bin");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
        }
    }
}
