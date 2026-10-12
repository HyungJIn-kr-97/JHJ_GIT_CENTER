using Jhj.Core.Setup;

namespace GitCenter.Setup
{
    /// <summary>
    /// 이 설치 파일이 아는 앱 — 몸통(설치본 찾기 · 버전 판정 · 설치 · 제거)은 JHJ_CS_CORE 의 Jhj.Core.Setup 이고 여기는 값뿐이다.
    /// 계약: Name · DisplayName 은 GitCenter.App 의 JhjApp(App.xaml.cs)와 글자까지 같다 — 어느 쪽으로 깔아도 같은 폴더·바로가기·「프로그램 추가/제거」 키다.
    /// 계약: PackagePrefix · Repository 는 Services/AppUpdate.cs, SetupExecutableName 은 800.Deploy/publish.ps1 의 $SetupAsset 과 같다.
    /// </summary>
    internal static class Setup
    {
        public static readonly SetupApp App = new SetupApp
        {
            Name = "JHJ_Git-Center",
            DisplayName = "JHJ Git 통합 관리 센터",
            Repository = "HyungJIn-kr-97/JHJ_GIT_CENTER",
            PackagePrefix = "JHJ_Git-Center",
            Publisher = "HyungJin Ju",
            SetupExecutableName = "JHJ_Git-Center_Setup.exe",
        };

        public static readonly AppInstaller Installer = new AppInstaller(App);
    }
}
