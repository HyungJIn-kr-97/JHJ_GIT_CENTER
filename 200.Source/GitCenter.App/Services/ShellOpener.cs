using System.Diagnostics;
using System.IO;
using GitCenter.Infrastructure.GitHubDesktop;

namespace GitCenter.App.Services;

/// <summary>
/// 저장소를 바깥 도구로 연다 — 탐색기 · 터미널 · GitHub Desktop · VS Code · 원격 페이지.
/// 계약: 못 여는 것은 조용히 false 를 돌려준다 — 화면이 「열 수 없습니다」로 알린다.
/// </summary>
public static class ShellOpener
{
    public static bool OpenFolder(string path) => Start("explorer.exe", [path], null);

    public static bool OpenTerminal(string path)
    {
        // 왜: Windows Terminal 이 있으면 그것을, 없으면 PowerShell 을 연다
        if (Start("wt.exe", ["-d", path], null))
        {
            return true;
        }

        return Start("powershell.exe", ["-NoExit", "-Command", "Set-Location -LiteralPath '" + path + "'"], path);
    }

    public static bool OpenInGitHubDesktop(string path)
    {
        var tool = GitHubDesktopReader.CommandLineTool;
        if (tool is not null && Start(tool, [path], path))
        {
            return true;
        }

        var exe = GitHubDesktopReader.InstallPath;
        return exe is not null && Start(exe, [path], path);
    }

    public static bool OpenInVsCode(string path)
    {
        foreach (var candidate in CodeCandidates())
        {
            if (File.Exists(candidate) && Start(candidate, [path], path))
            {
                return true;
            }
        }

        return Start("code.cmd", [path], path) || Start("code", [path], path);
    }

    /// 계약: git 원격 주소(ssh·https)를 브라우저가 열 수 있는 https 주소로 바꾼다
    public static string? ToBrowserUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var url = remoteUrl.Trim();

        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var at = url.IndexOf('@');
            var colon = url.IndexOf(':');
            if (at >= 0 && colon > at)
            {
                var host = url[(at + 1)..colon];
                var rest = url[(colon + 1)..];
                url = "https://" + host + "/" + rest;
            }
        }

        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    public static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> CodeCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe");
    }

    private static bool Start(string fileName, IReadOnlyList<string> args, string? workingDirectory)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory ?? string.Empty
            };

            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
