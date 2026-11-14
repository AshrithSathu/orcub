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
        public Sprite uiPause;      // two rounded bars
        public Sprite uiHeartClay;  // puffy clay heart (lives)
        public Sprite uiCogClay;    // clay cog (currency)
        public Sprite uiSound;      // speaker glyph
        public Sprite uiHaptics;    // phone + vibration glyph
        public Sprite wreathArcA;   // world sprites with normal maps: the gear-wreath card frame
        public Sprite wreathArcB;
        public float wreathRadius = 3f;

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
