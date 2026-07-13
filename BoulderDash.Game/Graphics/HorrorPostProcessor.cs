using BoulderDash.Core.Objects;
using BoulderDash.Core.Simulation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BoulderDash.Game.Graphics;

/// <summary>
/// Die Horror-Beleuchtung: Die Höhle liegt im Dunkeln, und das einzige Licht kommt aus den
/// Diamanten. Was sonst noch glimmt, glimmt schwach — der offene Ausgang und, für einen
/// Augenblick, eine Explosion.
///
/// KEINE ORIGINAL-ENTSPRECHUNG — wie ExploreMap eine bewusste Zutat des Ports. Reine DARSTELLUNG:
/// Sie liest den Cave-Zustand und rührt ihn nicht an; vor allem zieht sie KEINEN Zufall (der
/// Zufallsstrom des Kerns ist geteilt und verhaltensrelevant, siehe ExploreMap). Das Flackern der
/// Diamanten kommt deshalb aus einem Positions-Hash und der Uhrzeit, nicht aus Random.
///
/// Angeschaltet wird sie zusammen mit dem Cave-Explore (E-Taste): Beides erzählt dieselbe
/// Geschichte — man sieht nur, was in Reichweite liegt.
///
/// Die Kachelgrafik bleibt, wie sie ist. Der Schrecken kommt aus dem, was ÜBER ihr liegt, und das
/// wird bewusst NICHT im Kachelmaßstab gerechnet, sondern je Bildschirmpixel: Licht, Nebel, Rauch
/// und Korn kennen kein 16er-Raster und richten sich an keiner Kachelkante aus. Dafür laufen drei
/// Bilder in den Compose-Pass (Effects/HorrorPost.fx):
///
/// <list type="bullet">
/// <item>die SZENE — die fertig gezeichneten Kacheln in logischer Auflösung (grobpixelig, retro),</item>
/// <item>die LICHTKARTE — Grundhelligkeit plus die addierten Lichtkegel; ein weicher Verlauf, den
/// das bilineare Hochskalieren im Compose so glatt macht, wie das Fenster groß ist,</item>
/// <item>die MASKE — ein Texel je Kachel (R = Nebel, G = unerkundet). Auch sie wird bilinear
/// gelesen: Genau daraus entstehen die weichen, kachelunabhängigen Nebelgrenzen.</item>
/// </list>
///
/// Der Compose-Pass zeichnet direkt in den Backbuffer, in voller Fensterauflösung. Die Statuszeile
/// kommt danach unberührt darüber (siehe BoulderDashGame.Draw) — sie ist Anzeige, nicht Höhle.
///
/// Hier ist auch der Platz für die späteren Schreckmomente: Zeit und Bildschirmgeometrie liegen dem
/// Shader schon vor, ein weiterer Parameter genügt.
/// </summary>
public sealed class HorrorPostProcessor : IDisposable
{
    /// <summary>Grundhelligkeit außerhalb allen Lichts — gedämpft, aber nicht blind: Man ahnt die
    /// Steine, man erkennt sie nicht. Leicht ins Blaue gezogen, damit das Dunkel kalt wirkt.</summary>
    private static readonly Color Ambient = new(40, 43, 52);

    /// <summary>Die Lichtquellen. Der Radius zählt in Kacheln, die Farbe ist der Ton des Kegels.</summary>
    private const float JewelRadius = 4.5f;
    private const float ExitRadius = 3.5f;
    private const float ExplosionRadius = 6.5f;

    private static readonly Color JewelLight = new(150, 195, 255);
    private static readonly Color ExitLight = new(255, 185, 110);
    private static readonly Color ExplosionLight = new(255, 240, 210);

    /// <summary>Der weiteste Lichtradius: So viele Kacheln über den Bildrand hinaus muss gesucht
    /// werden, damit ein Diamant knapp außerhalb noch hereinleuchtet.</summary>
    private const int LightMargin = 7;

    /// <summary>Kantenlänge der Verlaufstextur. Sie wird ohnehin auf Kachelmaße gestreckt und
    /// bilinear gefiltert — mehr Auflösung sähe man nicht.</summary>
    private const int GradientSize = 128;

    private readonly GraphicsDevice _device;
    private readonly Effect _effect;
    private readonly Texture2D _gradient;

    private RenderTarget2D? _scene;
    private RenderTarget2D? _overlay;
    private RenderTarget2D? _light;

    private Texture2D? _mask;
    private Color[] _maskPixels = [];
    private int _maskColumns;
    private int _maskRows;

