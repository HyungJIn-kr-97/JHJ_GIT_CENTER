using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Services;
using GitCenter.Core.Models;

namespace GitCenter.App.ViewModels;

// GitHub 계정과 「GitHub 에 올리기」 층(Views/PublishLayer) — 원격이 없는 저장소를 GitHub 저장소로 만들어 연결하고 첫 Push 까지 한다.
// 계약: 계정은 Services/GitHubAccount(gh CLI)가 갖는다 — 제목 띠의 칩과 설정 「일반」 탭이 같은 객체를 본다.
// 계약: 올리기는 저장소 줄의 「GitHub 에 올리기」 · 상세 「작업」 탭 · 일괄 작업의 「원격 없음」 건너뜀(Nudge publish)이 연다.
// 함정: gh repo create --push 는 커밋이 없거나 원격이 이미 있으면 알아듣기 어려운 오류로 끝난다 — 여기서 먼저 가른다.
public sealed partial class MainViewModel
{
    public GitHubAccount GitHub { get; }

    [ObservableProperty]
    private bool _isPublishOpen;

    [ObservableProperty]
    private string _publishCaption = string.Empty;

    [ObservableProperty]
    private string _publishName = string.Empty;

    /// 계약: 기본은 비공개다 — 실수로 공개되는 쪽이 더 위험하다(2026-10-12 사용자 결정)
    [ObservableProperty]
    private bool _publishPrivate = true;

    [ObservableProperty]
    private string _publishDescription = string.Empty;

    [ObservableProperty]
    private string _publishNote = string.Empty;

    private RepoRow? _publishRow;

    [RelayCommand]
    private async Task ToggleGitHubLoginAsync()
    {
        if (GitHub.IsBusy)
        {
            return;
        }

        if (GitHub.IsLoggedIn)
        {
            if (!JhjMessage.Confirm(
                    Loc.T("GitHub 계정 {0} 에서 로그아웃할까요?", GitHub.Login),
                    Loc.T("이 PC 의 gh 로그인이 풀립니다 — 콘솔의 gh 명령도 함께 로그아웃됩니다.")))
            {
                return;
            }

            var done = await GitHub.LogoutAsync(CancellationToken.None).ConfigureAwait(true);
            AddActivity("GitHub", done ? StepOutcome.Success : StepOutcome.Failed,
                done ? Loc.T("GitHub 에서 로그아웃했습니다.") : GitHub.Note);
            return;
        }

        AddActivity("GitHub", StepOutcome.Success, Loc.T("gh 로그인 콘솔을 열었습니다 — 콘솔의 안내대로 브라우저에서 승인해 주십시오."));
        var ok = await GitHub.LoginAsync(CancellationToken.None).ConfigureAwait(true);
        AddActivity("GitHub", ok ? StepOutcome.Success : StepOutcome.Failed,
            ok ? Loc.T("GitHub 계정 {0} 으로 로그인했습니다.", GitHub.Login)
               : GitHub.Note.Length > 0 ? GitHub.Note : Loc.T("로그인이 끝나지 않았습니다 — 콘솔을 닫기 전에 브라우저 승인까지 마쳐 주십시오."));
    }

    [RelayCommand]
    private void OpenPublish()
    {
        if (SelectedRow is not { Path.Length: > 0 } row)
        {
            return;
        }

        _publishRow = row;
        PublishName = ToRepoName(row.Name);
        PublishPrivate = true;
        PublishDescription = string.Empty;
        PublishNote = string.Empty;
        PublishCaption = Loc.T("{0} 을(를) GitHub 에 올리기", row.Name);
        IsPublishOpen = true;
    }

    [RelayCommand]
    private void ClosePublish() => IsPublishOpen = false;

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (_publishRow is not { } row || IsBusy)
        {
            return;
        }

        var name = PublishName.Trim();
        if (!IsValidRepoName(name))
        {
            PublishNote = Loc.T("저장소 이름은 영문·숫자와 . _ - 만 쓸 수 있습니다.");
            return;
        }

        if (!GitHub.IsLoggedIn)
        {
            PublishNote = Loc.T("먼저 GitHub 에 로그인해 주십시오 — 아래 「로그인」 단추를 누르면 됩니다.");
            return;
        }

        IsBusy = true;
        BusyText = Loc.T("GitHub 에 올리는 중") + "…";
        try
        {
            var head = await _gitRunner.RunAsync(row.Path, ["rev-parse", "--verify", "--quiet", "HEAD"], CancellationToken.None, 20).ConfigureAwait(true);
            if (head.ExitCode != 0)
            {
                PublishNote = Loc.T("커밋이 하나도 없어 올릴 것이 없습니다 — 먼저 커밋해 주십시오.");
                return;
            }

            var remote = await _gitRunner.RunAsync(row.Path, ["remote", "get-url", Settings.RemoteName], CancellationToken.None, 20).ConfigureAwait(true);
            if (remote.ExitCode == 0)
            {
                PublishNote = Loc.T("이 저장소에는 이미 원격 {0} 이 있습니다 — 띠의 「Push」로 올려 주십시오.", Settings.RemoteName);
                return;
            }

            var result = await GitHub.CreateRepositoryAsync(row.Path, name, PublishPrivate, PublishDescription.Trim(), Settings.RemoteName, CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.Ok)
            {
                PublishNote = result.Message;
                AddActivity(row.Name, StepOutcome.Failed, Loc.T("GitHub 에 올리기") + " — " + result.Message);
                return;
            }

            AddActivity(row.Name, StepOutcome.Success,
                Loc.T("GitHub 에 {0} 저장소 {1} 을(를) 만들고 원격 {2} 으로 연결해 올렸습니다.",
                    PublishPrivate ? Loc.T("비공개") : Loc.T("공개"), result.Url ?? name, Settings.RemoteName));
            IsPublishOpen = false;
            if (NudgeAction == BulkNextAction.Publish)
            {
                NudgeAction = string.Empty;
            }

            row.Entry.RemoteUrl = result.Url;
            row.Status = await _git.ReadStatusAsync(row.Entry, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }

        ApplyFilter();
        UpdateStatusLine();
        await LoadDetailAsync(row).ConfigureAwait(true);
    }

    /// 계약: GitHub 저장소 이름의 글자는 영문·숫자·. _ - 뿐이다 — 폴더 이름의 나머지 글자는 - 로 바꾼다
    private static string ToRepoName(string folder)
    {
        var chars = folder.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray();
        var name = new string(chars).Trim('-', '.');
        return name.Length > 0 ? name : "repo";
    }

    private static bool IsValidRepoName(string name) =>
        name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && name.Trim('.') .Length > 0;
}
