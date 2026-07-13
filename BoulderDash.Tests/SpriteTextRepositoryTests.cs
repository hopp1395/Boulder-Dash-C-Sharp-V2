using BoulderDash.Core.Data;
using BoulderDash.Core.Simulation;

namespace BoulderDash.Tests;

public class SpriteTextRepositoryTests
{
    private static readonly string SpritesPath = Path.Combine(TestPaths.GameAssets, "Sprites");

    /// <summary>Die Frames aller Objekte hintereinander = die 49 Rohsprites (Reihenfolge in SPRITES.BIN).</summary>
    private static IReadOnlyList<byte[]> RawSprites() =>
        new SpriteTextRepository(SpritesPath).Get().SelectMany(sprite => sprite.Frames).ToArray();

    [Fact]
    public void Repository_liefert_49_Sprites_mit_korrekten_Groessen()
    {
        var sprites = RawSprites();

        Assert.Equal(49, sprites.Count);

        // Alle 16x16, nur das letzte (border-fill, MASK_SLAUF) ist 16x24.
        for (var i = 0; i < sprites.Count; i++)
        {
            Assert.Equal(i == 48 ? 16 * 24 : 16 * 16, sprites[i].Length);
        }
    }

    /// <summary>Die 4 Cave-Farben und ihre 4 Schattentöne (siehe Palette.Expand).</summary>
    [Fact]
    public void Alle_Pixelwerte_sind_gueltige_Palettenindizes_0_bis_7()
    {
        foreach (var sprite in RawSprites())
        {
            Assert.All(sprite, pixel => Assert.InRange(pixel, (byte)0, (byte)(Palette.Size - 1)));
        }
    }
}
