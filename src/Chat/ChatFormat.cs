namespace SurfTimer.Chat;

/// <summary>Shared, intentionally small colour and layout vocabulary for player-facing chat.</summary>
public static class ChatFormat
{
    public const string Reset = "\u0001";
    public const string ErrorColor = "\u0002";
    public const string RouteColor = "\u0003";
    public const string SuccessColor = "\u0004";
    public const string MutedColor = "\u0008";
    public const string HighlightColor = "\u0009";
    public const string BrandColor = "\u000B";

    public static string Prefix => $"{BrandColor}[SurfTimer]{Reset}";
    public static string Message(string text) => $"{Prefix} {text}{Reset}";
    public static string Header(string title) => $"{Prefix} {BrandColor}{title.ToUpperInvariant()}{Reset}";
    public static string Row(string label, string value, string valueColor = Reset) =>
        $"{MutedColor}{label}{Reset} {valueColor}{value}{Reset}";
    public static string Success(string text) => Message($"{SuccessColor}{text}{Reset}");
    public static string Warning(string text) => Message($"{HighlightColor}{text}{Reset}");
    public static string Error(string text) => Message($"{ErrorColor}{text}{Reset}");
    public static string OnOff(bool enabled) => enabled
        ? $"{SuccessColor}ON{Reset}"
        : $"{MutedColor}OFF{Reset}";
    public static string Rank(int rank) => rank == 1
        ? $"{HighlightColor}#1{Reset}"
        : $"{Reset}#{rank}{Reset}";
}
