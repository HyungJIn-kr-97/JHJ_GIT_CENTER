using GitCenter.Core.Models;

namespace GitCenter.Infrastructure.Git;

/// <summary>
/// git status --porcelain=v2 --branch --show-stash 출력을 읽는다.
/// 계약: v1 로 바꾸지 않는다 — v1 은 이름 바뀜과 충돌 종류를 구분하지 못한다.
/// </summary>
internal static class PorcelainV2
{
    public sealed class Parsed
    {
        public string Branch { get; set; } = string.Empty;

        public bool Detached { get; set; }

        public string? Upstream { get; set; }

        public int Ahead { get; set; }

        public int Behind { get; set; }

        public int Stash { get; set; }

        public List<FileChange> Changes { get; } = [];
    }

    public static Parsed Parse(string output)
    {
        var parsed = new Parsed();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                ParseHeader(line, parsed);
                continue;
            }

            switch (line[0])
            {
                case '1':
                    ParseOrdinary(line, parsed);
                    break;
                case '2':
                    ParseRenamed(line, parsed);
                    break;
                case 'u':
                    ParseUnmerged(line, parsed);
                    break;
                case '?':
                    parsed.Changes.Add(new FileChange { Path = line[2..], Index = '?', WorkTree = '?' });
                    break;
            }
        }

        return parsed;
    }

    private static void ParseHeader(string line, Parsed parsed)
    {
        if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
        {
            var name = line["# branch.head ".Length..];
            parsed.Detached = name == "(detached)";
            parsed.Branch = name;
            return;
        }

        if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            parsed.Upstream = line["# branch.upstream ".Length..];
            return;
        }

        if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            // 계약: 형식은 "+N -M" 이다
            var parts = line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Length < 2 || !int.TryParse(part[1..], out var value))
                {
                    continue;
                }

                if (part[0] == '+')
                {
                    parsed.Ahead = value;
                }
                else if (part[0] == '-')
                {
                    parsed.Behind = value;
                }
            }

            return;
        }

        if (line.StartsWith("# stash ", StringComparison.Ordinal) &&
            int.TryParse(line["# stash ".Length..].Trim(), out var stash))
        {
            parsed.Stash = stash;
        }
    }

    // 계약: 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
    private static void ParseOrdinary(string line, Parsed parsed)
    {
        var fields = line.Split(' ', 9);
        if (fields.Length < 9 || fields[1].Length < 2)
        {
            return;
        }

        parsed.Changes.Add(new FileChange
        {
            Path = fields[8],
            Index = fields[1][0],
            WorkTree = fields[1][1]
        });
    }

    // 계약: 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <score> <path>\t<origPath>
    private static void ParseRenamed(string line, Parsed parsed)
    {
        var fields = line.Split(' ', 10);
        if (fields.Length < 10 || fields[1].Length < 2)
        {
            return;
        }

        var paths = fields[9].Split('\t');
        parsed.Changes.Add(new FileChange
        {
            Path = paths[0],
            OriginalPath = paths.Length > 1 ? paths[1] : null,
            Index = fields[1][0],
            WorkTree = fields[1][1]
        });
    }

    // 계약: u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
    private static void ParseUnmerged(string line, Parsed parsed)
    {
        var fields = line.Split(' ', 11);
        if (fields.Length < 11)
        {
            return;
        }

        parsed.Changes.Add(new FileChange
        {
            Path = fields[10],
            Index = 'U',
            WorkTree = 'U'
        });
    }
}
