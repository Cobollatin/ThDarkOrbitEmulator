# Shared helpers for Start-CaptureSession.ps1 / Stop-CaptureSession.ps1. Dot-source only.
Set-StrictMode -Version 2
if (-not (Test-Path variable:script:LogFile)) { $script:LogFile = $null }

$script:RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:SessionsRoot = Join-Path $RepoRoot '_reversing\Requests\sessions'
$script:StateFile    = Join-Path $SessionsRoot '.active-session.json'
$script:VenvPython   = Join-Path $RepoRoot '.venv\Scripts\python.exe'
$script:Mitmdump     = Join-Path $RepoRoot '.venv\Scripts\mitmdump.exe'
$script:InetKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$script:VuplexProcessNames = @('Vuplex WebView.vuplex', 'Vuplex WebView.real.vuplex', 'Vuplex WebView', 'Vuplex WebView.real')

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-ddTHH:mm:ss.fffzzz'), $Level, $Message
    if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
    [Console]::Error.WriteLine($line)   # stderr keeps stdout clean for the JSON summary
}

function Save-State([hashtable]$State) {
    New-Item -ItemType Directory -Force -Path $SessionsRoot | Out-Null
    ($State | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $StateFile -Encoding UTF8
}

function Read-State {
    if (Test-Path -LiteralPath $StateFile) { return Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json }
    return $null
}

function Test-MitmCaInstalled {
    $cer = Join-Path $env:USERPROFILE '.mitmproxy\mitmproxy-ca-cert.cer'
    if (-not (Test-Path $cer)) { return $false }
    $thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
    # .NET store API instead of the Cert: drive: the drive is missing when powershell.exe inherits a pwsh PSModulePath.
    foreach ($loc in 'CurrentUser', 'LocalMachine') {
        $st = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root', $loc
        try { $st.Open('ReadOnly'); if ($st.Certificates | Where-Object Thumbprint -eq $thumb) { return $true } } catch {} finally { $st.Close() }
    }
    return $false
}

# ---- system (WinINet) proxy: only used with -ProxyMethod System ----
function Get-SystemProxy {
    $p = Get-ItemProperty -Path $InetKey
    $get = { param($n) if ($p.PSObject.Properties[$n]) { $p.$n } else { $null } }
    return [ordered]@{
        ProxyEnable   = & $get 'ProxyEnable'
        ProxyServer   = & $get 'ProxyServer'
        ProxyOverride = & $get 'ProxyOverride'
        AutoConfigURL = & $get 'AutoConfigURL'
    }
}

function Invoke-InetRefresh {
    if (-not ('CaptureWinInet' -as [type])) {
        Add-Type -Namespace '' -Name CaptureWinInet -MemberDefinition '[DllImport("wininet.dll", SetLastError=true)] public static extern bool InternetSetOption(System.IntPtr h, int o, System.IntPtr b, int l);'
    }
    [void][CaptureWinInet]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)  # SETTINGS_CHANGED
    [void][CaptureWinInet]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)  # REFRESH
}

function Set-SystemProxy([string]$Server) {
    Set-ItemProperty -Path $InetKey -Name ProxyEnable -Value 1 -Type DWord
    Set-ItemProperty -Path $InetKey -Name ProxyServer -Value $Server
    Set-ItemProperty -Path $InetKey -Name ProxyOverride -Value '<local>'
    Remove-ItemProperty -Path $InetKey -Name AutoConfigURL -ErrorAction SilentlyContinue
    Invoke-InetRefresh
}

function Restore-SystemProxy($Backup) {
    if ($null -eq $Backup) { return }
    foreach ($n in 'ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL') {
        $v = $Backup.$n
        if ($null -eq $v) { Remove-ItemProperty -Path $InetKey -Name $n -ErrorAction SilentlyContinue }
        elseif ($n -eq 'ProxyEnable') { Set-ItemProperty -Path $InetKey -Name $n -Value ([int]$v) -Type DWord }
        else { Set-ItemProperty -Path $InetKey -Name $n -Value ([string]$v) }
    }
    Invoke-InetRefresh
}

# ---- Vuplex CEF remote-debugging shim (only with -AutoLogin / -EnableCdp) ----
function Get-VuplexPaths([string]$ClientDir) {
    $d = Join-Path $ClientDir 'DarkOrbit_Data\Plugins\x86\VuplexWebViewChromium'
    return @{ Dir = $d; Host = Join-Path $d 'Vuplex WebView.vuplex'; Real = Join-Path $d 'Vuplex WebView.real.vuplex' }
}

function Install-VuplexCdpShim([string]$ClientDir) {
    $v = Get-VuplexPaths $ClientDir
    if (Test-Path -LiteralPath $v.Real) { Write-Log "CDP shim already installed ($($v.Real) exists)"; return }
    $shimSrc = Join-Path $PSScriptRoot 'cdp-shim\VuplexCdpShim.cs'
    $shimExe = Join-Path $SessionsRoot '.bin\VuplexCdpShim.exe'
    if (-not (Test-Path $shimExe) -or (Get-Item $shimSrc).LastWriteTime -gt (Get-Item $shimExe).LastWriteTime) {
        New-Item -ItemType Directory -Force -Path (Split-Path $shimExe) | Out-Null
        Add-Type -TypeDefinition (Get-Content -LiteralPath $shimSrc -Raw) -OutputAssembly $shimExe -OutputType ConsoleApplication
    }
    Move-Item -LiteralPath $v.Host -Destination $v.Real
    try {
        Copy-Item -LiteralPath $shimExe -Destination $v.Host
        # CEF locates the .pak/.dat files next to the executable; the real host keeps its folder.
    } catch { Move-Item -LiteralPath $v.Real -Destination $v.Host -Force; throw }
    Write-Log "Installed Vuplex CDP shim (original kept as $($v.Real))"
}

