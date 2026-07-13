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
