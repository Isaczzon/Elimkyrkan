using System.Globalization;

namespace Elimkyrkan.Web.Helpers;

public static class I18n
{
    public const string Swedish = "sv-SE";
    public const string English = "en-US";
    public const string Ukrainian = "uk-UA";
    public const string Spanish = "es-ES";
    public const string Thai = "th-TH";

    public static readonly (string Iso, string Native, string PathPrefix)[] Languages = new[]
    {
        (Swedish,    "Svenska",    "/"),
        (English,    "English",    "/en/"),
        (Ukrainian,  "Українська", "/uk/"),
        (Spanish,    "Español",    "/es/"),
        (Thai,       "ไทย",        "/th/"),
    };

    public static string CurrentTwoLetter() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public static string T(string sv, string en, string uk, string es, string th)
    {
        return CurrentTwoLetter() switch
        {
            "en" => en,
            "uk" => uk,
            "es" => es,
            "th" => th,
            _ => sv,
        };
    }
}
