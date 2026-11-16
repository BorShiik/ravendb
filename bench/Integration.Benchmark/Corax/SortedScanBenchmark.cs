using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace Integration.Benchmark.Corax;

[SimpleJob(warmupCount: 2, iterationCount: 8)]
[MemoryDiagnoser]
public class SortedScanBenchmark
{
    private const int NumberOfDocuments = 100_000;
    private RavenDbInstance _instance;
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
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _instance.Dispose();

    [Benchmark(Baseline = true)]
    public int StringCompoundAutomatic() => Run("from index 'Items/Compound' where Tag = 'tag1' order by Name limit 50", null);

    [Benchmark]
    public int StringCompoundBitmap() => Run("from index 'Items/Compound' where Tag = 'tag1' order by Name limit 50", "BitmapPipeline");

    [Benchmark]
    public int NumericCompoundAutomatic() => Run("from index 'Items/Compound' where Tag = 'tag1' and Price >= 50000 order by Price as long limit 50", null);

    [Benchmark]
    public int NumericCompoundBitmap() => Run("from index 'Items/Compound' where Tag = 'tag1' and Price >= 50000 order by Price as long limit 50", "BitmapPipeline");

    [Benchmark]
    public int NumericCompoundForcedScan() => Run("from index 'Items/Compound' where Tag = 'tag1' and Price >= 50000 order by Price as long limit 50", "CompoundSortedScan");

    [Benchmark]
    public int FieldRangeAutomatic() => Run("from index 'Items/ByPrice' where Price > 50000 and Grp < 50 order by Price as long limit 50", null);

    [Benchmark]
    public int FieldRangeBitmap() => Run("from index 'Items/ByPrice' where Price > 50000 and Grp < 50 order by Price as long limit 50", "BitmapPipeline");

    private int Run(string rql, string strategy)
    {
        using var session = Store.OpenSession();
        var query = session.Advanced.RawQuery<Item>(rql).NoCaching();
        if (strategy != null)
            query = query.AddParameter("rvn_corax_strategy", strategy);
        return query.ToList().Count;
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
