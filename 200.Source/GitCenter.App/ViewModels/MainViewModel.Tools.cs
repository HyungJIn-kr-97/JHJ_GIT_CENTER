using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCenter.App.Services;
using GitCenter.Application.Services;
using GitCenter.Core.Models;
using Microsoft.Win32;

namespace GitCenter.App.ViewModels;

// 상세 「작업」 탭(Views/RepoToolsView) — GitHub Desktop 에는 있고 이 앱에는 없던 한 저장소짜리 일을 모았다.
//   저장소 더하기(Clone · 새로 만들기) · 브랜치(만들기 · 이름 바꾸기 · 지우기) · 커밋(되돌리기 · 고치기 · Revert · Cherry-pick)
//   · stash(넣기 · 꺼내기) · 태그 · 원격 주소 · Pull Request.
// 계약: 되돌릴 수 없는 명령은 여기에도 없다 — 지우기는 branch -d(병합된 것만), 되돌리기는 reset --soft(변경이 남는다)뿐이다.
// 계약: 올린 커밋은 고치지 않는다 — 「되돌리기」·「고치기」는 HEAD 가 어느 원격 추적 ref 에도 없을 때만 돈다(force push 가 필요해지므로).
// 계약: 원격에 닿는 것은 Clone · 태그 올리기 둘이고 둘 다 단추를 눌렀을 때만이다. Pull Request 는 브라우저만 연다.
// 함정: 일괄 작업과 겹치면 같은 저장소에 git 이 둘 돈다 — IsBusy 를 함께 쥔다.
public sealed partial class MainViewModel
{
    // 왜: Clone 은 큰 저장소에서 보통 명령의 제한 시간(120초)을 넘긴다
    private const int CloneTimeoutSeconds = 1800;

    [ObservableProperty]
    private string _toolsCaption = string.Empty;

    [ObservableProperty]
    private string _cloneUrl = string.Empty;

    [ObservableProperty]
    private string _cloneFolder = string.Empty;

    [ObservableProperty]
    private string _newRepoName = string.Empty;

    [ObservableProperty]
    private string _branchNameInput = string.Empty;

    [ObservableProperty]
    private string _amendMessage = string.Empty;

    [ObservableProperty]
    private string _commitShaInput = string.Empty;

    [ObservableProperty]
    private string _stashMessage = string.Empty;

    [ObservableProperty]
    private string _tagNameInput = string.Empty;

    [ObservableProperty]
    private string _remoteUrlInput = string.Empty;

    // ── 저장소 더하기 ────────────────────────────────────────

