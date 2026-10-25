using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppMenace.Items;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Items;
using Menace.States;
using Menace.Strategy;
using Menace.Tactical;
using Menace.UI;
using Menace.UI.Strategy;
#endif

namespace MenaceCoop;

// EXPERIMENTAL best-effort equipment mirroring for the planning phase. This is
// DISPLAY-ONLY and cannot affect battle correctness: the battle always loads
// from the host's autosave, which carries the authoritative loadouts. So even
// a partial/imperfect mirror here is safe -- worst case the guest's planning
// view is slightly off, exactly the pre-v1.1 behaviour.
//
// Approach: replicate ItemContainer.Place/Remove on UNIT containers. Units are
// identified by hired-roster index (stable across machines from the same save)
// and items by GUID (Item.GetID / OwnedItems.GetItemByGuid).
internal static class EquipmentSync
{
    internal static bool ApplyingRemote;

    // Mission prep OR the strategy Squads tab (ArmoryUIScreen). Prep-only missed
    // every loadout change made from Squads (user 2026-10-02: host's CMG50 never
    // reached the guest). Not during a save load: the load's own Equip calls are
    // not edits, and the guest gets the same save anyway.
    private static bool ActiveHost =>
        CoopNet.Mode == CoopMode.Host && CoopNet.Connected && !ApplyingRemote
        && !SessionSync.RemoteLoadInProgress
        && (SessionSync.HostPrepActive || StrategyMirror.HostInArmory());

    private static Il2CppSystem.Collections.Generic.List<BaseUnitLeader> Hired()
        => StrategyState.Get()?.Roster?.GetHiredLeaders()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();

    // Match a container to the hired-roster index of the unit that owns it.
    private static int ContainerUnitIndex(ItemContainer container)
    {
        var hired = Hired();
        if (hired == null || container == null) return -1;
        for (int i = 0; i < hired.Count; i++)
        {
            var c = hired[i]?.GetItems();
            if (c != null && c.Pointer == container.Pointer) return i;
        }
        return -1;
    }

    internal static void OnHostPlace(ItemContainer container, Item item, int index)
    {
        if (!ActiveHost || item == null) return;
        int idx = ContainerUnitIndex(container);
        if (idx < 0) return; // non-unit (stash) container: handled implicitly on apply
        try
        {
            CoopNet.Send(new CoopMsg { T = "iplace", MIdx = idx, Prm = index, SkillId = item.GetID(), Name = _applyingFor > 0 ? _applyingFor.ToString() : null });
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] place broadcast failed: {e.Message}"); }
    }

    internal static void OnHostRemove(ItemContainer container, Item item)
    {
        if (!ActiveHost || item == null) return;
        int idx = ContainerUnitIndex(container);
        if (idx < 0) return;
        try
        {
            CoopNet.Send(new CoopMsg { T = "irem", MIdx = idx, SkillId = item.GetID(), Name = _applyingFor > 0 ? _applyingFor.ToString() : null });
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] remove broadcast failed: {e.Message}"); }
    }

    // Find an item by GUID anywhere the guest can reach it: unit containers
    // first, then the shared owned-items pool.
    // Find an item to equip on the guest, matching the host's item by its
    // (template) ID. Items are fungible by template, so we prefer an UNUSED
    // instance from the owned-items pool (stash) rather than stealing one that
    // is already equipped on another unit. Item.GetID() is a template id, not a
    // per-instance GUID, so GetItemByGuid does NOT work -- enumerate the pool.
    private static bool IsInAnyUnit(Item item, Il2CppSystem.Collections.Generic.List<BaseUnitLeader> hired)
    {
        if (hired == null) return false;
        for (int i = 0; i < hired.Count; i++)
            if (hired[i]?.GetItems()?.HasItem(item) == true) return true;
        return false;
    }

    private static Item FindItem(string id)
    {
        var hired = Hired();
        try
        {
            var owned = StrategyState.Get()?.OwnedItems;
            if (owned != null)
            {
                var list = new Il2CppSystem.Collections.Generic.List<BaseItem>();
                owned.GetInstances(list);
                Item anyMatch = null;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i]?.TryCast<Item>();
                    if (item == null || item.GetID() != id) continue;
                    if (anyMatch == null) anyMatch = item;
                    if (!IsInAnyUnit(item, hired)) return item; // prefer unused
                }
                if (anyMatch != null) return anyMatch;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] pool search failed: {e.Message}"); }

