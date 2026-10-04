function cb {
    # No param(): preserves short flags such as -i, -n and -c.
    $cliArgs = @(
        foreach ($arg in $args) {
            if ($arg -is [array]) { $arg -join ',' }
            else { $arg }
        }
    )

    # Find a CoreBridge checkout by searching upward.
    $root = (Get-Location).ProviderPath
    while ($root -and -not (Test-Path -LiteralPath (Join-Path $root 'CoreBridge.slnx'))) {
        $parent = Split-Path -Parent $root

        # Stop at filesystem root.
        if ($parent -eq $root) {
            $root = $null
            break
        }

        $root = $parent
    }

    $installedExe = Join-Path $env:LOCALAPPDATA 'ClaraTools\CoreBridge\CoreBridge.exe'

    # Outside a CoreBridge checkout: run the installed application.
    if (-not $root) {
        if (-not (Test-Path -LiteralPath $installedExe)) {
            Write-Error "CoreBridge is not installed: $installedExe"
            return
        }

        & $installedExe @cliArgs
        return
    }

    # Inside a CoreBridge checkout/worktree: run the development projects.
    if ($args.Count -eq 0) {
        $inRepo = (git -C $root rev-parse --is-inside-work-tree 2>$null) -eq 'true'

        if ($inRepo) {
            dotnet run --project (Join-Path $root 'src\CoreBridge.Cli') -- work
        }
        else {
            dotnet run --project (Join-Path $root 'src\CoreBridge.Tui')
        }
    }
    else {
        # Development version, with all supplied arguments.
        dotnet run --project (Join-Path $root 'src\CoreBridge.Cli') -- @cliArgs
    }
}