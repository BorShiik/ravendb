using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Raven.Client.Json;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers.Collation.Cultures;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues;

public class RavenDB_27920_SafetyGates : RavenTestBase
{
    public RavenDB_27920_SafetyGates(ITestOutputHelper output) : base(output)
    {
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void FullSingleKeyMultiValuedPrimaryMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        SeedMultiValuedPrimary(store);
        foreach (string order in new[] { "Tags", "Tags desc" })
            AssertMatchesBitmap(store, $"from index 'Items/ByTags' order by {order} limit 10", statistics: false);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void ForcedNonFullMultiValuedPrimaryMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        SeedMultiValuedPrimary(store);
        foreach (bool statistics in new[] { false, true })
            AssertMatchesBitmap(store, "from index 'Items/ByTags' where Tags >= 't0' order by Tags desc, S limit 10 offset 3", statistics);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void ForcedFullScanWithMissingValuesMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/Missing", Maps = { "from d in docs.Items select new { d.N, d.Always }" }
        }));
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            for (int i = 0; i < 100; i++)
            {
                var doc = new Dictionary<string, object> { ["Always"] = 1L };
                if (i % 4 != 0)
                    doc["N"] = i % 9 == 0 ? null : (long)i;
                commands.Put($"items/{i:D3}", null, doc, meta);
            }
        }
        Indexes.WaitForIndexing(store);
        foreach (string order in new[] { "N as long", "N as long desc" })
            AssertMatchesBitmap(store, $"from index 'Items/Missing' order by {order} limit 20", statistics: false);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CustomAnalyzerDisablesSortElisionAndMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/Collation",
            Maps = { "from d in docs.Items select new { d.Tag, d.Name }" },
            Fields = { ["Name"] = new IndexFieldOptions { Indexing = FieldIndexing.Search, Analyzer = typeof(EnCollationAnalyzer).AssemblyQualifiedName } },
            CompoundFields = [["Tag", "Name"]]
        }));
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            commands.Put("items/bool", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = false }, meta);
            commands.Put("items/first", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = "aaa" }, meta);
            commands.Put("items/last", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = "zzz" }, meta);
        }
        Indexes.WaitForIndexing(store);

        const string rql = "from index 'Items/Collation' where Tag = 'a' order by Name";
        AssertMatchesBitmap(store, rql, statistics: false, strategy: "CompoundSortedScan");
        var forced = Run(store, rql, "CompoundSortedScan", statistics: false);
        Assert.NotNull(Find(forced.Plan, "SortingMatch"));
    }

    private void SeedMultiValuedPrimary(IDocumentStore store)
    {
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByTags", Maps = { "from d in docs.Items select new { d.Tags, d.S }" }
        }));
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            for (int i = 0; i < 128; i++)
            {
                commands.Put($"items/{i:D3}", null, new Dictionary<string, object>
                {
                    ["Tags"] = new[] { $"t{i % 4}", $"t{i / 4 % 4}" }, ["S"] = $"s{i % 7}"
                }, meta);
            }
        }
        Indexes.WaitForIndexing(store);
    }

    private static void AssertMatchesBitmap(IDocumentStore store, string rql, bool statistics, string strategy = "FieldSortedScan")
    {
        var bitmap = Run(store, rql, "BitmapPipeline", statistics);
        var forced = Run(store, rql, strategy, statistics);
        var automatic = Run(store, rql, null, statistics);
        RavenTestHelper.AssertAll(() => rql,
            () => Assert.Equal(bitmap.Ids, forced.Ids),
            () => Assert.Equal(bitmap.Ids, automatic.Ids),
            () => Assert.Equal(bitmap.Total, forced.Total),
            () => Assert.Equal(bitmap.Total, automatic.Total));
    }

    private static (string[] Ids, long Total, QueryInspectionNode Plan) Run(IDocumentStore store, string rql, string strategy, bool statistics)
    {
        using var session = store.OpenSession();
        var query = session.Advanced.RawQuery<Item>($"{rql} include timings()").NoCaching().Timings(out QueryTimings timings);
        if (strategy != null)
            query = query.AddParameter("rvn_corax_strategy", strategy);
        if (strategy == "BitmapPipeline")
            query = query.AddParameter("rvn_corax_entry_scan", -1L);
        QueryStatistics stats = null;
        if (statistics)
            query = query.Statistics(out stats);
        var ids = query.ToList().Select(x => x.Id).ToArray();
        return (ids, stats?.TotalResults ?? -1, timings.QueryPlan as QueryInspectionNode);
    }

    private static QueryInspectionNode Find(QueryInspectionNode node, string operation)
    {
        if (node == null || node.Operation == operation)
            return node;
        return node.Children?.Select(child => Find(child, operation)).FirstOrDefault(child => child != null);
    }

    private class Item
    {
        public string Id { get; set; }
    }
}
