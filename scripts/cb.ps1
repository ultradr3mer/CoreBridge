function cb {
    # No param(): Common Parameters would capture short flags such as -i, -n, or -c.
    # Arrays (-c claude,dotnet) may arrive as separate arguments, so join them with commas.
    $cliArgs = @(
        foreach ($arg in $args) {
            if ($arg -is [array]) { $arg -join ',' }
            else { $arg }
        }
    )

    # Checkout: search upward for CoreBridge.slnx; otherwise use the installed checkout.
    $root = (Get-Location).ProviderPath
    while ($root -and -not (Test-Path -LiteralPath (Join-Path $root 'CoreBridge.slnx'))) {
        $root = Split-Path -Parent $root
    }

    $installedRoot = '__COREBRIDGE_ROOT__'
    if (-not $root -and (Test-Path -LiteralPath (Join-Path $installedRoot 'CoreBridge.slnx'))) {
        $root = $installedRoot
    }

    if (-not $root) {
        Write-Error "No CoreBridge checkout found. Run cb in the repository or a worktree."
        return
    }

    if ($args.Count -eq 0) {
        # Without arguments: run cb work inside a git repository, otherwise start the TUI.
        $inRepo = (git rev-parse --is-inside-work-tree 2>$null) -eq 'true'

        if ($inRepo) {
            dotnet run --project (Join-Path $root 'src\CoreBridge.Cli') -- work
        }
        else {
            dotnet run --project (Join-Path $root 'src\CoreBridge.Tui')
        }
    }
    else {
        dotnet run --project (Join-Path $root 'src\CoreBridge.Cli') -- @cliArgs
    }
}

function cb-sim {
    # Run cb with the simulator. State is stored in artifacts\multipass-sim.
    $previous = $env:DOTNET_ENVIRONMENT
    $env:DOTNET_ENVIRONMENT = 'Simulation'

    try {
        cb @args
    }
    finally {
        $env:DOTNET_ENVIRONMENT = $previous
    }
}