using System;
#if MELON
using Il2CppMenace.Tactical;
using Menace = Il2CppMenace;   // MelonLoader prefixes game namespaces; keeps Menace.X.Y references compiling
#else
using Menace.Tactical;
#endif

namespace MenaceCoop;

// DO NOT STAND IN THE ACID.
//
// MENACE models ground hazards as TILE EFFECTS -- acid puddles, fire and the
// like -- and standing in one costs health every round for as long as the unit
// stays there. Auto combat had no notion of them: it scored a tile on cover and
// range alone, so a squad would take up a good firing position in a puddle and
// quietly bleed out holding it.
//
// The engine's own AI already knows about this -- it carries a
// ThreatFromTileEffects weight and an AvoidDueToTileEffect flag -- so this is
// reading a native concept rather than inventing one.
internal static class Hazards
{
    private static int _logged;

    internal static bool IsHazardous(Tile t)
    {
        if (t == null) return false;
        try
        {
            var fx = t.GetEffects();
            if (fx == null || fx.Count == 0) return false;

            for (int i = 0; i < fx.Count; i++)
            {
                var e = fx[i];
                if (e == null) continue;

                // The engine's own "the AI should keep away from this" flag.
                // Anything we cannot read is treated as worth avoiding: standing
                // in an unknown effect is the risk this whole class exists to
                // remove, so the safe default is to move.
                bool avoid = true;
                try { avoid = (e.GetIl2CppType()?.Name ?? "").Contains("DamageOverTime"); } catch { }
                if (!avoid) continue;

                if (_logged++ < 5)
                    Plugin.Logger.LogInfo($"[AUTO] tile ({t.GetTilePos().x},{t.GetTilePos().y}) carries a hazard worth avoiding");
                return true;
            }
        }
        catch { }
        return false;
    }
}
