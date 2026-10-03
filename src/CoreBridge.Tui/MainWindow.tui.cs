using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBridge.Tui;

public partial class MainWindow : Runnable
{
    /// <summary>Platzhalter, damit der Designer eine gefüllte Liste zeigt.</summary>
    private static readonly string[] DesignTimeCommandNames =
        [ "vm-list", "vm-start", "vm-stop", "vm-delete", "vm-exec", "vm-create" ];

    /// <summary>
    /// Die Verben, die das DropDown anzeigt. Der View-Designer kompiliert diese Datei
    /// allein (nur Terminal.Gui/TuiControls, ohne CoreBridge.Cli), deshalb steht hier
    /// zunächst ein Beispiel-Datensatz drin. Der statische Konstruktor in
    /// <see cref="MainWindow"/> tauscht ihn zur Laufzeit  gegen <c>CommandMenus.All</c> aus —
    /// alle statischen Feldinitialisierer laufen vor dem statischen Konstruktor.
    /// </summary>
    public static IReadOnlyList<string> CommandMenuNames { get; set; } = DesignTimeCommandNames;

    /// <summary>
    /// Das DropDown meldet hier, welcher Verb bestätigt wurde. Die Deklaration muss in
    /// dieser Datei stehen, weil der View-Designer sie allein kompiliert — aber nur
    /// <c>MainWindow.cs</c> darf sie belegen, weil nur dort <c>CoreBridge.Cli</c> geladen
    /// werden kann. Der statische Konstruktor dort verdrahtet die beiden Hälften.
    /// </summary>
    public static Action<string>? CommandSelected { get; set; }

    private readonly ObservableCollection<string> commandItems;
    private readonly ListWrapper<string> commandSource;
    private readonly DropDownList commandList;

    public MainWindow()
    {
        commandItems = new ObservableCollection<string> (CommandMenuNames);
        commandSource = new ListWrapper<string> (commandItems);

        // Das erste Verb als Vorauswahl, sonst zeigt das Textfeld erst einmal nichts an.
        commandList = new DropDownList
        {
            X = Pos.Center (),
            Y = Pos.Center (),
            Width = Dim.Absolute(20), 
            Height = 1,
            Source = commandSource,
            Title = "Command",
            Text = commandItems.Count > 0 ? commandItems[0] : string.Empty
        };

        commandList.Accepted += (_, _) => CommandSelected?.Invoke (commandList.Text ?? string.Empty);

        Add (commandList);
    }
}
