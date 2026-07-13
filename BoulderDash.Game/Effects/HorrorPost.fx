// Der Compose-Pass der Horror-Beleuchtung (siehe HorrorPostProcessor).
//
// Er zieht die fertig gezeichnete Kachelszene, die Lichtkarte und die Sichtbarkeitsmaske zusammen
// und legt Nebel, Rauch und Korn darüber. Entscheidend: Gerechnet wird in BILDSCHIRMPIXELN, nicht
// in Kacheln — die Kachelgrafik bleibt grobpixelig wie im Original, die Effekte darüber sind so
// fein wie das Fenster groß ist und richten sich an keinem Kachelrand aus.
//
// Der Shader hängt an SpriteBatch: Die Technique hat NUR einen Pixelshader, den Vertexshader samt
// Projektionsmatrix stellt der SpriteBatch. Die Szene liegt deshalb als dessen Batch-Textur auf s0,
// Licht und Maske kommen als eigene Parameter dazu.
//
// Kompiliert wird mit /Profile:OpenGL (ps_3_0 über MojoShader): keine Integer-/Bit-Operationen,
// keine dynamischen Schleifen. Alles Rauschen ist deshalb sin/frac-Hash und fest ausgerollt.

#if OPENGL
    #define PS_SHADERMODEL ps_3_0
#else
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

/// Laufende Zeit in Sekunden (vom Prozessor modulo gehalten, damit sin() genau bleibt).
float Time;

/// Das Zielrechteck auf dem Bildschirm — daraus wird aus der Textur-UV die Bildschirmposition:
/// screenPx = DestOrigin + uv * DestSize. Alle Effekte rechnen in diesen Pixeln.
float2 DestOrigin;
float2 DestSize;

/// Das gezeichnete Cave-Fenster im Zielrechteck (Bildschirmpixel). Alles außerhalb — Statuszeile
/// und der schwarze Rand bei Caves, die kleiner sind als das Sichtfenster — bleibt unberührt.
float2 PlayfieldOrigin;
float2 PlayfieldSize;

float SmokeStrength;

/// Die Filmkörnung des Erinnerten (siehe FilmGrain): wie groß ein Korn in Bildschirmpixeln ist, wie
/// stark es die Helligkeit moduliert (die Dichte der Emulsion), wie viel es dem Schwarz obendrein
/// hinzufügt, und wie unruhig das Filmbild belichtet wird.
float GrainSize;
float GrainDensity;
float GrainStrength;
float GrainFlicker;

/// Nebeloptik wie Palette.Fog (Core): entsättigen, dann auf einen dunklen, flauen Bereich stauchen.
/// Dort geschieht das je Farbe der 4er-Palette, hier stufenlos je Pixel — dasselbe Bild, weiche Kante.
float FogFloor;
float FogContrast;

/// Die Helligkeit, die die Lichtkarte überall hat (HorrorPostProcessor.Ambient). Was darüber liegt,
/// ist echtes Licht — daran misst sich, wie weit der Nebel zurückweicht und wie dicht der Rauch steht.
float AmbientLevel;

