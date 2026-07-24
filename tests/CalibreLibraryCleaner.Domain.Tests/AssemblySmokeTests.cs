using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void DomainAssemblyIsAvailable()
    {
        typeof(AssemblyMarker).Assembly.GetName().Name.Should().Be("CalibreLibraryCleaner.Domain");
    }
}