    [RelayCommand]
    private void PickCloneFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("저장소를 둘 폴더를 고르십시오"), Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            CloneFolder = dialog.FolderName;
        }
    }

    /// 계약: 받을 폴더를 비우면 첫 루트 폴더다 — 화면의 안내 글이 그렇게 말한다
    private string? TargetParent()
    {
        var folder = CloneFolder.Trim().Trim('"');
        if (folder.Length == 0)
        {
            folder = Settings.ScanRoots.FirstOrDefault(r => r.Enabled)?.Path ?? string.Empty;
        }

        if (Directory.Exists(folder))
        {
            return folder;
        }

        ToolsCaption = Loc.T("둘 폴더를 찾지 못했습니다 — 폴더를 골라 주십시오.");
        return null;
    }

    [RelayCommand]
    private async Task CloneRepoAsync()
    {
        // 왜: 「미 clone」 줄을 고른 채 누르면 주소를 다시 적지 않아도 된다
        var url = CloneUrl.Trim();
        if (url.Length == 0 && SelectedRow is { Entry.Health: RepoHealth.NotCloned, Entry.RemoteUrl: { Length: > 0 } known })
        {
            url = known;
        }

        if (url.Length == 0)
        {
            ToolsCaption = Loc.T("주소를 적거나 「미 clone」 저장소를 골라 주십시오.");
            return;
        }

        var name = Path.GetFileName(url.TrimEnd('/', '\\').Replace(':', '/'));
        name = name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (IsBusy || name.Length == 0 || TargetParent() is not { } parent)
        {
            return;
        }

        var target = Path.Combine(parent, name);
        if (Directory.Exists(target))
        {
            ToolsCaption = Loc.T("이미 있는 폴더입니다: {0}", target);
            return;
        }

        IsBusy = true;
        BusyText = Loc.T("Clone 하는 중… {0}", name);
        GitResult result;
        try
        {
            result = await _gitRunner.RunAsync(parent, ["clone", "--", url, target], CancellationToken.None, CloneTimeoutSeconds).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }

        if (!Report(name, "Clone", result))
        {
            return;
        }

        CloneUrl = string.Empty;
        await AdoptAsync(target).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateRepoAsync()
    {
        var name = NewRepoName.Trim();
        if (IsBusy || name.Length == 0 || TargetParent() is not { } parent)
        {
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ToolsCaption = Loc.T("폴더 이름으로 쓸 수 없는 글자가 있습니다.");
            return;
        }

        var target = Path.Combine(parent, name);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            ToolsCaption = Loc.T("이미 있는 폴더입니다: {0}", target);
            return;
        }

        Directory.CreateDirectory(target);
        var result = await _gitRunner.RunAsync(target, ["init", "-b", "main"], CancellationToken.None).ConfigureAwait(true);
        if (!Report(name, Loc.T("새 저장소"), result))
        {
            return;
        }

        NewRepoName = string.Empty;
        await AdoptAsync(target).ConfigureAwait(true);
    }

    /// 계약: 루트 밖에 만든 저장소는 수동 추가에 적는다 — 안 적으면 다음 훑기에 목록에서 사라진다
    private async Task AdoptAsync(string path)
    {
        var key = RepoKey.ForPath(path);
        if (!Settings.ScanRoots.Any(r => r.Enabled && RepoKey.IsUnder(RepoKey.ForPath(r.Path), key)))
        {
            Settings.ManualPaths.Add(path);
            await SaveSettingsAsync().ConfigureAwait(true);
        }

        Settings.LastSelectedRepoId = key;
        await RescanAsync().ConfigureAwait(true);
    }

    // ── 브랜치 ──────────────────────────────────────────────

    [RelayCommand]
    private Task CreateBranchAsync() => BranchNameInput.Trim() is { Length: > 0 } name
        ? RunToolAsync(Loc.T("새 브랜치"), ["switch", "-c", name], () => BranchNameInput = string.Empty)
        : NeedInput(Loc.T("브랜치 이름을 적어 주십시오."));

    [RelayCommand]
    private Task RenameBranchAsync() => BranchNameInput.Trim() is { Length: > 0 } name
        ? RunToolAsync(Loc.T("브랜치 이름 바꾸기"), ["branch", "-m", name], () => BranchNameInput = string.Empty)
        : NeedInput(Loc.T("브랜치 이름을 적어 주십시오."));

    // 계약: -d 다 — 병합되지 않은 브랜치는 git 이 거부하고, 이 앱은 -D 로 밀지 않는다
    [RelayCommand]
    private Task DeleteBranchAsync() => BranchNameInput.Trim() is { Length: > 0 } name
        ? RunToolAsync(Loc.T("브랜치 지우기"), ["branch", "-d", name], () => BranchNameInput = string.Empty,
            Loc.T("로컬 브랜치 「{0}」을(를) 지울까요?", name) + Environment.NewLine + Loc.T("병합되지 않은 브랜치는 지워지지 않습니다."))
        : NeedInput(Loc.T("브랜치 이름을 적어 주십시오."));

    // ── 커밋 ────────────────────────────────────────────────

    [RelayCommand]
    private async Task UndoCommitAsync()
    {
        if (!await HeadIsLocalOnlyAsync().ConfigureAwait(true))
        {
            return;
        }

        await RunToolAsync(Loc.T("마지막 커밋 되돌리기"), ["reset", "--soft", "HEAD~1"], null,
            Loc.T("마지막 커밋을 되돌릴까요?") + Environment.NewLine + Loc.T("바꾼 내용은 그대로 남습니다.")).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AmendCommitAsync()
    {
        var message = AmendMessage.Trim();
        if (message.Length == 0)
        {
            await NeedInput(Loc.T("새 커밋 메시지를 적어 주십시오.")).ConfigureAwait(true);
            return;
        }

        if (await HeadIsLocalOnlyAsync().ConfigureAwait(true))
        {
            await RunToolAsync(Loc.T("마지막 커밋 고치기"), ["commit", "--amend", "-m", message], () => AmendMessage = string.Empty).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task RevertCommitAsync() => CommitShaInput.Trim() is { Length: > 0 } sha
        ? RunToolAsync("Revert", ["revert", "--no-edit", sha], () => CommitShaInput = string.Empty)
        : NeedInput(Loc.T("커밋 해시를 적어 주십시오."));

    [RelayCommand]
    private Task CherryPickAsync() => CommitShaInput.Trim() is { Length: > 0 } sha
        ? RunToolAsync("Cherry-pick", ["cherry-pick", sha], () => CommitShaInput = string.Empty)
        : NeedInput(Loc.T("커밋 해시를 적어 주십시오."));

    /// 함정: 올린 커밋을 되돌리거나 고치면 다음 올리기가 거부되고 force push 밖에 길이 없다 — 그래서 미리 막는다
    private async Task<bool> HeadIsLocalOnlyAsync()
    {
        if (SelectedRow is not { Path.Length: > 0 } row)
        {
            return false;
        }

        var local = await _gitRunner.RunAsync(row.Path, ["rev-list", "-n", "1", "HEAD", "--not", "--remotes"], CancellationToken.None).ConfigureAwait(true);
        if (local.ExitCode == 0 && local.StdOut.Trim().Length > 0)
        {
            return true;
        }

        ToolsCaption = Loc.T("마지막 커밋이 이미 원격에 있습니다 — 올린 커밋은 고치지 않습니다. Revert 를 쓰십시오.");
        return false;
    }

    // ── stash · 태그 ─────────────────────────────────────────

    [RelayCommand]
    private Task StashPushAsync()
    {
        List<string> args = ["stash", "push", "--include-untracked"];
        if (StashMessage.Trim() is { Length: > 0 } message)
        {
            args.AddRange(["-m", message]);
        }

        return RunToolAsync(Loc.T("stash 넣기"), args, () => StashMessage = string.Empty);
    }

    // 계약: pop 이 충돌하면 git 이 stash 를 지우지 않고 남긴다 — 잃는 것이 없다
    [RelayCommand]
    private Task StashPopAsync() => RunToolAsync(Loc.T("stash 꺼내기"), ["stash", "pop"]);

    [RelayCommand]
    private Task CreateTagAsync() => TagNameInput.Trim() is { Length: > 0 } tag
        ? RunToolAsync(Loc.T("태그 만들기"), ["tag", tag])
        : NeedInput(Loc.T("태그 이름을 적어 주십시오."));

    [RelayCommand]
    private Task PushTagAsync() => TagNameInput.Trim() is { Length: > 0 } tag
        ? RunToolAsync(Loc.T("태그 올리기"), ["push", Settings.RemoteName, "refs/tags/" + tag])
        : NeedInput(Loc.T("태그 이름을 적어 주십시오."));

    // ── 원격 ────────────────────────────────────────────────

    private async Task<string?> GetRemoteUrlAsync(RepoRow row)
    {
        var result = await _gitRunner.RunAsync(row.Path, ["remote", "get-url", Settings.RemoteName], CancellationToken.None).ConfigureAwait(true);
        return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } url ? url : null;
    }

    [RelayCommand]
    private async Task ReadRemoteUrlAsync()
    {
        if (SelectedRow is { Path.Length: > 0 } row)
        {
            RemoteUrlInput = await GetRemoteUrlAsync(row).ConfigureAwait(true) ?? string.Empty;
            ToolsCaption = RemoteUrlInput.Length == 0 ? Loc.T("원격 「{0}」이(가) 없습니다.", Settings.RemoteName) : string.Empty;
        }
    }

    [RelayCommand]
    private async Task SetRemoteUrlAsync()
    {
        var url = RemoteUrlInput.Trim();
        if (url.Length == 0 || SelectedRow is not { Path.Length: > 0 } row)
        {
            await NeedInput(Loc.T("원격 주소를 적어 주십시오.")).ConfigureAwait(true);
            return;
        }

        var before = await GetRemoteUrlAsync(row).ConfigureAwait(true);
        var verb = before is null ? "add" : "set-url";
        await RunToolAsync(Loc.T("원격 주소"), ["remote", verb, Settings.RemoteName, url], null,
            Loc.T("원격 「{0}」의 주소를 바꿀까요?", Settings.RemoteName) + Environment.NewLine
            + Loc.T("지금: {0}", before ?? "—") + Environment.NewLine + Loc.T("새로: {0}", url)).ConfigureAwait(true);
    }

    /// 계약: 브라우저만 연다 — 원격에 쓰지 않는다. 지금 브랜치가 원격에 없으면 GitHub 이 그 화면에서 알린다
    [RelayCommand]
    private async Task OpenPullRequestAsync()
    {
        if (SelectedRow is not { Path.Length: > 0, Status.Branch.Length: > 0 } row)
        {
            return;
        }

        var web = ShellOpener.ToBrowserUrl(await GetRemoteUrlAsync(row).ConfigureAwait(true) ?? row.Entry.RemoteUrl);
        var branch = row.Status!.Branch;
        var into = Settings.RepoBranches.TryGetValue(row.Id, out var rule) && rule.Upload.Length > 0 ? rule.Upload : Settings.UploadBranch.Trim();
        if (web is null || string.Equals(into, branch, StringComparison.Ordinal))
        {
            ToolsCaption = web is null
                ? Loc.T("원격 주소를 열지 못했습니다.")
                : Loc.T("지금 브랜치가 업로드 브랜치와 같습니다 — Pull Request 를 낼 브랜치로 옮겨 주십시오.");
            return;
        }

        var root = web.TrimEnd('/');
        var url = into.Length == 0
            ? $"{root}/pull/new/{Uri.EscapeDataString(branch)}"
            : $"{root}/compare/{Uri.EscapeDataString(into)}...{Uri.EscapeDataString(branch)}?expand=1";
        ToolsCaption = ShellOpener.OpenUrl(url) ? string.Empty : Loc.T("원격 주소를 열지 못했습니다.");
    }

    // ── 거들개 ──────────────────────────────────────────────

    private Task NeedInput(string message)
    {
        ToolsCaption = message;
        return Task.CompletedTask;
    }

    /// <summary>고른 저장소에 git 한 번을 돌리고, 결과를 작업 로그에 남긴 뒤 상태·상세를 다시 읽는다.</summary>
    private async Task RunToolAsync(string title, IReadOnlyList<string> args, Action? onSuccess = null, string? confirm = null)
    {
        if (IsBusy || SelectedRow is not { Path.Length: > 0 } row)
        {
            return;
        }

        if (confirm is not null && MessageBox.Show(confirm, "JHJ Git Center", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        BusyText = title + "…";
        try
        {
            var result = await _gitRunner.RunAsync(row.Path, args, CancellationToken.None, Settings.GitTimeoutSeconds).ConfigureAwait(true);
            if (Report(row.Name, title, result))
            {
                onSuccess?.Invoke();
            }

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

    private bool Report(string scope, string title, GitResult result)
    {
        var ok = result.ExitCode == 0;
        var text = (ok ? result.StdOut : result.StdErr).Trim();
        if (text.Length == 0)
        {
            text = (ok ? result.StdErr : result.StdOut).Trim();
        }

        // 함정: git 은 실패 끝에 hint: 줄을 덧붙인다 — 마지막 줄만 보면 까닭 대신 조언이 나온다
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("hint:", StringComparison.Ordinal)).ToList();
        var line = lines.FirstOrDefault(l => l.StartsWith("error:", StringComparison.Ordinal) || l.StartsWith("fatal:", StringComparison.Ordinal))
                   ?? lines.LastOrDefault() ?? string.Empty;
        ToolsCaption = ok ? Loc.T("{0} — 됐습니다.", title) : Loc.T("{0} — 실패: {1}", title, line);
        AddActivity(scope, ok ? StepOutcome.Success : StepOutcome.Failed, $"{title} — {(line.Length == 0 ? "OK" : line)}");
        return ok;
    }
}
