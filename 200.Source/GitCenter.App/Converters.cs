using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
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

/// <summary>빈 문자열·null 이면 숨긴다.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

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
