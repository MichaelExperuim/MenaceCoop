using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppMenace.Conversations;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;

namespace MenaceCoop;

// STRATEGY CONVERSATIONS ARE THE HOST'S (tester report 2026-10-01, BUGS #48).
//
// The post-operation talks (Hayflick: "Major, we've punctured a tire...") run on
// both machines, but co-op never synced them: the guest's clicks were swallowed
// by the viewer shield and the box hung until the guest rejoined. User ruling:
// sync them, host decisive.
//
// The host relays every step of a conversation on the strategy conversation
// screen -- "convnext" (advance, with the node it advanced FROM), "convchoice" /
// "convrole" (which answer), "convend" -- and the guest replays each by pressing
// its OWN matching button, the same engine path a click takes. The guest's own
// advances and answers are refused. Every effect of a replayed answer is refused
// on the guest too (StrategyMirror's data-layer gates, GuestUiReplay): the host's
// campaign changes arrive through their own relays, so nothing applies twice.
//
// Lockstep is checked by node Guid. A step the guest is not ready for yet (still
// animating the previous line) waits up to 8 s; on "convend" the guest's screen
// is closed if it is still up, so it can never hang again. Tactical barks use a
// different view and are left alone.
internal static class ConversationSync
{
    internal static bool Applying;
    private static int _hostDepth;   // nested presenter calls inside one host step relay once

    // guest: choice index / role index per button, recorded as the screen builds them
    private static readonly Dictionary<IntPtr, int> _choiceButtons = new();
    private static readonly Dictionary<int, SpeakerTemplate> _roleSpeakers = new();
    private static RoleChoiceConversationNode _roleNode;

    private static readonly List<(CoopMsg msg, float until)> _pending = new();
    private static float _endAt = -1f;
    private static string _endTemplate;

    private static bool Live => CoopNet.Connected && Il2CppMenace.Tactical.TacticalManager.Get() == null;

    internal static bool IsScreenPresenter(ConversationPresenter p)
    {
        try { return p?.GetView()?.TryCast<ConversationUIScreen>() != null; } catch { return false; }
    }

    private static string TemplateOf(ConversationPresenter p)
    {
        try { return p?.GetConversation()?.name ?? "?"; } catch { return "?"; }
    }

    private static int NodeOf(ConversationPresenter p)
    {
        try { return p?.m_CurrentConvNode?.Guid ?? 0; } catch { return 0; }
    }

