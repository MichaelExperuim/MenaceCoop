#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// WHAT IS AUTO-COMBAT ACTUALLY DOING RIGHT NOW?
//
// Ported from the standalone MenaceAutoCombat mod, where a player asked for it in
// exactly these terms: "a section under the panel that explains the AI's current
// thinking, like Angel is moving... angel is attacking... angel is thinking."
//
// It answers three questions that are otherwise guesswork while watching squads
// move themselves around: is it stuck, whose turn is it working on, and did it
// decide to do nothing on purpose. In co-op it earns its place twice over --
// when your partner's units are being played by their auto-combat, the only
// thing you can see from your seat is units moving with no explanation.
//
// Deliberately phrased the way a person would say it, using the squad's own
// name, because that is who you are watching on the board.
internal static class Thinking
{
    internal static string Current { get; private set; } = "Idle.";
    internal static float ChangedAt { get; private set; }

    // SAY IT TO THE LOG TOO, NOT JUST THE PANEL.
    //
    // Reported: "would like more detail in the auto combat logs, just saying
    // turns finished isnt descriptive. would like 'Darby is attacking unit, unit
    // is reviving, etc'."
    //
    // The action log already carried the mechanical record -- template names, AP,
    // ranges, weapon costs -- which is what you want when diagnosing a desync and
    // exactly not what you want when reading back what the squad DID. The
    // commentary is already written in those words for the panel; there is no
    // reason it should not be in the log beside the mechanics.
    //
    // Only on change, which is what the panel needs anyway, so a squad thinking
    // for ten seconds produces one line rather than six hundred.
    internal static void Say(string what)
    {
        if (what == Current) return;
        Current = what;
        ChangedAt = UnityEngine.Time.realtimeSinceStartup;
        try { Plugin.Logger.LogInfo($"[AUTO] {what}"); } catch { }
    }

    internal static void Reset() => Say("Idle.");

    // Skill ids read like "active.mod.medium_machinegun_suppressive_fire"; the
    // tail, spaced out, is what the skill is actually called on screen.
    internal static string PrettySkill(string id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return "a support skill";
            string s = id;
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            return s.Replace('_', ' ');
        }
        catch { return "a support skill"; }
    }

    // Squad templates read like "player_squad.tech"; the last word is the name a
    // player actually knows the unit by.
    internal static string NameOf(Actor a)
    {
        try
        {
            string t = a?.GetTemplate()?.name ?? "";
            int dot = t.LastIndexOf('.');
            if (dot >= 0 && dot < t.Length - 1) t = t.Substring(dot + 1);
            if (t.Length == 0) return "A squad";
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }
        catch { return "A squad"; }
    }
}
