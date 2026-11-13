using System;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.SortingMatches.Meta;
using Corax.Querying.Planning;
using Corax.Querying.Primitives;
using Raven.Server.Documents.Queries;
using Sparrow.Binary;
using Sparrow.Server;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Data.Lookups;
using Constants = Corax.Constants;
using Range = Corax.Querying.Matches.Meta.Range;

namespace Raven.Server.Documents.Indexes.Persistence.Corax.QueryPlanBuilder;

internal static partial class QueryPlanBuilder
{
    private static IQueryMatch ConstructCompoundField(ref InstantiateContext ctx, ResolutionContext walkerCtx, ClauseExecution field2Range, long entriesToScan, long bitmapCost, bool canElideCompoundSort)
    {
        var indexSearcher = ctx.PlanParams.IndexSearcher;
        var drivingClause = ctx.Exec.CompoundFieldDrivingClause;

        var packed = drivingClause.PackedParamValue;

        if (ctx.Exec.Plan.CompoundFieldResidualSet is null)
            return null;

        string field1Name = drivingClause.Clause.FieldName;
        string compoundFieldName = ctx.Exec.Plan.Template.CompoundFieldName;
        var compoundFieldMeta = indexSearcher.FieldMetadataBuilder(compoundFieldName, hasBoost: false);

        // Build the prefix bytes for field1's value.
        Slice analyzedPrefix = BuildField1Prefix(ref ctx, field1Name, packed, out string field1ValueStr);
        if (analyzedPrefix.HasValue == false || analyzedPrefix.Size > byte.MaxValue) // if too long, cannot be used for compound
            return null; // fall back to bitmap

        // An open BETWEEN ('*' or 'NULL') ranges from the bound it keeps; with neither bound it is exists(), which passes null and the empty
        // string but not missing, and all three share the driving value's key with an empty field2
        if (field2Range?.SentinelRewriteType == ClauseType.Exists)
        {
            if (FindKey([], analyzedPrefix.Size) != -1)
                return null;
            field2Range = null;
        }
        var field2RangeType = field2Range?.SentinelRewriteType ?? field2Range?.Clause.ClauseType;

        bool forward = ctx.OrderByFields[0].Ascending;

        bool usedCompositeRange = false;
        Slice compositeLow = default, compositeHigh = default;
        long emptyField2TermId = -1;
        if (CreateDrivingMatch(ref ctx) is not { } drivingMatch)
            return null; // unsupported shape (e.g. backward prefix scan) — fall back to the bitmap pipeline

        // When the sort wrapper is elided, the output IS the final order, but TermsProviderMatch returns entry-id order
        // so wrap it in a term-by-term walk that keeps field2 order (when not eliding, the outer SortingMatch re-sorts anyway).
        if (canElideCompoundSort && drivingMatch is TermsProviderMatch tpm)
            drivingMatch = new SortedDrivingMatch(tpm.Provider, tpm.Llt, ctx.PlanParams.Allocator);

        bool hasResidual = ctx.Exec.Plan.CompoundFieldResidualSet is { HasPredicates: true };

        long knownProbeTicks = -1; 
        int knownProbeTerms = 0;
        long knownTotal = hasResidual ? -1 : TryResolveCompoundKnownTotal(ref ctx);

        // When knownTotal resolves the scan is page-bounded even under statistics, compound sort still requires SortingMultiMatch (and thus full results)
        int take = canElideCompoundSort
            ? ResolveSortedScanTake(ctx.BuilderParams, knownTotal)
            : Constants.IndexSearcher.TakeAll;

        DirectScanMatchBase directScan;
        if (hasResidual)  // Filter every clause EXCEPT {driving, field2Range} (both enforced by the compound key).
        {
            ScanParamExtractor.Extract(ctx.Exec, indexSearcher, walkerCtx, ctx.Exec.Plan.CompoundFieldResidualSet);
            directScan = new DirectScanFilteredMatch(indexSearcher, drivingMatch, ctx.Exec, take: take, precompiledDelegate: ctx.Plan.CompoundFieldResidualSet.Compiled);
        }
        else // nothing to filter, just scan...
        {   
            directScan = new DirectScanSimpleMatch(indexSearcher, drivingMatch, take: take)
            {
                KnownExactTotal = knownTotal, KnownTotalProbeTicks = knownProbeTicks, KnownTotalProbeTerms = knownProbeTerms
            };
        }

        if (ctx.WantTimings) // only used when we use include timings()
            SetDirectScanPropertiesForIntrospection(ref ctx);

        return directScan;

        IQueryMatch CreateDrivingMatch(ref InstantiateContext context)
        {
            string fieldName = context.Exec.Plan.Template.CompoundFieldSortName;
            var field1Meta = QueryBuilderHelper.GetFieldMetadata(in context.BuilderParams, field1Name, hasBoost: false);
            var field2Meta = QueryBuilderHelper.GetFieldMetadata(in context.BuilderParams, fieldName, hasBoost: false);

            // A component is written by the type of the document's value, so a key built from a value of one type can neither place
            // nor tell apart the field's values of another; without a range, field2 is placed by the type the ORDER BY compares
            int? field2Type = field2Range?.PackedParamValue.ValueType ?? context.OrderByFields[0].FieldType switch
            {
                MatchCompareFieldType.Sequence => PackedParam.TypeString,
                MatchCompareFieldType.Integer or MatchCompareFieldType.Floating => PackedParam.TypeLong,
                _ => null
            };
            if (field2Type is not { } type || HoldsAnotherType(field1Meta, packed.ValueType) || HoldsAnotherType(field2Meta, type))
                return null;

            // No field2 narrowing: run a prefix scan on field1 and let entry-scan residuals filter the rest.
            if (field2Range is null)
            {
                return indexSearcher.StartWithQuery(compoundFieldMeta, analyzedPrefix,
                    isNegated: false, forward: forward,
                    validatePostfixLen: true);
            }

            // The field2 range is not part of the residual, so a range its keys cannot express is left to the bitmap
            if (TryBuildCompositeRangeKeys(ref context, analyzedPrefix, fieldName, field2Range, out var lowSlice, out var highSlice) == false)
                return null;

            // The driving value's key with an empty field2 (null, missing, empty string) may lie inside the range and is skipped by its id.
            // The empty string is a real value, so a string range that may hold it is left to the bitmap while the field has one, and so is
            // a range up to 'NULL', which holds null.
            emptyField2TermId = FindKey([], analyzedPrefix.Size);
            if (emptyField2TermId != -1 && (field2Range.SentinelRewriteType == ClauseType.GreaterThanOrEqual || RangeMayHoldEmptyString(ref context, field2Meta)))
                return null;

            // Every key inside the range starts with the driving value's bytes P: the value's own keys, and the keys of another value
            // that starts with P, or that P starts with when its field2 continues P. The walk takes the keys in bulk, so an open side ends at
            // the first bytes field2's values can start with, and the range is left to the bitmap while another value's keys may lie inside.
            Span<bool> firstBytes = stackalloc bool[byte.MaxValue + 1];
            Field2FirstBytes(ref context, field2Meta, firstBytes);
            int firstLow = firstBytes.IndexOf(true), firstHigh = firstBytes.LastIndexOf(true);
            // the tree reads a key that ends in 0x00, or in 0x00 and then 0x01 bytes, as if it went on with another 0x01, so an open low ends
            // at the first byte itself, and only at a byte of 2 or more
            if (field2RangeType is ClauseType.LessThan or ClauseType.LessThanOrEqual && firstLow > 1)
                lowSlice = PrefixWith(ref context, (byte)firstLow, 0);
            if (field2RangeType is ClauseType.GreaterThan or ClauseType.GreaterThanOrEqual && firstHigh < byte.MaxValue)
                highSlice = PrefixWith(ref context, (byte)firstHigh, sizeof(long));

            int p = analyzedPrefix.Size;
            if (OtherField1KeysMayLieInRange(field1Meta, field2Meta, lowSlice[p], highSlice[p], firstBytes))
                return null;

            usedCompositeRange = true;
            compositeLow = lowSlice;
            compositeHigh = highSlice;
            return BuildCompositeRangeMatch(lowSlice, highSlice);
        }

        IQueryMatch BuildCompositeRangeMatch(Slice low, Slice high)
        {
            return field2RangeType switch
            {
                ClauseType.GreaterThan => indexSearcher.RangeBuilder<Range.Exclusive, Range.Inclusive>(compoundFieldMeta, low, high, forward, emptyField2TermId),
                ClauseType.LessThan => indexSearcher.RangeBuilder<Range.Inclusive, Range.Exclusive>(compoundFieldMeta, low, high, forward, emptyField2TermId),
                _ => indexSearcher.RangeBuilder<Range.Inclusive, Range.Inclusive>(compoundFieldMeta, low, high, forward, emptyField2TermId)
            };
        }

        Slice PrefixWith(ref InstantiateContext context, byte first, int maxBytes)
        {
            context.PlanParams.Allocator.Allocate(analyzedPrefix.Size + 1 + maxBytes, out ByteString buf);
            Span<byte> key = buf.ToSpan();
            analyzedPrefix.CopyTo(key);
            key[analyzedPrefix.Size] = first;
            key[(analyzedPrefix.Size + 1)..].Fill(byte.MaxValue);
            return new Slice(buf);
        }

        // Marks the first bytes field2's non-empty components can start with. A number is written as a long or as a double, by the type
        // the document held, so both encodings count; each orders its values by their bytes unless they cross zero (doubles, legacy longs).
        void Field2FirstBytes(ref InstantiateContext context, in FieldMetadata field2Meta, scoped Span<bool> firstBytes)
        {
            if (field2Range.PackedParamValue.ValueType == PackedParam.TypeString)
            {
                if (indexSearcher.GetTermsFor(field2Meta.FieldName) is { } terms)
                {
                    var first = terms.Iterate<Lookup<CompactTree.CompactKeyLookup>.ForwardIterator>();
                    var last = terms.Iterate<Lookup<CompactTree.CompactKeyLookup>.BackwardIterator>();
                    first.Reset();
                    last.Reset();
                    // the empty string has a term but writes no bytes into the key
                    while (first.MoveNext(out CompactKey key, out _, out _))
                    {
                        if (key.Decoded().SequenceEqual(Constants.EmptyStringByteSpan))
                            continue;
                        int low = key.Decoded()[0];
                        if (last.MoveNext(out CompactKey lastKey, out _, out _))
                            firstBytes[low..(lastKey.Decoded()[0] + 1)].Fill(true);
                        break;
                    }

                    // a lone "\u0003" shares the term of the empty string but writes its byte into the key
                    firstBytes[Constants.EmptyStringByteSpan[0]] |= FindKey(Constants.EmptyStringByteSpan, analyzedPrefix.Size) != -1;
                    // a boolean is written as one byte, 0 or 1, while its term is the text
                    firstBytes[0] |= terms.TryGetValue("false"u8, out long _);
                    firstBytes[1] |= terms.TryGetValue("true"u8, out long _);
                }
            }
            else
            {
                if (indexSearcher.GetLongTermsFor(field2Meta.GetNumericFieldMetadata<long>(indexSearcher.Allocator).FieldName) is { } longs)
                {
                    var min = longs.Iterate<Lookup<Int64LookupKey>.ForwardIterator>();
                    var max = longs.Iterate<Lookup<Int64LookupKey>.BackwardIterator>();
                    min.Reset();
                    max.Reset();
                    long mask = context.BuilderParams.CompoundFieldNumericXorMask;
                    if (min.MoveNext(out Int64LookupKey minKey, out _, out _) && max.MoveNext(out Int64LookupKey maxKey, out _, out _))
                        Mark(firstBytes, (ulong)(minKey.Value ^ mask), (ulong)(maxKey.Value ^ mask));
                }

                if (indexSearcher.GetDoubleTermsFor(field2Meta.GetNumericFieldMetadata<double>(indexSearcher.Allocator).FieldName) is { } doubles)
                {
                    var min = doubles.Iterate<Lookup<DoubleLookupKey>.ForwardIterator>();
                    var max = doubles.Iterate<Lookup<DoubleLookupKey>.BackwardIterator>();
                    min.Reset();
                    max.Reset();
                    if (min.MoveNext(out DoubleLookupKey minKey, out _, out _) && max.MoveNext(out DoubleLookupKey maxKey, out _, out _))
                    {
                        // a NaN sorts first, below the smallest value
                        if (double.IsNaN(minKey.Value))
                        {
                            firstBytes.Fill(true);
                        }
                        else
                        {
                            // the bytes of every positive value sort below those of every negative one, so each sign is marked on its own,
                            // and a zero, one key for 0.0 and -0.0, is written as 0x00.. or 0xFF..
                            min.Seek(new DoubleLookupKey(0));
                            max.Seek(new DoubleLookupKey(0));
                            if (doubles.TryGetValue(new DoubleLookupKey(0), out _))
                            {
                                firstBytes[0] = firstBytes[byte.MaxValue] = true;
                                min.MoveNext(out DoubleLookupKey _, out _, out _);
                                max.MoveNext(out DoubleLookupKey _, out _, out _);
                            }

                            if (maxKey.Value > 0 && min.MoveNext(out DoubleLookupKey lowestPositive, out _, out _))
                                Mark(firstBytes, (ulong)Bits.DoubleToSortableLong(lowestPositive.Value), (ulong)Bits.DoubleToSortableLong(maxKey.Value));
                            if (minKey.Value < 0 && max.MoveNext(out DoubleLookupKey highestNegative, out _, out _))
                                Mark(firstBytes, (ulong)Bits.DoubleToSortableLong(minKey.Value), (ulong)Bits.DoubleToSortableLong(highestNegative.Value));
                        }
                    }
                }
            }

            if (firstBytes.Contains(true) == false)
                firstBytes.Fill(true);

            static void Mark(Span<bool> bytes, ulong min, ulong max)
            {
                if (min <= max)
                    bytes[(int)(min >> 56)..((int)(max >> 56) + 1)].Fill(true);
                else
                    bytes.Fill(true);
            }
        }

        // Another field1 value Q has keys inside the range only when they start with P. When Q starts with P, the byte after P is Q's own.
        // When P starts with Q, the key is Q + field2 + |Q| with field2 continuing P: if field2 is the rest of P the key is P + |Q|,
        // otherwise the byte after P is the byte of field2 that follows the rest of P.
        bool OtherField1KeysMayLieInRange(in FieldMetadata field1Meta, in FieldMetadata field2Meta, byte low, byte high, scoped ReadOnlySpan<bool> firstBytes)
        {
            var field1Terms = indexSearcher.GetTermsFor(field1Meta.FieldName);
            var value = analyzedPrefix.AsReadOnlySpan();
            if (HasTermContinuing(field1Terms, value, low, high))
                return true;

            bool field2IsString = field2Range.PackedParamValue.ValueType == PackedParam.TypeString;
            var field2Terms = field2IsString ? indexSearcher.GetTermsFor(field2Meta.FieldName) : null;
            for (int k = 0; k < value.Length; k++)
            {
                // an open low that ends at the byte k is the key P + k itself, and the tree reads the key P + 0 of an empty field1 as P 00 01
                if (low <= k && k <= high && FindKey([], k) != -1)
                    return true;

                // a number is written in eight bytes, so it continues the rest of P only when the rest is shorter
                bool keyContinues = firstBytes[value[k]] && (field2IsString || value.Length - k < sizeof(long));
                if (keyContinues == false)
                    continue;

                bool shorterValueExists = k == 0
                    ? indexSearcher.HasAnyNonExistingEntries(field1Meta) || indexSearcher.NumberOfDocumentsUnderSpecificTerm(field1Meta, (string)null) > 0 ||
                      indexSearcher.NumberOfDocumentsUnderSpecificTerm(field1Meta, string.Empty) > 0
                    : field1Terms?.TryGetValue(value[..k], out long _) == true;
                if (shorterValueExists && (field2IsString == false || HasTermContinuing(field2Terms, value[k..], low, high)))
                    return true;
            }

            return false;
        }

        // Whether a term starts with the prefix and continues with a byte in [low, high]
        static bool HasTermContinuing(CompactTree terms, ReadOnlySpan<byte> prefix, byte low, byte high)
        {
            if (terms is null)
                return false;

            Span<byte> seek = stackalloc byte[prefix.Length + 1];
            prefix.CopyTo(seek);
            seek[^1] = low;
            var iterator = terms.Iterate<Lookup<CompactTree.CompactKeyLookup>.ForwardIterator>();
            // a seek key that ends in 0x00 is read as if it went on with 0x01, so a low of 0x00 starts at the prefix and steps over it
            iterator.Seek(low == 0 ? prefix : seek);
            while (iterator.MoveNext(out CompactKey key, out _, out _))
            {
                var term = key.Decoded();
                if (low == 0 && term.SequenceEqual(prefix))
                    continue;
                return term.Length > prefix.Length && term.StartsWith(prefix) && term[prefix.Length] <= high;
            }

            return false;
        }

        long FindKey(ReadOnlySpan<byte> field2, int field1Length)
        {
            Span<byte> key = stackalloc byte[analyzedPrefix.Size + field2.Length + 1];
            analyzedPrefix.CopyTo(key);
            field2.CopyTo(key[analyzedPrefix.Size..]);
            key[^1] = (byte)field1Length;
            return indexSearcher.GetTermsFor(compoundFieldMeta.FieldName) is { } terms && terms.TryGetValue(key, out long termId) ? termId : -1;
        }

        bool HoldsAnotherType(in FieldMetadata fieldMeta, int valueType)
        {
            return valueType == PackedParam.TypeString
                ? indexSearcher.NumberOfEntriesWithNumericValue(fieldMeta) > 0
                : indexSearcher.HasNonNumericValues(fieldMeta);
        }

        bool RangeMayHoldEmptyString(ref InstantiateContext context, in FieldMetadata field2Meta)
        {
            if (field2Range.PackedParamValue.ValueType != PackedParam.TypeString)
                return false;

            if (field2RangeType is ClauseType.GreaterThan or ClauseType.GreaterThanOrEqual or ClauseType.Between &&
                context.Exec.StringValues[field2Range.PackedParamValue.Param1] is { Length: > 0 } low && low[0] > Constants.EmptyString[0])
                return false; // the empty string sorts below this low bound

            return indexSearcher.NumberOfDocumentsUnderSpecificTerm(field2Meta, string.Empty) > 0;
        }

        long TryResolveCompoundKnownTotal(ref InstantiateContext context)
        {
            if (canElideCompoundSort == false || CanResolveKnownTotal(context.BuilderParams) == false)
                return -1; // these require that we'll get the full result set anyway

            if (usedCompositeRange) // all results are in [low, high], we can just check the range
            {
                if (indexSearcher.HasMultipleTermsInField(compoundFieldMeta))
                    return -1; // but we cannot do that if we have multiple terms per document (need to de-dupe that)

                var countMatch = BuildCompositeRangeMatch(compositeLow, compositeHigh); // separate match, because count will consume it
                return TryCountPostingsInRange(countMatch, out knownProbeTicks, out knownProbeTerms);
            }

            // Bare field1-equality prefix (no field2 filter), we can just count that directly.
            // where Lang = 'en' order by Date desc - we get the already computed cardinality of Lang = 'en'
            if (field2Range is null
                && drivingClause.Clause.ClauseType == ClauseType.Equals
                && drivingClause.Cardinality > 0)
            {
                return drivingClause.Cardinality;
            }

            return -1;
        }

        void SetDirectScanPropertiesForIntrospection(ref InstantiateContext context)
        {
            directScan.DrivingTreeName = compoundFieldName;
            directScan.DrivingClause = $"{field1Name} = '{field1ValueStr}'";
            directScan.SeekBound = usedCompositeRange
                ? $"'{field1ValueStr}' + {field2Range.Clause.FieldName} {field2RangeType} (composite range)"
                : $"'{field1ValueStr}' (prefix, validatePostfixLen)";
            directScan.Direction = context.OrderByFields[0].Ascending ? "Forward" : "Backward";
            directScan.ResidualDescription = context.Exec.Plan.CompoundFieldResidualSet?.Predicates is { } cfr ? 
                FormatPredicates(cfr) 
                : null;
            directScan.Reason = $"entries_to_scan({entriesToScan}) × {QueryPrimitives.EntryScanCostMultiplier} < bitmap_cost({bitmapCost})";
        }

        string FormatPredicates(ScanPredicateInfo[] cfr) => string.Join(", ", Array.ConvertAll(cfr, p => $"{(p.Negated ? "not " : "")}{p.FieldName} {p.CompareOp.ToOperator()}"));
    }
}