    private static ConversationUIScreen OpenScreen()
    {
        try
        {
            var open = UIManager.Get()?.m_OpenScreens;
            if (open == null) return null;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var s = open[i]?.TryCast<ConversationUIScreen>();
                if (s != null) return s;
            }
        }
        catch { }
        return null;
    }

    private static void Refused(string what)
    {
        CoopNet.Status = "The host answers in conversations -- you'll follow along";
        if (UnityEngine.Time.realtimeSinceStartup - CoopRunner.LastUserInputAt < 0.6f)
            CoopRunner.ShowActionBlocked("THE HOST ANSWERS -- the conversation follows the host");
        Plugin.Logger.LogInfo($"[CONV] Held a local {what} (the host drives conversations)");
    }

    // ---- presenter advance -------------------------------------------------

    internal static bool BeforeContinue(ConversationPresenter p, out int nodeBefore)
    {
        nodeBefore = 0;
        if (!Live || !IsScreenPresenter(p)) return true;
        if (CoopNet.Mode == CoopMode.Guest)
        {
            if (Applying) return true;
            Refused("advance");
            return false;
        }
        nodeBefore = _hostDepth == 0 ? NodeOf(p) : 0;
        _hostDepth++;
        return true;
    }

    internal static void AfterContinue(ConversationPresenter p, int nodeBefore)
    {
        if (!Live || CoopNet.Mode != CoopMode.Host || !IsScreenPresenter(p)) return;
        if (_hostDepth > 0) _hostDepth--;
        if (_hostDepth > 0 || nodeBefore == 0) return;
        CoopNet.Send(new CoopMsg { T = "convnext", SkillId = TemplateOf(p), Prm = nodeBefore });
        Plugin.Logger.LogInfo($"[CONV] Host advanced '{TemplateOf(p)}' from node {nodeBefore} -> partner");
    }

    // ---- choices --------------------------------------------------------------

    internal static bool BeforeChoice(ConversationPresenter p, ChoiceConversationNode node, int idx)
    {
        if (!Live || !IsScreenPresenter(p)) return true;
        if (CoopNet.Mode == CoopMode.Guest)
        {
            if (Applying) return true;
            Refused($"answer #{idx}");
            return false;
        }
        int guid = 0;
        try { guid = node?.Guid ?? 0; } catch { }
        CoopNet.Send(new CoopMsg { T = "convchoice", SkillId = TemplateOf(p), Prm = idx, MIdx = guid });
        Plugin.Logger.LogInfo($"[CONV] Host chose answer #{idx} at node {guid} in '{TemplateOf(p)}' -> partner");
        _hostDepth++;   // the advance the answer causes is part of this step
        return true;
    }

    internal static void AfterChoice()
    {
        if (CoopNet.Mode == CoopMode.Host && _hostDepth > 0) _hostDepth--;
    }

    internal static bool BeforeRoleChoice(ConversationPresenter p, RoleChoiceConversationNode node, int roleIdx)
    {
        if (!Live || !IsScreenPresenter(p)) return true;
        if (CoopNet.Mode == CoopMode.Guest)
        {
            if (Applying) return true;
            Refused($"speaker pick #{roleIdx}");
            return false;
        }
        int guid = 0;
        try { guid = node?.Guid ?? 0; } catch { }
        CoopNet.Send(new CoopMsg { T = "convrole", SkillId = TemplateOf(p), Prm = roleIdx, MIdx = guid });
        Plugin.Logger.LogInfo($"[CONV] Host picked speaker #{roleIdx} at node {guid} in '{TemplateOf(p)}' -> partner");
        _hostDepth++;
        return true;
    }

    internal static void NoteChoiceButton(BaseButton b, int idx)
    {
        if (CoopNet.Mode != CoopMode.Guest || b == null) return;
        _choiceButtons[b.Pointer] = idx;
    }

    internal static void NoteRoleChoice(RoleChoiceConversationNode node, int roleIdx, SpeakerTemplate speaker)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (_roleNode == null || node == null || _roleNode.Pointer != node.Pointer) { _roleSpeakers.Clear(); _roleNode = node; }
        _roleSpeakers[roleIdx] = speaker;
    }

    internal static void NewChoiceSet() => _choiceButtons.Clear();

    // ---- event messages (host opens, guest follows) -------------------------------
    //
    // The strategy event messages open a conversation on the machine that clicks
    // them. The guest's clicks are refused (StrategyMirrorPatches.GateHostOnlyClicks);
    // when the HOST's conversation screen opens, the guest opens the same one --
    // by pressing its own message for that event, else by opening the screen
    // with its own instance -- and the steps above carry it from there.

    internal static bool IsEventMessage(UnityEngine.UIElements.VisualElement el)
    {
        for (var e = el; e != null; e = e.parent)
            if (e.TryCast<EventMessageElement>() != null) return true;
        return false;
    }

    internal static void HostScreenOpened(ConversationUIScreen s, IOpenScreenParams prm)
    {
        if (!Live || CoopNet.Mode != CoopMode.Host) return;
        string t = "?";
        try { t = prm?.TryCast<OpenConversationUIScreenParams>()?.Conversation?.GetTemplate()?.name ?? TemplateOf(s?.GetPresenter()); } catch { }
        CoopNet.Send(new CoopMsg { T = "convopen", SkillId = t });
        Plugin.Logger.LogInfo($"[CONV] Host opened conversation '{t}' -> partner");
    }

    private static bool TryOpenEvent(CoopMsg m, bool final)
    {
        var screen = OpenScreen();
        var p = screen?.GetPresenter();
        if (p != null && p.IsConversationRunning())
        {
            string mine = TemplateOf(p);
            if (mine == m.SkillId) return true;   // opened here too (post-operation talks do)
            if (final) Plugin.Logger.LogWarning($"[CONV] Host opened '{m.SkillId}' but '{mine}' is running here -- not opened");
            return false;
        }
        var msgEl = FindEventMessage(m.SkillId);
        if (msgEl == null)
        {
            if (final) Plugin.Logger.LogWarning($"[CONV] Host opened '{m.SkillId}': no such event message here -- not opened");
            return false;
        }
        Applying = true;
        StrategyMirror.GuestUiReplay = true;
        try
        {
            var click = msgEl.TryCast<InteractiveElement>();
            var act = click?.GetOnLeftClickedAction();
            if (act != null) act.Invoke(click);
            else
            {
                var ev = msgEl.GetEvent();
                UIManager.Get()?.OpenScreen(ConversationUIScreen.PREFAB_NAME,
                    new OpenConversationUIScreenParams(ev, null).Cast<IOpenScreenParams>(), false, false);
            }
            Plugin.Logger.LogInfo($"[CONV] Opened host's event conversation '{m.SkillId}' here ({(act != null ? "message click" : "screen open")})");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[CONV] Opening host's '{m.SkillId}' threw: {e.GetType().Name}: {e.Message}"); }
        finally { Applying = false; StrategyMirror.GuestUiReplay = false; }
        return true;
    }

    private static EventMessageElement FindEventMessage(string template)
    {
        try
        {
            var docs = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UIElements.UIDocument>());
            for (int i = 0; docs != null && i < docs.Length; i++)
            {
                var root = docs[i].TryCast<UnityEngine.UIElements.UIDocument>()?.rootVisualElement;
                var f = FindEventMessage(root, template, 0);
                if (f != null) return f;
            }
        }
        catch { }
        return null;
    }

    private static EventMessageElement FindEventMessage(UnityEngine.UIElements.VisualElement el, string template, int depth)
    {
        if (el == null || depth > 18) return null;
        var m = el.TryCast<EventMessageElement>();
        if (m != null)
        {
            try { if (m.GetEvent()?.GetTemplate()?.name == template) return m; } catch { }
        }
        for (int c = 0; c < el.childCount; c++)
        {
            var f = FindEventMessage(el[c], template, depth + 1);
            if (f != null) return f;
        }
        return null;
    }

    // ---- end ---------------------------------------------------------------------

    internal static void HostConversationOver(ConversationUIScreen s)
    {
        if (!Live || CoopNet.Mode != CoopMode.Host) return;
        string t = TemplateOf(s?.GetPresenter());
        CoopNet.Send(new CoopMsg { T = "convend", SkillId = t });
        Plugin.Logger.LogInfo($"[CONV] Host's conversation '{t}' is over -> partner");
        // AN EVENT'S OUTCOME CHANGES THE CAMPAIGN (player report 2026-10-20: the
        // workshop event finished, and the workshop never appeared for the
        // guest). The answers are mirrored, but what they unlock -- a building,
        // an item, a squaddie -- is applied to the host's campaign only. Send the
        // campaign itself; the auto-sync waits until the host is off any stateful
        // screen, so the guest's view reset is not seen.
        if (Il2CppMenace.Tactical.TacticalManager.Get() == null)
            SessionSync.ScheduleAutoSync(2f, $"strategy event '{t}' finished");
    }

    // ---- guest replay ------------------------------------------------------------

    internal static void HandleRemote(CoopMsg m)
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        if (m.T == "convend")
        {
            _pending.Clear();
            _endTemplate = m.SkillId;
            _endAt = UnityEngine.Time.realtimeSinceStartup + 1.5f;   // let a lockstep finish close it first
            return;
        }
        if (!TryApply(m, final: false))
            _pending.Add((m, UnityEngine.Time.realtimeSinceStartup + 8f));
    }

    internal static void Tick()
    {
        if (CoopNet.Mode != CoopMode.Guest) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        // in order: a later step never jumps an earlier one
        while (_pending.Count > 0)
        {
            var (msg, until) = _pending[0];
            bool expired = now > until;
            if (!TryApply(msg, final: expired) && !expired) break;
            _pending.RemoveAt(0);
        }
        if (_endAt > 0 && now >= _endAt)
        {
            _endAt = -1f;
            ForceClose();
        }
    }

    // True when applied (or definitively dropped when final).
    private static bool TryApply(CoopMsg m, bool final)
    {
        if (m.T == "convopen") return TryOpenEvent(m, final);
        var screen = OpenScreen();
        var p = screen?.GetPresenter();
        if (p == null || !p.IsConversationRunning())
        {
            if (final) Plugin.Logger.LogWarning($"[CONV] Host's {m.T} for '{m.SkillId}' dropped: no conversation running here");
            return false;
        }
        string mine = TemplateOf(p);
        if (mine != m.SkillId)
        {
            if (final) Plugin.Logger.LogWarning($"[CONV] Host's {m.T} is for '{m.SkillId}', this machine shows '{mine}' -- dropped (convend will close it)");
            return false;
        }
        int node = NodeOf(p);
        int want = m.T == "convnext" ? m.Prm : m.MIdx;
        if (node != want)
        {
            if (final) Plugin.Logger.LogWarning($"[CONV] Host's {m.T} expected node {want}, this machine is on {node} in '{mine}' -- dropped");
            return false;
        }

        Applying = true;
        StrategyMirror.GuestUiReplay = true;
        try
        {
            switch (m.T)
            {
                case "convnext":
                    p.ProcessContinue();
                    break;
                case "convchoice":
                {
                    BaseButton btn = null;
                    var box = screen.m_PlayerChoicesContainer;
                    if (box != null) btn = FindChoiceButton(box, m.Prm, 0);
                    var act = btn?.GetOnLeftClickedAction();
                    if (act == null)
                    {
                        if (final) Plugin.Logger.LogWarning($"[CONV] Host's answer #{m.Prm} in '{mine}': no such button here ({_choiceButtons.Count} recorded) -- dropped");
                        return false;
                    }
                    act.Invoke(btn);
                    break;
                }
                case "convrole":
                {
                    var node2 = p.m_CurrentConvNode?.TryCast<RoleChoiceConversationNode>();
                    if (node2 == null || !_roleSpeakers.TryGetValue(m.Prm, out var speaker))
                    {
                        if (final) Plugin.Logger.LogWarning($"[CONV] Host's speaker pick #{m.Prm} in '{mine}' cannot be replayed here -- dropped");
                        return false;
                    }
                    node2.ExecuteChoice(m.Prm, speaker, p);
                    break;
                }
                default:
                    return true;
            }
            Plugin.Logger.LogInfo($"[CONV] Replayed host's {m.T} ({m.Prm}) in '{mine}' at node {node}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[CONV] Replaying host's {m.T} in '{mine}' threw: {e.GetType().Name}: {e.Message}");
            return true;   // do not retry a throw
        }
        finally { Applying = false; StrategyMirror.GuestUiReplay = false; }
    }

    private static BaseButton FindChoiceButton(UnityEngine.UIElements.VisualElement el, int idx, int depth)
    {
        if (el == null || depth > 6) return null;
        var b = el.TryCast<BaseButton>();
        if (b != null && _choiceButtons.TryGetValue(b.Pointer, out int i) && i == idx) return b;
        for (int c = 0; c < el.childCount; c++)
        {
            var f = FindChoiceButton(el[c], idx, depth + 1);
            if (f != null) return f;
        }
        return null;
    }

    private static void ForceClose()
    {
        var screen = OpenScreen();
        if (screen == null) return;
        var p = screen.GetPresenter();
        string mine = TemplateOf(p);
        Plugin.Logger.LogInfo($"[CONV] Host's '{_endTemplate}' is over; closing '{mine}' here");
        Applying = true;
        StrategyMirror.GuestUiReplay = true;
        try
        {
            if (p != null && p.IsConversationRunning()) p.CancelConversation();
            if (OpenScreen() != null) screen.CloseScreen();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[CONV] Closing the conversation threw: {e.Message}"); }
        finally { Applying = false; StrategyMirror.GuestUiReplay = false; }
    }
}

