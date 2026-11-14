using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Orcub.Core;
using Orcub.Game;
using UnityEditor;
using UnityEngine;

namespace Orcub.QA
{
    public static class LevelQA
    {
        public sealed class Row
        {
            public int Slot, Variant, Id, Minimum = -1, Budget, BotMoves = -1;
            public float Rating;
            public readonly List<string> Hard = new List<string>();
            public readonly List<string> Checks = new List<string>();
            public readonly List<string> Notes = new List<string>();
        }

        public sealed class Report
        {
            public readonly List<Row> Rows = new List<Row>();
            public readonly List<string> PackFailures = new List<string>();
            public IEnumerable<string> HardFailures => PackFailures.Concat(Rows.SelectMany(r =>
                r.Hard.Select(f => $"slot {r.Slot} variant {r.Variant} id {r.Id}: {f}")));
            public string Summary => $"{Rows.Count} boards; {HardFailures.Count()} hard failures; " +
                $"{Rows.Count(r => r.Checks.Count > 0)} boards with advisory failures; " +
                $"{Rows.Count(r => r.Notes.Count > 0)} rating outliers; {Rows.Count(r => r.BotMoves >= 0)} bot clears";
        }

        [MenuItem("Orcub/QA/Verify All Levels")]
        public static void VerifyAllLevels() => Debug.Log(Run().Summary + "; see ArtSource/qa/level_report.md");

        public static Report Run()
        {
            var report = new Report();
            try
            {
                // Read a single fresh snapshot. Do not trust LevelLibrary's previous editor cache.
                LevelLibrary.Reload();
                var asset = Resources.Load<TextAsset>("Orcub/levels");
                if (asset == null) throw new InvalidDataException("Resources/Orcub/levels is missing");
                var raw = JObject.Parse(asset.text);
                CheckSchema(raw, typeof(LevelPack), "pack", report.PackFailures);
                var pack = LevelLibrary.Pack;
                if (pack.levels == null || pack.levels.Length == 0) throw new InvalidDataException("Pack is empty");
                float previous = float.NaN;
                for (int slot = 0; slot < LevelLibrary.Count; slot++)
                {
                    var choices = LevelLibrary.GetVariants(slot);
                    if (choices.Length == 0) report.PackFailures.Add($"slot {slot + 1}: empty variants");
                    var ratings = new List<float>();
                    for (int variant = 0; variant < choices.Length; variant++)
                    {
                        var row = Inspect(choices[variant], slot, variant);
                        report.Rows.Add(row);
                        if (Finite(row.Rating) && row.Rating > 0) ratings.Add(row.Rating);
                    }
                    // Get(slot,0) uses levels[slot], even if choices[0] differs.
                    if (choices.Length == 0 || JsonUtility.ToJson(pack.levels[slot]) != JsonUtility.ToJson(choices[0]))
                        report.Rows.Add(Inspect(LevelLibrary.Get(slot), slot, -1));
                    if (ratings.Count == 0) continue;
                    float median = ratings.OrderBy(x => x).ElementAt(ratings.Count / 2);
                    if (Finite(previous) && median < previous - 25f)
                        foreach (var row in report.Rows.Where(r => r.Slot == slot + 1))
                            row.Notes.Add($"slot median rating drops {previous:F1} -> {median:F1} (>25 Elo)");
                    previous = median;
                }
                for (int slot = 200; slot < 2000; slot += 7)
                    for (int variant = 0; variant < 5; variant++)
                    {
                        var data = InfiniteGenerator.Generate(slot, variant);
                        var row = Inspect(data, slot, variant);
                        if (JsonUtility.ToJson(data) != JsonUtility.ToJson(InfiniteGenerator.Generate(slot, variant))) row.Hard.Add("non-deterministic generation");
                        // Generated geometry and bot failures are release gates, not advisories.
                        row.Hard.AddRange(row.Checks); row.Checks.Clear();
                        report.Rows.Add(row);
                    }
            }
            catch (Exception e) { report.PackFailures.Add("Pack load/sweep: " + e.GetType().Name + ": " + e.Message); }
            Write(report);
            return report;
        }

