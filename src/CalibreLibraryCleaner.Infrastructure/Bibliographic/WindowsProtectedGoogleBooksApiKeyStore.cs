using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

internal sealed class WindowsProtectedGoogleBooksApiKeyStore(GoogleBooksApiKeyStoreOptions options) :
    IGoogleBooksApiKeyStore
{
    private const string FileName = "google-books-api-key.bin";
    private const int MaximumProtectedBytes = 4 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "CalibreLibraryCleaner.GoogleBooksApiKey.v1");

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        await ReadAsync(cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string root = GetStorageRoot();
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path) || !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _)) return null;
            FileInfo info = new(path);
            if (info.Length is <= 0 or > MaximumProtectedBytes) return null;
            byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] clearBytes = ProtectedData.Unprotect(
                protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                string value = Encoding.UTF8.GetString(clearBytes);
                return IsValid(value) ? value : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(apiKey))
            throw new ArgumentException("The Google Books API key is invalid.", nameof(apiKey));
        try
        {
            await SaveCoreAsync(apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new GoogleBooksApiKeyStorageException();
        }
    }

    private async Task SaveCoreAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string root = GetStorageRoot();
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            root = GetStorageRoot();
        }
        string path = Path.Combine(root, FileName);
        if (File.Exists(path) && !ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _))
            throw new IOException("The Google Books credential file is not physical.");
        byte[] clearBytes = Encoding.UTF8.GetBytes(apiKey);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(
                clearBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
        if (protectedBytes.Length > MaximumProtectedBytes)
            throw new InvalidDataException("The protected Google Books credential exceeds its bound.");
        string temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = GetStorageRoot();
            string path = Path.Combine(root, FileName);
            if (File.Exists(path) && ExecutionPathGuard.TryRejectReparsePointLeaf(path, true, out _))
                File.Delete(path);
            return Task.CompletedTask;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new GoogleBooksApiKeyStorageException();
        }
    }

    private string GetStorageRoot()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StorageRoot));
        bool exists = Directory.Exists(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, exists, out _))
            throw new IOException("The Google Books credential directory is not physical.");
        return root;
    }

    private static bool IsValid(string value) => value is { Length: >= 16 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsStorageFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or CryptographicException
        or InvalidDataException
        or PlatformNotSupportedException
        or ArgumentException
        or OverflowException;
}
