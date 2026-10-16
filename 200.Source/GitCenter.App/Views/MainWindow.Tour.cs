using System.Windows;
using GitCenter.App.Controls;
using GitCenter.App.ViewModels;

namespace GitCenter.App.Views;

// 도움말 안내의 걸음 목록 — 「도움말」을 누르면 지금 화면의 요소를 차례로 비춘다(그리기는 Controls/TourOverlay).
// 계약: 걸음은 화면(PageIndex)마다 따로다 — 첫 걸음은 늘 화면 탭이고, 보이지 않는 요소의 걸음은 건너뛴다.
// 계약: 화면에 기능을 더하면 여기에 한 걸음을 더한다 — 비출 요소에는 XAML 에서 x:Name 을 붙인다.

public partial class MainWindow
{
    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var steps = TourSteps(viewModel.PageIndex).Where(s => s.Target is null || s.Target.IsVisible).ToList();
        Tour.Start(steps, Loc.T("이전"), Loc.T("다음"), Loc.T("끝내기"), Loc.T("닫기"));
    }

    private IEnumerable<TourStep> TourSteps(int page)
    {
        yield return Step(NavTabs, "화면 탭",
            "화면은 넷입니다. 「대시보드」는 전체 현황, 「저장소 관리」는 받기·커밋·올리기, 「커밋 내역」은 고른 기간·저장소의 커밋, 「점검」은 설정 점검과 일괄 적용입니다. 도움말은 지금 보고 있는 화면을 짚습니다.");
        yield return Step(AccountButton, "GitHub 계정",
            "gh CLI 로 로그인한 GitHub 계정입니다. 누르면 로그인 콘솔이 열리고(브라우저에서 승인), 로그인돼 있으면 로그아웃합니다. 원격이 없는 저장소를 「GitHub 에 올리기」로 만들어 연결할 때 이 계정을 씁니다.");

        switch (page)
        {
            case 0:
                yield return Step(DashboardPage, "대시보드",
                    "저장소 전체를 한눈에 봅니다. 위쪽 숫자 칸과 카드의 줄은 건너가는 길입니다 — 누르면 그 조건이 걸린 「저장소 관리」로 옮깁니다.");
                yield return Step(DashboardPage.FindName("TopRow") as FrameworkElement, "통계 카드",
                    "카드 머리의 고르개로 표시 방식(도넛·막대·표 등)을 바꿉니다. 「UI 편집」을 켜면 카드 제목에서 그 칸에 올릴 통계를 고릅니다. 고르는 즉시 저장됩니다.");
                yield return Step(DashboardPage.FindName("MiddleRow") as FrameworkElement, "커밋 캘린더와 저장소 카드",
                    "캘린더는 기간(30일~2년)과 보기 형식을 따로 고릅니다. 칸·막대를 누르면 그 날의 커밋이, 「변경 많은 저장소」·「변경 줄 수」·「마지막 커밋 시점」의 줄을 누르면 그 저장소의 커밋이 「커밋 내역」에 열립니다.");
                yield return Step(DashboardPage.FindName("BottomRow") as FrameworkElement, "확인 필요 · 최근 커밋 · 최근 작업",
                    "「확인 필요 저장소」는 저장소 관리로, 「최근 커밋」은 그 저장소의 커밋 내역으로 건너갑니다. 카드 크기는 위쪽 「UI 편집」을 켠 동안에만 끌어서 바꿉니다.");
                break;

            case 1:
                yield return Step(RepoToolbar, "현황과 검색",
                    "왼쪽 칩은 변경 · 받을 것 · 올릴 것 · 충돌이 있는 저장소 수입니다. 누르면 그 상태만 남깁니다. 오른쪽에서 이름·경로를 찾고, 상태와 목록을 새로 읽고, 설정을 엽니다.");
                yield return Step(FacetPanel, "필터",
                    "그룹 · 상태 · 작업 · 브랜치를 조건마다 하나씩 골라 겹쳐 겁니다. 걸린 줄이나 「전체」를 다시 누르면 풉니다. 「그룹」 머리의 톱니로 그룹을 만들고 이름을 바꾸고 지웁니다. 이 단 머리의 화살표로 접고 폅니다.");
                yield return Step(RepoList, "저장소 목록",
                    "체크한 저장소가 일괄 작업의 대상입니다 — 필터에 가려진 저장소는 체크돼 있어도 건드리지 않습니다. 줄을 누르면 오른쪽에 상세가 열리고, 「작업 중 표시」로 지금 손대는 저장소에 표를 붙입니다.");
                yield return Step(AddRepoButton, "저장소 추가",
                    "폴더를 옮겨 다니며 목록에 없는 git 저장소를 찾아 체크해 더합니다. 숨긴 저장소도 여기서 다시 보이게 합니다.");
                yield return Step(DetailPanel, "저장소 상세",
                    "「변경 파일」은 파일별 추가·삭제 줄 수를 보여 주고, 확장자가 여럿이면 위쪽 칩으로 좁힙니다. 파일을 누르면 오른쪽 단에 차이가 펴집니다. 「브랜치」·「충돌」 탭도 여기 있고, 「상태」 탭은 지금 상태가 왜 그런지와 stash 목록을, 「설정」 탭은 GitHub 공유 사용자를 보여 주고 내 저장소면 더하고 뺍니다.");
                yield return Step(ActivityRail, "변경 내용 · 작업 로그",
                    "접혀 있는 오른쪽 단입니다. 눌러 펴면 고른 파일의 차이를 보고, 「작업 로그」는 고른 저장소의 일만, 「시스템 작업 로그」는 전체 기록을 봅니다.");
                yield return Step(ActionBar, "일괄 작업",
                    "브랜치 고르기 · 받기 · 병합 · 올리기가 줄마다 모여 있고, 체크한 저장소에만 돕니다. 커밋 제목·메시지에는 자리표 {기능}·{그룹}·{저장소} 를 쓸 수 있고 누를 때 채워집니다. 강제 올리기는 없습니다 — 거부되면 먼저 받아서 합칩니다.");
                break;

            case 2:
                yield return Step(HistoryPage, "커밋 내역",
                    "대시보드에서 캘린더의 칸·막대나 저장소 줄을 누르면 채워집니다. 지금 브랜치의 커밋만 읽습니다. 줄마다 「웹에서 보기」는 원격 사이트의 그 커밋을, 「저장소 보기」는 저장소 관리의 그 저장소를 엽니다.");
                break;

            default:
                yield return Step(AuditPage, "점검",
                    "「점검 실행」은 전체 저장소의 작성자 · 원격 · 무시 목록 · 줄바꿈 설정을 읽어 다수와 다른 것을 표시합니다. 표는 읽기만 합니다.");
                yield return Step(AuditTools, "일괄 적용",
                    "「작성자 일괄 적용」과 「공통 무시 목록」은 저장소 관리에서 체크한 저장소에만 씁니다. 먼저 「미리 보기」로 바뀔 저장소를 확인해야 「적용」이 나오고, 바꾼 내용은 작업 로그에 남습니다.");
                break;
        }
    }

    private static TourStep Step(FrameworkElement? target, string title, string body) => new(target, Loc.T(title), Loc.T(body));
}
