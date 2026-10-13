using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Services;
using GitCenter.Core.Models;
using Serilog;

namespace GitCenter.App.ViewModels;

// 상세의 「설정」 탭 — 고른 저장소의 GitHub 공유 사용자(Collaborators)를 보고, 내 저장소면 더하고 뺀다. 화면은 Views/RepoShareView.
// 왜: 브라우저의 저장소 설정 › Access 로 가야 하던 일을 이 장비 안에서 끝내려는 것이다(2026-10-14 사용자 지시 — 첫 판은 「사용자 추가」가 중심).
// 계약: 원격에 닿는 것은 탭을 누른 때(한 번)와 「새로 읽기」·「추가」·「빼기」를 누른 때뿐이다 — 저장소를 고르는 것만으로는 읽지 않는다.
// 계약: 「내 저장소」는 GitHubAccount.ReadRepoAccessAsync 의 IsAdmin 이다 — 아니면 목록만 보이고 더하기·빼기는 숨긴다.
// 함정: 더하기는 초대다 — 상대가 받아들이기 전까지 목록에 「초대 중」으로 남고, 빼기는 그 초대를 거둔다.
public sealed partial class MainViewModel
{
    /// 계약: 상세 탭 차례 — 0 변경 파일 · 1 브랜치 · 2 충돌 · 3 상태 · 4 작업 · 5 설정
    public const int ShareTabIndex = 5;

    public ObservableCollection<Collaborator> Collaborators { get; } = [];

    public IReadOnlyList<SharePermission> SharePermissions { get; } = GitHubAccount.Permissions;

    /// 계약: 머리 한 줄 — 「owner/name · 비공개 · 내 저장소」 꼴. 원격이 없거나 GitHub 가 아니면 그 사실을 적는다
    [ObservableProperty]
    private string _shareCaption = string.Empty;

    /// 계약: 마지막 실패·안내 한 줄 — 비면 할 말이 없다
    [ObservableProperty]
    private string _shareNote = string.Empty;

    [ObservableProperty]
    private bool _isShareBusy;

    /// 계약: 읽기가 끝나 목록이 믿을 만한 상태다 — 탭을 다시 눌러도 다시 읽지 않는다
    [ObservableProperty]
    private bool _isShareLoaded;

    /// 계약: GitHub 저장소라 읽을 수 있다 — 원격 없음·GitHub 아님이면 거짓이고 단추가 꺼진다
    [ObservableProperty]
    private bool _isShareAvailable;

    [ObservableProperty]
    private bool _isShareAdmin;

    /// 계약: 읽었는데 내 저장소가 아니다 — 「목록만 봅니다」 안내용
    [ObservableProperty]
    private bool _isShareReadOnly;

    [ObservableProperty]
    private string _shareUserInput = string.Empty;

    [ObservableProperty]
    private SharePermission _sharePermission = GitHubAccount.DefaultPermission;

    private RepoSlug? _shareSlug;
    private RepoRow? _shareRow;

    /// 계약: LoadDetailAsync 가 저장소마다 먼저 부른다 — 비우기만 하고, 좌표는 PrepareShareAsync 가 잡는다
    private void ResetShare(RepoRow row)
    {
        Collaborators.Clear();
        _shareRow = row;
        _shareSlug = null;
        IsShareLoaded = false;
        IsShareAdmin = false;
        IsShareReadOnly = false;
        IsShareAvailable = false;
        ShareNote = string.Empty;
        ShareUserInput = string.Empty;
        ShareCaption = string.Empty;
    }

