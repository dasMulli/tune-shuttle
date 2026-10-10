namespace Bridge.Web.Localization;

/// <summary>A CLDR cardinal plural category (the suffix of an i18next plural key: <c>_one</c>, <c>_few</c>, …).</summary>
public enum PluralCategory
{
    /// <summary><c>_one</c>.</summary>
    One,

    /// <summary><c>_few</c> (ru, uk).</summary>
    Few,

    /// <summary><c>_many</c> (ru, uk).</summary>
    Many,

    /// <summary><c>_other</c>: the fallback of every language.</summary>
    Other,
}

/// <summary>
/// The CLDR cardinal plural rules of the app's languages for integer counts (UI-SPEC 3.1). Only integers are counted
/// in this app, so the fraction branches of CLDR (<c>v != 0</c>) and the million rules of es/fr/pt are not modelled:
/// <list type="bullet">
/// <item><c>de</c>, <c>en</c>, <c>es</c>: <c>n == 1</c> → one, else other.</item>
/// <item><c>fr</c>, <c>pt</c> (pt-BR): <c>n == 0 || n == 1</c> → one, else other.</item>
/// <item><c>ru</c>, <c>uk</c>: <c>n % 10 == 1 &amp;&amp; n % 100 != 11</c> → one; <c>n % 10</c> in 2..4 and
/// <c>n % 100</c> not in 12..14 → few; else many.</item>
/// </list>
/// Unknown languages use the de/en rule. Negative counts are categorised by their absolute value.
/// </summary>
public static class PluralRules
{
    /// <summary>The category of <paramref name="count"/> in the language <paramref name="code"/> (two-letter).</summary>
    public static PluralCategory Select(string code, long count)
    {
        // The absolute value as ulong, so long.MinValue (…808) keeps its digits.
        var n = count < 0 ? (ulong)(-(count + 1)) + 1 : (ulong)count;
        switch (code)
        {
            case "fr":
            case "pt":
                return n <= 1 ? PluralCategory.One : PluralCategory.Other;
            case "ru":
            case "uk":
                var mod10 = n % 10;
                var mod100 = n % 100;
                if (mod10 == 1 && mod100 != 11)
                {
                    return PluralCategory.One;
                }

                return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? PluralCategory.Few : PluralCategory.Many;
            default:
                return n == 1 ? PluralCategory.One : PluralCategory.Other;
        }
    }

    /// <summary>The i18next key suffix of <paramref name="category"/>: <c>_one</c>, <c>_few</c>, <c>_many</c> or <c>_other</c>.</summary>
    public static string Suffix(PluralCategory category) => category switch
    {
        PluralCategory.One => "_one",
        PluralCategory.Few => "_few",
        PluralCategory.Many => "_many",
        _ => "_other",
    };
}
