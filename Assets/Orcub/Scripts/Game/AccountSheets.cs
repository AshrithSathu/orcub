using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Bottom sheets of the account flow: tracking pre-prompt (att_preprompt_B), save nudge (save_nudge_A)
    /// and cloud restore (cloud_restore_B). Each is one floating clay card with one primary action.
    /// </summary>
    public static class AccountSheets
    {
        const float Inner = 1080f * (1f - 2f * AccountUi.SideMargin) - 2f * 58f;   // text column width

        // ------------------------------------------------------------------ tracking pre-prompt

        /// <summary>
        /// "About tracking" on quiet felt, one Continue (no Allow, no Cancel, no close). Continue hands over to the
        /// iOS ATT request; whatever the player picks, play continues.
        /// </summary>
        public static AccountOverlay Tracking(Transform parent, UiKit kit, Action onContinue)
        {
            var o = AccountOverlay.Create(parent, kit, "Tracking", 520, 0f, felt: true);
            var word = kit.MakeText(o.Safe, "orcub", 150, UiKit.Cream);
            UiKit.Anchor(word.rectTransform, 0f, 0.74f, 1f, 0.82f);
            UiKit.Shadow(word, 0.3f);
            word.transform.SetSiblingIndex(0);

            var card = o.Panel;
            float y = 50f;
            var head = AccountUi.Row(card, "Head", y, 124f, 58f, 40f);
            var disc = kit.MakeImage(head, AccountArt.Info, Color.white);
            var drt = disc.rectTransform;
            drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f);
            drt.pivot = new Vector2(0f, 0.5f);
            drt.sizeDelta = new Vector2(124f, 124f);
            drt.anchoredPosition = new Vector2(-8f, 0f);
            var title = kit.MakeText(head, "About tracking", UiKit.Display, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(title.rectTransform, 0f, 0f, 1f, 1f);
            title.rectTransform.offsetMin = new Vector2(150f, 0f);
            y += 124f + 30f;

            y = Paragraph(kit, card, y, "Allowing tracking helps keep ads relevant and Orcub free.", UiKit.Title, UiKit.Ink) + 18f;
            y = Paragraph(kit, card, y, "Your choice. iOS will ask next.", UiKit.Body, AccountUi.InkSoft) + 40f;

            var go = kit.Pill(card, "Continue", UiKit.Mint, 72, () => o.Close(onContinue));
            Stretch(go, y, 156f, 46f);
            y += 156f + 52f;

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.BottomCard(y);
            o.Enter();
            return o;
        }

        // ------------------------------------------------------------------ save nudge

        /// <summary>Guests only: "Save your progress" with one Google button and a plain "Not now".</summary>
        public static AccountOverlay SaveNudge(Transform parent, UiKit kit, Action onDone)
        {
            var o = AccountOverlay.Create(parent, kit, "SaveNudge", 520, 0.55f);
            var card = o.Panel;
            float y = 46f;
            var head = AccountUi.Row(card, "Head", y, 104f, 58f, 40f);
            var cloud = kit.MakeImage(head, AccountArt.Cloud, Color.white);
            var crt = cloud.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 0.5f);
            crt.pivot = new Vector2(0f, 0.5f);
            crt.sizeDelta = new Vector2(140f, 103f);
            crt.anchoredPosition = new Vector2(-6f, 0f);
            var title = kit.MakeText(head, "Save your progress", UiKit.Title, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(title.rectTransform, 0f, 0f, 1f, 1f);
            title.rectTransform.offsetMin = new Vector2(160f, 0f);
            y += 104f + 26f;

            y = Paragraph(kit, card, y, "Keep your levels and stars safe with Google.", UiKit.Body, UiKit.Ink, 230f) + 10f;
            // the quiet note doubles as the reserved error line (no layout shift on failure)
            var noteRow = AccountUi.Row(card, "Note", y, 10f, 58f, 58f);
            var note = AccountUi.Wrapped(kit, noteRow, "Signing in is optional. Guest play always works.", UiKit.Label,
                AccountUi.InkSoft, TextAnchor.UpperLeft);
            float nh = AccountUi.WrappedHeight(note, Inner) + 8f;
            noteRow.offsetMin = new Vector2(58f, -(y + nh));
            y += nh + 34f;

            var gb = AccountUi.Row(card, "Google", y, 142f, 58f, 58f);
            GoogleButton google = null;
            google = GoogleButton.Create(kit, gb, async () =>
            {
                google.SetBusy(true);
                bool ok = false;
                try
                {
                    var svc = AccountGateway.Service;
                    if (svc == null || !svc.Ready) throw new InvalidOperationException("Accounts unavailable");
                    await svc.SignInWithGoogle();
                    ok = !svc.IsGuest;
                }
                catch (Exception e) { Debug.Log("[Orcub] Google sign-in did not complete: " + e.Message); }
                if (o == null) return;
                google.SetBusy(false);
                if (ok) { o.Close(onDone); return; }
                note.text = "Couldn’t sign in. Check your connection and try again.";
                note.color = UiKit.Ink;
            });
            y += 142f + 16f;

            var later = AccountUi.TextButton(kit, card, "Not now", UiKit.Ink, () => o.Close(onDone));
            Stretch(later, y, 128f, 200f);
            y += 128f + 34f;

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.BottomCard(y);
            o.Enter();
            return o;
        }

        // ------------------------------------------------------------------ cloud restore

        /// <summary>
        /// Side-by-side comparison of the cloud save and this phone. The further-ahead save is highlighted
        /// (mint + check + "Further ahead", never colour alone) and its Keep button is the primary action.
        /// </summary>
        public static AccountOverlay CloudRestore(Transform parent, UiKit kit, SaveSummary cloud, SaveSummary phone,
            Action<bool> onChoose)
        {
            var o = AccountOverlay.Create(parent, kit, "CloudRestore", 520, 0.55f);
            var card = o.Panel;
            bool cloudAhead = !phone.AheadOf(cloud);
            float y = 40f;
            var tr = AccountUi.Row(card, "Title", y, 110f, 30f, 30f);
            var t = kit.MakeText(tr, "Cloud save found", UiKit.Display, UiKit.Ink);
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            y += 110f;
            var sr = AccountUi.Row(card, "Sub", y, 64f, 30f, 30f);
            var sub = kit.MakeText(sr, "Choose which progress to keep.", UiKit.Body, UiKit.Ink);
            UiKit.Anchor(sub.rectTransform, 0, 0, 1, 1);
            y += 64f + 34f;

            const float tileH = 528f;
            var tiles = AccountUi.Row(card, "Tiles", y, tileH, 50f, 50f);
            SaveTile(kit, tiles, 0f, 0.485f, AccountArt.Cloud, new Vector2(176f, 129f), "Cloud save", cloud, cloudAhead);
            SaveTile(kit, tiles, 0.515f, 1f, AccountArt.Phone, new Vector2(100f, 130f), "This phone", phone, !cloudAhead);
            y += tileH + 40f;

            var primary = kit.Pill(card, cloudAhead ? "Keep cloud" : "Keep this phone", UiKit.Mint, 72,
                () => o.Close(() => onChoose(cloudAhead)));
            Stretch(primary, y, 156f, 46f);
            y += 156f + 20f;
            var secondary = kit.Pill(card, cloudAhead ? "Keep this phone" : "Keep cloud", UiKit.CardCream, UiKit.Title,
                () => o.Close(() => onChoose(!cloudAhead)));
            Stretch(secondary, y, 132f, 46f);
            y += 132f + 18f;
            var fr = AccountUi.Row(card, "Foot", y, 60f, 30f, 30f);
            var foot = kit.MakeText(fr, "The save you choose replaces the other.", UiKit.Label, UiKit.Ink);
            UiKit.Anchor(foot.rectTransform, 0, 0, 1, 1);
            y += 60f + 40f;

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.BottomCard(y);
            o.Enter();
            return o;
        }

        static void SaveTile(UiKit kit, RectTransform tiles, float x0, float x1, Sprite icon, Vector2 iconSize,
            string label, SaveSummary s, bool ahead)
        {
            var tile = UiKit.Rect(label, tiles);
            UiKit.Anchor(tile, x0, 0f, x1, 1f);
            AccountUi.Panel(kit, tile, ahead ? AccountUi.MintTile : UiKit.Slab, 72f);
            float top = 28f;
            var ir = AccountUi.Row(tile, "Icon", top, 130f, 0, 0);   // same row height for both icons: labels align
            AccountUi.Icon(kit, ir, icon, Color.white, 0.5f, 0.5f, iconSize.x, iconSize.y);
            top += 130f + 8f;
            var lr = AccountUi.Row(tile, "Label", top, 58f, 10f, 10f);
            UiKit.Anchor(kit.MakeText(lr, label, UiKit.Body, UiKit.Ink).rectTransform, 0, 0, 1, 1);
            top += 58f;
            var lv = AccountUi.Row(tile, "Level", top, 96f, 10f, 10f);
            var lt = kit.MakeText(lv, $"Level {s.Level}", UiKit.Display, UiKit.Ink);
            UiKit.Anchor(lt.rectTransform, 0, 0, 1, 1);
            AccountUi.Fit(lt, 400f, UiKit.Display, UiKit.Title);
            top += 96f;
            var st = AccountUi.Row(tile, "Stars", top, 76f, 10f, 10f);
            AccountUi.IconLabel(kit, st, kit.Art.uiStarClay, UiKit.StarGold, 60f, 60f, s.Stars.ToString(),
                UiKit.Title, UiKit.Ink, 8f);
            // number first, star after (as approved): mirror the pair
            foreach (Transform c in st) ((RectTransform)c).anchoredPosition = new Vector2(-((RectTransform)c).anchoredPosition.x, 0f);
            top += 76f + 12f;
            if (!ahead) return;
            var chip = AccountUi.Row(tile, "Ahead", top, 84f, 26f, 26f);
            AccountUi.Slab(kit, chip, new Color(0.86f, 0.95f, 0.84f), 84f);
            var face = UiKit.Rect("Face", chip);
            UiKit.Anchor(face, 0f, 0.04f, 1f, 1f);
            AccountUi.IconLabel(kit, face, AccountArt.Check, UiKit.Ink, 34f, 34f, "Further ahead", UiKit.Label, UiKit.Ink, 14f);
        }

        // ------------------------------------------------------------------ helpers

        static float Paragraph(UiKit kit, RectTransform card, float y, string text, int size, Color color, float right = 58f)
        {
            var row = AccountUi.Row(card, "Text", y, 10f, 58f, right);
            var t = AccountUi.Wrapped(kit, row, text, size, color, TextAnchor.UpperLeft);
            float h = AccountUi.WrappedHeight(t, Inner - (right - 58f)) + 6f;
            row.offsetMin = new Vector2(58f, -(y + h));
            return y + h;
        }

        static void Stretch(RectTransform rt, float top, float h, float inset)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(inset, -(top + h));
            rt.offsetMax = new Vector2(-inset, -top);
        }
    }
}
