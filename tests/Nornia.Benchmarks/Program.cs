using BenchmarkDotNet.Running;

namespace Nornia.Benchmarks;

public static class Program
{
    /// <summary>
    /// Runs the Nornia benchmarks, e.g.:
    ///   dotnet run -c Release --project tests/Nornia.Benchmarks --filter CatalogLookupBenchmark
    /// </summary>
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}