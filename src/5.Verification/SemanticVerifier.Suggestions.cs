using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// "Did you mean …?" suggestion support for name-resolution diagnostics
/// (unknown identifier / unknown type / member not found). Typos are the most common
/// cause of these errors; a close-match suggestion turns a search through scope into
/// a one-glance fix.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Returns <c> Did you mean 'best'?</c> (leading space included) when a close-enough
    /// candidate exists, or an empty string otherwise — append directly to a message.
    /// </summary>
    private static string DidYouMean(string target, IEnumerable<string> candidates)
    {
        string? best = SuggestSimilarName(target: target, candidates: candidates);
        return best == null
            ? string.Empty
            : $" Did you mean '{best}'?";
    }

    /// <summary>
    /// Picks the candidate with the smallest edit distance to <paramref name="target"/>,
    /// subject to a length-scaled threshold (1 edit for short names, up to 3 for long ones).
    /// Ties break alphabetically for deterministic output. Returns null when nothing is close.
    /// </summary>
    private static string? SuggestSimilarName(string target, IEnumerable<string> candidates)
    {
        if (target.Length == 0)
        {
            return null;
        }

        int maxDistance = target.Length switch
        {
            <= 4 => 1,
            <= 8 => 2,
            _ => 3
        };
        string? best = null;
        int bestDistance = maxDistance + 1;

        foreach (string candidate in candidates)
        {
            if (candidate.Length == 0 || candidate == target)
            {
                continue;
            }

            if (Math.Abs(value: candidate.Length - target.Length) > maxDistance)
            {
                continue;
            }

            int distance = BoundedEditDistance(a: target, b: candidate, cap: maxDistance);
            if (distance < bestDistance || distance == bestDistance && best != null &&
                string.CompareOrdinal(strA: candidate, strB: best) < 0)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= maxDistance
            ? best
            : null;
    }

    /// <summary>Holds the three rolling DP row arrays (previous-previous, previous, current) used by
    /// the bounded Damerau edit-distance algorithm so they can be passed as a single parameter.</summary>
    private record struct EditDpRows(int[] PrevPrev, int[] Prev, int[] Curr);

    /// <summary>
    /// Case-insensitive Damerau (optimal string alignment) edit distance with an early exit
    /// once every cell of a row exceeds <paramref name="cap"/> (returns cap+1 — "too far").
    /// Adjacent transpositions cost 1, so the most common typo class ("Tetx" → "Text",
    /// "add_lats" → "add_last") stays within the tight short-name threshold.
    /// </summary>
    private static int BoundedEditDistance(string a, string b, int cap)
    {
        int n = a.Length;
        int m = b.Length;
        var rows = new EditDpRows(PrevPrev: new int[m + 1],
            Prev: new int[m + 1],
            Curr: new int[m + 1]);
        for (int j = 0; j <= m; j++)
        {
            rows.Prev[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            rows.Curr[0] = i;
            char ca = char.ToLowerInvariant(c: a[index: i - 1]);
            int rowMin = FillEditRow(a: a,
                b: b,
                i: i,
                ca: ca,
                m: m,
                rows: rows);

            if (rowMin > cap)
            {
                return cap + 1;
            }

            (rows.PrevPrev, rows.Prev, rows.Curr) = (rows.Prev, rows.Curr, rows.PrevPrev);
        }

        return rows.Prev[m];
    }

    /// <summary>Fills one row of the edit-distance DP table and returns the row minimum.</summary>
    private static int FillEditRow(string a, string b, int i,
        char ca, int m, EditDpRows rows)
    {
        int rowMin = rows.Curr[0];
        for (int j = 1; j <= m; j++)
        {
            char cb = char.ToLowerInvariant(c: b[index: j - 1]);
            int cost = ca == cb
                ? 0
                : 1;
            rows.Curr[j] =
                Math.Min(val1: Math.Min(val1: rows.Curr[j - 1] + 1, val2: rows.Prev[j] + 1),
                    val2: rows.Prev[j - 1] + cost);
            if (i > 1 && j > 1 && ca == char.ToLowerInvariant(c: b[index: j - 2]) &&
                char.ToLowerInvariant(c: a[index: i - 2]) == cb)
            {
                rows.Curr[j] = Math.Min(val1: rows.Curr[j], val2: rows.PrevPrev[j - 2] + 1);
            }

            if (rows.Curr[j] < rowMin)
            {
                rowMin = rows.Curr[j];
            }
        }

        return rowMin;
    }

    /// <summary>
    /// Names a bare identifier could plausibly have meant: variables in scope,
    /// free routines (including generated try_/check_/lookup_ variants), and type names.
    /// </summary>
    private IEnumerable<string> IdentifierSuggestionCandidates()
    {
        foreach (string name in _registry.GetAllVariablesInScope()
                                         .Keys)
        {
            yield return name;
        }

        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            string name = routine.Name;
            if (routine.OwnerType == null && name.Length > 0 && !name.Contains(value: '.') &&
                !routine.IsWiredMemberRoutine)
            {
                yield return name;
            }
        }

        foreach (string typeName in TypeSuggestionCandidates())
        {
            yield return typeName;
        }
    }

    /// <summary>
    /// Plain (non-generic-resolution, unqualified) type names for unknown-type suggestions.
    /// </summary>
    private IEnumerable<string> TypeSuggestionCandidates()
    {
        foreach (TypeSymbol type in _registry.GetAllTypes())
        {
            string name = type.Name;
            if (string.IsNullOrEmpty(value: name) || type.IsGenericResolution ||
                name.Contains(value: '[') || name.Contains(value: '.') ||
                name.Contains(value: '('))
            {
                continue;
            }

            yield return name;
        }
    }

    /// <summary>Suggestion suffix for an unknown type name (also used by TypeResolver's S100 sites).</summary>
    internal string UnknownTypeSuggestion(string typeName)
    {
        return DidYouMean(target: typeName, candidates: TypeSuggestionCandidates());
    }

    /// <summary>
    /// Member names (non-wired memberRoutines + member variables) of the receiver type for
    /// member-not-found suggestions. Walks ALL registered routines matched by owner —
    /// GetMemberRoutinesForType only sees memberRoutines already materialized on a generic resolution,
    /// which is typically empty exactly when the user's first call on the type is a typo.
    /// </summary>
    private IEnumerable<string> MemberSuggestionCandidates(TypeSymbol type)
    {
        TypeSymbol? genericDef = type switch
        {
            RecordTypeSymbol record => record.GenericDefinition,
            EntityTypeSymbol entity => entity.GenericDefinition,
            ProtocolTypeSymbol protocol => protocol.GenericDefinition,
            _ => null
        };

        // "List[Core.S64]" must also match memberRoutines owned by the bare "List" definition.
        string baseName = type.BareName;

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        // Per-type memberRoutine tables hold the type's own declared memberRoutines (GetAllRoutines does
        // not include them all); query both the resolution and its generic definition.
        foreach (string name in YieldMemberRoutineNames(type: type, seen: seen))
        {
            yield return name;
        }

        if (genericDef != null)
        {
            foreach (string name in YieldMemberRoutineNames(type: genericDef, seen: seen))
            {
                yield return name;
            }
        }

        foreach (string name in YieldOwnerMatchedRoutineNames(type: type,
                     genericDef: genericDef,
                     baseName: baseName,
                     seen: seen))
        {
            yield return name;
        }

        List<MemberVariableInfo>? fields = type switch
        {
            RecordTypeSymbol record => record.MemberVariables,
            EntityTypeSymbol entity => entity.MemberVariables,
            _ => null
        };

        if (fields == null)
        {
            yield break;
        }

        foreach (MemberVariableInfo field in fields.Where(predicate: f => seen.Add(item: f.Name)))
        {
            yield return field.Name;
        }
    }

    /// <summary>
    /// Yields non-wired member-routine names for <paramref name="type"/> that have not yet been seen.
    /// </summary>
    private IEnumerable<string> YieldMemberRoutineNames(TypeSymbol type, HashSet<string> seen)
    {
        foreach (RoutineInfo memberRoutine in _registry.GetMemberRoutinesForType(type: type))
        {
            if (memberRoutine.IsWiredMemberRoutine)
            {
                continue;
            }

            string memberRoutineName = memberRoutine.Name;
            if (memberRoutineName.Length > 0 && seen.Add(item: memberRoutineName))
            {
                yield return memberRoutineName;
            }
        }
    }

    /// <summary>
    /// Yields routine names from all registered routines whose owner matches <paramref name="type"/>
    /// (by reference or base name), skipping already-seen names.
    /// </summary>
    private IEnumerable<string> YieldOwnerMatchedRoutineNames(TypeSymbol type,
        TypeSymbol? genericDef, string baseName, HashSet<string> seen)
    {
        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            TypeSymbol? owner = routine.OwnerType;
            if (owner == null)
            {
                continue;
            }

            // Owners are registered under bracketed generic-def names ("List[T]"),
            // receivers arrive as resolutions ("List[Core.S64]") — compare base names.
            bool ownerMatches = ReferenceEquals(objA: owner, objB: type) ||
                                genericDef != null &&
                                ReferenceEquals(objA: owner, objB: genericDef) ||
                                owner.BareName == baseName;
            if (!ownerMatches)
            {
                continue;
            }

            string name = routine.Name;
            if (name.Length > 0 && seen.Add(item: name))
            {
                yield return name;
            }
        }
    }
}
