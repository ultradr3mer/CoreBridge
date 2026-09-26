function cb {
    # Bewusst KEIN param(): sonst wird cb eine "advanced function" mit
    # Common-Parameters (-InformationAction, -Verbose, ...), die CLI-Shortflags
    # wie -i/-n/-c kaputt binden. Mit $args wird 1:1 durchgereicht.
    $cliProject = ".\src\CoreBridge.Cli\"
    $tuiProject = ".\src\CoreBridge.Tui\"

    if ($args.Count -eq 0) {
        # Kein Argument angegeben -> TUI starten statt der reinen CLI-Hilfe.
        if (Test-Path $tuiProject) {
            dotnet run --project $tuiProject
        }
    }
    elseif (Test-Path $cliProject) {
        dotnet run --project $cliProject -- @args
    }
    else {
        core-bridge @args
    }
}