using System;
using System.Collections;
using System.Threading.Tasks;
using Orcub.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Orcub.Game
{
    /// <summary>
    /// Owner of the gold-bar shop UI (approved shop_v1): the shop (shop_A), the starter offer (starter_offer_A),
    /// the purchase receipt + counter burst (purchase_success_B) and the 40-gold booster sheet. Created once by
    /// BoardView; screens reach it through the static entry points.
    /// </summary>
    public sealed class ShopFlow : MonoBehaviour
    {
        public const int ShopOrder = 600, ReceiptOrder = 650;

        public static ShopFlow Instance { get; private set; }

        UiKit _kit;
        Action _openSettings;
        Func<bool> _inLevel;
        Action<string> _toast;
        readonly ShopModel _model = new ShopModel(() => ShopGateway.Current);
        AccountOverlay _shop, _starter, _booster;
        ShopReceipt _receipt;
        Action _onShopClosed;

        public ShopModel Model => _model;
        public bool ShopOpen => _shop != null;

        public static ShopFlow Create(Transform parent, UiKit kit, Action openSettings, Func<bool> inLevel, Action<string> toast)
        {
            var go = new GameObject("ShopFlow");
            go.transform.SetParent(parent, false);
            var f = go.AddComponent<ShopFlow>();
            f._kit = kit;
            f._openSettings = openSettings;
            f._inLevel = inLevel;
            f._toast = toast;
            f._model.Succeeded += f.OnPurchased;
            Instance = f;
            return f;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ entry points

        public static void OpenShop(Action onClosed = null) => Instance?.ShowShop(onClosed);

        /// <summary>The server's starter offer, once per its cooldown, at a calm moment (home after a win).</summary>
        public static bool TryOfferStarter()
        {
            var f = Instance;
            var port = ShopGateway.Current;
            if (f == null || port == null || f._shop != null || f._starter != null) return false;
            if (!port.StarterOffered || !port.CanPresentStarter(DateTimeOffset.UtcNow)) return false;
            port.MarkStarterPresented();
            f.ShowStarter();
            return true;
        }

        public static void OfferBooster(BoosterKind kind, Action onBought) => Instance?.ShowBooster(kind, onBought);

        // ------------------------------------------------------------------ shop (shop_A)

        public void ShowShop(Action onClosed = null)
        {
            if (_shop != null) return;
            _onShopClosed = onClosed;
            _model.ClearMessage();
            _shop = ShopScreen.Build(transform, _kit, _model, CloseShop, () => { CloseShop(); _openSettings?.Invoke(); });
        }

        public void CloseShop()
        {
            if (_shop == null) return;
            var o = _shop;
            _shop = null;
            var after = _onShopClosed;
            _onShopClosed = null;
            o.Close(after);
        }

        // ------------------------------------------------------------------ starter (starter_offer_A)

        public void ShowStarter()
        {
            if (_starter != null) return;
            _model.ClearMessage();
            _starter = StarterOffer.Build(transform, _kit, _model, () =>
            {
                var o = _starter;
                _starter = null;
                o?.Close();
            });
        }

        // ------------------------------------------------------------------ booster for 40 gold

        public void ShowBooster(BoosterKind kind, Action onBought)
        {
            if (_booster != null) return;
            _booster = BoosterSheet.Build(transform, _kit, kind, () =>
            {
                var o = _booster;
                _booster = null;
                o?.Close();
            }, onBought);
        }

        // ------------------------------------------------------------------ success (purchase_success_B)

        void OnPurchased(string productId, long before)
        {
            long after = ShopGateway.Current?.Gold ?? before;
            var item = ShopCatalog.Find(productId);
            if (after <= before && item != null && item.Gold > 0) after = before + item.Gold;   // wallet refresh still in flight
            string line = productId == ShopCatalog.RemoveAds ? "Ads between levels are gone"
                : productId == ShopCatalog.Starter ? $"{ShopCatalog.Gold(ShopCatalog.StarterGold)} gold and boosters added"
                : $"{ShopCatalog.Gold(item?.Gold ?? after - before)} gold added";
            if (_starter != null) { var s = _starter; _starter = null; s.Close(); }
            CloseShop();
            TopBar.CelebrateGold(before, after);
            if (_inLevel != null && _inLevel())
            {
                _toast?.Invoke(line);
                return;
            }
            ShowReceipt(line);
        }

        public void ShowReceipt(string line)
        {
            if (_receipt != null) Destroy(_receipt.gameObject);
            _receipt = ShopReceipt.Show(transform, _kit, line);
        }
    }

    /// <summary>Layout helpers shared by the shop sheets (top-down canvas units, AccountUi conventions).</summary>
    static class ShopUi
    {
        public static readonly Color Rule = new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.14f);
        public static readonly Color RowCream = new Color(0.99f, 0.93f, 0.83f);
        public static readonly Color BadgeMint = new Color(0.62f, 0.88f, 0.66f);
        public static readonly Color BadgeCoral = new Color(0.96f, 0.62f, 0.55f);

        /// <summary>Close X (cream round clay button, coral glyph) in a card's top-right corner.</summary>
        public static RectTransform Close(UiKit kit, RectTransform card, float top, float right, Action onClick)
        {
            var row = UiKit.Rect("CloseSlot", card);
            row.anchorMin = row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(1f, 1f);
            row.sizeDelta = new Vector2(124f, 124f);
            row.anchoredPosition = new Vector2(-right, -top);
            var b = AccountUi.CloseButton(kit, row, onClick);
            UiKit.Anchor(b, 0, 0, 1, 1);
            return row;
        }

        /// <summary>Glyph left of a single text line, the pair centred in <paramref name="parent"/> or left aligned.</summary>
        public static Text IconLine(UiKit kit, RectTransform parent, Sprite icon, Color tint, float iconW, float iconH,
            string text, int size, Color color, bool left = false, float gap = 16f)
        {
            if (!left) return AccountUi.IconLabel(kit, parent, icon, tint, iconW, iconH, text, size, color, gap).label;
            var t = kit.MakeText(parent, text, size, color, TextAnchor.MiddleLeft);
            UiKit.Anchor(t.rectTransform, 0, 0, 1, 1);
            t.rectTransform.offsetMin = new Vector2(icon != null ? iconW + gap : 0f, 0f);
            if (icon != null)
            {
                var img = kit.MakeImage(parent, icon, tint);
                var irt = img.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                irt.pivot = new Vector2(0f, 0.5f);
                irt.sizeDelta = new Vector2(iconW, iconH);
                irt.anchoredPosition = Vector2.zero;
            }
            return t;
        }

        /// <summary>A cream price pill (label only, or tappable when <paramref name="onClick"/> is set).</summary>
        public static (RectTransform rt, Text label) PricePill(UiKit kit, RectTransform parent, string price, Action onClick, int size = UiKit.Title)
        {
            var rt = UiKit.Rect("Price", parent);
            var img = kit.MakeImage(rt, kit.Art.uiPill, UiKit.CardCream, sliced: true);
            UiKit.Anchor(img.rectTransform, 0, 0, 1, 1);
            var t = kit.MakeText(rt, price, size, UiKit.Ink);
            UiKit.Anchor(t.rectTransform, 0, 0.06f, 1, 0.94f);
            if (onClick != null)
            {
                img.raycastTarget = true;
                UiKit.Tap(rt.gameObject, onClick);
            }
            return (rt, t);
        }

        /// <summary>Small puffy ribbon badge ("Most popular", "One-time offer").</summary>
        public static RectTransform Ribbon(UiKit kit, RectTransform parent, string label, Color tint, int size = UiKit.Caption)
        {
            var rt = UiKit.Rect("Ribbon", parent);
            var art = kit.Art;
            var img = kit.MakeImage(rt, art.ribbon != null ? art.ribbon : art.uiPill, tint, sliced: true);
            if (art.ribbon != null) img.pixelsPerUnitMultiplier = 2.2f;
            UiKit.Anchor(img.rectTransform, 0, 0, 1, 1);
            var t = kit.MakeText(rt, label, size, UiKit.Ink);
            UiKit.Anchor(t.rectTransform, 0, 0.04f, 1, 0.96f);
            float w = t.preferredWidth + 64f;
            rt.sizeDelta = new Vector2(w, rt.sizeDelta.y);
            return rt;
        }

        /// <summary>Raised inner card (cream, rounded) whose face matches the rect.</summary>
        public static void InnerCard(UiKit kit, RectTransform rt, float corner = 64f, Color? tint = null)
            => AccountUi.Panel(kit, rt, tint ?? RowCream, corner);

        public static Sprite GoldIcon(OrcubArt art) => art.goldBar != null ? art.GoldBarTight : art.uiCogClay;

        public static void SetInteractable(RectTransform rt, bool on)
        {
            if (rt == null) return;
            var g = rt.GetComponent<CanvasGroup>();
            if (g == null) g = rt.gameObject.AddComponent<CanvasGroup>();
            g.interactable = g.blocksRaycasts = on;
            g.alpha = on ? 1f : 0.6f;
        }
    }

    /// <summary>Approved shop_A: starter card, four gold packs (select one), one Buy action, Remove Ads, Restore.</summary>
    public sealed class ShopScreen : MonoBehaviour
    {
        UiKit _kit;
        ShopModel _model;
        Text _buy, _message, _restore;
        RectTransform _buyRt, _list, _content;
        readonly System.Collections.Generic.Dictionary<string, (Image check, Text price, RectTransform row)> _rows =
            new System.Collections.Generic.Dictionary<string, (Image, Text, RectTransform)>();
        Text _starterPrice, _adsPrice;
        RectTransform _starterPill, _adsPill;
        float _priceRefresh;

        const float Pad = 40f;

        public static AccountOverlay Build(Transform parent, UiKit kit, ShopModel model, Action close, Action settings)
        {
            var o = AccountOverlay.Create(parent, kit, "Shop", ShopFlow.ShopOrder, 0f, felt: true);
            var s = o.gameObject.AddComponent<ShopScreen>();
            s._kit = kit;
            s._model = model;
            TopBar.Create(o.Safe, kit, kit.Art.uiCogClay, UiKit.Brass, settings);

            var panel = o.Panel;
            panel.anchorMin = new Vector2(0.04f, 0f);
            panel.anchorMax = new Vector2(0.96f, 0.902f);
            panel.pivot = new Vector2(0.5f, 1f);   // top-pinned: the enter tween and the height fit never fight
            panel.offsetMin = new Vector2(0f, 26f);
            panel.offsetMax = Vector2.zero;
            AccountUi.Panel(kit, panel, UiKit.CardCream, 104f);
            s.BuildContent(panel, close);
            model.Changed += s.Refresh;
            s.Refresh();
            o.Enter();
            return o;
        }

        void OnDestroy() { if (_model != null) _model.Changed -= Refresh; }

        void BuildContent(RectTransform panel, Action close)
        {
            var kit = _kit;
            var art = kit.Art;
            var port = ShopGateway.Current;

            // scrolls only when the phone is too short for the whole list (never on iPhone 12 Pro)
            var viewport = UiKit.Rect("Viewport", panel);
            UiKit.Anchor(viewport, 0, 0, 1, 1);
            viewport.offsetMax = new Vector2(0f, -166f);
            viewport.offsetMin = new Vector2(0f, 8f);
            viewport.gameObject.AddComponent<RectMask2D>().padding = new Vector4(-40f, 0f, -40f, 0f);
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            _content = UiKit.Rect("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = _content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.viewport = viewport;

            // header (fixed): title + close
            var head = AccountUi.Row(panel, "Head", 30f, 124f, Pad, Pad);
            var title = kit.MakeText(head, "Shop", 100, UiKit.Ink);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            ShopUi.Close(kit, panel, 34f, 34f, close);

            float y = 6f;
            if (port != null && port.StarterOffered) y = BuildStarter(y) + 26f;

            foreach (var item in ShopCatalog.Packs) y = BuildPack(item, y) + 18f;
            y += 8f;

            var buy = kit.Pill(_content, "Buy", UiKit.Mint, 66, () => _ = _model.Buy(_model.Selected));
            _buyRt = AccountUi.Row(_content, "BuyRow", y, 144f, Pad + 8f, Pad + 8f);
            buy.SetParent(_buyRt, false);
            UiKit.Anchor(buy, 0, 0, 1, 1);
            _buy = buy.GetComponentInChildren<Text>();
            y += 144f;

            // message line (reserved, so a pending / failed note never shifts the layout)
            var msgRow = AccountUi.Row(_content, "Message", y + 4f, 56f, Pad, Pad);
            _message = kit.MakeText(msgRow, "", UiKit.Caption, AccountUi.InkSoft);
            UiKit.Anchor(_message.rectTransform, 0, 0, 1, 1);
            _message.horizontalOverflow = HorizontalWrapMode.Wrap;
            y += 64f;

            y = BuildRemoveAds(y) + 14f;

            var restoreRow = AccountUi.Row(_content, "Restore", y, 96f, 200f, 200f);
            var link = kit.Link(restoreRow, "Restore purchases", UiKit.Ink, () => _ = _model.Restore());
            UiKit.Anchor(link, 0, 0, 1, 1);
            _restore = link.GetComponentInChildren<Text>();
            _restore.fontSize = UiKit.Body;
            y += 96f + 20f;

            _content.sizeDelta = new Vector2(0f, y);
            _panel = panel;
            _wanted = y + 166f + 16f;   // header + list + bottom inset
        }

        RectTransform _panel;
        float _wanted, _fitFor = -1f;

        /// <summary>The card hugs its list (approved shop_A) and only scrolls when the phone is too short.</summary>
        void FitPanel()
        {
            var parent = (RectTransform)_panel.parent;
            float h = parent.rect.height;
            if (h <= 0f || Mathf.Approximately(h, _fitFor)) return;
            _fitFor = h;
            float top = h * _panel.anchorMax.y, available = top - 26f;
            _panel.offsetMin = new Vector2(0f, Mathf.Max(26f, top - Mathf.Min(available, _wanted)));
        }

        float BuildStarter(float y)
        {
            var kit = _kit;
            var art = kit.Art;
            const float h = 432f;
            var card = AccountUi.Row(_content, "Starter", y, h, Pad, Pad);
            ShopUi.InnerCard(kit, card, 72f);
            var bundle = kit.MakeImage(card, art.starterBundle != null ? art.starterBundle : ShopUi.GoldIcon(art), Color.white);
            UiKit.Anchor(bundle.rectTransform, 0.02f, 0.08f, 0.34f, 0.92f);
            AccountUi.VRule(kit, card, 0.36f, 0.1f, 0.9f);

            var col = UiKit.Rect("Text", card);
            UiKit.Anchor(col, 0.39f, 0f, 0.98f, 1f);
            float top = 26f;
            const float colW = 0.59f * (1080f * 0.92f - 2f * Pad);   // text column width in canvas units
            Text Line(string text, int size, float height, Sprite icon = null, Color? tint = null, float iw = 0f, float ih = 0f)
            {
                var r = AccountUi.Row(col, "Line", top, height, 0f, 0f);
                top += height;
                var t = ShopUi.IconLine(kit, r, icon, tint ?? Color.white, iw, ih, text, size, UiKit.Ink, left: true, gap: 12f);
                AccountUi.Fit(t, colW - (icon != null ? iw + 12f : 0f) - 12f, size, UiKit.Caption);
                return t;
            }
            Line("Starter pack", 72, 88f);
            Line("One-time purchase", UiKit.Caption + 2, 46f);
            Line($"{ShopCatalog.StarterGold} gold", UiKit.Label, 58f, ShopUi.GoldIcon(art), Color.white, 70f, 42f);
            Line("3 Hint · 3 Undo · 3 Unclamp", UiKit.Label, 54f);
            Line("1h unlimited lives", UiKit.Label, 58f, art.uiHeartClay, UiKit.HeartRed, 52f, 46f);
            top += 10f;
            var pillRow = AccountUi.Row(col, "Buy", top, 104f, 20f, 50f);
            (_starterPill, _starterPrice) = ShopUi.PricePill(kit, pillRow, _model.Price(ShopCatalog.Starter), () => _ = _model.Buy(ShopCatalog.Starter));
            UiKit.Anchor(_starterPill, 0, 0, 1, 1);
            return y + h;
        }

        float BuildPack(ShopItem item, float y)
        {
            var kit = _kit;
            var art = kit.Art;
            const float h = 148f;
            var row = AccountUi.Row(_content, item.Id, y, h, Pad, Pad);
            var bg = AccountUi.Panel(kit, row, ShopUi.RowCream, 60f);
            bg.raycastTarget = true;
            UiKit.Tap(row.gameObject, () => _model.Select(item.Id), 0.5f);

            int idx = Array.IndexOf(ShopCatalog.Packs, item);
            Sprite packArt = art.goldPacks != null && idx >= 0 && idx < art.goldPacks.Length && art.goldPacks[idx] != null
                ? art.goldPacks[idx] : ShopUi.GoldIcon(art);
            var icon = kit.MakeImage(row, packArt, Color.white);
            UiKit.Anchor(icon.rectTransform, 0.02f, -0.12f, 0.26f, 1.12f);
            AccountUi.VRule(kit, row, 0.285f, 0.18f, 0.82f);

            var label = kit.MakeText(row, $"{ShopCatalog.Gold(item.Gold)} gold", UiKit.Title, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(label.rectTransform, 0.32f, item.Badge != null ? 0.06f : 0.1f, 0.7f, item.Badge != null ? 0.6f : 0.9f);
            if (item.Badge != null)
            {
                var ribbon = ShopUi.Ribbon(kit, row, item.Badge, item.Badge == "Best value" ? ShopUi.BadgeCoral : ShopUi.BadgeMint);
                ribbon.anchorMin = ribbon.anchorMax = new Vector2(0.32f, 0.78f);
                ribbon.pivot = new Vector2(0f, 0.5f);
                ribbon.sizeDelta = new Vector2(ribbon.sizeDelta.x, 60f);
                ribbon.anchoredPosition = Vector2.zero;
            }

            var pillSlot = UiKit.Rect("PriceSlot", row);
            UiKit.Anchor(pillSlot, 0.69f, 0.16f, 0.975f, 0.84f);
            var (pill, price) = ShopUi.PricePill(kit, pillSlot, _model.Price(item.Id), null);
            UiKit.Anchor(pill, 0, 0, 1, 1);

            var check = kit.MakeImage(row, art.uiRound, UiKit.Mint);
            var crt = check.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.sizeDelta = new Vector2(72f, 72f);
            crt.anchoredPosition = new Vector2(-30f, -14f);
            var tick = kit.MakeImage(check.transform, AccountArt.Check, UiKit.Ink);
            UiKit.Anchor(tick.rectTransform, 0.24f, 0.26f, 0.76f, 0.78f);
            _rows[item.Id] = (check, price, row);
            return y + h;
        }

        float BuildRemoveAds(float y)
        {
            var kit = _kit;
            var art = kit.Art;
            const float h = 214f;
            var card = AccountUi.Row(_content, "RemoveAds", y, h, Pad, Pad);
            ShopUi.InnerCard(kit, card, 64f);
            var tv = kit.MakeImage(card, art.removeAdsArt != null ? art.removeAdsArt : art.uiFilm, art.removeAdsArt != null ? Color.white : UiKit.Coral);
            UiKit.Anchor(tv.rectTransform, 0.02f, 0.1f, 0.25f, 0.9f);
            AccountUi.VRule(kit, card, 0.275f, 0.14f, 0.86f);

            var col = UiKit.Rect("Text", card);
            UiKit.Anchor(col, 0.305f, 0f, 0.975f, 1f);
            var t1 = kit.MakeText(AccountUi.Row(col, "Title", 20f, 64f, 0f, 250f), "Remove Ads", 58, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(t1.rectTransform, 0, 0, 1, 1);
            var t2 = kit.MakeText(AccountUi.Row(col, "Price", 86f, 46f, 0f, 250f), $"{_model.Price(ShopCatalog.RemoveAds)} · One-time", UiKit.Caption + 2, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(t2.rectTransform, 0, 0, 1, 1);
            var noteRow = AccountUi.Row(col, "Note", 136f, 66f, 0f, 10f);
            var note = AccountUi.Wrapped(kit, noteRow, "Removes ads between levels.\nOptional reward videos stay.", 29, AccountUi.InkSoft, TextAnchor.UpperLeft);
            note.lineSpacing = 0.95f;

            var slot = UiKit.Rect("PriceSlot", card);
            UiKit.Anchor(slot, 0.73f, 0.47f, 0.975f, 0.88f);
            (_adsPill, _adsPrice) = ShopUi.PricePill(kit, slot, _model.Price(ShopCatalog.RemoveAds), () => _ = _model.Buy(ShopCatalog.RemoveAds));
            UiKit.Anchor(_adsPill, 0, 0, 1, 1);
            _adsCaption = t2;
            return y + h;
        }

        Text _adsCaption;

        void Update()
        {
            if (_panel != null) FitPanel();
            _priceRefresh -= Time.unscaledDeltaTime;
            if (_priceRefresh > 0f) return;
            _priceRefresh = 1f;
            Refresh();   // StoreKit prices / remove-ads entitlement can arrive while the shop is open
        }

        void Refresh()
        {
            if (_buy == null) return;
            var port = _model.Port;
            foreach (var kv in _rows)
            {
                bool sel = kv.Key == _model.Selected;
                kv.Value.check.gameObject.SetActive(sel);
                kv.Value.price.text = _model.Price(kv.Key);
            }
            var item = ShopCatalog.Find(_model.Selected);
            _buy.text = _model.BusyProduct != null && _model.BusyProduct != ShopCatalog.Starter && _model.BusyProduct != ShopCatalog.RemoveAds
                ? "Purchasing…"
                : $"Buy {ShopCatalog.Gold(item?.Gold ?? 0)} gold · {_model.Price(_model.Selected)}";
            AccountUi.Fit(_buy, _buyRt.rect.width > 10f ? _buyRt.rect.width - 90f : 820f, 66, UiKit.Body);
            if (_starterPrice != null) _starterPrice.text = _model.BusyProduct == ShopCatalog.Starter ? "…" : _model.Price(ShopCatalog.Starter);
            bool owned = port != null && port.RemoveAds;
            if (_adsPrice != null)
            {
                _adsPrice.text = owned ? "Owned" : _model.BusyProduct == ShopCatalog.RemoveAds ? "…" : _model.Price(ShopCatalog.RemoveAds);
                _adsPrice.fontSize = owned ? UiKit.Body : UiKit.Title;
                _adsCaption.text = owned ? "Thanks for your support" : $"{_model.Price(ShopCatalog.RemoveAds)} · One-time";
            }
            bool busy = _model.Busy;
            ShopUi.SetInteractable(_buyRt, !busy);
            ShopUi.SetInteractable(_starterPill, !busy);
            ShopUi.SetInteractable(_adsPill, !busy && !owned);
            if (_adsPill != null && owned) _adsPill.GetComponent<CanvasGroup>().alpha = 1f;
            _restore.text = _model.Restoring ? "Restoring…" : "Restore purchases";
            _message.text = _model.Message ?? "";
            _message.color = _model.MessageTone == ShopModel.Tone.Error ? AccountUi.DeleteRed : AccountUi.InkSoft;
        }
    }

    /// <summary>Approved starter_offer_A: floating card with gold, three booster tiles, unlimited lives, one Buy.</summary>
    public static class StarterOffer
    {
        public static AccountOverlay Build(Transform parent, UiKit kit, ShopModel model, Action close)
        {
            var art = kit.Art;
            var o = AccountOverlay.Create(parent, kit, "StarterOffer", ShopFlow.ShopOrder, 0.55f, onScrimTap: close);
            var card = o.Panel;
            float y = 40f;
            var head = AccountUi.Row(card, "Head", y, 112f, 130f, 130f);
            var title = kit.MakeText(head, "Starter pack", UiKit.Display, UiKit.Ink);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            ShopUi.Close(kit, card, 34f, 34f, close);
            y += 112f + 8f;

            var ribbonRow = AccountUi.Row(card, "Ribbon", y, 64f, 0f, 0f);
            var ribbon = ShopUi.Ribbon(kit, ribbonRow, "One-time offer", ShopUi.BadgeCoral, UiKit.Label);
            ribbon.anchorMin = ribbon.anchorMax = new Vector2(0.5f, 0.5f);
            ribbon.pivot = new Vector2(0.5f, 0.5f);
            ribbon.sizeDelta = new Vector2(ribbon.sizeDelta.x, 64f);
            ribbon.anchoredPosition = Vector2.zero;
            y += 64f + 12f;

            var goldRow = AccountUi.Row(card, "Gold", y, 210f, 0f, 0f);
            var gold = kit.MakeImage(goldRow, art.goldBar != null ? art.goldBar : art.uiCogClay, Color.white);
            UiKit.Place(gold.rectTransform, 0.5f, 0.46f, 330f, 330f);
            y += 210f;
            var amount = kit.MakeText(AccountUi.Row(card, "Amount", y, 104f, 0f, 0f), $"{ShopCatalog.StarterGold} gold", 88, UiKit.Ink);
            UiKit.Anchor(amount.rectTransform, 0, 0, 1, 1);
            y += 104f + 22f;

            var tiles = AccountUi.Row(card, "Boosters", y, 214f, 46f, 46f);
            (Sprite icon, string label)[] items = { (art.iconHint, "Hint"), (art.iconUndo, "Undo"), (art.iconUnclamp, "Unclamp") };
            for (int i = 0; i < 3; i++)
            {
                var tile = UiKit.Rect(items[i].label, tiles);
                UiKit.Anchor(tile, i / 3f, 0f, (i + 1) / 3f, 1f);
                tile.offsetMin = new Vector2(i == 0 ? 0f : 10f, 0f);
                tile.offsetMax = new Vector2(i == 2 ? 0f : -10f, 0f);
                ShopUi.InnerCard(kit, tile, 56f);
                var ic = kit.MakeImage(tile, items[i].icon, new Color(1f, 0.98f, 0.95f));
                UiKit.Anchor(ic.rectTransform, 0.2f, 0.34f, 0.8f, 0.94f);
                UiKit.Shadow(ic, 0.25f);
                var t = kit.MakeText(tile, $"{items[i].label} ×{ShopCatalog.StarterBoosters}", UiKit.Label, UiKit.Ink);
                UiKit.Anchor(t.rectTransform, 0.02f, 0.06f, 0.98f, 0.34f);
                AccountUi.Fit(t, 270f, UiKit.Label, UiKit.Caption);
            }
            y += 214f + 22f;

            var lives = AccountUi.Row(card, "Lives", y, 122f, 46f, 46f);
            ShopUi.InnerCard(kit, lives, 56f);
            AccountUi.IconLabel(kit, lives, art.infinityHeart != null ? art.infinityHeart : art.uiHeartClay,
                art.infinityHeart != null ? Color.white : UiKit.HeartRed, 104f, 104f, "1h unlimited lives", UiKit.Title, UiKit.Ink, 24f);
            y += 122f + 22f;

            var note = kit.MakeText(AccountUi.Row(card, "Note", y, 48f, 0f, 0f), "One-time purchase", UiKit.Label, AccountUi.InkSoft);
            UiKit.Anchor(note.rectTransform, 0, 0, 1, 1);
            y += 48f + 12f;

            var buyRow = AccountUi.Row(card, "Buy", y, 144f, 46f, 46f);
            var buy = kit.Pill(buyRow, "Buy", UiKit.Mint, UiKit.Title, () => { });
            UiKit.Anchor(buy, 0, 0, 1, 1);
            var label = buy.GetComponentInChildren<Text>();
            y += 144f + 8f;
            var msg = kit.MakeText(AccountUi.Row(card, "Message", y, 52f, 46f, 46f), "", UiKit.Caption, AccountUi.InkSoft);
            UiKit.Anchor(msg.rectTransform, 0, 0, 1, 1);
            y += 52f + 18f;

            void Show()
            {
                label.text = model.BusyProduct == ShopCatalog.Starter ? "Purchasing…" : $"Buy starter pack · {model.Price(ShopCatalog.Starter)}";
                AccountUi.Fit(label, 820f, UiKit.Title, UiKit.Body);
                msg.text = model.Message ?? "";
                msg.color = model.MessageTone == ShopModel.Tone.Error ? AccountUi.DeleteRed : AccountUi.InkSoft;
                ShopUi.SetInteractable(buy, !model.Busy);
            }
            var b = buy.GetComponent<Button>();
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(() => { Haptics.Play(Haptics.Impact.Light, 0.6f); _ = model.Buy(ShopCatalog.Starter); });
            model.Changed += Show;
            o.gameObject.AddComponent<OnDestroyHook>().Action = () => model.Changed -= Show;
            Show();

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.BottomCard(y, 54f);
            o.Enter();
            return o;
        }
    }

    /// <summary>
    /// One booster for 40 gold. No dedicated approved image: built from the approved out_of_moves_gold_B sheet
    /// language (cream anchored card, mint gold pill, quiet helper line) and opened from an empty booster.
    /// </summary>
    public static class BoosterSheet
    {
        public static AccountOverlay Build(Transform parent, UiKit kit, BoosterKind kind, Action close, Action onBought)
        {
            var art = kit.Art;
            string name = kind == BoosterKind.Hint ? "Hint" : kind == BoosterKind.Undo ? "Undo" : "Unclamp";
            Sprite glyph = kind == BoosterKind.Hint ? art.iconHint : kind == BoosterKind.Undo ? art.iconUndo : art.iconUnclamp;
            // review m, M4: while a purchase is in flight the sheet can't be closed (X and scrim are inert), and a
            // confirmed purchase always refreshes the tray, even if the sheet went away meanwhile
            var spend = new GoldSpend();
            void TryClose() { if (!spend.Busy) close(); }
            var o = AccountOverlay.Create(parent, kit, "BoosterSheet", ShopFlow.ShopOrder, 0.5f, onScrimTap: TryClose);
            var card = o.Panel;
            float y = 40f;
            var head = AccountUi.Row(card, "Head", y, 132f, 52f, 150f);
            var disc = kit.MakeImage(head, art.uiRound, UiKit.Coral);
            var drt = disc.rectTransform;
            drt.anchorMin = drt.anchorMax = new Vector2(0f, 0.5f);
            drt.pivot = new Vector2(0f, 0.5f);
            drt.sizeDelta = new Vector2(132f, 132f);
            var g = kit.MakeImage(disc.transform, glyph, UiKit.Cream);
            UiKit.Anchor(g.rectTransform, 0.22f, 0.22f, 0.78f, 0.78f);
            var title = kit.MakeText(head, $"Get a {name}", UiKit.Display, UiKit.Ink, TextAnchor.MiddleLeft);
            UiKit.Anchor(title.rectTransform, 0, 0, 1, 1);
            title.rectTransform.offsetMin = new Vector2(160f, 0f);
            var x = ShopUi.Close(kit, card, 34f, 34f, TryClose);
            y += 132f + 30f;

            var buyRow = AccountUi.Row(card, "Buy", y, 150f, 46f, 46f);
            var buy = kit.Pill(buyRow, $"1 {name} for {ShopCatalog.BoosterGold} gold", UiKit.Mint, UiKit.Title, () => { }, ShopUi.GoldIcon(art), Color.white);
            UiKit.Anchor(buy, 0, 0, 1, 1);
            var label = buy.GetComponentInChildren<Text>();
            string idle = label.text;
            y += 150f + 14f;
            var helper = kit.MakeText(AccountUi.Row(card, "Helper", y, 54f, 46f, 46f), "", UiKit.Label, UiKit.Ink);
            UiKit.Anchor(helper.rectTransform, 0, 0, 1, 1);
            void Balance() => helper.text = $"You have {ShopCatalog.Gold(ShopGateway.Current?.Gold ?? 0)} gold";
            Balance();
            y += 54f + 40f;

            var b = buy.GetComponent<Button>();
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(async () =>
            {
                var port = ShopGateway.Current;
                if (spend.Busy) return;
                Haptics.Play(Haptics.Impact.Light, 0.6f);
                if (port == null) { helper.text = "Not available right now"; return; }
                label.text = "Buying…";
                ShopUi.SetInteractable(buy, false);
                ShopUi.SetInteractable(x, false);
                var r = await spend.Run(key => port.BuyBooster(name.ToLowerInvariant(), key),
                    () => ShopFlow.OpenShop(Balance), port.Gold, ShopCatalog.BoosterGold);
                if (r.Ok) onBought?.Invoke();   // the tray shows the new charge whatever happened to the sheet
                if (o == null) return;
                label.text = idle;
                ShopUi.SetInteractable(buy, true);
                ShopUi.SetInteractable(x, true);
                if (r.Ok) { Haptics.Notify(Haptics.Notice.Success); close(); return; }
                if (spend.Error != null) { helper.text = spend.Error; Haptics.Notify(Haptics.Notice.Warning); }
                else Balance();
            });

            AccountUi.Panel(kit, card, UiKit.CardCream, 104f);
            o.BottomCard(y, 54f);
            o.Enter();
            return o;
        }
    }

    /// <summary>Approved purchase_success_B receipt: a low, non-modal card (gold | "Purchase complete" + line | X).</summary>
    public sealed class ShopReceipt : MonoBehaviour
    {
        RectTransform _card;
        CanvasGroup _group;
        bool _closing;

        public static ShopReceipt Show(Transform parent, UiKit kit, string line, float seconds = 4.5f)
        {
            var (canvas, safe) = UiKit.Screen("Receipt", parent, ShopFlow.ReceiptOrder);
            var r = canvas.gameObject.AddComponent<ShopReceipt>();
            var art = kit.Art;
            var card = UiKit.Rect("Card", safe);
            card.anchorMin = new Vector2(0.08f, 0f);
            card.anchorMax = new Vector2(0.92f, 0f);
            card.pivot = new Vector2(0.5f, 0f);
            card.sizeDelta = new Vector2(0f, 196f);
            card.anchoredPosition = new Vector2(0f, 44f);
            r._card = card;
            var bg = AccountUi.Panel(kit, card, UiKit.CardCream, 80f);
            bg.raycastTarget = true;
            var gold = kit.MakeImage(card, ShopUi.GoldIcon(art), Color.white);
            UiKit.Anchor(gold.rectTransform, 0.04f, 0.18f, 0.24f, 0.82f);
            AccountUi.VRule(kit, card, 0.275f, 0.2f, 0.8f);
            var title = kit.MakeText(card, "Purchase complete", UiKit.Body + 4, UiKit.Ink, TextAnchor.LowerLeft);
            UiKit.Anchor(title.rectTransform, 0.31f, 0.52f, 0.84f, 0.9f);
            var sub = kit.MakeText(card, line, UiKit.Label, UiKit.Ink, TextAnchor.UpperLeft);
            UiKit.Anchor(sub.rectTransform, 0.31f, 0.12f, 0.98f, 0.48f);
            AccountUi.Fit(sub, 560f, UiKit.Label, UiKit.Caption);
            var close = UiKit.Rect("Close", card);
            close.anchorMin = close.anchorMax = new Vector2(1f, 1f);
            close.pivot = new Vector2(0.5f, 0.5f);
            close.sizeDelta = new Vector2(118f, 118f);
            close.anchoredPosition = new Vector2(-74f, -66f);
            var x = AccountUi.CloseButton(kit, close, r.Dismiss);
            UiKit.Anchor(x, 0.08f, 0.08f, 0.92f, 0.92f);
            r._group = card.gameObject.AddComponent<CanvasGroup>();
            r.StartCoroutine(r.Life(seconds));
            return r;
        }

        IEnumerator Life(float seconds)
        {
            float t = 0f;
            var baseY = _card.anchoredPosition.y;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / 0.22f;
                float e = UiKit.EaseOut(Mathf.Clamp01(t));
                _group.alpha = e;
                _card.anchoredPosition = new Vector2(0f, baseY - 40f * (1f - e));
                yield return null;
            }
            yield return new WaitForSecondsRealtime(seconds);
            Dismiss();
        }

        public void Dismiss()
        {
            if (_closing || this == null) return;
            _closing = true;
            StartCoroutine(Out());
        }

        IEnumerator Out()
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / 0.14f;
                _group.alpha = 1f - UiKit.EaseIn(Mathf.Clamp01(t));
                yield return null;
            }
            Destroy(gameObject);
        }
    }

    /// <summary>Runs an action when its GameObject is destroyed (unsubscribing sheet handlers).</summary>
    public sealed class OnDestroyHook : MonoBehaviour
    {
        public Action Action;
        void OnDestroy() => Action?.Invoke();
    }
}
