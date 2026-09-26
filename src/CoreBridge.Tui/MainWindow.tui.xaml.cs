using Terminal.Gui.Views;

namespace CoreBridge.Tui;

public partial class MainWindow
{
    public List<string> CommandList { get; set; } 

    public MainWindow()
    {
        CommandList = new List<string> { "Option 1", "Option 2", "Option 3" };
        InitializeComponent();
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
