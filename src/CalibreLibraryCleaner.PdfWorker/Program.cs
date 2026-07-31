using CalibreLibraryCleaner.Infrastructure.Pdf;

if (args.Length != 1 || !string.Equals(args[0], "--stdio", StringComparison.Ordinal))
{
    return 2;
}

return await PdfInspectionWorkerHost.RunAsync(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    CancellationToken.None).ConfigureAwait(false);
