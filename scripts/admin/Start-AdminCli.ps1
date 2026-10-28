param(
    [Alias('Url', 'GatewayUrl')][string]$BaseUri = $(if ($env:THDARKORBIT_GATEWAY_URL) { $env:THDARKORBIT_GATEWAY_URL } else { 'https://localhost:60952' }),
    [string]$Username = 'owner',
    [string]$Password = 'owner',
    [string]$AiQuery = '',
    [switch]$AutoExecute
)

$ErrorActionPreference = 'Stop'
$env:NODE_TLS_REJECT_UNAUTHORIZED = '0'

# The Node entry point owns the interactive command catalog and Tab completion,
# including /hangar, /skylab, /gate, and /clan progression routes.
$cliMjs = Join-Path $PSScriptRoot 'admin-cli.mjs'
if (-not (Test-Path $cliMjs)) {
    throw "Could not find $cliMjs"
}

$nodeArgs = @($cliMjs, '--url', $BaseUri, '--user', $Username, '--pass', $Password)
if (-not [string]::IsNullOrWhiteSpace($AiQuery)) {
    $nodeArgs += @('--ai', $AiQuery)
}
if ($AutoExecute) {
    $nodeArgs += '--auto-execute'
}

& node @nodeArgs
exit $LASTEXITCODE
