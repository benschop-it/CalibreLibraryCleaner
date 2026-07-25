using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

public sealed class RecoveryPlanJsonStoreTests
{
    [Fact]
    public async Task ImmutablePlanRoundTripsCreateNewWithCanonicalDigest()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(outside);
        string path = Path.Combine(outside, "plan.recovery-plan.json");
        using ServiceProvider provider = Provider();
        IRecoveryPlanStore store = provider.GetRequiredService<IRecoveryPlanStore>();
        RecoveryPlan plan = InfrastructureRecoveryTestData.Plan(approved: true);

        RecoveryPlanStoreResult written = await store.WriteCreateNewAsync(
            plan, path, library, CancellationToken.None);
        RecoveryPlanStoreResult read = await store.ReadAsync(
            path, library, CancellationToken.None);

        read.IsSuccess.Should().BeTrue(string.Join(" | ",
            read.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        written.IsSuccess.Should().BeTrue(string.Join(" | ",
            written.Issues.Select(value => $"{value.Code}: {value.Explanation}")));
        read.Plan!.ContentDigest.Should().Be(plan.ContentDigest);
        read.Plan.ArtifactRevision.Should().Be(plan.ArtifactRevision);
        read.Plan.State.Should().Be(RecoveryPlanState.Approved);
    }

    [Fact]
    public async Task ExistingOrTamperedPlanIsRejectedWithoutOverwrite()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(outside);
        string path = Path.Combine(outside, "plan.recovery-plan.json");
        await File.WriteAllTextAsync(path, "existing");
        byte[] existing = await File.ReadAllBytesAsync(path);
        using ServiceProvider provider = Provider();

        RecoveryPlanStoreResult result = await provider.GetRequiredService<IRecoveryPlanStore>()
            .WriteCreateNewAsync(InfrastructureRecoveryTestData.Plan(), path, library,
                CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await File.ReadAllBytesAsync(path)).Should().Equal(existing);
    }

    [Fact]
    public async Task PlanInsideLibraryFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        using ServiceProvider provider = Provider();

        RecoveryPlanStoreResult result = await provider.GetRequiredService<IRecoveryPlanStore>()
            .WriteCreateNewAsync(InfrastructureRecoveryTestData.Plan(),
                Path.Combine(library, "plan.recovery-plan.json"), library,
                CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.PLAN_PATH_UNSAFE");
    }

    private static ServiceProvider Provider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        return services.BuildServiceProvider();
    }
}
