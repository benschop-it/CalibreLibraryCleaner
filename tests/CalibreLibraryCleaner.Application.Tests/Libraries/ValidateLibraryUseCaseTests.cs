using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class ValidateLibraryUseCaseTests
{
    [Fact]
    public async Task ExecuteReturnsStructuredValidationFailure()
    {
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        LibraryError error = new(LibraryErrorCode.EmptyPath, "Choose a folder.", "Browse for a library.");
        A.CallTo(() => resolver.ValidateAsync(null, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Failure(error));
        ValidateLibraryUseCase useCase = new(resolver);

        LibraryValidationOutcome outcome = await useCase.ExecuteAsync(null, CancellationToken.None);

        outcome.Error.Should().Be(error);
    }
}
