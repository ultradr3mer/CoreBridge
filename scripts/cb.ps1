function cb {
    # Bewusst KEIN param(): sonst wird cb eine "advanced function" mit
    # Common-Parameters (-InformationAction, -Verbose, ...), die CLI-Shortflags
    # wie -i/-n/-c kaputt binden. Mit $args wird 1:1 durchgereicht.
    # PowerShell macht aus `-c claude,dotnet` ein Array und reicht es an native
    # Programme als getrennte Argumente weiter (`-c claude dotnet`) — der Parser
    # verwirft den Rest dann stillschweigend. Listen daher wieder zusammenfügen.
    $cliArgs = @(foreach ($arg in $args) { if ($arg -is [array]) { $arg -join ',' } else { $arg } })
    $cliProject = ".\src\CoreBridge.Cli\"
    $tuiProject = ".\src\CoreBridge.Tui\"

    if ($args.Count -eq 0) {
        # Kein Argument angegeben -> TUI starten statt der reinen CLI-Hilfe.
        if (Test-Path $tuiProject) {
            dotnet run --project $tuiProject
        }
    }
    elseif (Test-Path $cliProject) {
        dotnet run --project $cliProject -- @cliArgs
    }
    else {
        core-bridge @cliArgs
    }
}

function cb-sim {
    # Wie cb, aber gegen den Multipass-Simulator (appsettings.Simulation.json):
    # keine echten VMs, Zustand in artifacts\multipass-sim\state.json.
    # Zurücksetzen: Remove-Item artifacts\multipass-sim -Recurse
    $previous = $env:DOTNET_ENVIRONMENT
    $env:DOTNET_ENVIRONMENT = 'Simulation'
    try {
        cb @args
    }
    finally {
        $env:DOTNET_ENVIRONMENT = $previous
    }
}