function Uninstall-VuplexCdpShim([string]$ClientDir) {
    $v = Get-VuplexPaths $ClientDir
    if (-not (Test-Path -LiteralPath $v.Real)) { return }
    for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $v.Host); $i++) {
        Get-Process -Name $VuplexProcessNames -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        Remove-Item -LiteralPath $v.Host -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $v.Host) { throw "Could not remove CDP shim at $($v.Host) (still in use)" }
    Move-Item -LiteralPath $v.Real -Destination $v.Host
    Write-Log "Restored original Vuplex WebView host"
}

function Stop-Mitmdump($MitmPid, [string]$StopFile, [int]$TimeoutSec = 15) {
    if (-not $MitmPid) { return }
    $p = Get-Process -Id $MitmPid -ErrorAction SilentlyContinue
    if (-not $p) { return }
    if ($StopFile) { New-Item -ItemType File -Force -Path $StopFile | Out-Null }
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        Write-Log "mitmdump did not exit within ${TimeoutSec}s, killing process tree" 'WARN'
        Stop-ProcessTree $MitmPid
    } else { Write-Log "mitmdump exited cleanly" }
}

# Kill a process and all of its descendants (taskkill /T semantics, works when the parent already exited).
function Stop-ProcessTree([int]$ProcessId) {
    if (-not $ProcessId) { return }
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction SilentlyContinue
    $kids = @($all | Where-Object { $_.ParentProcessId -eq $ProcessId -and $_.ProcessId -ne $ProcessId })
    foreach ($k in $kids) { Stop-ProcessTree ([int]$k.ProcessId) }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

# Run an executable with a hard timeout; stdout/stderr go to files. On timeout the whole tree is killed.
# Returns @{ exitCode; timedOut; stdout; stderr }. Extra environment comes from the caller's $env: (inherited).
function Invoke-WithTimeout([string]$FilePath, [string[]]$ArgumentList, [int]$TimeoutSec, [string]$OutFile, [string]$WorkingDirectory = $RepoRoot) {
    $errFile = "$OutFile.err"
    $quoted = $ArgumentList | ForEach-Object { if ($_ -match '[\s"]' ) { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $p = Start-Process -FilePath $FilePath -ArgumentList $quoted -WorkingDirectory $WorkingDirectory -WindowStyle Hidden `
        -RedirectStandardOutput $OutFile -RedirectStandardError $errFile -PassThru
    $null = $p.Handle   # cache the handle so ExitCode is available after exit
    $timedOut = -not $p.WaitForExit($TimeoutSec * 1000)
    if ($timedOut) { Write-Log "$([IO.Path]::GetFileName($FilePath)) exceeded ${TimeoutSec}s; killing process tree $($p.Id)" 'WARN'; Stop-ProcessTree $p.Id; [void]$p.WaitForExit(5000) }
    $p.WaitForExit()
    $code = if ($timedOut) { 124 } else { $p.ExitCode }
    [pscustomobject]@{ exitCode = $code; timedOut = $timedOut
        stdout = @(Get-Content -LiteralPath $OutFile -ErrorAction SilentlyContinue); stderr = @(Get-Content -LiteralPath $errFile -ErrorAction SilentlyContinue) }
}

# Windows Credential Manager (generic credential) -> PSCredential, never logged.
function Get-StoredCredential([string]$Target) {
    if (-not ('CaptureCredMan' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class CaptureCredMan {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  struct CREDENTIAL { public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public int CredentialBlobSize;
    public IntPtr CredentialBlob; public int Persist; public int AttributeCount; public IntPtr Attributes;
    public string TargetAlias; public string UserName; }
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CredRead(string t, int type, int f, out IntPtr c);
  [DllImport("advapi32.dll")] static extern void CredFree(IntPtr c);
  public static string[] Read(string target) {
    IntPtr p; if (!CredRead(target, 1, 0, out p)) return null;
    try { var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
      return new[] { c.UserName, Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) }; }
    finally { CredFree(p); }
  }
}
'@
    }
    return [CaptureCredMan]::Read($Target)
}

# Keep the Unity window restored and on a fixed rectangle: Unity pauses rendering when minimized, which stalls the Vuplex UI.
function Set-ClientWindow([string]$Bounds, [string]$ProcessName = 'DarkOrbit') {
    if (-not $Bounds) { return $false }
    if (-not ('CaptureWin32' -as [type])) {
        Add-Type -Namespace '' -Name CaptureWin32 -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool MoveWindow(System.IntPtr h, int x, int y, int w, int hh, bool r);
[DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
public struct RECT { public int Left, Top, Right, Bottom; }
"@
    }
    $b = $Bounds -split ',' | ForEach-Object { [int]$_.Trim() }
    $p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if (-not $p) { return $false }
    $h = $p.MainWindowHandle
    $r = New-Object CaptureWin32+RECT; [void][CaptureWin32]::GetWindowRect($h, [ref]$r)
    $iconic = [CaptureWin32]::IsIconic($h)
    if ($iconic -or $r.Left -ne $b[0] -or $r.Top -ne $b[1] -or ($r.Right - $r.Left) -ne $b[2] -or ($r.Bottom - $r.Top) -ne $b[3]) {
        [void][CaptureWin32]::ShowWindow($h, 9)   # SW_RESTORE
        [void][CaptureWin32]::MoveWindow($h, $b[0], $b[1], $b[2], $b[3], $true)
        if ($iconic) { Write-Log "Client window was minimized; restored to $Bounds" } else { Write-Log "Client window placed at $Bounds" }
    }
    return $true
}