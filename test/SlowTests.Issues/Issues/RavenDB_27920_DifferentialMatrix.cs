using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Json;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues;

public class RavenDB_27920_DifferentialMatrix : RavenTestBase
{
    public RavenDB_27920_DifferentialMatrix(ITestOutputHelper output) : base(output)
    {
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void CompoundQueriesMatchBitmapForEveryStoredValueKind(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/Matrix",
            Maps = { "from d in docs.Items select new { d.Tag, d.Num, d.Flag, d.Value, d.Other }" },
            CompoundFields = [["Tag", "Value"], ["Num", "Value"], ["Flag", "Value"]]
        }));

        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            for (int i = 0; i < 140; i++)
            {
                var doc = new Dictionary<string, object>
                {
                    ["Tag"] = i % 3 == 0 ? "ab" : i % 2 == 0 ? "a" : "b",
                    ["Num"] = i % 3 == 0 ? (long)(i % 5) : i % 3 == 1 ? i % 5 + 0.5 : 5.0,
                    ["Flag"] = i % 4 == 0 ? true : i % 4 == 1 ? "true" : "other",
                    ["Other"] = (long)(i % 7)
                };
                switch (i % 14)
                {
                    case 0: doc["Value"] = long.MinValue; break;
                    case 1: doc["Value"] = -2L; break;
                    case 2: doc["Value"] = 0L; break;
                    case 3: doc["Value"] = 7L; break;
                    case 4: doc["Value"] = -1.5; break;
                    case 5: doc["Value"] = -0.0; break;
                    case 6: doc["Value"] = 2.5; break;
                    case 7: doc["Value"] = double.MaxValue; break;
                    case 8: doc["Value"] = ""; break;
                    case 9: doc["Value"] = "\u0003"; break;
                    case 10: doc["Value"] = $"a{i:D3}"; break;
                    case 11: doc["Value"] = $"n{i:D3}"; break;
                    case 12: doc["Value"] = i % 2 == 0; break;
                    case 13: doc["Value"] = null; break;
                }
                if (i % 28 == 13)
                    doc.Remove("Value");
                if (i % 17 == 0)
                    doc.Remove("Other");
                commands.Put($"items/{i:D3}", null, doc, meta);
            }
        }
        Indexes.WaitForIndexing(store);

        var queries = new[]
        {
            "from index 'Items/Matrix' where Tag = 'a' order by Value",
            "from index 'Items/Matrix' where Tag = 'a' order by Value desc",
            "from index 'Items/Matrix' where Tag = 'a' order by Value nulls last",
            "from index 'Items/Matrix' where Tag = 'a' order by Value as long",
            "from index 'Items/Matrix' where Tag = 'a' order by Value as double desc",
            "from index 'Items/Matrix' where Tag = 'a' and Value >= 0 order by Value as long",
            "from index 'Items/Matrix' where Tag = 'a' and Value between -2.0 and 2.0 order by Value as double",
            "from index 'Items/Matrix' where Tag = 'a' and Value < 'n' order by Value",
            "from index 'Items/Matrix' where Tag = 'a' and Value between 'a' and 'z' order by Value desc",
            "from index 'Items/Matrix' where Tag = 'a' and Value between '*' and 'n' order by Value",
            "from index 'Items/Matrix' where Tag = 'a' and Value between 'n' and 'NULL' order by Value nulls last",
            "from index 'Items/Matrix' where Tag = 'a' and Value between '*' and 'NULL' order by Value desc",
            "from index 'Items/Matrix' where Tag = 'a' and Other != 3 order by Value",
            "from index 'Items/Matrix' where Tag = 'a' and not Other > 4 order by Value as long desc",
            "from index 'Items/Matrix' where Tag = 'a' and (Other = 1 or Other = 2) order by Value",
            "from index 'Items/Matrix' where Num = 1 order by Value",
            "from index 'Items/Matrix' where Flag = true order by Value desc"
        };
        var pages = new[] { "", " limit 7", " limit 7 offset 5", " limit 7 offset 40" };
        foreach (string query in queries)
        foreach (string page in pages)
        foreach (bool statistics in new[] { false, true })
            AssertMatchesBitmap(store, query + page, "CompoundSortedScan", statistics);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void NumericRangesMatchBitmapAtFiniteAndInfiniteBounds(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/Numbers",
            Maps = { "from d in docs.Items select new { Ratio = (double)d.Total / (double)d.Count }" }
        }));
        var values = new (string Id, long Total, long Count)[]
        {
            ("negative-infinity", -1, 0), ("minimum", long.MinValue, 1), ("negative", -2, 1),
            ("negative-zero", 0, -1), ("zero", 0, 1), ("positive", 2, 1),
            ("maximum", long.MaxValue, 1), ("positive-infinity", 1, 0), ("nan", 0, 0)
        };
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            foreach (var value in values)
                commands.Put($"items/{value.Id}", null, new Dictionary<string, object> { ["Total"] = value.Total, ["Count"] = value.Count }, meta);
            commands.Put("items/double-min", null, new Dictionary<string, object> { ["Total"] = -double.MaxValue, ["Count"] = 1L }, meta);
            commands.Put("items/double-max", null, new Dictionary<string, object> { ["Total"] = double.MaxValue, ["Count"] = 1L }, meta);
        }
        Indexes.WaitForIndexing(store);

        var ranges = new (string Where, (string Name, object Value)[] Parameters)[]
        {
            ("where Ratio > 0.5", []),
            ("where Ratio >= 0.0", []),
            ("where Ratio < 0.0", []),
            ("where Ratio <= 0.0", []),
            ("where Ratio between $lo and $hi", [("lo", -2.0), ("hi", 2.0)]),
            ("where Ratio between $lo and $hi", [("lo", double.MinValue), ("hi", 0.0)]),
            ("where Ratio between $lo and $hi", [("lo", 0.0), ("hi", double.MaxValue)]),
            ("where Ratio between $lo and $hi", [("lo", double.MaxValue), ("hi", double.MaxValue)]),
            ("where Ratio between $lo and $hi", [("lo", double.MinValue), ("hi", double.MinValue)]),
            ("", [])
        };
        foreach (var (where, parameters) in ranges)
        foreach (string order in new[] { "Ratio as double", "Ratio as double desc" })
        foreach (string page in new[] { "", " limit 3", " limit 3 offset 2" })
        foreach (bool statistics in new[] { false, true })
            AssertMatchesBitmap(store, $"from index 'Items/Numbers' {where} order by {order}{page}", "FieldSortedScan", statistics, parameters);
    }

    [RavenTheory(RavenTestCategory.Corax | RavenTestCategory.Querying)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax)]
    public void ForcedTwoKeyWalkWithMultiValuedPrimaryMatchesBitmap(Options options)
    {
        using var store = GetDocumentStore(options);
        store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Items/ByTags", Maps = { "from d in docs.Items select new { d.Tags, d.S }" }
        }));
        var meta = new Dictionary<string, object> { ["@collection"] = "Items" };
        using (var commands = store.Commands())
        {
            for (int i = 0; i < 128; i++)
                commands.Put($"items/{i:D3}", null, new Dictionary<string, object>
                {
                    ["Tags"] = new[] { $"t{i % 4}", $"t{i / 4 % 4}" }, ["S"] = $"s{i % 7}"
                }, meta);
        }
        Indexes.WaitForIndexing(store);

        foreach (string page in new[] { "", " limit 10", " limit 10 offset 17" })
            AssertMatchesBitmap(store, $"from index 'Items/ByTags' order by Tags desc, S{page}", "FieldSortedScan", statistics: false);
    }

    private static void AssertMatchesBitmap(IDocumentStore store, string rql, string strategy, bool statistics, params (string Name, object Value)[] parameters)
    {
        var bitmap = Run(store, rql, "BitmapPipeline", statistics, parameters);
        var forced = Run(store, rql, strategy, statistics, parameters);
        var automatic = Run(store, rql, strategy: null, statistics, parameters);
        RavenTestHelper.AssertAll(() => $"{rql}, statistics: {statistics}",
            () => Assert.Equal(bitmap.Ids, forced.Ids),
            () => Assert.Equal(bitmap.Ids, automatic.Ids),
            () => Assert.Equal(bitmap.Total, forced.Total),
            () => Assert.Equal(bitmap.Total, automatic.Total));
    }

    private static (string[] Ids, long Total) Run(IDocumentStore store, string rql, string strategy, bool statistics, params (string Name, object Value)[] parameters)
    {
        using var session = store.OpenSession();
        var query = session.Advanced.RawQuery<Item>(rql).NoCaching();
        if (strategy != null)
            query = query.AddParameter("rvn_corax_strategy", strategy);
        if (strategy == "BitmapPipeline")
            query = query.AddParameter("rvn_corax_entry_scan", -1L);
        foreach (var (name, value) in parameters)
            query = query.AddParameter(name, value);
        QueryStatistics stats = null;
        if (statistics)
            query = query.Statistics(out stats);
        var ids = query.ToList().Select(x => x.Id).ToArray();
        return (ids, stats?.TotalResults ?? -1);
    }

    private class Item
    {
        public string Id { get; set; }
    }
}
