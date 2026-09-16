using Nornia.Core.Models;
using Nornia.Package.Services;

namespace Nornia.Tests;

public sealed class CacheClassificationServiceTests
{
    private readonly CacheClassificationService _service = new();

    [Theory]
    [InlineData(@"C:\Users\Test\.pnpm-store", "pnpm", "pnpm")]
    [InlineData(@"C:\Users\Test\.npm", "npm", "npm")]
    [InlineData(@"C:\Users\Test\.yarn\cache", "Yarn", "yarn")]
    [InlineData(@"C:\Users\Test\.nuget\packages", "NuGet", "nuget")]
    [InlineData(@"C:\Users\Test\.gradle\caches", "Gradle", "gradle")]
    [InlineData(@"C:\Users\Test\.m2\repository", "Maven", "maven")]
    [InlineData(@"C:\Users\Test\.cargo\registry\cache", "Cargo", "cargo")]
    [InlineData(@"C:\Users\Test\.bun\install\cache", "Bun", "bun")]
    [InlineData(@"C:\Users\Test\go\pkg\mod", "Go Modules", "go")]
    public void Classify_RecognizesKnownToolEcosystems(string path, string category, string type)
    {
        var classified = _service.Classify(Candidate(path, @"C:\Users\Test"));

        Assert.Equal(category, classified.CategoryName);
        Assert.Equal(type, classified.CacheType);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache", "Google Chrome")]
    [InlineData(@"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Profile 2\Code Cache", "Google Chrome")]
    [InlineData(@"C:\Users\Test\AppData\Local\Microsoft\Edge\User Data\Default\GPUCache", "Microsoft Edge")]
    [InlineData(@"C:\Users\Test\AppData\Roaming\Code\User\Cache", "Code")]
    [InlineData(@"C:\Users\Test\AppData\Roaming\TRAE SOLO CN\ModularData\ai-agent\vm\tools\python\Lib\site-packages\sqlalchemy\sql\__pycache__", "TRAE SOLO CN")]
    public void Classify_IgnoresStructuralSegmentsAndRecognizesApplications(string path, string category)
    {
        var root = path.Contains("\\Roaming\\", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Users\Test\AppData\Roaming"
            : @"C:\Users\Test\AppData\Local";

        var classified = _service.Classify(Candidate(path, root));

        Assert.Equal(category, classified.CategoryName);
        Assert.Contains("目录", classified.ClassificationReason);
    }

    [Fact]
    public void Classify_UnknownApplicationUsesApplicationRootInsteadOfDeepCacheAncestor()
    {
        var classified = _service.Classify(Candidate(
            @"C:\Users\Test\AppData\Local\Example App\runtime\python\Lib\site-packages\sqlalchemy\sql\__pycache__",
            @"C:\Users\Test\AppData\Local"));

        Assert.Equal("Example App", classified.CategoryName);
        Assert.NotEqual("sql", classified.CategoryName);
        Assert.NotEqual("sqlalchemy", classified.CategoryName);
        Assert.NotEqual("site-packages", classified.CategoryName);
    }

    [Fact]
    public void Summarize_MergesCategoryKeysCaseInsensitivelyAndKeepsTypes()
    {
        CacheCandidate[] candidates =
        [
            Candidate(@"C:\one", categoryKey: "googlechrome", categoryName: "Google Chrome", type: "其他", size: 100),
            Candidate(@"C:\two", categoryKey: "GOOGLECHROME", categoryName: "Google Chrome", type: "代码缓存", size: 200),
            Candidate(@"C:\three", categoryKey: "edge", categoryName: "Microsoft Edge", type: "GPU 缓存", size: 50)
        ];

        var summaries = _service.Summarize(candidates);

        var chrome = Assert.Single(summaries, summary => summary.CategoryName == "Google Chrome");
        Assert.Equal(2, chrome.CandidateCount);
        Assert.Equal(300, chrome.SizeBytes);
        Assert.Equal(2, chrome.CacheTypes.Count);
        Assert.Equal(2, summaries.Count);
    }

    [Theory]
    [InlineData(@"C:\App\GPUCache", "GPU 缓存")]
    [InlineData(@"C:\App\Code Cache", "代码缓存")]
    [InlineData(@"C:\App\ShaderCache", "着色器缓存")]
    [InlineData(@"C:\App\Temp", "临时文件")]
    public void ResolveCacheType_ClassifiesGenericCacheKinds(string path, string expected) =>
        Assert.Equal(expected, CacheClassificationService.ResolveCacheType(path));

    private static CacheCandidate Candidate(
        string path,
        string? root = null,
        string? categoryKey = null,
        string? categoryName = null,
        string? type = null,
        long size = 4096) =>
        new("cache", "User", path, size, CacheConfidence.High, "known cache",
            CategoryKey: categoryKey,
            CategoryName: categoryName,
            CacheType: type,
            UserDirectory: root);
}
