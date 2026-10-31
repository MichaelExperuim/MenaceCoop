#if COOP_DEVTOOLS
using System;
using System.Collections.Generic;
using Il2CppMenace.Items;
using Il2CppMenace.States;
using Il2CppMenace.Tactical;
using UnityEngine;
using UnityEngine.UIElements;

namespace MenaceCoop;

// DEVELOPMENT ONLY -- compiled out of every release (user, 2026-10-03).
// The csproj defines COOP_DEVTOOLS unless built with -p:DevTools=false, which
// Build-MelonRelease.ps1 does, and that script then refuses to package a DLL
// containing the "[DEVTOOLS]" marker every log line here carries.
//
// Lives in the co-op panel's DEBUG SETTINGS: search the game's item templates,
// pick one, add it to the black market or straight to the inventory. Host only,
// strategy layer only; connected clients are then synced to the host's game.
internal static class DevItemMenu
{
    private const string Tag = "[DEVTOOLS]";
    private static readonly List<BaseItemTemplate> _matches = new();
    private static int _idx;
    private static Button _pick;
    private static TextField _filter;

    internal static void Build(VisualElement body, Func<string, Action, Button> makeButton,
                               Action<Label> styleLabel, Action<TextField> styleField)
    {
        var header = new Label("DEV: ITEMS (development builds only, host)");
        styleLabel(header);
        header.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.style.marginTop = new StyleLength(6f);
        body.Add(header);

        _filter = new TextField("Search");
        _filter.value = "skybot";
        styleField(_filter);
        body.Add(_filter);
        body.Add(makeButton("Find items", Refilter));
        _pick = makeButton("Item: (press Find items)", () => Step(+1));
        body.Add(_pick);
        body.Add(makeButton("Previous item", () => Step(-1)));
        body.Add(makeButton("Add to black market", () => Add(toMarket: true)));
        body.Add(makeButton("Add to inventory", () => Add(toMarket: false)));
    }

    private static void Refilter()
    {
        _matches.Clear();
        _idx = 0;
        string want = (_filter?.value ?? "").Trim().ToLowerInvariant();
        try
        {
            var type = Il2CppInterop.Runtime.Il2CppType.Of<BaseItemTemplate>();
            try { Resources.LoadAll("Data", type); } catch { }
            var all = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]?.TryCast<BaseItemTemplate>();
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                if (want.Length == 0 || t.name.ToLowerInvariant().Contains(want)) _matches.Add(t);
            }
            _matches.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"{Tag} Item search threw {e.GetType().Name}: {e.Message}"); }
        Plugin.Logger.LogInfo($"{Tag} Item search '{want}': {_matches.Count} match(es)");
        ShowPick();
    }

    private static void Step(int d)
    {
        if (_matches.Count == 0) { Refilter(); return; }
        _idx = (_idx + d + _matches.Count) % _matches.Count;
        ShowPick();
    }

    private static void ShowPick()
    {
        if (_pick == null) return;
        _pick.text = _matches.Count == 0 ? "Item: (no match)"
            : $"Item: {_matches[_idx].name} ({_idx + 1}/{_matches.Count})";
    }

    private static void Add(bool toMarket)
    {
        if (CoopNet.Mode == CoopMode.Guest) { CoopNet.Status = "Item tools: host only"; return; }
        var strat = StrategyState.Get();
        if (strat == null || TacticalManager.Get() != null)
        {
            CoopNet.Status = "Item tools: open the strategy layer (not a battle)";
            return;
        }
        if (_matches.Count == 0) { CoopNet.Status = "Item tools: find an item first"; return; }
        var t = _matches[_idx];
        bool prev = StrategyMirror.Applying;
        StrategyMirror.Applying = true;   // the sync below carries it; no per-item relay
        try
        {
            if (toMarket)
            {
                var item = t.CreateItem(BaseItemTemplate.CreateGuid());
                strat.BlackMarket.AddItem(item, 99);
            }
            else strat.OwnedItems.AddItem(t, false, false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"{Tag} Adding '{t.name}' threw {e.GetType().Name}: {e.Message}");
            CoopNet.Status = "Item tools: add failed (see log)";
            return;
        }
        finally { StrategyMirror.Applying = prev; }
        string where = toMarket ? "black market" : "inventory";
        Plugin.Logger.LogInfo($"{Tag} Added '{t.name}' to the {where}");
        CoopNet.Status = $"Added {t.name} to the {where}" + (toMarket ? " (reopen the market)" : "");
        if (CoopNet.Connected) SessionSync.StartSync();
    }
}
#endif
