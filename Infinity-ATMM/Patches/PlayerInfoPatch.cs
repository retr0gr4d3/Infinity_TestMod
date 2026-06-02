using HarmonyLib;
using MelonLoader;

namespace Infinity_TestMod.Patches
{
    [HarmonyPatch(typeof(PlayerInfo), nameof(PlayerInfo.hasAchievement))]
    public static class PlayerInfoHasAchievementPatch
    {
        public static bool Prefix(PlayerInfo __instance, ref bool __result)
        {
            if (TestMod.fakeBadgesActive)
            {
                __result = true;
                return false; // Skip original method and return true
            }

            // Safety guard: if the player instance or achievements dictionary is null,
            // return false instead of letting the game crash with a NullReferenceException.
            if (__instance == null || __instance.achievements == null)
            {
                __result = false;
                return false; // Skip original method and return false
            }

            return true; // Fallback to original game logic when deactivated
        }
    }
}
