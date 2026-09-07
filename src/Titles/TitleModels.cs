namespace SurfTimer.Titles;

public sealed record PlayerTitleSettings(bool IsVip, string? CustomTitle, string? ColorPattern, string NameColor);

public sealed record PlayerTitleDisplay(string Text, string ColorPattern, string NameColor, bool IsCustom, string CompetitiveTitle)
{
    public string ScoreboardText => $"[{Text}]";
}