    /// <summary>Wo das Cave-Fenster in der logischen Zeichenfläche liegt (Pixel) — aus der Maske
    /// übernommen und im Compose auf Bildschirmpixel umgerechnet.</summary>
    private Rectangle _playfield;

    public HorrorPostProcessor(GraphicsDevice device, string effectPath)
    {
        if (!File.Exists(effectPath))
        {
            throw new FileNotFoundException(
                $"Der Shader der Horror-Beleuchtung fehlt: {effectPath}. Er entsteht beim Bauen aus "
                + "BoulderDash.Game/Effects/*.fx (siehe Target CompileShaders) — 'dotnet build BoulderDash.slnx'.",
                effectPath);
        }

        _device = device;
        _effect = new Effect(device, File.ReadAllBytes(effectPath));
        _gradient = CreateGradient(device);

        // Der Nebel sieht aus wie der der Kachel-Sprites (Palette.Fog), nur stufenlos: dieselben
        // beiden Regler, hier auf 0..1 statt auf 0..255.
        _effect.Parameters["FogFloor"].SetValue((float)(Palette.FogFloor / 255.0));
        _effect.Parameters["FogContrast"].SetValue((float)Palette.FogContrast);
        _effect.Parameters["SmokeStrength"].SetValue(0.10f);
        _effect.Parameters["GrainStrength"].SetValue(0.09f);

        // Woran der Shader erkennt, was echtes Licht ist und was bloß die Grundhelligkeit.
        var ambient = ((Ambient.R * 0.299f) + (Ambient.G * 0.587f) + (Ambient.B * 0.114f)) / 255f;
        _effect.Parameters["AmbientLevel"].SetValue(ambient);
    }

