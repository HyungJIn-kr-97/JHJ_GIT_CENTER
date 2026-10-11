using GitCenter.Core.Models;

namespace GitCenter.Application.Services;

/// <summary>충돌 구간 하나에 대한 판정 — 무엇을 고를지, 왜, 사람 확인 없이 믿어도 되는지.</summary>
public sealed record ConflictSuggestion(ConflictChoice Choice, string Reason, bool IsCertain);

/// <summary>
/// 충돌 구간을 「자동으로 풀 수 있는가」 판정하고, 고른 대로 파일 내용을 다시 짠다.
/// 계약: 파일도 git 도 건드리지 않는다 — 글자만 받아 글자만 돌려준다(쓰기·스테이지는 GitService.ResolveConflictAsync).
/// 계약: 자동(IsCertain)은 넷뿐이다 — 양쪽이 같음 · 공백만 다름 · 한쪽만 바꿈 · 서로 다른 줄을 고침. 나머지는 사람이 고른다.
/// 왜: git 은 맞붙은 줄을 따로 고쳐도 충돌로 본다 — 구간 안에서 줄 단위로 다시 견주면 그 대부분이 풀린다.
/// 함정: 표식 사이의 줄은 줄끝(\r)을 단 채로 다룬다 — 떼고 붙이면 CRLF 파일이 통째로 바뀐 것으로 잡힌다.
/// </summary>
public static class ConflictResolver
{
    /// 계약: 줄 수의 곱이 이보다 크면 줄 단위 견주기를 하지 않는다 — 표가 메모리를 먹는다
    private const long MaxCells = 4_000_000;

    public static ConflictSuggestion Suggest(ConflictHunk hunk, bool hasBase)
    {
        if (hunk.Ours.Count > ConflictParser.MaxLinesPerSide || hunk.Theirs.Count > ConflictParser.MaxLinesPerSide ||
            hunk.Base.Count > ConflictParser.MaxLinesPerSide)
        {
            return new(ConflictChoice.None, "구간이 길어 직접 골라야 합니다", false);
        }

        if (Same(hunk.Ours, hunk.Theirs, false))
        {
            return new(ConflictChoice.Ours, "양쪽이 같습니다", true);
        }

        if (Same(hunk.Ours, hunk.Theirs, true))
        {
            return new(ConflictChoice.Ours, "공백·줄끝만 다릅니다", true);
        }

        if (!hasBase)
        {
            return new(ConflictChoice.None, "양쪽이 새로 만든 파일입니다", false);
        }

        if (Same(hunk.Theirs, hunk.Base, false))
        {
            return new(ConflictChoice.Ours, "내 쪽만 바꿨습니다", true);
        }

        if (Same(hunk.Ours, hunk.Base, false))
        {
            return new(ConflictChoice.Theirs, "상대 쪽만 바꿨습니다", true);
        }

        if (TryMergeLines(hunk.Base, hunk.Ours, hunk.Theirs) is not null)
        {
            return new(ConflictChoice.Merged, "서로 다른 줄을 고쳤습니다", true);
        }

        // 왜: 같은 자리에 서로 다른 줄을 더한 것은 대개 둘 다 살린다 — 하지만 순서와 뜻은 사람이 봐야 한다
        return hunk.Base.Count == 0 && hunk.Ours.Count > 0 && hunk.Theirs.Count > 0
            ? new(ConflictChoice.Both, "양쪽이 같은 자리에 추가했습니다 — 확인해 주십시오", false)
            : new(ConflictChoice.None, "같은 줄을 서로 다르게 고쳤습니다", false);
    }

    /// <summary>고른 방식대로 그 구간에 남을 줄. 못 만들면 null.</summary>
    public static IReadOnlyList<string>? Lines(IReadOnlyList<string> baseLines, IReadOnlyList<string> ours, IReadOnlyList<string> theirs, ConflictChoice choice) =>
        choice switch
        {
            ConflictChoice.Ours => ours,
            ConflictChoice.Theirs => theirs,
            ConflictChoice.Both => [.. ours, .. theirs],
            ConflictChoice.Merged => TryMergeLines(baseLines, ours, theirs),
            _ => null
        };

