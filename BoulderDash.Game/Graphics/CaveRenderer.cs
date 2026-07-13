using BoulderDash.Core.Objects;
using BoulderDash.Core.Simulation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BoulderDash.Game.Graphics;

/// <summary>
/// Zeichnet den laufenden Spielzustand: das Kachel-Sichtfenster ab der aktuellen Kameraposition
/// (Original 20x12, per Spielflächen-Zoom bis zur vollen Cave, siehe ViewportSize) und darüber die
/// Verdeckung (ScreenCover) — entspricht copy64()+BildschirmMaske() aus src/BOULDER.CPP.
///
/// Welches Bild eine Kachel zeigt, entscheidet das Objekt selbst (CaveObject.Appearance); der
/// Renderer fragt nur noch danach und führt aus. Die Frameauswahl liegt damit in Core und ist
/// headless prüfbar — früher steckte sie als switch über alle Elemente hier drin.
/// </summary>
public sealed class CaveRenderer
{
    public const int TileSize = 16;

    /// <summary>Höhe der Statuszeile in Pixeln (eine BIOS-Textzeile); das Spielfeld beginnt darunter.</summary>
    public const int StatusLineHeight = 8;

    /// <summary>Womit eine verdeckte Kachel überzeichnet wird — der durchlaufende Rand-Füllstein.
    /// Er liegt ÜBER dem Gitter statt darin und gehört deshalb zu keiner Höhle (Cave.Nowhere);
    /// der Renderer hält ihn selbst und taktet ihn mit der Cave mit.</summary>
    private readonly BorderFillObject _cover = new(Cave.Nowhere);

    /// <summary>Was an der Stelle einer Kreatur steht, die im Nebel vergessen ist
    /// (CaveObject.VisibleInFog): Leerraum — genau das, worüber Geist und Schmetterling ziehen.
    /// Gehört wie die Verdeckung zu keiner Höhle.</summary>
    private readonly EmptyObject _forgotten = new(Cave.Nowhere);

    private readonly SpriteAtlas _atlas;

    public CaveRenderer(SpriteAtlas atlas)
    {
        _atlas = atlas;
    }

    /// <summary>Logische Größe der Zeichenfläche für ein Sichtfenster: Spielfeld plus Statuszeile.
    /// Beim Original-Sichtfenster 20x12 ergibt das genau die VGA-Auflösung 320x200.</summary>
    public static (int Width, int Height) LogicalSize(ViewportSize viewport) =>
        (viewport.Columns * TileSize, StatusLineHeight + (viewport.Rows * TileSize));

    /// <summary>Wo das gezeichnete Cave-Fenster auf der Zeichenfläche liegt und wie viele Kacheln es
    /// wirklich zeigt (siehe <see cref="LayoutFor"/>).</summary>
    public readonly record struct PlayfieldLayout(int OffsetX, int OffsetY, int Columns, int Rows);

    /// <summary>
    /// Die Lage des Cave-Fensters auf der Zeichenfläche. Ist das Sichtfenster größer als die Cave
    /// (z. B. eine 20x12-Intermission bei großem Zoom), steht die Kamera auf 0 (Camera.Clamp) und die
    /// Cave wird im schwarzen Rest zentriert.
    ///
    /// Der Versatz ist NICHT zwangsläufig kachelbündig — bei ungerader Spaltendifferenz bleiben 8
    /// Pixel. Wer damit rechnet, rechne in Pixeln. <see cref="Draw"/> und die Horror-Beleuchtung
    /// (HorrorPostProcessor) tun das hier gemeinsam, damit beide dieselbe Geometrie sehen.
    /// </summary>
    public static PlayfieldLayout LayoutFor(Cave cave, ViewportSize viewport) => new(
        Math.Max(0, (viewport.Columns - cave.Width) * TileSize / 2),
        Math.Max(0, (viewport.Rows - cave.Height) * TileSize / 2),
        Math.Min(viewport.Columns, cave.Width),
        Math.Min(viewport.Rows, cave.Height));

