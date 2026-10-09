using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace GitCenter.App.Services;

/// <summary>
/// 테마 = 색상 팔레트 5종 × 명암(라이트·다크). 고른 조합의 브러시를 App 리소스에 갈아 끼운다.
/// 계약: 색 값은 JHJ_AI_USAGE 의 ThemeManager 와 같은 표다 — JHJ 앱끼리 같은 색을 쓴다.
/// 계약: 창 XAML 이 테마 브러시를 DynamicResource 로 읽어야 실행 중 전환이 반영된다.
/// </summary>
public static class ThemeManager
{
    public const string SystemMode = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    // 계약: 이 앱의 기본은 violet 이다 — 트레이에 함께 상주하는 JHJ_AI_USAGE(bull)·JHJ_OPS(emerald)·JHJ_VPN(navy)과 색으로 갈린다
    public const string DefaultPalette = "violet";

    private sealed record Tone(
        string Surface, string Panel, string Ink, string Muted, string Line,
        string Brand, string BrandInk, string BrandSoft, string Accent);

    private sealed record PaletteDef(string Name, Tone LightTone, Tone DarkTone);

    private static Tone NeutralLight(string brand, string brandInk, string brandSoft, string accent) =>
        new("#F4F4F6", "#FFFFFF", "#1A1A1F", "#62626E", "#E2E2E8", brand, brandInk, brandSoft, accent);

    private static Tone NeutralDark(string brand, string brandInk, string brandSoft, string accent) =>
        new("#111114", "#1B1B20", "#ECECF1", "#A4A4B0", "#34343C", brand, brandInk, brandSoft, accent);

    private static readonly PaletteDef[] Palettes =
    [
        new("violet",
            NeutralLight("#6A3FC2", "#FAF7FF", "#ECE4FB", "#B04A82"),
            NeutralDark("#A585FF", "#130A26", "#261A44", "#F08CC0")),
        new("navy",
            NeutralLight("#1F4FA3", "#FFFFFF", "#E3EBF9", "#0F7C86"),
            NeutralDark("#6E9BFF", "#07122A", "#14213D", "#5FD0D8")),
        new("bull",
            new("#F4F1EE", "#FFFFFF", "#1D1512", "#6B5F58", "#E4DCD6", "#B3261E", "#FFF8F3", "#F9E6E3", "#8A6414"),
            new("#120D0B", "#1C1512", "#F3EBE4", "#B3A79E", "#3A2F29", "#E5484D", "#1A0505", "#3A1414", "#E6C36A")),
        new("emerald",
            NeutralLight("#0F7A55", "#F2FFF9", "#DFF3EA", "#7A5A12"),
            NeutralDark("#3ECF8E", "#04140D", "#0F2E22", "#E0C068")),
        new("slate",
            NeutralLight("#334155", "#F8FAFC", "#E6E9EE", "#B45309"),
            NeutralDark("#CBD5E1", "#0F172A", "#1E293B", "#F59E0B")),
    ];

    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static string Palette { get; private set; } = DefaultPalette;

    public static IReadOnlyList<string> PaletteNames { get; } = Palettes.Select(p => p.Name).ToList();

    /// <summary>설정 창의 색 견본용 — 지금 명암에서 그 팔레트의 대표색.</summary>
    public static Brush SwatchOf(string palette)
    {
        var def = Find(palette);
        return Frozen((IsDark ? def.DarkTone : def.LightTone).Brand);
    }

    public static bool ResolveIsDark(string? preference) => preference switch
    {
        Dark => true,
        Light => false,
        _ => SystemIsDark()
    };

    public static void Apply(bool dark) => Apply(Palette, dark);

    public static void Apply(string? palette, bool dark)
    {
        var def = Find(palette);
        var tone = dark ? def.DarkTone : def.LightTone;

        var next = new ResourceDictionary
        {
            ["BgBrush"] = Frozen(tone.Surface),
            ["PanelBrush"] = Frozen(tone.Panel),
            ["WindowBorderBrush"] = Frozen(tone.Line),
            ["TextPrimaryBrush"] = Frozen(tone.Ink),
            ["TextSecondaryBrush"] = Frozen(Mix(tone.Ink, tone.Muted, 0.55)),
            ["TextMutedBrush"] = Frozen(tone.Muted),
            ["TextFaintBrush"] = Frozen(Mix(tone.Muted, tone.Surface, 0.30)),
            ["DividerBrush"] = Frozen(tone.Line),
            ["TrackBrush"] = Frozen(tone.Line),
            ["AccentBrush"] = Frozen(tone.Brand),
            ["AccentInkBrush"] = Frozen(tone.BrandInk),
            ["AccentSoftBrush"] = Frozen(tone.BrandSoft),
            ["HighlightBrush"] = Frozen(tone.Accent),
            ["SubtleOverlayBrush"] = Frozen(dark ? "#0AFFFFFF" : "#0A000000"),
            ["HoverOverlayBrush"] = Frozen(dark ? "#15FFFFFF" : "#15000000"),
            ["PressedOverlayBrush"] = Frozen(dark ? "#25FFFFFF" : "#25000000"),
            // 계약: 상태 색 네 가지 — 깨끗함 · 받을 것 있음 · 올릴 것 있음 · 충돌·오류
            ["OkBrush"] = Frozen(dark ? "#34D399" : "#047857"),
            ["BehindBrush"] = Frozen(dark ? "#93C5FD" : "#1D4ED8"),
            ["AheadBrush"] = Frozen(dark ? "#FDBA74" : "#C2410C"),
            ["DangerBrush"] = Frozen(dark ? "#FCA5A5" : "#B91C1C"),
            ["DirtyBrush"] = Frozen(dark ? "#E6C36A" : "#8A6414")
        };

        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;

        // 왜: 새 것을 먼저 넣고 옛 것을 빼야 전환 순간에 키가 비는 틈이 없다
        merged.Add(next);
        if (_current is not null)
        {
            merged.Remove(_current);
        }

        _current = next;
        IsDark = dark;
        Palette = def.Name;
    }

    private static PaletteDef Find(string? name) =>
        Palettes.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];

    private static SolidColorBrush Frozen(string hex) => Frozen((Color)ColorConverter.ConvertFromString(hex));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // 계약: amount 0 이면 from, 1 이면 to
    private static Color Mix(string from, string to, double amount)
    {
        var a = (Color)ColorConverter.ConvertFromString(from);
        var b = (Color)ColorConverter.ConvertFromString(to);
        byte Blend(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return Color.FromRgb(Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
    }

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