        // fallback: item already in a unit container (matches by id)
        if (hired != null)
            for (int i = 0; i < hired.Count; i++)
            {
                var it = hired[i]?.GetItems()?.GetItemByID(id);
                if (it != null) return it;
            }
        return null;
    }

    internal static void HandlePlace(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (m.Name != null && m.Name == Peers.LocalSeat.ToString()) return;   // our own change, already applied here
        try
        {
            var hired = Hired();
            if (hired == null || m.MIdx < 0 || m.MIdx >= hired.Count) return;
            var container = hired[m.MIdx]?.GetItems();
            var item = FindItem(m.SkillId);
            if (container == null || item == null) return;
            if (container.HasItem(item)) return; // already there

            ApplyingRemote = true;
            try
            {
                // detach from its current container so the visual is consistent
                for (int i = 0; i < hired.Count; i++)
                {
                    var c = hired[i]?.GetItems();
                    if (c != null && c.HasItem(item)) { c.Remove(item, ItemEquipFlags.None); break; }
                }
                container.Equip(item, m.Prm, ItemEquipFlags.None);
            }
            finally { ApplyingRemote = false; }
            RefreshPrepUnitWindow();
            Plugin.Logger.LogInfo($"[EQUIP] Mirrored place: item {m.SkillId} -> unit {m.MIdx} slot {m.Prm}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] place apply failed: {e.Message}"); }
    }

    internal static void HandleRemove(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (m.Name != null && m.Name == Peers.LocalSeat.ToString()) return;   // our own change, already applied here
        try
        {
            var hired = Hired();
            if (hired == null || m.MIdx < 0 || m.MIdx >= hired.Count) return;
            var container = hired[m.MIdx]?.GetItems();
            var item = container?.GetItemByID(m.SkillId);
            if (container == null || item == null) return;
            ApplyingRemote = true;
            try { container.Remove(item, ItemEquipFlags.None); }
            finally { ApplyingRemote = false; }
            RefreshPrepUnitWindow();
            Plugin.Logger.LogInfo($"[EQUIP] Mirrored remove: item {m.SkillId} from unit {m.MIdx}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] remove apply failed: {e.Message}"); }
    }

    // ---- PLAYERS EDIT THEIR OWN SQUADS' LOADOUTS (user 2026-10-02) ----------
    // A squad assigned to you in the prep panel's picker is yours to equip.
    // The client never changes its own copy: the edit goes to the host as a
    // REQUEST ("ireq"), the host checks the seat still owns that unit and the
    // item is free, applies it with the normal container call, and the
    // existing iplace/irem mirror shows the result to everyone -- the asker
    // included. Reassign the squad and the old owner's requests are refused.

    [ThreadStatic] internal static int InClick;
    internal static bool InOwnLoadoutClick => InClick > 0 && ClientInPrep;

    private static bool ClientInPrep =>
        CoopNet.Mode == CoopMode.Guest && CoopNet.Connected && !ApplyingRemote
        && SessionSync.ViewerPrepActive && !SessionSync.RemoteLoadInProgress
        && Menace.Tactical.TacticalManager.Get() == null;

    internal static bool LocalOwns(BaseUnitLeader leader)
        => leader != null && SessionSync.ViewerPrepActive
           && CoopRunner.PrepSeatOfLeader(leader.Pointer) == Peers.LocalSeat;

    // THE CLICK SHIELD'S ONE EXCEPTION (user hand test 2026-10-02: "user can't
    // click on the characters to be able to change their equipment"). Every
    // client click in the strategy layer is refused (StrategyMirror); during
    // prep these pass, for the player's OWN units only: selecting the unit's
    // portrait, and the equipment slots / alternatives list / unequip buttons of
    // the unit window. Deploy toggle, squaddie +/- and perks stay the host's.
    internal static bool OwnLoadoutClick(Menace.UI.InteractiveElement el)
    {
        if (el == null || CoopNet.Mode != CoopMode.Guest || !SessionSync.ViewerPrepActive) return false;
        try
        {
            for (UnityEngine.UIElements.VisualElement e = el; e != null; e = e.parent)
            {
                var marker = e.TryCast<MissionPrepDeployedEntity>();   // own unit on the prep map (DeploySync)
                if (marker != null) return DeploySync.LocalOwns(marker.GetEntity());
                var slot = e.TryCast<MissionPrepUnitSelectSlot>();
                if (slot != null) return LocalOwns(slot.TryCast<BaseUnitSelectSlot>()?.GetLeader());
            }
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count; i++)
            {
                var ps = open[i]?.TryCast<MissionPrepUIScreen>();
                if (ps == null) continue;
                var eq = ps.m_UnitSelector?.GetUnitWindow()?.m_Equipment;
                if (eq == null || !LocalOwns(eq.m_Leader)) return false;
                return Inside(el, eq.m_EquipmentSlotsContainer?.TryCast<UnityEngine.UIElements.VisualElement>())
                    || Inside(el, eq.m_EquipmentAlternativesContainer)
                    || Inside(el, eq.m_UnequipButton?.TryCast<UnityEngine.UIElements.VisualElement>())
                    || Inside(el, eq.m_UnequipAllButton?.TryCast<UnityEngine.UIElements.VisualElement>());
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

    // Client: a container write the UI attempted. Always applied HERE (false):
    // the game's equipment UI changes the container as part of one click -- a
    // left-click on a slot takes the item off and puts it back to open the
    // alternatives list (hand test 2026-10-02: turning each step into a host
    // round trip redrew the window and closed that list at once). So the writes
    // of a click on our own unit are recorded, and when the click ends only the
    // NET change goes to the host (EndClick).
    private static readonly System.Collections.Generic.List<(int idx, string id, int index, bool place)> _clickOps = new();

    internal static void RecordClientWrite(ItemContainer container, Item item, int index, bool place)
    {
        if (InClick <= 0 || !ClientInPrep || item == null) return;
        int idx = ContainerUnitIndex(container);
        var hired = Hired();
        var leader = idx >= 0 && hired != null ? hired[idx] : null;
        if (leader != null && LocalOwns(leader)) _clickOps.Add((idx, item.GetID(), index, place));
    }

    // The outermost equipment click finished: send its net effect.
    internal static void EndClick()
    {
        if (InClick > 0) InClick--;
        if (InClick > 0 || _clickOps.Count == 0) return;
        var ops = new System.Collections.Generic.List<(int idx, string id, int index, bool place)>(_clickOps);
        _clickOps.Clear();
        // A take-off and put-back of the same item on the same unit cancels out.
        for (int i = 0; i < ops.Count; i++)
            for (int j = i + 1; j < ops.Count; j++)
                if (ops[i].idx == ops[j].idx && ops[i].id == ops[j].id && ops[i].place != ops[j].place)
                { ops.RemoveAt(j); ops.RemoveAt(i); i = -1; break; }
        foreach (var o in ops)
        {
            CoopNet.Send(new CoopMsg { T = "ireq", MIdx = o.idx, Prm = o.index, SkillId = o.id, Data = o.place ? "place" : "remove" });
            Plugin.Logger.LogInfo($"[EQUIP] Changed {o.id} ({(o.place ? "equip" : "remove")}) on our unit {o.idx}; sent to the host");
        }
    }

    // Host: whose request is being applied right now (its mirror is not echoed back).
    private static int _applyingFor = -1;

    // Host: a player changed one of their units.
    internal static void HandleRequest(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Host || m.From <= 0) return;
        string why = null;
        _applyingFor = m.From;
        try
        {
            var hired = Hired();
            var leader = hired != null && m.MIdx >= 0 && m.MIdx < hired.Count ? hired[m.MIdx] : null;
            var container = leader?.GetItems();
            if (container == null) why = "that unit is not in the roster";
            else if (!SessionSync.HostPrepActive) why = "loadouts can only be changed during mission planning";
            else if (CoopRunner.PrepSeatOfLeader(leader.Pointer) != m.From) why = "that squad is not assigned to you";
            else if (m.Data == "place")
            {
                var item = FreeItem(m.SkillId, hired) ?? CreateFor(m.SkillId, m.From, hired);
                if (item == null) why = "none of that item is free -- all are equipped";
                else if (!container.Equip(item, m.Prm, ItemEquipFlags.None)) why = "the game refused that equipment there";
            }
            else
            {
                var item = container.GetItemByID(m.SkillId);
                if (item == null) why = "that item is no longer on the unit";
                else if (!container.Remove(item, ItemEquipFlags.None)) why = "the game refused to remove it";
            }
        }
        catch (Exception e) { why = "error: " + e.Message; }
        finally { _applyingFor = -1; }
        if (why == null)
        {
            Plugin.Logger.LogInfo($"[EQUIP] {Peers.NameOf(m.From)} {(m.Data == "place" ? "equipped" : "removed")} {m.SkillId} on unit {m.MIdx}");
            RefreshPrepUnitWindow();
        }
        else
        {
            Plugin.Logger.LogInfo($"[EQUIP] Refused {Peers.NameOf(m.From)}'s change ({m.Data} {m.SkillId} on unit {m.MIdx}): {why}");
            CoopNet.SendTo(new CoopMsg { T = "irej", Data = why, MIdx = m.MIdx, Prm = m.Prm, SkillId = m.SkillId, Name = m.Data }, m.From);
        }
    }

    // Client: the host refused a change we already made here -- undo it.
    internal static void HandleRefusal(CoopMsg m)
    {
        CoopNet.Status = "Loadout change refused: " + (m.Data ?? "");
        Plugin.Logger.LogInfo($"[EQUIP] Host refused our change ({m.Name} {m.SkillId} on unit {m.MIdx}): {m.Data} -- undoing it");
        try
        {
            var hired = Hired();
            var container = hired != null && m.MIdx >= 0 && m.MIdx < hired.Count ? hired[m.MIdx]?.GetItems() : null;
            if (container != null)
            {
                ApplyingRemote = true;
                try
                {
                    if (m.Name == "place") { var it = container.GetItemByID(m.SkillId); if (it != null) container.Remove(it, ItemEquipFlags.None); }
                    else { var it = FindItem(m.SkillId); if (it != null) container.Equip(it, m.Prm, ItemEquipFlags.None); }
                }
                finally { ApplyingRemote = false; }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] undo failed: {e.Message}"); }
        RefreshPrepUnitWindow();
    }

    // The player's pick made a NEW copy on its machine (the game creates one for
    // a supply-cost item -- hand test 2026-10-02: blocked, then Equip threw).
    // Make the host's copy without the usual relay, and tell every OTHER client
    // to make theirs; the requester already has one.
    private static Item CreateFor(string id, int requester, Il2CppSystem.Collections.Generic.List<BaseUnitLeader> hired)
    {
        try
        {
            var strat = StrategyState.Get();
            var tmpl = strat == null ? null : StrategyMirror.FindTemplate(strat, id);
            if (tmpl == null) return null;
            StrategyMirror.Applying = true;
            try { strat.OwnedItems.AddItem(tmpl, false, false); }
            finally { StrategyMirror.Applying = false; }
            foreach (int seat in Peers.SessionSeats())
                if (seat != Peers.HostSeat && seat != requester)
                    CoopNet.SendTo(new CoopMsg { T = "sitem", Prm = 0, SkillId = id }, seat);
            Plugin.Logger.LogInfo($"[EQUIP] Created a copy of {id} for {Peers.NameOf(requester)}'s pick");
            return FreeItem(id, hired);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] could not create {id}: {e.Message}"); return null; }
    }

    // An UNUSED instance only: never take an item off another unit (it may be
    // someone else's squad).
    private static Item FreeItem(string id, Il2CppSystem.Collections.Generic.List<BaseUnitLeader> hired)
    {
        var owned = StrategyState.Get()?.OwnedItems;
        if (owned == null) return null;
        var list = new Il2CppSystem.Collections.Generic.List<BaseItem>();
        owned.GetInstances(list);
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i]?.TryCast<Item>();
            if (item != null && item.GetID() == id && !IsInAnyUnit(item, hired)) return item;
        }
        return null;
    }

    private static void RefreshPrepUnitWindow()
    {
        // best-effort: nudge the prep screen to re-read unit data
        try { SessionSync.RefreshViewerUnits(); }
        catch { }
        // ...and the prep screen's open unit window, on every machine (hand test
        // 2026-10-02: a player's swap reached the host in 11 ms but showed only
        // after reselecting the unit). ProcessUnitChanges is the screen's own
        // "a unit changed" path: supplies, list, and the unit window.
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count; i++)
            {
                var ps = open[i]?.TryCast<MissionPrepUIScreen>();
                if (ps == null) continue;
                ps.ProcessUnitChanges(true);
                var w = ps.m_UnitSelector?.GetUnitWindow();
                if (w != null) { w.Refresh(); w.GetEquipment()?.UpdateSquaddiesElements(); }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] prep refresh failed: {e.Message}"); }
        // ...and the Squads tab, if the guest has it open.
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return;
            for (int i = 0; i < open.Count; i++)
            {
                var armory = open[i]?.TryCast<ArmoryUIScreen>();
                if (armory == null) continue;
                armory.m_UnitSelector?.Refresh();
                var window = armory.m_UnitSelector?.GetUnitWindow();
                if (window != null)
                {
                    window.Refresh();
                    window.GetEquipment()?.UpdateSquaddiesElements();
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[EQUIP] squads refresh failed: {e.Message}"); }
    }
}

