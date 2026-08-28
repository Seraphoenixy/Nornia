using BenchmarkDotNet.Attributes;
using Nornia.Core.Models;

namespace Nornia.Benchmarks;

[MemoryDiagnoser]
public class CatalogLookupBenchmark
{
    private readonly string[] _names = ["node", "Python", "vcredist", "visual c++ redistributable", ".NET SDK", "unknown", "java", "Git"];

    [Benchmark]
    public int TryGet_ByAliasAndDisplayName()
    {
        var hits = 0;
        foreach (var name in _names)
        {
            if (EnvironmentComponentCatalog.TryGet(name, out _))
            {
                hits++;
            }
        }

        return hits;
    }
}