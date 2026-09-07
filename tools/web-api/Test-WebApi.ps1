[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [switch]$IncludeRateLimit
)
$ErrorActionPreference = 'Stop'
$script:passed = 0

function Invoke-TestRequest {
    param([string]$Path)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd('/') + $Path)
        [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $response.Content | ConvertFrom-Json }
    } catch {
        $status = [int]$_.Exception.Response.StatusCode
        $body = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message | ConvertFrom-Json } else { $null }
        [pscustomobject]@{ Status = $status; Body = $body }
    }
}

function Assert-Api {
    param([bool]$Condition,[string]$Name)
    if (-not $Condition) { throw "FAILED: $Name" }
    $script:passed++; Write-Host "PASS: $Name"
}

$version = Invoke-TestRequest '/api/version'
Assert-Api ($version.Status -eq 200 -and $version.Body.service -eq 'SurfTimer.Web') 'version diagnostics'
$health = Invoke-TestRequest '/api/health'
Assert-Api ($health.Status -eq 200 -and $health.Body.schemaVersion -ge 1) 'database health'
$maps = Invoke-TestRequest '/api/maps'
Assert-Api ($maps.Status -eq 200 -and $maps.Body.Count -gt 0) 'enabled map catalog'

$map = $maps.Body[0].name
$leaderboard = Invoke-TestRequest "/api/maps/$map/leaderboard?route=main&page=1&pageSize=1"
Assert-Api ($leaderboard.Status -eq 200 -and $leaderboard.Body.pagination.pageSize -eq 1) 'leaderboard pagination contract'
$mapStats = Invoke-TestRequest "/api/maps/$map/stats"
Assert-Api ($mapStats.Status -eq 200 -and $null -ne $mapStats.Body.totalCompletions) 'map statistics'
$activity = Invoke-TestRequest '/api/activity?limit=5'
Assert-Api ($activity.Status -eq 200 -and $activity.Body.activity.Count -le 5) 'global PB activity'
$rankings = Invoke-TestRequest '/api/rankings?limit=10'
Assert-Api ($rankings.Status -eq 200 -and $rankings.Body.policy -eq 'Points') 'dynamic points rankings'
if ($leaderboard.Body.records.Count -gt 0) {
    Assert-Api ($leaderboard.Body.records[0].rank -eq 1) 'leaderboard first-page rank'
    $steamId = $leaderboard.Body.records[0].steamId
    $profile = Invoke-TestRequest "/api/players/$steamId"
    Assert-Api ($profile.Status -eq 200 -and $profile.Body.steamId -eq $steamId) 'player profile'
    $records = Invoke-TestRequest "/api/players/$steamId/records?page=1&pageSize=2"
    Assert-Api ($records.Status -eq 200 -and $records.Body.pagination.pageSize -eq 2) 'player PB pagination contract'
    $playerStats = Invoke-TestRequest "/api/players/$steamId/stats"
    Assert-Api ($playerStats.Status -eq 200 -and $null -ne $playerStats.Body.worldRecords) 'player global statistics'
    $history = Invoke-TestRequest "/api/players/$steamId/history?pageSize=2"
    Assert-Api ($history.Status -eq 200 -and $history.Body.pagination.pageSize -eq 2) 'player PB history'
    $points = Invoke-TestRequest "/api/players/$steamId/points"
    Assert-Api ($points.Status -eq 200 -and $points.Body.ranking.points -ge 50) 'player points rank'
    $filtered = Invoke-TestRequest "/api/players/$steamId/records?route=main&sort=rank&pageSize=5"
    Assert-Api ($filtered.Status -eq 200 -and @($filtered.Body.records | Where-Object route -ne 'main').Count -eq 0) 'player record filtering and sorting'
}

