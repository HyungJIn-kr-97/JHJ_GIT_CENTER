using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GitCenter.App.ViewModels;

namespace GitCenter.App.Views;

// 저장소 목록의 손짓 — 끌어서 그룹 옮기기 · 두 번 눌러 줄 고치기 쪽지 열기 · 브랜치 고르개 채우기.
// 계약: 놓을 수 있는 곳은 그룹 머리 · 그 그룹의 줄 · 거르개 단의 「그룹」 줄이다 — 어느 그룹인지는 놓인 자리의 DataContext 로 읽는다.
// 계약: 체크한 줄을 끌면 보이는 줄 중 체크한 것이 함께 가고, 체크 안 한 줄은 그 줄만 간다(MainViewModel.RowsForDrag).
public partial class MainWindow
{
    private const string DragFormat = "GitCenter.RepoRows";

    private Point _dragStart;
    private RepoRow? _dragRow;
    private bool _rowPopupHooked;

    private async void OnBranchDropDownOpened(object sender, EventArgs e) => await ViewModel.LoadBranchNamesAsync();

    // 함정: 체크박스·톱니 위에서 누른 것은 끌기가 아니다 — 단추 위에서는 시작하지 않는다
    private void OnRepoListMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        _dragRow = Ancestor<ButtonBase>(source) is null ? RowAt(source) : null;
        _dragStart = e.GetPosition(RepoList);
    }

    private void OnRepoListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragRow = null;
            return;
        }

        var at = e.GetPosition(RepoList);
        if (Math.Abs(at.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(at.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var rows = ViewModel.RowsForDrag(_dragRow).ToArray();
        _dragRow = null;
        DragDrop.DoDragDrop(RepoList, new DataObject(DragFormat, rows), DragDropEffects.Move);
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) && DropGroup(e.OriginalSource as DependencyObject) is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnGroupDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not RepoRow[] rows || DropGroup(e.OriginalSource as DependencyObject) is not { } group)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.MoveRowsToGroupAsync(rows, group);
    }

    private void OnRepoListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton == MouseButton.Left && Ancestor<ButtonBase>(source) is null && RowAt(source) is { } row)
        {
            OpenRowPopup(row);
        }
    }

    private void OnRowEditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RepoRow row)
        {
            OpenRowPopup(row);
        }
    }

    private void OnRowPopupCloseClick(object sender, RoutedEventArgs e) => RowPopup.IsOpen = false;

    private async void OpenRowPopup(RepoRow row)
    {
        // 계약: 쪽지는 창의 다른 곳을 누르거나 창이 뒤로 가면 닫힌다
        if (!_rowPopupHooked)
        {
            _rowPopupHooked = true;
            PreviewMouseDown += (_, e) =>
            {
                if (!FromAnotherHwnd(e.OriginalSource))
                {
                    RowPopup.IsOpen = false;
                }
            };
            Deactivated += (_, _) => RowPopup.IsOpen = false;
        }

        RowPopup.IsOpen = false;
        await ViewModel.BeginEditAsync(row);
        RowPopup.IsOpen = true;
    }

    /// 계약: 쪽지·고르는 상자 안에서 난 입력인가 — 그러면 쪽지를 닫지 않는다.
    // 함정: Popup 은 제 창(HWND)에 그려지지만 라우트 이벤트는 논리 부모를 타고 본 창까지 올라온다 —
    //       창의 PreviewMouseDown 은 터널링이라 안쪽 ComboBox 보다 먼저 터져 쪽지를 닫아 버린다.
    // 계약: 브랜치 줄을 두 번 누르면 묻고 옮긴다 — 지금 브랜치는 묻지 않는다
    private async void OnBranchDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedBranch is not { IsCurrent: false } branch || ViewModel.SelectedRow is not { } row)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            Loc.T("{0} 저장소를 {1} 브랜치로 Switch 할까요?", row.Name, branch.Name) + Environment.NewLine
            + Loc.T("변경한 파일이 그 브랜치와 겹치면 옮기지 않습니다."),
            "JHJ Git Center", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await ViewModel.SwitchToBranchAsync(branch);
        }
    }

    private bool FromAnotherHwnd(object? source) =>
        source is DependencyObject at &&
        PresentationSource.FromDependencyObject(at) is { } from &&
        !ReferenceEquals(from, PresentationSource.FromVisual(this));

    private static RepoRow? RowAt(DependencyObject? source) => Ancestor<ListBoxItem>(source)?.DataContext as RepoRow;

    /// 계약: 놓인 자리가 가리키는 그룹 이름 — 빈 글은 「그룹 없음」이고 null 은 놓을 수 없는 곳이다
    private static string? DropGroup(DependencyObject? source)
    {
        for (var at = source; at is not null; at = Up(at))
        {
            switch ((at as FrameworkElement)?.DataContext)
            {
                case RepoRow row:
                    return row.Group;
                case CollectionViewGroup { Name: GroupNode node }:
                    return node.Key;
                case FacetItem { Facet: "group", Value: { } value }:
                    return value;
                case FacetItem or MainViewModel:
                    return null;
            }
        }

        return null;
    }

    private static T? Ancestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var at = source;
        while (at is not null and not T)
        {
            at = Up(at);
        }

        return at as T;
    }

    // 함정: 글 조각(Run)은 Visual 이 아니라 VisualTreeHelper 가 예외를 낸다 — 논리 트리로 올라간다
    private static DependencyObject? Up(DependencyObject at) =>
        at is Visual ? VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);
}
