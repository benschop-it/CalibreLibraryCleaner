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

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { DomainProject, [] },
        { ApplicationProject, [DomainProject] },
        { InfrastructureProject, [ApplicationProject] },
        { WpfProject, [ApplicationProject, InfrastructureProject] },
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
            "Microsoft.Extensions.Logging",
            "System.Data.SQLite",
            "VersOne.Epub",
            "HtmlAgilityPack",
        ];

        string[] packageReferences = ReadItemNames(projectName, "PackageReference");

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
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Plans{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Executions{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
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
        string epubPath = Path.Combine(RepositoryRoot, "src", InfrastructureProject, "Epub");
        string source = string.Join(Environment.NewLine, Directory.EnumerateFiles(epubPath, "*.cs").Select(File.ReadAllText));

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
    public void DomainMetadataMatchingAvoidsIntegrationAndFutureScopeAlgorithms()
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
                .Select(File.ReadAllText));

        source.Should().NotContain("System.IO");
        source.Should().NotContain("Microsoft.Data.Sqlite");
        source.Should().NotContain("AuthorSort");
        source.Should().NotContain("SortName");
        string upperSource = source.ToUpperInvariant();
        upperSource.Should().NotContain("ISBN");
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
    public void CleanupPlanIntegrationAndInteractionBoundariesRemainConfined()
    {
        string domainSource = ReadSource(DomainProject, "Plans");
        string applicationSource = ReadSource(ApplicationProject, "Plans");
        string infrastructureSource = ReadSource(InfrastructureProject, "Plans");
        string viewModelSource = ReadSource(WpfProject, "ViewModels");
        string nonCompositionWpf = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", WpfProject), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("App.xaml.cs", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        domainSource.Should().NotContain("System.Text.Json").And.NotContain("System.IO")
            .And.NotContain("Microsoft.Data.Sqlite").And.NotContain("System.Windows");
        applicationSource.Should().NotContain("System.Text.Json").And.NotContain("File.")
            .And.NotContain("Directory.").And.NotContain("Microsoft.Win32");
        viewModelSource.Should().NotContain("System.Text.Json").And.NotContain("File.")
            .And.NotContain("Directory.").And.NotContain("Microsoft.Win32");
        infrastructureSource.Should().Contain("System.Text.Json").And.Contain("FileStream");
        nonCompositionWpf.Should().NotContain("CalibreLibraryCleaner.Infrastructure");
        string upper = (domainSource + applicationSource + infrastructureSource).ToUpperInvariant();
        upper.Should().NotContain("PROCESSSTARTINFO").And.NotContain("CALIBREDB")
            .And.NotContain("RESTOREBACKUP");
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
    public void CalibreExecutionUsesOneDirectNoShellProcessBoundary()
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

        processSources.Should().ContainSingle()
            .Which.Should().EndWith("DirectCalibreProcessRunner.cs");
        runner.Should().Contain("UseShellExecute = false").And.Contain("ArgumentList.Add")
            .And.Contain("mayTerminateOnCancellation");
        allProductionSource.Should().NotContain("UseShellExecute = true")
            .And.NotContain("cmd.exe").And.NotContain("powershell.exe")
            .And.NotContain("bash.exe").And.NotContain("/bin/bash");
    }

    [Fact]
    public void CalibreMutationMappingIsFixedAndExcludesPermanentOrRollbackCommands()
    {
        string gateway = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", InfrastructureProject, "Calibre", "CalibreCommandGateway.cs"));

        gateway.Should().Contain("\"add_format\"").And.Contain("\"remove\"")
            .And.Contain("\"export\"");
        gateway.Should().NotContain("--permanent").And.NotContain("remove_format")
            .And.NotContain("restore_database").And.NotContain("backup_metadata")
            .And.NotContain("shell");
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