    /// <param name="fogViaShader">Der Nebel des Cave-Explore kommt nicht mehr von hier, sondern vom
    /// Compose-Shader der Horror-Beleuchtung (HorrorPostProcessor): Erkundetes wird dann in NORMALEN
    /// Farben gezeichnet und Unerkundetes gar nicht erst ausgelassen — Entsättigen und Schwärzen
    /// besorgt der Shader, und zwar mit weichen Kanten quer über die Kacheln hinweg statt an ihnen
    /// entlang. Was der Renderer trotzdem selbst tut: die vergessenen Kreaturen ersetzen. Das ist
    /// keine Optik, sondern die Regel, was das Gedächtnis behält.</param>
    public void Draw(SpriteBatch batch, Cave cave, Camera camera, GameState state, InputState input, Clocks clocks, ScreenCover? cover, ExploreMap? explore = null, bool fogViaShader = false)
    {
        var viewport = camera.Viewport;
        var context = new RenderContext(clocks.Clk4, state.ExitFlashOn, state.EnchantedWallRunning, input);

        // Die Verdeckung läuft im selben Takt wie die Objekte, gehört aber nicht zum Gitter.
        _cover.AnimationPhase = cave.AnimationPhase;

        var (offsetX, offsetY, columns, rows) = LayoutFor(cave, viewport);

        for (var row = 0; row < rows; row++)
        {
            var y = camera.Y + row;
            if (y >= cave.Height)
            {
                continue;
            }

            for (var col = 0; col < columns; col++)
            {
                var x = camera.X + col;
                if (x >= cave.Width)
                {
                    continue;
                }

                var destination = new Rectangle(
                    offsetX + (col * TileSize),
                    offsetY + StatusLineHeight + (row * TileSize),
                    TileSize,
                    TileSize);

                var tile = cave.Get(x, y);

                // Die Verdeckungsmaske liegt in Cave-Koordinaten (siehe ScreenCover) und entscheidet
                // selbst, wie lange sie gilt: beim Cave-Start bis zum Vollaufdecken, am Cave-Ende
                // bis zum vollständigen Zudecken. Sie hat Vorrang vor dem Nebel — sonst wäre die
                // Stahlwand-Animation über unerkundetem Gelände nicht zu sehen. Außerhalb der Höhle
                // deckt sie nichts zu (CaveObject.CoveredByScreen): Dort bleibt es schwarz, damit die
                // Silhouette der Cave auf- und zugeht und nicht das Rechteck des Gitters.
                if (cover is not null && cover.IsCovered(x, y) && tile.CoveredByScreen)
                {
                    _atlas.Draw(batch, destination, _cover.Appearance(context));
                    continue;
                }

                // Cave-Explore (siehe ExploreMap): Unerkundetes wird schlicht nicht gezeichnet — das
                // RenderTarget ist schwarz gelöscht (BoulderDashGame.Draw), es braucht kein schwarzes
                // Sprite. Erkundetes außerhalb des Blickradius kommt im Nebelgrau.
                var visibility = explore?.Visibility(x, y) ?? TileVisibility.Visible;
                if (visibility == TileVisibility.Hidden && !fogViaShader)
                {
                    continue;
                }

                var explored = visibility == TileVisibility.Explored;

                // Der Nebel zeigt nur die erinnerte Umgebung. Wer aus eigenem Antrieb umherzieht, ist
                // dort nicht zu sehen — an seiner Stelle steht der Leerraum, über den er zieht. Das
                // gilt auch für Unerkundetes: Beim Shader-Nebel wird es zwar gezeichnet, aber seine
                // weiche Kante darf keine Kreatur aus dem Schwarzen durchscheinen lassen.
                if (visibility != TileVisibility.Visible && !tile.VisibleInFog)
                {
                    tile = _forgotten;
                }

                _atlas.Draw(batch, destination, tile.Appearance(context), fogged: explored && !fogViaShader);
            }
        }
    }
}
