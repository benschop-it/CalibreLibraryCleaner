using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentGroup
{
    public const string Name = "Controlled process environment";
}
