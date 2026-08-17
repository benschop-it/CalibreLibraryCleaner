using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    private const string ApplicationProject = "CalibreLibraryCleaner.Application";
    private const string DomainProject = "CalibreLibraryCleaner.Domain";
    private const string InfrastructureProject = "CalibreLibraryCleaner.Infrastructure";
    private const string WpfProject = "CalibreLibraryCleaner.Wpf";
    private const string PdfWorkerProject = "CalibreLibraryCleaner.PdfWorker";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { DomainProject, [] },
        { ApplicationProject, [DomainProject] },
        { InfrastructureProject, [ApplicationProject] },
        { WpfProject, [ApplicationProject, InfrastructureProject] },
        { PdfWorkerProject, [InfrastructureProject] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void ProductionProjectsDeclareOnlyAllowedProjectReferences(
        string projectName,
        string[] expectedReferences)
    {
        string[] actualReferences = ReadItemNames(projectName, "ProjectReference");

        actualReferences.Should().BeEquivalentTo(expectedReferences);
    }

    [Theory]
    [InlineData(DomainProject)]
    [InlineData(ApplicationProject)]
    public void CoreProjectsDoNotDeclareIntegrationPackages(string projectName)
    {
        string[] forbiddenPackagePrefixes =
        [
            "CommunityToolkit.Mvvm",
            "Microsoft.Data.Sqlite",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Hosting",
            "System.Data.SQLite",
            "VersOne.Epub",
            "HtmlAgilityPack",
            "PdfPig",
        ];

        string[] packageReferences = ReadItemNames(projectName, "PackageReference");

        if (projectName == DomainProject)
        {
            forbiddenPackagePrefixes = [.. forbiddenPackagePrefixes, "Microsoft.Extensions.Logging"];
        }

        packageReferences.Should().NotContain(
            packageName => forbiddenPackagePrefixes.Any(
                prefix => packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DomainAssemblyDoesNotReferenceIntegrationOrUiAssemblies()
    {
        string[] forbiddenAssemblyPrefixes =
        [
            "Microsoft.Data.Sqlite",
            "Microsoft.Extensions.",
            "PresentationCore",
            "PresentationFramework",
            "System.Data.SQLite",
            "System.Xaml",
            "VersOne.Epub",
            "UglyToad.PdfPig",
            "WindowsBase",
        ];

        Assembly domainAssembly = typeof(Domain.AssemblyMarker).Assembly;
        string[] referencedAssemblies = domainAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        referencedAssemblies.Should().NotContain(
            assemblyName => forbiddenAssemblyPrefixes.Any(
                prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ApplicationAssemblyDoesNotReferenceSqliteOrUiAssemblies()
    {
        string[] forbiddenAssemblyPrefixes =
        [
            "Microsoft.Data.Sqlite",
            "UglyToad.PdfPig",
            "PresentationCore",
            "PresentationFramework",
            "System.Xaml",
            "WindowsBase",
        ];
        Assembly applicationAssembly = typeof(Application.AssemblyMarker).Assembly;
        string[] referencedAssemblies = applicationAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        referencedAssemblies.Should().NotContain(
            assemblyName => forbiddenAssemblyPrefixes.Any(
                prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void WpfViewModelsDoNotReferenceInfrastructureSqliteOrFileSystemApis()
    {
        string viewModelsPath = Path.Combine(
            RepositoryRoot,
            "src",
            WpfProject,
            "ViewModels");
        string source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(viewModelsPath, "*.cs").Select(File.ReadAllText));

        source.Should().NotContain("CalibreLibraryCleaner.Infrastructure");
        source.Should().NotContain("Microsoft.Data.Sqlite");
        source.Should().NotContain("System.IO");
    }

    [Fact]
    public void ProductionMutationWorkflowsCannotInvokeLibraryScanners()
    {
        string applicationRoot = Path.Combine(RepositoryRoot, "src", ApplicationProject);
        string[] mutationFiles = Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Executions{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}Recoveries{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        string source = string.Join(Environment.NewLine, mutationFiles.Select(File.ReadAllText));

        source.Should().NotContain("ScanFreshAsync");
        source.Should().NotContain("IExecutionLibraryScanner");
        source.Should().NotContain("IRecoveryCurrentStateScanner");
    }

    [Fact]
    public void EditionMetadataProposalsHaveNoMutationAuthority()
    {
        string[] mutationRoots =
        [
            Path.Combine(RepositoryRoot, "src", ApplicationProject, "Executions"),
            Path.Combine(RepositoryRoot, "src", InfrastructureProject, "Calibre"),
        ];
        string source = string.Join(
            Environment.NewLine,
            mutationRoots.SelectMany(root => Directory.EnumerateFiles(
                root, "*.cs", SearchOption.AllDirectories)).Select(File.ReadAllText));

        source.Should().NotContain("EditionMetadataProposal");
        source.Should().NotContain("IEditionMetadataProvider");
    }

    [Fact]
    public void GoogleBooksCredentialDoesNotFlowToCachesSnapshotsOrExports()
    {
        string[] protectedFiles =
        [
            Path.Combine(RepositoryRoot, "src", InfrastructureProject, "Bibliographic", "FileEditionMetadataProposalCache.cs"),
            Path.Combine(RepositoryRoot, "src", InfrastructureProject, "Metadata", "FileMetadataReviewDecisionStore.cs"),
            .. Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", InfrastructureProject, "LibrarySnapshots"),
                "*.cs",
                SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", InfrastructureProject, "Recommendations"),
                "*.cs",
                SearchOption.AllDirectories),
        ];
        string source = string.Join(Environment.NewLine, protectedFiles.Select(File.ReadAllText));

        source.Should().NotContain("IGoogleBooksApiKeyStore");
        source.Should().NotContain("GoogleBooksApiKey");
        source.Should().NotContain("ProviderPayload");
    }

    [Fact]
    public void MetadataReviewUsesVirtualizedMasterAndResizableSelectedDetail()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", WpfProject, "MainWindow.xaml"));

        source.Should().Contain("ItemsSource=\"{Binding MetadataReviewSubjects}\"");
        source.Should().Contain("EnableRowVirtualization=\"True\"");
        source.Should().Contain("EnableColumnVirtualization=\"True\"");
        source.Should().Contain("VirtualizingPanel.VirtualizationMode=\"Recycling\"");
        source.Should().Contain("SelectedItem=\"{Binding SelectedMetadataReviewSubject, Mode=TwoWay}\"");
        source.Should().Contain("AutomationProperties.Name=\"Resize metadata review details\"");
        source.Should().NotContain("RowDetailsVisibilityMode=\"Visible\"");
        source.Should().Contain("Text=\"{Binding Reasons, Mode=OneWay}\"");
        source.Should().Contain("Text=\"{Binding Provenance, Mode=OneWay}\"");
        source.Should().Contain("IsChecked=\"{Binding Apply, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");
    }

    [Fact]
    public void CoreAndWpfSourceDoNotImplementFileHashing()
    {
        string[] projectNames = [DomainProject, ApplicationProject, WpfProject];
        string source = string.Join(
            Environment.NewLine,
            projectNames.SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", project),
                "*.cs",
                SearchOption.AllDirectories))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Recommendations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Matching{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Plans{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Executions{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Recoveries{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.EndsWith("EpubContentSignatureCacheContracts.cs", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        source.Should().NotContain("FileStream");
        source.Should().NotContain("IncrementalHash");
        source.Should().NotContain("SHA256.HashData");
        source.Should().NotContain("File.ReadAllBytes");
    }

    [Fact]
    public void EpubLibrariesAreReferencedOnlyByInfrastructure()
    {
        ReadItemNames(InfrastructureProject, "PackageReference").Should().Contain(["VersOne.Epub", "HtmlAgilityPack"]);
        foreach (string project in new[] { DomainProject, ApplicationProject, WpfProject })
        {
            ReadItemNames(project, "PackageReference").Should().NotContain(
                name => name == "VersOne.Epub" || name == "HtmlAgilityPack");
        }
    }

    [Fact]
    public void ProductionEpubInspectionHasNoMutationExtractionOrNetworkApi()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            InfrastructureProject,
            "Epub",
            "VersOneEpubInspector.cs"));

        source.Should().NotContain("ExtractToFile");
        source.Should().NotContain("ExtractToDirectory");
        source.Should().NotContain("FileMode.Create");
        source.Should().NotContain("FileAccess.Write");
        source.Should().NotContain("File.Delete");
        source.Should().NotContain("File.Move");
        source.Should().NotContain("HttpClient");
        source.Should().NotContain("WebRequest");
        source.Should().Contain("DownloadContent = false");
        source.Should().Contain("FailClosedContentDownloader");
    }

    [Fact]
    public void ProductionHashingStreamsAndDoesNotMutateFiles()
    {
        string hashingPath = Path.Combine(
            RepositoryRoot,
            "src",
            InfrastructureProject,
            "Hashing");
        string source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(hashingPath, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        source.Should().NotContain("File.ReadAllBytes");
        source.Should().NotContain("ReadToEnd");
        source.Should().NotContain("MemoryMappedFile");
        source.Should().NotContain("FileMode.Create");
        source.Should().NotContain("FileMode.Append");
        source.Should().NotContain("FileMode.Truncate");
        source.Should().NotContain("File.Delete");
        source.Should().NotContain("File.Move");
        source.Should().NotContain("File.Replace");
    }

    [Fact]
    public void DomainMetadataMatchingAvoidsIntegrationAndFuzzyAlgorithms()
    {
        string duplicatesPath = Path.Combine(
            RepositoryRoot,
            "src",
            DomainProject,
            "Duplicates");
        string source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(duplicatesPath, "*.cs", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Contains("Metadata", StringComparison.Ordinal) ||
                               Path.GetFileName(path).StartsWith("Normalized", StringComparison.Ordinal))
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "MetadataCandidateRetentionPolicy.cs",
                    StringComparison.Ordinal))
                .Select(File.ReadAllText));

        source.Should().NotContain("System.IO");
        source.Should().NotContain("Microsoft.Data.Sqlite");
        source.Should().NotContain("AuthorSort");
        source.Should().NotContain("SortName");
        string upperSource = source.ToUpperInvariant();
        upperSource.Should().NotContain("LEVENSHTEIN");
        upperSource.Should().NotContain("JARO");
        upperSource.Should().NotContain("SIMILARITY");
    }

    [Fact]
    public void RecommendationIntegrationBoundariesRemainConfined()
    {
        string domainSource = ReadSource(DomainProject, "Recommendations");
        string applicationSource = ReadSource(ApplicationProject, "Recommendations");
        string viewModelSource = ReadSource(WpfProject, "ViewModels");
        string infrastructureSource = ReadSource(InfrastructureProject, "Recommendations");

        domainSource.Should().NotContain("System.Text.Json").And.NotContain("System.IO").And.NotContain("Microsoft.Data.Sqlite");
        applicationSource.Should().NotContain("System.Text.Json").And.NotContain("File.").And.NotContain("Directory.").And.NotContain("Microsoft.Win32");
        viewModelSource.Should().NotContain("System.Text.Json").And.NotContain("File.").And.NotContain("Directory.");
        infrastructureSource.Should().Contain("System.Text.Json").And.Contain("FileStream");
    }

    [Fact]
    public void RemovedMutationSubsystemsRemainAbsent()
    {
        string domainSource = ReadSource(DomainProject, "Plans");
        string applicationSource = ReadSource(ApplicationProject, "Plans");
        string infrastructureSource = ReadSource(InfrastructureProject, "Plans");

        domainSource.Should().BeEmpty();
        applicationSource.Should().BeEmpty();
        infrastructureSource.Should().BeEmpty();

        string production = string.Join(Environment.NewLine,
            new[] { DomainProject, ApplicationProject, InfrastructureProject, WpfProject }
                .SelectMany(project => Directory.EnumerateFiles(
                    Path.Combine(RepositoryRoot, "src", project), "*.cs", SearchOption.AllDirectories))
                .Select(File.ReadAllText));
        production.Should().NotContain("IExecutionBackupStore")
            .And.NotContain("IExecutionJournalStore")
            .And.NotContain("ICalibreCommandGateway")
            .And.NotContain("RecoveryWorkspaceViewModel");
    }

    [Fact]
    public void ExecutionCoreRemainsFreeOfProcessFileSystemSqliteJsonAndWpfTypes()
    {
        string domainSource = ReadSource(DomainProject, "Executions");
        string applicationSource = ReadSource(ApplicationProject, "Executions") +
                                   ReadSource(ApplicationProject, "Abstractions");

        domainSource.Should().NotContain("System.IO").And.NotContain("System.Text.Json")
            .And.NotContain("Process").And.NotContain("Microsoft.Data.Sqlite")
            .And.NotContain("System.Windows").And.NotContain("calibredb");
        applicationSource.Should().NotContain("System.IO").And.NotContain("System.Text.Json")
            .And.NotContain("ProcessStartInfo").And.NotContain("File.")
            .And.NotContain("Directory.").And.NotContain("Microsoft.Data.Sqlite")
            .And.NotContain("System.Windows");
    }

    [Fact]
    public void RecoveryNamespacesRemainAbsent()
    {
        string domain = ReadSource(DomainProject, "Recoveries");
        string application = ReadSource(ApplicationProject, "Recoveries");
        string infrastructure = ReadSource(InfrastructureProject, "Recovery");

        domain.Should().BeEmpty();
        application.Should().BeEmpty();
        infrastructure.Should().BeEmpty();
    }

    [Fact]
    public void ExternalWorkersUseDedicatedDirectNoShellProcessBoundaries()
    {
        string infrastructureRoot = Path.Combine(RepositoryRoot, "src", InfrastructureProject);
        string[] processSources = Directory.EnumerateFiles(infrastructureRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("ProcessStartInfo", StringComparison.Ordinal))
            .ToArray();
        string runner = File.ReadAllText(Path.Combine(infrastructureRoot, "Calibre", "DirectCalibreProcessRunner.cs"));
        string allProductionSource = string.Join(Environment.NewLine,
            new[] { DomainProject, ApplicationProject, InfrastructureProject, WpfProject }
                .SelectMany(project => Directory.EnumerateFiles(
                    Path.Combine(RepositoryRoot, "src", project), "*.cs", SearchOption.AllDirectories))
                .Select(File.ReadAllText));

        processSources.Should().HaveCount(4);
        processSources.Should().Contain(path => path.EndsWith("CalibreEbookViewerLauncher.cs", StringComparison.Ordinal));
        processSources.Should().Contain(path => path.EndsWith("DirectCalibreProcessRunner.cs", StringComparison.Ordinal));
        processSources.Should().Contain(path => path.EndsWith("IsolatedPdfInspector.cs", StringComparison.Ordinal));
        processSources.Should().Contain(path => path.EndsWith("PersistentCalibreMutationWorkerFactory.cs", StringComparison.Ordinal));
        runner.Should().Contain("UseShellExecute = false").And.Contain("ArgumentList.Add")
            .And.Contain("mayTerminateOnCancellation");
        string viewer = File.ReadAllText(Path.Combine(
            infrastructureRoot, "Calibre", "CalibreEbookViewerLauncher.cs"));
        viewer.Should().Contain("UseShellExecute = false")
            .And.Contain("ArgumentList.Add(formatPath!)")
            .And.Contain("TryValidateContainedRegularFile")
            .And.NotContain("UseShellExecute = true");
        string calibreWorker = File.ReadAllText(Path.Combine(
            infrastructureRoot, "Calibre", "PersistentCalibreMutationWorkerFactory.cs"));
        calibreWorker.Should().Contain("UseShellExecute = false")
            .And.Contain("RedirectStandardInput = true")
            .And.Contain("RedirectStandardOutput = true")
            .And.Contain("Environment.Clear()")
            .And.Contain("CLC_WORKER_TEMP_DIRECTORY")
            .And.Contain("calibre_mutation_worker.py");
        string pdfRunner = File.ReadAllText(Path.Combine(infrastructureRoot, "Pdf", "IsolatedPdfInspector.cs"));
        pdfRunner.Should().Contain("UseShellExecute = false").And.Contain("ArgumentList.Add(\"--stdio\")")
            .And.Contain("RedirectStandardInput = true").And.Contain("RedirectStandardOutput = true")
            .And.Contain("Environment.Clear()").And.Contain("Kill(entireProcessTree: true)");
        allProductionSource.Should().NotContain("UseShellExecute = true")
            .And.NotContain("cmd.exe").And.NotContain("powershell.exe")
            .And.NotContain("bash.exe").And.NotContain("/bin/bash");
    }

    [Fact]
    public void PdfParserTypesAndPackageStayInsideInfrastructure()
    {
        string nonInfrastructureSource = string.Join(Environment.NewLine,
            new[] { DomainProject, ApplicationProject, WpfProject, PdfWorkerProject }
                .SelectMany(project => Directory.EnumerateFiles(
                    Path.Combine(RepositoryRoot, "src", project), "*.cs", SearchOption.AllDirectories))
                .Select(File.ReadAllText));
        string[] projectsWithPdfPig = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("PackageReference Include=\"PdfPig\"", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToArray();

        nonInfrastructureSource.Should().NotContain("UglyToad.PdfPig").And.NotContain("PdfPig.");
        projectsWithPdfPig.Should().Equal(InfrastructureProject);
    }

    [Fact]
    public void PdfProductionBoundaryDoesNotDecodeImagesExtractAttachmentsOrUseNetworkApis()
    {
        string source = ReadSource(InfrastructureProject, "Pdf");

        source.Should().NotContain("TryGetEmbeddedFiles")
            .And.NotContain("TryGetBytesAsMemory")
            .And.NotContain("TryGetPng")
            .And.NotContain("RawMemory")
            .And.NotContain("RawBytes")
            .And.NotContain("HttpClient")
            .And.NotContain("WebRequest")
            .And.NotContain("WebClient")
            .And.NotContain("Socket")
            .And.NotContain("Process.Start(\"")
            .And.NotContain("File.ReadAllBytes")
            .And.NotContain("File.WriteAllBytes")
            .And.NotContain("File.WriteAllText")
            .And.NotContain("FileMode.Create")
            .And.NotContain("FileMode.Append")
            .And.NotContain("FileMode.Truncate")
            .And.NotContain("FileAccess.Write")
            .And.NotContain("File.Delete")
            .And.NotContain("File.Move")
            .And.NotContain("File.Replace")
            .And.NotContain("Directory.CreateDirectory")
            .And.NotContain("Directory.Delete")
            .And.NotContain("PdfDocumentBuilder")
            .And.Contain("PdfDocument.Open(stream, options)");
    }

    [Fact]
    public void PdfCoreAndUiBoundariesContainNoParserFileProcessOrJsonTypes()
    {
        string domain = ReadSource(DomainProject, "Assessments");
        string application = ReadSource(ApplicationProject, "Assessments");
        string viewModels = ReadSource(WpfProject, "ViewModels");
        string worker = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", PdfWorkerProject), "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        domain.Should().NotContain("UglyToad.PdfPig").And.NotContain("System.IO")
            .And.NotContain("System.Text.Json").And.NotContain("Microsoft.Data.Sqlite")
            .And.NotContain("Microsoft.Extensions.").And.NotContain("System.Windows");
        application.Should().NotContain("UglyToad.PdfPig").And.NotContain("System.IO")
            .And.NotContain("System.Text.Json").And.NotContain("ProcessStartInfo")
            .And.NotContain("Microsoft.Data.Sqlite").And.NotContain("System.Windows");
        viewModels.Should().NotContain("UglyToad.PdfPig").And.NotContain("ProcessStartInfo")
            .And.NotContain("CalibreLibraryCleaner.Infrastructure");
        worker.Should().NotContain("UglyToad.PdfPig").And.NotContain("PdfDocument.Open");
    }

    [Fact]
    public void PdfNativeInteropIsConfinedToTheJobObjectWrapper()
    {
        string pdfPath = Path.Combine(RepositoryRoot, "src", InfrastructureProject, "Pdf");
        string[] nativeSources = Directory.EnumerateFiles(pdfPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("LibraryImport", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("DllImport", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        nativeSources.Should().Equal("PdfWorkerJobObject.cs");
        string nativeSource = File.ReadAllText(Path.Combine(pdfPath, "PdfWorkerJobObject.cs"));
        nativeSource.Should().Contain("[DllImport(\"kernel32.dll\"")
            .And.NotContain("pdfium").And.NotContain("mupdf").And.NotContain("poppler");
    }

    [Fact]
    public void CalibreMutationMappingIsConfinedToFixedWorkerProtocol()
    {
        string worker = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", InfrastructureProject, "Calibre", "calibre_mutation_worker.py"));
        string factory = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", InfrastructureProject, "Calibre", "PersistentCalibreMutationWorkerFactory.cs"));

        worker.Should().Contain("transfer_format").And.Contain("remove_formats")
            .And.Contain("remove_books");
        (worker + factory).Should().NotContain("--permanent")
            .And.NotContain("restore_database").And.NotContain("backup_metadata")
            .And.NotContain("shell");
        File.Exists(Path.Combine(RepositoryRoot, "src", InfrastructureProject,
            "Calibre", "CalibreCommandGateway.cs")).Should().BeFalse();
    }

    [Fact]
    public void ExecutionUiDoesNotInvokeProcessesOrInfrastructureFileApis()
    {
        string viewModels = ReadSource(WpfProject, "ViewModels");

        viewModels.Should().NotContain("CalibreLibraryCleaner.Infrastructure")
            .And.NotContain("ProcessStartInfo").And.NotContain("System.Diagnostics")
            .And.NotContain("System.IO").And.NotContain("File.").And.NotContain("Directory.");
    }

    private static string ReadSource(string project, string folder)
    {
        string path = Path.Combine(RepositoryRoot, "src", project, folder);
        return string.Join(Environment.NewLine, Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    private static string[] ReadItemNames(string projectName, string itemName)
    {
        string projectPath = Path.Combine(
            RepositoryRoot,
            "src",
            projectName,
            $"{projectName}.csproj");
        XDocument project = XDocument.Load(projectPath);

        return project
            .Descendants(itemName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => itemName == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(value!)!
                : value!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CalibreLibraryCleaner.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
