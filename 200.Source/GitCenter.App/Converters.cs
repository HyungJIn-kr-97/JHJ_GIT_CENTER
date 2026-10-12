using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GitCenter.App.Localization;
using GitCenter.App.Services;
using GitCenter.App.ViewModels;
using GitCenter.Core.Models;

namespace GitCenter.App;

/// <summary>상태 한 단어 → 색. 색 값은 ThemeManager 가 넣어 둔 브러시에서 찾는다.</summary>
/// 계약: 찾지 못하면 본문 색으로 떨어진다 — 테마가 바뀌는 순간에도 바인딩이 비지 않게.
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            "충돌" or "오류" or "경로 없음" or "git 아님" or "읽기 실패" => "DangerBrush",
            "받을 것" => "BehindBrush",
            "올릴 것" => "AheadBrush",
            "변경" => "DirtyBrush",
            "깨끗함" => "OkBrush",
            _ => "TextMutedBrush"
        };

        return Lookup(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    internal static Brush Lookup(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush
        ?? System.Windows.Application.Current?.TryFindResource("TextPrimaryBrush") as Brush
        ?? Brushes.Gray;
}

/// <summary>작업 결과 → 색.</summary>
public sealed class OutcomeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StepOutcome.Success => "OkBrush",
            StepOutcome.Conflict => "DangerBrush",
            StepOutcome.Failed => "DangerBrush",
            StepOutcome.Canceled => "TextMutedBrush",
            _ => "TextMutedBrush"
        };

        return StateBrushConverter.Lookup(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>파일 변경 종류 두 글자 → 사람이 읽는 한 단어.</summary>
public sealed class ChangeKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileChange change)
        {
            return string.Empty;
        }

        if (change.IsConflict)
        {
            return "충돌";
        }

        if (change.IsUntracked)
        {
            return "새 파일";
        }

        var marks = new List<string>();
        if (change.IsStaged)
        {
            marks.Add(Describe(change.Index) + "(스테이지)");
        }

        if (change.HasWorkTreeChange)
        {
            marks.Add(Describe(change.WorkTree));
        }

        return marks.Count == 0 ? "변경" : string.Join(" · ", marks);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    private static string Describe(char code) => code switch
    {
        'M' => "수정",
        'A' => "추가",
        'D' => "삭제",
        'R' => "이름 바뀜",
        'C' => "복사",
        'T' => "형식 바뀜",
        _ => "변경"
    };
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>빈 문자열·null 이면 숨긴다. ConverterParameter="invert" 면 반대 — 빈 칸일 때만 보인다.</summary>
/// 계약: invert 쪽이 글상자의 흐린 안내 문구(placeholder)를 켜고 끈다
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = !string.IsNullOrWhiteSpace(value as string);
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0 보다 크면 보인다 — 진행 막대·건수 뱃지에 쓴다. ConverterParameter="invert" 면 반대다.</summary>
// 함정: invert 를 안 다루면 「없습니다」 안내가 영영 안 보인다 — 건수 0 일 때 보여야 하는 쪽이다
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is int count && count > 0;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>값이 null 이면 숨긴다 — 고른 저장소가 없을 때 쓰는 쪽. ConverterParameter="invert" 면 반대다.</summary>
/// 함정: 문자열용 EmptyToVisibility 로는 못 가린다 — 그쪽은 객체를 string 으로 캐스팅해 늘 null 로 본다
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>값이 있으면 true — 고른 것이 없을 때 단추를 끄는 쪽에 쓴다.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>충돌 덩어리의 줄 묶음 → 여러 줄 문자열.</summary>
public sealed class LinesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> lines ? string.Join(Environment.NewLine, lines) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>명암 키("system"·"light"·"dark") → 사람이 읽는 이름.</summary>
/// 왜: 설정 창의 고르는 상자에 내부 키가 그대로 보이면 사용자가 무슨 뜻인지 알 수 없다
public sealed class ThemeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        ThemeManager.Light => Loc.T("밝게"),
        ThemeManager.Dark => Loc.T("어둡게"),
        _ => Loc.T("Windows 설정 따름")
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>팔레트 키 → 사람이 읽는 색 이름.</summary>
public sealed class PaletteLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "violet" => Loc.T("보라"),
        "navy" => Loc.T("남색"),
        "bull" => Loc.T("빨강"),
        "emerald" => Loc.T("초록"),
        "slate" => Loc.T("회색"),
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>팔레트 키 → 지금 명암에서의 대표색 견본.</summary>
public sealed class PaletteSwatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeManager.SwatchOf(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>아이콘 선택값("" · "preset:키" · 파일 경로) → 고르는 상자에 보일 이름.</summary>
public sealed class IconChoiceLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var choice = value as string ?? string.Empty;
        if (choice.Length == 0)
        {
            return Loc.T("기본 (금박 JG)");
        }

        if (choice.StartsWith(Jhj.Core.Wpf.Shell.AppIcon.PresetPrefix, StringComparison.Ordinal))
        {
            var key = choice[Jhj.Core.Wpf.Shell.AppIcon.PresetPrefix.Length..];
            var preset = Jhj.Core.Wpf.Shell.AppIcon.Presets.FirstOrDefault(p => p.Key == key);
            return preset is null ? key : Loc.T(preset.Label);
        }

        return Loc.T("불러온 그림");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>아이콘 선택값 → 견본 그림. 계약: 견본은 40px 로 그려 20 DIP 자리에 넣는다 — 배율 200% 까지 또렷하다.</summary>
public sealed class IconChoicePreviewConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Jhj.Core.Wpf.Shell.AppIcon.Preview(value as string, 40);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>언어 키("en"·"ko"·"zh") → 그 언어로 적은 이름. 이름의 정본은 Core 의 LocLanguages 다.</summary>
/// 계약: 언어 이름은 번역하지 않는다 — 읽을 줄 모르는 언어로 보이면 되돌아올 수 없다
public sealed class LanguageLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Jhj.Core.Wpf.Localization.LocLanguages.NameOf(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>화면 배율 → "100%" 꼴.</summary>
public sealed class ScaleLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double scale ? $"{scale * 100:0}%" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>「목록을 조밀하게」 설정 → 목록 한 줄의 Style.</summary>
/// 함정: ItemContainerStyle 은 Style 객체를 받는다 — 바인딩으로 바꾸려면 여기서 리소스를 찾아 돌려줘야 한다
public sealed class RowStyleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "ListRowCompact" : "ListRow";
        return System.Windows.Application.Current?.TryFindResource(key) as Style;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>diff 한 줄의 종류 → 그 줄 바탕색.</summary>
/// 왜: 더한 줄·지운 줄을 글자색만으로 가르면 긴 파일에서 한눈에 안 들어온다 — 줄 전체에 옅은 바탕을 깐다
public sealed class DiffBackConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DiffLineKind.Added => "DiffAddBackBrush",
            DiffLineKind.Removed => "DiffDelBackBrush",
            DiffLineKind.Hunk => "SubtleOverlayBrush",
            _ => null
        };

        return key is null ? Brushes.Transparent : StateBrushConverter.Lookup(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>diff 한 줄의 종류 → 글자색.</summary>
public sealed class DiffForeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DiffLineKind.Added => "OkBrush",
            DiffLineKind.Removed => "DangerBrush",
            DiffLineKind.Hunk => "TextFaintBrush",
            _ => "TextSecondaryBrush"
        };

        return StateBrushConverter.Lookup(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>diff 한 줄의 종류 → 줄 앞에 붙는 부호 한 글자.</summary>
public sealed class DiffSignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        DiffLineKind.Hunk => "@",
        _ => " "
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>번호가 ConverterParameter 와 같으면 보인다 — 제목 띠의 탭이 고른 화면만 남긴다.</summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>번호가 ConverterParameter 와 같으면 true — 탭(RadioButton)의 IsChecked 를 번호 하나에 잇는다.</summary>
/// 계약: 꺼지는 쪽(false)은 되돌려 보내지 않는다 — 켜지는 탭 하나만 번호를 바꾼다
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter?.ToString(), out var index) ? index : Binding.DoNothing;
}
