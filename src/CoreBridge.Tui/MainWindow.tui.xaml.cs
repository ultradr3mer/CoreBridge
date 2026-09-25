using Terminal.Gui.Views;

namespace CoreBridge.Tui;

public partial class MainWindow
{
    public MainWindow ()
    {
        InitializeComponent ();
    }

    private void OnNameChanged (object? sender, EventArgs e)
    {
    }

    private void OnGreetClicked (object? sender, EventArgs e)
    {
        var name = NameField.Text.ToString ().Trim ();

        GreetingLabel.Text = string.IsNullOrEmpty (name)
            ? "Hello, Anonymous!"
            : $"Hello, {name}!";
    }

    private void OnExitClicked (object? sender, EventArgs e)
    {
        App?.RequestStop ();
    }
}
