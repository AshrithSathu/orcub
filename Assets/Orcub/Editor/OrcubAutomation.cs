using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Orcub.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Orcub.EditorTools
{
    /// <summary>
    /// Editor-side tooling for Orcub:
    ///  - Menu: Orcub/Import Art, Orcub/Build Game Scene, Orcub/Capture Preview, Orcub/Run Tests.
    ///  - A tiny file command channel (ArtSource/.automation/cmd.txt) so external tools / agents can drive the
    ///    open editor without a live MCP connection. Each line is a command; results go to result.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class OrcubAutomation
    {
        public const string SpritesDir = "Assets/Orcub/Art/Sprites";
        public const string ArtAssetPath = "Assets/Orcub/Art/OrcubArt.asset";
        public const string MaterialPath = "Assets/Orcub/Art/OrcubSpriteLit.mat";
        public const string ScenePath = "Assets/Scenes/Game.unity";

        static readonly string ProjectRoot = Directory.GetParent(Application.dataPath).FullName;
        static readonly string AutomationDir = Path.Combine(ProjectRoot, "ArtSource", ".automation");
        static string CmdFile => Path.Combine(AutomationDir, "cmd.txt");
        static string ResultFile => Path.Combine(AutomationDir, "result.txt");

        static double _nextPoll;
        static readonly Queue<string> Pending = new Queue<string>();

        static OrcubAutomation()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        // ------------------------------------------------------------------ command channel

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 0.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            if (Pending.Count == 0 && File.Exists(CmdFile))
            {
                var lines = File.ReadAllLines(CmdFile);
                File.Delete(CmdFile);
                foreach (var l in lines)
                    if (!string.IsNullOrWhiteSpace(l)) Pending.Enqueue(l.Trim());
                Log($"--- received {Pending.Count} command(s) at {DateTime.Now:HH:mm:ss}");
            }
            if (Pending.Count == 0) return;

            var cmd = Pending.Dequeue();
            try
            {
                Run(cmd);
            }
            catch (Exception e)
            {
                Log($"FAIL {cmd}: {e}");
            }
        }

        static void Run(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2);
            string arg = parts.Length > 1 ? parts[1] : null;
            switch (parts[0])
            {
                case "refresh":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    Log("OK refresh");
                    break;
                case "import-art": ImportArt(); break;
                case "build-scene": BuildScene(); break;
                case "test": SelfTest(); break;
                case "test-runner": RunTests(); break;
                case "capture": Capture(arg ?? Path.Combine(AutomationDir, "capture.png"), 1080, 1920); break;
                case "wait":
                    _nextPoll = EditorApplication.timeSinceStartup + float.Parse(arg ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                    Log("OK wait " + arg);
                    break;
                case "autosolve":
                {
                    var board = UnityEngine.Object.FindAnyObjectByType<BoardView>();
                    if (!EditorApplication.isPlaying || board == null) throw new Exception("autosolve needs play mode");
                    board.StartAutoSolve(float.Parse(arg ?? "0.6", System.Globalization.CultureInfo.InvariantCulture));
                    Log("OK autosolve");
                    break;
                }
                case "play":
                    EditorApplication.isPlaying = true;
                    Log("OK play");
                    break;
                case "stop":
                    EditorApplication.isPlaying = false;
                    Log("OK stop");
                    break;
                default:
                    Log($"FAIL unknown command '{line}'");
                    break;
            }
        }

        static void Log(string message)
        {
            Directory.CreateDirectory(AutomationDir);
            File.AppendAllText(ResultFile, message + "\n");
            Debug.Log("[Orcub] " + message);
        }

        // ------------------------------------------------------------------ art import

        [Serializable]
        class SpriteMeta
        {
            public string file;
            public string normal;  // optional normal map rendered alongside the sprite
            public float ppu = 200f;
            public float pivotX = 0.5f, pivotY = 0.5f;
            public float borderL, borderB, borderR, borderT;
            public float radius;   // ring sprites: tube centre-line radius in world units
            public string role;    // ring | clamp | mount | stem
        }

        [Serializable]
        class SpriteMetaList
        {
            public List<SpriteMeta> sprites = new List<SpriteMeta>();
        }

        [MenuItem("Orcub/Import Art")]
        public static void ImportArt()
        {
            var metaPath = Path.Combine(ProjectRoot, SpritesDir, "sprites.json");
            if (!File.Exists(metaPath)) throw new FileNotFoundException("Render the Blender sprites first", metaPath);
            var metas = JsonUtility.FromJson<SpriteMetaList>(File.ReadAllText(metaPath)).sprites;

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // Normal maps first, so sprites can reference them as their _NormalMap secondary texture.
            foreach (var m in metas.Where(m => !string.IsNullOrEmpty(m.normal)))
            {
                var ni = (TextureImporter)AssetImporter.GetAtPath($"{SpritesDir}/{m.normal}");
                if (ni == null) throw new Exception("Missing normal map " + m.normal);
                ni.textureType = TextureImporterType.NormalMap;
                ni.mipmapEnabled = false;
                ni.filterMode = FilterMode.Bilinear;
                ni.maxTextureSize = 2048;
                ni.SaveAndReimport();
            }

            foreach (var m in metas)
            {
                string path = $"{SpritesDir}/{m.file}";
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                if (ti == null) throw new Exception("Missing sprite " + path);
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.alphaIsTransparency = true;
                ti.mipmapEnabled = false;
                ti.filterMode = FilterMode.Bilinear;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.maxTextureSize = 2048;
                var settings = new TextureImporterSettings();
                ti.ReadTextureSettings(settings);
                settings.spritePixelsPerUnit = m.ppu;
                settings.spriteAlignment = (int)SpriteAlignment.Custom;
                settings.spritePivot = new Vector2(m.pivotX, m.pivotY);
                settings.spriteMeshType = m.role == "stem" ? SpriteMeshType.FullRect : SpriteMeshType.Tight;
                settings.spriteBorder = new Vector4(m.borderL, m.borderB, m.borderR, m.borderT);
                ti.SetTextureSettings(settings);
                if (!string.IsNullOrEmpty(m.normal))
                {
                    var normalTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{SpritesDir}/{m.normal}");
                    ti.secondarySpriteTextures = new[] { new SecondarySpriteTexture { name = "_NormalMap", texture = normalTex } };
                }
                ti.SaveAndReimport();
            }

            // Lit sprites: albedo + normal map, lit live by the scene's 2D lights so shading stays put while rings turn.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "OrcubSpriteLit" };
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.shader = shader;
            EditorUtility.SetDirty(mat);

            var art = AssetDatabase.LoadAssetAtPath<OrcubArt>(ArtAssetPath);
            if (art == null)
            {
                art = ScriptableObject.CreateInstance<OrcubArt>();
                AssetDatabase.CreateAsset(art, ArtAssetPath);
            }

            Sprite Load(SpriteMeta m) => AssetDatabase.LoadAssetAtPath<Sprite>($"{SpritesDir}/{m.file}");
            var rings = metas.Where(m => m.role == "ring").OrderBy(m => m.radius).ToList();
            art.ringSprites = rings.Select(Load).ToArray();
            art.ringSpriteRadii = rings.Select(m => m.radius).ToArray();
            art.ringTrimSprites = Array.Empty<Sprite>();
            art.clamp = Load(metas.First(m => m.role == "clamp"));
            art.mount = Load(metas.First(m => m.role == "mount"));
            var stemMeta = metas.First(m => m.role == "stem");
            art.stem = Load(stemMeta);
            art.stemWidth = art.stem.rect.height / stemMeta.ppu;
            var accentMeta = metas.FirstOrDefault(m => m.role == "clamp_accent");
            art.clampAccent = accentMeta != null ? Load(accentMeta) : null;
            art.hardwareTint = Hex("D9AE86"); // warm tan clay, as in the chosen concept
            art.spriteMaterial = mat;
            ImportAudioAndBackdrop(art);
            ImportLogo(art);
            ImportIcon();
            art.uiFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Orcub/Fonts/Fredoka-Bold.ttf");
            ImportUiKit(art);
            ImportHomeKit(art);
            // Soft-clay palette: saturated enough to read on navy, chalky enough to feel like clay.
            art.ringPalette = new[]
            {
                Hex("F2836B"), Hex("F6C35B"), Hex("7CCB8E"), Hex("6FB3E8"), Hex("B39AE8"),
                Hex("F29DBB"), Hex("5FC7C0"), Hex("E8E07A"), Hex("F5A262"), Hex("93A7F0"),
            };
            art.background = Hex("191C2E");
            art.backgroundGlow = Hex("262B47");
            EditorUtility.SetDirty(art);
            AssetDatabase.SaveAssets();
            Log($"OK import-art: {rings.Count} ring sprites, stem width {art.stemWidth:F3}");
        }

        public const string AudioDir = "Assets/Orcub/Audio";
        public const string BackgroundDir = "Assets/Orcub/Art/Backgrounds";
        public const string UnlitMaterialPath = "Assets/Orcub/Art/OrcubSpriteUnlit.mat";
        /// <summary>Which painted backdrop to use (bg_1 teal play-mat, bg_2 plum felt, bg_3 slate pegboard).</summary>
        public const string ChosenBackground = "bg_2.png";

        static void ImportAudioAndBackdrop(OrcubArt art)
        {
            AudioClip Clip(string file)
            {
                string path = $"{AudioDir}/{file}";
                var ai = (AudioImporter)AssetImporter.GetAtPath(path);
                if (ai == null) { Debug.LogWarning("[Orcub] missing sound " + path); return null; }
                var settings = ai.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad; // tiny clips, zero-latency playback
                settings.compressionFormat = AudioCompressionFormat.PCM;
                settings.preloadAudioData = true;
                ai.defaultSampleSettings = settings;
                ai.forceToMono = true;
                ai.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            art.sfxTick = Clip("tick.wav");
            art.sfxPick = Clip("pick.wav");
            art.sfxClunk = Clip("clunk.wav");
            art.sfxThud = Clip("thud.wav");
            art.sfxWhoosh = Clip("whoosh.wav");
            art.sfxChime = Clip("chime.wav");

            foreach (var file in Directory.GetFiles(Path.Combine(ProjectRoot, BackgroundDir), "*.png"))
            {
                string path = $"{BackgroundDir}/{Path.GetFileName(file)}";
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                if (ti == null) continue;
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.mipmapEnabled = false;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.spritePixelsPerUnit = 100;
                ti.SaveAndReimport();
            }
            art.backgroundSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{BackgroundDir}/{ChosenBackground}");

            var unlit = AssetDatabase.LoadAssetAtPath<Material>(UnlitMaterialPath);
            if (unlit == null)
            {
                unlit = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")) { name = "OrcubSpriteUnlit" };
                AssetDatabase.CreateAsset(unlit, UnlitMaterialPath);
            }
            unlit.shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            EditorUtility.SetDirty(unlit);
            art.unlitMaterial = unlit;
        }

        public const string LogoDir = "Assets/Orcub/Art/Logo";
        public const string IconPath = "Assets/Orcub/Art/Icon/icon_1024.png";
        public const string LaunchPath = "Assets/Orcub/Art/Launch/launch.png";

        [Serializable] class LogoLetter { public string file, normal, letter; public float cx, cy, ppu = 200f; }
        [Serializable] class LogoO { public float cx, cy, width, height; }
        [Serializable] class LogoMeta { public List<LogoLetter> letters = new List<LogoLetter>(); public LogoO o; }

        static Sprite ImportLitSprite(string path, string normalPath, float ppu)
        {
            Texture2D normal = null;
            if (!string.IsNullOrEmpty(normalPath))
            {
                var ni = (TextureImporter)AssetImporter.GetAtPath(normalPath);
                ni.textureType = TextureImporterType.NormalMap;
                ni.mipmapEnabled = false;
                ni.SaveAndReimport();
                normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            }
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            var settings = new TextureImporterSettings();
            ti.ReadTextureSettings(settings);
            settings.spritePixelsPerUnit = ppu;
            settings.spriteAlignment = (int)SpriteAlignment.Center;
            settings.spriteMeshType = SpriteMeshType.Tight;
            ti.SetTextureSettings(settings);
            if (normal != null)
                ti.secondarySpriteTextures = new[] { new SecondarySpriteTexture { name = "_NormalMap", texture = normal } };
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static void ImportLogo(OrcubArt art)
        {
            var jsonPath = Path.Combine(ProjectRoot, LogoDir, "logo.json");
            if (!File.Exists(jsonPath)) { Debug.LogWarning("[Orcub] no logo.json; splash disabled"); return; }
            var meta = JsonUtility.FromJson<LogoMeta>(File.ReadAllText(jsonPath));
            var sprites = new List<Sprite>();
            var centres = new List<Vector2>();
            foreach (var l in meta.letters)
            {
                sprites.Add(ImportLitSprite($"{LogoDir}/{l.file}", $"{LogoDir}/{l.normal}", l.ppu));
                centres.Add(new Vector2(l.cx, l.cy));
            }
            art.logoLetters = sprites.ToArray();
            art.logoLetterCenters = centres.ToArray();
            art.logoLetterColors = new[] { 1, 2, 3, 4 }; // r yellow, c mint, u sky, b lavender (o is coral = 0)
            art.logoOCenter = new Vector2(meta.o.cx, meta.o.cy);
            art.logoOHeight = meta.o.height;
        }

        /// <summary>UI kit from build_ui_kit.py: pre-lit sliced UI images + normal-mapped wreath arcs.</summary>
        static void ImportUiKit(OrcubArt art)
        {
            const string dir = "Assets/Orcub/Art/UI";
            var jsonPath = Path.Combine(ProjectRoot, dir, "ui_kit.json");
            if (!File.Exists(jsonPath)) { Debug.LogWarning("[Orcub] ui_kit.json missing"); return; }
            var json = File.ReadAllText(jsonPath);
            int Border(string key)
            {
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\{[^}]*\"border\"\\s*:\\s*(\\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
            Sprite Ui(string name, bool slicedH)
            {
                var s = ImportUiIcon($"{dir}/{name}.png");
                if (s == null || !slicedH) return s;
                var ti = (TextureImporter)AssetImporter.GetAtPath($"{dir}/{name}.png");
                int b = Border(name);
                var settings = new TextureImporterSettings();
                ti.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;   // sliced sprites need full rects
                settings.spriteBorder = new Vector4(b, 0, b, 0);      // stretch horizontally only
                ti.SetTextureSettings(settings);
                ti.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<Sprite>($"{dir}/{name}.png");
            }
            art.uiSlab = Ui("ui_slab", true);
            art.uiPill = Ui("ui_pill", true);
            art.uiRound = Ui("ui_round", false);
            art.uiStarClay = Ui("ui_star", false);
            art.uiFilm = Ui("ui_film", false);
            art.uiPause = Ui("ui_pause", false);
            art.uiCard = Ui("ui_card", false);
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath($"{dir}/ui_card.png");
                var settings = new TextureImporterSettings();
                ti.ReadTextureSettings(settings);
                int b = Border("ui_card");
                settings.spriteMeshType = SpriteMeshType.FullRect;
                settings.spriteBorder = new Vector4(b, b, b, b);      // corners stay round at any size
                ti.SetTextureSettings(settings);
                ti.SaveAndReimport();
                art.uiCard = AssetDatabase.LoadAssetAtPath<Sprite>($"{dir}/ui_card.png");
            }
            art.uiHeartClay = Ui("ui_heart", false);
            art.uiCogClay = Ui("ui_cog", false);
            art.uiSound = Ui("ui_sound", false);
            art.uiHaptics = Ui("ui_haptics", false);
            art.wreathArcA = ImportLitSprite($"{dir}/wreath_arc_a.png", $"{dir}/wreath_arc_a_n.png", 200f);
            art.wreathArcB = ImportLitSprite($"{dir}/wreath_arc_b.png", $"{dir}/wreath_arc_b_n.png", 200f);
            art.wreathRadius = 3f;
        }

        [Serializable] class KitPiece { public string name; public float x, y, width, height; }
        [Serializable] class KitLayout { public float[] reference_size; public KitPiece[] pieces; }
        [Serializable] class HomeKitMeta { public KitLayout workshop_layout; }

        /// <summary>Home / map / booster art from build_home_kit.py (pre-lit UI images + the workshop layout).</summary>
        static void ImportHomeKit(OrcubArt art)
        {
            const string dir = "Assets/Orcub/Art/Home";
            var jsonPath = Path.Combine(ProjectRoot, dir, "home_kit.json");
            if (!File.Exists(jsonPath)) { Debug.LogWarning("[Orcub] home_kit.json missing"); return; }
            Sprite S(string n) => ImportUiIcon($"{dir}/{n}.png");
            art.homeGearRing = S("gear_ring");
            art.homeBead = S("bead");
            art.homeLock = S("lock");
            art.iconHint = S("icon_hint");
            art.iconUndo = S("icon_undo");
            art.iconUnclamp = S("icon_unclamp");
            var layout = JsonUtility.FromJson<HomeKitMeta>(File.ReadAllText(jsonPath)).workshop_layout;
            art.workshopBox = new Vector2(layout.reference_size[0], layout.reference_size[1]);
            var ids = new List<string>();
            var restored = new List<Sprite>();
            var broken = new List<Sprite>();
            var pos = new List<Vector2>();
            var size = new List<Vector2>();
            foreach (var p in layout.pieces)
            {
                if (p.name == "bench")
                {
                    art.workshopBench = S("bench");
                    art.workshopBenchPos = new Vector2(p.x, p.y);
                    art.workshopBenchSize = new Vector2(p.width, p.height);
                    continue;
                }
                ids.Add(p.name);
                restored.Add(S(p.name));
                broken.Add(S(p.name + "_broken"));
                pos.Add(new Vector2(p.x, p.y));
                size.Add(new Vector2(p.width, p.height));
            }
            art.workshopIds = ids.ToArray();
            art.workshopRestored = restored.ToArray();
            art.workshopBroken = broken.ToArray();
            art.workshopPos = pos.ToArray();
            art.workshopSize = size.ToArray();
        }

        static Sprite ImportUiIcon(string path)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            if (ti == null) return null;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.spritePixelsPerUnit = 100;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static void ImportIcon()
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(IconPath);
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.mipmapEnabled = false;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = 1024;
            ti.SaveAndReimport();
        }

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.magenta;

        // ------------------------------------------------------------------ scene

        [MenuItem("Orcub/Build Game Scene")]
        public static void BuildScene()
        {
            var art = AssetDatabase.LoadAssetAtPath<OrcubArt>(ArtAssetPath);
            if (art == null) throw new Exception("Run Orcub/Import Art first");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.orthographic = true;
            cam.orthographicSize = 9f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = art.background;
            cam.transform.position = new Vector3(0, 0, -10);

            // Soft ambient fill + one big key light from the upper left. The key reads the normal maps.
            var fill = new GameObject("Fill Light").AddComponent<Light2D>();
            fill.lightType = Light2D.LightType.Global;
            fill.color = new Color(0.86f, 0.88f, 1f);
            fill.intensity = 0.40f;
            ConfigureLight(fill, normalQuality: 2, normalDistance: 3f);

            var key = new GameObject("Key Light").AddComponent<Light2D>();
            key.lightType = Light2D.LightType.Point;
            key.transform.position = new Vector3(-9f, 13f, 0f);
            key.color = new Color(1f, 0.96f, 0.9f);
            key.intensity = 1.1f;
            key.pointLightInnerRadius = 40f;
            key.pointLightOuterRadius = 60f;
            key.falloffIntensity = 0f;
            ConfigureLight(key, normalQuality: 1, normalDistance: 17f);

            var boardGo = new GameObject("Orcub Board");
            var board = boardGo.AddComponent<BoardView>();
            board.Art = art;
            board.Cam = cam;

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Log("OK build-scene " + ScenePath);
        }

        static void ConfigureLight(Light2D light, int normalQuality, float normalDistance)
        {
            var so = new SerializedObject(light);
            so.FindProperty("m_NormalMapQuality").intValue = normalQuality; // 0 Fast, 1 Accurate, 2 Disabled
            so.FindProperty("m_NormalMapDistance").floatValue = normalDistance;
            var useNormals = so.FindProperty("m_UseNormalMap");
            if (useNormals != null) useNormals.boolValue = normalQuality != 2;
            var layers = so.FindProperty("m_ApplyToSortingLayers");
            layers.arraySize = SortingLayer.layers.Length;
            for (int i = 0; i < SortingLayer.layers.Length; i++)
                layers.GetArrayElementAtIndex(i).intValue = SortingLayer.layers[i].id;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ capture

        [MenuItem("Orcub/Capture Preview")]
        static void CaptureMenu() => Capture(Path.Combine(AutomationDir, "capture.png"), 1080, 1920);

        public static void Capture(string file, int width, int height)
        {
            if (EditorApplication.isPlaying)
            {
                var live = UnityEngine.Object.FindAnyObjectByType<BoardView>();
                RenderToFile(live.Cam != null ? live.Cam : Camera.main, file, width, height, live);
                Log("OK capture (play) " + file);
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var board = UnityEngine.Object.FindAnyObjectByType<BoardView>();
            var cam = board.Cam != null ? board.Cam : Camera.main;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            try
            {
                cam.targetTexture = rt;
                board.Build();
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = null;
                board.Clear();
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single); // discard preview objects
            }
            Log("OK capture " + file);
        }

        static void RenderToFile(Camera cam, string file, int width, int height, BoardView board)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var prevTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                board.ForceRefit();
                foreach (var splash in UnityEngine.Object.FindObjectsByType<SplashView>(FindObjectsSortMode.None)) splash.Relayout();
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = prevTarget;
                board.ForceRefit();
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ------------------------------------------------------------------ tests

        /// <summary>
        /// Runs every [Test] in the Orcub.Tests assembly by reflection. NUnit asserts throw outside the runner,
        /// so this works headless and does not need the editor to be focused (the Test Runner does).
        /// </summary>
        [MenuItem("Orcub/Run Tests (quick)")]
        public static void SelfTest()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Orcub.Tests");
            if (asm == null) throw new Exception("Orcub.Tests assembly not loaded");
            int pass = 0, fail = 0;
            foreach (var type in asm.GetTypes())
            foreach (var method in type.GetMethods())
            {
                if (!method.GetCustomAttributes(true).Any(a => a.GetType().Name == "TestAttribute")) continue;
                try
                {
                    method.Invoke(Activator.CreateInstance(type), null);
                    pass++;
                    Log("  pass " + method.Name);
                }
                catch (System.Reflection.TargetInvocationException e)
                {
                    fail++;
                    Log("  FAIL " + method.Name + " :: " + e.InnerException?.Message.Trim());
                }
            }
            Log($"{(fail == 0 ? "OK" : "FAIL")} test: {pass} passed, {fail} failed");
        }

        [MenuItem("Orcub/Run Tests (Test Runner)")]
        public static void RunTests()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new TestCallbacks());
            api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Orcub.Tests" },
            }));
        }

        class TestCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return;
                var sb = new StringBuilder();
                sb.Append(result.TestStatus == TestStatus.Passed ? "  pass " : "  FAIL ").Append(result.Name);
                if (result.TestStatus == TestStatus.Failed) sb.Append(" :: ").Append(result.Message?.Trim());
                Log(sb.ToString());
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Log($"{(result.FailCount == 0 ? "OK" : "FAIL")} test: {result.PassCount} passed, {result.FailCount} failed, {result.SkipCount} skipped");
            }
        }
    }
}