    /// <summary>
    /// diff3 표식이 든 글에서 구간마다 고른 줄만 남긴다.
    /// 계약: 구간 수와 choices 수가 다르거나 하나라도 못 풀면 null 이다 — 반쯤 푼 파일을 내놓지 않는다.
    /// </summary>
    public static string? Apply(string merged, IReadOnlyList<ConflictChoice> choices, out string error)
    {
        var lines = merged.Split('\n');
        var output = new List<string>(lines.Length);
        var hunk = 0;
        var at = 0;

        while (at < lines.Length)
        {
            if (!lines[at].StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                output.Add(lines[at++]);
                continue;
            }

            var sides = new List<string>[] { [], [], [] };
            var section = 0;
            var closed = false;
            for (at++; at < lines.Length; at++)
            {
                var line = lines[at];
                if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                {
                    closed = true;
                    at++;
                    break;
                }

                if (line.StartsWith("|||||||", StringComparison.Ordinal))
                {
                    section = 1;
                }
                else if (line.StartsWith("=======", StringComparison.Ordinal))
                {
                    section = 2;
                }
                else
                {
                    sides[section].Add(line);
                }
            }

            if (!closed || hunk >= choices.Count)
            {
                error = closed ? "구간 수가 화면과 다릅니다 — 다시 읽어 주십시오." : "표식이 닫히지 않았습니다.";
                return null;
            }

            var kept = Lines(sides[1], sides[0], sides[2], choices[hunk]);
            if (kept is null)
            {
                error = $"{hunk + 1}번째 구간을 고르지 않았거나 자동으로 합칠 수 없습니다.";
                return null;
            }

            output.AddRange(kept);
            hunk++;
        }

        if (hunk != choices.Count)
        {
            error = "구간 수가 화면과 다릅니다 — 다시 읽어 주십시오.";
            return null;
        }

        error = string.Empty;
        return string.Join('\n', output);
    }

    /// <summary>
    /// 공통 조상에서 양쪽이 고친 줄 범위가 겹치지 않으면 둘을 합친다. 겹치면 null.
    /// 계약: 범위가 맞닿기만 한 것(윗줄·아랫줄)은 합친다 — 같은 자리에 끼워 넣은 것은 차례를 알 수 없어 합치지 않는다.
    /// </summary>
    public static IReadOnlyList<string>? TryMergeLines(IReadOnlyList<string> baseLines, IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
    {
        if (Diff(baseLines, ours) is not { } mine || Diff(baseLines, theirs) is not { } yours)
        {
            return null;
        }

        var edits = new List<Edit>(mine);
        foreach (var edit in yours)
        {
            // 계약: 양쪽이 똑같이 고친 것은 한 번만 넣는다
            if (mine.Any(m => m.Start == edit.Start && m.Length == edit.Length && Same(m.Lines, edit.Lines, false)))
            {
                continue;
            }

            if (mine.Any(m => Collides(m, edit)))
            {
                return null;
            }

            edits.Add(edit);
        }

        var result = new List<string>();
        var at = 0;
        foreach (var edit in edits.OrderBy(e => e.Start).ThenBy(e => e.Length))
        {
            for (; at < edit.Start; at++)
            {
                result.Add(baseLines[at]);
            }

            result.AddRange(edit.Lines);
            at = edit.Start + edit.Length;
        }

        for (; at < baseLines.Count; at++)
        {
            result.Add(baseLines[at]);
        }

        return result;
    }

    private sealed record Edit(int Start, int Length, IReadOnlyList<string> Lines);

    private static bool Collides(Edit a, Edit b)
    {
        // 함정: 끼워 넣기(Length 0)는 범위가 없어 겹침 검사를 빠져나간다 — 상대 범위의 안이나 끝에 닿으면 충돌로 본다
        if (a.Length == 0 || b.Length == 0)
        {
            var (point, other) = a.Length == 0 ? (a, b) : (b, a);
            return point.Start >= other.Start && point.Start <= other.Start + other.Length;
        }

        return a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;
    }

    /// 계약: from 을 to 로 바꾸는 고침 목록 — 가장 긴 공통 줄(LCS)을 뼈대로 그 사이 틈을 고침으로 본다
    private static List<Edit>? Diff(IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        var n = from.Count;
        var m = to.Count;
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            return null;
        }

        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = Equal(from[i], to[j], false) ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var edits = new List<Edit>();
        int a = 0, b = 0, startA = 0, startB = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && Equal(from[a], to[b], false))
            {
                Flush();
                a++;
                b++;
                startA = a;
                startB = b;
            }
            else if (b < m && (a == n || table[a, b + 1] >= table[a + 1, b]))
            {
                b++;
            }
            else
            {
                a++;
            }
        }

        Flush();
        return edits;

        void Flush()
        {
            if (a > startA || b > startB)
            {
                edits.Add(new Edit(startA, a - startA, [.. to.Skip(startB).Take(b - startB)]));
            }
        }
    }

    private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right, bool ignoreSpace) =>
        left.Count == right.Count && !left.Where((line, i) => !Equal(line, right[i], ignoreSpace)).Any();

    // 계약: 줄끝의 \r 은 늘 무시한다 — CRLF·LF 가 섞인 저장소에서 같은 줄을 다르다고 하지 않게
    private static bool Equal(string left, string right, bool ignoreSpace) => ignoreSpace
        ? left.AsSpan().TrimEnd().SequenceEqual(right.AsSpan().TrimEnd())
        : left.AsSpan().TrimEnd('\r').SequenceEqual(right.AsSpan().TrimEnd('\r'));
}