[HarmonyPatch]
internal static class EquipmentPatches
{
    // Host authority: a connected guest may not change loadouts (only the host
    // does; the guest sees the host's changes mirrored). Blocking at the UI
    // click level leaves the low-level container calls that save-loading and
    // battles depend on untouched.
    // A client may click the equipment of a squad assigned to it in the prep
    // picker (the write itself becomes a request -- EquipmentSync.ireq); every
    // other loadout stays the host's.
    private static bool GuestMayNotEquip(UnitWindowEquipment w)
    {
        if (CoopNet.Mode == CoopMode.Guest && CoopNet.Connected)
        {
            BaseUnitLeader leader = null;
            try { leader = w?.m_Leader; } catch { }
            if (EquipmentSync.LocalOwns(leader)) return false;
            CoopNet.Status = "Only the player this squad is assigned to (or the host) changes its equipment";
            return true; // block
        }
        return false;
    }

    [HarmonyPatch(typeof(UnitWindowEquipment), nameof(UnitWindowEquipment.OnEquipmentSlotLeftClick))]
    [HarmonyPrefix]
    private static bool BlockGuestEquipLeftClick(UnitWindowEquipment __instance)
    {
        if (GuestMayNotEquip(__instance)) return false;
        EquipmentSync.InClick++;
        return true;
    }

