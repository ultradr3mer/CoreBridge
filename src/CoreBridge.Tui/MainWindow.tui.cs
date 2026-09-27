using CoreBridge.Tui.Controls;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBridge.Tui;

public partial class MainWindow : Runnable
{
    private readonly Splitter splitter;

    public MainWindow()
    {
        this.splitter = new Splitter
        {
            Orientation = Orientation.Vertical,
            FixedPane = SplitterPane.Second,
            FixedSize = Dim.Percent(30)
        };

        this.Add(this.splitter);
    }
}
