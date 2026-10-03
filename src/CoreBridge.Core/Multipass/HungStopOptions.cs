using CoreBridge.Interaction;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Angebot, während <see cref="MultipassService.Restart"/> auf den Stop des Dienstes
/// wartet. Die Standardantwort ist „weiter warten“: mit ihr schließt der Dienst das
/// Angebot, wenn das Warten ohne Tastendruck endet.
/// </summary>
public enum HungStopOptions
{
    [OptionDef("multipassd jetzt hart beenden", "k,kill", [Keys.Enter])]
    Kill,

    [OptionDef("Abbrechen", "a,abbrechen", [Keys.Esc])]
    Cancel,

    [OptionDef("Weiter warten", "w,warten", IsDefault = true)]
    Wait
}
