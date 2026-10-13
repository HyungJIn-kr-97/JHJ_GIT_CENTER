using System.Text.Json;

namespace GitCenter.App.Services;

// 저장소 공유 — GitHub 의 「Collaborators」(저장소 설정 › Access) 를 앱 안에서 보고 더하고 뺀다. 화면은 Views/RepoShareView · 로직은 MainViewModel.Share.cs.
// 계약: 전부 gh api 다(repos/{owner}/{repo} · /collaborators · /invitations) — 토큰은 여전히 gh 가 쥔다.
// 계약: 「내 저장소」판정은 repos/{owner}/{repo} 의 permissions.admin 이다 — 원격 주소의 소유자 이름과 로그인 이름을 견주지 않는다(조직 저장소·이름 바뀐 저장소가 틀린다).
// 함정: 사용자를 더하면(PUT) 바로 권한이 생기는 것이 아니라 **초대**가 간다(201) — 조직 구성원이거나 이미 공유된 사람만 204(본문 없음)로 끝난다.
// 함정: invitations 는 push 권한이 있어야 읽힌다 — 403 이면 초대 목록만 비우고 실패로 보지 않는다.

/// <summary>GitHub 저장소 좌표 — 원격 주소에서 뽑는다.</summary>
public sealed record RepoSlug(string Owner, string Name)
{
    public override string ToString() => Owner + "/" + Name;

    public string AccessUrl => "https://github.com/" + Owner + "/" + Name + "/settings/access";
}

/// 계약: IsAdmin 이 「내 저장소」다 — 더하기·빼기는 이 값이 참일 때만 보인다
public sealed record RepoAccess(bool IsAdmin, bool IsPrivate, string OwnerLogin, string HtmlUrl);

/// 계약: IsPending 이면 아직 받아들이지 않은 초대다 — 빼기는 InvitationId 로 초대를 거둔다
public sealed record Collaborator(string Login, string Permission, string HtmlUrl, bool IsPending, long InvitationId)
{
    public string RoleLabel => GitHubAccount.PermissionLabel(Permission);
}

