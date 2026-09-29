using static Folio.Typography.BidiClass;

namespace Folio.Typography;

/// <summary>
/// The Unicode bidirectional algorithm (https://www.unicode.org/reports/tr9/) for one paragraph: embedding levels
/// (rules P2 to I2) and per-line reordering (L1, L2).
/// </summary>
internal static class Bidi
{
    /// <summary>Marks a character removed by rule X9 (embedding controls and boundary neutrals).</summary>
    public const byte Removed = 0xFF;

    private const int MaxDepth = 125;

    /// <summary>Resolves the embedding level of each character of a paragraph given by its code points.</summary>
    /// <param name="paragraphLevel">0 or 1, or null to take it from the first strong character (P2, P3).</param>
    public static (byte[] Levels, int ParagraphLevel) Resolve(ReadOnlySpan<int> codePoints, int? paragraphLevel)
    {
        var types = new BidiClass[codePoints.Length];
        for (var i = 0; i < types.Length; i++)
            types[i] = UnicodeData.Bidi(codePoints[i]);
        return Resolve(types, codePoints.ToArray(), paragraphLevel);
    }

    /// <summary>
    /// Resolves embedding levels from bidi classes; code points, when given, identify paired brackets (N0).
    /// Characters removed by X9 get <see cref="Removed"/>.
    /// </summary>
    public static (byte[] Levels, int ParagraphLevel) Resolve(BidiClass[] original, int[]? codePoints, int? paragraphLevel)
    {
        var n = original.Length;
        var types = (BidiClass[])original.Clone();
        var matchingPdi = MatchIsolates(original, out var matchingInitiator);
        var paraLevel = paragraphLevel ?? FirstStrongLevel(original, 0, n, matchingPdi) ?? 0;
        var levels = Explicit(original, types, paraLevel, matchingPdi);

        foreach (var sequence in IsolatingRunSequences(original, levels, matchingPdi, matchingInitiator, paraLevel))
            ResolveSequence(sequence, original, types, levels, codePoints);

        // L1 (without line breaks): separators, and whitespace and isolate controls before them or at the end.
        var trailing = true;
        for (var i = n - 1; i >= 0; i--)
        {
            var t = original[i];
            if (t is S or B)
            {
                levels[i] = (byte)paraLevel;
                trailing = true;
            }
            else if (t is WS or FSI or LRI or RLI or PDI && trailing)
            {
                levels[i] = (byte)paraLevel;
            }
            else if (levels[i] != Removed)
            {
                trailing = false;
            }
        }
        return (levels, paraLevel);
    }

    /// <summary>
    /// The visual order of the characters of one line (L2): indices of <paramref name="levels"/> from
    /// <paramref name="start"/> to <paramref name="end"/> (exclusive), removed characters left out.
    /// </summary>
    public static List<int> Reorder(byte[] levels, int start, int end)
    {
        var order = new List<int>();
        for (var i = start; i < end; i++)
        {
            if (levels[i] != Removed)
                order.Add(i);
        }
        if (order.Count == 0)
            return order;
        var highest = order.Max(i => levels[i]);
        var lowestOdd = order.Select(i => levels[i]).Where(l => l % 2 == 1).DefaultIfEmpty((byte)(highest + 1)).Min();
        for (var level = highest; level >= lowestOdd; level--)
        {
            for (var i = 0; i < order.Count;)
            {
                if (levels[order[i]] < level)
                {
                    i++;
                    continue;
                }
                var j = i;
                while (j < order.Count && levels[order[j]] >= level)
                    j++;
                order.Reverse(i, j - i);
                i = j;
            }
        }
        return order;
    }

    // BD9: each isolate initiator's matching PDI (or -1), and each PDI's matching initiator.
    private static int[] MatchIsolates(BidiClass[] types, out int[] matchingInitiator)
    {
        var matching = new int[types.Length];
        matchingInitiator = new int[types.Length];
        Array.Fill(matching, -1);
        Array.Fill(matchingInitiator, -1);
        var open = new Stack<int>();
        for (var i = 0; i < types.Length; i++)
        {
            switch (types[i])
            {
                case LRI or RLI or FSI:
                    open.Push(i);
                    break;
                case PDI when open.Count > 0:
                    var initiator = open.Pop();
                    matching[initiator] = i;
                    matchingInitiator[i] = initiator;
                    break;
                case B:
                    open.Clear();
                    break;
            }
        }
        return matching;
    }

    // P2, P3: the level of the first strong character, skipping isolates; null when there is none.
    private static int? FirstStrongLevel(BidiClass[] types, int start, int end, int[] matchingPdi)
    {
        for (var i = start; i < end; i++)
        {
            switch (types[i])
            {
                case L:
                    return 0;
                case R or AL:
                    return 1;
                case LRI or RLI or FSI:
                    if (matchingPdi[i] < 0)
                        return null;
                    i = matchingPdi[i];
                    break;
                case B:
                    return null;
            }
        }
        return null;
    }

    private readonly record struct Status(int Level, BidiClass? Override, bool Isolate);

