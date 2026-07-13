using System.Globalization;

namespace BoulderDash.Core.Simulation;

/// <summary>Eine Anzeigefarbe mit 8 Bit pro Kanal.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Liest eine Farbe im Format <c>#RRGGBB</c> (Rautezeichen optional, Hex-Ziffern in beliebiger Schreibweise).</summary>
    public static bool TryParse(string text, out Rgb color)
    {
        color = default;
        var digits = text.AsSpan().Trim();
        if (digits.StartsWith("#"))
        {
            digits = digits[1..];
        }

        if (digits.Length != 6
            || !byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(digits[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = new Rgb(r, g, b);
        return true;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Farben, die nicht aus der Cave-Datei stammen.
///
/// Die vier Farben einer Cave stehen inzwischen als RGB-Werte in der Cave-Datei selbst
/// (<c>[Rules] Colors</c>, siehe CaveTextFile) — wie alle Spieldaten also WYSIWYG. Die frühere
/// 16-Farben-Tabelle mit 6-Bit-VGA-DAC-Werten (setnewpalette, src/BOULDER.CPP:496-512) und die
/// Umrechnung auf 8 Bit pro Kanal gibt es nicht mehr; ihre Ergebniswerte sind einmalig in die
/// Cave-Dateien eingeflossen.
/// </summary>
public static class Palette
{
    /// <summary>Die Farben, die in der Cave-Datei stehen (Color1-Color4).</summary>
    public const int CaveColors = 4;

    /// <summary>Die Farben, die ein Sprite-Pixel ansprechen kann: die vier Cave-Farben plus ihre
    /// vier Schattentöne (siehe <see cref="Expand"/>).</summary>
    public const int Size = CaveColors * 2;

    /// <summary>Blitz-Farben des Ausgangs (ende(), BOULDER.CPP:683-684): abwechselnd weiß und dunkel.
    /// Sie ersetzen die Farbe 0 der Cave-Palette, entsprechen den DAC-Tripeln 63,63,63 bzw. 8,8,8.</summary>
    public static readonly Rgb ExitFlashBright = new(0xFF, 0xFF, 0xFF);
    public static readonly Rgb ExitFlashDark = new(0x20, 0x20, 0x20);

    /// <summary>Wie dunkel ein Schattenton gegenüber seiner Cave-Farbe ist. Tief genug, dass er sich
    /// klar absetzt, hoch genug, dass er nicht mit dem Cave-Hintergrund verschmilzt.</summary>
    private const double ShadowScale = 0.58;

    /// <summary>
    /// Die 8 Farben, mit denen Sprites gezeichnet werden: die vier Farben der Cave (Index 0-3) und
    /// zu jeder ihr Schattenton (Index 4-7, also Schatten von Farbe 0 auf 4, von 1 auf 5 usw.).
    ///
    /// Die Schattentöne stehen bewusst NICHT in den Cave-Dateien, sondern werden aus deren vier
    /// Farben abgeleitet: So behält jede Cave ihr eigenes Farbschema — der Schatten ist immer die
    /// dunklere Variante genau der Farbe, die daneben liegt — und die Cave-Dateien bleiben bei den
    /// vier Farben des Originals. Derselbe Kniff wie bei <see cref="Fog"/>, nur ohne Entsättigung.
    ///
    /// Damit haben die Sprites eine echte Tonleiter statt vier Sprüngen und können Rundungen
    /// schattieren (Boulder, Diamant); Palettenwechsel bleiben so billig wie zuvor, weil die
    /// Schatten mitberechnet und nicht mitgespeichert werden. Keine Original-Entsprechung: BD1
    /// hatte pro Cave nur die vier C64-Farben.
    /// </summary>
    public static Rgb[] Expand(IReadOnlyList<Rgb> caveColors)
    {
        var colors = new Rgb[Size];
        for (var i = 0; i < CaveColors; i++)
        {
            colors[i] = caveColors[i];
            colors[i + CaveColors] = Shadow(caveColors[i]);
        }

        return colors;
    }

    /// <summary>Die abgedunkelte Variante einer Cave-Farbe — der Farbton bleibt, die Helligkeit
    /// sinkt (siehe <see cref="Expand"/>).</summary>
    public static Rgb Shadow(Rgb color) => new(
        (byte)Math.Round(color.R * ShadowScale),
        (byte)Math.Round(color.G * ShadowScale),
        (byte)Math.Round(color.B * ShadowScale));

    /// <summary>Die beiden Geschmacks-Regler von <see cref="Fog"/>: Der Sockel hebt den Nebel als
    /// Ganzes vom Schwarz des Unerkundeten ab, die Dämpfung hält ihn hinter dem farbigen
    /// Blickradius zurück.</summary>
    private const double FogFloor = 22;
    private const double FogContrast = 0.5;

    /// <summary>
    /// Wie eine Cave-Farbe im Nebel aussieht (Cave-Explore, siehe ExploreMap): entsättigt und
    /// gedämpft. Erkundetes, aber gerade nicht sichtbares Gelände tritt damit blass grau hinter den
    /// farbigen Blickradius zurück.
    ///
    /// Entsättigt wird über die HSL-Helligkeit (max+min)/2, NICHT über die naheliegende
    /// Rec.-601-Luminanz: Deren Gewichtung (Blau nur 0,114) drückt gesättigte Farben fast auf
    /// Schwarz — das Blau #2020BA der Cave-Paletten landete bei #1B1B1B und wäre damit vom
    /// Hintergrund #202020 nicht mehr zu unterscheiden gewesen. Die Cave muss im Nebel aber lesbar
    /// bleiben; die HSL-Helligkeit hält alle vier Palettenfarben auseinander.
    ///
    /// Der Sockel (<see cref="FogFloor"/>) hebt auch die dunkelste Farbe — den Cave-Hintergrund
    /// #202020 — über Schwarz. Ohne ihn sähe ein erkundeter, aber leerer Gang genauso aus wie nie
    /// betretenes Gebiet, und von den drei Zuständen (schwarz / blass grau / farbig) blieben zwei
    /// übrig.
    ///
    /// Keine Original-Entsprechung.
    /// </summary>
    public static Rgb Fog(Rgb color)
    {
        var lightness = (Math.Max(color.R, Math.Max(color.G, color.B))
                         + Math.Min(color.R, Math.Min(color.G, color.B))) / 2.0;
        var grey = (byte)Math.Clamp(FogFloor + (lightness * FogContrast), 0, 255);
        return new Rgb(grey, grey, grey);
    }
}
