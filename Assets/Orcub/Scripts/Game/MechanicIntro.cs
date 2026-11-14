using Orcub.Core;
using UnityEngine;

namespace Orcub.Game
{
    /// <summary>
    /// "New gear!" card content (approved mech_intro_card_B): one line per mechanic, an optional number pill,
    /// and how the animated hint arrow moves. Shown once per mechanic at its tutorial level (level metadata
    /// mechanic_intro); the pause card's "i" re-opens it.
    /// </summary>
    public static class MechanicIntro
    {
        public struct Info
        {
            public string Key, Rule, Pill;
            public bool Opposed;     // linked: two arrows turning against each other
            public float Direction;  // +1 counter-clockwise, -1 clockwise
        }

        const string Prefix = "orcub.intro.";

        public static bool Seen(string key) => PlayerPrefs.GetInt(Prefix + key, 0) == 1;

        public static void MarkSeen(string key)
        {
            PlayerPrefs.SetInt(Prefix + key, 1);
            PlayerPrefs.Save();
        }

        public static Info For(string key, LevelDef def)
        {
            var info = new Info { Key = key, Direction = -1f };
            switch (key)
            {
                case "ratchet":
                    info.Rule = "This gear turns one way only.";
                    if (def != null) foreach (var r in def.Rings) if (r.Ratchet != 0) { info.Direction = r.Ratchet; break; }
                    break;
                case "linked":
                    info.Rule = "Turn one. Both turn.";
                    info.Opposed = true;
                    break;
                case "colour_clamp":
                    info.Rule = "A coloured clamp opens only for its own colour and symbol.";
                    break;
                case "key_padlock":
                    info.Rule = "Free the key to unlock the padlocked gear.";
                    break;
                case "rust_oil":
                    info.Rule = "Free the oil before the rust sets in.";
                    int rust = int.MaxValue;
                    if (def != null) foreach (var r in def.Rings) if (r.RustAfter > 0) rust = Mathf.Min(rust, r.RustAfter);
                    if (rust < int.MaxValue) info.Pill = rust == 1 ? "1 move" : $"{rust} moves";
                    break;
                case "spring":
                    info.Rule = "Pull free before it re-closes.";
                    int k = int.MaxValue;
                    if (def != null) foreach (var l in def.Links) if (l.SpringMoves > 0) k = Mathf.Min(k, l.SpringMoves);
                    if (k < int.MaxValue) info.Pill = k == 1 ? "1 move" : $"{k} moves";
                    break;
                case "clay":
                    info.Rule = "Pass the gap twice to crack the clay.";
                    break;
                default:
                    info.Rule = "Every gear you've met, all mixed together.";
                    break;
            }
            return info;
        }
    }
}
