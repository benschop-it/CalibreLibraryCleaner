using System.Text;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class WindowsProtectedGoogleBooksApiKeyStoreTests
{
    [Fact]
    public async Task SaveReplaceAndClearPersistOnlyCurrentUserCiphertext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        WindowsProtectedGoogleBooksApiKeyStore store = new(new(directory.Path));
        const string first = "AIzaFirstPrivateKey_1234567890";
        const string replacement = "AIzaReplacementPrivateKey_9876543210";

        await store.SaveAsync(first, CancellationToken.None);

        (await store.IsConfiguredAsync(CancellationToken.None)).Should().BeTrue();
        (await store.ReadAsync(CancellationToken.None)).Should().Be(first);
        string path = Directory.GetFiles(directory.Path).Single();
        string persisted = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        persisted.Should().NotContain(first);
        persisted.Should().NotContain(replacement);

        await store.SaveAsync(replacement, CancellationToken.None);
        (await store.ReadAsync(CancellationToken.None)).Should().Be(replacement);
        Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path)).Should().NotContain(replacement);

        await store.ClearAsync(CancellationToken.None);
        (await store.IsConfiguredAsync(CancellationToken.None)).Should().BeFalse();
        Directory.GetFiles(directory.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task CorruptCiphertextIsTreatedAsNotConfigured()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        WindowsProtectedGoogleBooksApiKeyStore store = new(new(directory.Path));
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "google-books-api-key.bin"),
            [1, 2, 3, 4]);

        (await store.ReadAsync(CancellationToken.None)).Should().BeNull();
        (await store.IsConfiguredAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidKeyIsRejectedBeforeAnyFileIsWritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        WindowsProtectedGoogleBooksApiKeyStore store = new(new(directory.Path));

        Func<Task> act = () => store.SaveAsync("contains spaces and ?", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        Directory.GetFiles(directory.Path).Should().BeEmpty();
    }
}
