using Nornia.Core.Models;
using Nornia.Package.Services;

namespace Nornia.Tests;

public sealed class CachePackageAssociationServiceTests
{
    [Fact]
    public void Associate_LinksKnownNpmCacheToInstalledNodePackage()
    {
        var candidate = new CacheCandidate("cache", "User", "C:\\Users\\Test\\.npm", 4096, CacheConfidence.High, "known cache");
        var packages = new[] { new PackageInfo("OpenJS.NodeJS", "Node.js", "22.0", null, "winget", true) };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("OpenJS.NodeJS", associated.PackageId);
        Assert.Equal("Node.js", associated.PackageName);
    }

    [Fact]
    public void Associate_LinksEcosystemCacheEvenWhenOtherMicrosoftPackagesExist()
    {
        // The ".npm" cache must link to Node.js even with .NET packages present; the old heuristic
        // scored every package sharing a common path fragment ("microsoft") and tie-broke wrongly.
        var candidate = new CacheCandidate("cache", "User", "C:\\Users\\Test\\AppData\\Roaming\\npm", 4096, CacheConfidence.High, "known cache");
        var packages = new[]
        {
            new PackageInfo("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.303 (x64)", "10.0.303", null, "winget", true),
            new PackageInfo("OpenJS.NodeJS", "Node.js", "22.0", null, "winget", true)
        };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("OpenJS.NodeJS", associated.PackageId);
    }

    [Fact]
    public void Associate_DoesNotLinkBrowserCacheToDotnetRuntime()
    {
        // Regression: a path containing "Microsoft" scored every Microsoft-named package equally and
        // the alphabetical tie-break linked Edge caches to ".NET Runtime". The distinctive "edge"
        // path segment must win for Edge itself.
        var candidate = new CacheCandidate("cache", "Microsoft", "C:\\Users\\Test\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\Cache", 4096, CacheConfidence.High, "known cache");
        var packages = new[]
        {
            new PackageInfo("Microsoft.DotNet.Runtime.8", "Microsoft .NET Runtime - 8.0.30 (x64)", "8.0.30", null, "winget", true),
            new PackageInfo("Microsoft.Edge", "Microsoft Edge", "151.0.4129.93", null, "winget", true)
        };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("Microsoft.Edge", associated.PackageId);
    }

    [Fact]
    public void Associate_LinksDistinctPathSegmentToItsPackage()
    {
        // A unique package name fragment appearing as a real path segment (Everything cache under
        // "AppData\Roaming\Everything") is a trustworthy single-segment match.
        var candidate = new CacheCandidate("cache", "Roaming", "C:\\Users\\Test\\AppData\\Roaming\\Everything\\Cache", 4096, CacheConfidence.High, "known cache");
        var packages = new[]
        {
            new PackageInfo("voidtools.Everything", "Everything 1.4.1", "1.4.1", null, "winget", true),
            new PackageInfo("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.303 (x64)", "10.0.303", null, "winget", true)
        };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("voidtools.Everything", associated.PackageId);
    }

    [Fact]
    public void Associate_DoesNotMatchSharedSegmentWordAlone()
    {
        // "user" appears in many installed package names; a lone shared path segment must not win.
        // "code" is distinctive, so VS Code wins on the "code"+"user" segment pair instead.
        var candidate = new CacheCandidate("cache", "Roaming", "C:\\Users\\Test\\AppData\\Roaming\\Code\\User\\Cache", 4096, CacheConfidence.High, "known cache");
        var packages = new[]
        {
            new PackageInfo("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code (User)", "1.134.0", null, "winget", true),
            new PackageInfo("ByteDance.TraeWork.CN", "TraeWork CN (User)", "0.1.52", null, "winget", true),
            new PackageInfo("Alibaba.Qoder", "Qoder IDE (User)", "1.106.3", null, "winget", true)
        };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("Microsoft.VisualStudioCode", associated.PackageId);
    }

