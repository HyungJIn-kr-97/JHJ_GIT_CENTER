using System.Text;
using System.Text.RegularExpressions;

namespace GitCenter.Infrastructure.GitHubDesktop;

/// <summary>
/// GitHub Desktop 이 들고 있는 저장소 목록을 읽는다.
/// 왜: Desktop 은 목록을 IndexedDB(LevelDB·snappy)에 두고 내보내는 파일이 없다 — 그래서 블록을 풀어 경로 문자열을 줍고 .git 이 있는 것만 남긴다.
/// 계약: 추측한 경로는 실제로 저장소일 때만 돌려준다 — 레코드 형식을 풀지 않으므로 검증이 유일한 거름망이다.
/// </summary>
public static class GitHubDesktopReader
{
    private const char Sep = '\\';

    private static readonly Regex PathPattern = new(
        @"[A-Za-z]:[\\/](?:[^\x00-\x1f""<>|*?:\r\n]{1,80}[\\/]){0,12}[^\x00-\x1f""<>|*?:\r\n/\\]{1,80}",
        RegexOptions.Compiled);

    public static string? InstallPath
    {
        get
        {
            var exe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitHubDesktop", "GitHubDesktop.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    public static bool IsInstalled => InstallPath is not null;

    /// 계약: Desktop 의 「열기」에 쓰는 명령 — bin 아래 github 러처는 경로 하나를 받는다
    public static string? CommandLineTool
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitHubDesktop", "bin");
            foreach (var name in new[] { "github.bat", "github.exe", "github.cmd" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static string DefaultDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitHub Desktop");

    /// <summary>저장소 경로 목록. 못 읽으면 빈 목록과 note 를 돌려준다.</summary>
    public static (IReadOnlyList<string> Paths, string? Note) ReadRepositoryPaths(string? dataPathOverride = null)
    {
        var dataPath = string.IsNullOrWhiteSpace(dataPathOverride) ? DefaultDataPath : dataPathOverride!;
        if (!Directory.Exists(dataPath))
        {
            return ([], $"GitHub Desktop 자료 폴더를 찾지 못했습니다: {dataPath}");
        }

        var dbDir = Path.Combine(dataPath, "IndexedDB");
        if (!Directory.Exists(dbDir))
        {
            return ([], $"IndexedDB 폴더가 없습니다: {dbDir}");
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(dbDir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".sst", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            return ([], $"IndexedDB 를 읽지 못했습니다: {ex.Message}");
        }

        if (files.Count == 0)
        {
            return ([], "IndexedDB 안에 읽을 파일이 없습니다.");
        }

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            foreach (var block in LevelDbBlockScanner.ReadBlocks(file))
            {
                CollectPaths(block, found);
            }
        }

        var repos = found.Where(IsGitRepository).ToList();
        return repos.Count > 0
            ? (repos, null)
            : ([], "Desktop 자료는 읽었지만 저장소 경로를 찾지 못했습니다 — Desktop 에서 저장소를 한 번 연 뒤 다시 시도해 주십시오.");
    }

    /// 계약: 같은 블록을 두 가지로 읽는다 — IndexedDB 의 문자열은 1바이트(Latin-1)거나 2바이트(UTF-16LE)다
    private static void CollectPaths(byte[] block, SortedSet<string> found)
    {
        Collect(Latin1(block), found);
        Collect(Encoding.Unicode.GetString(block), found);

        // 왜: 2바이트 문자열이 홀수 위치에서 시작할 수 있다 — 한 칸 밀어 한 번 더 본다
        if (block.Length > 1)
        {
            Collect(Encoding.Unicode.GetString(block, 1, block.Length - 1), found);
        }
    }

    private static string Latin1(byte[] data)
    {
        var chars = new char[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            chars[i] = (char)data[i];
        }

        return new string(chars);
    }

    private static void Collect(string text, SortedSet<string> found)
    {
        foreach (Match match in PathPattern.Matches(text))
        {
            var value = match.Value.Replace('/', Sep).TrimEnd(Sep, ' ', '.');
            if (value.Length > 3)
            {
                found.Add(value);
            }
        }
    }

    /// 계약: 폴더형(.git 디렉터리)과 worktree 형(.git 파일) 둘 다 저장소로 본다
    public static bool IsGitRepository(string path)
    {
        try
        {
            var dot = Path.Combine(path, ".git");
            return Directory.Exists(dot) || File.Exists(dot);
        }
        catch
        {
            return false;
        }
    }
}
