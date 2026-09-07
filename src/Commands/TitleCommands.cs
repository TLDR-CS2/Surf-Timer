using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SurfTimer.Chat;
using SurfTimer.Players;
using SurfTimer.Storage;
using SurfTimer.Titles;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;

namespace SurfTimer.Commands;

public sealed class TitleCommands(ISwiftlyCore core,SurfPlayerManager players,PlayerTitleRepository repository,
    TitleManager titles,RecordRepository records,ILogger<TitleCommands> logger)
{
    private static readonly HashSet<string> ReservedRanks=new(StringComparer.OrdinalIgnoreCase)
        {"provisional","surfer","skilled","veteran","expert","elite","master","legend","god"};
    private static readonly string[] Blacklist={"admin","administrator","mod","moderator","owner","staff","developer","dev","valve","fuck","shit","cunt","nigger","nigga","faggot"};
    private static readonly Regex ColorToken=new("\\{(?<name>[a-z]+)\\}",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.Compiled);
    private readonly List<Guid> _registrations=[];

    public void Register()
    {
        if(_registrations.Count!=0)return;
        RegisterRaw("customtag",OnCustomTag,"Sets your VIP tag: css_customtag \"{red}[TAG]\".");
        RegisterRaw("namecolour",OnNameColour,"Sets your VIP chat-name colour: css_namecolour red.");
        RegisterRaw("tagcolours",OnPalette,"Shows available VIP colours.");
        _registrations.Add(core.Command.RegisterCommand("stvip",OnVip,false,permission:"surftimer.admin",helpText:"Grants VIP title access: !stvip <SteamID64> <on|off>."));
        core.Command.RegisterCommandAlias("sw_stvip","css_stvip",true);
    }

    private void RegisterRaw(string name,ICommandService.CommandListener callback,string help)
    {
        _registrations.Add(core.Command.RegisterCommand("css_"+name,callback,true,helpText:help));
    }

    public void Unregister()
    {
        foreach(var name in new[]{"css_customtag","css_namecolour","css_tagcolours","css_stvip"})core.Command.UnregisterCommand(name);
        foreach(var registration in _registrations)core.Command.UnregisterCommand(registration);_registrations.Clear();
    }

    private void OnCustomTag(ICommandContext context)
    {
        if(!TryPlayer(context,out var session))return;
        if(context.Args.Length==0){context.Reply("Usage: css_customtag \"{red}[TAG]\" | css_customtag reset");return;}
        var input=string.Join(' ',context.Args).Trim();
        if(input.Equals("reset",StringComparison.OrdinalIgnoreCase)){_=ResetAsync(session);return;}
        if(!TryParseTag(input,out var tag,out var pattern,out var error)){context.Reply(error);return;}
        _=SaveTagAsync(session,tag,pattern);
    }

    private void OnNameColour(ICommandContext context)
    {
        if(!TryPlayer(context,out var session))return;
        if(context.Args.Length!=1||TitlePalette.Find(context.Args[0]) is not{} color){context.Reply("Usage: css_namecolour <colour>. Run css_tagcolours for the list.");return;}
        _=SaveNameColorAsync(session,color.Name);
    }

    private void OnPalette(ICommandContext context)=>context.Reply("Available colours: "+string.Join(", ",TitlePalette.Colors.Select(color=>color.Name))+".");

    private void OnVip(ICommandContext context)
    {
        if(context.Sender is null||context.Args.Length!=2||!ulong.TryParse(context.Args[0],out var steamId)||
           !(context.Args[1].Equals("on",StringComparison.OrdinalIgnoreCase)||context.Args[1].Equals("off",StringComparison.OrdinalIgnoreCase)))
        {context.Reply("[SurfTimer Admin] Usage: !stvip <SteamID64> <on|off>");return;}
        _=SetVipAsync(context,steamId,context.Args[1].Equals("on",StringComparison.OrdinalIgnoreCase));
    }

    private async Task SaveTagAsync(SurfPlayerSession session,string tag,string pattern)
    {
        try{var settings=await repository.LoadAsync(session.SteamId);if(!settings.IsVip){Reply(session,"VIP title customisation is not enabled for your account.");return;}
            await repository.SaveSelectionAsync(session.SteamId,tag,pattern);await titles.ApplyAsync(session.PlayerId,session.SessionId,session.SteamId);
            Reply(session,$"Custom tag set to [{TitlePalette.RenderChat(tag,pattern)}[default]].");}
        catch(Exception e){Fail(session,e);}
    }

    private async Task SaveNameColorAsync(SurfPlayerSession session,string color)
    {
        try{var settings=await repository.LoadAsync(session.SteamId);if(!settings.IsVip){Reply(session,"VIP title customisation is not enabled for your account.");return;}
            await repository.SaveNameColorAsync(session.SteamId,color);await titles.ApplyAsync(session.PlayerId,session.SessionId,session.SteamId);Reply(session,$"Chat name colour set to [{color}]{color}[/].");}
        catch(Exception e){Fail(session,e);}
    }

    private async Task ResetAsync(SurfPlayerSession session)
    {try{await repository.SaveSelectionAsync(session.SteamId,null,null);await repository.SaveNameColorAsync(session.SteamId,"default");await titles.ApplyAsync(session.PlayerId,session.SessionId,session.SteamId);Reply(session,"Custom tag reset; your competitive title is displayed again.");}catch(Exception e){Fail(session,e);}}

    private async Task SetVipAsync(ICommandContext context,ulong steamId,bool enabled)
    {try{await repository.SetVipAsync(steamId,enabled);await records.AppendAdminAuditAsync(context.Sender!.SteamID,context.Sender.Name,"vip.title",steamId.ToString(),enabled?"enabled":"disabled");
        var player=core.PlayerManager.GetPlayerFromSteamId(steamId,false);if(player is not null)await titles.ApplyAsync(player.PlayerID,player.SessionId,steamId);context.Reply($"[SurfTimer Admin] VIP title access {(enabled?"enabled":"disabled")} for {steamId}.");}
     catch(Exception e){logger.LogError(e,"Failed to update VIP title access for {SteamId}.",steamId);context.Reply("[SurfTimer Admin] Failed to update VIP title access. The player must have connected at least once.");}}

    private static bool TryParseTag(string input,out string tag,out string pattern,out string error)
    {
        tag="";pattern="";error="";input=input.Trim();var firstBracket=input.IndexOf('[');var lastBracket=input.LastIndexOf(']');
        if(firstBracket>=0&&lastBracket>firstBracket){input=input.Remove(lastBracket,1).Remove(firstBracket,1);}
        var current="default";var text=new StringBuilder();var colors=new List<string>();var position=0;
        foreach(Match match in ColorToken.Matches(input))
        {
            AddLiteral(input.AsSpan(position,match.Index-position),current,text,colors);
            var candidate=TitlePalette.Find(match.Groups["name"].Value);if(candidate is null){error=$"Unknown colour '{match.Groups["name"].Value}'. Run css_tagcolours for the list.";return false;}
            current=candidate.Name;position=match.Index+match.Length;
        }
        AddLiteral(input.AsSpan(position),current,text,colors);tag=text.ToString();
        if(tag!=tag.Trim()){error="Custom tags cannot begin or end with a space.";return false;}
        if(tag.Length is <1 or >10){error="Custom tags must contain 1-10 characters, excluding the surrounding brackets and colour markers.";return false;}
        if(!Regex.IsMatch(tag,"^[A-Za-z0-9 ]+$",RegexOptions.CultureInvariant)){error="Custom tags may contain letters, numbers and spaces only.";return false;}
        var normalized=Regex.Replace(tag,"[^A-Za-z0-9]","").ToLowerInvariant();
        if(ReservedRanks.Contains(normalized)){error="Competitive rank names cannot be used as custom tags.";return false;}
        if(Blacklist.Any(normalized.Contains)){error="That custom tag contains a reserved or blocked term.";return false;}
        pattern=string.Join(',',colors.Take(tag.Length));
        return true;
    }

    private static void AddLiteral(ReadOnlySpan<char> value,string color,StringBuilder text,List<string> colors)
    {foreach(var character in value){text.Append(character);colors.Add(color);}}
    private bool TryPlayer(ICommandContext context,out SurfPlayerSession session){session=null!;if(context.Sender is null||(session=players.Get(context.Sender.PlayerID)!) is null||!session.IsAuthorized){context.Reply("Your Steam account is not authorized yet.");return false;}return true;}
    private void Reply(SurfPlayerSession session,string text)=>core.Scheduler.NextTick(()=>{var player=core.PlayerManager.GetPlayer(session.PlayerId);if(player?.SessionId==session.SessionId)player.SendChat(text);});
    private void Fail(SurfPlayerSession session,Exception exception){logger.LogError(exception,"Title command failed for {SteamId}.",session.SteamId);Reply(session,"The title service is unavailable.");}
}
