using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class OnlineMetadataSettingsViewModelTests
{
    [Fact]
    public async Task SaveReplaceAndClearExposeStatusButNeverPlaintext()
    {
        const string apiKey = "AIzaFakePrivateKey_1234567890";
        IGoogleBooksApiKeyStore store = A.Fake<IGoogleBooksApiKeyStore>();
        A.CallTo(() => store.IsConfiguredAsync(A<CancellationToken>._)).Returns(false);
        OnlineMetadataSettingsViewModel viewModel = new(store);

        await viewModel.InitializeAsync();

        viewModel.IsGoogleBooksConfigured.Should().BeFalse();
        viewModel.SaveActionLabel.Should().Be("_Save key");

        await viewModel.SaveAsync(apiKey);

        viewModel.IsGoogleBooksConfigured.Should().BeTrue();
        viewModel.SaveActionLabel.Should().Be("_Replace key");
        viewModel.StatusMessage.Should().Contain("Windows current-user protection");
        A.CallTo(() => store.SaveAsync(apiKey, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        PublicStringValues(viewModel).Should().NotContain(value => value.Contains(apiKey, StringComparison.Ordinal));

        await viewModel.ClearAsync();

        viewModel.IsGoogleBooksConfigured.Should().BeFalse();
        viewModel.SaveActionLabel.Should().Be("_Save key");
        viewModel.StatusMessage.Should().Contain("Open Library remains available independently");
        A.CallTo(() => store.ClearAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExistingProtectedKeyShowsReplaceWithoutReadingPlaintext()
    {
        IGoogleBooksApiKeyStore store = A.Fake<IGoogleBooksApiKeyStore>();
        A.CallTo(() => store.IsConfiguredAsync(A<CancellationToken>._)).Returns(true);
        OnlineMetadataSettingsViewModel viewModel = new(store);

        await viewModel.InitializeAsync();

        viewModel.IsGoogleBooksConfigured.Should().BeTrue();
        viewModel.SaveActionLabel.Should().Be("_Replace key");
        A.CallTo(() => store.ReadAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task StorageFailureShowsGenericMessageWithoutPlaintextOrExceptionDetails()
    {
        const string apiKey = "AIzaFakePrivateKey_1234567890";
        IGoogleBooksApiKeyStore store = A.Fake<IGoogleBooksApiKeyStore>();
        A.CallTo(() => store.SaveAsync(apiKey, A<CancellationToken>._))
            .ThrowsAsync(new GoogleBooksApiKeyStorageException());
        OnlineMetadataSettingsViewModel viewModel = new(store);

        await viewModel.SaveAsync(apiKey);

        viewModel.StatusMessage.Should().Be(
            "The Google Books API key could not be saved with Windows protection.");
        viewModel.StatusMessage.Should().NotContain(apiKey).And.NotContain("unavailable");
        viewModel.IsGoogleBooksConfigured.Should().BeFalse();
    }

    private static IEnumerable<string> PublicStringValues(OnlineMetadataSettingsViewModel value) =>
        value.GetType().GetProperties()
            .Where(property => property.PropertyType == typeof(string) && property.CanRead)
            .Select(property => (string?)property.GetValue(value))
            .Where(propertyValue => propertyValue is not null)
            .Select(propertyValue => propertyValue!);
}
