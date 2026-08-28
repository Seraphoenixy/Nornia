using BenchmarkDotNet.Attributes;
using Nornia.Project.Models;

namespace Nornia.Benchmarks;

[MemoryDiagnoser]
public class VersionConstraintBenchmark
{
    private readonly string[] _requirements = ["10", "3.13", ">=22 <23", ">=2.50 <3", "26.0.4-preview.1"];
    private readonly string[] _installed = ["10.0.302", "3.13.13", "22.18.0", "2.55.0.windows.3", "26.0.4-preview.2"];

    [Benchmark]
    public int ParseAndCheck()
    {
        var satisfied = 0;
        for (var index = 0; index < _requirements.Length; index++)
        {
            if (VersionConstraintParser.TryParse(_requirements[index], out var constraint) && constraint!.IsSatisfiedBy(_installed[index]))
            {
                satisfied++;
            }
        }

        return satisfied;
    }
}