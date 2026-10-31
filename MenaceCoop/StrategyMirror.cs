using System;
using HarmonyLib;
using Il2CppMenace.Items;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.UI;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling

namespace MenaceCoop;

// LIVE STRATEGY LAYER (v2.1): the host's strategy screens and economy mirror to
// the guest AS CHANGES HAPPEN, instead of a one-shot save-sync afterwards.
// Design philosophy (project law): the GUEST NEVER CONTROLS ANYTHING outside
// its own squads in combat -- here that is enforced at the DATA layer: every
// strategy mutation (currency vars, owned items, market stock) is blocked when
// guest-initiated and applied only as a relay of the host's own mutations.
// Screens (black market, hiring) open and close on the guest in lockstep with
// the host, so both players look at the same shop together.
internal static class StrategyMirror
{
    // True while the guest is applying a relayed host mutation -- lets the
    // mutation through the guest lock and suppresses re-broadcast.
    internal static bool Applying;

    private static bool Active()
        => Plugin.LiveStrategy.Value && CoopNet.Connected;

    // The mirrored strategy screens (by prefab name, which is also what
    // UIManager.OpenScreen takes). Squads = ArmoryUIScreen,
    // O.C.I. = ShipUpgradesUIScreen.
    private static readonly string[] MirroredScreens =
        { "BlackMarketUIScreen", "HiringUIScreen", "ArmoryUIScreen", "ShipUpgradesUIScreen" };

    // Screens the guest may also open BY ITSELF while the host isn't in a
    // mirrored screen. Squads and O.C.I. are pure inspection of shared state,
    // and locking the guest out of them entirely is what made it feel trapped;
    // the market and hiring stay host-only because their whole purpose is
    // transactions.
    private static readonly string[] GuestBrowsableScreens =
        { "ArmoryUIScreen", "ShipUpgradesUIScreen" };

