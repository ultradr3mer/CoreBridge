using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace CoreBridge.Tui.Controls;

/// <summary>Welche Hälfte eines <see cref="Splitter"/> gemeint ist.</summary>
public enum SplitterPane
{
    /// <summary>Links (<see cref="Orientation.Horizontal"/>) bzw. oben (<see cref="Orientation.Vertical"/>).</summary>
    First,

    /// <summary>Rechts (<see cref="Orientation.Horizontal"/>) bzw. unten (<see cref="Orientation.Vertical"/>).</summary>
    Second
}

/// <summary>
/// Teilt seine Fläche in zwei Hälften mit einer ziehbaren Trennlinie.
/// <see cref="Orientation.Horizontal"/> legt <see cref="First"/> und <see cref="Second"/>
/// nebeneinander (links | rechts), <see cref="Orientation.Vertical"/> übereinander
/// (oben / unten). Die <see cref="FixedPane"/> startet mit <see cref="FixedSize"/>
/// (z. B. <c>20</c> Zellen oder <c>Dim.Percent(30)</c>) und trägt die Trennlinie; die
/// andere füllt den Rest.
/// Inhalte kommen per <c>First.Add(…)</c> bzw. <c>Second.Add(…)</c> hinein.
/// </summary>
/// <remarks>
/// Gezogen wird über die Arrangement-Funktion von Terminal.Gui an der Trennlinie der
/// festen Hälfte. Danach ist ihre Größe absolut (eine Prozent-Angabe gilt nur für den
/// Start); alles andere bleibt deklarativ, damit der Splitter Größenänderungen des
/// Fensters weiter folgt.
/// </remarks>
public class Splitter : View
{
    private Orientation orientation = Orientation.Horizontal;
    private SplitterPane fixedPane = SplitterPane.First;
    private Dim fixedSize = Dim.Percent(30);

    public Splitter()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        First = new View { Id = "First" };
        Second = new View { Id = "Second" };
        Add(First, Second);
        ApplyLayout();
    }

    /// <summary>Links bzw. oben.</summary>
    public View First { get; }

    /// <summary>Rechts bzw. unten.</summary>
    public View Second { get; }

    /// <summary><see cref="Orientation.Horizontal"/>: nebeneinander; <see cref="Orientation.Vertical"/>: übereinander.</summary>
    public Orientation Orientation
    {
        get => orientation;
        set
        {
            orientation = value;
            ApplyLayout();
        }
    }

    /// <summary>Die Hälfte mit fester Startgröße; die andere füllt den Rest.</summary>
    public SplitterPane FixedPane
    {
        get => fixedPane;
        set
        {
            fixedPane = value;
            ApplyLayout();
        }
    }

    /// <summary>Startgröße der <see cref="FixedPane"/>, z. B. <c>20</c> (Zellen) oder <c>Dim.Percent(30)</c>.</summary>
    public Dim FixedSize
    {
        get => fixedSize;
        set
        {
            fixedSize = value;
            ApplyLayout();
        }
    }

    /// <summary>Die Hälfte mit fester Größe.</summary>
    public View FixedView => fixedPane == SplitterPane.First ? First : Second;

    /// <summary>Die Hälfte, die den Rest füllt.</summary>
    public View FillView => fixedPane == SplitterPane.First ? Second : First;

    /// <summary>
    /// Ziehen setzt den Frame der festen Hälfte und macht damit X, Y, Width und Height
    /// absolut. Vor jedem Layout wird alles außer der gezogenen Größe wieder deklarativ.
    /// </summary>
    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        Reanchor(FixedView);
        base.OnSubViewLayout(args);
    }

    private void ApplyLayout()
    {
        var (fix, fill) = (FixedView, FillView);
        var horizontal = orientation == Orientation.Horizontal;

        fill.BorderStyle = LineStyle.None;
        fill.Arrangement = ViewArrangement.Fixed;
        fill.SuperViewRendersLineCanvas = false;

        // Die Trennlinie ist der Rand der festen Hälfte auf der Seite zur füllenden hin;
        // gezeichnet wird sie in der LineCanvas des Splitters.
        fix.BorderStyle = LineStyle.Single;
        fix.SuperViewRendersLineCanvas = true;
        (fix.Arrangement, fix.Border.Thickness) = (horizontal, fixedPane) switch
        {
            (true, SplitterPane.First) => (ViewArrangement.RightResizable, new Thickness(0, 0, 1, 0)),
            (true, SplitterPane.Second) => (ViewArrangement.LeftResizable, new Thickness(1, 0, 0, 0)),
            (false, SplitterPane.First) => (ViewArrangement.BottomResizable, new Thickness(0, 0, 0, 1)),
            _ => (ViewArrangement.TopResizable, new Thickness(0, 1, 0, 0))
        };

        if (horizontal)
        {
            fix.Width = fixedSize;
            fill.X = fixedPane == SplitterPane.First ? Pos.Right(fix) : 0;
            fill.Y = 0;
            fill.Width = fixedPane == SplitterPane.First ? Dim.Fill() : Dim.Fill(to: fix);
            fill.Height = Dim.Fill();
        }
        else
        {
            fix.Height = fixedSize;
            fill.X = 0;
            fill.Y = fixedPane == SplitterPane.First ? Pos.Bottom(fix) : 0;
            fill.Width = Dim.Fill();
            fill.Height = fixedPane == SplitterPane.First ? Dim.Fill() : Dim.Fill(to: fix);
        }

        // Position und Querachse der festen Hälfte setzt dieselbe Logik wie nach dem Ziehen.
        Reanchor(fix);
        SetNeedsLayout();
    }

    /// <summary>
    /// Feste Hälfte an ihren Rand heften: die zweite ans Ende (AnchorEnd), die erste an 0;
    /// die Querachse füllt. Gesetzt wird nur, was abweicht — sonst löste jeder Durchlauf
    /// ein neues Layout aus.
    /// </summary>
    private void Reanchor(View fix)
    {
        var atEnd = fixedPane == SplitterPane.Second;
        if (orientation == Orientation.Horizontal)
        {
            SetPos(fix, atEnd, horizontal: true);
            if (fix.Y is not PosAbsolute { Position: 0 })
            {
                fix.Y = 0;
            }

            if (fix.Height is not DimFill)
            {
                fix.Height = Dim.Fill();
            }
        }
        else
        {
            SetPos(fix, atEnd, horizontal: false);
            if (fix.X is not PosAbsolute { Position: 0 })
            {
                fix.X = 0;
            }

            if (fix.Width is not DimFill)
            {
                fix.Width = Dim.Fill();
            }
        }
    }

    private static void SetPos(View fix, bool atEnd, bool horizontal)
    {
        var current = horizontal ? fix.X : fix.Y;
        var wanted = atEnd
            ? current is PosAnchorEnd ? null : Pos.AnchorEnd()
            : current is PosAbsolute { Position: 0 } ? null : Pos.Absolute(0);
        if (wanted is null)
        {
            return;
        }

        if (horizontal)
        {
            fix.X = wanted;
        }
        else
        {
            fix.Y = wanted;
        }
    }
}
