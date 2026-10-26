param(
    [Parameter(Mandatory = $false, Position = 0)][string]$CommandText,
    [Alias('GatewayUrl', 'Url')][string]$BaseUri = 'http://localhost:60953',
    [switch]$Interactive,
    [string]$AiQuery,
    [string]$Username = 'owner',
    [string]$Password = 'owner'
)

$ErrorActionPreference = 'Stop'

# Launch interactive CLI when requested or when invoked with no CommandText/AiQuery.
if ($Interactive -or ([string]::IsNullOrWhiteSpace($CommandText) -and [string]::IsNullOrWhiteSpace($AiQuery))) {
    $interactiveScript = Join-Path $PSScriptRoot 'Start-AdminCli.ps1'
    if (Test-Path $interactiveScript) {
        & $interactiveScript -GatewayUrl $BaseUri -Username $Username -Password $Password
        exit $LASTEXITCODE
    }
    throw 'Set -CommandText or run .\scripts\admin\Start-AdminCli.ps1 for interactive mode.'
}

if (-not [string]::IsNullOrWhiteSpace($AiQuery)) {
    $cliMjs = Join-Path $PSScriptRoot 'admin-cli.mjs'
    & node $cliMjs --url $BaseUri --ai $AiQuery
    exit $LASTEXITCODE
}

function Enable-LocalhostTlsTrust([string]$TargetUri) {
    if ($TargetUri -notmatch '^https://(localhost|127\.0\.0\.1|\[::1\])(?::\d+)?(/|$)') {
        return
    }
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
    } catch {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    }
    if (-not ('LocalhostCertTrust' -as [type])) {
        Add-Type @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class LocalhostCertTrust {
    public static void Enable() {
        ServicePointManager.ServerCertificateValidationCallback =
            delegate(object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; };
    }
}
"@
    }
    [LocalhostCertTrust]::Enable()
}

function Resolve-VariableAlias([string]$Key) {
    if ([string]::IsNullOrWhiteSpace($Key)) { return $Key }
    $k = $Key.Trim().ToLowerInvariant()
    $aliases = @{
        'world.player.base_speed'          = 'world.ship.pusat_plus_base_speed'
        'player.base_speed'                = 'world.ship.pusat_plus_base_speed'
        'player.speed'                     = 'world.ship.pusat_plus_base_speed'
        'ship.base_speed'                  = 'world.ship.pusat_plus_base_speed'
        'pusat.speed'                      = 'world.ship.pusat_plus_base_speed'
        'world.ship.base_speed'            = 'world.ship.pusat_plus_base_speed'
        'world.player.phoenix_speed'       = 'world.ship.phoenix_base_speed'
        'phoenix.speed'                    = 'world.ship.phoenix_base_speed'
        'world.player.speed_multiplier'    = 'world.movement.speed_multiplier'
        'player.speed_multiplier'          = 'world.movement.speed_multiplier'
        'speed_multiplier'                 = 'world.movement.speed_multiplier'
        'world.speed_multiplier'           = 'world.movement.speed_multiplier'
        'laser_damage'                     = 'world.combat.laser_damage_multiplier'
        'world.laser_damage'               = 'world.combat.laser_damage_multiplier'
        'combat.laser_damage'              = 'world.combat.laser_damage_multiplier'
        'world.combat.laser_damage'        = 'world.combat.laser_damage_multiplier'
        'fire_range'                       = 'world.combat.fire_range'
        'combat.fire_range'                = 'world.combat.fire_range'
        'shield_absorption'                = 'world.combat.shield_absorption_ratio'
        'tick_rate'                        = 'world.tick_rate_ms'
        'world.tick_rate'                  = 'world.tick_rate_ms'
        'streuner_count'                   = 'world.spawn.streuner_count'
        'world.npc.streuner_count'         = 'world.spawn.streuner_count'
        'lordakia_count'                   = 'world.spawn.lordakia_count'
        'world.npc.lordakia_count'         = 'world.spawn.lordakia_count'
        'saimon_count'                     = 'world.spawn.saimon_count'
        'world.npc.saimon_count'           = 'world.spawn.saimon_count'
        'bonus_box_count'                  = 'world.spawn.bonus_box_count'
        'world.box.bonus_box_count'        = 'world.spawn.bonus_box_count'
    }
    if ($aliases.ContainsKey($k)) { return $aliases[$k] }
    return $Key
}