    /// 계약: 원격 주소는 git(remote get-url · 로컬만)으로 읽는다 — Entry.RemoteUrl 은 gh·GitHub Desktop 출처만 채워 훑어 찾은 저장소에서는 비어 있다
    /// 함정: 읽는 동안 사용자가 「설정」 탭을 먼저 눌렀을 수 있다 — 좌표가 잡히면 그때 한 번 읽는다
    private async Task PrepareShareAsync(RepoRow row, int generation)
    {
        var url = await GetRemoteUrlAsync(row).ConfigureAwait(true) ?? row.Entry.RemoteUrl;
        if (generation != _detailGeneration || !ReferenceEquals(SelectedRow, row))
        {
            return;
        }

        _shareSlug = GitHubAccount.ParseSlug(url);
        IsShareAvailable = _shareSlug is not null;
        ShareCaption = _shareSlug is { } slug
            ? slug.ToString()
            : string.IsNullOrWhiteSpace(url)
                ? Loc.T("원격이 없습니다 — 「GitHub 에 올리기」로 먼저 올려 주십시오.")
                : Loc.T("GitHub 저장소가 아니라 공유 사용자를 읽을 수 없습니다.");

        if (DetailTabIndex == ShareTabIndex)
        {
            OnShareTabOpened();
        }
    }

    /// 왜: 탭을 누른 것이 곧 「읽어 달라」다 — 단추를 한 번 더 누르게 하지 않는다. 다시 읽기는 「새로 읽기」가 맡는다
    private void OnShareTabOpened()
    {
        Log.Debug("공유 탭 열림 available={Available} loaded={Loaded} busy={Busy}", IsShareAvailable, IsShareLoaded, IsShareBusy);
        if (IsShareAvailable && !IsShareLoaded && !IsShareBusy)
        {
            _ = LoadShareAsync();
        }
    }