    /// <summary>Die Szene — ein runder Lichtabfall, weiß. Welche Farbe ein Kegel bekommt, entscheidet
    /// erst der Farbton beim Zeichnen; hier steht nur die Form. Prozedural erzeugt, damit keine
    /// Bilddatei ins Repository muss (alle Assets sind Textdateien).</summary>
    private static Texture2D CreateGradient(GraphicsDevice device)
    {
        var texture = new Texture2D(device, GradientSize, GradientSize);
        var pixels = new Color[GradientSize * GradientSize];
        const float centre = (GradientSize - 1) / 2f;

        for (var y = 0; y < GradientSize; y++)
        {
            for (var x = 0; x < GradientSize; x++)
            {
                var dx = (x - centre) / centre;
                var dy = (y - centre) / centre;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));

                // Quadriert: ein heller Kern, der schnell weich wird — Kerzenlicht, kein Scheinwerfer.
                // Der Abfall steckt in den Farbkanälen, das Alpha bleibt 1: Additiv addiert MonoGame
                // Farbe mal Alpha, ein Verlauf in beidem würde ihn zweimal anwenden.
                var falloff = MathF.Max(0f, 1f - distance);
                var value = falloff * falloff;

                pixels[(y * GradientSize) + x] = new Color(value, value, value, 1f);
            }
        }

        texture.SetData(pixels);
        return texture;
    }

    /// <summary>Bindet die Zeichenfläche für die Kachelszene (schwarz gelöscht) und gibt sie zurück.</summary>
    public RenderTarget2D BeginScene(int width, int height)
    {
        Ensure(ref _scene, width, height);
        Ensure(ref _overlay, width, height);
        Ensure(ref _light, width, height);

        _device.SetRenderTarget(_scene);
        _device.Clear(Color.Black);
        return _scene!;
    }

    /// <summary>Bindet die Zeichenfläche für die Statuszeile — durchsichtig, denn sie wird später
    /// unberührt über das fertige Bild gelegt.</summary>
    public void BeginOverlay()
    {
        _device.SetRenderTarget(_overlay);
        _device.Clear(Color.Transparent);
    }

    /// <summary>
    /// Baut die Lichtkarte: die Grundhelligkeit, und darauf addiert die Kegel aller Lichtquellen im
    /// Bild. Gesucht wird ein Stück über den Bildrand hinaus (<see cref="LightMargin"/>) — ein
    /// Diamant knapp außerhalb wirft sein Licht trotzdem herein.
    ///
    /// Was verdeckt (ScreenCover) oder NIE GESEHEN ist, leuchtet nicht: Man sieht kein Licht durch
    /// eine Wand, und ein unentdeckter Diamant liegt im Schwarzen — sein Glanz wäre der halbe Weg,
    /// den man selbst gehen soll.
    ///
    /// Ein einmal gesehener Diamant glimmt dagegen weiter, auch wenn Rockford längst fort ist. Das
    /// ist der ganze Sinn der Sache: Im Dunkeln zieht das ferne Funkeln, und der Weg zurück ist eine
    /// Kette von Lichtern. Deshalb reicht hier "nicht unerkundet" — NICHT "gerade im Blickfeld",
    /// sonst leuchtete nur, was ohnehin schon vor der Nase liegt.
    /// </summary>
    public void BuildLightMap(SpriteBatch batch, Cave cave, Camera camera, GameState state, ScreenCover? cover, ExploreMap explore, double totalSeconds)
    {
        var layout = CaveRenderer.LayoutFor(cave, camera.Viewport);
        _playfield = new Rectangle(
            layout.OffsetX,
            CaveRenderer.StatusLineHeight + layout.OffsetY,
            layout.Columns * CaveRenderer.TileSize,
            layout.Rows * CaveRenderer.TileSize);

        _device.SetRenderTarget(_light);
        _device.Clear(Ambient);

        batch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);

        var left = Math.Max(0, camera.X - LightMargin);
        var top = Math.Max(0, camera.Y - LightMargin);
        var right = Math.Min(cave.Width - 1, camera.X + layout.Columns + LightMargin);
        var bottom = Math.Min(cave.Height - 1, camera.Y + layout.Rows + LightMargin);

        var time = (float)totalSeconds;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var element = cave.GetElement(x, y);
                if (element is not (Element.Jewel or Element.EscapeDoor or Element.Explosion or Element.JewelExplosion))
                {
                    continue;
                }

                if ((cover?.IsCovered(x, y) ?? false) || explore.Visibility(x, y) == TileVisibility.Hidden)
                {
                    continue;
                }

                switch (element)
                {
                    case Element.Jewel:
                        // Jeder Diamant flackert für sich: Der Positions-Hash verschiebt seine Phase,
                        // sodass ein Haufen Diamanten unruhig glitzert statt gemeinsam zu pulsieren.
                        var flicker = 0.78f + (0.22f * MathF.Sin((time * 2.7f) + Phase(x, y)));
                        DrawLight(batch, layout, camera, x, y, JewelRadius, JewelLight, flicker);
                        break;

                    case Element.EscapeDoor when state.ExitFlashOn:
                        // Der Ausgang schlägt im Takt seines eigenen Blinkens: Die Cave tauscht dafür
                        // die Palettenfarbe 0 (Cave.OpenEscapeDoor), und wenn sie hell steht, atmet
                        // auch sein Licht auf. Der Weg hinaus soll rufen.
                        var beat = state.PaletteColor0Override == Palette.ExitFlashBright ? 1.0f : 0.55f;
                        DrawLight(batch, layout, camera, x, y, ExitRadius, ExitLight, beat * (0.8f + (0.2f * MathF.Sin(time * 3.4f))));
                        break;

                    case Element.Explosion:
                    case Element.JewelExplosion:
                        DrawLight(batch, layout, camera, x, y, ExplosionRadius, ExplosionLight, 1f);
                        break;
                }
            }
        }

        batch.End();
    }

    /// <summary>Eine feste, aber unregelmäßige Phase je Kachel — dasselbe frac(sin(...)) wie im Shader,
    /// nur hier in C#. Kein Zufall: Derselbe Diamant flackert immer gleich.</summary>
    private static float Phase(int x, int y)
    {
        var v = MathF.Sin((x * 127.1f) + (y * 311.7f)) * 43758.5453f;
        return (v - MathF.Floor(v)) * MathF.Tau;
    }

    private void DrawLight(SpriteBatch batch, CaveRenderer.PlayfieldLayout layout, Camera camera, int x, int y, float radiusTiles, Color color, float intensity)
    {
        const int tile = CaveRenderer.TileSize;

        var centreX = layout.OffsetX + ((x - camera.X) * tile) + (tile / 2);
        var centreY = CaveRenderer.StatusLineHeight + layout.OffsetY + ((y - camera.Y) * tile) + (tile / 2);
        var radius = (int)(radiusTiles * tile);

        // Die Stärke gehört in die Farbkanäle, nicht ins Alpha (siehe CreateGradient) — sonst ginge
        // sie beim additiven Zeichnen quadratisch ein.
        var tint = new Color(
            color.R / 255f * intensity,
            color.G / 255f * intensity,
            color.B / 255f * intensity,
            1f);

        batch.Draw(
            _gradient,
            new Rectangle(centreX - radius, centreY - radius, radius * 2, radius * 2),
            tint);
    }

    /// <summary>Schreibt die Sichtbarkeitsmaske fort: ein Texel je Kachel des Bildes, R = erinnert
    /// (Nebel), G = nie gesehen. Der Shader liest sie bilinear — die Kachelgrenzen verschwimmen
    /// dabei, und der Nebel bekommt genau den weichen Rand, den er soll.</summary>
    public void UpdateMask(Cave cave, Camera camera, ExploreMap explore)
    {
        var layout = CaveRenderer.LayoutFor(cave, camera.Viewport);
        if (_mask is null || _maskColumns != layout.Columns || _maskRows != layout.Rows)
        {
            _mask?.Dispose();
            _maskColumns = layout.Columns;
            _maskRows = layout.Rows;
            _mask = new Texture2D(_device, _maskColumns, _maskRows);
            _maskPixels = new Color[_maskColumns * _maskRows];
        }

        for (var row = 0; row < _maskRows; row++)
        {
            for (var col = 0; col < _maskColumns; col++)
            {
                var visibility = explore.Visibility(camera.X + col, camera.Y + row);
                _maskPixels[(row * _maskColumns) + col] = visibility switch
                {
                    TileVisibility.Explored => new Color(255, 0, 0),
                    TileVisibility.Hidden => new Color(0, 255, 0),
                    _ => new Color(0, 0, 0),
                };
            }
        }

        // Die Textur hängt nach dem letzten Compose noch in ihrem Sampler; SetData duldet das nicht.
        // Welchen Platz der Shader ihr gegeben hat, ist seine Sache — beide Zusatzplätze räumen.
        _device.Textures[1] = null;
        _device.Textures[2] = null;
        _mask.SetData(_maskPixels);
    }

    /// <summary>Der Compose-Pass: Szene, Licht und Maske werden im Shader zusammengezogen und dabei in
    /// voller Fensterauflösung ins Zielrechteck gezeichnet. Erst hier entstehen Rauch und Korn —
    /// deshalb sind sie so fein wie der Bildschirm und nicht so grob wie eine Kachel.</summary>
    public void Compose(SpriteBatch batch, Rectangle destination, float scale, double totalSeconds)
    {
        // Die Zeit wird umgeschlagen: sin() verliert bei großen Argumenten seine Genauigkeit, und ein
        // stundenlang laufendes Spiel dürfte nicht anfangen zu zittern.
        _effect.Parameters["Time"].SetValue((float)(totalSeconds % 3600.0));
        _effect.Parameters["DestOrigin"].SetValue(new Vector2(destination.X, destination.Y));
        _effect.Parameters["DestSize"].SetValue(new Vector2(destination.Width, destination.Height));
        _effect.Parameters["PlayfieldOrigin"].SetValue(new Vector2(
            destination.X + (_playfield.X * scale),
            destination.Y + (_playfield.Y * scale)));
        _effect.Parameters["PlayfieldSize"].SetValue(new Vector2(
            _playfield.Width * scale,
            _playfield.Height * scale));
        _effect.Parameters["LightTexture"].SetValue(_light);
        _effect.Parameters["MaskTexture"].SetValue(_mask);

        // Die Kacheln bleiben scharf (PointClamp) — nur beim Verkleinern glättet Linear, sonst fiele
        // jede zweite Pixelzeile weg (dieselbe Überlegung wie in BoulderDashGame.Draw).
        var sampler = scale >= 1f ? SamplerState.PointClamp : SamplerState.LinearClamp;

        batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, sampler, effect: _effect);
        batch.Draw(_scene, destination, Color.White);
        batch.End();
    }

    /// <summary>Die Statuszeile über das fertige Bild — hell und unangetastet.</summary>
    public void DrawOverlay(SpriteBatch batch, Rectangle destination, float scale)
    {
        var sampler = scale >= 1f ? SamplerState.PointClamp : SamplerState.LinearClamp;

        batch.Begin(samplerState: sampler);
        batch.Draw(_overlay, destination, Color.White);
        batch.End();
    }

    private void Ensure(ref RenderTarget2D? target, int width, int height)
    {
        if (target is not null && target.Width == width && target.Height == height)
        {
            return;
        }

        target?.Dispose();
        target = new RenderTarget2D(_device, width, height);
    }

    public void Dispose()
    {
        _scene?.Dispose();
        _overlay?.Dispose();
        _light?.Dispose();
        _mask?.Dispose();
        _gradient.Dispose();
        _effect.Dispose();
    }
}
