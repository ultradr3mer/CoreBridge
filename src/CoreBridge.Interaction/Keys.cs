namespace CoreBridge.Interaction;

/// <summary>
/// Tastenkürzel eines Auswahlpunkts, wie sie ein Terminal-UI erwarten würde.
/// <see cref="OptionDef"/> nimmt sie als reines Dokumentationsdetail an
/// (siehe <see cref="OptionDef.Keys"/>), die Konsolen-Eingabe
/// (<see cref="ConsoleAdapter"/>) wertet sie nicht aus — <c>Console.ReadLine</c>
/// kennt keine Einzeltasten.
/// </summary>
[Flags]
public enum Keys
{
    /// <summary>Kein Kürzel.</summary>
    None = 0,

    /// <summary>Eingabe bestätigen.</summary>
    Enter = 13,

    /// <summary>Zurück/Zeichen löschen.</summary>
    Backspace = 8,

    /// <summary>Abbrechen.</summary>
    Esc = 27,

    /// <summary>Umschalt.</summary>
    Tab = 9,

    /// <summary>Strg-Taste als Flag über einem Buchstaben, z. B. <c>Keys.Ctrl | Keys.C</c>.</summary>
    Ctrl = 1 << 16,

    A = 'a',
    B = 'b',
    C = 'c',
    D = 'd',
    E = 'e',
    F = 'f',
    G = 'g',
    H = 'h',
    I = 'i',
    J = 'j',
    K = 'k',
    L = 'l',
    M = 'm',
    N = 'n',
    O = 'o',
    P = 'p',
    Q = 'q',
    R = 'r',
    S = 's',
    T = 't',
    U = 'u',
    V = 'v',
    W = 'w',
    X = 'x',
    Y = 'y',
    Z = 'z'
}
