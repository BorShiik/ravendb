using System;
using System.Linq;
using FastTests;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues;

public class RavenDB_26607 : RavenTestBase
{
    public RavenDB_26607(ITestOutputHelper output) : base(output)
    {
    }

    [RavenTheory(RavenTestCategory.Querying | RavenTestCategory.Corax)]
    [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
    public void MatchingOrOrderingOnANonIndexedFieldIsRejected(Options options)
    {
        using var store = GetDocumentStore(options);

        store.ExecuteIndex(new TestIndex());

        using (var session = store.OpenSession())
        {
            session.Store(new Mitarbeiter { Id = "1", Name = "MA 1", Prop = "alpha" });
            session.Store(new Mitarbeiter { Id = "2", Name = "MA 2", Prop = "beta" });
            session.SaveChanges();
        }

        Indexes.WaitForIndexing(store);

        using var s = store.OpenSession();

        // both engines used to answer these without ever looking at the field.
        // Corax reported every document for '!= null' and none for '== null'; Lucene reported the
        // opposite and also missed 'alpha', although the document really carries that value.
        var cases = new Func<System.Collections.Generic.IEnumerable<TestIndex.Result>>[]
        {
            () => s.Query<TestIndex.Result, TestIndex>().Where(r => r.Prop == "alpha").ToList(),
            () => s.Query<TestIndex.Result, TestIndex>().Where(r => r.Prop != null).ToList(),
            () => s.Query<TestIndex.Result, TestIndex>().Where(r => r.Prop == null).ToList(),
            () => s.Query<TestIndex.Result, TestIndex>().OrderBy(r => r.Prop).ToList()
        };

        foreach (var run in cases)
        {
            var e = Assert.Throws<InvalidQueryException>(() => run());
            Assert.Contains(nameof(TestIndex.Result.Prop), e.Message);
        }

        // the field is still stored, so a projection keeps reading it back
        var projected = s.Query<TestIndex.Result, TestIndex>()
            .Where(r => r.Name == "MA 1")
            .Select(r => new { r.Name, r.Prop })
            .ToList();

        Assert.Equal("alpha", Assert.Single(projected).Prop);
    }

    private class TestIndex : AbstractIndexCreationTask<Mitarbeiter, TestIndex.Result>
    {
        public class Result
        {
            public string Name { get; set; }

            public string Prop { get; set; }
        }

        public TestIndex()
        {
            Map = mitarbeitende => from mitarbeiter in mitarbeitende
                                   select new Result { Name = mitarbeiter.Name, Prop = mitarbeiter.Prop };

            StoreAllFields(FieldStorage.Yes);
            Index(r => r.Prop, FieldIndexing.No);
        }
    }

    private class Mitarbeiter
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Prop { get; set; }
    }
}
