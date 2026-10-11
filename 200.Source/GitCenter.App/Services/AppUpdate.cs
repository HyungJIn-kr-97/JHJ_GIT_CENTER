using GitCenter.Application.Settings;
using Jhj.Core.Settings;
using Jhj.Core.Updates;
using Microsoft.Extensions.Configuration;

namespace GitCenter.App.Services;

/// <summary>
/// GitHub Release 자가 업데이트의 좌표 — 확인·내려받기·교체는 JHJ_CS_CORE 의 GithubUpdateService 가 한다.
/// 계약: PackagePrefix 는 800.Deploy\publish.ps1 의 $PackagePrefix 와 같아야 한다 — 어긋나면 오류 없이 「최신입니다」로 끝난다.
/// 계약: 저장소가 공개여야 한다 — 앱이 토큰 없이 Releases 를 읽는다.
/// </summary>
public static class AppUpdate
{
    public const string Repository = "HyungJIn-kr-97/JHJ_GIT_CENTER";

    public const string PackagePrefix = "JHJ_Git-Center";

    /// 계약: 릴리스마다 같은 이름으로 올리는 설치 파일이다 — 800.Deploy\publish.ps1 의 $SetupAsset 과 같아야 「설치 파일 주소」가 열린다
    public const string SetupAssetName = "JHJ_Git-Center_Setup.exe";

    /// 계약: 800.Deploy\winget-manifest.ps1 의 $id 와 같다 — winget 저장소에 받아들여지기 전에는 그 명령이 꾸러미를 찾지 못한다
    public const string WingetId = "HyungJin.JHJ_Git-Center";

    /// 계약: 받은 설치 파일에 이 인자를 주면 묻지 않고 설치한다 — winget 이 조용히 깔 때 쓴다
    public const string SilentInstallArg = "--silent";

    /// 계약: appsettings.json 의 「Jhj:Update」 구역이 덮는다 — Enabled 를 false 로 두면 확인도 적용도 하지 않는다
    public static UpdateOptions FromConfiguration(IConfiguration configuration) =>
        UpdateOptions.FromConfiguration(configuration, Repository, PackagePrefix);
}

/// 왜: Application 층은 Core 를 당기지 않는다 — 예약 업데이트가 읽을 세 칸을 여기서 계약 모양으로 잇는다
public sealed class UpdateSettings(AppSettings settings) : IScheduledUpdateSettings
{
    public bool AutoUpdateEnabled => settings.AutoUpdateEnabled;

    public string AutoUpdateTime => settings.AutoUpdateTime;

    public bool AutoUpdateNotify => settings.AutoUpdateNotify;
}
