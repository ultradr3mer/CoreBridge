using TuiControls;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CoreBridge.Cli;

namespace CoreBridge.Tui;

/// <summary>
/// Die Logic-Hälfte von <see cref="MainWindow"/>: hier steht alles, was
/// <c>CoreBridge.Cli</c> braucht. Die View-Hälfte <c>MainWindow.tui.cs</c> kennt das
/// nicht — der View-Designer kompiliert sie allein und kann die Bibliothek nicht laden.
/// Beide Hälften sind <c>partial</c>, deshalb gehört genau ein Konstruktor zur Klasse.
/// </summary>
public partial class MainWindow : Runnable
{
    static MainWindow()
    {
        // Die Verben aus den Attributen statt der Beispiel-Daten aus der View-Hälfte.
        CommandMenuNames = CommandMenus.All.ToList();

        // Die View-Hälfte meldet hierhin, sobald im DropDown ein Verb bestätigt wurde.
        CommandSelected = name =>
        {
            CommandMenu? menu = CommandMenus.Find(name);
            if (menu is null)
            {
                return;
            }

            // TODO: menu.Name / menu.Help / menu.Parameters hier verwenden (anzeigen oder starten).
        };
    }
}
