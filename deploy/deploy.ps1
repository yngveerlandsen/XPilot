# Deploys the XPilot dedicated server and master server to a Linux machine.
#
#   .\deploy\deploy.ps1 -Server hjemmelaga.online
#   .\deploy\deploy.ps1 -Server 1.2.3.4 -SkipTests
#
# Built here as a self-contained linux-x64 release and copied over finished, so the server needs no .NET.
# A new release goes beside the old ones and the `current` link is switched last; if the new one doesn't
# come up, switching the link back restores the old one. The machine must have had server-setup.sh run once.

param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = 'root',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$remote = "$User@$Server"
$remoteRelease = "/opt/xpilot/releases/$release"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# This machine has more than one dotnet, and the first on PATH has no SDK, so look for one that does.
function Find-Dotnet {
    $candidates = @($env:DOTNET_ROOT, "$env:ProgramFiles\dotnet", "$env:LOCALAPPDATA\Microsoft\dotnet", "$env:USERPROFILE\.dotnet") |
        Where-Object { $_ } | ForEach-Object { Join-Path $_ 'dotnet.exe' }
    $candidates += (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    foreach ($d in $candidates) {
        if (-not $d -or -not (Test-Path $d)) { continue }
        if (& $d --list-sdks 2>$null | Where-Object { $_ -match '^\s*(1[0-9]|[2-9][0-9])\.' }) { return $d }
    }
    throw 'Found no .NET with SDK 10 or newer. Check "dotnet --list-sdks".'
}

# Windows' own tar and scp by full path: a GNU tar or scp earlier on PATH reads "C:\..." as a remote host.
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$scp = Join-Path $env:SystemRoot 'System32\OpenSSH\scp.exe'
foreach ($tool in $tar, $scp) { if (-not (Test-Path $tool)) { throw "Missing $tool" } }
$sshOptions = @('-o', 'ConnectTimeout=15')

function Invoke-Remote($command) {
    ssh @sshOptions $remote $command
    if ($LASTEXITCODE -ne 0) { throw "Failed on the server: $command" }
}

Step 'Checking tools and the connection'
$dotnet = Find-Dotnet
Write-Host "  dotnet: $dotnet"
Invoke-Remote 'test -f /etc/xpilot/server.env || { echo "Run deploy/server-setup.sh on the server first."; exit 1; }'

if (-not $SkipTests) {
    Step 'Running tests'
    & $dotnet test "$root\XPilot.slnx" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

Step 'Building the server for Linux'
$output = Join-Path $env:TEMP "xpilot-deploy-$release"
& $dotnet publish "$root\src\XPilot.Server" -c Release -r linux-x64 --self-contained true -p:DebugType=none -o $output
if ($LASTEXITCODE -ne 0) { throw 'Building the server failed' }

Step "Copying the release to $Server"
$archive = "$output.tgz"
& $tar -czf $archive -C $output .
if ($LASTEXITCODE -ne 0) { throw 'Packing failed' }
Invoke-Remote "mkdir -p $remoteRelease"
& $scp @sshOptions $archive "${remote}:/tmp/xpilot-$release.tgz"
if ($LASTEXITCODE -ne 0) { throw 'Copying failed' }
# The archive is removed whatever happens, so an interrupted deploy leaves nothing in /tmp.
Invoke-Remote "tar -xzf /tmp/xpilot-$release.tgz -C $remoteRelease; k=`$?; rm -f /tmp/xpilot-$release.tgz; exit `$k"
Invoke-Remote "chmod +x $remoteRelease/XPilot.Server && chown -R root:xpilot $remoteRelease && chmod -R g+rX,o-rwx $remoteRelease"
Remove-Item $archive, $output -Recurse -Force -ErrorAction SilentlyContinue

Step 'Switching to the new release'
# Empty on the first deploy; "$(...)" turns no output into an empty string rather than $null.
$previous = "$(ssh @sshOptions $remote 'readlink /opt/xpilot/current || true')".Trim()
Invoke-Remote "ln -sfn $remoteRelease /opt/xpilot/current"
Invoke-Remote 'systemctl restart xpilot-master xpilot-server'

Step 'Checking that both services stay up'
Start-Sleep -Seconds 4
$state = (ssh @sshOptions $remote 'systemctl is-active xpilot-server xpilot-master' | Out-String)
$listening = "$(ssh @sshOptions $remote "journalctl -u xpilot-server --since '-30s' --no-pager | grep -c 'listening on UDP' || true")".Trim()
if ($state -match 'failed|inactive|activating' -or $listening -eq '0') {
    Write-Host "`nA service did not come up:" -ForegroundColor Red
    ssh @sshOptions $remote 'journalctl -u xpilot-server -u xpilot-master -n 30 --no-pager'
    if ($previous) {
        Write-Host "`nGoing back to $previous" -ForegroundColor Yellow
        ssh @sshOptions $remote "ln -sfn $previous /opt/xpilot/current && systemctl restart xpilot-master xpilot-server"
    }
    throw 'Deploy failed'
}

Step 'Keeping the five newest releases'
Invoke-Remote 'cd /opt/xpilot/releases && ls -1t | tail -n +6 | xargs -r rm -rf'

Write-Host "`nDeployed $release" -ForegroundColor Green
Write-Host "Logs:     ssh $remote 'journalctl -u xpilot-server -f'"
Write-Host "Settings: ssh $remote 'nano /etc/xpilot/server.env && systemctl restart xpilot-server'"
Write-Host "Players join $Server, or set Settings > Network > Master server to $Server for the internet list."