    [HarmonyPatch(typeof(UnitWindowEquipment), nameof(UnitWindowEquipment.OnEquipmentSlotRightClick))]
    [HarmonyPrefix]
    private static bool BlockGuestEquipRightClick(UnitWindowEquipment __instance)
    {
        if (GuestMayNotEquip(__instance)) return false;
        EquipmentSync.InClick++;
        return true;
    }

    [HarmonyPatch(typeof(UnitWindowEquipment), nameof(UnitWindowEquipment.OnAlternativeEquipmentItemClickedAction))]
    [HarmonyPrefix]
    private static bool BlockGuestEquipAlternative(UnitWindowEquipment __instance)
    {
        if (GuestMayNotEquip(__instance)) return false;
        EquipmentSync.InClick++;
        return true;
    }

    [HarmonyPatch(typeof(UnitWindowEquipment), nameof(UnitWindowEquipment.OnEquipmentSlotLeftClick))]
    [HarmonyFinalizer]
    private static void EndLeftClick() => EquipmentSync.EndClick();

    [HarmonyPatch(typeof(UnitWindowEquipment), nameof(UnitWindowEquipment.OnEquipmentSlotRightClick))]
    [HarmonyFinalizer]
    private static void EndRightClick() => EquipmentSync.EndClick();

    [HarmonyPatch(typeof(UnitWindowEquipment), nameof(UnitWindowEquipment.OnAlternativeEquipmentItemClickedAction))]
    [HarmonyFinalizer]
    private static void EndAltClick() => EquipmentSync.EndClick();

