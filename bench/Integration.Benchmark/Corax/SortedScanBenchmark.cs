using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Sparrow.Utils;

namespace Integration.Benchmark.Corax;

[SimpleJob(warmupCount: 5, iterationCount: 20)]
[MemoryDiagnoser]
public class SortedScanBenchmark
{
    private const int NumberOfDocuments = 100_000;
    private RavenDbInstance _instance;
    private long _nativeBytesAfterSetup;
    private IDocumentStore Store => _instance.Store;

    [GlobalSetup]
    public void GlobalSetup()
    {
        string path = Path.Combine(Path.GetTempPath(), "RavenDB-27920-sorted-scan-v2");
        string marker = path + ".built";
        bool isBuilt = File.Exists(marker);
        if (isBuilt == false && Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        _instance = new RavenDbInstance();
        _instance.InitializeDatabase(path);
        if (isBuilt == false)
        {
            using (var bulk = Store.BulkInsert())
            {
                for (int i = 0; i < NumberOfDocuments; i++)
                    bulk.Store(new Item { Tag = $"tag{i % 10}", Name = $"name{i:D6}", Price = i, Grp = i % 100 }, $"items/{i}");
            }

            Store.Maintenance.Send(new PutIndexesOperation(
                new IndexDefinition
                {
                    Name = "Items/Compound",
                    Maps = { "from i in docs.Items select new { i.Tag, i.Name, i.Price, i.Grp }" },
                    Configuration = new IndexConfiguration { ["Indexing.Static.SearchEngineType"] = "Corax" },
                    CompoundFields = [["Tag", "Price"], ["Tag", "Name"]]
                },
                new IndexDefinition
                {
                    Name = "Items/ByPrice",
                    Maps = { "from i in docs.Items select new { i.Tag, i.Name, i.Price, i.Grp }" },
                    Configuration = new IndexConfiguration { ["Indexing.Static.SearchEngineType"] = "Corax" }
                }));
        }

        using (var session = Store.OpenSession())
        {
            session.Query<Item>("Items/Compound").Customize(x => x.WaitForNonStaleResults(TimeSpan.FromMinutes(5))).Take(0).ToList();
            session.Query<Item>("Items/ByPrice").Customize(x => x.WaitForNonStaleResults(TimeSpan.FromMinutes(5))).Take(0).ToList();
        }

        if (isBuilt == false)
            File.WriteAllText(marker, string.Empty);

        _nativeBytesAfterSetup = NativeMemory.TotalAllocatedMemory;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Console.WriteLine($"Native memory retained delta: {NativeMemory.TotalAllocatedMemory - _nativeBytesAfterSetup} bytes");
        _instance.Dispose();
    }

    [Benchmark(Baseline = true)]
    [Arguments(50)]
    [Arguments(500)]
    public int StringCompoundAutomatic(int pageSize) => Run($"from index 'Items/Compound' where Tag = 'tag1' order by Name limit {pageSize}", null, pageSize);

    [Benchmark]
    [Arguments(50)]
    [Arguments(500)]
    public int StringCompoundBitmap(int pageSize) => Run($"from index 'Items/Compound' where Tag = 'tag1' order by Name limit {pageSize}", "BitmapPipeline", pageSize);

    [Benchmark]
    [Arguments(50, 10_000)]
    [Arguments(500, 10_000)]
    [Arguments(50, 90_000)]
    [Arguments(500, 90_000)]
    public int NumericCompoundAutomatic(int pageSize, int lowerBound) => Run(
        $"from index 'Items/Compound' where Tag = 'tag1' and Price >= {lowerBound} order by Price as long limit {pageSize}", null, pageSize);

    [Benchmark]
    [Arguments(50, 10_000)]
    [Arguments(500, 10_000)]
    [Arguments(50, 90_000)]
    [Arguments(500, 90_000)]
    public int NumericCompoundBitmap(int pageSize, int lowerBound) => Run(
        $"from index 'Items/Compound' where Tag = 'tag1' and Price >= {lowerBound} order by Price as long limit {pageSize}", "BitmapPipeline", pageSize);

    [Benchmark]
    [Arguments(50, 10_000)]
    [Arguments(500, 10_000)]
    [Arguments(50, 90_000)]
    [Arguments(500, 90_000)]
    public int NumericCompoundForcedScan(int pageSize, int lowerBound) => Run(
        $"from index 'Items/Compound' where Tag = 'tag1' and Price >= {lowerBound} order by Price as long limit {pageSize}", "CompoundSortedScan", pageSize);

    [Benchmark]
    [Arguments(50, 10_000)]
    [Arguments(500, 10_000)]
    [Arguments(50, 90_000)]
    [Arguments(500, 90_000)]
    public int FieldRangeAutomatic(int pageSize, int lowerBound) => Run(
        $"from index 'Items/ByPrice' where Price > {lowerBound} and Grp < 50 order by Price as long limit {pageSize}", null, pageSize);

    [Benchmark]
    [Arguments(50, 10_000)]
    [Arguments(500, 10_000)]
    [Arguments(50, 90_000)]
    [Arguments(500, 90_000)]
    public int FieldRangeBitmap(int pageSize, int lowerBound) => Run(
        $"from index 'Items/ByPrice' where Price > {lowerBound} and Grp < 50 order by Price as long limit {pageSize}", "BitmapPipeline", pageSize);

    private int Run(string rql, string strategy, int expectedCount)
    {
        using var session = Store.OpenSession();
        var query = session.Advanced.RawQuery<Item>(rql).NoCaching();
        if (strategy != null)
            query = query.AddParameter("rvn_corax_strategy", strategy);
        int count = query.ToList().Count;
        if (count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} rows for [{rql}], strategy {strategy ?? "automatic"}, but got {count}.");
        return count;
    }

    public class Item
    {
        public string Id { get; set; }
        public string Tag { get; set; }
        public string Name { get; set; }
        public long Price { get; set; }
        public int Grp { get; set; }
    }
}
