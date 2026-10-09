namespace GitCenter.App.Localization;

/// <summary>
/// 한국어 원문 → 영어. 화면에 새 문구를 넣으면 여기에 한 줄 더한다.
/// 계약: 없는 키는 한국어가 그대로 나간다 — 빠뜨려도 화면이 깨지지는 않는다.
/// </summary>
internal static class LocStrings
{
    public static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        // 창·구역
        ["Git 통합 관리 센터"] = "Git Center",
        ["저장소"] = "Repositories",
        ["상태"] = "Status",
        ["작업 로그"] = "Activity",
        ["설정"] = "Settings",
        ["변경 파일"] = "Changed files",
        ["브랜치"] = "Branches",
        ["충돌"] = "Conflicts",

        // 단추 — 목록
        ["목록 새로 고침"] = "Rescan",
        ["상태 새로 고침"] = "Refresh status",
        ["모두 선택"] = "Select all",
        ["선택 해제"] = "Clear selection",
        ["변경된 것만"] = "Changed only",
        ["검색"] = "Search",
        ["저장소 추가"] = "Add repository",
        ["목록에서 숨기기"] = "Hide from list",

        // 단추 — 일괄 작업
        ["전체 Fetch"] = "Fetch all",
        ["main 받기"] = "Update default branch",
        ["현재로 병합"] = "Merge default into current",
        ["현재 브랜치 Pull"] = "Pull current",
        ["모든 추적 브랜치 갱신"] = "Update all tracked branches",
        ["커밋"] = "Commit",
        ["올리기(Push)"] = "Push",
        ["커밋 + 올리기"] = "Commit and push",
        ["병합 되돌리기"] = "Abort merge",
        ["멈추기"] = "Stop",

        // 열 이름
        ["이름"] = "Name",
        ["묶음"] = "Group",
        ["현재 브랜치"] = "Branch",
        ["앞섬/뒤처짐"] = "Ahead / behind",
        ["변경"] = "Changes",
        ["마지막 커밋"] = "Last commit",
        ["출처"] = "Source",
        ["경로"] = "Path",

        // 문장
        ["저장소를 고르면 상태가 여기에 보입니다."] = "Pick a repository to see its status.",
        ["충돌이 없습니다."] = "No conflicts.",
        ["변경이 없습니다."] = "No changes.",
        ["작업이 없습니다."] = "No activity yet.",
        ["git 을 찾지 못했습니다 — PATH 에 git 이 있어야 합니다."] = "git was not found on PATH.",
        ["폴더에서 열기"] = "Open folder",
        ["터미널에서 열기"] = "Open terminal",
        ["GitHub Desktop 에서 열기"] = "Open in GitHub Desktop",
        ["VS Code 에서 열기"] = "Open in VS Code",
        ["원격 페이지 열기"] = "Open remote page",
        ["커밋 메시지"] = "Commit message",
        ["닫기"] = "Close",
        ["적용"] = "Apply",
        ["기본값으로"] = "Reset",
        ["선택 %d개"] = "%d selected"
    };
}
