using CalibreLibraryCleaner.Infrastructure.Pdf;

// Enable legacy single-byte code pages (e.g. windows-1252) so any text decoded by
// this worker honors a declared non-Unicode encoding. Registration is process-global
// and idempotent.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

if (args.Length != 1 || !string.Equals(args[0], "--stdio", StringComparison.Ordinal))
{
    return 2;
}

return await PdfInspectionWorkerHost.RunAsync(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    CancellationToken.None).ConfigureAwait(false);
