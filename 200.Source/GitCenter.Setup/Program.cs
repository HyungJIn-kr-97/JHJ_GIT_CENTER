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
        internal static string PackageVersion
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

            // 계약: 묻는 화면은 SetupForm 하나다 — 옵션 다섯 칸을 고른 뒤 그 창이 InstallPackage 를 부른다
            if (!silent)
            {
                using (var form = new SetupForm(packagePath, launch))
                {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }

            try
            {
                // 계약: 조용한 설치(winget)는 화면이 없다 — 처음 까는 PC 에서만 「시작 앱」을 켜고, 업데이트는 사용자가 정해 둔 켬/끔을 건드리지 않는다
                var options = InstallOptions.Default;
                if (!installer.IsInstalled)
                {
                    options.StartAtLogin = true;
                }

                InstallPackage(packagePath, options);
                if (launch)
                {
                    installer.Launch();
                }

                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>꾸러미를 정해(--package → 품은 것 → 최신 릴리스) 고른 옵션으로 깐다 — 실패는 예외로 올린다.</summary>
        internal static void InstallPackage(string packagePath, InstallOptions options)
        {
            string extracted = null;
            try
            {
                var zip = packagePath;
                if (string.IsNullOrEmpty(zip))
                {
                    zip = extracted = ExtractEmbeddedPackage() ?? DownloadLatest();
                }

                Setup.Installer.Install(zip, options);
            }
            finally
            {
                if (extracted != null)
                {
                    try { File.Delete(extracted); } catch (IOException) { }
                }
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
            Setup.Installer.DetectInstallDir();
            var keepData = !purge;
            if (!silent)
            {
                var choice = SetupPrompts.ConfirmUninstall(null, Setup.Installer);
                if (choice == UninstallChoice.Cancel)
                {
                    return 0;
                }

                keepData = choice == UninstallChoice.KeepData;
            }

            try
            {
                Setup.Installer.Uninstall(keepData);
                if (!silent)
                {
                    SetupPrompts.UninstallDone(null, Setup.Installer, keepData);
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    SetupPrompts.UninstallFailed(null, Setup.Installer, ex);
                }

                return 1;
            }
        }
    }
}
