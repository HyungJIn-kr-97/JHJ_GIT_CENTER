using System.Diagnostics;
using Microsoft.Win32;

namespace GitCenter.App.Services;

/// <summary>
/// 로그인할 때 자동 실행 — HKCU Run 값 하나다.
/// 계약: 값 이름은 실행 파일 이름과 같다(JHJ_Git-Center) — 작업 관리자 「시작 앱」에 그 이름으로 보인다.
/// 함정: 사용자가 작업 관리자에서 끄면 StartupApproved 에 표식이 남는다 — 설정 화면이 그것을 읽어 맞춘다.
/// </summary>
public static class StartupRegistry
{
    public const string ValueName = "JHJ_Git-Center";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static string ExecutablePath => Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDisabledByTaskManager()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // 계약: 첫 바이트가 짝수면 켜짐, 홀수(2·3)면 사용자가 꺼 둔 것이다
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var path = ExecutablePath;
                if (!string.IsNullOrEmpty(path))
                {
                    key.SetValue(ValueName, "\"" + path + "\"");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 계약: 레지스트리 실패가 앱을 세우지 않는다 — 설정 화면이 다음에 다시 시도한다
        }
    }
}