    [Fact]
    public void Associate_LinksGoCacheToGolangPackageOnly()
    {
        // "go" is a substring of "xboxgamingoverlay"; only the Go language package may win the
        // go/pkg/mod cache. The ecosystem key uses the distinctive "golang" term.
        var candidate = new CacheCandidate("cache", "User", "C:\\Users\\Test\\go\\pkg\\mod\\cache", 4096, CacheConfidence.High, "known cache");
        var packages = new[]
        {
            new PackageInfo("GoLang.Go", "Go", "1.24.0", null, "winget", true),
            new PackageInfo("Microsoft.XboxGamingOverlay", "Game Bar", "7.326.7271.0", null, "winget", true)
        };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("GoLang.Go", associated.PackageId);
    }

    [Fact]
    public void Associate_LinksTraeCacheToTraeWorkPackage()
    {
        var candidate = new CacheCandidate("cache", "Roaming", "C:\\Users\\Test\\AppData\\Roaming\\TraeCN\\Cache", 4096, CacheConfidence.High, "known cache");
        var packages = new[] { new PackageInfo("ByteDance.TraeWork.CN", "TraeWork CN (User)", "0.1.52", null, "winget", true) };

        var associated = Assert.Single(new CachePackageAssociationService().Associate([candidate], packages));

        Assert.Equal("ByteDance.TraeWork.CN", associated.PackageId);
    }

    [Fact]
    public void Summarize_GroupsCandidateSizesByPackage()
    {
        CacheCandidate[] candidates =
        [
            new("one", "AppData", "C:\\a", 100, CacheConfidence.High, "", "Git.Git", "Git", "winget"),
            new("two", "AppData", "C:\\b", 200, CacheConfidence.Review, "", "Git.Git", "Git", "winget"),
            new("three", "AppData", "C:\\c", 50, CacheConfidence.High, "")
        ];

        var summaries = new CachePackageAssociationService().Summarize(candidates);

        var git = Assert.Single(summaries, summary => summary.PackageId == "Git.Git");
        Assert.Equal(300, git.SizeBytes);
        Assert.Equal(2, git.CandidateCount);
        Assert.Equal("未关联的软件缓存", Assert.Single(summaries, summary => summary.PackageId is null).PackageName);
    }

    [Fact]
    public void Summarize_BreaksPackageCacheTypesDownIntoTypeCounts()
    {
        CacheCandidate[] candidates =
        [
            new("one", "AppData", "C:\\Users\\Test\\.npm", 100, CacheConfidence.High, "", "OpenJS.NodeJS", "Node.js", "winget", "npm"),
            new("two", "AppData", "C:\\Users\\Test\\.yarn\\cache", 200, CacheConfidence.High, "", "OpenJS.NodeJS", "Node.js", "winget", "yarn"),
            new("three", "AppData", "C:\\Users\\Test\\.npm", 50, CacheConfidence.High, "", "OpenJS.NodeJS", "Node.js", "winget", "npm")
        ];

        var summary = Assert.Single(new CachePackageAssociationService().Summarize(candidates));

        var npm = Assert.Single(summary.CacheTypes, type => type.Type == "npm");
        Assert.Equal(2, npm.Count);
        Assert.Equal(150, npm.SizeBytes);
        var yarn = Assert.Single(summary.CacheTypes, type => type.Type == "yarn");
        Assert.Equal(1, yarn.Count);
    }

    [Theory]
    [InlineData("C:\\Users\\Test\\.pnpm-store", "pnpm")]
    [InlineData("C:\\Users\\Test\\.npm", "npm")]
    [InlineData("C:\\Users\\Test\\.yarn\\cache", "yarn")]
    [InlineData("C:\\Users\\Test\\.nuget\\packages", "nuget")]
    [InlineData("C:\\Users\\Test\\.gradle\\caches", "gradle")]
    [InlineData("C:\\Users\\Test\\.m2\\repository", "maven")]
    [InlineData("C:\\Users\\Test\\.cargo\\registry\\cache", "cargo")]
    [InlineData("C:\\Users\\Test\\go\\pkg\\mod", "go")]
    [InlineData("C:\\Users\\Test\\AppData\\Local\\SomeApp\\Cache", "其他")]
    public void ResolveCacheType_ClassifiesPathIntoToolEcosystem(string path, string expected) =>
        Assert.Equal(expected, CachePackageAssociationService.ResolveCacheType(path));
}
