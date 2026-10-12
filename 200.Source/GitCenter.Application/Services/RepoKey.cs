namespace GitCenter.Application.Services;

/// <summary>
/// 저장소의 신원 = 정규화한 경로.
/// 왜: 같은 저장소를 폴더 스캔·GitHub Desktop·수동 추가가 각각 다른 표기로 들고 온다 — 한 줄로 합치려면 키가 하나여야 한다.
/// 계약: 아직 clone 하지 않은 계정 저장소는 경로가 없어 "remote:owner/name" 을 키로 쓴다.
/// </summary>
public static class RepoKey
{
    public static string ForPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch
        {
            // 계약: 정규화에 실패해도 키는 만들어 둔다 — 중복 합치기만 덜 될 뿐이다
        }

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    public static string ForRemote(string ownerAndName) => "remote:" + ownerAndName.Trim().ToLowerInvariant();

    public static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// 계약: 바로 위 폴더 이름을 묶음으로 쓴다 — c:\DEV\Github\ATIS\ATIS-SBU_DEV 는 "ATIS"
    public static string GroupOf(string path)
    {
        try
        {
            var parent = Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return parent?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public const string GroupSeparator = " / ";

    /// <summary>루트에서 몇 단계 아래 폴더까지를 묶음 이름으로 쓸지 — 1 이면 "ATIS", 2 면 "JHJ / 900.Legacy".</summary>
    /// 계약: level 이 0 이하거나 path 가 root 밖이면 GroupOf 와 같다 — 루트 바로 아래 저장소는 루트 폴더 이름을 쓴다
    public static string GroupUnder(string root, string path, int level)
    {
        var rootKey = ForPath(root);
        var pathKey = ForPath(path);
        if (level <= 0 || !IsUnder(rootKey, pathKey))
        {
            return GroupOf(path);
        }

        // 왜: 키는 소문자라 화면에 못 쓴다 — 길이만 키로 재고 글자는 정규화한 원래 경로에서 자른다
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return GroupOf(path);
        }

        var parts = full[rootKey.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var folders = parts.Take(Math.Max(0, parts.Length - 1)).Take(level).ToArray();
        return folders.Length == 0 ? NameOf(root) : string.Join(GroupSeparator, folders);
    }

    /// 계약: 둘 다 ForPath 를 거친 키여야 한다 — 같은 경로는 「아래」가 아니다
    public static bool IsUnder(string rootKey, string pathKey) =>
        pathKey.Length > rootKey.Length + 1
        && pathKey.StartsWith(rootKey, StringComparison.Ordinal)
        && pathKey[rootKey.Length] == Path.DirectorySeparatorChar;
}
