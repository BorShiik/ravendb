using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.Json;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues;

public class RavenDB_27920_RegressionMatrix : RavenTestBase
{
    public RavenDB_27920_RegressionMatrix(ITestOutputHelper output) : base(output)
    {
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void MixedNumericFirstComponentMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Age");
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            commands.Put("items/long", null, new Dictionary<string, object> { ["Tag"] = 5L, ["Age"] = 1L }, meta);
            commands.Put("items/fraction", null, new Dictionary<string, object> { ["Tag"] = 5.5, ["Age"] = 2L }, meta);
            commands.Put("items/double", null, new Dictionary<string, object> { ["Tag"] = 5.0, ["Age"] = 3L }, meta);
        }
        Indexes.WaitForIndexing(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = 5 order by Age as long", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void MixedNumericSecondComponentMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Age");
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            commands.Put("items/long", null, new Dictionary<string, object> { ["Tag"] = "a", ["Age"] = 6L }, meta);
            commands.Put("items/fraction", null, new Dictionary<string, object> { ["Tag"] = "a", ["Age"] = 6.5 }, meta);
            commands.Put("items/double", null, new Dictionary<string, object> { ["Tag"] = "a", ["Age"] = 7.0 }, meta);
        }
        Indexes.WaitForIndexing(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = 'a' and Age >= 6 order by Age as long", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void BooleanFirstComponentMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Age");
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            commands.Put("items/bool", null, new Dictionary<string, object> { ["Tag"] = true, ["Age"] = 1L }, meta);
            commands.Put("items/string", null, new Dictionary<string, object> { ["Tag"] = "true", ["Age"] = 2L }, meta);
        }
        Indexes.WaitForIndexing(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = true order by Age as long", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void BooleanSecondComponentRangeMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Name");
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            commands.Put("items/bool", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = false }, meta);
            commands.Put("items/string", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = "n000" }, meta);
        }
        Indexes.WaitForIndexing(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = 'a' and Name < 'a' order by Name", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void BooleanSecondComponentOrderMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Name");
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            commands.Put("items/bool", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = false }, meta);
            commands.Put("items/first", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = "aaa" }, meta);
            commands.Put("items/last", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = "n000" }, meta);
        }
        Indexes.WaitForIndexing(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = 'a' order by Name", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void DoublePrefixOrderAcrossZeroMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Score");
        SeedScores(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = 'a' order by Score as double", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void DoubleRangeAcrossZeroMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        CreateCompoundIndex(store, "Tag", "Score");
        SeedScores(store);

        AssertMatchesBitmap(store, "from index 'Items/Compound' where Tag = 'a' and Score between -2.0 and 2.0 order by Score as double", "CompoundSortedScan");
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void FullDoubleScanIncludesNegativeInfinity(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByRatio",
            Maps = { "from d in docs.Items select new { Ratio = (double)d.Total / (double)d.Count }" }
        }));
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            commands.Put("items/negative-inf", null, new Dictionary<string, object> { ["Total"] = -1L, ["Count"] = 0L }, meta);
            commands.Put("items/zero", null, new Dictionary<string, object> { ["Total"] = 0L, ["Count"] = 1L }, meta);
            commands.Put("items/max", null, new Dictionary<string, object> { ["Total"] = double.MaxValue, ["Count"] = 1L }, meta);
            commands.Put("items/positive-inf", null, new Dictionary<string, object> { ["Total"] = 1L, ["Count"] = 0L }, meta);
        }
        Indexes.WaitForIndexing(store);

        foreach (var order in new[] { "Ratio as double", "Ratio as double desc" })
            AssertMatchesBitmap(store, $"from index 'Items/ByRatio' order by {order}", "FieldSortedScan");
    }

    private void SeedScores(IDocumentStore store)
    {
        using var commands = store.Commands();
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        commands.Put("items/negative", null, new Dictionary<string, object> { ["Tag"] = "a", ["Score"] = -1.0 }, meta);
        commands.Put("items/zero", null, new Dictionary<string, object> { ["Tag"] = "a", ["Score"] = 0.0 }, meta);
        commands.Put("items/positive", null, new Dictionary<string, object> { ["Tag"] = "a", ["Score"] = 1.0 }, meta);
        Indexes.WaitForIndexing(store);
    }

    private static void CreateCompoundIndex(IDocumentStore store, string first, string second)
    {
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/Compound",
            Maps = { $"from d in docs.Items select new {{ d.{first}, d.{second} }}" },
            CompoundFields = [[first, second]]
        }));
    }

    private static void AssertMatchesBitmap(IDocumentStore store, string rql, string strategy)
    {
        var bitmap = Run(store, rql, "BitmapPipeline");
        RavenTestHelper.AssertAll(() => rql,
            () => Assert.Equal(bitmap, Run(store, rql, strategy)),
            () => Assert.Equal(bitmap, Run(store, rql, strategy: null)));
    }

    private static string[] Run(IDocumentStore store, string rql, string strategy)
    {
        using var session = store.OpenSession();
        var query = session.Advanced.RawQuery<Item>($"{rql} include timings()")
            .NoCaching()
            .Timings(out QueryTimings _);
        if (strategy != null)
            query = query.AddParameter("rvn_corax_strategy", strategy);
        if (strategy == "BitmapPipeline")
            query = query.AddParameter("rvn_corax_entry_scan", -1L);
        return query.ToList().Select(x => x.Id).ToArray();
    }

    private class Item
    {
        public string Id { get; set; }
    }
}
