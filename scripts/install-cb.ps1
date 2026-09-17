$ErrorActionPreference = 'Stop'

$functionSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cb.ps1') -Raw
$beginMarker = '# >>> CoreBridge cb >>>'
$endMarker = '# <<< CoreBridge cb <<<'
$block = $beginMarker + [Environment]::NewLine + $functionSource.TrimEnd() + [Environment]::NewLine + $endMarker

$profiles = @(
    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PowerShell\Microsoft.PowerShell_profile.ps1'),
    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'WindowsPowerShell\Microsoft.PowerShell_profile.ps1')
)

foreach ($profilePath in $profiles) {
    $directory = Split-Path -Parent $profilePath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $existing = ''
    if (Test-Path -LiteralPath $profilePath) {
        $existing = Get-Content -LiteralPath $profilePath -Raw
    }

    $pattern = [regex]::Escape($beginMarker) + '[\s\S]*?' + [regex]::Escape($endMarker)
    if ($existing -match $pattern) {
        $replacement = $block.Replace('$', '$$')
        $updated = [regex]::Replace($existing, $pattern, $replacement)
    }
    else {
        $updated = $existing.TrimEnd() + [Environment]::NewLine + [Environment]::NewLine + $block + [Environment]::NewLine
    }

    Set-Content -LiteralPath $profilePath -Value $updated -NoNewline
    Write-Host "cb-Funktion installiert: $profilePath"
}
