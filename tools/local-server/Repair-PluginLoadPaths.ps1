[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameInfoPath)
$ErrorActionPreference = 'Stop'
$content = Get-Content -LiteralPath $GameInfoPath -Raw
$newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
$anchor = '(?m)^([\t ]*)Game_LowViolence[^\r\n]*(?:\r?\n)'
if ([regex]::Matches($content, $anchor).Count -ne 1) { throw 'Expected exactly one Game_LowViolence search-path entry.' }
# Metamod must precede the Swiftly loader; the BotController shared API alone
# does not install its native movement hooks.
$content = [regex]::Replace($content, '(?m)^[\t ]*Game[\t ]+"?csgo/addons/(?:metamod|swiftlys2)"?[\t ]*(?://[^\r\n]*)?\r?\n', '')
$content = [regex]::Replace($content, $anchor, [System.Text.RegularExpressions.MatchEvaluator]{ param($match)
    $indent = $match.Groups[1].Value
    $match.Value + $indent + "Game`tcsgo/addons/metamod" + $newline + $indent + "Game`tcsgo/addons/swiftlys2" + $newline
})
Set-Content -LiteralPath $GameInfoPath -Value $content -NoNewline
Write-Host 'Configured Metamod followed by SwiftlyS2.'
