// TUI Live Preview settings. Fill in what you need and delete the rest; an empty value
// is ignored. Paths are relative to this file. These win over MainWindow.tui.json.
// @tuipreview class=CoreBridge.Tui.MainWindow  -- the class to preview
// @tuipreview width=80 height=24               -- render size; interactive mode fills the terminal
// @tuipreview extra=""                         -- .cs files compiled with the view, for layout helpers; repeat the line for several
// @tuipreview reference=""                     -- .dlls, or folders of them, for controls beyond Terminal.Gui; repeat the line for several
// @tuipreview clipath=""                       -- the tuipreview tool to preview this view with
using System.Collections.ObjectModel;
using CoreBridge.Cli;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBridge.Tui;

public partial class MainWindow : Runnable
{
    /// <summary>
    /// Die Verben, die das DropDown anzeigt. Der View-Designer kompiliert diese Datei
    /// allein (nur Terminal.Gui/TuiControls, ohne CoreBridge.Cli), deshalb steht hier
    /// zunächst ein Beispiel-Datensatz drin. Der statische Konstruktor in
    /// <see cref="MainWindow"/> tauscht ihn zur Laufzeit  gegen <c>CommandMenus.All</c> aus —
    /// alle statischen Feldinitialisierer laufen vor dem statischen Konstruktor.
    /// </summary>
    public static IReadOnlyList<CommandMenu> CommandMenuNames { get; set; } = CommandMenus.All;

    /// <summary>
    /// Das DropDown meldet hier, welcher Verb bestätigt wurde. Die Deklaration muss in
    /// dieser Datei stehen, weil der View-Designer sie allein kompiliert — aber nur
    /// <c>MainWindow.cs</c> darf sie belegen, weil nur dort <c>CoreBridge.Cli</c> geladen
    /// werden kann. Der statische Konstruktor dort verdrahtet die beiden Hälften.
    /// </summary>
    public static Action<string>? CommandSelected { get; set; }

    private readonly ObservableCollection<CommandMenu> commandItems;
    private readonly ListWrapper<CommandMenu> commandSource;
    private readonly DropDownList commandList;

    public MainWindow()
    {
        commandItems = new ObservableCollection<CommandMenu> (CommandMenuNames);
        commandSource = new ListWrapper<CommandMenu> (commandItems);

        // Das erste Verb als Vorauswahl, sonst zeigt das Textfeld erst einmal nichts an.
        commandList = new DropDownList
        {
            X = Pos.Center (),
            Y = Pos.Center (),
            Width = Dim.Absolute(20), 
            Height = 1,
            Source = commandSource,
            Title = "Command",
            Text = (commandItems.Count > 0 ? commandItems[0] : CommandMenu.Empty).ToString()
        };

        commandList.Accepted += (_, _) => CommandSelected?.Invoke (commandList.Text ?? string.Empty);

        Add (commandList);
    }
}
