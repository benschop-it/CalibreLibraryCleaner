using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Assessments;

public sealed record EpubContentSignatureLimits
{
    public EpubContentSignatureLimits(
        int landmarkCount = 12,
        int tokensPerLandmark = 64,
        long maximumTokenCount = 5_000_000,
        int minimumDistinctTokensPerLandmark = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(landmarkCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(landmarkCount, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokensPerLandmark, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokensPerLandmark, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTokenCount, tokensPerLandmark);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumTokenCount, 20_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumDistinctTokensPerLandmark, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumDistinctTokensPerLandmark, tokensPerLandmark);
        LandmarkCount = landmarkCount;
        TokensPerLandmark = tokensPerLandmark;
        MaximumTokenCount = maximumTokenCount;
        MinimumDistinctTokensPerLandmark = minimumDistinctTokensPerLandmark;
    }

    public static EpubContentSignatureLimits V1 { get; } = new();
    public int LandmarkCount { get; }
    public int TokensPerLandmark { get; }
    public long MaximumTokenCount { get; }
    public int MinimumDistinctTokensPerLandmark { get; }
}

public sealed record EpubContentSignatureRequest
{
    public EpubContentSignatureRequest(
        EpubInspectionRequest source,
        EpubContentSignatureLimits? signatureLimits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        SignatureLimits = signatureLimits ?? EpubContentSignatureLimits.V1;
    }

    public EpubInspectionRequest Source { get; }
    public EpubContentSignatureLimits SignatureLimits { get; }
}

public enum EpubContentSignatureProblemCode
{
    CannotOpen,
    UnsafeArchive,
    PackageMalformed,
    Unsupported,
    Encrypted,
    ChangedDuringInspection,
    LimitExceeded,
    Unreadable,
    InsufficientText,
}

public sealed record EpubContentSignatureResult
{
    private EpubContentSignatureResult(
        EpubContentSignature? signature,
        EpubContentSignatureProblemCode? problemCode)
    {
        if ((signature is null) == (problemCode is null))
            throw new ArgumentException("A content signature result must contain either a signature or a problem code.");
        Signature = signature;
        ProblemCode = problemCode;
    }

    public bool IsSuccess => Signature is not null;
    public EpubContentSignature? Signature { get; }
    public EpubContentSignatureProblemCode? ProblemCode { get; }
    public static EpubContentSignatureResult Success(EpubContentSignature signature) => new(signature, null);
    public static EpubContentSignatureResult Failure(EpubContentSignatureProblemCode problemCode) => new(null, problemCode);
}

public sealed record EpubContentSignatureProgress(string Stage, int CompletedUnits, int? TotalUnits);
