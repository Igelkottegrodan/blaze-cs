# The Windows half of task check:windows (scripts/check-windows.sh), run in the Windows copy of the repo:
# check-windows.ps1 <dotnet directory> <sdk version>...
param([string]$Dotnet)

# This SDK first on PATH: for blaze-cs.cmd, and for the dotnet the daemon runs.
$env:PATH = "$Dotnet;$env:PATH"
$env:DOTNET_ROOT = $Dotnet
Set-Location (Split-Path $PSScriptRoot)

dotnet build BlazeCs.slnx -nologo -v:q
if ($LASTEXITCODE) { exit 1 }
$status = 0
foreach ($sdk in $args) {
    "== Windows, SDK $sdk"
    $env:BLAZE_CS_SDK = $sdk
    dotnet run tests\fixture\check.cs
    if ($LASTEXITCODE) { $status = 1 }
}
exit $status
