using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Settings from home (approved account_v1 settings_B / settings_signed_in_B, account_v2 signing_in_A,
    /// signing_in_failed_A, signout_confirm_A, delete_confirm_A). Account first, then Sound / Haptics tiles,
    /// then Privacy policy · Manage ad consent · Delete account and data. Bottom-anchored floating clay card.
    /// The in-game pause card is separate and unchanged.
    /// </summary>
    public sealed class SettingsScreen : MonoBehaviour
    {
        public const string PrivacyUrl = "https://orcub.sathuashrith.com/privacy";

        /// <summary>What the account block shows. Editor captures may substitute a fake.</summary>
        public struct View
        {
            public bool Guest;
            public string Provider, Email;
            public DateTime? Saved;
        }

        /// <summary>Editor-only override (OrcubShots) to render signed-in states without a real Google account.</summary>
        public static View? Fake;

        enum SignIn { Idle, Busy, Failed }

        UiKit _kit;
        AccountOverlay _overlay;
        Action _onClosed;
        GoogleButton _google;
        RectTransform _status;
        Text _savedWhen;
        View _view;
        SignIn _signIn;
        float _tick;
        AccountDialog _dialog;

        static IAccountService Svc => AccountGateway.Service;

        public static SettingsScreen Open(Transform parent, UiKit kit, Action onClosed)
        {
            var s = new GameObject("SettingsHost").AddComponent<SettingsScreen>();
            s.transform.SetParent(parent, false);
            s._kit = kit;
            s._onClosed = onClosed;
            s._view = CurrentView();
            s.Build(animate: true);
            return s;
        }

        static View CurrentView()
        {
            if (Fake.HasValue) return Fake.Value;
            var svc = Svc;
            if (svc == null || !svc.Ready || svc.IsGuest) return new View { Guest = true };
            return new View { Guest = false, Provider = svc.Provider ?? "Google", Email = svc.Email, Saved = svc.LastCloudSave };
        }

        public void Close()
        {
            if (_overlay == null || _overlay.Closing) return;
            _dialog?.Dismiss();
            _overlay.Close(() => { _onClosed?.Invoke(); Destroy(gameObject); });
        }

        void Build(bool animate)
        {
            if (_overlay != null) Destroy(_overlay.gameObject);
            var kit = _kit;
            var art = kit.Art;
            _overlay = AccountOverlay.Create(transform, kit, "Settings", 450, 0.55f, onScrimTap: Close);
            var card = _overlay.Panel;
            float y = 30f;

            // ---- title row: "Settings" centred, close in the corner
            var titleRow = AccountUi.Row(card, "Title", y, 120f, 0f, 0f);
            var title = kit.MakeText(titleRow, "Settings", UiKit.Display, UiKit.Ink);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            var close = AccountUi.CloseButton(kit, titleRow, Close);
            close.anchorMin = close.anchorMax = new Vector2(1f, 0.5f);
            close.pivot = new Vector2(1f, 0.5f);
            close.sizeDelta = new Vector2(UiKit.MinTouch, UiKit.MinTouch);
            close.anchoredPosition = new Vector2(-30f, 0f);
            y += 120f + 22f;

            // ---- account block
            y = _view.Guest ? BuildGuest(card, y) : BuildSignedIn(card, y);

            // ---- Sound / Haptics tiles
            const float tileH = 340f, gutter = 30f;
            var tiles = AccountUi.Row(card, "Tiles", y, tileH, AccountUi.Inset, AccountUi.Inset);
            Tile(tiles, 0f, 0.5f - gutter / 2f / 900f, art.uiSound, "Sound", GameSettings.SoundOn, on => GameSettings.SoundOn = on);
            Tile(tiles, 0.5f + gutter / 2f / 900f, 1f, art.uiHaptics, "Haptics", GameSettings.HapticsOn, on => GameSettings.HapticsOn = on);
            y += tileH + 34f;

            // ---- privacy rows
            const float rowH = 128f, rowGap = 26f;
            AccountUi.LinkRow(kit, card, "Privacy", y, rowH, "Privacy policy", UiKit.Ink, UiKit.Ink, () => Application.OpenURL(PrivacyUrl));
            y += rowH + rowGap;
            AccountUi.LinkRow(kit, card, "Consent", y, rowH, "Manage ad consent", UiKit.Ink, UiKit.Ink, ManageConsent);
            y += rowH + rowGap;
            AccountUi.LinkRow(kit, card, "Delete", y, rowH, "Delete account and data", AccountUi.DeleteRed,
                _view.Guest ? (Color?)null : AccountUi.DeleteRed, ConfirmDelete);
            y += rowH + 44f;

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            _overlay.BottomCard(y);
            _overlay.Enter(animate);
            SetSignIn(_signIn);
        }

        float BuildGuest(RectTransform card, float y)
        {
            var kit = _kit;
            const float accountH = 210f;
            AccountCard(card, y, accountH, "Account", "Playing as guest · Save your progress", null);
            y += accountH + 40f;

            // One flat Google button (Apple stays hidden behind AccountGateway.ShowAppleSignIn).
            var gb = AccountUi.Row(card, "Google", y, 142f, AccountUi.Inset + 6f, AccountUi.Inset + 6f);
            _google = GoogleButton.Create(kit, gb, StartSignIn);
            y += 142f;

            // Reserved status row: empty, or the inline "Couldn't sign in. Try again". Never resizes.
            _status = AccountUi.Row(card, "Status", y, 116f, AccountUi.Inset, AccountUi.Inset);
            return y + 116f + 18f;
        }

        float BuildSignedIn(RectTransform card, float y)
        {
            var kit = _kit;
            const float accountH = 236f;
            AccountCard(card, y, accountH, "Account", $"Signed in with {_view.Provider ?? "Google"}", MaskEmail(_view.Email));
            y += accountH + 18f;

            // mint status strip: "Progress saved to cloud ✓ | just now" (live time right-aligned)
            const float stripH = 100f;
            var strip = AccountUi.Row(card, "Saved", y, stripH, AccountUi.Inset, AccountUi.Inset);
            AccountUi.Slab(kit, strip, AccountUi.MintStrip, stripH);
            var face = UiKit.Rect("Face", strip);
            UiKit.Anchor(face, 0f, 0.04f, 1f, 1f);
            var left = kit.MakeText(face, "Progress saved to cloud", UiKit.Label, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(left.rectTransform, 0f, 0f, 0.74f, 1f);
            left.rectTransform.offsetMin = new Vector2(40f, 0f);
            float lw = AccountUi.Width(left);
            var check = kit.MakeImage(face, AccountArt.Check, UiKit.Ink);
            var crt = check.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 0.5f);
            crt.sizeDelta = new Vector2(34f, 34f);
            crt.anchoredPosition = new Vector2(40f + lw + 30f, 0f);
            AccountUi.VRule(kit, face, 0.75f);
            _savedWhen = kit.MakeText(face, Ago(_view.Saved), UiKit.Label, UiKit.Ink, TextAnchor.MiddleRight);
            UiKit.Anchor(_savedWhen.rectTransform, 0.76f, 0f, 1f, 1f);
            _savedWhen.rectTransform.offsetMax = new Vector2(-40f, 0f);
            y += stripH + 22f;

            // quiet secondary: Sign out
            var so = kit.Pill(card, "Sign out", UiKit.Slab, UiKit.Body, ConfirmSignOut);
            var srt = so;
            srt.anchorMin = new Vector2(0f, 1f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.pivot = new Vector2(0.5f, 1f);
            srt.offsetMin = new Vector2(AccountUi.Inset - 10f, -(y + 128f));
            srt.offsetMax = new Vector2(-(AccountUi.Inset - 10f), -y);
            return y + 128f + 26f;
        }

        void AccountCard(RectTransform card, float y, float h, string title, string line1, string line2)
        {
            var kit = _kit;
            var box = AccountUi.Row(card, "Account", y, h, AccountUi.Inset, AccountUi.Inset);
            AccountUi.Panel(kit, box, UiKit.Slab, 64f);
            var cloud = kit.MakeImage(box, AccountArt.Cloud, Color.white);
            UiKit.Anchor(cloud.rectTransform, 0.035f, 0.18f, 0.25f, 0.84f);
            AccountUi.VRule(kit, box, 0.285f, 0.16f, 0.84f);

            var col = UiKit.Rect("Text", box);
            UiKit.Anchor(col, 0.325f, 0f, 1f, 1f);
            col.offsetMax = new Vector2(-24f, 0f);
            float width = (900f - 2f * AccountUi.Inset) * 0.675f - 24f;
            var t = kit.MakeText(col, title, UiKit.Title, UiKit.Ink, TextAnchor.LowerLeft);
            var l1 = kit.MakeText(col, line1, UiKit.Label, UiKit.Ink, TextAnchor.UpperLeft);
            // one line when it fits at caption size; otherwise wrap after the middle dot, never mid-phrase
            if (line2 == null) AccountUi.Fit(l1, width, UiKit.Label, UiKit.Caption);
            if (line2 == null && AccountUi.Width(l1) > width && line1.Contains(" · "))
            {
                var parts = line1.Split(new[] { " · " }, StringSplitOptions.None);
                l1.fontSize = UiKit.Label;
                l1.text = parts[0] + " ·";
                line2 = parts[1];
            }
            if (line2 == null)
            {
                UiKit.Anchor(t.rectTransform, 0, 0.5f, 1, 0.92f);
                UiKit.Anchor(l1.rectTransform, 0, 0.1f, 1, 0.47f);
                return;
            }
            UiKit.Anchor(t.rectTransform, 0, 0.6f, 1, 0.93f);
            UiKit.Anchor(l1.rectTransform, 0, 0.34f, 1, 0.6f);
            l1.alignment = TextAnchor.MiddleLeft;
            var l2 = kit.MakeText(col, line2, UiKit.Label, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(l2.rectTransform, 0, 0.08f, 1, 0.34f);
            AccountUi.Fit(l2, width, UiKit.Label, UiKit.Caption);
        }

        void Tile(RectTransform tiles, float x0, float x1, Sprite icon, string label, bool on, Action<bool> changed)
        {
            var kit = _kit;
            var tile = UiKit.Rect(label, tiles);
            UiKit.Anchor(tile, x0, 0f, x1, 1f);
            AccountUi.Panel(kit, tile, UiKit.Slab, 72f);
            var ic = kit.MakeImage(tile, icon, AccountUi.TileTan);
            UiKit.Anchor(ic.rectTransform, 0.24f, 0.62f, 0.76f, 0.95f);
            var t = kit.MakeText(tile, label, UiKit.Body, UiKit.Ink);
            UiKit.Anchor(t.rectTransform, 0f, 0.43f, 1f, 0.63f);
            var sw = UiKit.Rect("Switch", tile);
            UiKit.Anchor(sw, 0.13f, 0.08f, 0.87f, 0.38f);
            ClayToggle.Create(kit, sw, on, changed);
        }

        // ------------------------------------------------------------------ sign in

        void StartSignIn()
        {
            if (_signIn == SignIn.Busy) return;
            _ = SignInAsync();
        }

        async Task SignInAsync()
        {
            SetSignIn(SignIn.Busy);
            bool ok = false;
            try
            {
                var svc = Svc;
                if (svc == null || !svc.Ready) throw new InvalidOperationException("Accounts unavailable");
                await svc.SignInWithGoogle();
                ok = !svc.IsGuest;
            }
            catch (Exception e) { Debug.Log("[Orcub] Google sign-in did not complete: " + e.Message); }
            if (this == null) return;
            if (!ok) { SetSignIn(SignIn.Failed); return; }
            _signIn = SignIn.Idle;
            _view = CurrentView();
            Build(animate: false);
        }

        /// <summary>Busy and failed states change only the button content and the reserved status row.</summary>
        void SetSignIn(SignIn state)
        {
            _signIn = state;
            if (_google == null || _status == null) return;
            _google.SetBusy(state == SignIn.Busy);
            for (int i = _status.childCount - 1; i >= 0; i--) Destroy(_status.GetChild(i).gameObject);
            if (state != SignIn.Failed) return;

            var kit = _kit;
            var group = UiKit.Rect("Error", _status);
            UiKit.Anchor(group, 0, 0, 1, 1);
            var msg = kit.MakeText(group, "Couldn’t sign in.", UiKit.Label, UiKit.Ink);
            var retry = kit.Link(group, "Try again", UiKit.Ink, StartSignIn);
            float mw = AccountUi.Width(msg), rw = AccountUi.Width(retry.GetComponentInChildren<Text>());
            const float icon = 50f, gap = 18f, space = 14f;
            float total = icon + gap + mw + space + rw, x0 = -total * 0.5f;
            var ic = kit.MakeImage(group, AccountArt.InfoRing, UiKit.Coral);
            UiKit.Place(ic.rectTransform, 0.5f, 0.5f, icon, icon);
            ic.rectTransform.anchoredPosition = new Vector2(x0 + icon * 0.5f, 0f);
            UiKit.Place(msg.rectTransform, 0.5f, 0.5f, mw + 6f, 80f);
            msg.rectTransform.anchoredPosition = new Vector2(x0 + icon + gap + mw * 0.5f, 0f);
            UiKit.Place(retry, 0.5f, 0.5f, rw + 12f, UiKit.MinTouch);
            retry.anchoredPosition = new Vector2(x0 + icon + gap + mw + space + rw * 0.5f, 0f);
            var g = group.gameObject.AddComponent<CanvasGroup>();
            StartCoroutine(UiKit.FadeIn(g, 0.18f));
        }

        // ------------------------------------------------------------------ privacy

        static void ManageConsent()
        {
            var ads = AdsGateway.Service;
            if (ads != null && ads.PrivacyOptionsRequired) { ads.ShowPrivacyOptions(); return; }
#if UNITY_IOS && !UNITY_EDITOR
            // No UMP form applies in this region: the tracking choice lives in the app's iOS Settings page.
            Application.OpenURL("app-settings:");
#else
            Debug.Log("[Orcub] Manage ad consent: no UMP privacy options form is required here");
#endif
        }

        // ------------------------------------------------------------------ confirmations

        void ConfirmSignOut()
        {
            _dialog = AccountDialog.Open(transform, _kit, AccountArt.Cloud, new Vector2(200f, 147f), "Sign out?",
                "Your progress stays saved in the cloud; this phone continues as a guest.", null,
                "Sign out", "Signing out…", "Couldn’t sign out. Check your connection and try again.",
                () => Svc != null ? Svc.SignOut() : Task.FromException(new InvalidOperationException("Offline")));
        }

        void ConfirmDelete()
        {
            _dialog = AccountDialog.Open(transform, _kit, AccountArt.Trash, new Vector2(128f, 140f), "Delete account and data?",
                "This permanently deletes your progress, stars and cloud save.", "This cannot be undone.",
                "Delete", "Deleting…", "Couldn’t delete. Check your connection and try again.",
                () => Svc != null ? Svc.DeleteAccount() : Task.FromException(new InvalidOperationException("Offline")));
        }

        // ------------------------------------------------------------------ editor capture hooks

        public void ShowSigningIn() => SetSignIn(SignIn.Busy);
        public void ShowSignInFailed() => SetSignIn(SignIn.Failed);
        public void OpenSignOutConfirm() => ConfirmSignOut();
        public void OpenDeleteConfirm() => ConfirmDelete();
        public AccountDialog Dialog => _dialog;

        // ------------------------------------------------------------------ helpers

        void Update()
        {
            if (_savedWhen == null) return;
            _tick -= Time.unscaledDeltaTime;
            if (_tick > 0f) return;
            _tick = 5f;
            var svc = Svc;
            var saved = Fake.HasValue ? Fake.Value.Saved : svc?.LastCloudSave;
            _savedWhen.text = Ago(saved);
        }

        static string Ago(DateTime? utc)
        {
            if (!utc.HasValue) return "pending";
            var s = (DateTime.UtcNow - utc.Value).TotalSeconds;
            if (s < 60) return "just now";
            if (s < 3600) return $"{(int)(s / 60)} min ago";
            if (s < 86400) return $"{(int)(s / 3600)} h ago";
            return utc.Value.ToLocalTime().ToString("d MMM");
        }

        /// <summary>"alex@gmail.com" → "a••••@gmail.com".</summary>
        public static string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email)) return "Google account";
            int at = email.IndexOf('@');
            if (at <= 0) return email;
            return email[0] + "••••" + email.Substring(at);
        }
    }

    /// <summary>
    /// Centred safe-choice confirmation (approved delete_confirm_A / signout_confirm_A): small neutral icon, title,
    /// consequence, a wide mint Cancel (primary, safe) above a smaller coral action. While the action runs its pill
    /// shows a spinner and Cancel is disabled; a failure replaces the quiet line with the error. Nothing moves.
    /// </summary>
    public sealed class AccountDialog : MonoBehaviour
    {
        AccountOverlay _overlay;
        RectTransform _action;
        Text _actionLabel, _note;   // _note: the line a failure message replaces (the quiet note, else the body)
        GameObject _spinner;
        CanvasGroup _cancel;
        bool _busy;
        string _busyLabel, _actionText, _error;
        Func<Task> _run;

        public static AccountDialog Open(Transform parent, UiKit kit, Sprite icon, Vector2 iconSize, string title,
            string body, string note, string action, string busyLabel, string error, Func<Task> run)
        {
            var d = new GameObject("Dialog").AddComponent<AccountDialog>();
            d.transform.SetParent(parent, false);
            d._run = run;
            d._busyLabel = busyLabel;
            d._actionText = action;
            d._error = error;
            d._overlay = AccountOverlay.Create(d.transform, kit, "Confirm", 500, 0.62f);
            var card = d._overlay.Panel;
            const float widthShare = 0.76f;
            float width = 1080f * widthShare - 2f * 50f;
            float y = 34f;

            var ic = AccountUi.Row(card, "Icon", y, iconSize.y, 0, 0);
            AccountUi.Icon(kit, ic, icon, Color.white, 0.5f, 0.5f, iconSize.x, iconSize.y);
            y += iconSize.y + 14f;

            var tr = AccountUi.Row(card, "Title", y, 84f, 40f, 40f);
            var t = kit.MakeText(tr, title, UiKit.Title, UiKit.Ink);
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            AccountUi.Fit(t, width + 20f, UiKit.Title, 52);
            y += 84f + 10f;

            const float bodyInset = 92f;   // a narrower measure balances the line breaks (no one-word last line)
            var br = AccountUi.Row(card, "Body", y, 10f, bodyInset, bodyInset);
            var b = AccountUi.Wrapped(kit, br, body, UiKit.Body, UiKit.Ink, TextAnchor.UpperCenter);
            float bh = AccountUi.WrappedHeight(b, 1080f * widthShare - 2f * bodyInset) + 6f;
            br.offsetMin = new Vector2(bodyInset, -(y + bh));
            y += bh + 14f;

            // A failure replaces the quiet note (or, without one, the body) in place: the layout never shifts.
            if (note != null)
            {
                // two Label lines tall, so the longer error fits without touching Cancel
                var nr = AccountUi.Row(card, "Note", y, 96f, 40f, 40f);
                d._note = AccountUi.Wrapped(kit, nr, note, UiKit.Label, AccountUi.InkSoft, TextAnchor.MiddleCenter);
                y += 96f + 22f;
            }
            else { d._note = b; y += 16f; }

            var cancel = kit.Pill(card, "Cancel", UiKit.Mint, 72, () => d.Dismiss());
            Stretch(cancel, y, 150f, 44f);
            d._cancel = cancel.gameObject.AddComponent<CanvasGroup>();
            y += 150f + 30f;

            var act = kit.Pill(card, action, UiKit.Coral, UiKit.Body, () => d.Run());
            act.anchorMin = act.anchorMax = new Vector2(0.5f, 1f);
            act.pivot = new Vector2(0.5f, 1f);
            act.sizeDelta = new Vector2(width * 0.62f, 124f);
            act.anchoredPosition = new Vector2(0f, -y);
            d._action = act;
            d._actionLabel = act.GetComponentInChildren<Text>();
            var spin = kit.MakeImage(act, AccountArt.Spinner, UiKit.Ink);
            spin.gameObject.AddComponent<Spinner>();
            d._spinner = spin.gameObject;
            d._spinner.SetActive(false);
            y += 124f + 44f;

            AccountUi.Panel(kit, card, UiKit.CardCream, 100f);
            d._overlay.CenterCard(widthShare, y, 0.46f);
            d._overlay.Enter();
            return d;
        }

        static void Stretch(RectTransform rt, float top, float h, float inset)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(inset, -(top + h));
            rt.offsetMax = new Vector2(-inset, -top);
        }

        public void Dismiss()
        {
            if (_busy || _overlay == null) return;
            _overlay.Close(() => Destroy(gameObject));
        }

        void Run()
        {
            if (_busy) return;
            _ = RunAsync();
        }

        /// <summary>Busy look (also used by editor captures).</summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
            _cancel.alpha = busy ? 0.45f : 1f;
            _cancel.interactable = !busy;
            _cancel.blocksRaycasts = !busy;
            // spinner + label centred together inside the unchanged pill
            _actionLabel.text = busy ? _busyLabel : _actionText;
            _spinner.SetActive(busy);
            float lw = AccountUi.Width(_actionLabel), s = 44f, gap = 16f;
            var lrt = _actionLabel.rectTransform;
            lrt.anchoredPosition = busy ? new Vector2((s + gap) * 0.5f, 0f) : Vector2.zero;
            var srt = (RectTransform)_spinner.transform;
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.53f);
            srt.sizeDelta = new Vector2(s, s);
            srt.anchoredPosition = new Vector2(-(lw + gap + s) * 0.5f + s * 0.5f, 0f);
        }

        public void ShowError()
        {
            _note.text = _error;
            _note.color = UiKit.Ink;
        }

        async Task RunAsync()
        {
            SetBusy(true);
            try
            {
                await _run();
                // Success resets the client and reloads the scene into a fresh guest; nothing else to do here.
            }
            catch (Exception e)
            {
                Debug.Log("[Orcub] account action failed: " + e.Message);
                if (this == null) return;
                SetBusy(false);
                ShowError();
            }
        }
    }
}
