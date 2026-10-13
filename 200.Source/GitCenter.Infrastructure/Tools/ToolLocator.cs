using System.IO;
using Microsoft.Win32;

namespace GitCenter.Infrastructure.Tools;

/// <summary>외부 CLI(git · gh)의 실행 파일 자리 — PATH 에 없어도 알려진 설치 폴더에서 찾는다.</summary>
/// 왜: 설치 파일이 winget 으로 막 깐 도구는 그 세션의 PATH 에 아직 없다 — 이름만 넘기면 Win32Exception 으로 「없음」이 된다
/// 계약: 실행 파일 이름·알려진 폴더는 GitCenter.Setup/Prerequisites.cs 와 같다 — 설치 화면이 「있다」고 본 것을 앱도 찾아야 한다
/// 계약: 찾은 자리는 한 번만 재고(Lazy) 설치 뒤에는 Invalidate 로 다시 잰다
public static class ToolLocator
{
    private static readonly string[] GitDirs = [@"%ProgramFiles%\Git\cmd", @"%ProgramFiles(x86)%\Git\cmd", @"%LOCALAPPDATA%\Programs\Git\cmd"];
    private static readonly string[] GhDirs = [@"%ProgramFiles%\GitHub CLI", @"%LOCALAPPDATA%\Programs\GitHub CLI"];

    private static Lazy<string?> _git = new(() => Find("git.exe", GitDirs));
    private static Lazy<string?> _gh = new(() => Find("gh.exe", GhDirs));

    /// <summary>git 실행 파일 — 못 찾으면 "git"(PATH 에 맡긴다).</summary>
    public static string Git => _git.Value ?? "git";

    /// <summary>gh 실행 파일 — 못 찾으면 "gh".</summary>
    public static string Gh => _gh.Value ?? "gh";

    public static bool IsGitInstalled => _git.Value is not null;

    public static bool IsGhInstalled => _gh.Value is not null;

    /// <summary>도구를 새로 깐 뒤 — PATH 를 레지스트리에서 다시 읽고 자리를 다시 잰다.</summary>
    public static void Invalidate()
    {
        RefreshProcessPath();
        _git = new Lazy<string?>(() => Find("git.exe", GitDirs));
        _gh = new Lazy<string?>(() => Find("gh.exe", GhDirs));
    }

    private static string? Find(string exe, string[] knownDirs)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(knownDirs.Select(Environment.ExpandEnvironmentVariables));
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 에 섞인 잘못된 글자는 건너뛴다
            }
        }

        return null;
    }

    /// 계약: 장비 PATH + 사용자 PATH 를 합쳐 이 프로세스에 놓는다 — 설치 뒤 앱을 다시 띄우지 않고도 새 도구를 찾게
    private static void RefreshProcessPath()
    {
        try
        {
            var machine = ReadPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            var user = ReadPath(Registry.CurrentUser, "Environment");
            var merged = string.Join(Path.PathSeparator, new[] { machine, user }.Where(p => p.Length > 0));
            if (merged.Length > 0)
            {
                Environment.SetEnvironmentVariable("PATH", merged);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // 못 읽으면 지금 PATH 그대로 둔다
        }
    }

    private static string ReadPath(RegistryKey root, string subKey)
    {
        using var key = root.OpenSubKey(subKey);
        var raw = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return raw is null ? string.Empty : Environment.ExpandEnvironmentVariables(raw);
    }
}
