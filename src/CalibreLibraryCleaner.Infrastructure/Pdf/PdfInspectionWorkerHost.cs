using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CalibreLibraryCleaner.Application.Assessments.Pdf;

namespace CalibreLibraryCleaner.Infrastructure.Pdf;

public static class PdfInspectionWorkerHost
{
    public static async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using StreamReader reader = new(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using StreamWriter writer = new(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        PdfWorkerRequestMessage request;
        try
        {
            request = await PdfWorkerProtocol.ReadAsync<PdfWorkerRequestMessage>(
                reader,
                PdfInspectionLimits.V1.MaximumWorkerMessageBytes,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(request.ProtocolVersion, PdfWorkerProtocol.Version, StringComparison.Ordinal)
                || !string.Equals(request.Kind, "Request", StringComparison.Ordinal)
                || request.Request is null)
            {
                return 2;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or EndOfStreamException)
        {
            return 2;
        }

        PdfInspectionLimits limits = request.Request.Limits;
        PdfWorkerMessageBudget outputBudget = new(limits.MaximumWorkerMessageBytes);
        try
        {
            limits.Validate();
            using SemaphoreSlim writeGate = new(1, 1);
            int managedHeapWarning = 0;
            Channel<PdfInspectionProgress> progressChannel = Channel.CreateBounded<PdfInspectionProgress>(
                new BoundedChannelOptions(8)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
            Task progressWriter = DrainProgressAsync(
                progressChannel.Reader,
                writer,
                writeGate,
                limits,
                outputBudget,
                cancellationToken);
            SynchronousProgress<PdfInspectionProgress> progress = new(value =>
            {
                if (ManagedHeapWarningExceeded(limits.ManagedHeapWarningBytes))
                {
                    Interlocked.Exchange(ref managedHeapWarning, 1);
                }

                progressChannel.Writer.TryWrite(value with
                {
                    ResourceWarning = value.ResourceWarning || Volatile.Read(ref managedHeapWarning) != 0,
                });
            });
            PdfPigPdfInspector inspector = new();
            PdfInspectionResult result;
            try
            {
                result = await inspector.InspectAsync(
                    request.Request,
                    async (header, token) =>
                    {
                        await WriteOutputAsync(
                            writer,
                            writeGate,
                            new PdfWorkerOutputMessage(PdfWorkerProtocol.Version, "DocumentOpened", Header: header),
                            limits,
                            outputBudget,
                            token).ConfigureAwait(false);
                        PdfWorkerSelectionMessage selection = await PdfWorkerProtocol.ReadAsync<PdfWorkerSelectionMessage>(
                            reader,
                            limits.MaximumWorkerMessageBytes,
                            token).ConfigureAwait(false);
                        if (!string.Equals(selection.ProtocolVersion, PdfWorkerProtocol.Version, StringComparison.Ordinal)
                            || !string.Equals(selection.Kind, "PageSelection", StringComparison.Ordinal)
                            || selection.SelectedPages is null)
                        {
                            throw new InvalidDataException("The PDF worker protocol version is incompatible.");
                        }

                        return selection.SelectedPages;
                    },
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                progressChannel.Writer.TryComplete();
                await progressWriter.ConfigureAwait(false);
            }

            if (ManagedHeapWarningExceeded(limits.ManagedHeapWarningBytes))
            {
                Interlocked.Exchange(ref managedHeapWarning, 1);
            }

            if (Volatile.Read(ref managedHeapWarning) != 0)
            {
                result = result with { ResourceCountsWithinSoftLimits = false };
            }

            await WriteOutputAsync(
                writer,
                writeGate,
                new PdfWorkerOutputMessage(PdfWorkerProtocol.Version, "Completed", Result: result),
                limits,
                outputBudget,
                cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 3;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or EndOfStreamException or ArgumentException or PdfPageSelectionException)
        {
            await TryWriteFailureAsync(writer, PdfInspectionProblemCode.WorkerProtocolInvalid, limits.MaximumWorkerMessageBytes, outputBudget).ConfigureAwait(false);
            return 2;
        }
        catch
        {
            await TryWriteFailureAsync(writer, PdfInspectionProblemCode.WorkerCrashed, limits.MaximumWorkerMessageBytes, outputBudget).ConfigureAwait(false);
            return 1;
        }
    }

    internal static bool ManagedHeapWarningExceeded(long warningBytes) =>
        GC.GetTotalMemory(forceFullCollection: false) > warningBytes;

    private static async Task DrainProgressAsync(
        ChannelReader<PdfInspectionProgress> progress,
        StreamWriter writer,
        SemaphoreSlim writeGate,
        PdfInspectionLimits limits,
        PdfWorkerMessageBudget budget,
        CancellationToken cancellationToken)
    {
        await foreach (PdfInspectionProgress value in progress.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteOutputAsync(
                writer,
                writeGate,
                new PdfWorkerOutputMessage(PdfWorkerProtocol.Version, "Progress", Progress: value),
                limits,
                budget,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteOutputAsync(
        StreamWriter writer,
        SemaphoreSlim writeGate,
        PdfWorkerOutputMessage message,
        PdfInspectionLimits limits,
        PdfWorkerMessageBudget budget,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PdfWorkerProtocol.WriteAsync(
                writer,
                message,
                limits.MaximumWorkerMessageBytes,
                cancellationToken,
                budget).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task TryWriteFailureAsync(
        TextWriter writer,
        PdfInspectionProblemCode code,
        int maximumBytes,
        PdfWorkerMessageBudget budget)
    {
        try
        {
            await PdfWorkerProtocol.WriteAsync(
                writer,
                new PdfWorkerOutputMessage(PdfWorkerProtocol.Version, "Failed", FailureCode: code),
                maximumBytes,
                CancellationToken.None,
                budget).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