/// 계약: Key 는 GitHub API 의 permission 값 그대로다(pull · triage · push · maintain · admin)
public sealed record SharePermission(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class GitHubAccount
{
    /// 계약: 차례가 곧 화면의 고르는 상자 차례다 — 기본은 「쓰기」(push)
    /// 왜: 부를 때마다 새로 만든다 — 정적 초기화 시점에는 언어가 아직 안 잡혀 있다
    public static IReadOnlyList<SharePermission> Permissions =>
    [
        new("pull", Loc.T("읽기")),
        new("triage", Loc.T("분류")),
        new("push", Loc.T("쓰기")),
        new("maintain", Loc.T("유지관리")),
        new("admin", Loc.T("관리자")),
    ];

    public static SharePermission DefaultPermission => Permissions[2];

    /// 계약: role_name(read·write·…)과 invitation.permissions(read·write·…)·permission 키(pull·push·…) 를 한 표로 받는다
    public static string PermissionLabel(string value) => value.ToLowerInvariant() switch
    {
        "pull" or "read" => Loc.T("읽기"),
        "triage" => Loc.T("분류"),
        "push" or "write" => Loc.T("쓰기"),
        "maintain" => Loc.T("유지관리"),
        "admin" => Loc.T("관리자"),
        _ => value,
    };

    /// <summary>원격 주소가 github.com 저장소면 소유자/이름을 돌려준다 — 아니면 null.</summary>
    public static RepoSlug? ParseSlug(string? remoteUrl)
    {
        var web = ShellOpener.ToBrowserUrl(remoteUrl);
        if (web is null || !Uri.TryCreate(web, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? new RepoSlug(parts[0], parts[1]) : null;
    }

    public async Task<(RepoAccess? Access, string Error)> ReadRepoAccessAsync(RepoSlug slug, CancellationToken cancellationToken)
    {
        var (output, error) = await RunGhAsync(["api", "repos/" + slug], null, 30, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            return (null, DescribeShareFailure(error, forRepo: true));
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var admin = root.TryGetProperty("permissions", out var perms)
                        && perms.TryGetProperty("admin", out var a) && a.ValueKind == JsonValueKind.True;
            var owner = root.TryGetProperty("owner", out var o) && o.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            var isPrivate = root.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True;
            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            return (new RepoAccess(admin, isPrivate, owner, url), string.Empty);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>직접 공유된 사용자와 아직 받아들이지 않은 초대를 한 목록으로 돌려준다 — 소유자는 뺀다.</summary>
    public async Task<(List<Collaborator>? List, string Error)> ListCollaboratorsAsync(
        RepoSlug slug, string ownerLogin, CancellationToken cancellationToken)
    {
        var (output, error) = await RunGhAsync(
            ["api", "--paginate", "repos/" + slug + "/collaborators?affiliation=direct&per_page=100"], null, 60, cancellationToken)
            .ConfigureAwait(false);
        if (output is null)
        {
            return (null, DescribeShareFailure(error, forRepo: true));
        }

        var list = new List<Collaborator>();
        try
        {
            foreach (var item in EnumeratePaginated(output))
            {
                var login = item.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty;
                if (login.Length == 0 || login.Equals(ownerLogin, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var role = item.TryGetProperty("role_name", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                var url = item.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : string.Empty;
                list.Add(new Collaborator(login, role, url, IsPending: false, InvitationId: 0));
            }
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }

        // 함정: 초대 목록은 push 권한이 없으면 403 이다 — 그때는 공유 목록만 보인다
        var (inv, _) = await RunGhAsync(
            ["api", "--paginate", "repos/" + slug + "/invitations?per_page=100"], null, 60, cancellationToken).ConfigureAwait(false);
        if (inv is not null)
        {
            try
            {
                foreach (var item in EnumeratePaginated(inv))
                {
                    var id = item.TryGetProperty("id", out var i) && i.TryGetInt64(out var v) ? v : 0;
                    var login = item.TryGetProperty("invitee", out var invitee) && invitee.TryGetProperty("login", out var l)
                        ? l.GetString() ?? string.Empty : string.Empty;
                    var url = item.TryGetProperty("invitee", out invitee) && invitee.TryGetProperty("html_url", out var h)
                        ? h.GetString() ?? string.Empty : string.Empty;
                    var perm = item.TryGetProperty("permissions", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                    if (login.Length > 0)
                    {
                        list.Add(new Collaborator(login, perm, url, IsPending: true, InvitationId: id));
                    }
                }
            }
            catch (JsonException)
            {
                // 왜: 초대 목록이 깨져도 공유 목록은 보여 준다
            }
        }

        return (list, string.Empty);
    }

    public sealed record ShareResult(bool Ok, string Message, bool Invited);

    /// <summary>사용자를 더한다 — 초대가 가면 Invited, 이미 접근 권한이 있던 사람이면 바로 반영된다.</summary>
    public async Task<ShareResult> AddCollaboratorAsync(RepoSlug slug, string login, string permission, CancellationToken cancellationToken)
    {
        var (output, error) = await RunGhAsync(
            ["api", "-X", "PUT", "repos/" + slug + "/collaborators/" + login, "-f", "permission=" + permission], null, 60, cancellationToken)
            .ConfigureAwait(false);
        if (output is null)
        {
            return new ShareResult(false, DescribeShareFailure(error, forRepo: false), false);
        }

        return new ShareResult(true, string.Empty, Invited: output.Trim().Length > 0);
    }

    public async Task<(bool Ok, string Message)> RemoveCollaboratorAsync(RepoSlug slug, Collaborator who, CancellationToken cancellationToken)
    {
        var path = who.IsPending
            ? "repos/" + slug + "/invitations/" + who.InvitationId
            : "repos/" + slug + "/collaborators/" + who.Login;
        var (output, error) = await RunGhAsync(["api", "-X", "DELETE", path], null, 60, cancellationToken).ConfigureAwait(false);
        return output is null ? (false, DescribeShareFailure(error, forRepo: false)) : (true, string.Empty);
    }

    /// 함정: --paginate 는 JSON 배열을 쪽마다 이어 붙여 `[...][...]` 로 낸다 — 배열 하나로 읽으면 두 쪽째에서 깨진다
    private static List<JsonElement> EnumeratePaginated(string output)
    {
        var items = new List<JsonElement>();
        var bytes = System.Text.Encoding.UTF8.GetBytes(output);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                continue;
            }

            using var doc = JsonDocument.ParseValue(ref reader);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                items.Add(item.Clone());
            }
        }

        return items;
    }

    private static string DescribeShareFailure(string error, bool forRepo)
    {
        if (error.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            return forRepo
                ? Loc.T("GitHub 에서 이 저장소를 찾지 못했습니다 — 지워졌거나 이 계정으로는 볼 수 없습니다.")
                : Loc.T("GitHub 에 그런 사용자가 없습니다 — 아이디를 확인해 주십시오.");
        }

        if (error.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("이 저장소의 공유를 바꿀 권한이 없습니다 — 소유자나 관리자만 할 수 있습니다.");
        }

        if (error.Contains("HTTP 422", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("GitHub 가 요청을 받아들이지 않았습니다 — 자기 자신이나 소유자는 더할 수 없습니다.");
        }

        if (error.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not logged into", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("GitHub 로그인이 풀렸습니다 — 다시 로그인해 주십시오.");
        }

        return error;
    }
}
