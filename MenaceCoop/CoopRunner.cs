using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppMenace.Strategy;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Menu;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
using Steamworks = Il2CppSteamworks;
using CfgBool = MelonLoader.MelonPreferences_Entry<bool>;
#else
using Menace.Strategy;
using Menace.UI;
using Menace.UI.Menu;
using CfgBool = BepInEx.Configuration.ConfigEntry<bool>;
#endif
using UnityEngine;
using UnityEngine.UIElements;

namespace MenaceCoop;

// Injected MonoBehaviour: drains network messages on the Unity main thread and
// owns the co-op connection panel. Menace's UI is UI Toolkit (UIElements) --
// uGUI/TMP and IMGUI are stripped or unused -- so the panel is UIElements too,
// hosted in our own UIDocument that borrows the game's PanelSettings (which
// brings the game's theme and fonts along).
public class CoopRunner : MonoBehaviour
{
    public CoopRunner(IntPtr ptr) : base(ptr) { }

    private static GameObject _uiGo;
    private static VisualElement _container;
    private static VisualElement _collapsed;
    private static VisualElement _buttonRow;
    private static Label _status;
    private static Label _thinkingLabel;
    private static TextField _ipField;
    private static TextField _portField;
    private static TextField _passwordField;
    private static Button _hostBtn;
    private static Button _joinBtn;
    private static Button _discBtn;
    private static Button _letMeClickBtn;   // guest, strategy layer: StrategyMirror.ToggleFreeClicks
    private static Button _sendSaveBtn, _resumeBattleBtn, _stopWaitingBtn;
    private static VisualElement _steamRow;
    private static VisualElement _cursorSection;
    private static bool _cursorSectionShown;
    private static bool _battleSectionShown;
    private static VisualElement _debugBody;
    private static Label _debugEmptyNote;
    private static bool _debugOpen;
    private static Button _forceBtn, _checkpointBtn, _saveBattleBtn, _passTurnBtn;
    private static VisualElement _battleSection;
    internal static float LastUserInputAt;
    private static Button _steamHostBtn;
    private static Button _steamJoinBtn;
    private static TextField _steamIdField;
    private static TextField _nameField;
    private static Button _nameBtn;
    private static bool _buildFailed;
    private static bool _lastOff = true;
    private static int _waitFrames;

    private static VisualElement _root;
    private static float _lastCursorSent;

    // Drag-to-move for the panel (grab the title bar). Polled rather than using
    // UI Toolkit pointer callbacks, which don't register reliably under IL2CPP.
    private static Label _titleBar;
    private static bool _dragging;
    private static Vector2 _dragMouseStart;
    private static float _dragStartLeft;
    private static float _dragStartTop;
    private static bool _prevMouseDown;
    private static int _refitCounter;

