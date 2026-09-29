using TuiControls;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CoreBridge.Cli;

namespace CoreBridge.Tui;

public partial class MainWindow : Runnable
{
    static MainWindow()
    {
        var test = CommandMenus.All;
    }
}
