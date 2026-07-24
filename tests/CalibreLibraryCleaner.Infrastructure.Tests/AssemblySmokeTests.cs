using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void InfrastructureAssemblyIsAvailable()
    {
        typeof(AssemblyMarker).Assembly.GetName().Name.Should().Be("CalibreLibraryCleaner.Infrastructure");
    }
}
