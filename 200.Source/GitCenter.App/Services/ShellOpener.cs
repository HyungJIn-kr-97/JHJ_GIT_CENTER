using GitCenter.Infrastructure.GitHubDesktop;
using CoreShell = Jhj.Core.Windows.Shell.ShellOpener;

namespace GitCenter.App.Services;

/// <summary>
/// 저장소를 바깥 도구로 연다 — GitHub Desktop 과 원격 주소만 이 앱의 몫이다.
/// 계약: 탐색기·터미널·VS Code·주소 열기는 JHJ_CS_CORE 가 갖는다 — 여기서는 그대로 넘긴다.
/// 계약: 못 여는 것은 조용히 false 를 돌려준다 — 화면이 「열 수 없습니다」로 알린다.
/// </summary>
public static class ShellOpener
{
    public static bool OpenFolder(string path) => CoreShell.OpenFolder(path);

    public static bool OpenTerminal(string path) => CoreShell.OpenTerminal(path);

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

    public static bool OpenInVsCode(string path) => CoreShell.OpenInVsCode(path);

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

    public static bool OpenUrl(string url) => CoreShell.OpenUrl(url);

    private static bool Start(string fileName, IReadOnlyList<string> args, string? workingDirectory) =>
        CoreShell.Start(fileName, args, workingDirectory);
}
