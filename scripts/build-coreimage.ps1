param(
    [string]$BuildVmName = "cbx-t11-build2",
    [string]$BuildImage = "26.04",
    [string]$BuildDisk = "20G",
    [string]$ModelUrl = "https://raw.githubusercontent.com/canonical/models/main/ubuntu-classic-2610-amd64-dangerous.model",
    [string]$Destination = "$env:LOCALAPPDATA\CoreBridge\images\cbx-classic-2610.img"
)

$ErrorActionPreference = "Stop"
Write-Host "Hinweis: 26.04 ist nur das Build-VM-Image; das gebaute Zielmodell ist Classic Ubuntu 26.10 Edge."
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Cli {
    param([string[]]$CliArgs)
    & dotnet run --project (Join-Path $repoRoot "src/CoreBridge.Cli") -- @CliArgs
    if ($LASTEXITCODE -ne 0) {
        throw "CLI-Aufruf fehlgeschlagen (exit $LASTEXITCODE): $($CliArgs -join ' ')"
    }
}

$names = & dotnet run --project (Join-Path $repoRoot "src/CoreBridge.Cli") -- vm-list
$vmExists = $names -match "^$BuildVmName\s"
if (-not $vmExists) {
    Invoke-Cli @("vm-create", "--name", $BuildVmName, "--image", $BuildImage, "--disk", $BuildDisk)
}

Invoke-Cli @("vm-exec", "--name", $BuildVmName, "--timeout", "600",
    "--command", "sudo snap install ubuntu-image --classic 2>&1 | tail -1")

Invoke-Cli @("vm-exec", "--name", $BuildVmName, "--timeout", "300",
    "--command", "wget -q -O /home/ubuntu/model.model '$ModelUrl' && head -2 /home/ubuntu/model.model")

Invoke-Cli @("vm-exec", "--name", $BuildVmName, "--timeout", "1800",
    "--command", "rm -rf /home/ubuntu/imgout && sudo ubuntu-image snap /home/ubuntu/model.model -O /home/ubuntu/imgout 2>&1 | tail -2 && ls -lh /home/ubuntu/imgout/ | head -3")

Invoke-Cli @("vm-get", "--name", $BuildVmName, "--remote", "/home/ubuntu/imgout/pc.img", "--destination", $Destination)

$megaBytes = [math]::Round((Get-Item $Destination).Length / 1MB)
Write-Host "Image fertig: $Destination ($megaBytes MB)"
Write-Host "Hinweis: Import via `multipass launch <url>` — unter Windows NICHT via file:// (multipass 1.16.3 Bug), sondern https:// (siehe EXPERIMENT_LOG T11)."
# Legacy compatibility script; see AGENT.md for its removal/integration decision.