[HarmonyPatch]
internal static class ConversationPatches
{
    [HarmonyPatch(typeof(ConversationPresenter), nameof(ConversationPresenter.ProcessContinue))]
    [HarmonyPrefix]
    private static bool BeforeContinue(ConversationPresenter __instance, out int __state)
        => ConversationSync.BeforeContinue(__instance, out __state);

    // A finalizer, not a postfix: if the engine throws mid-advance the host's
    // depth counter must still unwind, or every later step stops relaying.
    [HarmonyPatch(typeof(ConversationPresenter), nameof(ConversationPresenter.ProcessContinue))]
    [HarmonyFinalizer]
    private static void AfterContinue(ConversationPresenter __instance, int __state)
        => ConversationSync.AfterContinue(__instance, __state);

    [HarmonyPatch(typeof(ConversationPresenter), nameof(ConversationPresenter.ShowChoices))]
    [HarmonyPrefix]
    private static void NewChoiceSet() => ConversationSync.NewChoiceSet();

    [HarmonyPatch(typeof(Choice), nameof(Choice.SetupButton))]
    [HarmonyPostfix]
    private static void NoteButton(BaseButton _button, int _choiceIdx) => ConversationSync.NoteChoiceButton(_button, _choiceIdx);

    [HarmonyPatch(typeof(ConversationUIScreen), nameof(ConversationUIScreen.ShowRoleChoice))]
    [HarmonyPostfix]
    private static void NoteRole(RoleChoiceConversationNode _choiceNode, int _roleIdx, SpeakerTemplate _speaker)
        => ConversationSync.NoteRoleChoice(_choiceNode, _roleIdx, _speaker);