function Resolve-MapAlias([string]$MapToken) {
    $mapNames = @{
        '1-1' = '1'; '1-2' = '2'; '1-3' = '3'; '1-4' = '4'
        '2-1' = '5'; '2-2' = '6'; '2-3' = '7'; '2-4' = '8'
        '3-1' = '9'; '3-2' = '10'; '3-3' = '11'; '3-4' = '12'
        '4-1' = '13'; '4-2' = '14'; '4-3' = '15'; '4-4' = '16'
    }
    if ($null -ne $MapToken -and $mapNames.ContainsKey($MapToken.Trim())) {
        return $mapNames[$MapToken.Trim()]
    }
    return $MapToken
}

function Normalize-AdminCommandText([string]$RawText) {
    if ([string]::IsNullOrWhiteSpace($RawText)) { return $RawText }
    $trimmed = $RawText.Trim()
    # Leave non-slash commands untouched unless they use a known variable alias in `var get/set/reset/history`.
    if (-not $trimmed.StartsWith('/')) {
        $parts = $trimmed -split '\s+', 4
        if ($parts.Count -ge 3 -and $parts[0] -ieq 'var' -and $parts[1] -imatch '^(get|set|reset|history)$') {
            $resolvedKey = Resolve-VariableAlias $parts[2]
            if ($resolvedKey -ne $parts[2]) {
                $parts[2] = $resolvedKey
                return ($parts -join ' ')
            }
        }
        return $RawText
    }

    $withoutSlash = $trimmed.Substring(1).Trim()
    if ([string]::IsNullOrWhiteSpace($withoutSlash)) { return 'help' }
    $tokens = $withoutSlash -split '\s+'
    $verb = $tokens[0].ToLowerInvariant()
    $rest = if ($tokens.Count -gt 1) { $tokens[1..($tokens.Count - 1)] } else { @() }

    switch ($verb) {
        'help' { return 'help' }
        'getvar' {
            if ($rest.Count -ge 1) { return "var get $(Resolve-VariableAlias $rest[0])" }
            return 'var list'
        }
        'setvar' {
            if ($rest.Count -ge 2) {
                $key = Resolve-VariableAlias $rest[0]
                $tail = ($rest[1..($rest.Count - 1)] -join ' ')
                return "var set $key $tail"
            }
            return "var set $($rest -join ' ')"
        }
        'resetvar' {
            if ($rest.Count -ge 1) {
                $key = Resolve-VariableAlias $rest[0]
                $tail = if ($rest.Count -gt 1) { ' ' + ($rest[1..($rest.Count - 1)] -join ' ') } else { '' }
                return "var reset $key$tail"
            }
            return 'var reset'
        }
        { $_ -in @('vars', 'listvars', 'varlist') } {
            if ($rest.Count -ge 1) { return "var list $($rest -join ' ')" }
            return 'var list'
        }
        'varhistory' {
            if ($rest.Count -ge 1) {
                $first = Resolve-VariableAlias $rest[0]
                $tail = if ($rest.Count -gt 1) { ' ' + ($rest[1..($rest.Count - 1)] -join ' ') } else { '' }
                return "var history $first$tail"
            }
            return 'var history'
        }
        'rollbackvar' {
            return "var rollback $($rest -join ' ')".TrimEnd()
        }
        'var' {
            if ($rest.Count -ge 2 -and $rest[0] -imatch '^(get|set|reset|history)$') {
                $rest[1] = Resolve-VariableAlias $rest[1]
            }
            return "var $($rest -join ' ')".TrimEnd()
        }
        'tp' {
            if ($rest.Count -eq 3) {
                return "world tp admin $(Resolve-MapAlias $rest[0]) $($rest[1]) $($rest[2])"
            }
            if ($rest.Count -ge 4) {
                return "world tp $($rest[0]) $(Resolve-MapAlias $rest[1]) $($rest[2]) $($rest[3])"
            }
            return "world tp $($rest -join ' ')".TrimEnd()
        }
        'heal' {
            if ($rest.Count -eq 0) { return 'world heal admin' }
            return "world heal $($rest[0])"
        }
        'spawn' {
            if ($rest.Count -eq 1) { return "world spawn $($rest[0]) 1 2000 1300" }
            if ($rest.Count -eq 2) { return "world spawn $($rest[0]) $(Resolve-MapAlias $rest[1]) 2000 1300" }
            if ($rest.Count -ge 4) { return "world spawn $($rest[0]) $(Resolve-MapAlias $rest[1]) $($rest[2]) $($rest[3])" }
            return "world spawn $($rest -join ' ')".TrimEnd()
        }
        'grant' {
            $assets = @('credits', 'uridium', 'xp', 'honor')
            if ($rest.Count -eq 2 -and ($rest[0].ToLowerInvariant() -in $assets)) {
                return "world grant admin $($rest[0]) $($rest[1])"
            }
            if ($rest.Count -eq 3 -and ($rest[0].ToLowerInvariant() -in $assets)) {
                return "world grant $($rest[2]) $($rest[0]) $($rest[1])"
            }
            return "world grant $($rest -join ' ')".TrimEnd()
        }
        'announce' {
            return "world announce $($rest -join ' ')".TrimEnd()
        }
        'status' {
            if ($rest.Count -ge 1) { return "world status $(Resolve-MapAlias $rest[0])" }
            return 'world status'
        }
        'mesh' {
            if ($rest.Count -eq 0) { return 'mesh status' }
            return "mesh $($rest -join ' ')"
        }
        default {
            return $withoutSlash
        }
    }
}

