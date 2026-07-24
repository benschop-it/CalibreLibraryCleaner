using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void ApplicationAssemblyIsAvailable()
    {
        typeof(AssemblyMarker).Assembly.GetName().Name.Should().Be("CalibreLibraryCleaner.Application");
    }
}