    // Any click the own-loadout exception lets through (the unequip buttons are
    // plain buttons, not the slot handlers above) is one recorded click too.
    [HarmonyPatch(typeof(Menace.UI.InteractiveElement), nameof(Menace.UI.InteractiveElement.OnClicked))]
    [HarmonyPrefix]
    private static void BeginOwnLoadoutClick(Menace.UI.InteractiveElement __instance, out bool __state)
    {
        __state = EquipmentSync.OwnLoadoutClick(__instance);
        if (__state) EquipmentSync.InClick++;
    }

    [HarmonyPatch(typeof(Menace.UI.InteractiveElement), nameof(Menace.UI.InteractiveElement.OnClicked))]
    [HarmonyFinalizer]
    private static void EndOwnLoadoutClick(bool __state) { if (__state) EquipmentSync.EndClick(); }

    // v0.7.15 renamed Place -> Equip and ItemEventFlags -> ItemEquipFlags.
    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.Equip))]
    [HarmonyPostfix]
    private static void PlacePostfix(ItemContainer __instance, Item _item, int _index, bool __result)
    {
        if (!__result) return;
        EquipmentSync.OnHostPlace(__instance, _item, _index);
        EquipmentSync.RecordClientWrite(__instance, _item, _index, true);
    }

    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.Remove), typeof(Item), typeof(ItemEquipFlags))]
    [HarmonyPostfix]
    private static void RemovePostfix(ItemContainer __instance, Item _item, bool __result)
    {
        if (!__result || _inSlotRemove) return;
        EquipmentSync.OnHostRemove(__instance, _item);
        EquipmentSync.RecordClientWrite(__instance, _item, -1, false);
    }

    // The by-slot overload (unequip from a slot). Engine behaviour unknown: it
    // may or may not call Remove(Item) inside. Capture the item first, relay it
    // once here, and mute the inner overload meanwhile -- one relay either way.
    [ThreadStatic] private static bool _inSlotRemove;

    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.Remove), typeof(ItemSlot), typeof(int))]
    [HarmonyPrefix]
    private static void SlotRemovePrefix(ItemContainer __instance, ItemSlot _slot, int _index, out Item __state)
    {
        __state = null;
        try
        {
            var items = __instance.GetAllItemsAtSlotCopy(_slot);
            if (items != null && _index >= 0 && _index < items.Length) __state = items[_index];
        }
        catch { }
        _inSlotRemove = true;
    }

    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.Remove), typeof(ItemSlot), typeof(int))]
    [HarmonyPostfix]
    private static void SlotRemovePostfix(ItemContainer __instance, Item __state, bool __result)
    {
        _inSlotRemove = false;
        if (!__result || __state == null) return;
        EquipmentSync.OnHostRemove(__instance, __state);
        EquipmentSync.RecordClientWrite(__instance, __state, -1, false);
    }

    // Runs even when the original throws -- never leave removals muted.
    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.Remove), typeof(ItemSlot), typeof(int))]
    [HarmonyFinalizer]
    private static void SlotRemoveFinalizer() => _inSlotRemove = false;
}

