using BenchmarkDotNet.Attributes;
using Nornia.Package.Services;

namespace Nornia.Benchmarks;

[MemoryDiagnoser]
public class PackageResolutionBenchmark
{
    private readonly RuntimePackageResolver _resolver = new();

    [Benchmark]
    public int ResolveMany_SupportedComponents()
    {
        var count = 0;
        foreach (var (component, version) in new[]
                 {
                     ("dotnet", "10.0.302"), ("python", "3.13.2"), ("node", "22.14.0"),
                     ("java", "21"), ("git", "2.50.0"), ("visual-cpp-redistributable", "14.51"),
                     ("dotnet-desktop-runtime", "10.0.1"), ("windows-app-runtime", "1.8.5")
                 })
        {
            count += _resolver.ResolveMany(component, version).Count;
        }

        return count;
    }
}