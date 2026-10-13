using System;
using System.Threading;
using System.Threading.Tasks;
using Jhj.Core.Setup;
using Jhj.Core.Setup.Ui;

namespace GitCenter.Setup
{
    /// <summary>
    /// 설치 화면 — 품은 꾸러미(없으면 최신 릴리스)를 설치·업데이트·다시 설치·되돌리기 한다. 화면 틀은 Core 의 SetupWindow(040 §6)다.
    /// 계약: 판정·설치·바로가기·「시작 앱」 등록은 Core 의 AppInstaller 가 하고 이 창은 묻고 보여 주기만 한다.
    /// 계약: 「필수 도구」 Git · GitHub CLI(Prerequisites.All)는 없으면 켠 채로 보이고 앱 설치 뒤 winget 으로 깐다 — 첫 설치 PC 에서 git 없이 뜨지 않게.
    /// </summary>
    internal sealed class SetupForm : SetupWindow
    {
        private readonly string _package;

        public SetupForm(string packagePath, bool launch)
            : base(Setup.Installer, new SetupWindowOptions { RunAfter = launch, Prerequisites = Prerequisites.All })
        {
            _package = packagePath;
        }

        protected override string TargetVersion => Program.PackageVersion;

        protected override async Task<string> InstallCoreAsync(InstallOptions options, IProgress<int> progress, CancellationToken ct)
        {
            await Task.Run(() => Program.InstallPackage(_package, options));
            return Program.PackageVersion;
        }
    }
}
