[CmdletBinding()]
param([string]$BaseUrl='http://127.0.0.1:5087')
# Read-only checks for tools/storage-tests --seed-api on its isolated st_test_ schema.
$ErrorActionPreference='Stop'
foreach($route in @('/api/maps/surf_test/leaderboard','/api/maps/surf_test/stages/1')) {
    $a=Invoke-RestMethod ($BaseUrl+$route+'?page=1&pageSize=1')
    $b=Invoke-RestMethod ($BaseUrl+$route+'?page=2&pageSize=1')
    if($a.records[0].rank -ne 1 -or $b.records[0].rank -ne 1 -or $a.records[0].steamId -eq $b.records[0].steamId) { throw "Tie pagination failed: $route" }
    Write-Output "PASS: cross-page tied ranks $route"
}
$profile=Invoke-RestMethod ($BaseUrl+'/api/players/76561198000000003')
$points=Invoke-RestMethod ($BaseUrl+'/api/players/76561198000000003/points')
if($profile.steamId -ne '76561198000000003' -or $null -ne $points.ranking) { throw 'Unranked contract failed' }
Write-Output 'PASS: unranked profile and nullable Points contract'
$routes=Invoke-RestMethod ($BaseUrl+'/api/maps/surf_test/routes')
if(@($routes.routes | Where-Object { $_.route -eq 'stage' -and $_.index -eq 3 }).Count -ne 1 -or @($routes.routes | Where-Object { $_.route -eq 'bonus' -and $_.index -eq 2 }).Count -ne 1) { throw 'Configured empty routes missing' }
foreach($route in @('/api/maps/surf_test/stages/3','/api/maps/surf_test/leaderboard?route=bonus&index=2')) {
    $empty=Invoke-RestMethod ($BaseUrl+$route)
    if($empty.records.Count -ne 0 -or $empty.pagination.total -ne 0) { throw "Empty route contract failed: $route" }
}
Write-Output 'PASS: empty configured stage3/bonus2 selectable and empty'
$a=Invoke-RestMethod ($BaseUrl+'/api/rankings?page=1&pageSize=1')
$b=Invoke-RestMethod ($BaseUrl+'/api/rankings?page=2&pageSize=1')
if($a.rankings[0].rank -ne 1 -or $b.rankings[0].rank -ne 1) { throw 'Points tie rank failed' }
Write-Output 'PASS: Points tie rank across pages'
