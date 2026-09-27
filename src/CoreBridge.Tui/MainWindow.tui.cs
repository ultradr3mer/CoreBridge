using CoreBridge.Tui.Controls;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBridge.Tui;

public class MainWindow : Runnable
{
    private readonly Splitter splitter;

    public MainWindow()
    {
        this.splitter = new Splitter
        {
            Orientation = Orientation.Vertical,
            FixedPane = SplitterPane.First,
            FixedSize = Dim.Percent(50),
            CanCollapseFirst = true,
            CanCollapseSecond = true
        };

               var splitter2 = new Splitter
        {
            Orientation = Orientation.Horizontal,
            FixedPane = SplitterPane.First,
            FixedSize = Dim.Percent(50),
            CanCollapseFirst = true,
            CanCollapseSecond = true,
        };

        splitter2.First.Add(splitter);

        this.Add(splitter2);
    }
}