function Acquire-DevAdminToken([string]$TargetBaseUri, [string]$LoginUser, [string]$LoginPass) {
    $candidates = @(
        @{ u = $LoginUser; p = $LoginPass },
        @{ u = 'owner'; p = 'owner' },
        @{ u = 'admin'; p = 'Admin123!' }
    )
    $loginUri = $TargetBaseUri.TrimEnd('/') + '/api/v1/identity/login'
    foreach ($cred in $candidates) {
        try {
            $loginBody = @{ usernameOrEmail = $cred.u; password = $cred.p } | ConvertTo-Json -Compress
            $loginResp = Invoke-RestMethod -Uri $loginUri -Method Post -ContentType 'application/json' -Body $loginBody -TimeoutSec 4
            if ($loginResp -and $loginResp.accessToken) {
                $env:THDARKORBIT_ADMIN_TOKEN = $loginResp.accessToken
                return $loginResp.accessToken
            }
        } catch { }
    }
    # Fallback to direct local Keycloak token endpoint in dev mode
    $kcUri = 'http://localhost:8081/realms/thdarkorbit/protocol/openid-connect/token'
    foreach ($cred in $candidates) {
        try {
            $form = @{
                grant_type = 'password'
                client_id  = 'thdarkorbit-cms'
                username   = $cred.u
                password   = $cred.p
            }
            $kcResp = Invoke-RestMethod -Uri $kcUri -Method Post -ContentType 'application/x-www-form-urlencoded' -Body $form -TimeoutSec 4
            if ($kcResp -and $kcResp.access_token) {
                $env:THDARKORBIT_ADMIN_TOKEN = $kcResp.access_token
                return $kcResp.access_token
            }
        } catch { }
    }
    return $null
}

Enable-LocalhostTlsTrust $BaseUri

$token = [Environment]::GetEnvironmentVariable('THDARKORBIT_ADMIN_TOKEN', 'Process')
if ([string]::IsNullOrWhiteSpace($token)) {
    $token = [Environment]::GetEnvironmentVariable('THDARKORBIT_ADMIN_TOKEN', 'User')
}
if ([string]::IsNullOrWhiteSpace($token)) {
    $token = Acquire-DevAdminToken -TargetBaseUri $BaseUri -LoginUser $Username -LoginPass $Password
}
if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'Set THDARKORBIT_ADMIN_TOKEN in this process to an OpsWrite bearer token.'
}

$effectiveCommand = Normalize-AdminCommandText $CommandText
$uri = $BaseUri.TrimEnd('/') + '/api/v1/backoffice/admin/command'
$body = @{ text = $effectiveCommand; surface = 'ServerManagement' } | ConvertTo-Json -Compress
try {
    $response = Invoke-RestMethod -Uri $uri -Method Post -ContentType 'application/json' `
        -Headers @{ Authorization = 'Bearer ' + $token } -Body $body
}
catch {
    # Surface the server's {"error": "..."} body (e.g. a 409 rollback conflict) instead of only the status line.
    $detail = $_.ErrorDetails.Message
    if ([string]::IsNullOrWhiteSpace($detail) -and $_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        try { $detail = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    try { $detail = ($detail | ConvertFrom-Json).error } catch { }
    [Console]::Error.WriteLine("Admin command failed (HTTP $status): $detail")
    exit 1
}
$response | ConvertTo-Json -Depth 20