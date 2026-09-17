function cb {
    # Bewusst KEIN param(): sonst wird cb eine "advanced function" mit
    # Common-Parameters (-InformationAction, -Verbose, ...), die CLI-Shortflags
    # wie -i/-n/-c kaputt binden. Mit $args wird 1:1 durchgereicht.
    $cliProject = "F:\source\CoreBridge\src\CoreBridge.Cli"

    if (Test-Path $cliProject) {
        dotnet run --project $cliProject -- @args
    }
    else {
        core-bridge @args
    }
}
