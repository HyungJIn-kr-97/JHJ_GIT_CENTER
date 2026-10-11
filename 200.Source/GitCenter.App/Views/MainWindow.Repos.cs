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
        // 계약: 쪽지는 창의 다른 곳을 누르거나 창이 뒤로 가면 닫힌다 — 쪽지 안(다른 창)을 누르는 것은 여기에 안 잡힌다
        if (!_rowPopupHooked)
        {
            _rowPopupHooked = true;
            PreviewMouseDown += (_, _) => RowPopup.IsOpen = false;
            Deactivated += (_, _) => RowPopup.IsOpen = false;
        }

        RowPopup.IsOpen = false;
        await ViewModel.BeginEditAsync(row);
        RowPopup.IsOpen = true;
    }

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
