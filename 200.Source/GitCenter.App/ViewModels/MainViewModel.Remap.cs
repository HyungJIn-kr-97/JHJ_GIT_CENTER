namespace GitCenter.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>설정 창이 루트·그룹 매핑을 갈아엎은 뒤 목록을 다시 세운다.</summary>
    /// 함정: 사라진 그룹을 가리키는 거르개가 남으면 다시 훑은 목록이 빈 것으로 보인다 — 거르개를 먼저 푼다
    public async Task ReloadMappingAsync()
    {
        ResetFilters();
        await RescanAsync().ConfigureAwait(true);
    }
}
