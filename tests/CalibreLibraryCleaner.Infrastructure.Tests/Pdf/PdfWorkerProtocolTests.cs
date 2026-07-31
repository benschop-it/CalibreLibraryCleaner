using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Infrastructure.Pdf;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Pdf;

public sealed class PdfWorkerProtocolTests
{
    [Fact]
    public async Task ReadAsyncRejectsDuplicatePropertiesAtAnyDepth()
    {
        using StringReader reader = new("{\"protocolVersion\":\"one\",\"kind\":\"Request\",\"request\":{\"limits\":{},\"limits\":{}}}\n");

        Func<Task> act = () => PdfWorkerProtocol.ReadAsync<PdfWorkerRequestMessage>(reader, 4_096, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsyncRejectsUnknownPropertiesAndInvalidJson()
    {
        using StringReader unknown = new("{\"protocolVersion\":\"one\",\"kind\":\"PageSelection\",\"selectedPages\":[],\"unexpected\":true}\n");
        using StringReader malformed = new("{\n");

        Func<Task> unknownAct = () => PdfWorkerProtocol.ReadAsync<PdfWorkerSelectionMessage>(unknown, 4_096, CancellationToken.None);
        Func<Task> malformedAct = () => PdfWorkerProtocol.ReadAsync<PdfWorkerSelectionMessage>(malformed, 4_096, CancellationToken.None);

        await unknownAct.Should().ThrowAsync<JsonException>();
        await malformedAct.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task ReadAndWriteRejectMessagesBeyondTheByteLimit()
    {
        using StringReader reader = new("{\"value\":\"too-large\"}\n");
        using StringWriter writer = new();

        Func<Task> read = () => PdfWorkerProtocol.ReadAsync<Dictionary<string, string>>(reader, 10, CancellationToken.None);
        Func<Task> write = () => PdfWorkerProtocol.WriteAsync(writer, new { Value = "too-large" }, 10, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidDataException>();
        await write.Should().ThrowAsync<InvalidDataException>();

        using StringWriter budgetWriter = new();
        PdfWorkerMessageBudget budget = new(1);
        Func<Task> budgetedWrite = () => PdfWorkerProtocol.WriteAsync(
            budgetWriter, new { Value = "x" }, 4_096, CancellationToken.None, budget);
        await budgetedWrite.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsyncEnforcesOneCumulativeBudgetAcrossMessages()
    {
        using StringReader reader = new("{}\n{}\n");
        PdfWorkerMessageBudget budget = new(3);

        await PdfWorkerProtocol.ReadAsync<Dictionary<string, string>>(
            reader, 4_096, CancellationToken.None, budget: budget);
        Func<Task> second = () => PdfWorkerProtocol.ReadAsync<Dictionary<string, string>>(
            reader, 4_096, CancellationToken.None, budget: budget);

        await second.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAndWriteReportTheBoundedUtf8MessageSize()
    {
        using StringWriter writer = new();
        int writtenBytes = 0;
        await PdfWorkerProtocol.WriteAsync(
            writer,
            new { Value = "synthetic" },
            4_096,
            CancellationToken.None,
            observeByteCount: bytes => writtenBytes = bytes);
        using StringReader reader = new(writer.ToString());
        int readBytes = 0;

        Dictionary<string, string> result = await PdfWorkerProtocol.ReadAsync<Dictionary<string, string>>(
            reader,
            4_096,
            CancellationToken.None,
            bytes => readBytes = bytes);

        result.Should().Contain("value", "synthetic");
        readBytes.Should().Be(writtenBytes).And.BeGreaterThan(0);
    }

    [Fact]
    public void WorkerManagedHeapWarningUsesTheConfiguredSoftThreshold()
    {
        PdfInspectionWorkerHost.ManagedHeapWarningExceeded(1).Should().BeTrue();
        PdfInspectionWorkerHost.ManagedHeapWarningExceeded(long.MaxValue).Should().BeFalse();
    }

    [Fact]
    public async Task WorkerRejectsAnUnknownMessageKind()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        using StringWriter payload = new();
        await PdfWorkerProtocol.WriteAsync(
            payload,
            new PdfWorkerRequestMessage(PdfWorkerProtocol.Version, "Unknown", fixture.CreateRequest()),
            PdfInspectionLimits.V1.MaximumWorkerMessageBytes,
            CancellationToken.None);
        using MemoryStream input = new(Encoding.UTF8.GetBytes(payload.ToString()));
        using MemoryStream output = new();

        int exitCode = await PdfInspectionWorkerHost.RunAsync(input, output, CancellationToken.None);

        exitCode.Should().Be(2);
    }

    [Fact]
    public async Task WorkerRejectsAnOutOfRangePageSelectionWithABoundedFailure()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        using StringWriter payload = new();
        await PdfWorkerProtocol.WriteAsync(
            payload,
            new PdfWorkerRequestMessage(PdfWorkerProtocol.Version, "Request", fixture.CreateRequest()),
            PdfInspectionLimits.V1.MaximumWorkerMessageBytes,
            CancellationToken.None);
        await PdfWorkerProtocol.WriteAsync(
            payload,
            new PdfWorkerSelectionMessage(PdfWorkerProtocol.Version, "PageSelection", [2]),
            PdfInspectionLimits.V1.MaximumWorkerMessageBytes,
            CancellationToken.None);
        using MemoryStream input = new(Encoding.UTF8.GetBytes(payload.ToString()));
        using MemoryStream output = new();

        int exitCode = await PdfInspectionWorkerHost.RunAsync(input, output, CancellationToken.None);
        string response = Encoding.UTF8.GetString(output.ToArray());

        exitCode.Should().Be(2);
        response.Should().Contain("\"kind\":\"DocumentOpened\"");
        response.Should().Contain("\"kind\":\"Failed\"");
        response.Should().NotContain(fixture.Path);
    }
}