    [RelayCommand]
    private async Task LoadShareAsync()
    {
        if (_shareRow is not { } row || _shareSlug is not { } slug || IsShareBusy)
        {
            return;
        }

        if (!GitHub.IsLoggedIn)
        {
            ShareNote = Loc.T("먼저 GitHub 에 로그인해 주십시오 — 아래 「로그인」 단추를 누르면 됩니다.");
            return;
        }

        IsShareBusy = true;
        ShareNote = string.Empty;
        try
        {
            Log.Information("공유 사용자 읽기 {Repo}", slug.ToString());
            var (access, error) = await GitHub.ReadRepoAccessAsync(slug, CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(_shareRow, row))
            {
                return;
            }

            if (access is null)
            {
                ShareNote = error;
                return;
            }

            IsShareAdmin = access.IsAdmin;
            IsShareReadOnly = !access.IsAdmin;
            ShareCaption = string.Join(" · ", slug.ToString(),
                access.IsPrivate ? Loc.T("비공개") : Loc.T("공개"),
                access.IsAdmin ? Loc.T("내 저장소") : Loc.T("소유자 {0}", access.OwnerLogin));

            var (list, listError) = await GitHub.ListCollaboratorsAsync(slug, access.OwnerLogin, CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(_shareRow, row))
            {
                return;
            }

            Collaborators.Clear();
            if (list is null)
            {
                ShareNote = listError;
                return;
            }

            foreach (var who in list.OrderBy(c => c.IsPending).ThenBy(c => c.Login, StringComparer.OrdinalIgnoreCase))
            {
                Collaborators.Add(who);
            }

            IsShareLoaded = true;
        }
        catch (Exception ex)
        {
            // 함정: 탭 열기는 기다리지 않는 호출(_ = LoadShareAsync)이라 예외가 조용히 사라진다 — 화면과 로그에 남긴다
            ShareNote = ex.Message;
            Log.Warning(ex, "공유 사용자 읽기 실패 {Repo}", slug.ToString());
        }
        finally
        {
            IsShareBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddCollaboratorAsync()
    {
        if (_shareRow is not { } row || _shareSlug is not { } slug || IsShareBusy || !IsShareAdmin)
        {
            return;
        }

        var login = ShareUserInput.Trim().TrimStart('@');
        if (!IsValidLogin(login))
        {
            ShareNote = Loc.T("GitHub 아이디는 영문·숫자·하이픈(-)만 쓸 수 있습니다.");
            return;
        }

        if (login.Equals(GitHub.Login, StringComparison.OrdinalIgnoreCase))
        {
            ShareNote = Loc.T("자기 자신은 더할 수 없습니다.");
            return;
        }

        if (Collaborators.Any(c => c.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
        {
            ShareNote = Loc.T("{0} 은(는) 이미 목록에 있습니다 — 권한을 바꾸려면 빼고 다시 더해 주십시오.", login);
            return;
        }

        IsShareBusy = true;
        ShareNote = string.Empty;
        try
        {
            var result = await GitHub.AddCollaboratorAsync(slug, login, SharePermission.Key, CancellationToken.None).ConfigureAwait(true);
            if (!result.Ok)
            {
                ShareNote = result.Message;
                AddActivity(row.Name, StepOutcome.Failed, Loc.T("공유 사용자 추가") + " — " + result.Message);
                return;
            }

            AddActivity(row.Name, StepOutcome.Success, result.Invited
                ? Loc.T("{0} 에게 {1} 권한으로 초대를 보냈습니다 — 받아들이면 공유됩니다.", login, SharePermission.Label)
                : Loc.T("{0} 에게 {1} 권한을 주었습니다.", login, SharePermission.Label));
            ShareUserInput = string.Empty;
        }
        finally
        {
            IsShareBusy = false;
        }

        IsShareLoaded = false;
        await LoadShareAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveCollaboratorAsync(Collaborator? who)
    {
        if (who is null || _shareRow is not { } row || _shareSlug is not { } slug || IsShareBusy || !IsShareAdmin)
        {
            return;
        }

        var question = who.IsPending
            ? Loc.T("{0} 에게 보낸 초대를 거둘까요?", who.Login)
            : Loc.T("{0} 의 접근 권한을 뺄까요?", who.Login);
        if (!JhjMessage.Confirm(question, Loc.T("저장소 {0} 에서 뺍니다. 다시 더하면 초대가 새로 갑니다.", slug)))
        {
            return;
        }

        IsShareBusy = true;
        ShareNote = string.Empty;
        try
        {
            var (ok, message) = await GitHub.RemoveCollaboratorAsync(slug, who, CancellationToken.None).ConfigureAwait(true);
            if (!ok)
            {
                ShareNote = message;
                AddActivity(row.Name, StepOutcome.Failed, Loc.T("공유 사용자 빼기") + " — " + message);
                return;
            }

            AddActivity(row.Name, StepOutcome.Success, who.IsPending
                ? Loc.T("{0} 에게 보낸 초대를 거두었습니다.", who.Login)
                : Loc.T("{0} 의 접근 권한을 뺐습니다.", who.Login));
        }
        finally
        {
            IsShareBusy = false;
        }

        IsShareLoaded = false;
        await LoadShareAsync().ConfigureAwait(true);
    }

    /// 계약: 브라우저의 같은 화면(설정 › Access)을 연다 — 앱에 없는 일(팀·외부 협력자·권한 바꾸기)은 거기서 한다
    [RelayCommand]
    private void OpenShareInBrowser()
    {
        if (_shareSlug is { } slug && !ShellOpener.OpenUrl(slug.AccessUrl))
        {
            ShareNote = Loc.T("브라우저를 열지 못했습니다.");
        }
    }

    [RelayCommand]
    private void OpenCollaborator(Collaborator? who)
    {
        if (who is { HtmlUrl.Length: > 0 })
        {
            ShellOpener.OpenUrl(who.HtmlUrl);
        }
    }

    /// 계약: GitHub 아이디는 영문·숫자·하이픈 39자 이내이고 하이픈으로 시작·끝나지 않는다
    private static bool IsValidLogin(string login) =>
        login.Length is > 0 and <= 39
        && login.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && login[0] != '-' && login[^1] != '-';
}