    internal static bool IsMirroredScreen(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var s in MirroredScreens) if (s == name) return true;
        return false;
    }

    internal static bool IsGuestBrowsable(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var s in GuestBrowsableScreens) if (s == name) return true;
        return false;
    }

    // What the host currently has open (guest-side knowledge), or null.
    internal static string HostOpenScreen => _hostOpenScreen;

    internal static string FriendlyName(string screen)
    {
        if (screen == null) return "strategy";
        switch (screen)
        {
            case "BlackMarketUIScreen": return "black market";
            case "HiringUIScreen": return "hiring";
            case "ArmoryUIScreen": return "squads";
            case "ShipUpgradesUIScreen": return "O.C.I.";
            default: return "strategy";
        }
    }

    // Host-side: is the Squads tab (where loadouts are edited) open?
    internal static bool HostInArmory()
    {
        if (CoopNet.Mode != CoopMode.Host) return false;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return false;
            for (int i = 0; i < open.Count; i++)
                if (open[i]?.TryCast<Menace.UI.Strategy.ArmoryUIScreen>() != null) return true;
        }
        catch { }
        return false;
    }

    // Host-side: is the host currently inside ANY stateful strategy screen?
    // Auto-syncs are held while this is true (a sync load resets the guest's
    // view -- it must only happen while the host sits on navigation screens).
    internal static bool HostInStatefulScreen()
    {
        if (CoopNet.Mode != CoopMode.Host) return false;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return false;
            for (int i = 0; i < open.Count; i++)
            {
                var s = open[i];
                if (s == null) continue;
                if (s.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>() != null
                    || s.TryCast<Menace.UI.Strategy.HiringUIScreen>() != null
                    || s.TryCast<Menace.UI.Strategy.ArmoryUIScreen>() != null
                    || s.TryCast<Menace.UI.Strategy.WorkshopUIScreen>() != null
                    || s.TryCast<Menace.UI.Strategy.ShipUpgradesUIScreen>() != null) return true;
            }
        }
        catch { }
        return false;
    }

    // ---- per-frame work (CoopRunner.Update) --------------------------------
    // Host: poll the black-market CART (items staged to buy/sell) and relay it
    // when it changes, so the guest sees what is ABOUT to be purchased, not
    // just the before/after. Guest: reopen the mirrored screen after a sync
    // load reset it.
    private static string _lastCartSent;
    private static float _lastCartPollAt, _lastCartResendAt;
    private static string _hostOpenScreen;      // guest-side: what the host has open
    private static float _reopenAt = -1f;
    private static string _lastHostMirrored;    // host-side: which mirrored screen is open
    private static float _lastGuestGuardAt;

    internal static void Tick()
    {
        try { ConversationSync.Tick(); } catch (Exception e) { Plugin.Logger.LogWarning($"[CONV] Tick: {e.Message}"); }
        if (!Active()) { ViewerLocked = false; return; }
        float now = UnityEngine.Time.realtimeSinceStartup;

        if (CoopNet.Mode == CoopMode.Host)
        {
            if (now - _lastCartPollAt < 0.3f) return;
            _lastCartPollAt = now;

            // Screen lockstep by POLL, not events: the armory's tab switches
            // (hiring -> squads/OCI) do not fire CloseScreen, which left the
            // guest trapped inside a dead mirror. The current state is the
            // truth; relay its changes.
            string cur = CurrentMirroredOpen();
            if (cur != _lastHostMirrored)
            {
                if (_lastHostMirrored != null)
                    CoopNet.Send(new CoopMsg { T = "sclose", SkillId = _lastHostMirrored });
                if (cur != null)
                    CoopNet.Send(new CoopMsg { T = "sopen", SkillId = cur });
                Plugin.Logger.LogInfo($"[STRAT] Host mirrored screen: {_lastHostMirrored ?? "none"} -> {cur ?? "none"}");
                _lastHostMirrored = cur;
            }

            string payload = BuildHostCart();
            // resend periodically while non-empty so a guest whose screen was
            // reset (sync reopen) recovers the cart without asking
            if (payload != _lastCartSent || (payload.Length > 1 && now - _lastCartResendAt > 5f))
            {
                _lastCartSent = payload;
                _lastCartResendAt = now;
                CoopNet.Send(new CoopMsg { T = "scart", Data = payload });
            }
        }
        else
        {
            if (_reopenAt > 0 && now >= _reopenAt)
            {
                _reopenAt = -1f;
                if (_hostOpenScreen != null && StrategyState.Get() != null
                    && Menace.Tactical.TacticalManager.Get() == null)
                {
                    Plugin.Logger.LogInfo($"[STRAT] Reopening mirrored screen after sync: {_hostOpenScreen}");
                    HandleScreen(new CoopMsg { SkillId = _hostOpenScreen }, open: true);
                }
            }
            // Viewer guard: the guest's market screen must never be able to
            // execute a trade -- the game re-enables the button on refresh, so
            // keep disabling it.
            if (now - _lastGuestGuardAt > 0.5f)
            {
                _lastGuestGuardAt = now;
                DisableGuestTradeButton();
                // The INPUT SHIELD: while we are MIRRORING the host (the host
                // has a mirrored screen open and so do we), every game-UI click
                // is blocked at the dispatch level (see GateViewerClicks) --
                // the guest is a pure viewer there, which is what actually
                // stops "buying" for good. When the host is NOT in a mirrored
                // screen, a screen the guest opened for itself (squads /
                // O.C.I.) stays fully browsable.
                ViewerLocked = _hostOpenScreen != null && CurrentMirroredOpen() != null;
            }
        }
    }

    internal static bool ViewerLocked;

    // "LET ME CLICK" (tester request 2026-10-01; same design as Darkest Dungeon
    // Together's input-gate release). The viewer shield made the guest feel
    // locked out -- no opening commanders, units or perks while the host was in a
    // screen, and the post-operation dialog could not be answered. Whitelisting
    // each panel is a long road with a wrong turn in it, so the player can simply
    // say "let me click" for 30 s; it lapses on its own, or press again to end
    // it. Nothing campaign-changing gets through: every mutation (vars, items,
    // market, hiring, perks, dismissals, squad edits, equipment) is still refused
    // at the data layer.
    internal const float FreeClickSeconds = 30f;
    private static float _freeClicksUntil;
    private static bool _freeClicksLogged;

    internal static bool FreeClicks
    {
        get
        {
            bool on = CoopNet.Mode == CoopMode.Guest && CoopNet.Connected
                      && UnityEngine.Time.realtimeSinceStartup < _freeClicksUntil;
            if (!on && _freeClicksLogged)
            {
                _freeClicksLogged = false;
                Plugin.Logger.LogInfo("[STRAT] Guest free clicks lapsed -- viewing the host's screens again");
            }
            return on;
        }
    }

    internal static int FreeClicksLeft
        => FreeClicks ? (int)System.Math.Ceiling(_freeClicksUntil - UnityEngine.Time.realtimeSinceStartup) : 0;

    internal static void ToggleFreeClicks()
    {
        if (FreeClicks)
        {
            _freeClicksUntil = 0f;
            _ = FreeClicks;   // logs the end
            CoopNet.Status = "Viewing the host's screens again";
            return;
        }
        _freeClicksUntil = UnityEngine.Time.realtimeSinceStartup + FreeClickSeconds;
        _freeClicksLogged = true;
        Plugin.Logger.LogInfo($"[STRAT] Guest LET ME CLICK for {FreeClickSeconds:F0}s (view only -- campaign changes stay blocked)");
        CoopNet.Status = $"Clicks free for {FreeClickSeconds:F0}s -- look around; changes stay with the host";
    }

    // The TOPMOST mirrored screen, not the first one in the list. The market
    // and hiring are opened FROM the armory, so both sit in the open-screens
    // list at once -- scanning forwards returned whichever was registered
    // first (the armory), so the reported screen could flip between them
    // between polls and spray the guest with open/close pairs. Scanning
    // backwards yields the most recently opened screen, which is the one the
    // host is actually looking at.
    private static string CurrentMirroredOpen()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return null;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var s = open[i];
                if (s == null) continue;
                if (s.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>() != null) return "BlackMarketUIScreen";
                if (s.TryCast<Menace.UI.Strategy.HiringUIScreen>() != null) return "HiringUIScreen";
                if (s.TryCast<Menace.UI.Strategy.ShipUpgradesUIScreen>() != null) return "ShipUpgradesUIScreen";
                if (s.TryCast<Menace.UI.Strategy.ArmoryUIScreen>() != null) return "ArmoryUIScreen";
            }
        }
        catch { }
        return null;
    }

    internal static void DisableGuestTradeButton()
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return;
            for (int i = 0; i < open.Count; i++)
            {
                var bm = open[i]?.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>();
                if (bm != null) bm.m_TradeButton?.SetInteractable(false);
            }
        }
        catch { }
    }

    // Guest: called when a sync load has completed -- if the host still has a
    // mirrored screen open, bring the guest back into it.
    internal static void OnGuestSyncLoaded()
    {
        if (CoopNet.Mode == CoopMode.Guest && _hostOpenScreen != null)
            _reopenAt = UnityEngine.Time.realtimeSinceStartup + 1.5f;
    }

    private static string BuildHostCart()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open != null)
                for (int i = 0; i < open.Count; i++)
                {
                    var bm = open[i]?.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>();
                    if (bm == null) continue;
                    var sb = new System.Text.StringBuilder();
                    AppendNames(sb, bm.m_BuyItems);
                    sb.Append('|');
                    AppendNames(sb, bm.m_SellItems);
                    return sb.ToString();
                }
        }
        catch { }
        return "|";   // no market open / empty cart
    }

    private static void AppendNames(System.Text.StringBuilder sb, Il2CppSystem.Collections.Generic.List<BaseItem> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i]?.GetBaseItemTemplate()?.name;
            if (string.IsNullOrEmpty(n)) continue;
            if (sb.Length > 0 && sb[sb.Length - 1] != '|') sb.Append(',');
            sb.Append(n);
        }
    }

    // Guest: mirror the host's staged cart into our own screen's buy/sell
    // lists (using OUR item instances, matched by template) -- the game then
    // renders the pending trade natively.
    internal static void HandleCart(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || !Active() || m.Data == null) return;
        var strat = StrategyState.Get();
        if (strat == null) return;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return;
            for (int i = 0; i < open.Count; i++)
            {
                var bm = open[i]?.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>();
                if (bm == null) continue;
                int bar = m.Data.IndexOf('|');
                if (bar < 0) return;
                var used = new System.Collections.Generic.HashSet<IntPtr>();
                Applying = true;
                try
                {
                    FillCartList(bm.m_BuyItems, m.Data.Substring(0, bar), fromMarket: true, strat, used);
                    FillCartList(bm.m_SellItems, m.Data.Substring(bar + 1), fromMarket: false, strat, used);
                    bm.UpdateWindow();
                    bm.m_TradeButton?.SetInteractable(false);
                }
                finally { Applying = false; }
                return;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRAT] cart apply failed: {e.Message}"); }
    }

    private static void FillCartList(Il2CppSystem.Collections.Generic.List<BaseItem> target, string names,
        bool fromMarket, StrategyState strat, System.Collections.Generic.HashSet<IntPtr> used)
    {
        if (target == null) return;
        target.Clear();
        if (string.IsNullOrEmpty(names)) return;
        foreach (var name in names.Split(','))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var item = fromMarket ? FindMarketItemExcept(strat, name, used) : FindOwnedItemExcept(strat, name, used);
            if (item == null) continue;
            used.Add(item.Pointer);
            target.Add(item);
        }
    }

    private static BaseItem FindMarketItemExcept(StrategyState strat, string name, System.Collections.Generic.HashSet<IntPtr> used)
    {
        try
        {
            var market = strat.BlackMarket;
            if (market == null) return null;
            for (int pass = 0; pass < 2; pass++)
            {
                var list = new Il2CppSystem.Collections.Generic.List<BaseItem>();
                market.GetInstances(list, pass == 1);
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    if (it != null && !used.Contains(it.Pointer) && it.GetBaseItemTemplate()?.name == name) return it;
                }
            }
        }
        catch { }
        return null;
    }

    private static BaseItem FindOwnedItemExcept(StrategyState strat, string name, System.Collections.Generic.HashSet<IntPtr> used)
    {
        try
        {
            var raw = strat.OwnedItems?.GetRawInstances();
            if (raw != null)
                foreach (var kv in raw)
                    if (kv.Key != null && kv.Key.name == name && kv.Value != null)
                        for (int i = kv.Value.Count - 1; i >= 0; i--)
                        {
                            var it = kv.Value[i];
                            if (it != null && !used.Contains(it.Pointer)) return it;
                        }
        }
        catch { }
        return null;
    }

    // ---- host -> guest sends ---------------------------------------------

    internal static void SendVar(int var, int value, bool absolute)
        => CoopNet.Send(new CoopMsg { T = "svar", Prm = var, Seed = value, MIdx = absolute ? 1 : 0 });

    // Prm codes for "sitem": 0 owned-add, 1 owned-remove, 2 market-add, 3 market-remove
    internal static void SendItem(int op, string templateName)
    {
        if (string.IsNullOrEmpty(templateName)) return;
        CoopNet.Send(new CoopMsg { T = "sitem", Prm = op, SkillId = templateName });
    }

    // ---- O.C.I. (ship upgrade) live relay ---------------------------------
    // A ship upgrade is an ATOMIC transaction: TryEquipUpgrade takes the OCI
    // components paid as a parameter and applies the slot change. We relay the
    // transaction itself and SUPPRESS any currency relay generated inside it,
    // so the guest's own execution of the same call reproduces the identical
    // currency + slot result. That works whether the game deducts inside the
    // call or in the caller around it -- the suppression window is exactly the
    // call, so an outside deduction still relays normally as its own "svar".
    internal static bool InShipTransaction;

    private static readonly System.Collections.Generic.Dictionary<string, ShipUpgradeTemplate> _shipCache = new();
    private static IntPtr _shipCacheFor = IntPtr.Zero;

    internal static void SendShipUpgrade(string templateName, int slotIdx, int paidOci, bool equip)
    {
        if (string.IsNullOrEmpty(templateName)) return;
        CoopNet.Send(new CoopMsg { T = "sship", SkillId = templateName, Prm = slotIdx, Seed = paidOci, MIdx = equip ? 1 : 0 });
        Plugin.Logger.LogInfo($"[STRAT] Relaying O.C.I. {(equip ? "install" : "removal")}: {templateName} slot {slotIdx} ({paidOci} components)");
    }

    internal static void HandleShipUpgrade(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || !Active()) return;
        var ships = StrategyState.Get()?.ShipUpgrades;
        if (ships == null) return;
        var tmpl = FindShipUpgrade(ships, m.SkillId);
        if (tmpl == null)
        {
            Plugin.Logger.LogWarning($"[STRAT] O.C.I. upgrade '{m.SkillId}' not found -- next sync will heal it");
            return;
        }
        Applying = true;
        try
        {
            if (m.MIdx == 1) ships.TryEquipUpgrade(tmpl, m.Seed, m.Prm, false);
            else ships.TryUnequipUpgrade(tmpl);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRAT] O.C.I. apply failed: {e.Message}"); }
        finally { Applying = false; }
        RefreshShipScreen();
    }

    private static ShipUpgradeTemplate FindShipUpgrade(ShipUpgrades ships, string name)
    {
        if (ships == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            if (ships.Pointer != _shipCacheFor) { _shipCache.Clear(); _shipCacheFor = ships.Pointer; }
            if (_shipCache.Count == 0) BuildShipCache(ships);
        }
        catch { }
        return _shipCache.TryGetValue(name, out var t) ? t : null;
    }

    // Every upgrade reachable in the game: each slot template's basic upgrades
    // plus everything in their tech trees, keyed by asset name (identical on
    // both machines, like item templates).
    private static void BuildShipCache(ShipUpgrades ships)
    {
        try
        {
            var slots = ships.m_SlotTemplates;
            if (slots == null) return;
            for (int i = 0; i < slots.Length; i++)
            {
                var basics = slots[i]?.BasicUpgrades;
                if (basics == null) continue;
                for (int b = 0; b < basics.Length; b++)
                {
                    var basic = basics[b];
                    if (basic == null) continue;
                    _shipCache[basic.name] = basic;
                    var tree = ships.GetTechTreeUpgrades(basic);
                    if (tree == null) continue;
                    for (int u = 0; u < tree.Count; u++)
                        if (tree[u] != null) _shipCache[tree[u].name] = tree[u];
                }
            }
            Plugin.Logger.LogInfo($"[STRAT] O.C.I. template cache built ({_shipCache.Count} upgrades)");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRAT] O.C.I. cache build failed: {e.Message}"); }
    }

    // Redraw an open O.C.I. screen. HandleDialogClosed is the game's own
    // "an upgrade just changed" refresh path (it runs when the purchase dialog
    // closes), so it repaints the slots; the Update* calls cover the stat and
    // asset panels.
    internal static void RefreshShipScreen()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return;
            for (int i = 0; i < open.Count; i++)
            {
                var s = open[i]?.TryCast<Menace.UI.Strategy.ShipUpgradesUIScreen>();
                if (s == null) continue;
                try { s.HandleDialogClosed(); } catch { }
                try { s.UpdateShipStats(); } catch { }
                try { s.UpdateOffmapAbilitiesContainer(); } catch { }
            }
        }
        catch { }
    }

    // ---- guest-side application ------------------------------------------

    internal static void HandleVar(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || !Active()) return;
        var strat = StrategyState.Get();
        if (strat == null) return;
        Applying = true;
        try
        {
            var v = (StrategyVars)m.Prm;
            // invokeEvent: true so the game's own listeners (top bar, screens)
            // refresh; notifications: false to avoid double popups.
            if (m.MIdx == 1) strat.SetVar(v, m.Seed, false, true, false);
            else strat.ChangeVar(v, m.Seed, false, true, false);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRAT] var apply failed: {e.Message}"); }
        finally { Applying = false; }
        RefreshOpenScreens();
    }

    // A detached item kept briefly so a remove+add pair (a sell moving an item
    // from stash to market, or a buy the other way) can reuse the instance.
    private static BaseItem _detached;
    private static string _detachedName;

    internal static void HandleItem(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest || !Active()) return;
        var strat = StrategyState.Get();
        if (strat == null) return;
        Applying = true;
        try
        {
            switch (m.Prm)
            {
                case 0:   // owned-add (host bought / gained an item)
                {
                    var tmpl = FindTemplate(strat, m.SkillId);
                    if (tmpl != null) strat.OwnedItems.AddItem(tmpl, false, false);
                    else Plugin.Logger.LogWarning($"[STRAT] owned-add: template '{m.SkillId}' not found (next sync heals)");
                    break;
                }
                case 1:   // owned-remove (host sold / spent an item)
                {
                    var item = FindOwnedItem(strat, m.SkillId);
                    if (item != null) { Detach(item, m.SkillId); strat.OwnedItems.RemoveItem(item); }
                    else Plugin.Logger.LogWarning($"[STRAT] owned-remove: '{m.SkillId}' not found");
                    break;
                }
                case 2:   // market-add (host sold an item to the market)
                {
                    var item = _detachedName == m.SkillId ? _detached : null;
                    if (item == null) item = FindOwnedItem(strat, m.SkillId);
                    if (item != null) strat.BlackMarket.AddItem(item);
                    else Plugin.Logger.LogWarning($"[STRAT] market-add: no instance for '{m.SkillId}' (next sync heals)");
                    _detached = null; _detachedName = null;
                    break;
                }
                case 3:   // market-remove (host bought an item from the market)
                {
                    var item = FindMarketItem(strat, m.SkillId);
                    if (item != null) { Detach(item, m.SkillId); strat.BlackMarket.RemoveItem(item); }
                    else Plugin.Logger.LogWarning($"[STRAT] market-remove: '{m.SkillId}' not found");
                    break;
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRAT] item apply failed: {e.Message}"); }
        finally { Applying = false; }
        RefreshOpenScreens();
    }

    private static void Detach(BaseItem item, string name) { _detached = item; _detachedName = name; }

    internal static void HandleScreen(CoopMsg m, bool open)
    {
        if (CoopNet.Mode != CoopMode.Guest || !Active()) return;
        if (!IsMirroredScreen(m.SkillId)) return;
        // Track the host's screen state BEFORE any early-out -- the
        // reopen-after-sync path needs to know what the host has open even if
        // we can't act on it right now.
        if (open) _hostOpenScreen = m.SkillId;
        else if (_hostOpenScreen == m.SkillId) _hostOpenScreen = null;
        if (StrategyState.Get() == null || Menace.Tactical.TacticalManager.Get() != null) return;
        Applying = true;
        try
        {
            var ui = UIManager.Get();
            if (ui == null) return;
            if (open)
            {
                if (FindOpenMirrored(ui, m.SkillId) == null)   // dedupe (poll + events)
                {
                    ui.OpenScreen(m.SkillId);
                    Plugin.Logger.LogInfo($"[STRAT] Mirrored screen open: {m.SkillId}");
                }
                CoopNet.Status = $"Viewing the host's {FriendlyName(m.SkillId)} screen";
                ViewerLocked = true;      // lock immediately, don't wait for the next tick
                DisableGuestTradeButton();
            }
            else
            {
                var screen = FindOpenMirrored(ui, m.SkillId);
                if (screen != null) { screen.CloseScreen(); Plugin.Logger.LogInfo($"[STRAT] Mirrored screen close: {m.SkillId}"); }
                ViewerLocked = false;   // free the guest's input immediately
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[STRAT] screen mirror failed: {e.Message}"); }
        finally { Applying = false; }
    }

    private static UIScreen FindOpenMirrored(UIManager ui, string name)
    {
        try
        {
            var open = ui.m_OpenScreens;
            if (open == null) return null;
            for (int i = 0; i < open.Count; i++)
            {
                var s = open[i];
                if (s == null) continue;
                if (name == "BlackMarketUIScreen" && s.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>() != null) return s;
                if (name == "HiringUIScreen" && s.TryCast<Menace.UI.Strategy.HiringUIScreen>() != null) return s;
                if (name == "ArmoryUIScreen" && s.TryCast<Menace.UI.Strategy.ArmoryUIScreen>() != null) return s;
                if (name == "ShipUpgradesUIScreen" && s.TryCast<Menace.UI.Strategy.ShipUpgradesUIScreen>() != null) return s;
            }
        }
        catch { }
        return null;
    }

    // Refresh whichever mirrored screen the guest has open so relayed changes
    // are visible immediately.
    internal static void RefreshOpenScreens()
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        try
        {
            var ui = UIManager.Get();
            if (ui == null) return;
            var open = ui.m_OpenScreens;
            if (open == null) return;
            for (int i = 0; i < open.Count; i++)
            {
                var bm = open[i]?.TryCast<Menace.UI.Strategy.BlackMarketUIScreen>();
                if (bm != null) { bm.UpdateWindow(); bm.m_TradeButton?.SetInteractable(false); continue; }
                var hire = open[i]?.TryCast<Menace.UI.Strategy.HiringUIScreen>();
                if (hire != null) { hire.UpdateWindow(); continue; }
                var ship = open[i]?.TryCast<Menace.UI.Strategy.ShipUpgradesUIScreen>();
                if (ship != null) { try { ship.UpdateShipStats(); } catch { } }
            }
        }
        catch { }
    }

    // ---- lookups ----------------------------------------------------------

    internal static BaseItemTemplate FindTemplate(StrategyState strat, string name)
    {
        try
        {
            var raw = strat.OwnedItems?.GetRawInstances();
            if (raw != null)
                foreach (var kv in raw)
                    if (kv.Key != null && kv.Key.name == name) return kv.Key;
        }
        catch { }
        try
        {
            var stacks = strat.BlackMarket?.m_ItemStacks;
            if (stacks != null)
                for (int i = 0; i < stacks.Count; i++)
                {
                    var t = stacks[i]?.m_ItemTemplate;
                    if (t != null && t.name == name) return t;
                }
        }
        catch { }
        return null;
    }

    private static BaseItem FindOwnedItem(StrategyState strat, string name)
    {
        try
        {
            var raw = strat.OwnedItems?.GetRawInstances();
            if (raw != null)
                foreach (var kv in raw)
                    if (kv.Key != null && kv.Key.name == name && kv.Value != null && kv.Value.Count > 0)
                        return kv.Value[kv.Value.Count - 1];
        }
        catch { }
        return null;
    }

    private static BaseItem FindMarketItem(StrategyState strat, string name)
    {
        try
        {
            var market = strat.BlackMarket;
            if (market == null) return null;
            // Regular stock first, then special offers.
            for (int pass = 0; pass < 2; pass++)
            {
                var list = new Il2CppSystem.Collections.Generic.List<BaseItem>();
                market.GetInstances(list, pass == 1);
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    if (it != null && it.GetBaseItemTemplate()?.name == name) return it;
                }
            }
        }
        catch { }
        return null;
    }

    // The guest lock: guest-initiated strategy mutations are refused (the host
    // relays its own, which arrive under Applying). Loads pass -- state
    // reconstruction happens wholesale there.
    // TRANSACTION gates (ship upgrades, hiring, perks) must fire ONLY for a
    // genuine user click. The same calls are used by the engine to REBUILD
    // state -- deserialising a save re-hires the roster and re-installs ship
    // upgrades -- and blocking those leaves the guest with a half-built
    // campaign that never finishes coming in. Recent-input detection is the
    // mechanism already proven by the manual turn-end gate.
    // True while the guest replays a host UI step (ConversationSync): whatever the
    // engine does inside it counts as guest-initiated, so the transaction gates
    // refuse it -- the host's own transaction arrives through its relay.
    internal static bool GuestUiReplay;

    internal static bool BlockGuestTransaction(string what, string display)
    {
        if (!GuestUiReplay && UnityEngine.Time.realtimeSinceStartup - CoopRunner.LastUserInputAt > 0.6f)
            return false;   // engine-driven (load / init) -- must pass
        return BlockGuestMutation(what, display);
    }

    internal static bool BlockGuestMutation(string what, string display = null)
    {
        if (CoopNet.Mode != CoopMode.Guest || !Active()) return false;
        if (Applying || SessionSync.RemoteLoadInProgress) return false;
        // A player equipping its OWN squad may create the item copy the game
        // makes for that pick; the host makes the matching one (EquipmentSync).
        if (what == "item add" && EquipmentSync.InOwnLoadoutClick) return false;
        if (UnityEngine.Time.realtimeSinceStartup - _lastBlockLogAt > 3f)
        {
            _lastBlockLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo($"[STRAT] Blocked guest-local {what} (host drives the strategy layer)");
        }
        // Only TELL the player when they actually clicked something. The engine
        // makes these same calls while building screens (main menu -> planning,
        // planning -> deployment), and those blocks are routine -- raising a
        // banner for them just cries wolf during every transition.
        if (UnityEngine.Time.realtimeSinceStartup - CoopRunner.LastUserInputAt < 0.6f)
        {
            CoopRunner.ShowActionBlocked(display ?? "BLOCKED -- only the host can change the campaign");
            CoopNet.Status = display ?? "Only the host can change the campaign";
        }
        return true;
    }
    private static float _lastBlockLogAt;
}

