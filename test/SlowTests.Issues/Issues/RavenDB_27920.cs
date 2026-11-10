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
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void NegatedExactNotEqualsIsAnEquality(Options options)
    {
        using var store = GetDocumentStore(options);
        Seed(store);

        // not exact(F != v) is F = v: alone, inside a negated group (not A or F = v) and inside an OR group
        var tag3 = Enumerable.Range(0, 10).Select(i => $"items/{i * 10 + 3}").ToArray();
        foreach (var (where, expected) in new (string, string[])[]
                 {
                     ("where Age >= 0 and not exact(Tag != '3')", tag3),
                     ("where startsWith(Name, 'n0') and not (Age >= 0 and exact(Tag != '3'))", tag3),
                     ("where Age >= 0 and (Tag = '3' or not exact(Name != 'n014'))", [.. tag3, "items/14"])
                 })
        {
            int gate = Enumerable.Range(0, 16).FirstOrDefault(g => Run(store, where, "rvn_corax_entry_scan", (long)g).EntryScanAt == g, -1);
            RavenTestHelper.AssertAll(() => where,
                () => Assert.Equal(expected.Order(), RunRql(store, $"from index 'Items/ByAll' {where}").Ids.Order()),
                () => Assert.Equal(expected.Order(), RunRql(store, $"from index 'Items/ByAll' {where}", "BitmapPipeline").Ids.Order()),
                () => Assert.True(gate >= 0, "the entry scan has no gate for this query"),
                () => Assert.Equal(expected.Order(), Run(store, where, "rvn_corax_entry_scan", (long)gate).Ids.Order()));
        }

        var walk = RunRql(store, "from index 'Items/ByAll' where Age >= 0 and (Tag = '3' or not exact(Name != 'n014')) order by Age as long", "FieldSortedScan");
        Assert.Equal("FieldSortedScan", walk.Hint);
        Assert.Equal(new[] { 3, 13, 14, 23, 33, 43, 53, 63, 73, 83, 93 }.Select(i => $"items/{i}"), walk.Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
    public void NegatedWrapperOverAGroupNegatesTheGroup(Options options)
    {
        using var store = GetDocumentStore(options);
        Seed(store);

        // items/14 is the one document with Name n014 and Tag 4; every edge document has Tag 4
        var numbered = Enumerable.Range(0, 100).ToArray();
        var edges = Enumerable.Range(0, 10).Select(k => $"items/e{k}").ToArray();
        string[] ageAtLeast0 = [.. Items(numbered), "items/e2", "items/e3", "items/e4", "items/e5", "items/e7", "items/e8", "items/e9"];
        string[] allButItem14 = ageAtLeast0.Except(["items/14"]).ToArray();
        foreach (var (where, expected) in new (string, string[])[]
                 {
                     ("where Age >= 0 and not exact(Name = 'n014' and Tag = '4')", allButItem14),
                     ("where Age >= 0 and not boost(Name = 'n014' and Tag = '4', 2)", allButItem14),
                     ("where Age >= 0 and not when($on = true, Name = 'n014' and Tag = '4')", allButItem14),
                     ("where Age >= 0 and not exact(Name != 'n014' and Tag = '4')", [.. Items(numbered.Where(i => i % 10 != 4).ToArray()), "items/14"]),
                     ("where Tag = '3' or not exact(Name = 'n014' or Tag = '4')", Items(numbered.Where(i => i % 10 != 4).ToArray())),
                     ("where Tag = '3' or not exact(Name = 'n014' and Tag = '4')", [.. Items(numbered.Where(i => i != 14).ToArray()), .. edges]),
                     ("where Tag = '4' and (endsWith(Name, '7') or not exact(Age > 50 and Score > 20.5))",
                         [.. Items(4, 14, 24, 34, 44), "items/e0", "items/e1", "items/e2", "items/e3", "items/e6", "items/e7"])
                 })
        {
            RavenTestHelper.AssertAll(() => where,
                () => Assert.Equal(expected.Order(), Run(store, where, "on", true).Ids.Order()));
        }

        static string[] Items(params int[] ids) => ids.Select(i => $"items/{i}").ToArray();
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
        // or a 'NULL' bound (both add the null posting list); a group that folds to a constant counts it exactly
        foreach (var (negated, page) in new (string, string[])[]
                 {
                     ("not Age > 600", ["items/11", "items/12", "items/13", "items/14", "items/15"]),
                     ("not Bucket in (null, 1)", ["items/11", "items/12", "items/13", "items/14", "items/15"]),
                     ("not Tags between 'c' and 'NULL'", ["items/11", "items/12", "items/15", "items/16", "items/17"]),
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

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void TwoKeyWalkTruncatesAGroupAmongTheEntriesThatPassTheResidual(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Products/ByCatPrice",
            Maps = { "from p in docs.Products select new { p.Cat, p.Price, p.Grp }" }
        }));
        using (var bulk = store.BulkInsert())
        {
            for (int i = 0; i < 3000; i++)
                bulk.Store(new Product { Cat = "a", Price = i, Grp = $"g{i * 7 % 3000:D4}" }, $"products/{i}");
        }
        Indexes.WaitForIndexing(store);

        foreach (var (filter, first, step) in new[]
                 {
                     ("Price >= 100 order by Cat, Price as long", 100, 1),
                     ("Price between 1100 and 1200 order by Cat, Price as long", 1100, 1),
                     ("Price < 1000 order by Cat, Price as long desc", 999, -1),
                     ("Price >= 2900 order by Cat, Price as long", 2900, 1),
                     ("not Price < 100 order by Cat, Price as long", 100, 1),
                     ("Price < 2900 order by Cat, Price as long desc", 2899, -1),
                     ("Price < 700 order by Cat, Price as long desc", 699, -1),
                     ("Price < 778 order by Cat, Price as long desc", 777, -1)
                 })
        {
            string query = $"from index 'Products/ByCatPrice' where Cat >= 'a' and {filter} limit 10";
            var expected = Enumerable.Range(0, 10).Select(k => $"products/{first + k * step}").ToArray();
            var scan = RunRql(store, query, "FieldSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal("FieldSortedScan", scan.Hint),
                () => Assert.Equal(expected, scan.Ids),
                () => Assert.Equal(expected, RunRql(store, query).Ids));
        }

        // Grp does not follow the id, so a later round brings entries that rank before the kept ones as well as after them
        foreach (string order in new[] { "Grp", "Grp desc" })
        {
            string query = $"from index 'Products/ByCatPrice' where Cat >= 'a' and Price >= 100 order by Cat, {order} limit 10";
            var scan = RunRql(store, query, "FieldSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal("FieldSortedScan", scan.Hint),
                () => Assert.Equal(RunRql(store, query, "BitmapPipeline").Ids, scan.Ids));
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CompoundRangeWalksOnlyTheKeysOfTheDrivingValue(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(
            new IndexDefinition { Name = "Items/ByTagAge", Maps = { "from d in docs.Items select new { d.Tag, d.Age }" }, CompoundFields = [["Tag", "Age"]] },
            new IndexDefinition { Name = "Items/ByTagName", Maps = { "from d in docs.Items select new { d.Tag, d.Name }" }, CompoundFields = [["Tag", "Name"]] }));

        var tags = new[] { "a", "ab", "aé", "an" };
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            for (int i = 0; i < 400; i++)
            {
                var doc = new Dictionary<string, object>
                {
                    ["Tag"] = tags[i % 4],
                    ["Age"] = i % 37 == 0 ? null : (long)(i % 100),
                    ["Name"] = i % 43 == 0 ? null : tags[i % 4] == "an" ? $"0{i % 100:D2}" : $"n{i % 100:D3}"
                };
                if (i % 41 == 0)
                    doc.Remove("Age");
                commands.Put($"items/{i}", null, doc, meta);
            }

            // its key starts with the bytes of 'aé' and lies inside the ranges of that value
            commands.Put("items/shorter", null, new Dictionary<string, object> { ["Tag"] = "a", ["Age"] = 0x43A9000000000005L }, meta);
        }
        Indexes.WaitForIndexing(store);

        var queries = new[]
        {
            "from index 'Items/ByTagAge' where Tag = 'a' and Age < 50 order by Age as long",
            "from index 'Items/ByTagAge' where Tag = 'a' and Age > 10 order by Age as long",
            "from index 'Items/ByTagAge' where Tag = 'a' and Age >= 10 order by Age as long desc",
            "from index 'Items/ByTagAge' where Tag = 'a' and Age between 10 and 50 order by Age as long",
            "from index 'Items/ByTagAge' where Tag = 'aé' and Age < 50 order by Age as long",
            "from index 'Items/ByTagName' where Tag = 'a' and Name < 'n050' order by Name",
            "from index 'Items/ByTagName' where Tag = 'a' and Name > 'n010' order by Name desc",
            "from index 'Items/ByTagName' where Tag = 'a' and Name between 'n010' and 'n050' order by Name"
        };
        // The keys of 'ab', 'aé', 'an' and of items/shorter lie inside most of these ranges, which take the bitmap then. Ages 10 to 50
        // start with the byte 0x80, and no other value continues 'a' with it.
        foreach (var query in queries)
        {
            var scan = RunRql(store, query, "CompoundSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal(query.Contains("between 10 and 50") ? "CompoundSortedScan" : "BitmapPipeline", scan.Hint),
                () => Assert.Equal(RunRql(store, query, "BitmapPipeline").Ids, scan.Ids));
        }

        var emptyMeta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
            commands.Put("items/other-empty", null, new Dictionary<string, object> { ["Tag"] = "a", ["Name"] = "" }, emptyMeta);
        Indexes.WaitForIndexing(store);

        const string aboveEmpty = "from index 'Items/ByTagName' where Tag = 'ab' and Name > 'n010' order by Name desc";
        var walk = RunRql(store, aboveEmpty, "CompoundSortedScan");
        Assert.Equal("CompoundSortedScan", walk.Hint);
        Assert.Equal(RunRql(store, aboveEmpty, "BitmapPipeline").Ids, walk.Ids);

        using (var commands = store.Commands())
            commands.Put("items/empty", null, new Dictionary<string, object> { ["Tag"] = "ab", ["Name"] = "" }, emptyMeta);
        Indexes.WaitForIndexing(store);

        const string withEmpty = "from index 'Items/ByTagName' where Tag = 'ab' and Name < 'n050' order by Name";
        var bitmap = RunRql(store, withEmpty, "BitmapPipeline").Ids;
        Assert.Contains("items/empty", bitmap);
        Assert.Equal(bitmap, RunRql(store, withEmpty, "CompoundSortedScan").Ids);

        // a lone "\u0003" shares the term of the empty string, but its key holds its byte, below the first byte of every other Name
        using (var commands = store.Commands())
        {
            commands.Put("items/u", null, new Dictionary<string, object> { ["Tag"] = "u", ["Name"] = "n001" }, emptyMeta);
            commands.Put("items/u-etx", null, new Dictionary<string, object> { ["Tag"] = "u", ["Name"] = "\u0003" }, emptyMeta);
        }
        Indexes.WaitForIndexing(store);

        const string belowNames = "from index 'Items/ByTagName' where Tag = 'u' and Name < 'n050' order by Name";
        var lone = RunRql(store, belowNames, "CompoundSortedScan");
        Assert.Equal("CompoundSortedScan", lone.Hint);
        Assert.Equal(RunRql(store, belowNames, "BitmapPipeline").Ids, lone.Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CompoundRangeIsWalkedOnlyWhenNoOtherValueHasKeysInside(Options options)
    {
        using var store = GetDocumentStore(options);
        const string map = "from o in docs.Orders select new { o.Customer, o.Amount, o.Qty, o.Title, o.Score }";
        store.Maintenance.Send(new PutIndexesOperation(
            new IndexDefinition { Name = "Orders/ByCustomerAmount", Maps = { map }, CompoundFields = [["Customer", "Amount"]] },
            new IndexDefinition { Name = "Orders/ByCustomerQty", Maps = { map }, CompoundFields = [["Customer", "Qty"]] },
            new IndexDefinition { Name = "Orders/ByCustomerTitle", Maps = { map }, CompoundFields = [["Customer", "Title"]] },
            new IndexDefinition { Name = "Orders/ByCustomerScore", Maps = { map }, CompoundFields = [["Customer", "Score"]] }));

        // every key of customers/10 starts with the bytes of customers/1 followed by '0'
        var meta = new Dictionary<string, object> { ["@collection"] = "Orders" };
        using (var commands = store.Commands())
        {
            for (int i = 0; i < 600; i++)
            {
                commands.Put($"orders/{i}", null, new Dictionary<string, object>
                {
                    ["Customer"] = new[] { "customers/1", "customers/10", "customers/7" }[i % 3], ["Amount"] = (long)(i / 3 + 1), ["Qty"] = (long)(i % 50), ["Title"] = $"t{i / 3:D3}",
                    ["Score"] = i + 0.5
                }, meta);
            }

            commands.Put("orders/no-qty", null, new Dictionary<string, object> { ["Customer"] = "customers/7", ["Amount"] = 5L, ["Title"] = "t999" }, meta);
        }
        Indexes.WaitForIndexing(store);

        foreach (var (query, hint) in new[]
                 {
                     // '0' is below the first byte of every amount, also below the smallest one that ends the open side
                     ("from index 'Orders/ByCustomerAmount' where Customer = 'customers/1' and Amount > 100 order by Amount as long", "CompoundSortedScan"),
                     ("from index 'Orders/ByCustomerAmount' where Customer = 'customers/1' and Amount < 150 order by Amount as long desc", "CompoundSortedScan"),
                     // a Qty of 0 leaves the open side at the first byte 0x00, where the keys of customers/10 are
                     ("from index 'Orders/ByCustomerQty' where Customer = 'customers/1' and Qty < 20 order by Qty as long", "BitmapPipeline"),
                     // a Qty of 0 adds only the first bytes 0x00 and 0xFF, so no key of customers/1 continues customers/10
                     ("from index 'Orders/ByCustomerQty' where Customer = 'customers/10' and Qty > 20 order by Qty as long", "CompoundSortedScan"),
                     // no other value has keys inside, and the key of orders/no-qty, with no Qty, is skipped
                     ("from index 'Orders/ByCustomerQty' where Customer = 'customers/7' and Qty < 20 order by Qty as long", "CompoundSortedScan"),
                     ("from index 'Orders/ByCustomerTitle' where Customer = 'customers/1' and Title < 't100' order by Title", "CompoundSortedScan"),
                     // a double is written by its own bits, so the open side ends at its first byte 0x3F, above the '0' of customers/10
                     ("from index 'Orders/ByCustomerScore' where Customer = 'customers/1' and Score < 150.5 order by Score as double", "CompoundSortedScan")
                 })
        {
            var scan = RunRql(store, query, "CompoundSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal(hint, scan.Hint),
                () => Assert.Equal(hint, RunRql(store, query).Hint),
                () => Assert.Equal(RunRql(store, query, "BitmapPipeline").Ids, scan.Ids),
                () => Assert.Equal(RunWithStatistics(store, query + " limit 7", "BitmapPipeline"), RunWithStatistics(store, query + " limit 7", "CompoundSortedScan")));
        }

        // the key customers/10 + 11 would be a customers/1 order whose Title is '0', and no order has it
        const string fromEmpty = "from index 'Orders/ByCustomerTitle' where Customer = 'customers/10' and Title >= '' order by Title limit 10";
        var fromEmptyScan = RunRql(store, fromEmpty, "CompoundSortedScan");
        Assert.Equal("CompoundSortedScan", fromEmptyScan.Hint);
        using (var session = store.OpenSession())
        {
            // the reference sorts in memory, the index-order sort cannot seek to an empty string (RavenDB-27813)
            var inMemoryIds = session.Advanced.RawQuery<Item>(fromEmpty).NoCaching().AddParameter("rvn_corax_strategy", "BitmapPipeline")
                .AddParameter("rvn_corax_entry_scan", -1L).AddParameter("rvn_corax_sort", "InMemorySort").ToList().Select(x => x.Id).ToArray();
            Assert.Equal(inMemoryIds, fromEmptyScan.Ids);
        }

        // a title starting with '0' widens the open side down to the keys of customers/10
        using (var commands = store.Commands())
            commands.Put("orders/zero", null, new Dictionary<string, object> { ["Customer"] = "customers/7", ["Amount"] = 1L, ["Qty"] = 1L, ["Title"] = "0ld" }, meta);
        Indexes.WaitForIndexing(store);

        const string belowTitle = "from index 'Orders/ByCustomerTitle' where Customer = 'customers/1' and Title < 't100' order by Title";
        var widened = RunRql(store, belowTitle, "CompoundSortedScan");
        Assert.Equal("BitmapPipeline", widened.Hint);
        Assert.Equal(RunRql(store, belowTitle, "BitmapPipeline").Ids, widened.Ids);

        // a bound longer than a key component can hold has no range keys, and the range is not part of the per-document check
        string longBound = "t05" + new string('x', 300);
        using (var session = store.OpenSession())
        {
            const string rql = "from index 'Orders/ByCustomerTitle' where Customer = 'customers/1' and Title < $b order by Title";
            var bitmapIds = session.Advanced.RawQuery<Item>(rql).NoCaching().AddParameter("b", longBound)
                .AddParameter("rvn_corax_strategy", "BitmapPipeline").AddParameter("rvn_corax_entry_scan", -1L).ToList().Select(x => x.Id).ToArray();
            var scanIds = session.Advanced.RawQuery<Item>(rql).NoCaching().AddParameter("b", longBound)
                .AddParameter("rvn_corax_strategy", "CompoundSortedScan").ToList().Select(x => x.Id).ToArray();
            Assert.Equal(60, bitmapIds.Length);
            Assert.Equal(bitmapIds, scanIds);
        }

        // its key customers/1 + '0ld' + 11 is customers/10 + 'ld' + 11, inside the range of customers/10
        using (var commands = store.Commands())
            commands.Put("orders/old", null, new Dictionary<string, object> { ["Customer"] = "customers/1", ["Title"] = "0ld" }, meta);
        Indexes.WaitForIndexing(store);

        const string longerValue = "from index 'Orders/ByCustomerTitle' where Customer = 'customers/10' and Title < 't100' order by Title";
        var inside = RunRql(store, longerValue, "CompoundSortedScan");
        Assert.Equal("BitmapPipeline", inside.Hint);
        Assert.Equal(RunRql(store, longerValue, "BitmapPipeline").Ids, inside.Ids);

        // 2^-15 is written 3F 00 00 00 00 00 00 00, so its key continues the open side's end at the first byte 0x3F with zeros
        using (var commands = store.Commands())
            commands.Put("orders/tiny", null, new Dictionary<string, object> { ["Customer"] = "customers/1", ["Score"] = System.Math.Pow(2, -15) }, meta);
        Indexes.WaitForIndexing(store);

        const string belowScore = "from index 'Orders/ByCustomerScore' where Customer = 'customers/1' and Score < 150.5 order by Score as double";
        var tiny = RunRql(store, belowScore, "CompoundSortedScan");
        Assert.Equal("CompoundSortedScan", tiny.Hint);
        Assert.Equal(RunRql(store, belowScore, "BitmapPipeline").Ids, tiny.Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CompoundRangeSkipsTheKeysOfAnEmptyField1(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByLangTitle", Maps = { "from d in docs.Items select new { d.Lang, d.Title }" }, CompoundFields = [["Lang", "Title"]]
        }));
        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            for (int i = 0; i < 100; i++)
                commands.Put($"items/{i}", null, new Dictionary<string, object> { ["Lang"] = i % 2 == 1 ? "en" : "de", ["Title"] = $"w{i:D3}" }, meta);

            // a key with an empty field1 is field2 + 0, which starts with 'en' when field2 does
            commands.Put("items/z-null", null, new Dictionary<string, object> { ["Lang"] = null, ["Title"] = "energy" }, meta);
            commands.Put("items/z-missing", null, new Dictionary<string, object> { ["Title"] = "enzyme" }, meta);
            commands.Put("items/z-empty", null, new Dictionary<string, object> { ["Lang"] = "", ["Title"] = "enormous" }, meta);
        }
        Indexes.WaitForIndexing(store);

        const string query = "from index 'Items/ByLangTitle' where Lang = 'en' and Title >= 'a' order by Title";
        var bitmap = RunRql(store, query, "BitmapPipeline");
        Assert.Equal(50, bitmap.Ids.Length);
        Assert.Equal(bitmap.Ids, RunRql(store, query, "CompoundSortedScan").Ids);
        Assert.Equal(bitmap.Ids, RunRql(store, query).Ids);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CompoundRangeSeeksNoKeyThatEndsInAZeroByte(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(
            new IndexDefinition { Name = "Items/ByCatQty", Maps = { "from d in docs.Items select new { d.Cat, d.Qty }" }, CompoundFields = [["Cat", "Qty"]] },
            new IndexDefinition { Name = "Items/ByTagName", Maps = { "from d in docs.Items select new { d.Tag, d.Name }" }, CompoundFields = [["Tag", "Name"]] },
            new IndexDefinition { Name = "Items/ByCodeLabel", Maps = { "from d in docs.Items select new { d.Code, d.Label }" }, CompoundFields = [["Code", "Label"]] },
            new IndexDefinition { Name = "Items/ByNumText", Maps = { "from d in docs.Items select new { d.Num, d.Text }" }, CompoundFields = [["Num", "Text"]] }));
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            for (int i = 0; i <= 20; i++)
                commands.Put($"items/{i}", null, new Dictionary<string, object> { ["Cat"] = 5L, ["Qty"] = (long)i, ["Tag"] = "ab", ["Name"] = $"n{i:D3}" }, meta);
            for (int i = 0; i < 8; i++)
                commands.Put($"items/abc-{i}", null, new Dictionary<string, object> { ["Code"] = "abc", ["Label"] = $"x{i}" }, meta);
            // the open low of abc ends at the first byte 2 of U+0002, and the key of Code ab with Label c is abc + 2
            commands.Put("items/ab-c", null, new Dictionary<string, object> { ["Code"] = "ab", ["Label"] = "c" }, meta);
            commands.Put("items/q", null, new Dictionary<string, object> { ["Code"] = "q", ["Label"] = "\u0002foo" }, meta);
            // Num 0 is written ending in 0, so a low of Num 0 + 1 would read as 0 1 1, above the key of '\u0001\0x'
            foreach (var text in new[] { "\u0001\0x", "\u0001b", "m1", "m2" })
                commands.Put($"items/num-{text.Length}-{text[^1]}", null, new Dictionary<string, object> { ["Num"] = 0L, ["Text"] = text }, meta);
            // the key of a missing Cat is Qty + 0, here the bytes of Cat 5 followed by 0
            commands.Put("items/no-cat", null, new Dictionary<string, object> { ["Qty"] = 5L }, meta);
            // the open low of 'ab' ends at the first byte 'n', which this Name continues with zeros
            commands.Put("items/nul8", null, new Dictionary<string, object> { ["Tag"] = "ab", ["Name"] = "n" + new string('\0', 8) }, meta);
        }
        Indexes.WaitForIndexing(store);

        // The tree reads a key that ends in 0, or in 0 and then 1s, as if it went on with another 1, so the key of items/no-cat lies inside an
        // open low of Cat 5 that starts with zeros (a Qty of 0 also indexes the double 0.0, whose first byte is 0)
        AssertRows("from index 'Items/ByCatQty' where Cat = 5 and Qty < 100 order by Qty as long", "BitmapPipeline");
        AssertRows("from index 'Items/ByCatQty' where Cat = 5 and Qty < 100 order by Qty as long desc", "BitmapPipeline");
        AssertRows("from index 'Items/ByTagName' where Tag = 'ab' and Name < 'n050' order by Name", "CompoundSortedScan");
        AssertRows("from index 'Items/ByCodeLabel' where Code = 'abc' and Label < 'z' order by Label", "BitmapPipeline");
        AssertRows("from index 'Items/ByNumText' where Num = 0 and Text < 'z' order by Text", "CompoundSortedScan");

        // a boolean false is written as the byte 0, so the open low of 'ab' starts with zeros, and a Tag that continues 'ab' with zeros lies inside
        using (var commands = store.Commands())
        {
            commands.Put("items/flag", null, new Dictionary<string, object> { ["Tag"] = "zz", ["Name"] = false }, meta);
            commands.Put("items/ab00", null, new Dictionary<string, object> { ["Tag"] = "ab\0\0x", ["Name"] = "n005" }, meta);
        }
        Indexes.WaitForIndexing(store);

        AssertRows("from index 'Items/ByTagName' where Tag = 'ab' and Name < 'n050' order by Name", "BitmapPipeline");

        void AssertRows(string query, string hint)
        {
            var scan = RunRql(store, query, "CompoundSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal(hint, scan.Hint),
                () => Assert.Equal(hint, RunRql(store, query).Hint),
                () => Assert.Equal(RunRql(store, query, "BitmapPipeline").Ids, scan.Ids));
        }
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void TwoKeyWalkOrdersEqualKeysLikeTheBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        SeedTwoKeys(store);

        foreach (var order in new[] { "Cat, N as long", "Cat, N as long desc", "Cat, D as double desc", "Cat, S", "Cat, S desc", "Cat desc, N as long desc" })
            AssertTwoKeyWalkMatchesBitmap(store, "Items/ByCat", order);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void TwoKeyWalkPlacesNullStringSecondariesLikeTheBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        SeedTwoKeys(store);

        foreach (var order in new[] { "Cat, S nulls last", "Cat, S desc nulls first", "Cat desc, S nulls last" })
            AssertTwoKeyWalkMatchesBitmap(store, "Items/ByCat", order);
        foreach (var order in new[] { "Cat, S", "Cat, S desc" })
            AssertTwoKeyWalkMatchesBitmap(store, "Items/ByCatNullsLargest", order);
    }

    private static void AssertTwoKeyWalkMatchesBitmap(IDocumentStore store, string index, string order)
    {
        foreach (var filter in new[] { "", " and R != 3" })
        foreach (var page in new[] { "", " limit 7 offset 13" })
        {
            string query = $"from index '{index}' where Cat >= 'a'{filter} order by {order}{page}";
            var scan = RunRql(store, query, "FieldSortedScan");
            RavenTestHelper.AssertAll(() => query,
                () => Assert.Equal("FieldSortedScan", scan.Hint),
                () => Assert.Equal(RunRql(store, query, "BitmapPipeline").Ids, scan.Ids));
        }
    }

    private void SeedTwoKeys(IDocumentStore store)
    {
        const string map = "from d in docs.Items select new { d.Cat, d.N, d.D, d.S, d.R }";
        store.Maintenance.Send(new PutIndexesOperation(
            new IndexDefinition { Name = "Items/ByCat", Maps = { map } },
            new IndexDefinition { Name = "Items/ByCatNullsLargest", Maps = { map }, Configuration = new IndexConfiguration { ["Indexing.Querying.Corax.NullsSortMode"] = "NullsLargest" } }));

        using (var commands = store.Commands())
        {
            var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
            for (int i = 0; i < 600; i++)
            {
                var doc = new Dictionary<string, object>
                {
                    ["Cat"] = "abc"[i % 3].ToString(),
                    ["N"] = i % 29 == 0 ? null : (long)(i / 3 % 5),
                    ["D"] = i / 3 % 3 + 0.5,
                    ["S"] = i % 23 == 0 ? null : $"s{i / 3 % 4}",
                    ["R"] = (long)(i % 7)
                };
                if (i % 31 == 0)
                    doc.Remove("N");
                if (i % 19 == 0)
                    doc.Remove("S");
                commands.Put($"items/{i}", null, doc, meta);
            }
        }

        Indexes.WaitForIndexing(store);
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

    private static (string Ids, long Total) RunWithStatistics(IDocumentStore store, string rql, string strategy)
    {
        using var session = store.OpenSession();
        var ids = session.Advanced.RawQuery<Item>(rql)
            .NoCaching()
            .Statistics(out QueryStatistics statistics)
            .AddParameter("rvn_corax_strategy", strategy)
            .AddParameter("rvn_corax_entry_scan", -1L)
            .ToList()
            .Select(x => x.Id);
        return (string.Join(", ", ids), statistics.TotalResults);
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

    private class Product
    {
        public string Cat { get; set; }
        public long Price { get; set; }
        public string Grp { get; set; }
    }
}
