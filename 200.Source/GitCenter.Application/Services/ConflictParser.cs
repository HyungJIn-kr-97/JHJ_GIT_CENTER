using GitCenter.Core.Models;

namespace GitCenter.Application.Services;

/// <summary>
/// 충돌 표식이 박힌 파일에서 「어디가 충돌인지」를 뽑는다.
/// 계약: 파일을 고치지 않는다 — 읽어서 보여줄 뿐이고, 고르기·합치기는 사용자가 편집기에서 한다.
/// 함정: diff3 설정이면 ours 와 theirs 사이에 공통 조상(|||||||) 구역이 하나 더 있다.
/// </summary>
public static class ConflictParser
{
    private const string Start = "<<<<<<<";
    private const string BaseMark = "|||||||";
    private const string Middle = "=======";
    private const string End = ">>>>>>>";

    /// 계약: 한 덩어리가 이보다 길면 잘라서 보여준다 — 화면이 멈추지 않게
    private const int MaxLinesPerSide = 400;

    public static ConflictFile Parse(string path, string content)
    {
        var file = new ConflictFile { Path = path };

        if (content.Contains('\0'))
        {
            file.IsBinary = true;
            file.Note = "바이너리 파일입니다 — 양쪽 중 하나를 고르는 방식으로만 해결됩니다.";
            return file;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var at = 0;

        while (at < lines.Length)
        {
            if (!lines[at].StartsWith(Start, StringComparison.Ordinal))
            {
                at++;
                continue;
            }

            var hunk = new ConflictHunk
            {
                StartLine = at + 1,
                OursLabel = Label(lines[at], Start)
            };

            at++;
            var section = 0;
            var theirsLabel = string.Empty;

            while (at < lines.Length)
            {
                var line = lines[at];

                if (line.StartsWith(End, StringComparison.Ordinal))
                {
                    theirsLabel = Label(line, End);
                    at++;
                    break;
                }

                if (line.StartsWith(BaseMark, StringComparison.Ordinal))
                {
                    section = 1;
                    at++;
                    continue;
                }

                if (line.StartsWith(Middle, StringComparison.Ordinal))
                {
                    section = 2;
                    at++;
                    continue;
                }

                var target = section switch
                {
                    0 => hunk.Ours,
                    1 => hunk.Base,
                    _ => hunk.Theirs
                };

                if (target.Count < MaxLinesPerSide)
                {
                    target.Add(line);
                }
                else if (target.Count == MaxLinesPerSide)
                {
                    target.Add("… (이하 생략 — 파일을 열어 확인해 주십시오)");
                }

                at++;
            }

            file.Hunks.Add(new ConflictHunk
            {
                StartLine = hunk.StartLine,
                OursLabel = hunk.OursLabel,
                TheirsLabel = theirsLabel,
                Ours = hunk.Ours,
                Base = hunk.Base,
                Theirs = hunk.Theirs
            });
        }

        if (file.Hunks.Count == 0)
        {
            file.Note = "표식이 없습니다 — 한쪽이 지운 파일이거나 이미 해결된 파일입니다.";
        }

        return file;
    }

    private static string Label(string line, string marker)
    {
        var rest = line[marker.Length..].Trim();
        return rest.Length == 0 ? marker : rest;
    }
}
