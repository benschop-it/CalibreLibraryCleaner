using CalibreLibraryCleaner.Application.Assessments.Pdf;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace CalibreLibraryCleaner.Infrastructure.Pdf;

internal sealed class QuotaFilterProvider(PdfInspectionLimits limits) : IFilterProvider
{
    private readonly IFilterProvider inner = DefaultFilterProvider.Instance;
    private long aggregateDecodedBytes;
    private int softLimitExceeded;

    public long AggregateDecodedBytes => Interlocked.Read(ref aggregateDecodedBytes);
    public bool SoftLimitExceeded => Volatile.Read(ref softLimitExceeded) != 0;

    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(inner.GetFilters(dictionary));

    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(inner.GetNamedFilters(names));

    public IReadOnlyList<IFilter> GetAllFilters() => Wrap(inner.GetAllFilters());

    private IFilter[] Wrap(IReadOnlyList<IFilter> filters) =>
        filters.Select(filter => (IFilter)new QuotaFilter(filter, this, limits)).ToArray();

    private sealed class QuotaFilter(IFilter inner, QuotaFilterProvider owner, PdfInspectionLimits limits) : IFilter
    {
        public bool IsSupported => inner.IsSupported;

        public Memory<byte> Decode(
            Memory<byte> input,
            DictionaryToken streamDictionary,
            IFilterProvider filterProvider,
            int filterIndex)
        {
            if (!inner.IsSupported)
            {
                throw new PdfUnsupportedFilterException();
            }

            if (input.Length > limits.MaximumEncodedStreamBytes)
            {
                throw new PdfInspectionLimitException(PdfInspectionProblemCode.StreamLimitExceeded);
            }

            if (input.Length > limits.EncodedStreamWarningBytes)
            {
                Volatile.Write(ref owner.softLimitExceeded, 1);
            }

            Memory<byte> decoded = inner.Decode(input, streamDictionary, owner, filterIndex);
            if (decoded.Length > limits.MaximumDecodedStreamBytes)
            {
                throw new PdfInspectionLimitException(PdfInspectionProblemCode.StreamLimitExceeded);
            }

            long total = Interlocked.Add(ref owner.aggregateDecodedBytes, decoded.Length);
            if (total > limits.MaximumAggregateDecodedBytes)
            {
                throw new PdfInspectionLimitException(PdfInspectionProblemCode.StreamLimitExceeded);
            }

            if (decoded.Length > limits.DecodedStreamWarningBytes
                || total > limits.AggregateDecodedWarningBytes)
            {
                Volatile.Write(ref owner.softLimitExceeded, 1);
            }

            return decoded;
        }
    }
}

internal sealed class PdfInspectionLimitException(PdfInspectionProblemCode code) : IOException
{
    public PdfInspectionProblemCode Code { get; } = code;
}

internal sealed class PdfUnsupportedFilterException : IOException
{
}