    private void Update()
    {
        if (_container == null && !_buildFailed)
            TryBuildUi();

        // Re-fit the panel to the window periodically so a resolution or
        // window change can't push controls off-screen again.
        if (_container != null && ++_refitCounter >= 120) { _refitCounter = 0; ApplyPanelHeightLimit(); }

        CoopNet.PumpSteam();
        SteamLobby.Tick();

#if COOP_DEVTOOLS
        while (NetLag.TryNext(out var msg))   // dev: optional simulated slow link
#else
        while (CoopNet.Incoming.TryDequeue(out var msg))
#endif
        {
            if (!Peers.Accept(msg)) continue;
            if (AiPacing.Intercept(msg)) continue;   // played back in order by AiPacing.Tick
            try
            {
                Replication.Apply(msg);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[SYNC] Failed to apply remote message '{msg.T}': {e}");
            }
        }
        AiPacing.Tick();
        DeploySync.Tick();
        FogSync.Tick();
        FogTimingProbe.Tick();
        FacingSync.Tick();
        DebugSync.ProbeSkybotsTick();
        TryPendingAutoConnect();

        // Track real user input: the ManualTurnEnd gate distinguishes the End
        // Turn button/hotkey (always right after a click or key press) from the
        // engine's automatic end-turn (fired from animation completion).
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if ((mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                || (kb != null && kb.anyKey.wasPressedThisFrame))
                LastUserInputAt = Time.realtimeSinceStartup;
        }
        catch { }

        // Each subsystem individually guarded: one throwing tick must not kill
        // the whole update loop (it re-fires every frame -- an invisible
        // console-only exception spiral that presents as a total freeze).
        Replication.TrackBoardActivity();
        try { Replication.EnforceTurnSettings(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] TurnSettings: {e.Message}"); }
        try { TurnControl.Tick(); UpdatePhaseBanner(); UpdateHoldOverlay(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] TurnControl: {e.Message}"); }
        try { Replication.CatchUpToHostRound(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] CatchUp: {e.Message}"); }
        try { Replication.GuardRoundRunaway(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] Runaway: {e.Message}"); }
        try { Replication.RelayAiTurnEnds(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] AiDone: {e.Message}"); }
        try { ContainerSync.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] Container: {e.Message}"); }
        try { PhantomWatch.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] Phantom: {e.Message}"); }
        try { UpdatePhantomMarks(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] PhantomMarks: {e.Message}"); }
        try { Replication.SettleIncapacitatedUnits(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] Incap: {e.Message}"); }
        try { Replication.ReassertOwnTurnEnds(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] Reassert: {e.Message}"); }
        try { Replication.CheckPlayerPhaseEnd(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] PhaseEnd: {e.Message}"); }
        try { Replication.UpdateFactionPump(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] FactionPump: {e.Message}"); }
        try { Replication.TryApplyFactionAdvances(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] FactionAdvances: {e.Message}"); }
        try { Replication.RetryDeferred(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] RetryDeferred: {e.Message}"); }
        try { Replication.VerifyRelayedMoves(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] RelayedMoves: {e.Message}"); }
        try { Replication.UpdateActionLock(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] ActionLock: {e.Message}"); }
        try { Replication.HostPhaseHeartbeat(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] HostPhase: {e.Message}"); }
        try { ObjectiveProgressSync.Tick(); } catch { }
        try { DesyncDetector.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] DesyncTick: {e.Message}"); }
        try { GuestReload.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] GuestReload: {e.Message}"); }
        try { BattleSave.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] BattleSave: {e.Message}"); }
        // Not harness-gated: knowing which operations a save can offer is exactly
        // what a human needs when choosing what to test against.
        try { UiAutopilot.ListOperations(); } catch { }
        try { BattleRoster.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] RosterTick: {e.Message}"); }
        try { TerrainCheck.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] TerrainTick: {e.Message}"); }
        try { SessionSync.Update(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] SessionSync: {e.Message}"); }
        try { SelfTest.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] SelfTest: {e.Message}"); }
        try { UiAutopilot.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] UiAutopilot: {e.Message}"); }
        try { StrategyMirror.Tick(); } catch (Exception e) { Plugin.Logger.LogError($"[TICK] StrategyMirror: {e.Message}"); }
        Ownership.UpdateLifecycle();
        UpdatePanelDrag();
        UpdateUiState();
        UpdateAssignments();
        UpdateTitleMenu();
        UpdateSyncBlocker();
        SendCursor();
        UpdateRemoteCursor();
        // Independent of the partner-cursor toggle: ownership tags are about
        // YOUR OWN board readability, not about what the partner is doing.
        OwnerLabels.Tick();
        BattleWatchdog.Tick();
    }

    internal static void OnRemoteCursor(int seat, float nx, float ny)
    {
        var mk = Mark(seat);
        mk.Norm = new Vector2(nx, ny);
        mk.At = Time.realtimeSinceStartup;
    }

    // Cursor sharing. Outside battles: normalized screen position at ~10 Hz.
    // In battles the players' cameras differ, so a screen position is
    // meaningless -- instead broadcast the HOVERED TILE (on change + 2s
    // keepalive) and let the remote side pin its marker to that tile.
    private static int _lastTileX = int.MinValue;
    private static int _lastTileY = int.MinValue;
    private static int _lastTileSel;
    private static int _lastTileOX;
    private static int _lastTileOY;
    private static float _lastTileKeepalive;

    // A button bound to a persisted bool config entry; its label shows ON/OFF
    // and clicking flips the value.
    private static Button _autoToggle;
    private static Button _dbgPickBtn;
    private static string AutoToggleText() => AutoCombatBridge.Delegated
        ? "Auto combat (your squads): " + (AutoCombatOn() ? "ON" : "OFF")
        : AutoCombatBridge.ModInstalled
            ? "Auto combat: update MenaceAutoCombat (not connected)"
            : "Auto combat: needs the MenaceAutoCombat mod";

    // ON whenever auto combat is actually driving: the Auto Combat mod's switch
    // OR the dev harness, which drives it without flipping that switch (user,
    // 2026-09-27: the tab read OFF while units fought on their own). Co-op ships
    // no brain of its own (user, 2026-09-28), so without that mod it is never on.
    private static bool AutoCombatOn()
    {
        if (!AutoCombatBridge.Delegated) return false;
        if (Plugin.SelfTestAutoPlay != null && Plugin.SelfTestAutoPlay.Value) return true;
        return AutoCombatBridge.TheirEnabled;
    }

    private static Button MakeToggle(string prefix, CfgBool cfg)
    {
        Button b = null;
        b = MakeButton(prefix + (cfg.Value ? "ON" : "OFF"), () =>
        {
            cfg.Value = !cfg.Value;
            if (b != null) b.text = prefix + (cfg.Value ? "ON" : "OFF");
        });
        return b;
    }

    private static void SendCursor()
    {
        if (!CoopNet.Connected) return;
        if (!Plugin.ShareCursor.Value) return; // player has hidden their cursor from the partner
        float now = Time.realtimeSinceStartup;

        var tm = Menace.Tactical.TacticalManager.Get();
        if (tm != null)
        {
            var tile = Menace.States.TacticalState.Get()?.GetCurrentTile();
            if (tile == null) return;
            var p = tile.GetTilePos();

            // When the player has a unit selected on their own turn, also send
            // that unit's tile so the partner sees the intended MOVE as an orange
            // line (unit -> hovered tile), instead of just the hovered-tile
            // bracket. Best-effort: if there's no active unit it stays a bracket.
            int ox = 0, oy = 0, sel = 0;
            try
            {
                if (Plugin.ShareLine.Value && tm.IsPlayerTurn())
                {
                    var origin = tm.GetActiveActor()?.GetLastTile();
                    if (origin != null)
                    {
                        var op = origin.GetTilePos();
                        if (op.x != p.x || op.y != p.y) { ox = op.x; oy = op.y; sel = 1; }
                    }
                }
            }
            catch { }

            bool changed = p.x != _lastTileX || p.y != _lastTileY
                           || sel != _lastTileSel || ox != _lastTileOX || oy != _lastTileOY;
            if (!changed && now - _lastTileKeepalive < 2f) return;
            _lastTileX = p.x; _lastTileY = p.y;
            _lastTileSel = sel; _lastTileOX = ox; _lastTileOY = oy;
            _lastTileKeepalive = now;
            CoopNet.Send(new CoopMsg { T = "tcursor", AX = p.x, AY = p.y, TX = ox, TY = oy, Prm = sel });
            return;
        }

        if (now - _lastCursorSent < 0.1f) return;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return;
        _lastCursorSent = now;
        Vector2 mp = mouse.position.ReadValue();
        CoopNet.Send(new CoopMsg
        {
            T = "cursor",
            CX = Mathf.Clamp01(mp.x / Screen.width),
            CY = Mathf.Clamp01(1f - mp.y / Screen.height), // flip to top-left origin
        });
    }

    // ONE MARKER PER OTHER PLAYER (v5; user 2026-10-02: "recall the dots").
    // Each seat has its own dot / tile bracket / move line, in that seat's colour
    // (SeatColors) and labelled with that player's name, so with three players
    // the two partners' cursors no longer share -- and flicker between -- one dot.
    private sealed class RemoteMark
    {
        internal int Seat;
        internal Vector2 Norm;
        internal float At = -999f;
        internal Vector2Int Tile, Origin;
        internal bool HasSel;
        internal float TileAt = -999f;
        internal VisualElement Dot;
        internal Label Tag;
        internal VisualElement Line;
        internal bool BattleMode;
        internal bool Failed;
    }
    private static readonly System.Collections.Generic.Dictionary<int, RemoteMark> _marks = new();

    private static RemoteMark Mark(int seat)
    {
        if (seat < 0) seat = Peers.LinkSeat;     // older build: no sender seat
        if (!_marks.TryGetValue(seat, out var mk)) { mk = new RemoteMark { Seat = seat }; _marks[seat] = mk; }
        return mk;
    }

    internal static void OnRemoteTileCursor(int seat, int x, int y, int ox, int oy, int sel)
    {
        var mk = Mark(seat);
        mk.Tile = new Vector2Int(x, y);
        mk.Origin = new Vector2Int(ox, oy);
        mk.HasSel = sel != 0;
        mk.TileAt = Time.realtimeSinceStartup;
    }

    private static void CreateRemoteCursor(RemoteMark mk)
    {
        try
        {
            var dot = new VisualElement();
            dot.pickingMode = PickingMode.Ignore;
            dot.style.position = new StyleEnum<Position>(Position.Absolute);
            dot.style.width = new StyleLength(14f);
            dot.style.height = new StyleLength(14f);
            dot.style.backgroundColor = new StyleColor(RemoteCol(mk.Seat, 0.95f));
            dot.style.borderTopLeftRadius = new StyleLength(7f);
            dot.style.borderTopRightRadius = new StyleLength(7f);
            dot.style.borderBottomLeftRadius = new StyleLength(7f);
            dot.style.borderBottomRightRadius = new StyleLength(7f);

            var tag = new Label(Peers.NameOf(mk.Seat));
            tag.pickingMode = PickingMode.Ignore;
            tag.style.position = new StyleEnum<Position>(Position.Absolute);
            tag.style.left = new StyleLength(16f);
            tag.style.top = new StyleLength(-2f);
            tag.style.fontSize = new StyleLength(11f);
            tag.style.color = new StyleColor(RemoteCol(mk.Seat, 0.95f));
            dot.Add(tag);

            _root.Add(dot);
            mk.Dot = dot;
            mk.Tag = tag;
            mk.BattleMode = false;
            Plugin.Logger.LogInfo($"[UI] Cursor marker created for Player {mk.Seat + 1}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Remote cursor unavailable: {e.Message}");
            mk.Dot = null;
            mk.Failed = true;
        }
    }

    // ---- PHANTOM ENEMY MARKERS ---------------------------------------------
    //
    // Same idea as the partner-cursor tile bracket, in red, over every tile the
    // partner reports an enemy on that this machine has nothing on. Asked for
    // directly: "if a host is getting shot at from an invisible enemy and it
    // shows up on the guest, put the same thing we use for the cursor over them."
    //
    // Pooled, because the count changes every few seconds and rebuilding
    // VisualElements per frame in IL2CPP is not free.
    private static readonly System.Collections.Generic.List<VisualElement> _phantomMarks = new();

    private static VisualElement MakePhantomMark()
    {
        var v = new VisualElement();
        v.pickingMode = PickingMode.Ignore;
        v.style.position = new StyleEnum<Position>(Position.Absolute);
        v.style.width = new StyleLength(28f);
        v.style.height = new StyleLength(28f);
        var red = new Color(0.95f, 0.20f, 0.20f);
        v.style.backgroundColor = new StyleColor(new Color(red.r, red.g, red.b, 0.16f));
        SetBorder(v, 2f, red);

        var tag = new Label("ENEMY");
        tag.pickingMode = PickingMode.Ignore;
        tag.style.position = new StyleEnum<Position>(Position.Absolute);
        tag.style.left = new StyleLength(30f);
        tag.style.top = new StyleLength(4f);
        tag.style.fontSize = new StyleLength(11f);
        tag.style.unityFontStyleAndWeight = FontStyle.Bold;
        tag.style.color = new StyleColor(red);
        v.Add(tag);

        _root.Add(v);
        return v;
    }

    private static void UpdatePhantomMarks()
    {
        try
        {
            if (_root == null) return;
            var list = PhantomWatch.Phantoms;
            var cam = Camera.main;
            var map = Menace.Tactical.TacticalManager.Get()?.GetMap();
            float w = _root.resolvedStyle.width, h = _root.resolvedStyle.height;

            int shown = 0;
            if (cam != null && map != null && w > 0f && h > 0f && Plugin.ShowPartnerCursor.Value)
            {
                for (int i = 0; i < list.Count && shown < 24; i++)
                {
                    var t = map.GetTile(list[i].X, list[i].Y);
                    if (t == null) continue;
                    Vector3 sp = cam.WorldToScreenPoint(t.GetPos());
                    if (sp.z <= 0f) continue;                       // behind the camera

                    while (_phantomMarks.Count <= shown) _phantomMarks.Add(MakePhantomMark());
                    var mark = _phantomMarks[shown];
                    var label = mark.Q<Label>();
                    if (label != null) label.text = "ENEMY  " + PhantomWatch.Pretty(list[i].Template);
                    mark.visible = true;
                    mark.style.left = new StyleLength((sp.x / Screen.width) * w - 14f);
                    mark.style.top = new StyleLength((1f - sp.y / Screen.height) * h - 14f);
                    shown++;
                }
            }
            for (int i = shown; i < _phantomMarks.Count; i++) _phantomMarks[i].visible = false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Phantom markers failed: {e.Message}");
            for (int i = 0; i < _phantomMarks.Count; i++) _phantomMarks[i].visible = false;
        }
    }

    // Colour identifies WHOSE cursor this is -- the seat's own colour, the same
    // as its unit tags (host blue, player 2 orange, 3 red, 4 green).
    private static Color RemoteCol(int seat, float a)
    {
        Color c = SeatColors.Of(seat);
        c.a = a;
        return c;
    }

    private static void ApplyMarkerMode(RemoteMark mk, bool battle)
    {
        if (mk.BattleMode == battle || mk.Dot == null) return;
        mk.BattleMode = battle;
        if (battle)
        {
            // hollow tile bracket
            mk.Dot.style.width = new StyleLength(28f);
            mk.Dot.style.height = new StyleLength(28f);
            mk.Dot.style.backgroundColor = new StyleColor(RemoteCol(mk.Seat, 0.12f));
            SetBorder(mk.Dot, 2f, RemoteCol(mk.Seat, 0.95f));
        }
        else
        {
            // solid dot
            mk.Dot.style.width = new StyleLength(14f);
            mk.Dot.style.height = new StyleLength(14f);
            mk.Dot.style.backgroundColor = new StyleColor(RemoteCol(mk.Seat, 0.95f));
            SetBorder(mk.Dot, 0f, Color.clear);
        }
    }

    private static void UpdateRemoteCursor()
    {
        if (_root == null) return;
        foreach (var mk in _marks.Values) UpdateRemoteCursor(mk);
    }

    private static void UpdateRemoteCursor(RemoteMark mk)
    {
        if (!Plugin.ShowPartnerCursor.Value) // partners' cursors hidden on my screen
        {
            if (mk.Dot != null) mk.Dot.visible = false;
            HideMoveLine(mk);
            return;
        }
        float rnow = Time.realtimeSinceStartup;
        bool inBattle = Menace.Tactical.TacticalManager.Get() != null;
        // A player who has left keeps no marker.
        bool here = CoopNet.Connected && Peers.IsConnected(mk.Seat);
        bool tileFresh = here && inBattle && rnow - mk.TileAt < 6f;
        bool fresh = here && !inBattle && rnow - mk.At < 3f;

        if (tileFresh)
        {
            UpdateTileMarker(mk);
            return;
        }
        HideMoveLine(mk); // no battle tile marker -> no move line

        if (mk.Dot == null)
        {
            if (!fresh || mk.Failed) return;
            CreateRemoteCursor(mk);
            if (mk.Dot == null) return;
        }

        ApplyMarkerMode(mk, false);
        mk.Dot.visible = fresh;
        if (!fresh) return;
        if (mk.Tag != null) mk.Tag.text = Peers.NameOf(mk.Seat);
        float w = _root.resolvedStyle.width;
        float h = _root.resolvedStyle.height;
        if (w <= 0f || h <= 0f) return;
        mk.Dot.style.left = new StyleLength(mk.Norm.x * w - 7f);
        mk.Dot.style.top = new StyleLength(mk.Norm.y * h - 7f);
    }

    // Battle marker: pinned to that player's hovered TILE in the world,
    // re-projected through the camera every frame so it tracks camera moves.
    private static void UpdateTileMarker(RemoteMark mk)
    {
        try
        {
            if (mk.Dot == null)
            {
                if (mk.Failed) return;
                CreateRemoteCursor(mk);
                if (mk.Dot == null) return;
            }
            var tile = Menace.Tactical.TacticalManager.Get()?.GetMap()?.GetTile(mk.Tile.x, mk.Tile.y);
            var cam = Camera.main;
            if (tile == null || cam == null)
            {
                mk.Dot.visible = false;
                HideMoveLine(mk);
                return;
            }
            Vector3 sp = cam.WorldToScreenPoint(tile.GetPos());
            if (sp.z <= 0f)
            {
                mk.Dot.visible = false; // behind the camera
                HideMoveLine(mk);
                return;
            }
            float w = _root.resolvedStyle.width;
            float h = _root.resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;
            ApplyMarkerMode(mk, true);
            mk.Dot.visible = true;
            if (mk.Tag != null) mk.Tag.text = Peers.NameOf(mk.Seat);
            float dx = (sp.x / Screen.width) * w;
            float dy = (1f - sp.y / Screen.height) * h;
            mk.Dot.style.left = new StyleLength(dx - 14f);
            mk.Dot.style.top = new StyleLength(dy - 14f);

            // If that player has a unit selected, draw the intended move as a
            // line in their colour from that unit's tile to the hovered tile.
            UpdateMoveLine(mk, dx, dy, w, h, cam);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Tile marker update failed: {e.Message}");
            mk.TileAt = -999f;
        }
    }

    private static void HideMoveLine(RemoteMark mk)
    {
        if (mk.Line != null) mk.Line.visible = false;
    }

    // Move line (that player's selected unit -> its hovered destination),
    // mirroring the game's own move indicator. A thin VisualElement centred on
    // the midpoint and rotated to the two endpoints (default transform origin is
    // the element centre, so a centred horizontal bar rotates into place).
    private static void UpdateMoveLine(RemoteMark mk, float dx, float dy, float w, float h, Camera cam)
    {
        if (!mk.HasSel || !Plugin.ShowPartnerLine.Value)
        {
            HideMoveLine(mk);
            return;
        }
        var otile = Menace.Tactical.TacticalManager.Get()?.GetMap()?.GetTile(mk.Origin.x, mk.Origin.y);
        if (otile == null) { HideMoveLine(mk); return; }
        Vector3 osp = cam.WorldToScreenPoint(otile.GetPos());
        if (osp.z <= 0f) { HideMoveLine(mk); return; }
        float ox = (osp.x / Screen.width) * w;
        float oy = (1f - osp.y / Screen.height) * h;

        if (mk.Line == null)
        {
            mk.Line = new VisualElement();
            mk.Line.pickingMode = PickingMode.Ignore;
            mk.Line.style.position = new StyleEnum<Position>(Position.Absolute);
            mk.Line.style.height = new StyleLength(3f);
            _root.Add(mk.Line);
            mk.Line.PlaceBehind(mk.Dot); // keep the destination bracket on top
        }

        float mx = (ox + dx) * 0.5f;
        float my = (oy + dy) * 0.5f;
        float ddx = dx - ox, ddy = dy - oy;
        float len = Mathf.Sqrt(ddx * ddx + ddy * ddy);
        float deg = Mathf.Atan2(ddy, ddx) * Mathf.Rad2Deg;
        mk.Line.style.backgroundColor = new StyleColor(RemoteCol(mk.Seat, 0.9f));
        mk.Line.style.left = new StyleLength(mx - len * 0.5f);
        mk.Line.style.top = new StyleLength(my - 1.5f);
        mk.Line.style.width = new StyleLength(len);
        mk.Line.style.rotate = new StyleRotate(new Rotate(new Angle(deg, AngleUnit.Degree)));
        mk.Line.visible = true;
    }

    private static void TryBuildUi()
    {
        PanelSettings settings = null;
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PanelSettings>());
        if (all != null && all.Length > 0) settings = all[0].TryCast<PanelSettings>();
        if (settings == null)
        {
            if (++_waitFrames == 1800)
                Plugin.Logger.LogWarning("[UI] No PanelSettings found after 1800 frames; co-op panel unavailable");
            return;
        }

        try
        {
            _uiGo = new GameObject("MenaceCoopUI");
            UnityEngine.Object.DontDestroyOnLoad(_uiGo);
            var doc = _uiGo.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.sortingOrder = 5000f;
            var root = doc.rootVisualElement;
            _root = root;
            OwnerLabels.Attach(root);

            _container = new VisualElement();
            TryStylePanel(_container);
            root.Add(_container);

            var title = new Label(":: MENACE (co-op)   (drag to move)");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            StyleLabel(title);
            _container.Add(title);
            _titleBar = title;

            // Persistent version line: mod version + game build (before the '|'
            // metadata size). Handy for confirming both players match.
            string gameVer = Plugin.GameFingerprint;
            int bar = gameVer.IndexOf('|');
            if (bar > 0) gameVer = gameVer.Substring(0, bar);
            var versionInfo = new Label($"Mod v{Plugin.PluginVersion}   |   Game {gameVer}");
            StyleLabel(versionInfo);
            versionInfo.style.fontSize = new StyleLength(11f);
            _container.Add(versionInfo);

            _status = new Label("MenaceCoop " + Plugin.PluginVersion);
            StyleLabel(_status);
            _container.Add(_status);

            _ipField = new TextField("Host IP");
            _ipField.value = Plugin.HostAddress.Value;
            StyleField(_ipField);
            _container.Add(_ipField);

            _portField = new TextField("Port");
            _portField.value = Plugin.Port.Value.ToString();
            StyleField(_portField);
            _container.Add(_portField);

            // Direct IP only (CoopAuth.cs): both players use the same one. The host
            // normally shares a GENERATED code (NEW CODE) rather than a personal
            // password; the field stays editable for anyone who wants their own.
            // NOT pre-filled here: the panel is built before the player picks Host
            // or Join, and a guest shown a valid-looking code of its own reads IT
            // out instead of asking for the host's. Host (direct IP) makes one if
            // the field is empty.
            var pwHint = new Label("Host: type your own password or press NEW CODE, then COPY and send it. Guest: enter the host's.");
            StyleLabel(pwHint);
            pwHint.style.fontSize = new StyleLength(10f);
            _container.Add(pwHint);
            _passwordField = new TextField("Password");
            _passwordField.isPasswordField = true;
            _passwordField.value = Plugin.SessionPassword.Value;
            StyleField(_passwordField);
            _container.Add(_passwordField);
            var pwRow = new VisualElement();
            pwRow.style.flexDirection = FlexDirection.Row;
            pwRow.Add(MakeButton("SHOW", () =>
            {
                _passwordField.isPasswordField = !_passwordField.isPasswordField;
            }));
            pwRow.Add(MakeButton("NEW CODE", () =>
            {
                _passwordField.value = CoopAuth.NewCode();
                _passwordField.isPasswordField = false;   // shown, so it can be read to the partner
                CoopNet.Status = "New session code -- give it to your partner (not a password you use anywhere else)";
            }));
            // Share it by paste (Discord etc.) instead of reading it out. Works the
            // same for a typed password and a generated code.
            pwRow.Add(MakeButton("COPY", () =>
            {
                string v = _passwordField.value ?? "";
                if (v.Length == 0) { CoopNet.Status = "Type a password (or press NEW CODE) first"; return; }
                try { GUIUtility.systemCopyBuffer = v; CoopNet.Status = "Password copied -- paste it to your partner"; }
                catch { CoopNet.Status = "Could not copy -- press SHOW and read it out instead"; }
            }));
            _container.Add(pwRow);

            _steamIdLabel = new Label("Your Steam ID: ...");
            StyleLabel(_steamIdLabel);
            _container.Add(_steamIdLabel);

            // Optional since v5.1: Host (Steam) opens a lobby and friends come in
            // by invite. Typing IDs stays as the fallback.
            _steamIdField = new TextField("Partner ID (optional)");
            _steamIdField.value = Plugin.PartnerSteamId.Value;
            StyleField(_steamIdField);
            _container.Add(_steamIdField);

            var playersHeader = new Label("PLAYERS");
            StyleLabel(playersHeader);
            playersHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            playersHeader.style.marginTop = new StyleLength(6f);
            _container.Add(playersHeader);
            _playersBox = new VisualElement();
            _container.Add(_playersBox);

            _buttonRow = new VisualElement();
            _container.Add(_buttonRow);
            _steamRow = new VisualElement();
            _container.Add(_steamRow);
            // One JOIN button per friend who is hosting right now (SteamLobby).
            _friendBox = new VisualElement();
            _friendBoxVersion = -1;
            _container.Add(_friendBox);
            _steamInviteBtn = MakeButton("INVITE FRIENDS (Steam)", SteamLobby.Invite);

            _hostBtn = MakeButton("Host (direct IP)", () => StartNet(CoopMode.Host));
            _joinBtn = MakeButton("Join (direct IP)", () => StartNet(CoopMode.Guest));
            _steamHostBtn = MakeButton("Host (Steam)", () => StartNetSteam(CoopMode.Host));
            _steamJoinBtn = MakeButton("Join (Steam)", () => StartNetSteam(CoopMode.Guest));
            _discBtn = MakeButton("Disconnect", CoopNet.Stop);
            _letMeClickBtn = MakeButton(LetMeClickText(), StrategyMirror.ToggleFreeClicks);
            _letMeClickBtn.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            // HOST: the waits for players to load have no clock (user, 2026-10-30)
            // -- a person ends one that will never finish. Shown only while waiting.
            _stopWaitingBtn = MakeButton("STOP WAITING FOR PLAYERS", SessionSync.StopWaiting);
            _stopWaitingBtn.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            _sendSaveBtn = MakeButton("Sync partner to my game", SessionSync.StartSync);
            // BattleSave: resume the battle saved with SAVE BATTLE STATE, from the
            // strategy layer or from inside a battle. Host (or solo) only.
            _resumeBattleBtn = MakeButton("RESUME SAVED BATTLE", () =>
            {
                Plugin.Logger.LogInfo("[BATTLESAVE] RESUME SAVED BATTLE pressed");
                if (!BattleSave.Resume(out string why)) ShowDesyncWarning("Cannot resume: " + why);
            });
            _buttonRow.Add(_hostBtn);
            _buttonRow.Add(_joinBtn);
            _steamRow.Add(_steamHostBtn);
            _steamRow.Add(_steamJoinBtn);

            // DISPLAY NAME (user 2026-10-02): under Join; shown to every player
            // on unit tags, cursors, the player list and the turn banner.
            _nameField = new TextField("Your name");
            _nameField.value = Plugin.PlayerName.Value ?? "";
            _nameField.maxLength = Peers.MaxNameLength;
            StyleField(_nameField);
            _container.Add(_nameField);
            _nameBtn = MakeButton("Set name", () =>
            {
                Peers.SetLocalName(_nameField.value);
                _nameField.value = Plugin.PlayerName.Value ?? "";
                CoopNet.Status = string.IsNullOrEmpty(Plugin.PlayerName.Value)
                    ? "Name cleared -- you show as your player number"
                    : $"You are now '{Plugin.PlayerName.Value}'";
            });
            _container.Add(_nameBtn);

            // BATTLE section -- only shown in a battle, and only the buttons the
            // local role can actually use. Two similarly-worded "sync" buttons
            // were genuinely confusing: these now read as what they DO, one
            // fixing drift and the others being an explicit save/restore.
            // EVERYTHING IN HERE IS A DEBUGGING TOOL, and it is labelled as one.
            //
            // These are recovery levers for a session that has already gone
            // wrong: force a resync, snapshot the board, roll it back, hand the
            // turn over by hand. A player on a healthy session should never need
            // any of them, and testers repeatedly reported them as "not working"
            // partly because it was never clear what they were FOR or when they
            // would legitimately do nothing. Grouping and naming them is half the
            // fix; the other half is that every one of them now logs its press,
            // so "I clicked it and nothing happened" becomes checkable rather
            // than a matter of opinion.
            _battleSection = new VisualElement();
            var battleHeader = new Label("DEBUGGING  (battle)");
            StyleLabel(battleHeader);
            battleHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            battleHeader.style.marginTop = new StyleLength(6f);
            _battleSection.Add(battleHeader);

            var battleNote = new Label("Recovery tools. Not needed in normal play.");
            StyleLabel(battleNote);
            battleNote.style.fontSize = new StyleLength(10f);
            battleNote.style.opacity = 0.7f;
            _battleSection.Add(battleNote);

            // Explicit "I'm done" -- beats waiting for the activation-finished
            // event to be inferred, and removes any reason to hurry a player along.
            _passTurnBtn = MakeButton("PASS TURN TO PARTNER", () =>
            {
                Plugin.Logger.LogInfo($"[TURN] PASS TURN pressed ({CoopNet.Mode}); "
                    + $"active={TurnControl.Active} myTurn={TurnControl.IsMyTurn}");
                TurnControl.RequestPassTurn();
            });
            _battleSection.Add(_passTurnBtn);

            _forceBtn = MakeButton("FIX DESYNC", () =>
            {
                // SAY SOMETHING, ALWAYS. Testers reported this button "does not
                // appear to do anything" -- it was in fact working every time
                // (the host logged three forced pushes in the Zeta run), but it
                // pushes UNIT STATE, and their actual fault was a diverged MAP.
                // Repositioning units onto a board that is itself different
                // cannot help, so the visible result was nothing changing. An
                // action with no feedback is indistinguishable from a dead
                // button; this one now reports what it did and, when the map is
                // known to differ, says plainly why it will not be enough.
                Plugin.Logger.LogInfo("[DESYNC] FIX DESYNC pressed by "
                    + (CoopNet.Mode == CoopMode.Host ? "host" : "guest"));
                if (TerrainCheck.Mismatched)
                {
                    Plugin.Logger.LogWarning("[DESYNC] ...but the MAPS DIFFER. Forcing unit positions cannot fix terrain.");
                    CoopRunner.ShowDesyncWarning(
                        "The two maps are different -- forcing positions cannot fix that.");
                    return;
                }
                // Rebuild the guest's battle from the host's (GuestReload). The
                // unit-state push could not repair engine bookkeeping it cannot
                // reach, which is why it "never fixed anything".
                GuestReload.Start(CoopNet.Mode == CoopMode.Host ? "host pressed FIX DESYNC" : "guest pressed FIX DESYNC");
            });
            _battleSection.Add(_forceBtn);

            _saveBattleBtn = MakeButton("SAVE BATTLE STATE", () =>
            {
                Plugin.Logger.LogInfo($"[DESYNC] SAVE BATTLE STATE pressed ({CoopNet.Mode})");
                DesyncDetector.SaveBattleState();
                // ...and a resumable save (BattleSave), which survives quitting.
                if (!BattleSave.Save(out string why)) ShowDesyncWarning("Battle not saved for resuming: " + why);
            });
            _battleSection.Add(_saveBattleBtn);

            _checkpointBtn = MakeButton("RESTORE BATTLE STATE", () =>
            {
                Plugin.Logger.LogInfo($"[DESYNC] RESTORE BATTLE STATE pressed ({CoopNet.Mode})");
                DesyncDetector.ForceGuestToHostState(true);
            });
            _battleSection.Add(_checkpointBtn);

            // SYNCED DEBUG (DebugSync.cs). Dev setting, both machines; every button
            // runs the same engine call on host and guest, in the host's order.
            if (DebugSync.LocalOn)
            {
                var dbgHeader = new Label("SYNCED DEBUG (both machines)");
                StyleLabel(dbgHeader);
                dbgHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
                dbgHeader.style.marginTop = new StyleLength(6f);
                _battleSection.Add(dbgHeader);
                _dbgPickBtn = MakeButton("Spawn: " + DebugSync.Picked, () =>
                {
                    DebugSync.Cycle(+1);
                    _dbgPickBtn.text = "Spawn: " + DebugSync.Picked;
                });
                _battleSection.Add(_dbgPickBtn);
                _battleSection.Add(MakeButton("Reload template list", () =>
                {
                    DebugSync.RefreshTemplates();
                    _dbgPickBtn.text = "Spawn: " + DebugSync.Picked;
                }));
                _battleSection.Add(MakeButton("SPAWN at cursor", DebugSync.SpawnAtCursor));
                _battleSection.Add(MakeButton("KILL unit at cursor", DebugSync.KillAtCursor));
                _battleSection.Add(MakeButton("Refill ship support", DebugSync.RefillSupport));
                _battleSection.Add(MakeButton("Grant ALL ship support", DebugSync.GrantAllSupport));
                _battleSection.Add(MakeButton("Grant drone skills to unit at cursor", DebugSync.GrantDronesAtCursor));
                _battleSection.Add(MakeButton("Unlock game cheat menu (F2, this machine only)", DebugSync.UnlockGameCheats));
            }

            // The banner TOGGLES are not in here -- they sit with the green one
            // under BANNERS. Turning a banner off is an ordinary preference and
            // players asked for it, so it must not require opening a section
            // labelled "can destabilise a running session". What belongs in here
            // is only the things that ACT on a live battle.

            _battleSection.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            // NOTE: added to the DEBUG SETTINGS body further down, not here.

            // Cursor/line options only matter in battle -- shown there only
            // (they cluttered the menu screen).
            _cursorSection = new VisualElement();
            var cursorHeader = new Label("CURSOR / MOVE LINE");
            StyleLabel(cursorHeader);
            cursorHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            cursorHeader.style.marginTop = new StyleLength(6f);
            _cursorSection.Add(cursorHeader);
            _cursorSection.Add(MakeToggle("Send my cursor: ", Plugin.ShareCursor));
            _cursorSection.Add(MakeToggle("Send my move line: ", Plugin.ShareLine));
            _cursorSection.Add(MakeToggle("Show partner cursor: ", Plugin.ShowPartnerCursor));
            _cursorSection.Add(MakeToggle("Show partner line: ", Plugin.ShowPartnerLine));
            // Which player commands which squad is otherwise invisible once the
            // planning phase is over, so this is on by default -- but it is a lot
            // of on-screen text, so it can be switched off for a clean board.
            _cursorSection.Add(MakeToggle("Show HOST/GUEST labels: ", Plugin.ShowOwnerLabels));
            _cursorSection.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            _container.Add(_cursorSection);

            // AUTO COMBAT. Each player chooses independently -- it only ever
            // drives your own squads, so one of you can automate while the other
            // plays by hand.
            var autoHeader = new Label("COMBAT");
            StyleLabel(autoHeader);
            autoHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            autoHeader.style.marginTop = new StyleLength(6f);
            _container.Add(autoHeader);
            // With the Auto Combat mod installed, ITS brain drives and ITS setting is
            // the switch -- shown here, since that mod builds no panel of its own
            // inside co-op (one panel, not two overlapping ones). F10 flips the same
            // setting, so UpdateUiState keeps this label in step.
            // One button either way, labelled by AutoCombatOn(), flipping whichever
            // switch is in charge (the Auto Combat mod's when bridged, ours if not).
            // Without the Auto Combat mod the control is greyed out and says why --
            // co-op no longer ships a brain of its own (user, 2026-09-28).
            _autoToggle = MakeButton(AutoToggleText(), () =>
            {
                if (!AutoCombatBridge.Delegated) return;
                AutoCombatBridge.TheirEnabled = !AutoCombatBridge.TheirEnabled;
                _autoToggle.text = AutoToggleText();
            });
            _autoToggle.SetEnabled(AutoCombatBridge.Delegated);
            _container.Add(_autoToggle);
            var autoHint = AutoCombatBridge.Delegated
                ? new Label("From the Auto Combat mod (F10 also toggles it). Only ever\ndrives your own squads. Click or press a key to take over.")
                : AutoCombatBridge.ModInstalled
                    ? new Label("MenaceAutoCombat is installed but could not connect to co-op --\nupdate both mods to their latest versions.")
                    : new Label("Auto combat is a separate mod. Install MenaceAutoCombat\n(both players) to let it fight your squads in co-op.");
            StyleLabel(autoHint);
            autoHint.style.fontSize = new StyleLength(10f);
            autoHint.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            _container.Add(autoHint);

            // A RUNNING COMMENTARY ON WHAT AUTO-COMBAT IS DOING.
            //
            // Asked for on the standalone mod in these words: "a section under
            // the panel that explains the AI's current thinking, like Angel is
            // moving... angel is attacking... angel is thinking."
            //
            // It earns its place twice over in co-op. Watching your own squads
            // move themselves is one thing; watching your PARTNER's squads move
            // with no explanation, from a seat where you cannot see their panel
            // or their log, is the situation this line exists for. It is also
            // the difference between "auto-combat is stuck" and "auto-combat is
            // waiting for the other player", which look identical on the board.
            _thinkingLabel = new Label("Idle.");
            StyleLabel(_thinkingLabel);
            _thinkingLabel.style.fontSize = new StyleLength(11f);
            _thinkingLabel.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            _thinkingLabel.style.marginTop = new StyleLength(3f);
            _thinkingLabel.style.color = new StyleColor(new Color(0.62f, 0.85f, 0.62f, 1f));
            _container.Add(_thinkingLabel);

            // On-screen notifications. Some players find the orange banner
            // firing constantly during a battle distracting -- turning it off
            // hides the notification only; corrections still happen and are
            // still written to the log.
            var bannerHeader = new Label("ON-SCREEN BANNERS");
            StyleLabel(bannerHeader);
            bannerHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            bannerHeader.style.marginTop = new StyleLength(6f);
            _container.Add(bannerHeader);
            // Only the green turn banner belongs in normal UI options. The other
            // All three together: a player who finds a banner distracting should
            // be able to switch it off from the obvious place. These default OFF
            // for the red and orange ones (see Plugin.Load) -- that is about what
            // a fresh install SHOWS, not about hiding the control.
            _container.Add(MakeToggle("Green turn banner: ", Plugin.ShowPhaseBanner));
            _container.Add(MakeToggle("Orange warning banner: ", Plugin.ShowWarningBanner));
            _container.Add(MakeToggle("Red alert banner: ", Plugin.ShowAlertBanner));

            // DEBUG SETTINGS -- LAST, AND FOLDED AWAY.
            //
            // Everything in here can destabilise a live session: forcing a board
            // push, rolling the battle back to a checkpoint, handing the turn
            // over out of sequence. That is fine for the person debugging the mod
            // and wrong to put in front of a player who will reasonably assume a
            // visible button is a supported feature. Collapsed by default, opened
            // deliberately, and placed at the bottom so it is never the first
            // thing anyone reaches for.
            // Declared first so the click handler can retitle the button it
            // belongs to -- interop does not surface Button.clicked, so the
            // action has to be supplied at construction (see MakeButton).
            Button debugToggleBtn = null;
            _debugBody = new VisualElement();
            _debugBody.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            _debugBody.Add(_battleSection);          // moved out of the main flow

            var debugNote = new Label("For mod debugging. These can destabilise a running session.");
            StyleLabel(debugNote);
            debugNote.style.fontSize = new StyleLength(10f);
            debugNote.style.opacity = 0.7f;
            debugNote.style.whiteSpace = WhiteSpace.Normal;
            _debugBody.Insert(0, debugNote);

            // Shown when the battle controls are hidden, so opening the section
            // outside a battle explains itself instead of looking broken --
            // reported as "you can't see any of the debug settings in the
            // strategy layer".
            _debugEmptyNote = new Label("Battle tools are unavailable in the strategy layer.");
            StyleLabel(_debugEmptyNote);
            _debugEmptyNote.style.fontSize = new StyleLength(11f);
            _debugEmptyNote.style.whiteSpace = WhiteSpace.Normal;
            _debugBody.Add(_debugEmptyNote);

            // Reloads the partner's whole session from a save. A recovery action,
            // not something to keep one stray click away during normal play.
            _debugBody.Add(_sendSaveBtn);
            _debugBody.Add(_resumeBattleBtn);
#if COOP_DEVTOOLS
            // Development builds only: item spawner (DevItemMenu.cs). Compiled out of releases.
            DevItemMenu.Build(_debugBody, MakeButton, StyleLabel, StyleField);
#endif

            debugToggleBtn = MakeButton("> DEBUG SETTINGS", () =>
            {
                _debugOpen = !_debugOpen;
                _debugBody.style.display = new StyleEnum<DisplayStyle>(
                    _debugOpen ? DisplayStyle.Flex : DisplayStyle.None);
                if (debugToggleBtn != null)
                    debugToggleBtn.text = (_debugOpen ? "v" : ">") + " DEBUG SETTINGS";
                Plugin.Logger.LogInfo($"[UI] Debug settings {(_debugOpen ? "opened" : "closed")}");
            });

            _container.Add(debugToggleBtn);
            _container.Add(_debugBody);

            var hideBtn = MakeButton("Hide", () =>
            {
                _container.RemoveFromHierarchy();
                root.Add(_collapsed);
            });
            _container.Add(hideBtn);

            _collapsed = new VisualElement();
            _collapsed.style.position = new StyleEnum<Position>(Position.Absolute);
            _collapsed.style.top = new StyleLength(12f);
            _collapsed.style.right = new StyleLength(12f);
            _collapsed.style.width = new StyleLength(110f);
            var showBtn = MakeButton("Co-op", () =>
            {
                _collapsed.RemoveFromHierarchy();
                root.Add(_container);
            });
            _collapsed.Add(showBtn);

            // Start collapsed: the title-screen CO-OP menu entry (or the small
            // pill) opens the full panel on demand.
            _container.RemoveFromHierarchy();
            root.Add(_collapsed);

            MakePanelScrollable();

            _lastOff = true;
            Plugin.Logger.LogInfo($"[UI] Co-op panel created (UI Toolkit, PanelSettings '{settings.name}')");
        }
        catch (Exception e)
        {
            _buildFailed = true;
            Plugin.Logger.LogError($"[UI] Panel build failed: {e}");
        }
    }

    // Palette tuned to the game's look: dark surfaces, bone text, thin borders.
    private static readonly Color PanelBg = new(0.04f, 0.05f, 0.07f, 0.92f);
    private static readonly Color FieldBg = new(0.11f, 0.12f, 0.15f, 1f);
    private static readonly Color ButtonBg = new(0.15f, 0.16f, 0.19f, 1f);
    private static readonly Color BoneText = new(0.85f, 0.82f, 0.74f, 1f);
    private static readonly Color BorderCol = new(0.42f, 0.40f, 0.34f, 1f);

    private static void SetBorder(VisualElement el, float width, Color color)
    {
        el.style.borderTopWidth = new StyleFloat(width);
        el.style.borderBottomWidth = new StyleFloat(width);
        el.style.borderLeftWidth = new StyleFloat(width);
        el.style.borderRightWidth = new StyleFloat(width);
        el.style.borderTopColor = new StyleColor(color);
        el.style.borderBottomColor = new StyleColor(color);
        el.style.borderLeftColor = new StyleColor(color);
        el.style.borderRightColor = new StyleColor(color);
    }

    // The panel had no height limit, so as sections were added it simply grew
    // past the bottom of the screen and those controls became unreachable. Cap
    // it to the window and move everything below the title bar into a
    // ScrollView, so the content scrolls instead of overflowing. Done as a
    // re-parent after the panel is built, which keeps every existing
    // _container.Add(...) call working unchanged.
    private static void MakePanelScrollable()
    {
        try
        {
            if (_container == null) return;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = new StyleFloat(1f);
            SlimScrollbar(scroll);
            _panelScroll = scroll;

            // Everything except the drag handle moves inside, so the title stays
            // put while the rest scrolls under it.
            var movable = new System.Collections.Generic.List<VisualElement>();
            for (int i = 0; i < _container.childCount; i++)
            {
                var child = _container.ElementAt(i);
                if (child == null || child == _titleBar) continue;
                movable.Add(child);
            }
            foreach (var child in movable)
            {
                child.RemoveFromHierarchy();
                scroll.Add(child);
            }
            _container.Add(scroll);

            ApplyPanelHeightLimit();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Could not make the panel scrollable (it stays fixed-height): {e.Message}");
        }
    }

    // The default UI Toolkit scroller is built for mouse-first desktop UI and is
    // far too heavy next to this panel: a wide track, arrow buttons at both ends
    // and a chunky dragger. Slim it to a thin unobtrusive bar in the panel's own
    // colours.
    private static void SlimScrollbar(ScrollView scroll)
    {
        const float BarWidth = 6f;
        try
        {
            var scroller = scroll.verticalScroller;
            if (scroller == null) return;
            scroller.style.width = new StyleLength(BarWidth);
            scroller.style.marginLeft = new StyleLength(4f);

            // The little arrow buttons at each end are pure clutter at this size.
            if (scroller.lowButton != null) scroller.lowButton.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            if (scroller.highButton != null) scroller.highButton.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);

            var slider = scroller.slider;
            if (slider != null)
            {
                slider.style.width = new StyleLength(BarWidth);
                slider.style.marginLeft = new StyleLength(0f);
                slider.style.marginRight = new StyleLength(0f);

                var tracker = slider.Q("unity-tracker", (string)null);
                if (tracker != null)
                {
                    tracker.style.backgroundColor = new StyleColor(new Color(1f, 1f, 1f, 0.06f));
                    SetBorder(tracker, 0f, Color.clear);
                    tracker.style.width = new StyleLength(BarWidth);
                    tracker.style.marginLeft = new StyleLength(0f);
                }

                var dragger = slider.Q("unity-dragger", (string)null);
                if (dragger != null)
                {
                    dragger.style.backgroundColor = new StyleColor(new Color(1f, 1f, 1f, 0.28f));
                    SetBorder(dragger, 0f, Color.clear);
                    dragger.style.width = new StyleLength(BarWidth);
                    dragger.style.marginLeft = new StyleLength(0f);
                    dragger.style.borderTopLeftRadius = new StyleLength(3f);
                    dragger.style.borderTopRightRadius = new StyleLength(3f);
                    dragger.style.borderBottomLeftRadius = new StyleLength(3f);
                    dragger.style.borderBottomRightRadius = new StyleLength(3f);
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[UI] Could not slim the scrollbar: {e.Message}"); }
    }

    // Keep the panel inside the window whatever the resolution, leaving room for
    // the top offset and a small margin at the bottom.
    private static void ApplyPanelHeightLimit()
    {
        try
        {
            if (_container == null) return;
            float screenH = _root != null && _root.resolvedStyle.height > 0f
                ? _root.resolvedStyle.height
                : Screen.height;
            if (screenH <= 0f) return;
            _container.style.maxHeight = new StyleLength(Mathf.Max(200f, screenH - 40f));
        }
        catch { }
    }

    private static void TryStylePanel(VisualElement panel)
    {
        try
        {
            // top-right corner, clear of the title menu and logo
            panel.style.position = new StyleEnum<Position>(Position.Absolute);
            panel.style.top = new StyleLength(12f);
            panel.style.right = new StyleLength(12f);
            panel.style.width = new StyleLength(330f);
            panel.style.backgroundColor = new StyleColor(PanelBg);
            SetBorder(panel, 1f, BorderCol);
            panel.style.paddingLeft = new StyleLength(12f);
            panel.style.paddingRight = new StyleLength(12f);
            panel.style.paddingTop = new StyleLength(10f);
            panel.style.paddingBottom = new StyleLength(10f);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Styling failed (panel will be unstyled but functional): {e.Message}");
        }
    }

    private static void StyleLabel(Label label)
    {
        label.style.color = new StyleColor(BoneText);
        label.style.marginBottom = new StyleLength(4f);
    }

    private static void StyleButton(Button btn)
    {
        btn.style.backgroundColor = new StyleColor(ButtonBg);
        btn.style.color = new StyleColor(BoneText);
        SetBorder(btn, 1f, BorderCol);
        btn.style.marginTop = new StyleLength(6f);
        btn.style.height = new StyleLength(30f);
    }

    private static void StyleField(TextField field)
    {
        field.style.marginTop = new StyleLength(4f);
        field.style.color = new StyleColor(BoneText); // inherited by inner text
        // TextField = [Label, TextInput]; style both children by walk
        for (int i = 0; i < field.childCount; i++)
        {
            var child = field[i];
            var asLabel = child.TryCast<Label>();
            if (asLabel != null)
            {
                asLabel.style.color = new StyleColor(BoneText);
                asLabel.style.minWidth = new StyleLength(64f);
                continue;
            }
            child.style.backgroundColor = new StyleColor(FieldBg);
            child.style.color = new StyleColor(BoneText);
            SetBorder(child, 1f, BorderCol);
        }
    }

    // Player roster: one row per player slot with a status box (green =
    // active, red = inactive). Two fixed slots today; built as rows so more
    // players are an easy extension later.
    private static readonly Color ActiveGreen = new(0.25f, 0.68f, 0.35f, 1f);
    private static readonly Color InactiveRed = new(0.69f, 0.23f, 0.21f, 1f);
    // v5: one row per player (seat order), name in that player's colour, a
    // grey "spectator" mark for a player with no squad in this battle.
    private static VisualElement _playersBox;
    private static string _lastPlayerState = "";

    private static (VisualElement box, Label label) MakePlayerRow(string name)
    {
        var row = new VisualElement();
        row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
        row.style.alignItems = new StyleEnum<Align>(Align.Center);
        row.style.marginBottom = new StyleLength(3f);

        var box = new VisualElement();
        box.style.width = new StyleLength(12f);
        box.style.height = new StyleLength(12f);
        box.style.marginRight = new StyleLength(8f);
        box.style.backgroundColor = new StyleColor(InactiveRed);
        SetBorder(box, 1f, BorderCol);

        var label = new Label(name);
        label.style.color = new StyleColor(BoneText);

        row.Add(box);
        row.Add(label);
        _playersBox.Add(row);
        return (box, label);
    }

    private static void UpdatePlayerRoster()
    {
        if (_playersBox == null) return;
        bool connected = CoopNet.Connected;
        bool inBattle = Ownership.Active;
        var seats = Peers.SessionSeats();
        // Offline / waiting: still show the two familiar rows.
        if (!connected) seats = new List<int> { Peers.HostSeat, 1 };

        var key = new System.Text.StringBuilder();
        key.Append((int)CoopNet.Mode).Append('|').Append(connected).Append('|').Append(Peers.RosterVersion).Append('|').Append(inBattle);
        foreach (var s in seats) key.Append('|').Append(s).Append(inBattle ? Ownership.CountOf(s) : -1);
        string k = key.ToString();
        if (k == _lastPlayerState) return;
        _lastPlayerState = k;

        _playersBox.Clear();
        foreach (var s in seats)
        {
            bool me = connected ? s == Peers.LocalSeat : (CoopNet.Mode == CoopMode.Guest ? s == 1 : s == Peers.HostSeat);
            bool up = me || (connected && Peers.IsConnected(s));
            string role = s == Peers.HostSeat ? "Host" : (seats.Count <= 2 ? "Guest" : $"Player {s + 1}");
            string name = connected ? Peers.NameOf(s) : role;
            string text = (name == role || string.IsNullOrWhiteSpace(name) ? role : $"{role}: {name}") + (me ? " (you)" : "");
            bool spectator = inBattle && connected && Ownership.CountOf(s) == 0;
            if (spectator) text += "  -- SPECTATOR";
            var (box, label) = MakePlayerRow(text);
            box.style.backgroundColor = new StyleColor(up ? ActiveGreen : InactiveRed);
            label.style.color = new StyleColor(spectator ? SeatColors.Spectator : (connected ? SeatColors.Of(s) : BoneText));
        }
    }

    // Squad assignment (planning phase): the host decides which deployed
    // squads each player commands; the choice mirrors live to every client's
    // panel read-only. Feeds Ownership.PrepSeats for battle start.
    //
    // v5 (user 2026-10-02): the per-squad HOST/GUEST toggle is a PLAYER PICKER --
    // each click hands the squad to the next player in seat order, shown in that
    // player's colour. A player left with no squad is listed as SPECTATOR, grey.
    private static VisualElement _assignSection;
    private static ScrollView _assignRows;    // squad rows, own scrollbar (8+ players)
    private static ScrollView _panelScroll;   // the panel's main scroll area
    private static readonly List<Button> _assignButtons = new();
    private static int[] _slotSeats = new int[0];
    private static int _assignCount = -1;
    private static string _assignSeatsKey = "";
    private static float _assignRefreshAt;
    private static Label _spectatorLabel;

    private static void UpdateAssignments()
    {
        PrepPortraits.Tick();
        float now = Time.realtimeSinceStartup;
        if (now < _assignRefreshAt) return;
        _assignRefreshAt = now + 1f;

        bool hostSide = CoopNet.Mode == CoopMode.Host && SessionSync.HostPrepActive;
        bool guestSide = CoopNet.Mode == CoopMode.Guest && SessionSync.ViewerPrepActive;
        bool active = CoopNet.Connected && (hostSide || guestSide);

        if (!active)
        {
            if (_assignSection != null && _assignSection.parent != null)
            {
                _assignSection.RemoveFromHierarchy();
                _assignCount = -1;
            }
            return;
        }

        var entities = Menace.States.StrategyState.Get()?.BattlePlan?.GetEntitiesToDeploy()
            ?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
        if (entities == null) return;

        if (_assignSection == null)
        {
            _assignSection = new VisualElement();
            _assignSection.style.flexShrink = new StyleFloat(0f);
            var header = new Label("SQUAD ASSIGNMENT" );
            StyleLabel(header);
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginTop = new StyleLength(6f);
            _assignSection.Add(header);
            // A long roster (v5: up to 8 players) scrolls inside its own box, capped
            // at 6 rows, instead of pushing the rest of the panel away.
            _assignRows = new ScrollView(ScrollViewMode.Vertical);
            _assignRows.style.maxHeight = new StyleLength(6 * 30f);
            _assignRows.style.flexShrink = new StyleFloat(0f);
            SlimScrollbar(_assignRows);
            _assignSection.Add(_assignRows);
        }
        // INSIDE the panel's scroll area: added straight to the panel frame (after
        // MakePanelScrollable ran) it was squeezed to nothing and the rows overlapped.
        if (_assignSection.parent == null) (_panelScroll ?? (VisualElement)_container).Add(_assignSection);

        // The host re-defaults when the roster OR the set of players changes (a
        // player joining during planning must get a squad without a click).
        string seatsKey = string.Join(",", Peers.SessionSeats());
        if (entities.Count != _assignCount || (hostSide && seatsKey != _assignSeatsKey))
        {
            _assignCount = entities.Count;
            _assignSeatsKey = seatsKey;
            if (hostSide || _slotSeats.Length != entities.Count)
                _slotSeats = Ownership.DefaultSeats(entities.Count);
            if (hostSide) PushAssignment();
            RebuildAssignRows(entities, hostSide);
        }
    }

    private static void RebuildAssignRows(Il2CppSystem.Collections.Generic.List<DeployedEntity> entities, bool hostSide)
    {
        // keep the header (0) and the row box (1); drop old rows and the spectator line
        _assignRows.Clear();
        while (_assignSection.childCount > 2) _assignSection[2].RemoveFromHierarchy();
        _assignButtons.Clear();

        for (int i = 0; i < entities.Count; i++)
        {
            int slot = i;
            string name;
            try { name = (string)entities[i].GetUnitLeader()?.GetNickname() ?? $"Squad {i + 1}"; }
            catch { name = $"Squad {i + 1}"; }
            if (string.IsNullOrWhiteSpace(name)) name = $"Squad {i + 1}";

            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.alignItems = new StyleEnum<Align>(Align.Center);
            // Never squeezed: with a long panel the flex layout shrank every row to
            // ~0 height and the names drew on top of each other (user, 2026-10-03).
            row.style.flexShrink = new StyleFloat(0f);
            row.style.minHeight = new StyleLength(26f);
            row.style.marginBottom = new StyleLength(4f);

            var lbl = new Label(name);
            lbl.style.color = new StyleColor(BoneText);
            lbl.style.flexGrow = new StyleFloat(1f);
            lbl.style.flexShrink = new StyleFloat(0f);
            row.Add(lbl);

            var btn = new Button();
            StyleButton(btn);
            btn.style.width = new StyleLength(80f);
            btn.style.height = new StyleLength(22f);
            btn.style.flexShrink = new StyleFloat(0f);
            btn.style.marginTop = new StyleLength(0f);
            if (hostSide)
            {
                var clickable = btn.clickable;
                if (clickable != null)
                    clickable.clicked = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                        (Action)(() => ToggleAssignment(slot)));
            }
            else
            {
                btn.SetEnabled(false);
            }
            row.Add(btn);
            _assignRows.Add(row);
            _assignButtons.Add(btn);
        }

        _spectatorLabel = new Label("");
        _spectatorLabel.style.color = new StyleColor(SeatColors.Spectator);
        _spectatorLabel.style.fontSize = new StyleLength(11f);
        _assignSection.Add(_spectatorLabel);
        RefreshAssignVisuals();
    }

    // Host: hand the squad to the next player in seat order.
    private static void ToggleAssignment(int slot)
    {
        if (slot < 0 || slot >= _slotSeats.Length) return;
        var order = Peers.SessionSeats();
        int at = order.IndexOf(_slotSeats[slot]);
        _slotSeats[slot] = order[(at + 1) % order.Count];
        RefreshAssignVisuals();
        PushAssignment();
    }

    private static void PushAssignment()
    {
        Ownership.PrepSeats = (int[])_slotSeats.Clone();
        var guest = new List<int>();
        for (int i = 0; i < _slotSeats.Length; i++) if (_slotSeats[i] != Peers.HostSeat) guest.Add(i);
        Ownership.PrepGuestSlots = new HashSet<int>(guest);
        // Data stays the guest-slot list (older clients); Seats is the per-slot owner.
        CoopNet.Send(new CoopMsg { T = "assign", Data = string.Join(",", guest), Seats = string.Join(",", _slotSeats) });
    }

    private static void RefreshAssignVisuals()
    {
        PrepPortraits.RepaintNow();
        for (int i = 0; i < _assignButtons.Count; i++)
        {
            int seat = i < _slotSeats.Length ? _slotSeats[i] : Peers.HostSeat;
            _assignButtons[i].text = seat == Peers.LocalSeat ? $"{Peers.Label(seat)} (you)" : Peers.Label(seat);
            _assignButtons[i].style.color = new StyleColor(SeatColors.Of(seat));
        }
        if (_spectatorLabel != null)
        {
            var idle = new List<string>();
            foreach (var seat in Peers.SessionSeats())
                if (Array.IndexOf(_slotSeats, seat) < 0) idle.Add(Peers.NameOf(seat));
            _spectatorLabel.text = idle.Count == 0 ? "" : "SPECTATOR (no squad): " + string.Join(", ", idle);
        }
    }

    // The seat a unit is assigned to in the prep picker, or -1 (not deployed /
    // no picker). Same on every machine: _slotSeats is the host's, mirrored by
    // "assign". Decides who may edit that unit's loadout (EquipmentSync).
    internal static int PrepSeatOfLeader(IntPtr leader)
    {
        if (leader == IntPtr.Zero) return -1;
        try
        {
            var entities = Menace.States.StrategyState.Get()?.BattlePlan?.GetEntitiesToDeploy()
                ?.TryCast<Il2CppSystem.Collections.Generic.List<DeployedEntity>>();
            if (entities == null) return -1;
            for (int i = 0; i < entities.Count && i < _slotSeats.Length; i++)
            {
                var l = entities[i]?.GetUnitLeader();
                if (l != null && l.Pointer == leader) return _slotSeats[i];
            }
        }
        catch { }
        return -1;
    }

    // Client side: the host's assignment arrives over the wire.
    internal static void OnRemoteAssignment(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (!string.IsNullOrEmpty(m.Seats))
        {
            var list = new List<int>();
            foreach (var x in m.Seats.Split(',', StringSplitOptions.RemoveEmptyEntries))
                list.Add(int.TryParse(x, out int v) ? v : Peers.HostSeat);
            _slotSeats = list.ToArray();
        }
        else
        {
            // Older host: the listed slots are the (one) guest's.
            var slots = new HashSet<int>();
            if (!string.IsNullOrEmpty(m.Data))
                foreach (var x in m.Data.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(x, out int v)) slots.Add(v);
            int n = Math.Max(_slotSeats.Length, slots.Count == 0 ? 0 : System.Linq.Enumerable.Max(slots) + 1);
            _slotSeats = new int[n];
            for (int i = 0; i < n; i++) _slotSeats[i] = slots.Contains(i) ? Peers.LocalSeat : Peers.HostSeat;
        }
        RefreshAssignVisuals();
    }

    private static string LetMeClickText()
        => StrategyMirror.FreeClicks
            ? $"Hold my clicks again ({StrategyMirror.FreeClicksLeft}s)"
            : $"Let me click ({StrategyMirror.FreeClickSeconds:F0}s, view only)";

    private static Button MakeButton(string label, Action onClick)
    {
        var btn = new Button();
        btn.text = label;
        StyleButton(btn);
        // Interop doesn't surface Button.clicked as an event; set the
        // underlying Clickable delegate field directly.
        var clickable = btn.clickable;
        if (clickable != null)
            clickable.clicked = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(onClick);
        else
            Plugin.Logger.LogWarning($"[UI] Button '{label}' has no Clickable; it will not respond");
        return btn;
    }

    // Steam may not be initialized when the panel is first built; keep
    // retrying the ID lookup until it resolves. Once resolved the ID is
    // MASKED for screenshots and revealed only while the cursor hovers it.
    private static Label _steamIdLabel;
    private static bool _steamIdResolved;
    private static float _steamIdRetryAt;
    private static string _steamIdReal;

    private static void UpdateSteamIdLabel()
    {
        if (_steamIdLabel == null) return;

        if (!_steamIdResolved)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _steamIdRetryAt) { return; }
            _steamIdRetryAt = now + 5f;
            try
            {
                ulong id = Steamworks.SteamUser.GetSteamID().m_SteamID;
                if (id != 0)
                {
                    _steamIdReal = id.ToString();
                    _steamIdResolved = true;
                }
            }
            catch { }
            if (!_steamIdResolved)
            {
                _steamIdLabel.text = "Your Steam ID: (waiting for Steam...)";
                return;
            }
        }

        // resolved: show real ID only on hover, masked otherwise
        _steamIdLabel.text = IsHovering(_steamIdLabel)
            ? "Your Steam ID: " + _steamIdReal
            : "Your Steam ID: (hover to reveal)";
    }

    // Convert the current screen mouse position into panel space (same mapping
    // the shared cursor / hover test use). Returns false if unavailable.
    private static bool ScreenMouseToPanel(out Vector2 ps, out float w, out float h)
    {
        ps = default; w = 0f; h = 0f;
        if (_root == null) return false;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        w = _root.resolvedStyle.width;
        h = _root.resolvedStyle.height;
        if (w <= 0f || h <= 0f) return false;
        Vector2 mp = mouse.position.ReadValue();
        ps = new Vector2(Mathf.Clamp01(mp.x / Screen.width) * w, Mathf.Clamp01(1f - mp.y / Screen.height) * h);
        return true;
    }

    // Drag-to-move the panel by grabbing its title bar. Polled each frame so it
    // doesn't depend on UI Toolkit pointer callbacks (unreliable under IL2CPP).
    // Grabbing the title switches the panel from right-anchored to left/top so
    // it can be positioned anywhere -- e.g. off the game's F7 bug-report button.
    private static void UpdatePanelDrag()
    {
        try
        {
            if (_container == null || _container.parent == null || _titleBar == null)
            {
                _dragging = false;
                _prevMouseDown = false;
                return;
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;
            bool down = mouse.leftButton.isPressed;
            if (!ScreenMouseToPanel(out Vector2 ps, out float w, out float h)) { _prevMouseDown = down; return; }

            if (down && !_prevMouseDown && IsHovering(_titleBar))
            {
                _dragging = true;
                _dragMouseStart = ps;
                var b = _container.worldBound;     // current on-screen position (panel space)
                _dragStartLeft = b.xMin;
                _dragStartTop = b.yMin;
                // move from right-anchored to left/top so we can drag freely
                _container.style.right = new StyleLength(StyleKeyword.Auto);
                _container.style.left = new StyleLength(_dragStartLeft);
                _container.style.top = new StyleLength(_dragStartTop);
            }
            else if (_dragging && down)
            {
                Vector2 delta = ps - _dragMouseStart;
                float pw = _container.resolvedStyle.width;
                float phe = _container.resolvedStyle.height;
                if (pw <= 0f) pw = 330f;
                if (phe <= 0f) phe = 40f;
                // keep at least a sliver on-screen so it can't be lost
                float nl = Mathf.Clamp(_dragStartLeft + delta.x, 0f, Mathf.Max(0f, w - 40f));
                float nt = Mathf.Clamp(_dragStartTop + delta.y, 0f, Mathf.Max(0f, h - 20f));
                _container.style.left = new StyleLength(nl);
                _container.style.top = new StyleLength(nt);
            }
            else if (!down)
            {
                _dragging = false;
            }
            _prevMouseDown = down;
        }
        catch (Exception e)
        {
            _dragging = false;
            Plugin.Logger.LogWarning($"[UI] Panel drag error: {e.Message}");
        }
    }

    // Polled hover test: convert the screen mouse position into panel space
    // (same mapping the shared cursor uses) and check the element's bounds.
    private static bool IsHovering(VisualElement el)
    {
        try
        {
            if (_root == null || el == null || el.parent == null) return false;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return false;
            float w = _root.resolvedStyle.width;
            float h = _root.resolvedStyle.height;
            if (w <= 0f || h <= 0f) return false;
            Vector2 mp = mouse.position.ReadValue();
            float px = Mathf.Clamp01(mp.x / Screen.width) * w;
            float py = Mathf.Clamp01(1f - mp.y / Screen.height) * h;
            var b = el.worldBound;
            return px >= b.xMin && px <= b.xMax && py >= b.yMin && py <= b.yMax;
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateUiState()
    {
        if (_status == null) return;
        _status.text = CoopNet.Status;

        // Only meaningful while auto-combat is actually driving something; when
        // it is off the line would sit there reading "Idle." forever and imply
        // the feature is broken rather than switched off.
        if (_thinkingLabel != null)
        {
            bool delegated = AutoCombatBridge.Delegated;
            bool autoOn = AutoCombatOn();
            _thinkingLabel.style.display = new StyleEnum<DisplayStyle>(
                autoOn ? DisplayStyle.Flex : DisplayStyle.None);
            if (autoOn) _thinkingLabel.text = delegated ? AutoCombatBridge.TheirCommentary : Thinking.Current;
        }
        if (_autoToggle != null)
        {
            string t = AutoToggleText();
            if (_autoToggle.text != t) _autoToggle.text = t;
        }
        UpdatePlayerRoster();
        UpdateSteamIdLabel();
        UpdateFriendLobbies();

        // Cursor/line options and the battle controls are battle-only (menu
        // clutter otherwise).
        bool inBattle = Menace.Tactical.TacticalManager.Get() != null;
        if (_letMeClickBtn != null && Time.frameCount % 15 == 0)
        {
            bool offer = CoopNet.Mode == CoopMode.Guest && CoopNet.Connected && !inBattle;
            _letMeClickBtn.style.display = new StyleEnum<DisplayStyle>(offer ? DisplayStyle.Flex : DisplayStyle.None);
            string t = LetMeClickText();
            if (offer && _letMeClickBtn.text != t) _letMeClickBtn.text = t;
        }
        if (_stopWaitingBtn != null && Time.frameCount % 15 == 0)
        {
            bool offer = SessionSync.Waiting;
            _stopWaitingBtn.style.display = new StyleEnum<DisplayStyle>(offer ? DisplayStyle.Flex : DisplayStyle.None);
        }
        if (_resumeBattleBtn != null && Time.frameCount % 30 == 0)
        {
            bool offer = CoopNet.Mode != CoopMode.Guest && !BattleSave.Restoring && BattleSave.Exists;
            _resumeBattleBtn.style.display = new StyleEnum<DisplayStyle>(offer ? DisplayStyle.Flex : DisplayStyle.None);
            if (offer) _resumeBattleBtn.text = $"RESUME SAVED BATTLE ({BattleSave.Label})";
        }

        if (_battleSection != null)
        {
            bool showBattle = inBattle && CoopNet.Connected;
            if (showBattle != _battleSectionShown)
            {
                _battleSectionShown = showBattle;
                _battleSection.style.display = new StyleEnum<DisplayStyle>(showBattle ? DisplayStyle.Flex : DisplayStyle.None);
                if (_debugEmptyNote != null)
                    _debugEmptyNote.style.display = new StyleEnum<DisplayStyle>(showBattle ? DisplayStyle.None : DisplayStyle.Flex);
                if (_sendSaveBtn != null)
                    _sendSaveBtn.style.display = new StyleEnum<DisplayStyle>(
                        CoopNet.Mode == CoopMode.Host ? DisplayStyle.Flex : DisplayStyle.None);
            }
            if (showBattle)
            {
                bool host = CoopNet.Mode == CoopMode.Host;
                // The host pushes truth; the guest can only ask for it.
                _forceBtn.text = host ? "FIX DESYNC (push my board)" : "FIX DESYNC (ask host)";

                // Only offer the handover when it is actually ours to give, so the
                // button never promises something it cannot do.
                if (_passTurnBtn != null)
                {
                    bool mine = TurnControl.Enabled && TurnControl.Active && TurnControl.IsMyTurn;
                    _passTurnBtn.SetEnabled(mine);
                    _passTurnBtn.text = mine ? "PASS TURN TO PARTNER" : "PASS TURN (not your turn)";
                }
                // Saving and restoring the battle are the host's alone.
                var hostOnly = new StyleEnum<DisplayStyle>(host ? DisplayStyle.Flex : DisplayStyle.None);
                _saveBattleBtn.style.display = hostOnly;
                _checkpointBtn.style.display = hostOnly;
                _checkpointBtn.text = DesyncDetector.HasCheckpoint
                    ? $"RESTORE BATTLE STATE ({DesyncDetector.CheckpointLabel})"
                    : "RESTORE BATTLE STATE (nothing saved)";
            }
        }
        if (_cursorSection != null && inBattle != _cursorSectionShown)
        {
            _cursorSectionShown = inBattle;
            _cursorSection.style.display = new StyleEnum<DisplayStyle>(inBattle ? DisplayStyle.Flex : DisplayStyle.None);
        }

        bool off = CoopNet.Mode == CoopMode.Off;
        if (off == _lastOff) return;
        _lastOff = off;

        _buttonRow.Clear();
        _steamRow.Clear();
        if (off)
        {
            _buttonRow.Add(_hostBtn);
            _buttonRow.Add(_joinBtn);
            _steamRow.Add(_steamHostBtn);
            _steamRow.Add(_steamJoinBtn);
        }
        else
        {
            _buttonRow.Add(_discBtn);
            if (CoopNet.Mode == CoopMode.Host && CoopNet.Transport == TransportKind.Steam)
                _steamRow.Add(_steamInviteBtn);
            _buttonRow.Add(_letMeClickBtn);
            _buttonRow.Add(_stopWaitingBtn);
            // "Sync partner to my game" now lives in DEBUG SETTINGS: it reloads
            // the partner's entire session from a save, which is a recovery
            // action, not something to sit one stray click away during play.
        }
    }

    // Title-screen integration: insert a CO-OP entry into the game's own menu
    // list, styled and wired exactly like its siblings. The menu is found by
    // scanning UI documents for TitleMenuButton elements (no game callback
    // dependency), re-inserting whenever a rebuilt menu drops our entry.
    private const string TitleEntryName = "MenaceCoopMenuButton";
    private static VisualElement _titleEntry;
    private static float _menuScanAt;

    internal static void InsertTitleMenuEntry(TitleButtons buttons)
    {
        var anchor = buttons?.m_CreditsButton;
        if (anchor != null) InsertEntryNextTo(anchor);
    }

    private static void UpdateTitleMenu()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _menuScanAt) return;
        _menuScanAt = now + 2f;
        try
        {
            // The title menu document can survive (hidden) after entering the
            // game, so "entry still parented" is not enough -- branding must
            // also require that no campaign/battle is loaded.
            bool atTitle = Menace.States.StrategyState.Get() == null
                && Menace.Tactical.TacticalManager.Get() == null;
            if (_titleEntry != null && _titleEntry.parent != null)
            {
                // localization refreshes can overwrite the raw label; re-assert
                SetTitleEntryLabel(_titleEntry);
                UpdateTitleBranding(atTitle);
                return;
            }
            _titleEntry = null;
            UpdateTitleBranding(false);
            var docs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UIDocument>());
            if (docs == null) return;
            for (int i = 0; i < docs.Length; i++)
            {
                var doc = docs[i].TryCast<UIDocument>();
                var root = doc?.rootVisualElement;
                if (root == null || root == _root) continue;
                var anchor = FindTitleButton(root, 0);
                if (anchor != null)
                {
                    InsertEntryNextTo(anchor);
                    UpdateTitleBranding(atTitle);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Title menu scan failed: {e.Message}");
            _menuScanAt = now + 30f;
        }
    }

    private static TitleMenuButton FindTitleButton(VisualElement el, int depth)
    {
        if (el == null || depth > 14) return null;
        var asButton = el.TryCast<TitleMenuButton>();
        if (asButton != null) return asButton;
        for (int i = 0; i < el.childCount; i++)
        {
            var found = FindTitleButton(el[i], depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static void InsertEntryNextTo(TitleMenuButton anchor)
    {
        var parent = anchor?.parent;
        if (parent == null) return;

        int lastButtonIdx = -1;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent[i];
            if (child == null) continue;
            if (child.name == TitleEntryName) { child.RemoveFromHierarchy(); continue; }
            if (lastButtonIdx < 0 && child.TryCast<TitleMenuButton>() != null) lastButtonIdx = i;
        }

        var btn = new TitleMenuButton();
        btn.name = TitleEntryName;
        btn.SetOnLeftClickedAction(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<InteractiveElement>>(
            (Action<InteractiveElement>)(_ => ShowPanel())));
        // just above the last entry (conventionally Quit)
        parent.Insert(lastButtonIdx >= 0 ? lastButtonIdx : parent.childCount, btn);
        _titleEntry = btn;
        SetTitleEntryLabel(btn);
        Plugin.Logger.LogInfo("[UI] MENACE (co-op) entry added to title menu");
    }

    // ButtonText routes through the localization table (unknown keys render as
    // the UXML default "text"), so write the label element directly, raw.
    private static void SetTitleEntryLabel(VisualElement entry)
    {
        try
        {
            var label = entry.TryCast<TitleMenuButton>()?.Text;
            label?.SetText("MENACE (co-op)", false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Could not set menu entry label: {e.Message}");
        }
    }

    private static void ShowPanel()
    {
        if (_container == null || _root == null) return;
        _collapsed.RemoveFromHierarchy();
        if (_container.parent == null) _root.Add(_container);
    }

    // Title-screen branding: a red CO-OP tag under the MENACE logo, and the
    // Early Access welcome box hijacked with our own message. Re-asserted
    // every scan tick because localization refreshes restore the originals.
    private static Label _coopTag;

    private const string WelcomeHeader = "Welcome to MENACE Co-Op!";
    private const string WelcomeBody =
        "If you can read this, the co-op mod is alive and your dropship now seats " +
        "two. Half the squad answers to your buddy now - HQ has ruled that " +
        "friendly banter is NOT a court-martial offense. Casualties caused by " +
        "\"I thought YOU had that flank\" remain your own problem.";
    private const string WelcomeInstructions =
        "Quick start: click MENACE (co-op) in the menu. Steam: swap Steam IDs " +
        "once, then Host (Steam) / Join (Steam). Direct IP: host clicks Host, " +
        "guest enters the host's IP (port 27555). The host runs the campaign, " +
        "battles split the squads automatically - and hold your fire until it " +
        "says \"weapons free\"!";

    private static void UpdateTitleBranding(bool titleActive)
    {
        if (_root == null) return;
        if (!titleActive)
        {
            _coopTag?.RemoveFromHierarchy();
            return;
        }

        if (_coopTag == null)
        {
            _coopTag = new Label("CO-OP");
            _coopTag.pickingMode = PickingMode.Ignore;
            _coopTag.style.position = new StyleEnum<Position>(Position.Absolute);
            _coopTag.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _coopTag.style.unityFontStyleAndWeight = FontStyle.Bold;
            _coopTag.style.color = new StyleColor(new Color(0.88f, 0.10f, 0.12f, 1f));
            _coopTag.style.marginTop = new StyleLength(0f);
            _coopTag.style.marginBottom = new StyleLength(0f);
            _coopTag.style.paddingTop = new StyleLength(0f);
            _coopTag.style.paddingBottom = new StyleLength(0f);
        }
        PlaceCoopTag();

        try
        {
            var docs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UIDocument>());
            if (docs == null) return;
            for (int i = 0; i < docs.Length; i++)
            {
                var doc = docs[i].TryCast<UIDocument>();
                var root = doc?.rootVisualElement;
                if (root == null || root == _root) continue;
                ReplaceWelcomeLabels(root, 0);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[UI] Welcome box rebrand failed: {e.Message}");
        }
    }

    // The tag lives in the title screen's own document and is placed from the
    // live layout, never fixed pixels: v0.7.15 redesigned the title screen and
    // the old fixed spot landed on the CONTINUE button. User (2026-10-01): under
    // the MENACE logo, not in the menu. So: centred under the logo, below the
    // version line if there is one. The menu-column spot below is the fallback
    // for a title screen without the logo field.
    private static void PlaceCoopTag()
    {
        if (PlaceUnderLogo()) return;

        var entry = _titleEntry;
        var menu = entry?.parent;
        if (menu == null || entry.worldBound.width <= 0f)
        {
            _coopTag.RemoveFromHierarchy();
            return;
        }

        var docRoot = menu;
        while (docRoot.parent != null) docRoot = docRoot.parent;

        Rect col = entry.worldBound;
        VisualElement below = null, last = null;
        int idx = menu.IndexOf(entry);
        for (int i = idx + 1; i < menu.childCount; i++)
        {
            var b = menu[i]?.TryCast<TitleMenuButton>();
            if (b == null || b.worldBound.height <= 0f) continue;
            if (below == null) below = b;
            last = b;
        }

        float top, height;
        float gap = below != null ? below.worldBound.yMin - col.yMax : 0f;
        if (gap >= 24f)
        {
            top = col.yMax;
            height = gap;
        }
        else
        {
            top = (last ?? entry).worldBound.yMax + 4f;
            height = col.height * 1.2f;
        }

        // Right-aligned on the same edge as the menu labels (they are
        // right-aligned inside their buttons), not centred in the column.
        float right = col.xMax;
        try
        {
            var lbl = entry.TryCast<TitleMenuButton>()?.Text;
            if (lbl != null && lbl.worldBound.width > 0f) right = lbl.worldBound.xMax;
        }
        catch { }

        Rect origin = docRoot.worldBound;
        _coopTag.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleRight);
        _coopTag.style.left = new StyleLength(col.x - origin.x);
        _coopTag.style.width = new StyleLength(right - col.x);
        // All caps: the line box reserves descender room under the letters, so
        // a centred "CO-OP" reads low (screenshot 2026-10-01: 11 px above,
        // 3 px below). Lift it by that share of the box.
        _coopTag.style.top = new StyleLength(top - origin.y - height * 0.15f);
        _coopTag.style.height = new StyleLength(height);
        _coopTag.style.fontSize = new StyleLength(Mathf.Clamp(height * 0.8f, 20f, 44f));
        if (_coopTag.parent != docRoot)
        {
            _coopTag.RemoveFromHierarchy();
            docRoot.Add(_coopTag);
        }
    }

    // Client auto-connect (AutoMode=Guest) waits for the title screen -- set at
    // init by MelonEntry; see the comment there.
    internal static bool PendingAutoConnect;

    private static void TryPendingAutoConnect()
    {
        if (!PendingAutoConnect) return;
        bool titleUp = false;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count && !titleUp; i++)
                titleUp = open[i]?.TryCast<TitleUIScreen>() != null;
        }
        catch { }
        if (!titleUp) return;
        PendingAutoConnect = false;
        Plugin.Logger.LogInfo("[NET] Title screen up -- auto-connecting to the host");
        CoopNet.Start(CoopMode.Guest, Plugin.HostAddress.Value, Plugin.Port.Value);
    }

    private static bool PlaceUnderLogo()
    {
        VisualElement logo = null;
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count && logo == null; i++)
            {
                var t = open[i]?.TryCast<TitleUIScreen>();
                if (t != null) logo = t.m_MenaceLogoTopLayer ?? t.m_MenaceLogoMiddleLayer;
            }
        }
        catch { }
        if (logo == null || logo.worldBound.width <= 0f) return false;

        var docRoot = logo;
        while (docRoot.parent != null) docRoot = docRoot.parent;

        Rect l = logo.worldBound;
        float top = l.yMax;
        // The build/version line sits just under the logo; start below it.
        var ver = FindVersionLabel(docRoot, 0);
        if (ver != null)
        {
            Rect v = ver.worldBound;
            if (v.yMin >= l.yMax - 0.5f * l.height && v.yMin <= l.yMax + l.height && v.xMax >= l.x && v.x <= l.xMax)
                top = Mathf.Max(top, v.yMax);
        }

        float height = Mathf.Clamp(l.height * 0.4f, 28f, 64f);
        Rect origin = docRoot.worldBound;
        _coopTag.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
        _coopTag.style.left = new StyleLength(l.x - origin.x);
        _coopTag.style.width = new StyleLength(l.width);
        _coopTag.style.top = new StyleLength(top - origin.y);
        _coopTag.style.height = new StyleLength(height);
        _coopTag.style.fontSize = new StyleLength(height * 0.8f);
        if (_coopTag.parent != docRoot)
        {
            _coopTag.RemoveFromHierarchy();
            docRoot.Add(_coopTag);
        }
        return true;
    }

    private static Label FindVersionLabel(VisualElement el, int depth)
    {
        if (el == null || depth > 16 || el == _coopTag) return null;
        var lbl = el.TryCast<Label>();
        if (lbl != null)
        {
            string t = lbl.text;
            if (!string.IsNullOrEmpty(t) && t.StartsWith("v0.") && lbl.worldBound.width > 0f) return lbl;
        }
        for (int i = 0; i < el.childCount; i++)
        {
            var f = FindVersionLabel(el[i], depth + 1);
            if (f != null) return f;
        }
        return null;
    }

    private static void ReplaceWelcomeLabels(VisualElement el, int depth)
    {
        if (el == null || depth > 16) return;
        var lbl = el.TryCast<Label>();
        if (lbl != null)
        {
            string t = lbl.text;
            if (!string.IsNullOrEmpty(t))
            {
                if (t.Contains("Early Access of MENACE")) lbl.text = WelcomeHeader;
                else if (t.Contains("At Overhype Studios")) lbl.text = WelcomeBody;
                else if (t.Contains("So between now")) lbl.text = WelcomeInstructions;
            }
        }
        for (int i = 0; i < el.childCount; i++)
            ReplaceWelcomeLabels(el[i], depth + 1);
    }

    // Full-screen input blocker ("pause screen") shown while either side is
    // mid-sync: on the host while the guest catches up, on the guest during
    // its own load/mission-entry sequences. Lifted explicitly, on disconnect,
    // or by a failsafe timeout.
    private static VisualElement _syncBlocker;
    private static Label _blockerLabel;
    private static float _blockerShownAt;

    // Persistent red banner across the top for version mismatches -- doesn't
    // block input (the player may want to disconnect), just impossible to miss.
    private static Label _versionWarning;
    private static bool _alertDismissed;

    // THE RED BANNER HAS TO BE DISMISSIBLE.
    //
    // Reported: "add ability to hide red banner, we have one for green and orange
    // and it stays forever which is annoying." It was written for one situation
    // -- mismatched game versions, where staying forever is correct because
    // nothing will work until you fix it. Then the roster and terrain checks
    // started using it for things a player might well decide to play through, and
    // a permanent banner across the top of the screen became a punishment.
    //
    // Two ways out now: click it to dismiss this one, or turn the whole thing off
    // in the panel like the other two banners. Suppressing it never suppresses the
    // LOG -- whatever raised it is still recorded in full.
    private static string _lastAlertText;

    // ================= RED BANNER vs ORANGE BANNER =========================
    //
    // The red one exists because of ONE case: mismatched game or mod builds.
    // Nothing works in that state, no amount of syncing will help, and the
    // message has to stay on screen until somebody fixes their install. The
    // orange banner could not do that job -- it clears itself after nine
    // seconds, which is right for "we handled it" and useless for "your two
    // installs are different".
    //
    // It then got reused for the roster and terrain checks, and the line blurred
    // until the only real difference was colour. So, stated once:
    //
    //   RED   = THE PLAYER MUST ACT. The mod cannot fix this by itself.
    //           Mismatched builds (sticky until clicked), the two of you in
    //           different missions, different maps, genuinely different armies.
    //           Outcome: fix your installs, or abandon and relaunch.
    //
    //   ORANGE = THE MOD IS HANDLING IT. Informational, no action required.
    //           Resyncs, round mismatches, stalled-board recovery, blocked guest
    //           actions, save/restore confirmations, and drift that the resync
    //           machinery is already correcting.
    //
    // The test is not severity, it is AGENCY: if the mod is already doing
    // something about it, the player does not need a red bar across their screen
    // telling them so. Mid-battle drift and a transport that unloaded on one
    // side moved to orange under that rule -- both are recoverable, and both
    // were firing repeatedly during normal play.
    //
    // Everything raises a full log line either way; the banner is only ever a
    // summary of something already recorded.

    // RED BANNERS TIME OUT NOW.
    //
    // Reported: "ngl red banners are kind of annoying, they need to disappear
    // after 5ish seconds." They were written for the one case where staying
    // forever is right -- mismatched game or mod builds, where nothing will work
    // until you fix it -- and then reused for the roster, terrain and drift
    // checks, which are worth SAYING once and not worth blocking the top of the
    // screen for the rest of the mission.
    //
    // So the timeout is the default and stickiness is opt-in, claimed only by
    // the build-mismatch callers. Nothing about this suppresses the LOG: the
    // reason is recorded in full either way, which is what actually gets read
    // afterwards.
    private const float AlertBannerShow = 6f;
    private static float _alertShownAt = -999f;
    private static bool _alertSticky;

    internal static void ShowVersionWarning(string text) => ShowVersionWarning(text, false);

    internal static void ShowVersionWarning(string text, bool sticky)
    {
        if (_root == null) return;
        if (Plugin.ShowAlertBanner != null && !Plugin.ShowAlertBanner.Value) return;
        _alertShownAt = Time.realtimeSinceStartup;
        _alertSticky = sticky;

        // Dismissed already, and nothing new to say: stay quiet.
        if (_alertDismissed && text == _lastAlertText) return;
        if (text != _lastAlertText) _alertDismissed = false;   // a NEW alert earns a fresh showing
        _lastAlertText = text;

        if (_versionWarning == null)
        {
            _versionWarning = new Label(text);
            _versionWarning.pickingMode = PickingMode.Position;   // clickable, so it can be dismissed
            _versionWarning.RegisterCallback<PointerDownEvent>((EventCallback<PointerDownEvent>)(_ =>
            {
                _alertDismissed = true;
                HideVersionWarning();
                Plugin.Logger.LogInfo("[UI] Alert banner dismissed by the player (the reason is still in the log)");
            }));
            _versionWarning.style.position = new StyleEnum<Position>(Position.Absolute);
            _versionWarning.style.left = new StyleLength(0f);
            _versionWarning.style.right = new StyleLength(0f);
            _versionWarning.style.top = new StyleLength(0f);
            _versionWarning.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _versionWarning.style.backgroundColor = new StyleColor(new Color(0.72f, 0.10f, 0.10f, 0.96f));
            _versionWarning.style.color = new StyleColor(Color.white);
            _versionWarning.style.fontSize = new StyleLength(18f);
            _versionWarning.style.unityFontStyleAndWeight = FontStyle.Bold;
            _versionWarning.style.paddingTop = new StyleLength(6f);
            _versionWarning.style.paddingBottom = new StyleLength(6f);
        }
        text = text + (_alertSticky ? "   (click to dismiss)" : "");
        _versionWarning.text = text;
        if (_versionWarning.parent == null) _root.Add(_versionWarning);
    }

    private static void HideVersionWarning()
    {
        _versionWarning?.RemoveFromHierarchy();
    }

    // Transient desync notice at the top of the screen -- surfaces what the log
    // records ("actor at (x,y) isn't here", "state mismatch") so players know a
    // desync happened and roughly what, in real time. Auto-clears.
    private static Label _desyncBanner;
    private static float _desyncBannerAt = -999f;
    private const float DesyncBannerShow = 9f;

    // Guest tried something the host owns (buying, hiring, clicking a mirrored
    // screen). Reuses the orange top banner, throttled so a frustrated click
    // storm produces one clear message instead of twenty.
    private static float _lastBlockedAt;

    internal static void ShowActionBlocked(string text)
    {
        if (Time.realtimeSinceStartup - _lastBlockedAt < 1.5f) return;
        _lastBlockedAt = Time.realtimeSinceStartup;
        ShowDesyncWarning(text);
    }

    // Persistent whose-turn-is-it banner, pinned at the top of the screen while
    // sequential phases are running. Colour-coded the same way cursors are:
    // green when it is your turn, the partner's role colour when it is theirs.
    // ---- WAITING-FOR-PARTNER OVERLAY --------------------------------------
    //
    // The host's ORDERS were already blocked while the guest loads in
    // (BlockedByHold, and ShouldBroadcastOrder refuses to send) -- but nothing
    // SAID so. The only signal was CoopNet.Status, a small line inside the co-op
    // panel, which the player may not even have open. So the host roamed the map
    // clicking units that silently did nothing, which reads as the mod being
    // broken rather than as a deliberate wait. Reported 2026-09-17: "host needs a
    // better pause menu for when the guest is loading, host has a lot of freedom
    // when moving around."
    //
    // DELIBERATELY NOT INPUT-BLOCKING. pickingMode stays Ignore, so this can
    // never trap someone behind an overlay that failed to clear: the co-op panel
    // and its FIX DESYNC button stay reachable, and that is the escape hatch
    // when a hold does get stuck. Enforcement already lives in the action gates;
    // this is only the part that explains it.
    //
    // NOT gated on ShowPhaseBanner / ShowWarningBanner. Those are debugging
    // toggles that default to off, and "why is nothing happening" is not
    // debugging output -- it is the one thing the player needs right then.
    private static VisualElement _holdOverlay;
    private static Label _holdTitle;
    private static Label _holdBody;
    private static float _holdSince = -1f;
    // Partner-loading wait: its own plain screen (below).
    private static VisualElement _loadOverlay;
    private static Label _loadLabel;

    private static void UpdateHoldOverlay()
    {
        if (_root == null) return;

        string title = null, body = null;
        bool partnerLoading = false;
        if (CoopNet.Connected)
        {
            // Rejoin: no card. The sync blocker line ("X IS REJOINING -- PLEASE
            // WAIT") is the one display (user, 2026-10-03: two overlays at once).
            if (Replication.SyncPauseActive && GuestReload.RejoiningName != null) { }
            else if (Replication.SyncPauseActive)
            {
                title = "RESOLVING A DESYNC";
                body = "Both games are paused while the boards are put back in step. This finishes on its own.";
            }
            // No battle-entry case here: the host's hold-fire wait already has its
            // own banner ("PARTNER IS ENTERING THE BATTLE -- HOLD FIRE", from
            // SessionSync via SetSyncBlocker). A second overlay on top of it was a
            // duplicate, and the user preferred the original (2026-09-25).
            else if (SessionSync.GuestStillLoading)
            {
                // The STRATEGY-LAYER half of the same wait, and the one the
                // player actually runs into: mission prep is fully usable while
                // the guest loads, so without this the START MISSION button
                // simply does nothing and looks broken.
                partnerLoading = true;
            }
        }

        UpdateLoadOverlay(partnerLoading);

        if (title == null)
        {
            _holdSince = -1f;
            _holdOverlay?.RemoveFromHierarchy();
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (_holdSince < 0f) _holdSince = now;

        if (_holdOverlay == null)
        {
            _holdOverlay = new VisualElement();
            _holdOverlay.pickingMode = PickingMode.Ignore;
            _holdOverlay.style.position = new StyleEnum<Position>(Position.Absolute);
            _holdOverlay.style.left = new StyleLength(0f);
            _holdOverlay.style.right = new StyleLength(0f);
            _holdOverlay.style.top = new StyleLength(0f);
            _holdOverlay.style.bottom = new StyleLength(0f);
            _holdOverlay.style.alignItems = new StyleEnum<Align>(Align.Center);
            _holdOverlay.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            // Dim rather than black out: the board stays readable, so the wait
            // feels like a pause in the game and not a lost connection.
            _holdOverlay.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.55f));

            var card = new VisualElement();
            card.pickingMode = PickingMode.Ignore;
            card.style.backgroundColor = new StyleColor(new Color(0.10f, 0.12f, 0.16f, 0.97f));
            card.style.paddingLeft = new StyleLength(28f);
            card.style.paddingRight = new StyleLength(28f);
            card.style.paddingTop = new StyleLength(20f);
            card.style.paddingBottom = new StyleLength(20f);
            card.style.alignItems = new StyleEnum<Align>(Align.Center);

            _holdTitle = new Label(title);
            _holdTitle.pickingMode = PickingMode.Ignore;
            _holdTitle.style.color = new StyleColor(Color.white);
            _holdTitle.style.fontSize = new StyleLength(26f);
            _holdTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _holdTitle.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _holdTitle.style.marginBottom = new StyleLength(10f);

            _holdBody = new Label(body);
            _holdBody.pickingMode = PickingMode.Ignore;
            _holdBody.style.color = new StyleColor(new Color(0.85f, 0.87f, 0.92f, 1f));
            _holdBody.style.fontSize = new StyleLength(15f);
            _holdBody.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _holdBody.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);

            card.Add(_holdTitle);
            card.Add(_holdBody);
            _holdOverlay.Add(card);
        }

        _holdTitle.text = title;
        // The elapsed count is what distinguishes "loading" from "hung". Without
        // it a wait of any length looks identical to a freeze.
        _holdBody.text = $"{body}\n\nWaiting {Mathf.RoundToInt(now - _holdSince)}s";
        if (_holdOverlay.parent == null) _root.Add(_holdOverlay);
    }

    // PARTNER LOADING: faded black and one line of text, nothing more, and never
    // on top of the sync blocker (user, 2026-09-26: "make sure there aren't two
    // displays, just the faded black screen with the text is good"). Same look
    // as the blocker, so every partner-loading wait reads the same.
    private static void UpdateLoadOverlay(bool show)
    {
        if (show && _syncBlocker != null && _syncBlocker.parent != null) show = false;
        if (!show) { _loadOverlay?.RemoveFromHierarchy(); return; }
        if (_loadOverlay == null)
        {
            _loadOverlay = new VisualElement();
            _loadOverlay.pickingMode = PickingMode.Ignore;
            _loadOverlay.style.position = new StyleEnum<Position>(Position.Absolute);
            _loadOverlay.style.left = new StyleLength(0f);
            _loadOverlay.style.right = new StyleLength(0f);
            _loadOverlay.style.top = new StyleLength(0f);
            _loadOverlay.style.bottom = new StyleLength(0f);
            _loadOverlay.style.alignItems = new StyleEnum<Align>(Align.Center);
            _loadOverlay.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            _loadOverlay.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.66f));
            _loadLabel = new Label("PARTNER IS LOADING YOUR GAME");
            _loadLabel.pickingMode = PickingMode.Ignore;
            _loadLabel.style.color = new StyleColor(BoneText);
            _loadLabel.style.fontSize = new StyleLength(26f);
            _loadLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _loadOverlay.Add(_loadLabel);
        }
        if (_loadOverlay.parent == null) _root.Add(_loadOverlay);
    }

    private static Label _phaseBanner;
    private static string _phaseBannerText;

    internal static void ShowPhaseChange(string text) => _phaseBannerText = text;

    private static void UpdatePhaseBanner()
    {
        if (_root == null) return;
        if (Plugin.ShowPhaseBanner != null && !Plugin.ShowPhaseBanner.Value)
        {
            _phaseBanner?.RemoveFromHierarchy();
            return;
        }
        string text = TurnControl.Active ? TurnControl.StatusText : null;
        if (string.IsNullOrEmpty(text))
        {
            _phaseBanner?.RemoveFromHierarchy();
            return;
        }
        if (_phaseBanner == null)
        {
            _phaseBanner = new Label(text);
            _phaseBanner.pickingMode = PickingMode.Ignore;
            _phaseBanner.style.position = new StyleEnum<Position>(Position.Absolute);
            _phaseBanner.style.left = new StyleLength(0f);
            _phaseBanner.style.right = new StyleLength(0f);
            _phaseBanner.style.top = new StyleLength(0f);
            _phaseBanner.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _phaseBanner.style.color = new StyleColor(Color.white);
            _phaseBanner.style.fontSize = new StyleLength(18f);
            _phaseBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _phaseBanner.style.paddingTop = new StyleLength(4f);
            _phaseBanner.style.paddingBottom = new StyleLength(4f);
        }
        _phaseBanner.text = text;
        bool mine = TurnControl.IsMyTurn;
        _phaseBanner.style.backgroundColor = new StyleColor(mine
            ? new Color(0.10f, 0.45f, 0.15f, 0.94f)     // your turn -- green
            : new Color(0.30f, 0.30f, 0.34f, 0.94f));   // waiting -- neutral grey
        if (_phaseBanner.parent == null) _root.Add(_phaseBanner);
    }

    internal static void ShowDesyncWarning(string text)
    {
        if (_root == null) return;
        // Suppressing the banner hides the NOTIFICATION only -- corrections are
        // still applied and still logged, so nothing is silently skipped.
        if (Plugin.ShowWarningBanner != null && !Plugin.ShowWarningBanner.Value)
        {
            _desyncBanner?.RemoveFromHierarchy();
            return;
        }
        if (_desyncBanner == null)
        {
            _desyncBanner = new Label(text);
            _desyncBanner.pickingMode = PickingMode.Ignore;
            _desyncBanner.style.position = new StyleEnum<Position>(Position.Absolute);
            _desyncBanner.style.left = new StyleLength(0f);
            _desyncBanner.style.right = new StyleLength(0f);
            _desyncBanner.style.top = new StyleLength(30f); // below a version banner if present
            _desyncBanner.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _desyncBanner.style.backgroundColor = new StyleColor(new Color(0.85f, 0.45f, 0.05f, 0.96f));
            _desyncBanner.style.color = new StyleColor(Color.white);
            _desyncBanner.style.fontSize = new StyleLength(16f);
            _desyncBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _desyncBanner.style.paddingTop = new StyleLength(5f);
            _desyncBanner.style.paddingBottom = new StyleLength(5f);
        }
        _desyncBanner.text = text;
        if (_desyncBanner.parent == null) _root.Add(_desyncBanner);
        _desyncBannerAt = Time.realtimeSinceStartup;
    }

    // Keep a pause on screen for at least this long so it can't flash away
    // before the partner registers it (a common "the menu closed too fast"
    // complaint). Hard releases (disconnect / failsafe) bypass this.
    private static bool _blockerHidePending;
    private const float BlockerMinShow = 1.6f;

    internal static void SetSyncBlocker(bool show, string text = "PARTNER IS SYNCING -- PLEASE WAIT")
    {
        if (_root == null) return;
        if (!show)
        {
            // A persistent pause owns the screen right now -- the host is holding
            // fire for the guest, or the guest is loading into a battle. Ignore
            // stray hide requests (e.g. the guest's "synced" ack firing mid-load)
            // until that flag is cleared by its own explicit release / failsafe.
            if (SessionSync.HostHoldFire || SessionSync.GuestEnteringBattle) return;
            if (_syncBlocker != null && _syncBlocker.parent != null
                && Time.realtimeSinceStartup - _blockerShownAt < BlockerMinShow)
            {
                _blockerHidePending = true;   // defer -- UpdateSyncBlocker finishes it
                return;
            }
            _blockerHidePending = false;
            _syncBlocker?.RemoveFromHierarchy();
            return;
        }
        _blockerHidePending = false;
        if (_syncBlocker == null)
        {
            _syncBlocker = new VisualElement();
            _syncBlocker.style.position = new StyleEnum<Position>(Position.Absolute);
            _syncBlocker.style.left = new StyleLength(0f);
            _syncBlocker.style.right = new StyleLength(0f);
            _syncBlocker.style.top = new StyleLength(0f);
            _syncBlocker.style.bottom = new StyleLength(0f);
            _syncBlocker.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.66f));
            _syncBlocker.style.alignItems = new StyleEnum<Align>(Align.Center);
            _syncBlocker.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            _blockerLabel = new Label(text);
            _blockerLabel.style.color = new StyleColor(BoneText);
            _blockerLabel.style.fontSize = new StyleLength(26f);
            _blockerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _syncBlocker.Add(_blockerLabel);
        }
        _blockerLabel.text = text;
        if (_syncBlocker.parent == null) _root.Add(_syncBlocker);
        _blockerShownAt = Time.realtimeSinceStartup;
    }

    private static void UpdateSyncBlocker()
    {
        // version-mismatch banner clears once disconnected
        if (_versionWarning != null && _versionWarning.parent != null
            && CoopNet.Mode == CoopMode.Off && !CoopNet.Connected)
            HideVersionWarning();

        // transient desync banner auto-clears
        if (_desyncBanner != null && _desyncBanner.parent != null
            && Time.realtimeSinceStartup - _desyncBannerAt > DesyncBannerShow)
            _desyncBanner.RemoveFromHierarchy();

        // ...and so does the red one, unless it is a build mismatch (sticky).
        if (!_alertSticky && _versionWarning != null && _versionWarning.parent != null
            && Time.realtimeSinceStartup - _alertShownAt > AlertBannerShow)
            HideVersionWarning();

        if (_syncBlocker == null || _syncBlocker.parent == null) return;
        float now = Time.realtimeSinceStartup;
        // hard release on disconnect / long failsafe, bypassing the minimum show
        if (!CoopNet.Connected || now - _blockerShownAt > 150f)
        {
            _blockerHidePending = false;
            _syncBlocker.RemoveFromHierarchy();
            return;
        }
        // finish a deferred hide once the minimum show time has elapsed
        if (_blockerHidePending && now - _blockerShownAt >= BlockerMinShow)
        {
            _blockerHidePending = false;
            _syncBlocker.RemoveFromHierarchy();
        }
    }

    private static void StartNetSteam(CoopMode mode)
    {
        // v5.1: no ID needed. The HOST opens a Steam lobby and players come in
        // by invite or from the friends list (SteamLobby). IDs typed here still
        // work, with or without the lobby: the HOST may list several players'
        // SteamID64s (comma separated); a client enters the host's one.
        string raw = _steamIdField.value?.Trim();
        var ids = CoopNet.ParseSteamIds(raw);
        if (mode == CoopMode.Host)
        {
            Plugin.PartnerSteamId.Value = raw ?? "";
            CoopNet.StartSteam(mode, ids);
            SteamLobby.HostCreate();
            return;
        }
        if (ids.Count > 1)
        {
            CoopNet.Status = "Join: enter only the HOST's SteamID64";
            return;
        }
        if (ids.Count == 0)
        {
            SteamLobby.JoinFriend();          // the friend who is hosting
            return;
        }
        Plugin.PartnerSteamId.Value = raw;
        CoopNet.StartSteam(mode, ids);
    }

    // JOIN buttons for friends who are hosting, shown while offline.
    private static VisualElement _friendBox;
    private static Button _steamInviteBtn;
    private static int _friendBoxVersion = -1;
    private static bool _friendBoxOff;

    private static void UpdateFriendLobbies()
    {
        if (_friendBox == null) return;
        bool off = CoopNet.Mode == CoopMode.Off;
        if (_friendBoxVersion == SteamLobby.FriendLobbiesVersion && _friendBoxOff == off) return;
        _friendBoxVersion = SteamLobby.FriendLobbiesVersion;
        _friendBoxOff = off;
        _friendBox.Clear();
        if (!off) return;
        foreach (var f in SteamLobby.FriendLobbies)
        {
            ulong lobby = f.Lobby;
            string name = f.Name;
            _friendBox.Add(MakeButton($"JOIN {name} (Steam)", () => SteamLobby.Join(lobby, $"JOIN button, friend '{name}'")));
        }
    }

    internal static bool TitleScreenUp()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            for (int i = 0; open != null && i < open.Count; i++)
                if (open[i]?.TryCast<TitleUIScreen>() != null) return true;
        }
        catch { }
        return false;
    }

    private static void StartNet(CoopMode mode)
    {
        string ip = _ipField.value?.Trim();
        if (!int.TryParse(_portField.value?.Trim(), out int port) || port < 1 || port > 65535)
        {
            CoopNet.Status = "Invalid port";
            return;
        }
        if (mode == CoopMode.Guest && string.IsNullOrWhiteSpace(ip))
        {
            CoopNet.Status = "Enter the host's IP";
            return;
        }
        string pw = _passwordField?.value ?? "";
        if (string.IsNullOrEmpty(CoopAuth.Normalize(pw)))
        {
            if (mode == CoopMode.Guest)
            {
                CoopNet.Status = "Type the host's session code in Password first";
                return;
            }
            // Host with no code: make one and show it, ready to read out.
            pw = CoopAuth.NewCode();
            _passwordField.value = pw;
            _passwordField.isPasswordField = false;
            Plugin.Logger.LogInfo("[NET] Generated a session code for this host");
        }
        Plugin.SessionPassword.Value = pw;
        Plugin.HostAddress.Value = ip;
        Plugin.Port.Value = port;
        CoopNet.Start(mode, ip, port);
    }
}