        static Row Inspect(LevelData data, int slot, int variant)
        {
            var row = new Row { Slot = slot + 1, Variant = variant, Id = data?.id ?? -1 };
            try
            {
                if (data == null) throw new InvalidDataException("Null level");
                var def = data.ToDef();
                var model = new PuzzleModel(def);
                if (model.Rings.Count == 0) throw new InvalidDataException("No rings");
                CheckTransfer(data.rings, def.Rings.ToArray(), model.Rings.ToArray(),
                    new[] { "x", "y", "r", "gap" }, row.Checks);
                CheckTransfer(data.links, def.Links.ToArray(), model.Links.ToArray(), Array.Empty<string>(), row.Checks);
                // Each stage reports separately, so a solver exception doesn't hide geometry/bot findings.
                try { row.Minimum = SolverMeasure.Search(def).MinimumMoves; }
                catch (Exception e) { row.Hard.Add("solver: " + e.Message); }
                try
                {
                    var rules = LevelRules.For(def, tutorial: slot < 3);
                    row.Budget = rules.MoveLimit;
                    if (row.Minimum < 0 || row.Minimum > row.Budget)
                        row.Hard.Add($"solver minimum {row.Minimum} exceeds budget {row.Budget} or no solution");
                    if (rules.ThreeStar < row.Minimum || rules.ThreeStar < 0 ||
                        rules.TwoStar <= rules.ThreeStar || rules.TwoStar > rules.MoveLimit ||
                        rules.StarsFor(row.Minimum) != 3)
                        row.Hard.Add($"star thresholds unreachable/unordered: 3 <= {rules.ThreeStar}, 2 <= {rules.TwoStar}, limit {rules.MoveLimit}");
                }
                catch (Exception e) { row.Hard.Add("rules/stars: " + e.Message); }
                try
                {
                    row.BotMoves = Bot(def, 3 * row.Budget, unchecked(data.seed + slot * 7919 + variant));
                    if (row.BotMoves < 0) row.Checks.Add("bot: no clear in 24 seeded greedy/random restarts within 3x budget");
                }
                catch (Exception e) { row.Checks.Add("bot: " + e.Message); }
                try { Geometry(model, row.Checks); }
                catch (Exception e) { row.Checks.Add("geometry: " + e.Message); }
                try
                {
                    row.Rating = LevelRating.For(def);
                    if (!Finite(row.Rating)) row.Checks.Add("rating is non-finite");
                }
                catch (Exception e) { row.Checks.Add("rating: " + e.Message); }
            }
            catch (Exception e) { row.Hard.Add("load/model: " + e.GetType().Name + ": " + e.Message); }
            return row;
        }

        static void CheckSchema(JToken token, Type type, string path, List<string> errors)
        {
            if (token.Type == JTokenType.Null) return;
            if (type.IsArray && token is JArray array)
            {
                for (int i = 0; i < array.Count; i++) CheckSchema(array[i], type.GetElementType(), path + $"[{i}]", errors);
                return;
            }
            if (!(token is JObject obj)) return;
            foreach (var property in obj.Properties())
            {
                var field = type.GetField(property.Name, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) errors.Add(path + "." + property.Name + ": unknown data field (JsonUtility would discard it)");
                else CheckSchema(property.Value, field.FieldType, path + "." + property.Name, errors);
            }
        }

