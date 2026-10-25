using System;
using System.Collections.Generic;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif
using UnityEngine;
using UnityEngine.UIElements;

namespace MenaceCoop;

// HOST/GUEST TAGS ABOVE EVERY PLAYER UNIT.
//
// Squad ownership is assigned once in the planning phase, and after that
// nothing on screen says who commands which unit -- in a firefight it is not
// obvious whose squad you are looking at. These tags make ownership readable
// at a glance for the whole battle.
//
// Labels are pooled VisualElements re-projected through the camera each frame,
// the same approach as the partner tile marker: UI Toolkit elements live in
// screen space, so a world position has to be re-projected every frame or the
// tag detaches from its unit as soon as the camera moves.
internal static class OwnerLabels
{
    private sealed class Tag
    {
        internal Label El;
        internal bool InUse;
    }

    private static readonly List<Tag> _pool = new();
    private static VisualElement _root;

    // Colour and text per seat (SeatColors, Peers.cs): host is Player 1, guest is
    // Player 2 -- matches the turn banner, so "PLAYER 2 IS ACTING" and the tags agree.

    private static readonly Dictionary<IntPtr, int> _perTile = new();
    private static readonly HashSet<IntPtr> _loggedStack = new();
    private const float RowHeight = 17f;   // 11px text + plate padding, and a gap

    internal static void Attach(VisualElement root) => _root = root;

    internal static void Reset()
    {
        foreach (var t in _pool) { try { t.El?.RemoveFromHierarchy(); } catch { } }
        _pool.Clear();
    }

    private static Tag Rent()
    {
        foreach (var t in _pool) if (!t.InUse) { t.InUse = true; return t; }

        var el = new Label("")
        {
            pickingMode = PickingMode.Ignore   // never steal clicks from the board
        };
        el.style.position = new StyleEnum<Position>(Position.Absolute);
        el.style.fontSize = new StyleLength(11f);
        el.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
        // A dark plate behind the text keeps it legible over bright terrain.
        el.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.55f));
        el.style.paddingLeft = new StyleLength(3f);
        el.style.paddingRight = new StyleLength(3f);
        el.style.borderTopLeftRadius = new StyleLength(3f);
        el.style.borderTopRightRadius = new StyleLength(3f);
        el.style.borderBottomLeftRadius = new StyleLength(3f);
        el.style.borderBottomRightRadius = new StyleLength(3f);

        var tag = new Tag { El = el, InUse = true };
        _pool.Add(tag);
        try { _root?.Add(el); } catch { }
        return tag;
    }

    internal static void Tick()
    {
        if (_root == null) return;

        // Only meaningful once ownership has actually been divided up.
        if (!Plugin.ShowOwnerLabels.Value || !CoopNet.Connected || !Ownership.Active)
        {
            HideAll();
            return;
        }

        try
        {
            var tm = TacticalManager.Get();
            var cam = Camera.main;
            if (tm == null || cam == null) { HideAll(); return; }

            var actors = tm.GetPlayerFaction()?.GetActors();
            if (actors == null) { HideAll(); return; }

            float w = _root.resolvedStyle.width;
            float h = _root.resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;

            foreach (var t in _pool) t.InUse = false;

            // STACKED, NOT OVERLAPPED (user, 2026-09-25): a unit riding in a
            // vehicle shares the vehicle's tile, so a host squad in a guest carrier
            // drew "HOST" and "GUEST" on top of each other. Tags are counted per
            // tile: vehicles/units on foot first (pass 0) so theirs stays at the
            // usual height, then passengers (pass 1), each one row higher.
            _perTile.Clear();
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                // A dying unit is mid-animation and about to leave the faction;
                // tagging it just leaves a label floating over a corpse.
                try { if (a.IsDying()) continue; } catch { continue; }
                if (!Ownership.IsKnownPlayerUnit(a)) continue;

                bool aboard;
                try { aboard = a.IsContainedWithinAnotherEntity(); } catch { aboard = false; }
                if (aboard != (pass == 1)) continue;

                Tile tile = null;
                try
                {
                    if (aboard) tile = a.GetContainerEntity()?.TryCast<Actor>()?.GetLastTile();
                    if (tile == null) tile = a.GetLastTile();
                }
                catch { continue; }
                if (tile == null) continue;

                Vector3 sp;
                try { sp = cam.WorldToScreenPoint(tile.GetPos()); } catch { continue; }
                if (sp.z <= 0f) continue;   // behind the camera

                _perTile.TryGetValue(tile.Pointer, out int row);
                _perTile[tile.Pointer] = row + 1;

                int seat = Ownership.OwnerOf(a);
                string label = Peers.Label(seat);

                var tag = Rent();
                tag.El.text = label;
                tag.El.style.color = new StyleColor(SeatColors.Of(seat));
                tag.El.visible = true;

                float dx = (sp.x / Screen.width) * w;
                float dy = (1f - sp.y / Screen.height) * h;
                // Sit above the unit rather than on it, so the tag never covers
                // the model or the engine's own selection ring.
                tag.El.style.left = new StyleLength(dx - 20f);
                tag.El.style.top = new StyleLength(dy - 46f - row * RowHeight);
                if (row > 0 && _loggedStack.Add(a.Pointer))
                    Plugin.Logger.LogInfo($"[UI] '{DesyncDetector.TemplateNameOf(a)}' ({label}) aboard -- tag stacked at row {row} above its vehicle's");
            }

            foreach (var t in _pool) if (!t.InUse && t.El != null) t.El.visible = false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Owner labels failed: {e.Message}");
            HideAll();
        }
    }

    private static void HideAll()
    {
        foreach (var t in _pool) { if (t.El != null) t.El.visible = false; t.InUse = false; }
    }
}