/// Die Szene. Sie kommt vom SpriteBatch, der sie IMMER auf Platz 0 legt — gesetzt wird SceneTexture
/// von außen deshalb nie. Trotzdem muss sie hier stehen wie die anderen beiden: MonoGame verteilt die
/// Sampler-Plätze über die Texturparameter, und ein Sampler ohne Parameter bringt die Zählung
/// durcheinander — dann läse die Lichtkarte still die Szene.
texture SceneTexture;
sampler2D SceneSampler : register(s0) = sampler_state
{
    Texture = <SceneTexture>;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

/// Die Lichtkarte (Grundhelligkeit plus die Lichtkegel der Diamanten, des Ausgangs, der Explosionen).
/// Der Platz ist ausdrücklich vergeben: Wird er dem Übersetzer überlassen, landen die Zusatztexturen
/// nicht dort, wo MonoGame sie hinbindet, und der Shader läse stumm die Szene ein zweites Mal.
texture LightTexture;
sampler2D LightSampler : register(s1) = sampler_state
{
    Texture = <LightTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

/// Die Sichtbarkeitsmaske: ein Texel je Kachel des Fensters, R = Nebel (erinnert), G = unerkundet.
/// Linear gesampelt — genau daraus entstehen die weichen, kachelunabhängigen Nebelgrenzen.
texture MaskTexture;
sampler2D MaskSampler : register(s2) = sampler_state
{
    Texture = <MaskTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

float Hash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
}

/// Wertrauschen: die vier Gitterecken glatt (smoothstep) ineinander geblendet.
float ValueNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    float2 w = f * f * (3.0 - (2.0 * f));

    float a = Hash(cell);
    float b = Hash(cell + float2(1.0, 0.0));
    float c = Hash(cell + float2(0.0, 1.0));
    float d = Hash(cell + float2(1.0, 1.0));

    return lerp(lerp(a, b, w.x), lerp(c, d, w.x), w.y);
}

/// Drei Oktaven, von Hand ausgerollt (ps_3_0 mag keine dynamischen Schleifen).
float Fbm(float2 p)
{
    float v = 0.5 * ValueNoise(p);
    v += 0.25 * ValueNoise(p * 2.03);
    v += 0.125 * ValueNoise(p * 4.01);

    return v / 0.875;
}

float Luminance(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

/// Silberkorn, wie es analoger Film hat: mehrere Lagen unterschiedlich feiner Körner übereinander,
/// nicht ein Rauschwert je Bildschirmpixel. Ein Korn ist ein paar Pixel groß und weich — es klumpt,
/// statt zu flimmern.
///
/// Ausgewürfelt wird je FILMBILD, nicht je Bildschirmbild: Der Übergabewert ist die auf 24 Bilder je
/// Sekunde gerundete Zeit. Daher das ruhige "Kochen" des Korns statt eines nervösen Digitalrauschens
/// im Monitortakt — das Korn gehört dem Film, nicht dem Bildschirm.
float FilmGrain(float2 screen, float frame)
{
    float2 p = screen / max(GrainSize, 0.5);
    float2 drift = float2(frame * 17.13, frame * 31.71);

    // Die grobe Lage trägt das Bild; die feineren geben ihr nur Struktur. Zu viel Gewicht auf ihnen,
    // und aus dem Korn wird wieder Digitalrauschen.
    float g = ValueNoise(p + drift);
    g += 0.40 * ValueNoise((p * 2.17) - drift.yx);
    g += 0.12 * ValueNoise((p * 4.31) + drift.yx);

    return (g / 1.52) - 0.5;
}

float4 MainPS(float4 tint : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float3 scene = tex2D(SceneSampler, uv).rgb;

    float2 screen = DestOrigin + (uv * DestSize);
    float2 maskUv = (screen - PlayfieldOrigin) / PlayfieldSize;

    // Außerhalb des Cave-Fensters wird die Szene unangetastet durchgereicht: Die Statuszeile kommt
    // ohnehin erst nach diesem Pass darüber, der Rand ist schwarz und soll schwarz bleiben.
    float inside = step(0.0, maskUv.x) * step(maskUv.x, 1.0)
                 * step(0.0, maskUv.y) * step(maskUv.y, 1.0);

    float2 mask = tex2D(MaskSampler, maskUv).rg;
    float fog = mask.r;
    float hidden = mask.g;

    float3 light = tex2D(LightSampler, uv).rgb;

    // Wie viel echtes Licht hier ankommt — die Grundhelligkeit zählt nicht mit, die liegt überall.
    float glow = saturate((Luminance(light) - AmbientLevel) * 2.5);

    // Erinnertes Gelände: entsättigt und flau — man weiß, was dort liegt, sieht es aber nicht mehr.
    // Wo aber ein Diamant hinleuchtet, weicht der Nebel: Man SIEHT wieder, statt sich zu erinnern.
    // Genau das macht die Diamanten zur Lichtquelle und nicht bloß zu hellen Flecken.
    float lightness = (max(max(scene.r, scene.g), scene.b) + min(min(scene.r, scene.g), scene.b)) * 0.5;
    float3 grey = saturate(FogFloor + (lightness * FogContrast)).xxx;

    // Wie viel Erinnerung an dieser Stelle übrig ist, nachdem das Licht seinen Teil zurückgeholt hat.
    // Der Schleier trägt auch die Körnung: Wo ein Diamant hinleuchtet, tritt sie mit ihm zurück.
    float veil = fog * (1.0 - glow);

    float3 base = lerp(scene, grey, veil) * (1.0 - hidden);
    float3 lit = base * light;

    // Rauch: zwei gegeneinander treibende Rauschfelder, sichtbar nur dort, wo wenig Licht ist.
    // Unerkundetes bleibt reinschwarz — dort verriete das Wabern die Form der Höhle.
    float darkness = 1.0 - glow;
    float smoke = Fbm((screen / 90.0) + float2(Time * 0.05, Time * 0.02))
                * Fbm((screen / 160.0) - float2(Time * 0.02, Time * 0.035));
    lit += smoke * SmokeStrength * darkness * (1.0 - hidden) * float3(0.55, 0.62, 0.75);

    // Das Erinnerte liegt da wie ein alter, unterbelichteter Film: entsättigt, körnig, unruhig.
    float frame = floor(Time * 24.0);
    float grain = FilmGrain(screen, frame);

    // Das Korn sitzt im Mittelton — im tiefen Schwarz und im hellen Licht tritt es zurück, wie die
    // Dichtekurve einer Emulsion. Es MODULIERT die Helligkeit, statt bloß Rauschen aufzuaddieren:
    // Genau das unterscheidet Filmkorn von Digitalrauschen.
    float lum = Luminance(lit);
    float response = saturate(lum * 8.0) * (1.0 - (0.5 * lum));
    lit *= 1.0 + (grain * GrainDensity * response * veil);

    // Eine Spur obendrauf, damit das Korn auch dort noch lebt, wo kaum Licht ankommt — sonst läge das
    // Erinnerte im Dunkeln glatt und tot da.
    lit += grain * GrainStrength * veil;

    // Torflimmern: Jedes Filmbild wird ein Quäntchen anders belichtet. Das nimmt dem Nebel das
    // Digitale endgültig — er atmet.
    lit *= 1.0 + ((Hash(float2(frame, 17.0)) - 0.5) * GrainFlicker * veil);

    return float4(lerp(scene, saturate(lit), inside), 1.0) * tint;
}

technique HorrorComposite
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