// Separate patch class so a bind failure after a game update degrades only the
// live-mirror feature, never core replication.
[HarmonyPatch]
internal static class StrategyMirrorPatches
{
    private static bool HostSending()
        => CoopNet.Mode == CoopMode.Host && CoopNet.Connected && Plugin.LiveStrategy.Value
           && !StrategyMirror.Applying && !SessionSync.RemoteLoadInProgress;

    // Currency changes made INSIDE a ship transaction are not relayed on their
    // own -- the relayed transaction reproduces them on the guest.
    private static bool VarRelayAllowed() => HostSending() && !StrategyMirror.InShipTransaction;

    // ---- economy vars (money/intel/OCI components/...) --------------------

    [HarmonyPatch(typeof(StrategyState), nameof(StrategyState.ChangeVar))]
    [HarmonyPrefix]
    private static bool GateChangeVar() => !StrategyMirror.BlockGuestMutation("var change");

    [HarmonyPatch(typeof(StrategyState), nameof(StrategyState.ChangeVar))]
    [HarmonyPostfix]
    private static void MirrorChangeVar(StrategyVars _var, int _change)
    {
        if (!VarRelayAllowed()) return;
        StrategyMirror.SendVar((int)_var, _change, absolute: false);
    }

    [HarmonyPatch(typeof(StrategyState), nameof(StrategyState.SetVar))]
    [HarmonyPrefix]
    private static bool GateSetVar() => !StrategyMirror.BlockGuestMutation("var set");

