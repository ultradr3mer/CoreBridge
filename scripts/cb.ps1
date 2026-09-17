function cb {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    $cliProject = "F:\source\CoreBridge\src\CoreBridge.Cli"

    if (Test-Path $cliProject) {
        dotnet run --project $cliProject -- @Arguments
    }
    else {
        core-bridge @Arguments
    }
}
