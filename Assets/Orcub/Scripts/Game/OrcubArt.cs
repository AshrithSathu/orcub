using UnityEngine;

namespace Orcub.Game
{
    /// <summary>All sprites, colours and tuning used to draw the board. Created by the editor builder.</summary>
    [CreateAssetMenu(menuName = "Orcub/Art Set", fileName = "OrcubArt")]
    public sealed class OrcubArt : ScriptableObject
    {
        [Header("Gear-ring sprites (gap pointing +X), one per authored radius")]
        [Tooltip("Tintable layer (takes the ring's palette colour).")]
        public Sprite[] ringSprites;
        [Tooltip("Optional untinted hardware layer drawn over the tintable layer (teeth, end caps). Same size/pivot.")]
        public Sprite[] ringTrimSprites;
        [Tooltip("Tube centre-line radius, in world units, that each ring sprite was rendered at.")]
        public float[] ringSpriteRadii;

        [Header("Holder parts (tube runs along local X; stem leaves along +Y for the mount, -Y for the clamp)")]
        public Sprite clamp;
        [Tooltip("Optional layer over the clamp tinted with the OWNER ring's colour, so you can see who holds what.")]
        public Sprite clampAccent;
        public Sprite mount;
        public Sprite stem;
        [Tooltip("Stem thickness in world units.")]
        public float stemWidth = 0.22f;

        [Header("Colours")]
        public Color[] ringPalette =
        {
            new Color(0.93f, 0.42f, 0.36f), new Color(0.97f, 0.73f, 0.30f), new Color(0.45f, 0.78f, 0.55f),
            new Color(0.36f, 0.66f, 0.90f), new Color(0.67f, 0.52f, 0.90f), new Color(0.95f, 0.55f, 0.72f),
            new Color(0.40f, 0.80f, 0.80f), new Color(0.85f, 0.85f, 0.45f), new Color(0.98f, 0.60f, 0.40f),
            new Color(0.60f, 0.72f, 0.95f),
        };
        public Color hardwareTint = Color.white;
        public Color background = new Color(0.07f, 0.08f, 0.14f);
        public Color backgroundGlow = new Color(0.13f, 0.15f, 0.26f);

        [Header("Logo (splash)")]
        [Tooltip("Clay letters r, c, u, b (white albedo + normal map, tinted at runtime).")]
        public Sprite[] logoLetters;
        [Tooltip("Centre of each letter in word space (world units), from the Blender layout.")]
        public Vector2[] logoLetterCenters;
        [Tooltip("Palette index per letter.")]
        public int[] logoLetterColors = { 1, 2, 3, 4 };
        [Tooltip("Centre and height of the 'o' slot in word space; the gear-ring fills it.")]
        public Vector2 logoOCenter;
        public float logoOHeight = 1.9f;

        [Header("UI")]
        public Font uiFont;

        [Header("UI kit (approved: #1 top bar / buttons / stars, #4 gear-wreath cards)")]
        public Sprite uiSlab;       // cream clay slab, sliced horizontally
        public Sprite uiPill;       // puffy clay pill, sliced horizontally, tinted per button
        public Sprite uiRound;      // round puffy clay button
        public Sprite uiStarClay;   // neutral clay star: tinted gold (earned) or tan (unearned)
        public Sprite uiFilm;       // film/play glyph (watch ad)
        public Sprite uiCard;       // cream clay card for menus, sliced on all four sides
        public Sprite uiPause;      // two rounded bars
        public Sprite uiHeartClay;  // puffy clay heart (lives)
        public Sprite uiCogClay;    // clay cog (currency)
        public Sprite uiSound;      // speaker glyph
        public Sprite uiHaptics;    // phone + vibration glyph
        public Sprite wreathArcA;   // world sprites with normal maps: the gear-wreath card frame
        public Sprite wreathArcB;
        public float wreathRadius = 3f;

        [Header("Home, map, boosters (Blender home kit)")]
        public Sprite homeGearRing;      // closed clay gear ring: loader progress + map nodes + goal icons
        public Sprite homeBead;          // map path dots
        public Sprite homeLock;
        public Sprite iconHint, iconUndo, iconUnclamp;
        public Sprite workshopBench;
        public Vector2 workshopBenchPos, workshopBenchSize;
        public string[] workshopIds = new string[0];
        public Sprite[] workshopRestored = new Sprite[0];
        public Sprite[] workshopBroken = new Sprite[0];
        public Vector2[] workshopPos = new Vector2[0];
        public Vector2[] workshopSize = new Vector2[0];
        public Vector2 workshopBox = new Vector2(1000, 900);

        [Header("Navigation v3 (approved: home A, chapters A, map A, level start B)")]
        public Sprite navSquare;        // cream rounded-square button base (back / settings)
        public Sprite glyphBack, glyphClose, glyphChevron, glyphPath;
        public Sprite starSocket;       // empty star pressed into clay
        public Sprite mapNode;          // tintable gear node
        public Sprite mapNodeCenter;    // cream centre disc for the number
        public Sprite nodeGlow;         // soft halo behind the current node
        public Sprite bubble;           // "Play" speech bubble, sliced left/right
        public Sprite sheet;            // level-start bottom sheet, sliced
        public Sprite homeHero;         // connected clay gear-rings illustration
        public Sprite chapterLamp;      // chapter 1 card art

