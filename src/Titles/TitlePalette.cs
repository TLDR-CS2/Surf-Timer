using System.Text;

namespace SurfTimer.Titles;

public sealed record TitleColor(string Name);

public static class TitlePalette
{
    public static IReadOnlyList<TitleColor> Colors { get; } =
    [
        new("default"),new("red"),new("darkred"),new("orange"),new("lightyellow"),new("yellow"),
        new("green"),new("lime"),new("olive"),new("blue"),new("lightblue"),new("darkblue"),
        new("bluegrey"),new("magenta"),new("lightpurple"),new("purple"),new("white"),new("grey"),
        new("gold"),new("silver")
    ];

    public static TitleColor? Find(string value) => Colors.FirstOrDefault(color =>
        color.Name.Equals(value.Trim().Trim('{','}','[',']'),StringComparison.OrdinalIgnoreCase));

    public static string DefaultFor(string competitiveTitle) => competitiveTitle switch
    {
        "God"=>"red", "Legend"=>"orange", "Master"=>"gold", "Elite"=>"lime",
        "Expert"=>"lightblue", "Veteran"=>"lightpurple", "Skilled"=>"silver", _=>"grey"
    };

    public static string RenderChat(string text,string pattern)
    {
        var colors=Parse(pattern);if(colors.Count==0)colors.Add("default");var output=new StringBuilder(text.Length*10);
        for(var index=0;index<text.Length;index++)output.Append('[').Append(colors[Math.Min(index,colors.Count-1)]).Append(']').Append(text[index]);
        return output.Append("[/]").ToString();
    }

    public static List<string> Parse(string? pattern)=>string.IsNullOrWhiteSpace(pattern)?[]:pattern
        .Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
        .Select(value=>Find(value)?.Name).Where(value=>value is not null).Cast<string>().ToList();
}
