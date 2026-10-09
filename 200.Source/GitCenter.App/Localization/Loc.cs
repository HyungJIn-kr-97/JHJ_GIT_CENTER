using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace GitCenter.App.Localization;

/// <summary>
/// 화면 문구의 언어 전환. **한국어 원문이 곧 키**이고, 영어는 LocStrings 의 사전에서 찾는다.
/// 계약: XAML 은 {loc:Tr '저장소'} 로, 코드는 Loc.T(...) 로 읽는다 — 사전에 없으면 한국어가 그대로 나간다.
/// 함정: JHJ_AI_USAGE 는 영어를 키로 쓴다 — 이 앱은 한국어가 원문이라 방향이 반대다. 섞어 쓰지 않는다.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Korean = "ko";
    public const string English = "en";

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>언어가 바뀐 뒤 불린다 — 코드가 만든 문자열을 다시 그릴 곳이 구독한다.</summary>
    public static event Action? LanguageChanged;

    public static string Language { get; private set; } = Korean;

    public static bool IsKorean => Language == Korean;

    // 계약: 값 자체는 의미가 없고, 바뀌었다는 알림이 {loc:Tr} 바인딩을 전부 다시 평가시킨다
    public int Version { get; private set; }

    public static void SetLanguage(string? language)
    {
        var next = language == English ? English : Korean;
        if (next == Language && Instance.Version > 0)
        {
            return;
        }

        Language = next;
        Instance.Version++;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Version)));
        LanguageChanged?.Invoke();
    }

    public static string T(string text)
    {
        if (IsKorean)
        {
            return text;
        }

        return LocStrings.English.TryGetValue(text, out var english) ? english : text;
    }

    public static string T(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(format), args);
}

/// <summary>XAML 용: Text="{loc:Tr '전체 받기'}". 언어가 바뀌면 다시 평가된다.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Version))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = KeyConverter.Instance,
            ConverterParameter = Key
        };

        return binding.ProvideValue(serviceProvider);
    }

    private sealed class KeyConverter : IValueConverter
    {
        public static readonly KeyConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            Loc.T((string)parameter);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