        [Header("Gear mechanics kit (approved features_v1; lit board pieces + pre-lit UI icons)")]
        [Tooltip("Tube centre-line radius each per-radius decal set was rendered at (1.0, 1.2, 1.4).")]
        public float[] mechRadii = new float[0];
        public Sprite[] ratchetCcw = new Sprite[0];   // per radius: left-pointing at the top = counter-clockwise
        public Sprite[] ratchetCw = new Sprite[0];    // per radius: mirrored copy (albedo + red-flipped normal)
        [Tooltip("Ring stamps, index = radiusIndex * Shapes.Length + shape.")]
        public Sprite[] ringStamps = new Sprite[0];
        public Sprite[] clampStamps = new Sprite[0];  // per shape: replacement clamp with the stamp pressed in
        public Sprite[] rustPatchA = new Sprite[0], rustPatchB = new Sprite[0], rustSeized = new Sprite[0];   // per radius
        public Sprite linkedMark, key, padlockLocked, padlockUnlocked, oilCan, movesBadge, idBadge;
        public Sprite springClosed, springOpen, springBadge, clayIntact, clayCracked;
        [Tooltip("Whitened copies of the clay shell, tinted cream at runtime so the coat reads against the tan clamp.")]
        public Sprite clayIntactLight, clayCrackedLight;
        public Sprite bossBadge, goldBar;
        public string[] introKeys = new string[0];
        public Sprite[] introIcons = new Sprite[0];

        /// <summary>Shape stamps for colour clamps, in kit order. Never colour alone: each palette index has a shape.</summary>
        public static readonly string[] Shapes = { "triangle", "square", "star", "diamond", "circle", "heart" };

        /// <summary>Palette index → shape (coral 0 = triangle and lavender 4 = circle, as in the approved board).</summary>
        public static int ShapeFor(int paletteIndex) => Mathf.Abs(paletteIndex) % Shapes.Length;

        public int MechRadiusIndex(float radius)
        {
            int best = 0;
            for (int i = 1; i < mechRadii.Length; i++)
                if (Mathf.Abs(mechRadii[i] - radius) < Mathf.Abs(mechRadii[best] - radius)) best = i;
            return best;
        }

        public Sprite PerRadius(Sprite[] set, float radius)
        {
            if (set == null || set.Length == 0) return null;
            return set[Mathf.Min(MechRadiusIndex(radius), set.Length - 1)];
        }

        public Sprite RingStamp(int shape, float radius)
        {
            if (ringStamps == null || ringStamps.Length < Shapes.Length) return null;
            int r = Mathf.Min(MechRadiusIndex(radius), ringStamps.Length / Shapes.Length - 1);
            return ringStamps[r * Shapes.Length + shape];
        }

        [System.NonSerialized] Sprite _bossTight, _goldTight;

        /// <summary>The boss badge cropped to its pill (the kit icon is a padded square), so it sizes like text.</summary>
        public Sprite BossBadgeTight => _bossTight != null ? _bossTight : (_bossTight = Tight(bossBadge, new Rect(21, 81, 198, 78)));

        /// <summary>The gold bar cropped to the bar.</summary>
        public Sprite GoldBarTight => _goldTight != null ? _goldTight : (_goldTight = Tight(goldBar, new Rect(27, 68, 205, 120)));

        static Sprite Tight(Sprite s, Rect r)
        {
            if (s == null) return null;
            var rect = new Rect(s.rect.x + r.x, s.rect.y + r.y, r.width, r.height);
            if (rect.xMax > s.texture.width || rect.yMax > s.texture.height) return s;
            return Sprite.Create(s.texture, rect, new Vector2(0.5f, 0.5f), s.pixelsPerUnit);
        }

        public Sprite IntroIcon(string key)
        {
            for (int i = 0; i < introKeys.Length && i < introIcons.Length; i++)
                if (introKeys[i] == key) return introIcons[i];
            return null;
        }

        [Header("Background")]
        [Tooltip("Optional full-screen backdrop, scaled to cover the camera view.")]
        public Sprite backgroundSprite;

        [Header("Sound")]
        [Range(0f, 1f)] public float masterVolume = 0.9f;
        public AudioClip sfxTick;
        public AudioClip sfxPick;
        public AudioClip sfxClunk;
        public AudioClip sfxThud;
        public AudioClip sfxWhoosh;
        public AudioClip sfxChime;

        [Header("Rendering")]
        public Material spriteMaterial;
        [Tooltip("Unlit material for the backdrop so 2D lights don't double-shade a painted background.")]
        public Material unlitMaterial;

        public Color RingColor(int index) =>
            ringPalette == null || ringPalette.Length == 0 ? Color.white : ringPalette[Mathf.Abs(index) % ringPalette.Length];

        /// <summary>Picks the closest pre-rendered ring and the uniform scale needed to hit <paramref name="radius"/>.</summary>
        public Sprite RingSprite(float radius, out float scale, out Sprite trim)
        {
            int best = 0;
            for (int i = 1; i < ringSpriteRadii.Length; i++)
                if (Mathf.Abs(ringSpriteRadii[i] - radius) < Mathf.Abs(ringSpriteRadii[best] - radius))
                    best = i;
            scale = radius / ringSpriteRadii[best];
            trim = ringTrimSprites != null && best < ringTrimSprites.Length ? ringTrimSprites[best] : null;
            return ringSprites[best];
        }
    }
}
