using System;
using Corax.Mappings;
using Corax.Querying;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Querying.Planning;
using Corax.Querying.Primitives;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Queries;
using Voron;

namespace Raven.Server.Documents.Indexes.Persistence.Corax.QueryPlanBuilder;

internal static partial class QueryPlanBuilder
{
    private static bool CanElideCompoundSort(ref InstantiateContext ctx)
    {
        if (ctx.OrderByFields is not [var sortField] || ctx.Exec.CompoundFieldField2Range is not null ||
            sortField.FieldType != MatchCompareFieldType.Sequence || HasLegacyCharEncoding(ref ctx))
            return false;

        var indexSearcher = ctx.PlanParams.IndexSearcher;
        if (sortField.MayHaveMissingEntries || indexSearcher.TryGetPostingListForNull(in sortField.Field, out _))
            return false;

        var field2Meta = QueryBuilderHelper.GetFieldMetadata(in ctx.BuilderParams, ctx.Exec.Plan.Template.CompoundFieldSortName, hasBoost: false);
        return indexSearcher.NumberOfEntriesWithNumericValue(field2Meta) == 0 &&
               HasBooleanTerms(indexSearcher, field2Meta) == false &&
               indexSearcher.NumberOfDocumentsUnderSpecificTerm(field2Meta, string.Empty) == 0;
    }

    private static IQueryMatch ConstructCompoundField(ref InstantiateContext ctx, ResolutionContext walkerCtx, long entriesToScan, long bitmapCost, bool canElideCompoundSort)
    {
        var indexSearcher = ctx.PlanParams.IndexSearcher;
        var drivingClause = ctx.Exec.CompoundFieldDrivingClause;
        var packed = drivingClause.PackedParamValue;

        if (ctx.Exec.Plan.CompoundFieldResidualSet is null)
            return null;

        string field1Name = drivingClause.Clause.FieldName;
        string compoundFieldName = ctx.Exec.Plan.Template.CompoundFieldName;
        var compoundFieldMeta = indexSearcher.FieldMetadataBuilder(compoundFieldName, hasBoost: false);
        Slice analyzedPrefix = BuildField1Prefix(ref ctx, field1Name, packed, out string field1ValueStr);
        if (analyzedPrefix.HasValue == false || analyzedPrefix.Size > byte.MaxValue)
            return null;

        var field1Meta = QueryBuilderHelper.GetFieldMetadata(in ctx.BuilderParams, field1Name, hasBoost: false);
        if (HasLegacyCharEncoding(ref ctx) || indexSearcher.NumberOfEntriesWithNumericValue(field1Meta) > 0 || HasBooleanTerms(indexSearcher, field1Meta))
            return null;

        bool forward = ctx.OrderByFields[0].Ascending;
        IQueryMatch drivingMatch = indexSearcher.StartWithQuery(compoundFieldMeta, analyzedPrefix, isNegated: false, forward: forward, validatePostfixLen: true);
        if (drivingMatch is null)
            return null;

        if (canElideCompoundSort && drivingMatch is TermsProviderMatch tpm)
            drivingMatch = new SortedDrivingMatch(tpm.Provider, tpm.Llt, ctx.PlanParams.Allocator);

        bool hasResidual = ctx.Exec.Plan.CompoundFieldResidualSet is { HasPredicates: true };
        long knownTotal = -1;
        if (hasResidual == false && canElideCompoundSort && CanResolveKnownTotal(ctx.BuilderParams) &&
            drivingClause.Clause.ClauseType == ClauseType.Equals && drivingClause.Cardinality > 0)
            knownTotal = drivingClause.Cardinality;

        int take = canElideCompoundSort
            ? ResolveSortedScanTake(ctx.BuilderParams, knownTotal)
            : global::Corax.Constants.IndexSearcher.TakeAll;

        DirectScanMatchBase directScan;
        if (hasResidual)
        {
            ScanParamExtractor.Extract(ctx.Exec, indexSearcher, walkerCtx, ctx.Exec.Plan.CompoundFieldResidualSet);
            directScan = new DirectScanFilteredMatch(indexSearcher, drivingMatch, ctx.Exec, take, ctx.Exec.Plan.CompoundFieldResidualSet.Compiled, ctx.Token);
        }
        else
        {
            directScan = new DirectScanSimpleMatch(indexSearcher, drivingMatch, take, ctx.Token)
            {
                KnownExactTotal = knownTotal,
                KnownTotalProbeTicks = -1,
                KnownTotalProbeTerms = 0
            };
        }

        if (ctx.WantTimings)
        {
            directScan.DrivingTreeName = compoundFieldName;
            directScan.DrivingClause = $"{field1Name} = '{field1ValueStr}'";
            directScan.SeekBound = $"'{field1ValueStr}' (prefix, validatePostfixLen)";
            directScan.Direction = forward ? "Forward" : "Backward";
            directScan.ResidualDescription = ctx.Exec.Plan.CompoundFieldResidualSet?.Predicates is { } predicates
                ? string.Join(", ", Array.ConvertAll(predicates, p => $"{(p.Negated ? "not " : "")}{p.FieldName} {p.CompareOp.ToOperator()}"))
                : null;
            directScan.Reason = $"entries_to_scan({entriesToScan}) × {QueryPrimitives.EntryScanCostMultiplier} < bitmap_cost({bitmapCost})";
        }

        return directScan;
    }

    private static bool HasBooleanTerms(IndexSearcher indexSearcher, in FieldMetadata field)
    {
        var terms = indexSearcher.GetTermsFor(field.FieldName);
        return terms is not null && (terms.TryGetValue("false"u8, out _) || terms.TryGetValue("true"u8, out _));
    }

    private static bool HasLegacyCharEncoding(ref InstantiateContext ctx) =>
        ctx.BuilderParams.Index?.Definition.Version is not { } version ||
        IndexDefinitionBaseServerSide.IndexVersion.IsCoraxCharAsStringSupported(version) == false;
}
