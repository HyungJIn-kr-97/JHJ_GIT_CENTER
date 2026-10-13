using Jhj.Core.Setup;

namespace GitCenter.Setup
{
    /// <summary>
    /// 이 앱이 돌려면 있어야 하는 외부 CLI 둘 — 설치 화면 「필수 도구」 칸이 되고, 없는 것은 앱 설치 뒤 winget 으로 깐다(Core SetupWindow).
    /// 계약: 패키지 ID·실행 파일·알려진 폴더는 앱의 GitCenter.Infrastructure/Tools/ToolLocator 와 같다 — 설치 화면이 「있다」고 본 것을 앱도 찾아야 한다.
    /// 계약: winget 이 이 앱을 조용히 깔 때는 이 코드가 돌지 않는다 — 그 길은 800.Deploy/winget-manifest.ps1 의 Dependencies 가 같은 두 패키지를 맡는다.
    /// </summary>
    internal static class Prerequisites
    {
        public static SetupPrerequisite[] All => new[]
        {
            new SetupPrerequisite
            {
                Label = "Git",
                WingetId = "Git.Git",
                ExecutableName = "git.exe",
                KnownDirs = new[] { @"%ProgramFiles%\Git\cmd", @"%ProgramFiles(x86)%\Git\cmd", @"%LOCALAPPDATA%\Programs\Git\cmd" },
                ToolTip = "git 명령줄(Git for Windows)을 winget 으로 설치합니다.\n저장소 상태를 읽고 받고 올리는 데 꼭 필요합니다.",
            },
            new SetupPrerequisite
            {
                Label = "GitHub CLI",
                WingetId = "GitHub.cli",
                ExecutableName = "gh.exe",
                KnownDirs = new[] { @"%ProgramFiles%\GitHub CLI", @"%LOCALAPPDATA%\Programs\GitHub CLI" },
                ToolTip = "GitHub CLI(gh)를 winget 으로 설치합니다.\nGitHub 계정 로그인 · 저장소 만들어 올리기에 씁니다.",
            },
        };
    }
}