    [HarmonyPatch(typeof(RoleChoiceConversationNode), nameof(RoleChoiceConversationNode.ExecuteChoice))]
    [HarmonyPrefix]
    private static bool BeforeRole(RoleChoiceConversationNode __instance, int _roleIdx, ConversationPresenter _presenter)
        => ConversationSync.BeforeRoleChoice(_presenter, __instance, _roleIdx);

    [HarmonyPatch(typeof(RoleChoiceConversationNode), nameof(RoleChoiceConversationNode.ExecuteChoice))]
    [HarmonyFinalizer]
    private static void AfterRole() => ConversationSync.AfterChoice();

    [HarmonyPatch(typeof(ConversationUIScreen), nameof(ConversationUIScreen.OnConversationOver))]
    [HarmonyPostfix]
    private static void Over(ConversationUIScreen __instance) => ConversationSync.HostConversationOver(__instance);

    [HarmonyPatch(typeof(ConversationUIScreen), nameof(ConversationUIScreen.OnOpened))]
    [HarmonyPostfix]
    private static void Opened(ConversationUIScreen __instance, IOpenScreenParams _openScreenParams)
        => ConversationSync.HostScreenOpened(__instance, _openScreenParams);
}

// The answer button's click handler is a compiler lambda on Choice
// (<>c__DisplayClassN_0._SetupButton_b__M, capturing the choice index, node and
// presenter). Its class/lambda numbers change between game builds, so it is
// found by shape: the void, one-InteractiveElement-argument SetupButton lambda.
[HarmonyPatch]
internal static class ConversationChoicePatch
{
    private static MethodBase TargetMethod()
    {
        foreach (var t in typeof(Choice).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!m.Name.StartsWith("_SetupButton_b__") || m.ReturnType != typeof(void)) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(InteractiveElement)) return m;
            }
        Plugin.Logger.LogWarning("[CONV] Choice click lambda not found -- conversation answers will not sync");
        return null;
    }

    private static bool Prefix(object __instance)
    {
        try
        {
            var t = __instance.GetType();
            var p = (ConversationPresenter)t.GetProperty("_presenter")?.GetValue(__instance);
            var node = (ChoiceConversationNode)t.GetProperty("_choiceNode")?.GetValue(__instance);
            int idx = (int)(t.GetProperty("_choiceIdx")?.GetValue(__instance) ?? -1);
            return ConversationSync.BeforeChoice(p, node, idx);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[CONV] Reading the clicked answer failed: {e.Message}");
            return true;
        }
    }

    private static void Finalizer() => ConversationSync.AfterChoice();
}