        static void CheckTransfer<T, D, M>(T[] data, D[] defs, M[] models, string[] geometry, List<string> errors)
        {
            // Automatically covers newly added mechanic fields; no allow-list of mechanic names.
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (geometry.Contains(field.Name)) continue;
                var d = typeof(D).GetField(field.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var m = typeof(M).GetField(field.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                for (int i = 0; i < data.Length; i++)
                    if (d == null || m == null || !Equals(field.GetValue(data[i]), d.GetValue(defs[i])) ||
                        !Equals(field.GetValue(data[i]), m.GetValue(models[i])))
                    { errors.Add($"model does not preserve {typeof(T).Name}[{i}].{field.Name}"); break; }
            }
        }

        static int Bot(LevelDef def, int budget, int seed)
        {
            var random = new System.Random(seed);
            for (int restart = 0; restart < 24; restart++)
            {
                var model = new PuzzleModel(def);
                for (int move = 0; move <= budget; move++)
                {
                    bool removed;
                    do
                    {
                        removed = false;
                        for (int i = 0; i < model.Rings.Count; i++) removed |= model.TryRemove(i);
                    } while (removed);
                    if (model.IsSolved) return move;
                    if (move == budget) break;
                    int pick = -1; float best = float.NegativeInfinity; float sweep = 0;
                    foreach (var ring in model.Rings)
                    {
                        if (!model.CanRotate(ring.Id)) continue;
                        int mate = model.LinkedPartner(ring.Id);
                        float angle = 720;
                        if (!model.DirectionAllowed(ring.Id, angle) || (mate >= 0 && !model.DirectionAllowed(mate, -angle))) angle = -angle;
                        if (!model.DirectionAllowed(ring.Id, angle) || (mate >= 0 && !model.DirectionAllowed(mate, -angle))) continue;
                        int clamps = model.Links.Count(l => l.Active && (l.Target == ring.Id || l.Target == mate));
                        if (clamps == 0) continue;
                        // Prioritise deadlines/keys; later restarts explore a different legal order.
                        float score = restart == 0 ? clamps + (ring.KeyId > 0 || ring.OilId > 0 ? 20 : 0) +
                            (ring.RustAfter > 0 ? 30f / Mathf.Max(1, ring.RustAfter - move) : 0) : (float)random.NextDouble();
                        if (score > best) { best = score; pick = ring.Id; sweep = angle; }
                    }
                    if (pick < 0) break;
                    model.Rotate(pick, sweep);
                    model.CompleteMove();
                }
            }
            return -1;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static float SourceNumber(string source, string pattern)
        {
            var match = Regex.Match(source, pattern);
            if (!match.Success) throw new InvalidDataException("Camera/HUD source changed; update QA projection: " + pattern);
            return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        static void Geometry(PuzzleModel model, List<string> errors)
        {
            string board = File.ReadAllText("Assets/Orcub/Scripts/Game/BoardView.cs");
            string hud = File.ReadAllText("Assets/Orcub/Scripts/Game/LevelHud.cs");
            float teeth = SourceNumber(board, @"float e = r.Radius \+ ([\d.]+)f");
            float padding = SourceNumber(board, @"float framePadding = ([\d.]+)f");
            float bottom = LevelHud.TrayFraction + SourceNumber(hud, @"DevBandFraction = ([\d.]+)f") +
                SourceNumber(hud, @"BottomReserve =>[^;]+\+ ([\d.]+)f;");
            float top = LevelHud.TopReserve;
            const float aspect = 9f / 19.5f;
            float minX = float.PositiveInfinity, minY = minX, maxX = float.NegativeInfinity, maxY = maxX;
            foreach (var r in model.Rings)
            {
                if (!Finite(r.Center.x) || !Finite(r.Center.y) || !Finite(r.Radius) || r.Radius <= 0)
                    throw new InvalidDataException($"invalid geometry on ring {r.Id}");
                float extent = r.Radius + teeth;
                minX = Mathf.Min(minX, r.Center.x - extent); maxX = Mathf.Max(maxX, r.Center.x + extent);
                minY = Mathf.Min(minY, r.Center.y - extent); maxY = Mathf.Max(maxY, r.Center.y + extent);
                foreach (var other in model.Rings)
                    if (other.Id > r.Id && Vector2.Distance(r.Center, other.Center) <= r.Radius + other.Radius + 2 * teeth)
                        errors.Add($"overlapping rings/teeth {r.Id},{other.Id}");
            }
            float size = Mathf.Max(((maxY - minY) / 2 + padding) / (1 - top - bottom),
                ((maxX - minX) / 2 + padding) / aspect);
            float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2 + (top - bottom) * size;
            if (!Finite(size) || size <= 0) throw new InvalidDataException("invalid fitted camera");
            foreach (var r in model.Rings)
            {
                float e = r.Radius + teeth;
                float left = .5f + (r.Center.x - e - cx) / (2 * size * aspect);
                float right = .5f + (r.Center.x + e - cx) / (2 * size * aspect);
                float low = .5f + (r.Center.y - e - cy) / (2 * size);
                float high = .5f + (r.Center.y + e - cy) / (2 * size);
                if (left < -0.0001f || right > 1.0001f || low < bottom - .0001f || high > 1 - top + .0001f)
                    errors.Add($"ring {r.Id} outside 9:19.5 frame/HUD reserves");
            }
        }

        static string Escape(string s) => "\"" + s.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") + "\"";
        static void Write(Report report)
        {
            var markdown = new StringBuilder("# Level QA\n\n").AppendLine(DateTime.UtcNow.ToString("u"))
                .AppendLine().AppendLine(report.Summary).AppendLine()
                .AppendLine("Variants are zero-based; -1 is a distinct base board. Budgets use tutorial rules for slots 1–3, without assistance. Bot uses 24 seeded restarts and 720-degree gestures. Geometry projects BoardView's dynamic camera fit at 9:19.5 with the development HUD reserve; device notch/safe-area testing is separate. Rating drops over 25 Elo between slot medians are advisory. Field checks detect unknown JSON and dropped data-to-model fields; they do not prove the semantics of future mechanics.\n");
            foreach (string failure in report.PackFailures) markdown.AppendLine("- HARD: " + failure);
            markdown.AppendLine("\n|Slot|Variant|ID|Minimum|Budget|Bot moves / 3x budget|Rating|Result|Details|")
                .AppendLine("|---|---|---|---|---|---|---|---|---|");
            var csv = new StringBuilder("slot,variant,id,solver_minimum,budget,bot_moves,bot_budget,rating,result,details\n");
            foreach (var r in report.Rows.OrderByDescending(r => r.Hard.Count > 0).ThenByDescending(r => r.Checks.Count > 0).ThenBy(r => r.Slot).ThenBy(r => r.Variant))
            {
                string status = r.Hard.Count > 0 ? "HARD FAIL" : r.Checks.Count > 0 ? "CHECK FAIL" : "PASS";
                string details = string.Join("; ", r.Hard.Concat(r.Checks).Concat(r.Notes));
                string rating = r.Rating.ToString("F1", CultureInfo.InvariantCulture);
                markdown.AppendLine($"|{r.Slot}|{r.Variant}|{r.Id}|{r.Minimum}|{r.Budget}|{r.BotMoves} / {3 * r.Budget}|{rating}|{status}|{details.Replace("|", "/")}|");
                csv.AppendLine($"{r.Slot},{r.Variant},{r.Id},{r.Minimum},{r.Budget},{r.BotMoves},{3 * r.Budget},{rating},{status},{Escape(details)}");
            }
            foreach (var failure in report.PackFailures) csv.AppendLine(",,,,,,,,HARD FAIL," + Escape(failure));
            Directory.CreateDirectory("ArtSource/qa");
            File.WriteAllText("ArtSource/qa/level_report.md", markdown.ToString());
            File.WriteAllText("ArtSource/qa/level_report.csv", csv.ToString());
        }
    }
}
