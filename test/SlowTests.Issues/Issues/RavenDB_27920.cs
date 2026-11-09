using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Timings;
using Raven.Client.Documents.Session;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues;

public class RavenDB_27920 : RavenTestBase
{
    public RavenDB_27920(ITestOutputHelper output) : base(output)
    {
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData("where Age > 10 order by Age as long", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("where Age > 10 order by Age as long desc", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("where Age > 10 order by Age as long, Name", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("where Name > 'n050' order by Name", SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void FieldSortedScanSkipsDocumentsWithoutTheSortValue(Options options, string query)
    {
        using var store = GetDocumentStore(options);
        Seed(store);

        var bitmap = Run(store, query, "rvn_corax_strategy", "BitmapPipeline");
        var scan = Run(store, query, "rvn_corax_strategy", "FieldSortedScan");
        Assert.Equal("FieldSortedScan", scan.Hint);
        Assert.Equal(bitmap.Ids, scan.Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData("not Age > 50", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not Score >= 50.5", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not Score between 20.5 and 60.5", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not Name > 'n050'", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not startsWith(Name, 'n01')", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not endsWith(Name, '4')", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not exists(Tags)", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not exact(Name = 'n014')", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not (Score > 20.5 and Score < 30.5)", SearchEngineMode = RavenSearchEngineMode.Corax)]
    [RavenData("not exact(Name = 'n014' or Tag = '1')", SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void NegatedClauseKeepsSortedScanAndEntryScan(Options options, string negated)
    {
        using var store = GetDocumentStore(options);
        Seed(store);

        string sorted = $"where Age > 10 and {negated} order by Age as long";
        var bitmap = Run(store, sorted, "rvn_corax_strategy", "BitmapPipeline");
        var scan = Run(store, sorted, "rvn_corax_strategy", "FieldSortedScan");
        Assert.Equal("FieldSortedScan", scan.Hint);
        Assert.Equal(bitmap.Ids, scan.Ids);

        string filtered = $"where Tag = '4' and {negated}";
        var noScan = Run(store, filtered, "rvn_corax_entry_scan", -1L);
        int gate = Enumerable.Range(0, 16).FirstOrDefault(g => Run(store, filtered, "rvn_corax_entry_scan", (long)g).EntryScanAt == g, -1);
        Assert.True(gate >= 0, "the entry scan has no gate for this query");
        Assert.Equal(noScan.Ids.Order(), Run(store, filtered, "rvn_corax_entry_scan", (long)gate).Ids.Order());
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
    public void SortedQueriesOnTheFilteredFieldAgreeWithLucene(Options options)
    {
        using var store = GetDocumentStore(options);
        Seed(store);

        using (var session = store.OpenSession())
        {
            var page = session.Advanced.RawQuery<Item>("from index 'Items/ByAll' where Age > 10 order by Age as long limit 5")
                .Statistics(out QueryStatistics stats)
                .ToList();
            Assert.Equal(Items(11, 12, 13, 14, 15), page.Select(x => x.Id));
            Assert.Equal(96, stats.TotalResults);
        }

        Assert.Equal(["items/e9", "items/e8", "items/e7", "items/e5", "items/e4"], Ids(store, "where Age > 10 order by Age as long desc limit 5"));
        Assert.Equal(Items(Enumerable.Range(11, 40).ToArray()), Ids(store, "where Age > 10 and not Age > 50 order by Age as long"));
        Assert.Equal(Items(Enumerable.Range(11, 39).ToArray()).Concat(["items/e2", "items/e3", "items/e7"]),
            Ids(store, "where Age > 10 and not Score >= 50.5 order by Age as long"));
        Assert.Equal(["items/e5", "items/e9"], Ids(store, "where Age > 10 and not exists(Tags) order by Age as long"));
        Assert.Equal(Items(20, 21, 22, 23, 24), Ids(store, "where Age > 10 and not startsWith(Name, 'n01') order by Age as long limit 5"));
        Assert.Equal(Items(10, 11, 12, 15, 16, 17, 18, 19, 20), Ids(store, "where Age between 10 and 20 and not (Score > 12.5 and Score < 15.5) order by Age as long"));

        static string[] Items(params int[] ids) => ids.Select(i => $"items/{i}").ToArray();
        static string[] Ids(IDocumentStore store, string where) => RunRql(store, $"from index 'Items/ByAll' {where}").Ids;
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void SortedScanCostGateCountsTheEntriesANegationPasses(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByAgeBucket",
            Maps = { "from d in docs.Items select new { d.Age, d.Bucket, d.Tags, Ratio = (double)d.Total / (double)d.Count }" }
        }));

        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            for (int i = 0; i < 2000; i++)
            {
                commands.Put($"items/{i}", null, new Dictionary<string, object>
                {
                    ["Age"] = (long)i,
                    ["Bucket"] = (long)(i % 100),
                    ["Tags"] = i % 5 < 3 ? new[] { "a", "b" } : new[] { "c" },
                    ["Total"] = 1L,
                    ["Count"] = i < 1980 ? 0L : 2L
                }, meta);
            }
        }

        Indexes.WaitForIndexing(store);

        // half of the index passes the negation, so a few entries of the sorted walk fill the page
        var half = RunRql(store, "from index 'Items/ByAgeBucket' where Age > 10 and not Bucket > 49 order by Age as long limit 5");
        Assert.Equal("FieldSortedScan", half.Hint);
        Assert.Equal(["items/11", "items/12", "items/13", "items/14", "items/15"], half.Ids);

        // one entry in a hundred passes, so the walk would read hundreds of entries for the page
        var few = RunRql(store, "from index 'Items/ByAgeBucket' where Age > 10 and not Bucket > 0 order by Age as long limit 5");
        Assert.Equal("BitmapPipeline", few.Hint);
        Assert.Equal(["items/100", "items/200", "items/300", "items/400", "items/500"], few.Ids);

        // exists() is estimated at the whole index, so nothing says its negation passes anything, and a walk that finds nothing reads the whole range
        var unsized = RunRql(store, "from index 'Items/ByAgeBucket' where Age > 10 and not exists(Bucket) order by Age as long limit 5");
        Assert.Equal("BitmapPipeline", unsized.Hint);
        Assert.Empty(unsized.Ids);

        // the IN terms share documents, so their counts add up to the index size as well
        var overlapping = RunRql(store, "from index 'Items/ByAgeBucket' where Age > 10 and not Tags in ('a', 'b') order by Age as long limit 5");
        Assert.Equal("BitmapPipeline", overlapping.Hint);
        Assert.Equal(["items/13", "items/14", "items/18", "items/19", "items/23"], overlapping.Ids);

        // a negation counts one passing entry when its operand's estimate is not guaranteed to bound it from above: a sampled range, a null IN value
        // or a 'NULL' bound (both add the null posting list) and a group (Estimate counts a nested negation by its operand); a group that folds
        // to a constant counts it exactly
        foreach (var (negated, page) in new (string, string[])[]
                 {
                     ("not Age > 600", ["items/11", "items/12", "items/13", "items/14", "items/15"]),
                     ("not Bucket in (null, 1)", ["items/11", "items/12", "items/13", "items/14", "items/15"]),
                     ("not Tags between 'c' and 'NULL'", ["items/11", "items/12", "items/15", "items/16", "items/17"]),
                     ("not exact(Bucket = 5 or not Bucket > 98)", ["items/99", "items/199", "items/299", "items/399", "items/499"]),
                     ("not exact(Bucket = 5 or not endsWith(Tags, 'z'))", []),
                     ("not (exists(Bucket) and (Bucket = 5 or true))", [])
                 })
        {
            var result = RunRql(store, $"from index 'Items/ByAgeBucket' where Age > 10 and {negated} order by Age as long limit 5");
            Assert.True(result.Hint == "BitmapPipeline", $"{negated}: {result.Hint}");
            Assert.Equal(page, result.Ids);
        }

        // `or true` passes every entry, so the walk fills the page at once
        var allPass = RunRql(store, "from index 'Items/ByAgeBucket' where Age > 10 and (Bucket = 5 or true) order by Age as long limit 5");
        Assert.Equal("FieldSortedScan", allPass.Hint);
        Assert.Equal(["items/11", "items/12", "items/13", "items/14", "items/15"], allPass.Ids);

        // Total / 0 indexes +∞, past the double.MaxValue where the term-count estimate stops, so only the walk over the range counts it
        var infinite = RunRql(store, "from index 'Items/ByAgeBucket' where Age > 10 and not Ratio > 1.5 order by Age as long limit 5");
        Assert.Equal("BitmapPipeline", infinite.Hint);
        Assert.Equal(["items/1980", "items/1981", "items/1982", "items/1983", "items/1984"], infinite.Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void EqualsNullDoesNotDriveTheSortedWalk(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByTags",
            Maps = { "from d in docs.Items select new { d.Tags, d.Other }" }
        }));

        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            for (int i = 0; i < 10; i++)
                commands.Put($"items/{i}", null, new Dictionary<string, object> { ["Tags"] = new[] { "a", "x" + i } }, meta);
            commands.Put("items/null1", null, new Dictionary<string, object> { ["Tags"] = null }, meta);
            commands.Put("items/null2", null, new Dictionary<string, object> { ["Tags"] = null }, meta);
            commands.Put("items/none", null, new Dictionary<string, object> { ["Other"] = 1L }, meta);
        }

        Indexes.WaitForIndexing(store);

        // Tags is multi-valued, so `Tags = null` keeps its sort key and can drive the walk when forced
        const string query = "from index 'Items/ByTags' where Tags = null order by Tags";
        var bitmap = RunRql(store, query, "BitmapPipeline");
        var forced = RunRql(store, query, "FieldSortedScan");
        Assert.Equal("BitmapPipeline", forced.Hint);
        Assert.Equal(["items/null1", "items/null2"], bitmap.Ids.Order());
        Assert.Equal(bitmap.Ids, forced.Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CompoundSortedScanChecksOnlyNotEqualsAndNotInPerDocument(Options options)
    {
        using var store = GetDocumentStore(options);
        Seed(store);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByTagAge",
            Maps = { "from d in docs.Items select new { d.Tag, d.Age, d.Name, d.Tags }" },
            CompoundFields = [["Tag", "Age"]]
        }));
        Indexes.WaitForIndexing(store);

        // the compound walk checks the negations it always did, any other one keeps the query on the bitmap
        foreach (var (negated, hint) in new[]
                 {
                     ("Name != 'n014'", "CompoundSortedScan"),
                     ("not Name in ('n024', 'n034')", "CompoundSortedScan"),
                     ("not Tags all in ('4', 'x0')", "CompoundSortedScan"),
                     ("not Age > 50", "BitmapPipeline"),
                     ("(Name = 'n014' or not Age > 50)", "BitmapPipeline")
                 })
        {
            string query = $"from index 'Items/ByTagAge' where Tag = '4' and Age >= 0 and {negated} order by Age as long";
            var scan = RunRql(store, query, "CompoundSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal(hint, scan.Hint),
                () => Assert.Equal(RunRql(store, query, "BitmapPipeline").Ids, scan.Ids));
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void OpenBetweenDrivingTheScanSkipsDocumentsWithoutTheField(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByName",
            Maps = { "from d in docs.Items select new { d.Name, d.Other }" }
        }));

        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            for (int i = 0; i < 100; i++)
                commands.Put($"items/{i}", null, new Dictionary<string, object> { ["Name"] = $"n{i:D3}" }, meta);
            commands.Put("items/unnamed", null, new Dictionary<string, object> { ["Other"] = 1L }, meta);
        }

        Indexes.WaitForIndexing(store);

        var openHigh = RunRql(store, "from index 'Items/ByName' where Name between 'n095' and 'NULL' order by Name", "FieldSortedScan");
        Assert.Equal("FieldSortedScan", openHigh.Hint);
        Assert.Equal(["items/95", "items/96", "items/97", "items/98", "items/99"], openHigh.Ids);

        var openLow = RunRql(store, "from index 'Items/ByName' where Name between '*' and 'n004' order by Name", "FieldSortedScan");
        Assert.Equal("FieldSortedScan", openLow.Hint);
        Assert.Equal(["items/0", "items/1", "items/2", "items/3", "items/4"], openLow.Ids);
    }

    private void Seed(IDocumentStore store)
    {
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByAll",
            Maps = { "from d in docs.Items select new { d.Name, d.Tag, d.Age, d.Score, d.Tags }" }
        }));

        using var commands = store.Commands();
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        for (int i = 0; i < 100; i++)
        {
            commands.Put($"items/{i}", null, new Dictionary<string, object>
            {
                ["Name"] = $"n{i:D3}",
                ["Tag"] = (i % 10).ToString(),
                ["Age"] = (long)i,
                ["Score"] = i + 0.5,
                ["Tags"] = new[] { (i % 10).ToString(), "x" + i % 3 }
            }, meta);
        }

        var edges = new (string Field, object Value)[] { ("Age", null), ("Age", "abc"), ("Score", null), ("Score", "abc"), ("Name", null), ("Tags", System.Array.Empty<string>()) };
        var missing = new[] { "Age", "Score", "Name", "Tags" };
        for (int k = 0; k < edges.Length + missing.Length; k++)
        {
            var doc = new Dictionary<string, object> { ["Name"] = $"n9{k}", ["Tag"] = "4", ["Age"] = 200L + k, ["Score"] = 60.5, ["Tags"] = new[] { "4", "x0" } };
            if (k < edges.Length)
                doc[edges[k].Field] = edges[k].Value;
            else
                doc.Remove(missing[k - edges.Length]);
            commands.Put($"items/e{k}", null, doc, meta);
        }

        Indexes.WaitForIndexing(store);
    }

    private static (string[] Ids, string Hint, int EntryScanAt) Run(IDocumentStore store, string where, string parameter, object value)
    {
        using var session = store.OpenSession();
        var ids = session.Advanced.RawQuery<Item>($"from index 'Items/ByAll' {where} include timings()")
            .NoCaching()
            .Timings(out QueryTimings timings)
            .AddParameter(parameter, value)
            .ToList()
            .Select(x => x.Id)
            .ToArray();

        var plan = (QueryInspectionNode)timings.QueryPlan;
        string hint = null;
        Find(plan, "CompiledQuery")?.Parameters?.TryGetValue("OptimizationHint", out hint);
        int entryScanAt = -1;
        var entryScan = Find(plan, "EntryScan");
        if (entryScan?.Parameters != null && entryScan.Parameters.TryGetValue("Taken", out var taken) && taken == "True")
            int.TryParse(entryScan.Parameters["SwitchedAfterClauses"], out entryScanAt);
        return (ids, hint, entryScanAt);
    }

    private static (string[] Ids, string Hint) RunRql(IDocumentStore store, string rql, string strategy = null)
    {
        using var session = store.OpenSession();
        var query = session.Advanced.RawQuery<Item>($"{rql} include timings()")
            .NoCaching()
            .Timings(out QueryTimings timings);
        if (strategy != null)
            query = query.AddParameter("rvn_corax_strategy", strategy);
        if (strategy == "BitmapPipeline")
            query = query.AddParameter("rvn_corax_entry_scan", -1L); // the reference checks no clause per document

        var ids = query.ToList().Select(x => x.Id).ToArray();
        string hint = null;
        Find((QueryInspectionNode)timings.QueryPlan, "CompiledQuery")?.Parameters?.TryGetValue("OptimizationHint", out hint);
        return (ids, hint);
    }

    private static QueryInspectionNode Find(QueryInspectionNode node, string operation)
    {
        if (node == null || node.Operation == operation)
            return node;
        return node.Children?.Select(c => Find(c, operation)).FirstOrDefault(x => x != null);
    }

    private class Item
    {
        public string Id { get; set; }
    }
}
