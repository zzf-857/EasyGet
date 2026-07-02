using System.Xml.Linq;
using Xunit;

namespace EasyGet.Tests;

public class ProjectDependencyTests
{
    [Fact]
    public void MainProjectPackageReferencesUsePinnedVersions()
    {
        var project = XDocument.Load(Path.Combine(TestRepositoryPaths.Root, "EasyGet.csproj"));

        var packageVersions = project
            .Descendants("PackageReference")
            .Select(package => (
                Name: package.Attribute("Include")?.Value ?? "",
                Version: package.Attribute("Version")?.Value ?? ""))
            .Where(package => !string.IsNullOrWhiteSpace(package.Name))
            .ToList();

        Assert.NotEmpty(packageVersions);
        Assert.All(packageVersions, package =>
        {
            Assert.False(
                package.Version.Contains('*', StringComparison.Ordinal),
                $"{package.Name} should use a pinned version instead of {package.Version}.");
            Assert.Matches(@"^\d+\.\d+\.\d+$", package.Version);
        });
    }

}
