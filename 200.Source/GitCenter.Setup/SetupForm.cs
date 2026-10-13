using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Jhj.Core.Install;
using Jhj.Core.Setup;

namespace GitCenter.Setup
{
    /// <summary>
    /// 설치 화면 — 품은 꾸러미(없으면 최신 릴리스)를 설치·업데이트·다시 설치·되돌리기 하고 옵션 다섯 칸을 고르게 한다.
    /// 계약: 판정·설치·바로가기·「시작 앱」 등록은 Core 의 AppInstaller 가 하고 이 창은 묻고 보여 주기만 한다.
    /// 계약: 버전은 창 머리와 우측 하단에 싣는다 — JHJ_DEV/000.AGENTS_MD/080.CS앱-화면/080.CS앱-화면-공통.md §5
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private const int Pad = 24;
        private const int Wide = 472;

        private static readonly Color Muted = Color.FromArgb(110, 110, 118);

        private readonly Label _message = new Label { Location = new Point(Pad, 94), Size = new Size(Wide, 62) };
        private readonly Label _path = new Label { Location = new Point(Pad, 186), Size = new Size(Wide, 20), AutoEllipsis = true };
        private readonly CheckBox _startMenu = new CheckBox { Text = "시작 메뉴", Location = new Point(Pad, 236), AutoSize = true, Checked = true };
        private readonly CheckBox _desktop = new CheckBox { Text = "바탕 화면", Location = new Point(150, 236), AutoSize = true };
        private readonly CheckBox _taskbar = new CheckBox { Text = "작업 표시줄 고정", Location = new Point(276, 236), AutoSize = true };
        private readonly CheckBox _runAfter = new CheckBox { Text = "설치 뒤 바로 실행", Location = new Point(Pad, 266), AutoSize = true };
        // 계약: 켠 채로 연다 — Core InstallOptions.StartAtLogin 으로 넘어가 「시작 앱」의 켬/끔이 된다
        private readonly CheckBox _startAtLogin = new CheckBox { Text = "로그인할 때 자동 실행", Location = new Point(276, 266), AutoSize = true, Checked = true };
        private readonly Label _status = new Label { Location = new Point(Pad, 304), Size = new Size(Wide, 20), ForeColor = Muted, AutoEllipsis = true };
        private readonly Button _install = new Button { Location = new Point(320, 332), Size = new Size(104, 32) };
        private readonly Button _close = new Button { Text = "닫기", Location = new Point(432, 332), Size = new Size(64, 32) };
        private readonly AppInstaller _installer = Setup.Installer;
        private readonly string _package;

        public SetupForm(string packagePath, bool launch)
        {
            _package = packagePath;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Setup.App.DisplayName + " " + Program.PackageVersion + " 설치";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Malgun Gothic", 9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Pad + Wide + Pad, 396);
            AcceptButton = _install;
            CancelButton = _close;

            var icon = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(Pad, 22), Size = new Size(40, 40) };
            var title = new Label { Text = Setup.App.DisplayName, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Location = new Point(74, 18), AutoSize = true };
            var subtitle = new Label { Text = "JHJ Git Center", ForeColor = Muted, Location = new Point(76, 48), AutoSize = true };
            var pathCaption = new Label { Text = "설치 위치", ForeColor = Muted, Location = new Point(Pad, 164), AutoSize = true };
            var optionCaption = new Label { Text = "옵션", ForeColor = Muted, Location = new Point(Pad, 214), AutoSize = true };
            var version = new Label
            {
                Text = "v" + Program.PackageVersion,
                ForeColor = Muted,
                Font = new Font(Font.FontFamily, 8f),
                Location = new Point(Pad, 372),
                Size = new Size(Wide, 16),
                TextAlign = ContentAlignment.MiddleRight,
            };

            new ToolTip().SetToolTip(_taskbar, "Windows 11 은 프로그램이 작업 표시줄에 직접 고정할 수 없습니다.\n설치 뒤 바로가기 폴더를 열어 드리니\n오른쪽 클릭 → 「작업 표시줄에 고정」을 눌러 주십시오.");
            _runAfter.Checked = launch;

            Controls.AddRange(new Control[]
            {
                icon, title, subtitle, Line(80), _message, pathCaption, _path, optionCaption,
                _startMenu, _desktop, _taskbar, _runAfter, _startAtLogin, Line(296), _status, _install, _close, version,
            });

            _install.Click += async (s, e) => await InstallAsync();
            _close.Click += (s, e) => Close();

            RefreshState();
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>프로세스 종료 코드 — 설치에 실패했으면 1.</summary>
        public int ExitCode { get; private set; }

        private static Label Line(int top) =>
            new Label { Location = new Point(Pad, top), Size = new Size(Wide, 1), BackColor = Color.FromArgb(214, 214, 220) };

        private void RefreshState()
        {
            var installed = _installer.IsInstalled;
            // 왜: Describe 의 뒤 문단(설치 위치)은 바로 아래 칸이 따로 보인다 — 첫 문단만 쓴다
            _message.Text = Program.Describe(_installer).Split(new[] { "\n\n" }, StringSplitOptions.None)[0];
            _path.Text = _installer.InstallDir;
            _install.Text = ActionLabel(_installer.Classify(Program.PackageVersion));

            // 왜: 설치본이 있으면 바로가기 체크를 실제 상태로 맞춘다 — 업데이트 때 이미 있는 바로가기가 조용히 지워지지 않게
            if (installed)
            {
                _startMenu.Checked = _installer.HasStartMenuShortcut;
                _desktop.Checked = _installer.HasDesktopShortcut;
                _taskbar.Checked = _installer.IsPinnedToTaskbar;
            }
        }

        private static string ActionLabel(InstallAction action)
        {
            switch (action)
            {
                case InstallAction.Update: return "업데이트";
                case InstallAction.Reinstall: return "다시 설치";
                case InstallAction.Rollback: return "되돌리기";
                default: return "설치";
            }
        }

        private async Task InstallAsync()
        {
            var options = new InstallOptions { StartMenuShortcut = _startMenu.Checked, DesktopShortcut = _desktop.Checked, PinToTaskbar = _taskbar.Checked, StartAtLogin = _startAtLogin.Checked };
            SetBusy(true);
            _status.Text = "설치하는 중입니다...";
            try
            {
                await Task.Run(() => Program.InstallPackage(_package, options));
                ExitCode = 0;
                _status.Text = "설치했습니다.";

                if (options.PinToTaskbar && !_installer.IsPinnedToTaskbar)
                {
                    var shortcut = _installer.RevealShortcutForTaskbarPin();
                    MessageBox.Show(
                        this,
                        "작업 표시줄 고정은 Windows 가 프로그램에 허용하지 않습니다.\n열린 폴더에서 선택된 바로가기를 오른쪽 클릭한 뒤\n「작업 표시줄에 고정」을 눌러 주십시오.\n\n바로가기: " + shortcut,
                        Setup.App.DisplayName + " · 작업 표시줄 고정", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                if (_runAfter.Checked)
                {
                    _installer.Launch();
                    Close();
                    return;
                }

                RefreshState();
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                _status.Text = "설치하지 못했습니다.";
                MessageBox.Show(this, "설치하지 못했습니다.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _install.Enabled = !busy;
            _close.Enabled = !busy;
            UseWaitCursor = busy;
        }
    }
}
