using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class QuarantineDestinationInspectorTests
{
    [Fact]
    public async Task AbsentFixedSiblingIsReportedWithoutCreation()
    {
        using Fixture fixture = new();
        QuarantineDestinationInspector inspector = new();

        QuarantineDestinationInspection result = await inspector.InspectAsync(
            fixture.Source,
            fixture.Destination,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.Identity!.Exists.Should().BeFalse();
        Directory.Exists(fixture.Destination).Should().BeFalse();
    }

    [Fact]
    public async Task ExistingSiblingReturnsExactLibraryIdentity()
    {
        using Fixture fixture = new();
        string expected = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        CreateMetadataDatabase(fixture.Destination, expected);
        QuarantineDestinationInspector inspector = new();

        QuarantineDestinationInspection result = await inspector.InspectAsync(
            fixture.Source,
            fixture.Destination,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.Identity.Should().BeEquivalentTo(new { Exists = true, LibraryUuid = expected });
    }

    [Fact]
    public async Task InvalidExistingIdentityIsRejected()
    {
        using Fixture fixture = new();
        CreateMetadataDatabase(fixture.Destination, "not-a-uuid");
        QuarantineDestinationInspector inspector = new();

        QuarantineDestinationInspection result = await inspector.InspectAsync(
            fixture.Source,
            fixture.Destination,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("QUARANTINE_DESTINATION.IDENTITY_INVALID");
    }

    private static void CreateMetadataDatabase(string root, string uuid)
    {
        Directory.CreateDirectory(root);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Path.Combine(root, "metadata.db"),
            Pooling = false,
        };
        using SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE library_id (uuid TEXT NOT NULL); INSERT INTO library_id VALUES ($uuid);";
        command.Parameters.AddWithValue("$uuid", uuid);
        command.ExecuteNonQuery();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "clqi" + Guid.NewGuid().ToString("N")[..6]);
            Source = Path.Combine(Root, "S");
            Destination = Path.Combine(Root, "S - Unresolvable");
            Directory.CreateDirectory(Source);
            File.WriteAllBytes(Path.Combine(Source, "metadata.db"), []);
        }

        public string Root { get; }
        public string Source { get; }
        public string Destination { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
