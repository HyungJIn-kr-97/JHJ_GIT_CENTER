using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Jhj.Core.Install;
using Jhj.Core.Setup;

namespace GitCenter.Setup
{
    internal static class Program
    {
        // 계약: JHJ_Git-Center_Setup.exe [--silent] [--package <zip>] [--no-launch] | --uninstall [--silent] [--purge]
        //       --silent 는 winget 이 조용히 깔 때, --uninstall 은 「프로그램 추가/제거」가 부른다. --purge 가 있어야 설정까지 지운다
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var silent = SetupArgs.Has(args, "--silent");
            Environment.ExitCode = SetupArgs.Has(args, "--uninstall")
                ? Uninstall(silent, SetupArgs.Has(args, "--purge"))
                : Install(silent, SetupArgs.Find(args, "--package"), !SetupArgs.Has(args, "--no-launch"));
        }

        /// 계약: 이 설치 파일이 까는 판 — 「배포 버전.빌드 날짜」(1.0.2.20261012). 창 제목과 확인 문구에 그대로 보인다
        private static string PackageVersion
        {
            get
            {
                var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                var text = info == null ? string.Empty : info.InformationalVersion;
                var plus = text.IndexOf('+');
                return plus >= 0 ? text.Substring(0, plus) : text;
            }
        }

        private static int Install(bool silent, string packagePath, bool launch)
        {
            var title = Setup.App.DisplayName + " " + PackageVersion + " 설치";
            var installer = Setup.Installer;
            installer.DetectInstallDir();

            if (!silent && MessageBox.Show(Describe(installer), title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                return 0;
            }

            string extracted = null;
            try
            {
                var zip = packagePath;
                if (string.IsNullOrEmpty(zip))
                {
                    zip = extracted = ExtractEmbeddedPackage() ?? DownloadLatest();
                }

                installer.Install(zip);
                if (launch)
                {
                    installer.Launch();
                }

                if (!silent)
                {
                    MessageBox.Show("설치했습니다.\n" + installer.InstallDir, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("설치하지 못했습니다.\n" + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return 1;
            }
            finally
            {
                if (extracted != null)
                {
                    try { File.Delete(extracted); } catch (IOException) { }
                }
            }
        }

        // 계약: 문장이 끝나면 줄을 바꾼다 — 080.CS앱-화면-공통.md §4
        private static string Describe(AppInstaller installer)
        {
            var have = installer.IsInstalled ? installer.InstalledVersion() : null;
            var head = Setup.App.DisplayName + " " + PackageVersion;
            switch (installer.Classify(PackageVersion))
            {
                case InstallAction.Update:
                    return head + " 로 업데이트합니다.\n설치된 버전: " + have + "\n\n설치 위치: " + installer.InstallDir;
                case InstallAction.Reinstall:
                    return head + " 을(를) 다시 설치합니다.\n같은 버전이 이미 설치돼 있습니다.\n\n설치 위치: " + installer.InstallDir;
                case InstallAction.Rollback:
                    return head + " 로 되돌립니다.\n설치된 버전이 더 높습니다: " + have + "\n\n설치 위치: " + installer.InstallDir;
                default:
                    return head + " 을(를) 설치합니다.\n\n설치 위치: " + installer.InstallDir;
            }
        }

        // 계약: 꾸러미는 publish.ps1 이 「package.zip」이라는 논리 이름으로 넣는다 — 없으면 null 이고 최신 릴리스를 받는다
        private static string ExtractEmbeddedPackage()
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("package.zip"))
            {
                if (input == null)
                {
                    return null;
                }

                var path = Path.Combine(Path.GetTempPath(), Setup.App.Name + "-setup-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var output = File.Create(path))
                {
                    input.CopyTo(output);
                }

                return path;
            }
        }

        private static string DownloadLatest()
        {
            using (var client = new GithubReleaseClient(Setup.App))
            {
                var release = client.GetReleasesAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .FirstOrDefault(r => !r.Prerelease);
                if (release == null)
                {
                    throw new InvalidOperationException("받을 수 있는 릴리스가 없습니다.");
                }

                return client.DownloadAsync(release, null, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private static int Uninstall(bool silent, bool purge)
        {
            var title = Setup.App.DisplayName + " 제거";
            Setup.Installer.DetectInstallDir();
            var keepData = !purge;
            if (!silent)
            {
                var answer = MessageBox.Show(
                    "설치된 앱을 제거합니다.\n\n설정(" + Setup.Installer.DataDir + ")도 함께 지울까요?\n「예」= 모두 지움\n「아니요」= 앱만 지우고 설정은 남김",
                    title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Cancel)
                {
                    return 0;
                }

                keepData = answer == DialogResult.No;
            }

            try
            {
                Setup.Installer.Uninstall(keepData);
                if (!silent)
                {
                    MessageBox.Show(keepData ? "제거했습니다.\n설정은 남겨 두었습니다." : "제거했습니다.\n설정도 지웠습니다.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("제거하지 못했습니다.\n" + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return 1;
            }
        }
    }
}
