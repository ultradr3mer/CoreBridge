param(
    [Parameter(Mandatory = $true)]
    [string]$VmName,

    [ValidateSet("claude", "dotnet")]
    [string[]]$Components = @("claude", "dotnet"),

    [string]$BuildVmName = "cbx-t11-build2"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot "src/CoreBridge.Cli"
$tempDir = Join-Path $env:TEMP "opencode"

function Invoke-Cli {
    param([string[]]$CliArgs, [int]$TimeoutSeconds = 300)
    & dotnet run --project $cliProject -- @CliArgs
    if ($LASTEXITCODE -ne 0) {
        throw "CLI-Aufruf fehlgeschlagen (exit $LASTEXITCODE): $($CliArgs -join ' ')"
    }
}

function Invoke-VmExec {
    param([string]$Command, [int]$TimeoutSeconds = 300)
    Invoke-Cli @("vm-exec", "--name", $VmName, "--timeout", "$TimeoutSeconds", "--command", $Command)
}

if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
}

if ($Components -contains "dotnet") {
    Write-Host "== Komponente: .NET SDK 10 (classic snap + vendored libs) =="
    Invoke-VmExec 'snap list dotnet-sdk-100 >/dev/null 2>&1 || sudo snap install dotnet-sdk-100 2>&1 | tail -1'
    Invoke-VmExec 'mkdir -p ~/.local/bin ~/.local/lib && ln -sf /snap/dotnet-sdk-100/current/usr/bin/dotnet ~/.local/bin/dotnet'

    $needsLibs = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'ls ~/.local/lib/libicudata.so.78 >/dev/null 2>&1; echo $?'
    if (-not ($needsLibs -match "0")) {
        Write-Host "Vendore libunwind8 + libicu78 über die Build-VM ..."
        Invoke-Cli @("vm-exec", "--name", $BuildVmName, "--timeout", "300", "--command",
            'cd /home/ubuntu && rm -rf vm-libs && mkdir vm-libs && cd vm-libs && apt-get download libunwind8 libicu78 2>&1 | tail -1 && for d in *.deb; do dpkg -x $d ex-$d; done && mkdir -p ex/usr/lib/x86_64-linux-gnu && cp ex-*.deb/usr/lib/x86_64-linux-gnu/*.so.* ex/usr/lib/x86_64-linux-gnu/ 2>/dev/null; cd ex/usr/lib/x86_64-linux-gnu && tar -czf /home/ubuntu/vm-libs.tar.gz *.so* && ls -lh /home/ubuntu/vm-libs.tar.gz')
        Invoke-Cli @("vm-get", "--name", $BuildVmName, "--remote", "/home/ubuntu/vm-libs.tar.gz", "--destination", "$tempDir\vm-libs.tar.gz")
        Invoke-Cli @("vm-put", "--name", $VmName, "--source", "$tempDir\vm-libs.tar.gz", "--destination", "vm-libs.tar.gz")
        Invoke-VmExec 'tar -xzf ~/vm-libs.tar.gz -C ~/.local/lib && rm -f ~/vm-libs.tar.gz'
    }

    Invoke-VmExec 'grep -q "^export DOTNET_ROOT" ~/.profile 2>/dev/null || echo "export DOTNET_ROOT=/snap/dotnet-sdk-100/current/usr/lib/dotnet" >> ~/.profile'
    Invoke-VmExec 'grep -q "^export LD_LIBRARY_PATH" ~/.profile 2>/dev/null || echo "export LD_LIBRARY_PATH=\$HOME/.local/lib:\$LD_LIBRARY_PATH" >> ~/.profile'
    Invoke-VmExec 'bash -lc "dotnet --version"'
}

if ($Components -contains "claude") {
    Write-Host "== Komponente: Claude Code (Native-Binary) =="
    $base = "https://downloads.claude.ai/claude-code-releases"
    $version = (Invoke-WebRequest -Uri "$base/latest" -UseBasicParsing).Content.Trim()
    $manifest = (Invoke-WebRequest -Uri "$base/$version/manifest.json" -UseBasicParsing).Content | ConvertFrom-Json
    $expected = $manifest.platforms.'linux-x64'.checksum
    $binaryPath = Join-Path $tempDir "claude-linux-x64"

    $existing = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'bash -lc "claude --version" 2>&1'
    if ($existing -match [regex]::Escape($version)) {
        Write-Host "Claude Code $version bereits vorhanden."
    }
    else {
        if (-not (Test-Path $binaryPath) -or ((Get-FileHash $binaryPath -Algorithm SHA256).Hash.ToLower() -ne $expected)) {
            Invoke-WebRequest -Uri "$base/$version/linux-x64/claude" -OutFile $binaryPath -UseBasicParsing
            if ((Get-FileHash $binaryPath -Algorithm SHA256).Hash.ToLower() -ne $expected) {
                throw "SHA256-Pruefung des Claude-Binarys fehlgeschlagen."
            }
        }

        Invoke-VmExec 'mkdir -p ~/.local/bin'
        Invoke-Cli @("vm-put", "--name", $VmName, "--source", $binaryPath, "--destination", ".local/bin/claude")
        Invoke-VmExec 'chmod +x ~/.local/bin/claude && ~/.local/bin/claude install 2>&1 | tail -2'
        Invoke-VmExec 'bash -lc "claude --version"'
    }
}

Write-Host "Bootstrap fuer '$VmName' abgeschlossen: $($Components -join ', ')"
