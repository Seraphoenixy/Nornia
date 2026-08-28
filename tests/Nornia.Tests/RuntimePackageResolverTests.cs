using Nornia.Package.Services;

namespace Nornia.Tests;

public sealed class RuntimePackageResolverTests
{
    private readonly RuntimePackageResolver _resolver = new();

    [Theory]
    [InlineData("dotnet", "10.0.302", "Microsoft.DotNet.SDK.10", null)]
    [InlineData("python", "3.13.2", "Python.Python.3.13", null)]
    [InlineData("node", "22.14.0", "OpenJS.NodeJS", "22.14.0")]
    [InlineData("Git", "2.50.0", "Git.Git", "2.50.0")]
    public void ResolveMany_ReturnsWingetPackage(string runtime, string version, string expectedId, string? expectedVersion)
    {
        var package = Assert.Single(_resolver.ResolveMany(runtime, version));

        Assert.Equal(expectedId, package.PackageId);
        Assert.Equal(expectedVersion, package.PackageVersion);
    }

    [Fact]
    public void SupportedComponentNames_AreSeparatedByCategory()
    {
        Assert.Equal(["visual-cpp-redistributable", "dotnet-desktop-runtime", "windows-app-runtime"], _resolver.SupportedRuntimeNames);
        Assert.Equal(["dotnet", "node", "python", "java", "git"], _resolver.SupportedToolNames);
    }

    [Fact]
    public void ResolveMany_MapsJavaToMicrosoftOpenJdk() =>
        Assert.Equal("Microsoft.OpenJDK.21", Assert.Single(_resolver.ResolveMany("java", "21")).PackageId);

    [Theory]
    [InlineData("7000.770.750.0", "Microsoft.WindowsAppRuntime.1.7")]
    [InlineData("6000.457.2140.0", "Microsoft.WindowsAppRuntime.1.6")]
    public void ResolveMany_MapsWindowsAppRuntimeBuildToItsPackageRelease(string version, string expectedId)
    {
        var package = Assert.Single(_resolver.ResolveMany("windows-app-runtime", version));

        Assert.Equal(expectedId, package.PackageId);
    }

    [Fact]
    public void ResolveMany_VisualCppAlwaysIncludesX86Installer()
    {
        var packages = _resolver.ResolveMany("visual-cpp-redistributable", "14.51");

        Assert.Contains(packages, package => package.PackageId == "Microsoft.VCRedist.2015+.x86");
    }

    [Fact]
    public void ResolveMany_RejectsVersionMissingRequiredPart()
    {
        // The python mapping requires a minor part (Python.Python.{major}.{minor}).
        Assert.Throws<ArgumentException>(() => _resolver.ResolveMany("python", "3"));
    }

    [Fact]
    public void ResolveMany_RejectsUnknownComponent()
    {
        Assert.Throws<ArgumentException>(() => _resolver.ResolveMany("unknown-runtime", "1.0"));
    }

    [Fact]
    public void TryResolveAll_ReturnsNullForUnmappedComponent()
    {
        Assert.Null(_resolver.TryResolveAll("unknown-runtime", "1.0"));
    }

    [Fact]
    public void CustomMappings_AreAppliedWithoutCodeChanges()
    {
        var mappings = PackageMappingLoader.FromJson("""
            { "mappings": [ { "runtimeId": "node", "packages": [ { "idTemplate": "Custom.Node.{major}", "versionTemplate": "{version}" } ] } ] }
            """);
        var resolver = new RuntimePackageResolver(mappings);

        var package = Assert.Single(resolver.ResolveMany("node", "23.1.0"));

        Assert.Equal("Custom.Node.23", package.PackageId);
        Assert.Equal("23.1.0", package.PackageVersion);
    }
}