// A BOX IN THE OWNER'S COLOUR ROUND EACH PORTRAIT ON THE PREP SCREEN (user
// 2026-10-02, for clarity once squads -- and so loadout rights -- are split
// between players). Colour = the seat the prep picker assigns that unit to;
// no box for a unit not being deployed. Tiles are collected as the screen
// draws them and recoloured every second, so a reassignment shows at once.
[HarmonyPatch]
internal static class PrepPortraits
{
    private static readonly System.Collections.Generic.List<MissionPrepUnitSelectSlot> _slots = new();
    private static float _nextAt;

    [HarmonyPatch(typeof(MissionPrepUnitSelectSlot), nameof(MissionPrepUnitSelectSlot.UpdateDeployed))]
    [HarmonyPostfix]
    private static void Collect(MissionPrepUnitSelectSlot __instance)
    {
        if (__instance == null || !CoopNet.Connected) return;
        foreach (var s in _slots) if (s != null && s.Pointer == __instance.Pointer) { Paint(__instance); return; }
        _slots.Add(__instance);
        Paint(__instance);
    }

    // Called from the co-op panel's 1 s tick.
    internal static void Tick()
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextAt) return;
        _nextAt = now + 1f;
        bool prep = CoopNet.Connected && (SessionSync.HostPrepActive || SessionSync.ViewerPrepActive);
        if (!prep) { _slots.Clear(); return; }
        for (int i = _slots.Count - 1; i >= 0; i--)
            if (!Paint(_slots[i])) _slots.RemoveAt(i);
    }

    internal static void RepaintNow() => _nextAt = 0f;

    private static bool Paint(MissionPrepUnitSelectSlot slot)
    {
        try
        {
            var ve = slot?.TryCast<UnityEngine.UIElements.VisualElement>();
            var leader = slot?.TryCast<BaseUnitSelectSlot>()?.GetLeader();
            if (ve == null) return false;
            int seat = leader == null ? -1 : CoopRunner.PrepSeatOfLeader(leader.Pointer);
            float w = seat >= 0 ? 3f : 0f;
            var c = new UnityEngine.UIElements.StyleColor(SeatColors.Of(seat));
            ve.style.borderTopWidth = w; ve.style.borderBottomWidth = w;
            ve.style.borderLeftWidth = w; ve.style.borderRightWidth = w;
            ve.style.borderTopColor = c; ve.style.borderBottomColor = c;
            ve.style.borderLeftColor = c; ve.style.borderRightColor = c;
            return true;
        }
        catch { return false; }
    }
}
