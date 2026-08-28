using BenchmarkDotNet.Attributes;
using Nornia.Core.Collections;

namespace Nornia.Benchmarks;

[MemoryDiagnoser]
public class BulkCollectionBenchmark
{
    [Params(10_000, 100_000)]
    public int Count { get; set; }

    private int[] _items = [];

    [GlobalSetup]
    public void Setup() => _items = Enumerable.Range(0, Count).ToArray();

    [Benchmark(Baseline = true)]
    public int PerItemAdd()
    {
        var collection = new List<int>();
        foreach (var item in _items) collection.Add(item);
        return collection.Count;
    }

    [Benchmark]
    public int BulkReplace()
    {
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceRange(_items);
        return collection.Count;
    }
}