Assert-Api ((Invoke-TestRequest "/api/maps/$map/leaderboard?route=kz").Status -eq 400) 'invalid route rejected'
Assert-Api ((Invoke-TestRequest '/api/maps/surf_does_not_exist/routes').Status -eq 404) 'missing map rejected'
Assert-Api ((Invoke-TestRequest "/api/maps/$map/stages/0").Status -eq 400) 'invalid stage rejected'
Assert-Api ((Invoke-TestRequest '/api/players/search?q=x').Status -eq 400) 'short search rejected'
Assert-Api ((Invoke-TestRequest '/api/players/not-a-steamid').Status -eq 400) 'invalid SteamID rejected'
Assert-Api ((Invoke-TestRequest '/api/players/999/records?sort=invalid').Status -eq 400) 'invalid record sort rejected'
Assert-Api ((Invoke-TestRequest '/api/players/999').Status -eq 404) 'missing player rejected'
$websiteStatus = (Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd('/') + "/maps/$map")).StatusCode
Assert-Api ($websiteStatus -eq 200) 'shareable website fallback'

# These checks are read-only, including the deliberately out-of-range pages.
$farPage = Invoke-TestRequest "/api/maps/$map/leaderboard?page=2147483647&pageSize=100"
Assert-Api ($farPage.Status -eq 200 -and $farPage.Body.records.Count -eq 0 -and $farPage.Body.pagination.total -eq $leaderboard.Body.pagination.total) 'large page preserves leaderboard total without overflow'
$routes = Invoke-TestRequest "/api/maps/$map/routes"
Assert-Api ($routes.Status -eq 200 -and @($routes.Body.routes | Where-Object route -eq 'stage').Count -eq $maps.Body[0].stageCount) 'configured stage routes exist before completions'
$rankPage = Invoke-TestRequest '/api/rankings?page=2147483647&pageSize=100'
Assert-Api ($rankPage.Status -eq 200 -and $rankPage.Body.rankings.Count -eq 0 -and $rankPage.Body.pagination.total -eq $rankings.Body.pagination.total) 'large rankings page preserves total'
$times = Invoke-TestRequest "/api/maps/$map/leaderboard?pageSize=100"
$previous = $null
foreach ($record in $times.Body.records) {
    if ($null -ne $previous -and $record.timeUs -eq $previous.timeUs) { Assert-Api ($record.rank -eq $previous.rank) 'equal times share competition rank' }
    $previous = $record
}

if ($leaderboard.Body.records.Count -gt 0) {
    foreach ($path in @("/api/players/$steamId/records", "/api/players/$steamId/history")) {
        $first = Invoke-TestRequest ($path + '?page=1&pageSize=1')
        $far = Invoke-TestRequest ($path + '?page=2147483647&pageSize=100')
        Assert-Api ($far.Status -eq 200 -and $far.Body.pagination.total -eq $first.Body.pagination.total) "empty-page total for $path"
    }
    $rawTimes = (Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd('/') + "/api/maps/$map/leaderboard")).Content
    Assert-Api ($rawTimes -match '"achievedAt":"[^"]+Z"') 'record timestamps explicitly UTC'
}
$stageRoute = $routes.Body.routes | Where-Object route -eq 'stage' | Select-Object -First 1
if ($null -ne $stageRoute) {
    $first = Invoke-TestRequest "/api/maps/$map/stages/$($stageRoute.index)?page=1&pageSize=1"
    $far = Invoke-TestRequest "/api/maps/$map/stages/$($stageRoute.index)?page=2147483647&pageSize=100"
    Assert-Api ($far.Status -eq 200 -and $far.Body.pagination.total -eq $first.Body.pagination.total) 'empty stage page preserves total'
}

if ($IncludeRateLimit) {
    $limited = $false
    foreach ($request in 1..10050) {
        if ((Invoke-TestRequest '/api/version').Status -eq 429) { $limited = $true; break }
    }
    Assert-Api $limited 'per-client API rate limit'
    Write-Warning 'The rate-limit test intentionally exhausts this client allowance for up to one minute.'
}

Write-Host "API TESTS PASSED: $script:passed"