    // X1 to X9: explicit levels and overrides; removed characters are marked.
    private static byte[] Explicit(BidiClass[] original, BidiClass[] types, int paraLevel, int[] matchingPdi)
    {
        var n = original.Length;
        var levels = new byte[n];
        var stack = new Stack<Status>();
        stack.Push(new Status(paraLevel, null, false));
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;

        for (var i = 0; i < n; i++)
        {
            var top = stack.Peek();
            var t = original[i];
            switch (t)
            {
                case RLE or LRE or RLO or LRO:
                {
                    var odd = t is RLE or RLO;
                    var level = odd ? (top.Level + 1) | 1 : (top.Level + 2) & ~1;
                    if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        stack.Push(new Status(level, t == RLO ? R : t == LRO ? L : null, false));
                    else if (overflowIsolates == 0)
                        overflowEmbeddings++;
                    levels[i] = Removed;
                    break;
                }
                case RLI or LRI or FSI:
                {
                    levels[i] = (byte)top.Level;
                    if (top.Override is { } o)
                        types[i] = o;
                    var rtl = t == RLI || t == FSI && FirstStrongLevel(original, i + 1, matchingPdi[i] < 0 ? n : matchingPdi[i], matchingPdi) == 1;
                    var level = rtl ? (top.Level + 1) | 1 : (top.Level + 2) & ~1;
                    if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stack.Push(new Status(level, null, true));
                    }
                    else
                    {
                        overflowIsolates++;
                    }
                    break;
                }
                case PDI:
                    if (overflowIsolates > 0)
                    {
                        overflowIsolates--;
                    }
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stack.Peek().Isolate)
                            stack.Pop();
                        stack.Pop();
                        validIsolates--;
                    }
                    top = stack.Peek();
                    levels[i] = (byte)top.Level;
                    if (top.Override is { } po)
                        types[i] = po;
                    break;
                case PDF:
                    if (overflowIsolates == 0)
                    {
                        if (overflowEmbeddings > 0)
                            overflowEmbeddings--;
                        else if (!top.Isolate && stack.Count >= 2)
                            stack.Pop();
                    }
                    levels[i] = Removed;
                    break;
                case B:
                    // X8: a paragraph separator ends all embeddings and isolates.
                    levels[i] = (byte)paraLevel;
                    stack.Clear();
                    stack.Push(new Status(paraLevel, null, false));
                    overflowIsolates = overflowEmbeddings = validIsolates = 0;
                    break;
                case BN:
                    levels[i] = Removed;
                    break;
                default:
                    levels[i] = (byte)top.Level;
                    if (top.Override is { } ov)
                        types[i] = ov;
                    break;
            }
        }
        return levels;
    }

    private sealed record Sequence(List<int> Indices, int Level, BidiClass Sos, BidiClass Eos);

    // BD13, X10: level runs joined across matching isolate initiators and PDIs, with their start and end types.
    private static List<Sequence> IsolatingRunSequences(BidiClass[] original, byte[] levels, int[] matchingPdi, int[] matchingInitiator, int paraLevel)
    {
        var runs = new List<List<int>>();
        List<int>? current = null;
        var lastLevel = -1;
        for (var i = 0; i < levels.Length; i++)
        {
            if (levels[i] == Removed)
                continue;
            if (current is null || levels[i] != lastLevel)
                runs.Add(current = []);
            current.Add(i);
            lastLevel = levels[i];
        }

        var runStartingAt = runs.ToDictionary(r => r[0]);
        var sequences = new List<Sequence>();
        foreach (var run in runs)
        {
            if (original[run[0]] == PDI && matchingInitiator[run[0]] >= 0)
                continue; // continues the sequence of its initiator
            var indices = new List<int>(run);
            while (original[indices[^1]] is LRI or RLI or FSI && matchingPdi[indices[^1]] is var pdi && pdi >= 0
                   && runStartingAt.TryGetValue(pdi, out var next))
                indices.AddRange(next);

            var level = levels[indices[0]];
            var before = indices[0] - 1;
            while (before >= 0 && levels[before] == Removed)
                before--;
            var after = indices[^1] + 1;
            while (after < levels.Length && levels[after] == Removed)
                after++;
            var beforeLevel = before >= 0 ? levels[before] : paraLevel;
            var afterLevel = after < levels.Length && original[indices[^1]] is not (LRI or RLI or FSI) ? levels[after] : paraLevel;
            sequences.Add(new Sequence(indices, level,
                Math.Max(level, beforeLevel) % 2 == 1 ? R : L,
                Math.Max(level, afterLevel) % 2 == 1 ? R : L));
        }
        return sequences;
    }

    private static void ResolveSequence(Sequence sequence, BidiClass[] original, BidiClass[] types, byte[] levels, int[]? codePoints)
    {
        var s = sequence.Indices;
        var count = s.Count;
        BidiClass T(int k) => types[s[k]];
        void Set(int k, BidiClass value) => types[s[k]] = value;

        // W1: non-spacing marks take the type of what precedes them (other neutral after isolate controls).
        for (var k = 0; k < count; k++)
        {
            if (T(k) == NSM)
                Set(k, k == 0 ? sequence.Sos : T(k - 1) is LRI or RLI or FSI or PDI ? ON : T(k - 1));
        }
        // W2, W3: European numbers after Arabic letters are Arabic numbers; Arabic letters become R.
        var lastStrong = sequence.Sos;
        for (var k = 0; k < count; k++)
        {
            var t = T(k);
            if (t is L or R or AL)
                lastStrong = t;
            else if (t == EN && lastStrong == AL)
                Set(k, AN);
        }
        for (var k = 0; k < count; k++)
        {
            if (T(k) == AL)
                Set(k, R);
        }
        // W4: a single separator between two numbers of the same kind joins them.
        for (var k = 1; k < count - 1; k++)
        {
            var (prev, t, next) = (T(k - 1), T(k), T(k + 1));
            if (t == ES && prev == EN && next == EN || t == CS && prev == next && prev is EN or AN)
                Set(k, prev);
        }
        // W5: terminators next to European numbers become European numbers.
        for (var k = 0; k < count; k++)
        {
            if (T(k) != ET)
                continue;
            var end = k;
            while (end < count && T(end) == ET)
                end++;
            if (k > 0 && T(k - 1) == EN || end < count && T(end) == EN)
            {
                for (var m = k; m < end; m++)
                    Set(m, EN);
            }
            k = end - 1;
        }
        // W6: remaining separators and terminators are other neutrals.
        for (var k = 0; k < count; k++)
        {
            if (T(k) is ES or ET or CS)
                Set(k, ON);
        }
        // W7: European numbers after L are L.
        lastStrong = sequence.Sos;
        for (var k = 0; k < count; k++)
        {
            var t = T(k);
            if (t is L or R)
                lastStrong = t;
            else if (t == EN && lastStrong == L)
                Set(k, L);
        }

        var embedding = sequence.Level % 2 == 1 ? R : L;
        if (codePoints is not null)
            ResolveBrackets(sequence, original, types, codePoints, embedding);

        // N1, N2: runs of neutrals take the direction around them if it agrees, else the embedding direction.
        static BidiClass? Strong(BidiClass t) => t switch
        {
            L => L,
            R or EN or AN => R,
            _ => null,
        };
        for (var k = 0; k < count; k++)
        {
            if (Strong(T(k)) is not null)
                continue;
            var end = k;
            while (end < count && Strong(T(end)) is null)
                end++;
            var before = k == 0 ? sequence.Sos : Strong(T(k - 1))!.Value;
            var after = end == count ? sequence.Eos : Strong(T(end))!.Value;
            var direction = before == after ? before : embedding;
            for (var m = k; m < end; m++)
                Set(m, direction);
            k = end - 1;
        }

        // I1, I2: implicit levels.
        foreach (var i in s)
        {
            var t = types[i];
            if (levels[i] % 2 == 0)
                levels[i] += (byte)(t == R ? 1 : t is AN or EN ? 2 : 0);
            else if (t is L or EN or AN)
                levels[i]++;
        }
    }

    // N0: paired brackets take the direction of the strong text inside them, or of the context before them.
    private static void ResolveBrackets(Sequence sequence, BidiClass[] original, BidiClass[] types, int[] codePoints, BidiClass embedding)
    {
        var s = sequence.Indices;
        // BD16: pair openers and closers (canonical equivalents of the angle brackets included), at most 63 open.
        static int Canonical(int cp) => cp switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => cp };
        var pairs = new List<(int Open, int Close)>();
        var open = new List<(int Position, int Closer)>();
        for (var k = 0; k < s.Count; k++)
        {
            if (types[s[k]] != ON || UnicodeData.Bracket(codePoints[s[k]]) is not { } bracket)
                continue;
            if (bracket.Opens)
            {
                if (open.Count == 63)
                    break;
                open.Add((k, Canonical(bracket.Pair)));
                continue;
            }
            var cp = Canonical(codePoints[s[k]]);
            for (var j = open.Count - 1; j >= 0; j--)
            {
                if (open[j].Closer == cp)
                {
                    pairs.Add((open[j].Position, k));
                    open.RemoveRange(j, open.Count - j);
                    break;
                }
            }
        }
        pairs.Sort();

        static BidiClass? Strong(BidiClass t) => t switch
        {
            L => L,
            R or EN or AN => R,
            _ => null,
        };
        foreach (var (o, c) in pairs)
        {
            BidiClass? found = null;
            for (var k = o + 1; k < c; k++)
            {
                if (Strong(types[s[k]]) is { } d)
                {
                    found = d;
                    if (d == embedding)
                        break;
                }
            }
            if (found is null)
                continue;
            var direction = embedding;
            if (found != embedding)
            {
                var context = sequence.Sos;
                for (var k = o - 1; k >= 0; k--)
                {
                    if (Strong(types[s[k]]) is { } d)
                    {
                        context = d;
                        break;
                    }
                }
                direction = context == found ? found.Value : embedding;
            }
            foreach (var k in new[] { o, c })
            {
                types[s[k]] = direction;
                // Marks that followed the bracket (W1 made them neutral) follow its new direction.
                for (var m = k + 1; m < s.Count && original[s[m]] == NSM; m++)
                    types[s[m]] = direction;
            }
        }
    }
}