    [HarmonyPatch(typeof(StrategyState), nameof(StrategyState.SetVar))]
    [HarmonyPostfix]
    private static void MirrorSetVar(StrategyVars _var, int _rawValue)
    {
        if (!VarRelayAllowed()) return;
        StrategyMirror.SendVar((int)_var, _rawValue, absolute: true);
    }

    // ---- owned items ------------------------------------------------------
    // AddItem has two overloads and one may call the other -- the outer-call
    // flag prevents a double relay.
    private static bool _inOuterAdd;

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.AddItem),
        typeof(AddItemType), typeof(BaseItemTemplate), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BaseItemTemplate>), typeof(RewardTableTemplate), typeof(float), typeof(bool), typeof(bool))]
    [HarmonyPrefix]
    private static bool GateAddItemBig() { if (StrategyMirror.BlockGuestMutation("item add")) return false; _inOuterAdd = true; return true; }

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.AddItem),
        typeof(AddItemType), typeof(BaseItemTemplate), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BaseItemTemplate>), typeof(RewardTableTemplate), typeof(float), typeof(bool), typeof(bool))]
    [HarmonyPostfix]
    private static void MirrorAddItemBig(BaseItem __result)
    {
        _inOuterAdd = false;
        if (!HostSending() || __result == null) return;
        StrategyMirror.SendItem(0, TmplName(__result));
    }

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.AddItem), typeof(BaseItemTemplate), typeof(bool), typeof(bool))]
    [HarmonyPrefix]
    private static bool GateAddItem() => !StrategyMirror.BlockGuestMutation("item add");

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.AddItem), typeof(BaseItemTemplate), typeof(bool), typeof(bool))]
    [HarmonyPostfix]
    private static void MirrorAddItem(BaseItemTemplate _itemTemplate)
    {
        if (_inOuterAdd || !HostSending() || _itemTemplate == null) return;
        StrategyMirror.SendItem(0, _itemTemplate.name);
    }

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.RemoveItem), typeof(BaseItem))]
    [HarmonyPrefix]
    private static bool GateRemoveItem() => !StrategyMirror.BlockGuestMutation("item remove");

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.RemoveItem), typeof(BaseItem))]
    [HarmonyPostfix]
    private static void MirrorRemoveItem(BaseItem _item, bool __result)
    {
        if (!HostSending() || !__result || _item == null) return;
        StrategyMirror.SendItem(1, TmplName(_item));
    }

    // ---- black market stock ----------------------------------------------

    [HarmonyPatch(typeof(BlackMarket), nameof(BlackMarket.AddItem))]
    [HarmonyPrefix]
    private static bool GateMarketAdd() => !StrategyMirror.BlockGuestMutation("market add");

    [HarmonyPatch(typeof(BlackMarket), nameof(BlackMarket.AddItem))]
    [HarmonyPostfix]
    private static void MirrorMarketAdd(BaseItem _item)
    {
        if (!HostSending() || _item == null) return;
        StrategyMirror.SendItem(2, TmplName(_item));
    }

    [HarmonyPatch(typeof(BlackMarket), nameof(BlackMarket.RemoveItem))]
    [HarmonyPrefix]
    private static bool GateMarketRemove() => !StrategyMirror.BlockGuestMutation("market remove");

    [HarmonyPatch(typeof(BlackMarket), nameof(BlackMarket.RemoveItem))]
    [HarmonyPostfix]
    private static void MirrorMarketRemove(BaseItem _item, bool __result)
    {
        if (!HostSending() || !__result || _item == null) return;
        StrategyMirror.SendItem(3, TmplName(_item));
    }

    private static string TmplName(BaseItem item)
    {
        try { return item.GetBaseItemTemplate()?.name; } catch { return null; }
    }

    // ---- screen lockstep --------------------------------------------------

    [HarmonyPatch(typeof(UIManager), nameof(UIManager.OpenScreen))]
    [HarmonyPrefix]
    private static bool GateOpenScreen(string _screenPrefabName)
    {
        if (!StrategyMirror.IsMirroredScreen(_screenPrefabName)) return true;
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected || !Plugin.LiveStrategy.Value) return true;
        if (StrategyMirror.Applying) return true;   // this open IS the mirror
        // Squads / O.C.I. are inspection screens: the guest may open them for
        // itself whenever the host isn't driving a mirrored screen. The market
        // and hiring stay host-only (they exist to spend money).
        if (StrategyMirror.IsGuestBrowsable(_screenPrefabName)
            && (StrategyMirror.HostOpenScreen == null || StrategyMirror.FreeClicks)) return true;
        string msg = StrategyMirror.HostOpenScreen != null
            ? $"BLOCKED -- the host is in the {StrategyMirror.FriendlyName(StrategyMirror.HostOpenScreen)} screen; you'll see it together"
            : $"BLOCKED -- the {StrategyMirror.FriendlyName(_screenPrefabName)} screen is opened by the host";
        CoopNet.Status = msg;
        CoopRunner.ShowActionBlocked(msg);
        return false;
    }

    // (Event-based open/close relays removed in 2.1.2 -- the armory's tab
    // switches never fire CloseScreen, which trapped the guest inside a dead
    // mirror. StrategyMirror.Tick POLLS the host's open-screen state instead.)

    // Extra trade-path gates: the market trade moves items at the STACK level
    // too, which bypassed the higher-level gates and let the guest buy.
    [HarmonyPatch(typeof(BlackMarket.BlackMarketItemStack), nameof(BlackMarket.BlackMarketItemStack.AddInstance))]
    [HarmonyPrefix]
    private static bool GateStackAdd() => !StrategyMirror.BlockGuestMutation("market stack add");

    [HarmonyPatch(typeof(BlackMarket.BlackMarketItemStack), nameof(BlackMarket.BlackMarketItemStack.TryRemoveInstance))]
    [HarmonyPrefix]
    private static bool GateStackRemove() => !StrategyMirror.BlockGuestMutation("market stack remove");

    [HarmonyPatch(typeof(OwnedItems), nameof(OwnedItems.RemoveItem), typeof(BaseItemTemplate))]
    [HarmonyPrefix]
    private static bool GateRemoveItemByTemplate() => !StrategyMirror.BlockGuestMutation("item remove");

    // ---- ATOMIC transaction gates -----------------------------------------
    // These are the user-initiated purchases reachable from Squads / O.C.I.
    // Gating them HERE rather than at their sub-steps is what makes guest
    // browsing safe: cost and effect both live inside these single calls
    // (TryEquipUpgrade even takes the OCI components paid as a parameter), so a
    // refusal can never leave half a transaction applied — which is precisely
    // how a blocked currency change with an un-blocked effect would desync.
    [HarmonyPatch(typeof(ShipUpgrades), nameof(ShipUpgrades.TryEquipUpgrade))]
    [HarmonyPrefix]
    private static bool GateEquipUpgrade()
    {
        if (StrategyMirror.BlockGuestTransaction("ship upgrade equip",
                "BLOCKED -- only the host can install O.C.I. upgrades")) return false;
        StrategyMirror.InShipTransaction = true;   // suppress currency relays inside
        return true;
    }

    [HarmonyPatch(typeof(ShipUpgrades), nameof(ShipUpgrades.TryEquipUpgrade))]
    [HarmonyPostfix]
    private static void MirrorEquipUpgrade(ShipUpgradeTemplate _shipUpgradeTemplate, int _paidOciComponents, int _slotIdx, bool __result)
    {
        StrategyMirror.InShipTransaction = false;
        if (!HostSending() || !__result || _shipUpgradeTemplate == null) return;
        StrategyMirror.SendShipUpgrade(_shipUpgradeTemplate.name, _slotIdx, _paidOciComponents, equip: true);
    }

    [HarmonyPatch(typeof(ShipUpgrades), nameof(ShipUpgrades.TryUnequipUpgrade))]
    [HarmonyPrefix]
    private static bool GateUnequipUpgrade()
    {
        if (StrategyMirror.BlockGuestTransaction("ship upgrade unequip",
                "BLOCKED -- only the host can remove O.C.I. upgrades")) return false;
        StrategyMirror.InShipTransaction = true;
        return true;
    }

    [HarmonyPatch(typeof(ShipUpgrades), nameof(ShipUpgrades.TryUnequipUpgrade))]
    [HarmonyPostfix]
    private static void MirrorUnequipUpgrade(ShipUpgradeTemplate _shipUpgrade, bool __result)
    {
        StrategyMirror.InShipTransaction = false;
        if (!HostSending() || !__result || _shipUpgrade == null) return;
        StrategyMirror.SendShipUpgrade(_shipUpgrade.name, 0, 0, equip: false);
    }

    [HarmonyPatch(typeof(Roster), nameof(Roster.HireLeader))]
    [HarmonyPrefix]
    private static bool GateHireLeader()
        => !StrategyMirror.BlockGuestTransaction("hire leader",
            "BLOCKED -- only the host can hire");

    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.TryRemoveLastPerk))]
    [HarmonyPrefix]
    private static bool GateRemovePerk()
        => !StrategyMirror.BlockGuestTransaction("perk removal",
            "BLOCKED -- only the host can change perks");

    // Picking a perk and dismissing a leader were never gated: the viewer
    // shield stopped the clicks. FREE CLICKS lifts that shield, so gate them here.
    [HarmonyPatch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.AddPerk))]
    [HarmonyPrefix]
    private static bool GateAddPerk()
        => PerkSync.Applying || !StrategyMirror.BlockGuestTransaction("perk pick",
            "BLOCKED -- only the host can pick perks");

    [HarmonyPatch(typeof(Roster), nameof(Roster.TryDismissLeader))]
    [HarmonyPrefix]
    private static bool GateDismissLeader()
        => !StrategyMirror.BlockGuestTransaction("leader dismissal",
            "BLOCKED -- only the host can dismiss leaders");

    // THE INPUT SHIELD: while the guest is inside a mirrored screen, every
    // game-UI click is swallowed at the dispatch level. The guest is a pure
    // viewer there -- the host drives; the screen opens, changes, and closes by
    // relay. (The co-op panel is a separate UI system and stays clickable.)
    private static float _lastShieldLogAt;

    // THE GUEST DOES NOT CLICK IN THE STRATEGY LAYER (user, 2026-10-01 playtest:
    // "guest shouldn't be able to click really at all inside the sync screen").
    // Gating button by button kept missing one -- event messages, start
    // operation, then ABORT OPERATION got through by a path no data gate saw --
    // so it is now the other way round: every game-UI click by a connected guest
    // is refused in the strategy layer, except
    //   - dialogs (the game menu, reward pop-ups the engine raises on its own),
    //   - the title / result screens the guest has to get through by itself,
    //   - "Let me click" (look-only), and even then never the ACTION buttons:
    //     abort / plan mission / start operation / event messages.
    // The co-op panel is a separate UI and is unaffected. Replays (Applying)
    // always pass.
    private static readonly string[] GuestClickableScreens =
        { "TitleUIScreen", "SplashUIScreen", "EmptyUIScreen", "MissionResultUIScreen", "OperationResultUIScreen" };
    private static float _lastGuestClickLogAt;

    [HarmonyPatch(typeof(Menace.UI.InteractiveElement), nameof(Menace.UI.InteractiveElement.OnClicked))]
    [HarmonyPrefix]
    private static bool GateGuestClicks(Menace.UI.InteractiveElement __instance)
    {
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected) return true;
        if (ConversationSync.Applying || StrategyMirror.Applying || SessionSync.ModLaunching) return true;
        if (Menace.Tactical.TacticalManager.Get() != null) return true;   // battle: own squads
        if (EquipmentSync.OwnLoadoutClick(__instance)) return true;     // own squad's loadout in prep
        try
        {
            var ui = UIManager.Get();
            if (ui != null && ui.IsDialogOpen()) return true;
            string active = ui?.GetActiveScreen()?.GetType().Name;
            if (active != null && System.Array.IndexOf(GuestClickableScreens, active) >= 0) return true;
        }
        catch { }

        bool action = IsGuestActionButton(__instance);
        if (StrategyMirror.FreeClicks && !action) return true;

        string msg = action
            ? "BLOCKED -- the host decides this; you'll follow along"
            : "BLOCKED -- the host drives the campaign (\"Let me click\" in the co-op panel to look around)";
        CoopNet.Status = "The host drives the campaign screens";
        CoopRunner.ShowActionBlocked(msg);
        if (UnityEngine.Time.realtimeSinceStartup - _lastGuestClickLogAt > 2f)
        {
            _lastGuestClickLogAt = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo($"[STRAT] Blocked guest click on '{__instance?.name}'{(action ? " (host-only action)" : "")}");
        }
        return false;
    }

    // Abort / plan mission / start operation buttons, and event messages.
    private static bool IsGuestActionButton(Menace.UI.InteractiveElement el)
    {
        if (el == null) return false;
        if (ConversationSync.IsEventMessage(el)) return true;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count; i++)
            {
                var ms = open[i]?.TryCast<Menace.UI.Strategy.MissionSelectUIScreen>();
                if (ms != null && (Inside(el, ms.m_AbortOperationButton) || Inside(el, ms.m_PlanMissionButton))) return true;
                var os = open[i]?.TryCast<Menace.UI.Strategy.OperationSelectUIScreen>();
                if (os != null && Inside(el, os.m_StartOperationButton)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool Inside(UnityEngine.UIElements.VisualElement el, UnityEngine.UIElements.VisualElement target)
    {
        if (target == null) return false;
        for (var e = el; e != null; e = e.parent)
            if (e.Pointer == target.Pointer) return true;
        return false;
    }

    private static bool BlockGuestOperationStart(string where)
    {
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected || StrategyMirror.Applying
            || SessionSync.RemoteLoadInProgress) return false;
        CoopNet.Status = "Only the host starts operations";
        CoopRunner.ShowActionBlocked("BLOCKED -- only the host starts operations");
        Plugin.Logger.LogInfo($"[STRAT] Blocked guest operation start ({where})");
        return true;
    }

    [HarmonyPatch(typeof(Menace.UI.Strategy.OperationSelectUIScreen), nameof(Menace.UI.Strategy.OperationSelectUIScreen.ShowStartOperationDialog))]
    [HarmonyPrefix]
    private static bool GateStartOperationDialog() => !BlockGuestOperationStart("start dialog");

    [HarmonyPatch(typeof(Menace.UI.Strategy.OperationSelectUIScreen), nameof(Menace.UI.Strategy.OperationSelectUIScreen.StartOperation))]
    [HarmonyPrefix]
    private static bool GateStartOperationScreen() => !BlockGuestOperationStart("operation screen");

    [HarmonyPatch(typeof(Menace.Strategy.SystemMapOperationHUD), nameof(Menace.Strategy.SystemMapOperationHUD.StartOperation))]
    [HarmonyPrefix]
    private static bool GateStartOperationHud() => !BlockGuestOperationStart("system map");

    // ABORT OPERATION is EndOperation(cancel: true). Only the abort is gated: the
    // normal end (victory points reached) runs from the guest's own result flow.
    [HarmonyPatch(typeof(Menace.States.StrategyState), nameof(Menace.States.StrategyState.EndOperation))]
    [HarmonyPrefix]
    private static bool GateAbortOperation(bool _cancel)
        => !_cancel || !StrategyMirror.BlockGuestTransaction("operation abort", "BLOCKED -- only the host can abort the operation");

    [HarmonyPatch(typeof(Menace.Strategy.Operation), nameof(Menace.Strategy.Operation.EndOperation))]
    [HarmonyPrefix]
    private static bool GateAbortOperationInner(bool _cancel)
        => !_cancel || !StrategyMirror.BlockGuestTransaction("operation abort", "BLOCKED -- only the host can abort the operation");

    // PLAN MISSION: the guest's prep opens by following the host (SessionSync),
    // never from its own click.
    [HarmonyPatch(typeof(Menace.UI.Strategy.MissionPrepUIScreen), nameof(Menace.UI.Strategy.MissionPrepUIScreen.TryOpen))]
    [HarmonyPrefix]
    private static bool GatePlanMission(ref bool __result)
    {
        if (!StrategyMirror.BlockGuestTransaction("mission planning",
                "BLOCKED -- the host plans the mission; you'll follow into planning")) return true;
        __result = false;
        return false;
    }

    // The engine call underneath: guest-initiated only (a save load must pass).
    [HarmonyPatch(typeof(Menace.Strategy.Operation), nameof(Menace.Strategy.Operation.StartOperation))]
    [HarmonyPrefix]
    private static bool GateStartOperationEngine()
        => !StrategyMirror.BlockGuestTransaction("operation start", "BLOCKED -- only the host starts operations");

    [HarmonyPatch(typeof(Menace.UI.InteractiveElement), nameof(Menace.UI.InteractiveElement.OnClicked))]
    [HarmonyPrefix]
    private static bool GateViewerClicks(Menace.UI.InteractiveElement __instance)
    {
        if (!StrategyMirror.ViewerLocked || StrategyMirror.FreeClicks) return true;
        if (EquipmentSync.OwnLoadoutClick(__instance)) return true;     // own squad's loadout in prep
        if (CoopNet.Mode != CoopMode.Guest || !CoopNet.Connected || !Plugin.LiveStrategy.Value) return true;
        if (UnityEngine.Time.realtimeSinceStartup - _lastShieldLogAt > 2f)
        {
            _lastShieldLogAt = UnityEngine.Time.realtimeSinceStartup;
            CoopNet.Status = "Viewing the host's screen -- the host drives it";
        }
        CoopRunner.ShowActionBlocked(
            $"CLICKING DISABLED -- you are viewing the host's {StrategyMirror.FriendlyName(StrategyMirror.HostOpenScreen)} screen");
        return false;
    }
}
