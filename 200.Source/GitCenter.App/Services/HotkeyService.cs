using System.Windows.Input;
using GitCenter.Application.Settings;
using NHotkey;
using NHotkey.Wpf;
using Serilog;

namespace GitCenter.App.Services;

/// <summary>
/// 전역 단축키 하나 — 어느 창에서든 Git 센터를 띄운다.
/// 함정: 다른 앱이 이미 쓰는 조합이면 등록이 실패한다 — 앱을 세우지 않고 로그만 남긴다.
/// </summary>
public sealed class HotkeyService(AppSettings settings, TrayHost tray) : IDisposable
{
    private const string Name = "JHJ_Git-Center.Show";

    private bool _registered;

    public string? LastError { get; private set; }

    public void Register()
    {
        Unregister();

        if (!TryParse(settings.Hotkey, out var key, out var modifiers))
        {
            LastError = $"단축키를 읽지 못했습니다: {settings.Hotkey}";
            return;
        }

        try
        {
            HotkeyManager.Current.AddOrReplace(Name, key, modifiers, OnPressed);
            _registered = true;
            LastError = null;
        }
        catch (HotkeyAlreadyRegisteredException)
        {
            LastError = $"{settings.Hotkey} 는 다른 프로그램이 쓰고 있습니다.";
            Log.Warning("단축키 충돌 {Hotkey}", settings.Hotkey);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warning(ex, "단축키 등록 실패");
        }
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            HotkeyManager.Current.Remove(Name);
        }
        catch
        {
            // 계약: 이미 없으면 그만이다
        }

        _registered = false;
    }

    private void OnPressed(object? sender, HotkeyEventArgs e)
    {
        tray.ShowWindow();
        e.Handled = true;
    }

    /// 계약: "Ctrl+Alt+G" 꼴을 읽는다 — 수식 키 이름은 Ctrl · Alt · Shift · Win
    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        return key != Key.None && modifiers != ModifierKeys.None;
    }

    public void Dispose() => Unregister();
}
