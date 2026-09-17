param(
    [Parameter(Mandatory = $true)]
    [string]$VmName,

    [ValidateSet("claude", "dotnet", "opencode", "python")]
    [string[]]$Components = @("claude", "dotnet"),

    [ValidateSet("snap", "binary")]
    [string]$ClaudeInstallMode = "snap"
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

    # ldd-getrieben: nur vendoren, was auf der Ziel-VM wirklich fehlt.
    # apt-get download braucht kein root → Ziel-VM lädt ihre debs selbst,
    # kein Umweg über Build-VM/Host.
    $missing = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'ldd /snap/dotnet-sdk-100/current/usr/bin/dotnet 2>/dev/null | grep "not found" | sort -u || echo NONE'
    if ($missing -notmatch "NONE") {
        Write-Host "ldd meldet fehlende libs:"
        Write-Host $missing
        Invoke-VmExec 'cd ~ && rm -rf vm-libs && mkdir vm-libs && cd vm-libs && for p in libunwind8 libicu78 libbrotli1 libgssapi-krb5-2 libssl3t64 zlib1g; do apt-get download $p 2>/dev/null || echo "SKIP $p"; done && for d in *.deb; do dpkg -x $d ex-$d; done && cp ex-*.deb/usr/lib/x86_64-linux-gnu/*.so.* ~/.local/lib/ 2>/dev/null; cd ~ && rm -rf vm-libs'
        Invoke-VmExec 'LD_LIBRARY_PATH=$HOME/.local/lib ldd /snap/dotnet-sdk-100/current/usr/bin/dotnet | grep "not found" || echo "OK: keine fehlenden libs mehr"'
    }
    else {
        Write-Host "ldd: alle dotnet-Libs vorhanden, kein Vendoring noetig."
    }

    Invoke-VmExec 'grep -q "^export DOTNET_ROOT" ~/.profile 2>/dev/null || echo "export DOTNET_ROOT=/snap/dotnet-sdk-100/current/usr/lib/dotnet" >> ~/.profile'
    Invoke-VmExec 'grep -q "^export LD_LIBRARY_PATH" ~/.profile 2>/dev/null || echo "export LD_LIBRARY_PATH=\$HOME/.local/lib:\$LD_LIBRARY_PATH" >> ~/.profile'
    Invoke-VmExec 'bash -lc "dotnet --version"'
}

if ($Components -contains "claude") {
    $existing = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'bash -lc "claude --version" 2>&1'

    if ($ClaudeInstallMode -eq "snap") {
        Write-Host "== Komponente: Claude Code (Store-Snap 'claude-code', Default) =="
        $existingSnap = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'snap list claude-code >/dev/null 2>&1; echo $?'
        if ($existingSnap -match "0") {
            Write-Host "Snap claude-code bereits installiert ($existing)."
        }
        else {
            Invoke-VmExec 'sudo snap install claude-code 2>&1 | tail -1'
            Invoke-VmExec 'snap list claude-code | tail -1'
            Invoke-VmExec 'bash -lc "claude --version"'
        }
    }
    else {
        Write-Host "== Komponente: Claude Code (offizielles Native-Binary, Checksum-geprueft) =="
        $base = "https://downloads.claude.ai/claude-code-releases"
        $version = (Invoke-WebRequest -Uri "$base/latest" -UseBasicParsing).Content.Trim()
        $manifest = (Invoke-WebRequest -Uri "$base/$version/manifest.json" -UseBasicParsing).Content | ConvertFrom-Json
        $expected = $manifest.platforms.'linux-x64'.checksum
        $binaryPath = Join-Path $tempDir "claude-linux-x64"

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
}

if ($Components -contains "opencode") {
    Write-Host "== Komponente: opencode (Store-Snap, Publisher Ubuntu verified) =="
    $existingSnap = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'snap list opencode >/dev/null 2>&1; echo $?'
    if ($existingSnap -match "0") {
        Write-Host "Snap opencode bereits installiert."
    }
    else {
        Invoke-VmExec 'sudo snap install opencode --classic 2>&1 | tail -1'
        Invoke-VmExec 'snap list opencode | tail -1'
        Invoke-VmExec 'bash -lc "opencode --version"'
    }
}

if ($Components -contains "python") {
    Write-Host "== Komponente: Python 3.14 (Snap python3-alt, Befehl python3-alt.3-14) =="
    $existingSnap = & dotnet run --project $cliProject -- vm-exec --name $VmName --command 'snap list python3-alt >/dev/null 2>&1; echo $?'
    if ($existingSnap -match "0") {
        Write-Host "Snap python3-alt bereits installiert."
    }
    else {
        Invoke-VmExec 'sudo snap install python3-alt 2>&1 | tail -1'
        Invoke-VmExec 'snap list python3-alt | tail -1'
    }
    Invoke-VmExec 'mkdir -p ~/.local/bin && ln -sf /snap/bin/python3-alt.3-14 ~/.local/bin/python && ln -sf /snap/bin/python3-alt.pip3-14 ~/.local/bin/pip'
    Invoke-VmExec 'bash -lc "python3-alt.3-14 --version && python3-alt.pip3-14 --version"'
}

Write-Host "Bootstrap fuer '$VmName' abgeschlossen: $($Components -join ', ')"
