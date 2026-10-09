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
}
