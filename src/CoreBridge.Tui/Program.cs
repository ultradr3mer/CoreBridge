// -----------------------------------------------------------------------------
// CoreBridge TUI entry point.
// Run it with: dotnet run --project src/CoreBridge.Tui
// -----------------------------------------------------------------------------

using CoreBridge.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;

ConfigurationManager.Enable (ConfigLocations.All);

Application
    .Create ()
    .Run<MainWindow> ()
    .Dispose ();