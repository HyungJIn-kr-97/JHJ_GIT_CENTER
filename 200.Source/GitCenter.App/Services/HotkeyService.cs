using System.Windows.Input;
using GitCenter.Application.Settings;
using Jhj.Core.Wpf.Hotkeys;
using Serilog;
using CoreHotkeys = Jhj.Core.Wpf.Hotkeys.HotkeyService;

namespace GitCenter.App.Services;

/// <summary>
/// 전역 단축키 하나 — 어느 창에서든 Git 센터를 띄운다.
/// 계약: 해석·등록·규칙 검사는 JHJ_CS_CORE 가 한다 — 여기는 **이 앱의 설정 한 칸과 실패 문구**만 갖는다.
/// 계약: 자리표는 JHJ_DEV/000.AGENTS_MD/060.단축키/060.단축키-대장.md 다 — Git Center 는 3번(Ctrl+Alt+3)이다.
/// 함정: 읽을 수 없는 값을 Core 에 그대로 넘기면 Core 가 제 기본값으로 등록해 버린다 — 그래서 **여기서 먼저 걸러**
///       등록 자체를 하지 않고, Core 의 되살림 값도 이 앱 자리로 맞춰 둔다(아래 정적 생성자).
/// </summary>
public sealed class HotkeyService(AppSettings settings, TrayHost tray) : IDisposable
{
    // 왜: Core 가 1번 칸을 되살릴 때 쓰는 값이다 — 안 맞추면 설정이 깨졌을 때 자리표 밖의 키로 산다
    static HotkeyService() => HotkeyRules.Default = AppSettings.DefaultHotkey;

    private CoreHotkeys? _inner;

    public string? LastError { get; private set; }

    public void Register()
    {
        Unregister();

        if (!TryParse(settings.Hotkey, out var key, out var modifiers))
        {
            LastError = $"단축키를 읽지 못했습니다: {settings.Hotkey}";
            return;
        }

        if (HotkeyRules.Check(key, modifiers).Verdict == HotkeyVerdict.Blocked)
        {
            LastError = $"{settings.Hotkey} 는 쓸 수 없는 조합입니다.";
            return;
        }

        LastError = null;
        _inner = new CoreHotkeys([settings.Hotkey], onPressed: tray.ShowWindow, warn: OnWarn);
    }

    public void Unregister()
    {
        _inner?.Dispose();
        _inner = null;
    }

    private void OnWarn(string message, Exception? ex)
    {
        LastError = $"{settings.Hotkey} 는 다른 프로그램이 쓰고 있습니다.";
        Log.Warning(ex, "단축키 {Hotkey}: {Message}", settings.Hotkey, message);
    }

    /// 계약: "Ctrl+Alt+3" 꼴을 읽는다 — 수식 키 이름은 Ctrl · Alt · Shift · Win
    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers) =>
        HotkeyRules.TryParse(text, out key, out modifiers) && modifiers != ModifierKeys.None;

    public void Dispose() => Unregister();
}
