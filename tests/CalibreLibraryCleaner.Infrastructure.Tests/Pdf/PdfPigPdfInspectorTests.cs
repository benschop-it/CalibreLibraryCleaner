using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Infrastructure.Pdf;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Pdf;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class PdfPigPdfInspectorTests
{
    private readonly PdfPigPdfInspector inspector = new();

    [Fact]
    public async Task InspectAsyncReadsBoundedTextMetadataAndValidIsbnEvidence()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(text: string.Concat(
            Enumerable.Repeat("Synthetic document text ISBN 978-0-306-40615-7. ", 20)));

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.OpenStatus.Should().Be(PdfOpenStatus.Opened, System.Text.Json.JsonSerializer.Serialize(result));
        result.PageCount.Should().Be(1);
        result.Metadata.Title.Value.Should().Be("Synthetic PDF");
        result.PageFacts.Should().ContainSingle(page => page.Parsed && page.HasUsefulText && !page.FontEvidenceReliable);
        result.Identifiers.Should().Contain(identifier =>
            identifier.NormalizedIsbn == "9780306406157" && identifier.Source == PdfIdentifierSource.EarlyPageText);
        result.InvalidIdentifierCandidateCount.Should().Be(0);
        result.Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task InspectAsyncReportsImageOnlyPagesWithoutDecodingImageBytes()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(imageOnly: true);

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.PageFacts.Should().ContainSingle(page =>
            page.Parsed && page.ImageCount == 1 && page.IsImageDominant && page.UsefulCharacterCount == 0);
    }

    [Fact]
    public async Task InspectAsyncRecordsInvisibleTextRenderingAsLimitedEvidence()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateHiddenTextDocument();

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.PageFacts.Should().ContainSingle(page =>
            page.Parsed && page.UsefulCharacterCount > 0 && page.HiddenTextPresent);
    }

    [Fact]
    public async Task InspectAsyncValidatesMetadataAndEarlyPageIsbnChecksumsAndSources()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(
            title: "Metadata ISBN 0-306-40615-2",
            text: string.Concat(Enumerable.Repeat("Invalid early candidate 978-0-306-40615-8. ", 20)));

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.Identifiers.Should().ContainSingle(identifier =>
            identifier.NormalizedIsbn == "0306406152"
            && identifier.Source == PdfIdentifierSource.DocumentInformation
            && !identifier.PageNumber.HasValue);
        result.InvalidIdentifierCandidateCount.Should().BeGreaterThan(0);
        result.Identifiers.Should().NotContain(identifier => identifier.NormalizedIsbn == "9780306406158");
    }

    [Fact]
    public async Task InspectAsyncReportsMixedImageAndTextFactsAndDoesNotOpenLinks()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(addImage: true, addExternalLink: true);

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.PageFacts.Should().ContainSingle(page => page.HasUsefulText && page.IsImageDominant);
        result.ActiveContent.ExternalReferenceMarkers.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InspectAsyncOnlyReportsInertActionAndAttachmentMarkers()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateInertActiveContentDocument();

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.OpenStatus.Should().Be(PdfOpenStatus.Opened);
        result.ActiveContent.JavaScriptOrActionMarkers.Should().BeGreaterThan(0);
        result.ActiveContent.ExternalReferenceMarkers.Should().BeGreaterThan(0);
        result.ActiveContent.EmbeddedFileMarkers.Should().BeGreaterThan(0);
        Directory.GetFiles(fixture.Root).Should().ContainSingle(path => path == fixture.Path);
    }

    [Fact]
    public async Task InspectAsyncDoesNotTreatStreamTextAsStructuralObjectsOrActions()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateFakeStructuralMarkerStream();
        PdfInspectionLimits limits = PdfInspectionLimits.V1 with
        {
            MaximumObjects = 10,
            ObjectWarningCount = 10,
        };

        PdfInspectionResult result = await InspectAllAsync(fixture, limits);

        result.OpenStatus.Should().Be(PdfOpenStatus.Opened, System.Text.Json.JsonSerializer.Serialize(result));
        result.ObjectCount.Should().Be(4);
        result.ActiveContent.Should().Be(new PdfActiveContentSummary(0, 0, 0));
    }

    [Fact]
    public async Task InspectAsyncDistinguishesPresentAndAbsentOutlines()
    {
        using SyntheticPdfFixture withOutline = SyntheticPdfFixture.CreateOutlineDocument();
        using SyntheticPdfFixture withoutOutline = SyntheticPdfFixture.CreateText();

        PdfInspectionResult present = await InspectAllAsync(withOutline);
        PdfInspectionResult absent = await InspectAllAsync(withoutOutline);

        present.OutlinePresent.Should().BeTrue();
        present.OutlineEntryCount.Should().Be(1);
        absent.OutlinePresent.Should().BeFalse();
        absent.OutlineEntryCount.Should().Be(0);
    }

    [Fact]
    public async Task InspectAsyncReadsBoundedXmpAndRejectsDtdProcessing()
    {
        const string validXmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><identifier>978-0-306-40615-7</identifier></x:xmpmeta>";
        const string dtdXmp = "<!DOCTYPE x [<!ENTITY external SYSTEM 'https://invalid.example.test/never-read'>]><x>&external;</x>";
        string deepXmp = string.Concat(Enumerable.Repeat("<x>", 66)) + string.Concat(Enumerable.Repeat("</x>", 66));
        using SyntheticPdfFixture validFixture = SyntheticPdfFixture.CreateXmpDocument(validXmp);
        using SyntheticPdfFixture dtdFixture = SyntheticPdfFixture.CreateXmpDocument(dtdXmp);
        using SyntheticPdfFixture deepFixture = SyntheticPdfFixture.CreateXmpDocument(deepXmp);
        using SyntheticPdfFixture disallowedFixture = SyntheticPdfFixture.CreateXmpDocument(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><creator>978-0-306-40615-7</creator></x:xmpmeta>");

        PdfInspectionResult valid = await InspectAllAsync(validFixture);
        PdfInspectionResult dtd = await InspectAllAsync(dtdFixture);
        PdfInspectionResult deep = await InspectAllAsync(deepFixture);
        PdfInspectionResult disallowed = await InspectAllAsync(disallowedFixture);
        PdfInspectionResult oversized = await InspectAllAsync(validFixture, PdfInspectionLimits.V1 with
        {
            MaximumXmpBytes = 10,
            XmpWarningBytes = 10,
        });

        valid.Identifiers.Should().ContainSingle(identifier =>
            identifier.NormalizedIsbn == "9780306406157" && identifier.Source == PdfIdentifierSource.XmpMetadata);
        dtd.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.MalformedMetadata);
        deep.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.StackDepthExceeded);
        disallowed.Identifiers.Should().BeEmpty();
        oversized.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.MetadataLimitExceeded);
    }

    [Fact]
    public async Task InspectAsyncReportsMissingDocumentMetadataWithoutInventingValues()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(title: null, author: null);

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.Metadata.Title.Status.Should().Be(PdfMetadataValueStatus.Missing);
        result.Metadata.Author.Status.Should().Be(PdfMetadataValueStatus.Missing);
        result.Identifiers.Should().BeEmpty();
    }

    [Fact]
    public async Task InspectAsyncRejectsChecksumValidNonBookEan13Candidates()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(title: "Product EAN 4006381333931", text: "No identifier here.");

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.Identifiers.Should().BeEmpty();
        result.InvalidIdentifierCandidateCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InspectAsyncCountsOnlyLettersAndDigitsAndMarksTruncatedFingerprintsIncomplete()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(text: new string('!', 1_000));
        PdfInspectionLimits limits = PdfInspectionLimits.V1 with { MaximumFingerprintCharactersPerPage = 4 };

        PdfInspectionResult result = await InspectAllAsync(fixture, limits);

        PdfPageFacts page = result.PageFacts.Should().ContainSingle().Subject;
        page.UsefulCharacterCount.Should().BeLessThan(50);
        page.TextFingerprintComplete.Should().BeFalse();
        page.CountersCapped.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public async Task InspectAsyncTranslatesInvalidInputs(byte[] bytes, PdfInspectionProblemCode expected)
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateBytes(bytes);

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.Problems.Should().ContainSingle(problem => problem.Code == expected);
        result.OpenStatus.Should().NotBe(PdfOpenStatus.Opened);
    }

    public static TheoryData<byte[], PdfInspectionProblemCode> InvalidFiles => new()
    {
        { [], PdfInspectionProblemCode.ZeroLength },
        { "not a pdf"u8.ToArray(), PdfInspectionProblemCode.InvalidSignature },
        { "%PDF-1.7\n1 0 obj\n<<>>\nendobj"u8.ToArray(), PdfInspectionProblemCode.TruncatedFile },
        { "%PDF-1.7\nmalformed\nstartxref\n0\n%%EOF"u8.ToArray(), PdfInspectionProblemCode.MalformedStructure },
    };

    [Fact]
    public async Task InspectAsyncFailsClosedWhenTheExpectedHashDoesNotMatch()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        PdfInspectionRequest request = fixture.CreateRequest(sha256: new string('a', 64));

        PdfInspectionResult result = await inspector.InspectAsync(request, SelectAll, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.ChangedFile);
        result.PageFacts.Should().BeEmpty();
    }

    [Fact]
    public async Task InspectAsyncDiscardsFactsWhenFileIdentityChangesDuringInspection()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        PdfInspectionRequest request = fixture.CreateRequest();

        PdfInspectionResult result = await inspector.InspectAsync(
            request,
            (header, token) =>
            {
                token.ThrowIfCancellationRequested();
                File.SetAttributes(
                    fixture.Path,
                    File.GetAttributes(fixture.Path) ^ FileAttributes.NotContentIndexed);
                return ValueTask.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, header.PageCount).ToArray());
            },
            null,
            CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.ChangedFile);
        result.PageFacts.Should().BeEmpty();
    }

    [Fact]
    public void IsolatedInspectorRepeatsIdentityValidationImmediatelyBeforePublication()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        PdfInspectionRequest request = fixture.CreateRequest();
        PdfInspectionResult result = PdfInspectionResult.Failed(
            request.BookId, request.ExpectedRelativePath, PdfInspectionProblemCode.InvalidSignature);

        IsolatedPdfInspector.ParentIdentityMatches(request, result).Should().BeTrue();
        File.SetLastWriteTimeUtc(fixture.Path, request.Observation.LastWriteTimeUtc.UtcDateTime.AddSeconds(2));
        IsolatedPdfInspector.ParentIdentityMatches(request, result).Should().BeFalse();
    }

    [Fact]
    public void IsolatedInspectorPreservesWorkerReportedFileStateFailures()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        PdfInspectionRequest request = fixture.CreateRequest();
        PdfInspectionProblemCode[] fileStateProblems =
        [
            PdfInspectionProblemCode.MissingFile,
            PdfInspectionProblemCode.InaccessibleFile,
            PdfInspectionProblemCode.UnsafePath,
            PdfInspectionProblemCode.ChangedFile,
        ];

        foreach (PdfInspectionProblemCode problem in fileStateProblems)
        {
            PdfInspectionResult result = PdfInspectionResult.Failed(
                request.BookId, request.ExpectedRelativePath, problem);
            IsolatedPdfInspector.ParentIdentityMatches(request, result).Should().BeTrue();
        }
    }

    [Fact]
    public async Task InspectAsyncSeparatesMissingAndInaccessibleFiles()
    {
        using SyntheticPdfFixture missingFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionRequest missingRequest = missingFixture.CreateRequest();
        File.Delete(missingFixture.Path);
        PdfInspectionResult missing = await inspector.InspectAsync(missingRequest, SelectAll, null, CancellationToken.None);
        missing.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.MissingFile);

        if (OperatingSystem.IsWindows())
        {
            using SyntheticPdfFixture lockedFixture = SyntheticPdfFixture.CreateText();
            PdfInspectionRequest lockedRequest = lockedFixture.CreateRequest();
            await using FileStream locked = new(lockedFixture.Path, FileMode.Open, FileAccess.Read, FileShare.None);
            PdfInspectionResult inaccessible = await inspector.InspectAsync(lockedRequest, SelectAll, null, CancellationToken.None);
            inaccessible.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.InaccessibleFile);
        }
    }

    [Theory]
    [InlineData("../synthetic.pdf")]
    [InlineData("C:/synthetic.pdf")]
    public async Task InspectAsyncRejectsUnsafeRelativePaths(string relativePath)
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        PdfInspectionRequest request = fixture.CreateRequest() with { ExpectedRelativePath = relativePath };

        PdfInspectionResult result = await inspector.InspectAsync(request, SelectAll, null, CancellationToken.None);

        result.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.UnsafePath);
    }

    [Fact]
    public async Task InspectAsyncSeparatesPasswordProtectedZeroPageAndCyclicPageTreeFailures()
    {
        using SyntheticPdfFixture encrypted = SyntheticPdfFixture.CreatePasswordProtectedStub();
        PdfInspectionResult password = await InspectAllAsync(encrypted);
        password.EncryptionStatus.Should().Be(PdfEncryptionStatus.PasswordRequired);
        password.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.PasswordRequired);

        using SyntheticPdfFixture unsupportedEncryption = SyntheticPdfFixture.CreateUnsupportedEncryptionStub();
        PdfInspectionResult unsupported = await InspectAllAsync(unsupportedEncryption);
        unsupported.EncryptionStatus.Should().Be(PdfEncryptionStatus.UnsupportedEncryption);
        unsupported.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.UnsupportedEncryption);

        using SyntheticPdfFixture zeroPage = SyntheticPdfFixture.CreateZeroPageDocument();
        PdfInspectionResult empty = await InspectAllAsync(zeroPage);
        empty.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.ZeroPages);

        using SyntheticPdfFixture cyclic = SyntheticPdfFixture.CreateCyclicPageTree();
        PdfInspectionResult malformed = await InspectAllAsync(cyclic);
        malformed.Problems.Should().ContainSingle();
        malformed.Problems[0].Code.Should().BeOneOf(
            PdfInspectionProblemCode.MalformedStructure,
            PdfInspectionProblemCode.PageTreeUnavailable);
    }

    [Fact]
    public async Task InspectAsyncReportsEmptyPasswordEncryptionSeparatelyFromPasswordRequired()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateEmptyPasswordEncryptedStub();

        PdfInspectionResult result = await InspectAllAsync(fixture);

        result.OpenStatus.Should().Be(PdfOpenStatus.Opened);
        result.EncryptionStatus.Should().Be(PdfEncryptionStatus.EncryptedAccessibleWithEmptyPassword);
        result.PageFacts.Should().ContainSingle(page => page.Parsed);
    }

    [Fact]
    public async Task InspectAsyncFailsClosedAtPageObjectStreamDimensionImageAndOperationLimits()
    {
        using SyntheticPdfFixture twoPages = SyntheticPdfFixture.CreateText(pageCount: 2);
        PdfInspectionResult pageLimit = await InspectAllAsync(twoPages, PdfInspectionLimits.V1 with { MaximumPages = 1 });
        pageLimit.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.PageLimitExceeded);

        using SyntheticPdfFixture objectFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult objectLimit = await InspectAllAsync(objectFixture, PdfInspectionLimits.V1 with { MaximumObjects = 1, ObjectWarningCount = 1 });
        objectLimit.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.ObjectLimitExceeded);

        using SyntheticPdfFixture streamFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult streamLimit = await InspectAllAsync(streamFixture, PdfInspectionLimits.V1 with
        {
            MaximumEncodedStreamBytes = 1,
            EncodedStreamWarningBytes = 1,
        });
        streamLimit.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.StreamLimitExceeded);

        using SyntheticPdfFixture dimensionFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult dimensionLimit = await InspectAllAsync(dimensionFixture, PdfInspectionLimits.V1 with { MaximumPageDimensionPoints = 500, PageDimensionWarningPoints = 400 });
        dimensionLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.DimensionLimitExceeded);

        using SyntheticPdfFixture imageFixture = SyntheticPdfFixture.CreateText(addImage: true, imageCopies: 2);
        PdfInspectionResult imageLimit = await InspectAllAsync(imageFixture, PdfInspectionLimits.V1 with
        {
            MaximumImagesPerPage = 1,
            ImagesPerPageWarning = 1,
            MaximumImagesPerSample = 1,
            ImagesPerSampleWarning = 1,
        });
        imageLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.ImageLimitExceeded);

        using SyntheticPdfFixture pixelFixture = SyntheticPdfFixture.CreateText(
            addImage: true, imageWidthSamples: 2, imageHeightSamples: 1);
        PdfInspectionResult pixelLimit = await InspectAllAsync(pixelFixture, PdfInspectionLimits.V1 with
        {
            MaximumDeclaredPixelsPerImage = 1,
            DeclaredPixelsPerImageWarning = 1,
        });
        pixelLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.ImageLimitExceeded);

        PdfInspectionResult imageDimensionLimit = await InspectAllAsync(pixelFixture, PdfInspectionLimits.V1 with
        {
            MaximumImageDimensionSamples = 1,
            ImageDimensionSamplesWarning = 1,
        });
        imageDimensionLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.ImageLimitExceeded);

        using SyntheticPdfFixture operationFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult operationLimit = await InspectAllAsync(operationFixture, PdfInspectionLimits.V1 with
        {
            MaximumOperationsPerPage = 1,
            OperationsPerPageWarning = 1,
            MaximumOperationsPerSample = 1,
        });
        operationLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.OperationLimitExceeded);

        using SyntheticPdfFixture characterFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult characterLimit = await InspectAllAsync(characterFixture, PdfInspectionLimits.V1 with
        {
            MaximumUsefulCharactersPerPage = 1,
            UsefulCharactersPerPageWarning = 1,
            MaximumUsefulCharactersPerSample = 1,
            UsefulCharactersPerSampleWarning = 1,
        });
        characterLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.StreamLimitExceeded);

        using SyntheticPdfFixture outlineFixture = SyntheticPdfFixture.CreateTwoEntryOutlineDocument();
        PdfInspectionResult outlineLimit = await InspectAllAsync(outlineFixture, PdfInspectionLimits.V1 with
        {
            MaximumOutlineNodes = 1,
            OutlineNodeWarningCount = 1,
        });
        outlineLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.ObjectLimitExceeded);
    }

    [Fact]
    public async Task InspectAsyncFailsClosedAtFileMetadataAndDecodedStreamLimits()
    {
        using SyntheticPdfFixture fileFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult fileLimit = await InspectAllAsync(fileFixture, PdfInspectionLimits.V1 with
        {
            MaximumFileBytes = 1,
            FileWarningBytes = 1,
        });
        fileLimit.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.StreamLimitExceeded);

        using SyntheticPdfFixture metadataFixture = SyntheticPdfFixture.CreateText(title: new string('M', 100));
        PdfInspectionResult metadataLimit = await InspectAllAsync(metadataFixture, PdfInspectionLimits.V1 with
        {
            MaximumMetadataFieldBytes = 32,
            MetadataFieldWarningBytes = 16,
        });
        metadataLimit.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.MetadataLimitExceeded);

        using SyntheticPdfFixture decodedFixture = SyntheticPdfFixture.CreateText();
        PdfInspectionResult decodedLimit = await InspectAllAsync(decodedFixture, PdfInspectionLimits.V1 with
        {
            MaximumDecodedStreamBytes = 1,
            DecodedStreamWarningBytes = 1,
        });
        decodedLimit.Problems.Should().Contain(problem => problem.Code == PdfInspectionProblemCode.StreamLimitExceeded);
    }

    [Fact]
    public async Task InspectAsyncReportsSoftResourceThresholdsWithoutCappingFacts()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(addImage: true);
        PdfInspectionLimits limits = PdfInspectionLimits.V1 with
        {
            UsefulCharactersPerPageWarning = 1,
            UsefulCharactersPerSampleWarning = 1,
            ImagesPerPageWarning = 1,
            ImagesPerSampleWarning = 1,
            OperationsPerPageWarning = 1,
        };

        PdfInspectionResult result = await InspectAllAsync(fixture, limits);

        result.Problems.Should().BeEmpty();
        result.ResourceCountsWithinSoftLimits.Should().BeFalse();
        result.PageFacts.Should().ContainSingle(page => page.Parsed && page.ResourceHeavy && !page.CountersCapped);
    }

    [Fact]
    public async Task InspectAsyncUsesTheApplicationSelectedBoundedSampleForALargePdf()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(pageCount: 201);
        PdfPageSamplingPolicy sampling = new();

        PdfInspectionResult result = await inspector.InspectAsync(
            fixture.CreateRequest(),
            (header, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(sampling.Select(header.PageCount, header.OutlineTargetPages));
            },
            null,
            CancellationToken.None);

        result.OpenStatus.Should().Be(PdfOpenStatus.Opened);
        result.PageCount.Should().Be(201);
        result.SelectedPages.Should().HaveCount(200).And.OnlyHaveUniqueItems();
        result.PageFacts.Should().HaveCount(200);
    }

    [Fact]
    public async Task InspectAsyncHonorsCancellationBeforeOpeningTheFile()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> act = () => inspector.InspectAsync(fixture.CreateRequest(), SelectAll, null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InspectAsyncHonorsCancellationAtPageAnalysisBoundaries()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(pageCount: 10);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(value =>
        {
            if (value.Stage == "Pages" && value.CompletedPages == 1)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = () => inspector.InspectAsync(
            fixture.CreateRequest(), SelectAll, progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InspectAsyncDoesNotRetainFullPageTextInTheResultGraph()
    {
        const string sentinel = "FULL_TEXT_SENTINEL_SHOULD_NOT_ESCAPE";
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText(text: string.Concat(Enumerable.Repeat(sentinel, 30)));

        PdfInspectionResult result = await InspectAllAsync(fixture);
        string serialized = System.Text.Json.JsonSerializer.Serialize(result);

        serialized.Should().NotContain(sentinel);
    }

    [Fact]
    public async Task IsolatedInspectorCompletesTheVersionedWorkerHandshake()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        string worker = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CalibreLibraryCleaner.PdfWorker",
            "bin",
            "Debug",
            "net10.0",
            OperatingSystem.IsWindows() ? "CalibreLibraryCleaner.PdfWorker.exe" : "CalibreLibraryCleaner.PdfWorker");
        IsolatedPdfInspector isolated = new(new PdfWorkerOptions { ExecutablePath = worker });

        PdfInspectionResult result = await isolated.InspectAsync(
            fixture.CreateRequest(),
            SelectAll,
            null,
            CancellationToken.None);

        result.OpenStatus.Should().Be(PdfOpenStatus.Opened, System.Text.Json.JsonSerializer.Serialize(result));
        result.PageFacts.Should().ContainSingle(page => page.Parsed && page.HasUsefulText);
    }

    [Fact]
    public async Task IsolatedInspectorFailsClosedWhenTheFixedWorkerIsMissing()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        IsolatedPdfInspector isolated = new(new PdfWorkerOptions
        {
            ExecutablePath = Path.Combine(fixture.Root, "missing-worker.exe"),
        });

        PdfInspectionResult result = await isolated.InspectAsync(
            fixture.CreateRequest(),
            SelectAll,
            null,
            CancellationToken.None);

        result.OpenStatus.Should().Be(PdfOpenStatus.WorkerFailed);
        result.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.WorkerCrashed);
    }

    [Fact]
    public async Task IsolatedInspectorFailsClosedWhenWindowsJobAssignmentFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        string worker = Path.Combine(
            FindRepositoryRoot(), "src", "CalibreLibraryCleaner.PdfWorker", "bin", "Debug", "net10.0",
            "CalibreLibraryCleaner.PdfWorker.exe");
        IsolatedPdfInspector isolated = new(new PdfWorkerOptions
        {
            ExecutablePath = worker,
            JobObjectFactory = static (_, _) => null,
        });

        PdfInspectionResult result = await isolated.InspectAsync(
            fixture.CreateRequest(), SelectAll, null, CancellationToken.None);

        result.OpenStatus.Should().Be(PdfOpenStatus.WorkerFailed);
        result.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.WorkerCrashed);
    }

    [Fact]
    public void IsolatedInspectorEnforcesCpuAndWorkingSetHardLimits()
    {
        using System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();
        while (current.TotalProcessorTime <= TimeSpan.FromSeconds(1))
        {
            Thread.SpinWait(10_000);
            current.Refresh();
        }

        PdfInspectionProblemCode? cpu = IsolatedPdfInspector.CheckResources(
            current,
            PdfInspectionLimits.V1 with { CpuTimeSeconds = 1, CpuTimeWarningSeconds = 1 });
        PdfInspectionProblemCode? memory = IsolatedPdfInspector.CheckResources(
            current,
            PdfInspectionLimits.V1 with
            {
                CpuTimeSeconds = int.MaxValue,
                CpuTimeWarningSeconds = int.MaxValue,
                WorkingSetBytes = 1,
                WorkingSetWarningBytes = 1,
            });

        cpu.Should().Be(PdfInspectionProblemCode.CpuLimitExceeded);
        memory.Should().Be(PdfInspectionProblemCode.MemoryLimitExceeded);
    }

    [Fact]
    public async Task IsolatedInspectorTerminatesAWorkerAtTheWallTimeLimit()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        string helper = FindTestCalibre();
        string control = Path.Combine(Path.GetDirectoryName(helper)!, "calibre-test-control.json");
        int before = System.Diagnostics.Process.GetProcessesByName("CalibreLibraryCleaner.TestCalibre").Length;
        File.WriteAllText(control, "{\"SleepMilliseconds\":10000}");
        try
        {
            IsolatedPdfInspector isolated = new(new PdfWorkerOptions { ExecutablePath = helper });
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

            PdfInspectionResult result = await isolated.InspectAsync(
                fixture.CreateRequest(PdfInspectionLimits.V1 with { WallTimeSeconds = 1, WallTimeWarningSeconds = 1 }),
                SelectAll,
                null,
                CancellationToken.None);

            elapsed.Stop();
            result.OpenStatus.Should().Be(PdfOpenStatus.TimedOut);
            result.Problems.Should().ContainSingle(problem => problem.Code == PdfInspectionProblemCode.ParserTimeout);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            await Task.Delay(250);
            System.Diagnostics.Process.GetProcessesByName("CalibreLibraryCleaner.TestCalibre").Length.Should().Be(before);
        }
        finally
        {
            File.Delete(control);
        }
    }

    [Fact]
    public async Task IsolatedInspectorTerminatesTheWorkerWhenTheCallerCancels()
    {
        using SyntheticPdfFixture fixture = SyntheticPdfFixture.CreateText();
        string helper = FindTestCalibre();
        string control = Path.Combine(Path.GetDirectoryName(helper)!, "calibre-test-control.json");
        int before = System.Diagnostics.Process.GetProcessesByName("CalibreLibraryCleaner.TestCalibre").Length;
        File.WriteAllText(control, "{\"SleepMilliseconds\":10000}");
        try
        {
            IsolatedPdfInspector isolated = new(new PdfWorkerOptions { ExecutablePath = helper });
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250));
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

            Func<Task> act = () => isolated.InspectAsync(
                fixture.CreateRequest(PdfInspectionLimits.V1 with { WallTimeSeconds = 10, WallTimeWarningSeconds = 9 }),
                SelectAll,
                null,
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            elapsed.Stop();
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            System.Diagnostics.Process.GetProcessesByName("CalibreLibraryCleaner.TestCalibre").Length.Should().Be(before);
        }
        finally
        {
            File.Delete(control);
        }
    }

    private async Task<PdfInspectionResult> InspectAllAsync(SyntheticPdfFixture fixture, PdfInspectionLimits? limits = null) =>
        await inspector.InspectAsync(fixture.CreateRequest(limits), SelectAll, null, CancellationToken.None);

    private static ValueTask<IReadOnlyList<int>> SelectAll(PdfDocumentHeaderFacts header, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, header.PageCount).ToArray());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CalibreLibraryCleaner.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string FindTestCalibre() => Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "CalibreLibraryCleaner.TestCalibre",
        "bin",
        "Debug",
        "net10.0",
        OperatingSystem.IsWindows() ? "CalibreLibraryCleaner.TestCalibre.exe" : "CalibreLibraryCleaner.TestCalibre");

    private sealed class InlineProgress(Action<PdfInspectionProgress> report) : IProgress<PdfInspectionProgress>
    {
        public void Report(PdfInspectionProgress value) => report(value);
    }
}
