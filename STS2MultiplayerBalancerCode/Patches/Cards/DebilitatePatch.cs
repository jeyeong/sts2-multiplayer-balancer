using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.Cards;

/// <summary>
/// Tames <see cref="DebilitatePower"/> (applied by the Necrobinder's Debilitate), which in
/// vanilla amplifies Vulnerable on every powered hit against the debuffed owner and Weak
/// on every powered hit the debuffed owner throws - regardless of who is on the other end
/// of the swing. In multiplayer that means a teammate who never played Debilitate still
/// benefits from (and suffers) the amplified multipliers, which is out of line with how
/// other applier-gated multiplayer debuffs behave.
///
/// We gate the amplification to the applier on both sides:
/// <list type="bullet">
///   <item>Vulnerable boost only fires when the hit on the owner is dealt by the applier.</item>
///   <item>Weak boost only fires when the owner's outgoing hit is aimed at the applier.</item>
/// </list>
/// The base Vulnerable/Weak multipliers (and the 10/12 attack damage on the card) are
/// untouched; we only strip the Debilitate-specific boost when the relevant creature isn't
/// the applier. That mirrors how <see cref="FlankingPower"/>'s own
/// <c>ModifyDamageMultiplicative</c> already short-circuits on <c>dealer == Applier</c>.
///
/// Caveat: <see cref="DebilitatePower"/> uses <c>PowerStackType.Counter</c> and does not
/// override <c>AfterApplied</c>/<c>IsInstanced</c>, so if multiple players apply Debilitate
/// to the same enemy the stacks merge and only one of them will match <c>Applier</c>. The
/// common case - one player applies, that player's hits are amplified - is what we scope
/// this patch to.
/// </summary>
[HarmonyPatch(typeof(DebilitatePower), nameof(DebilitatePower.ModifyVulnerableMultiplier))]
public static class DebilitateVulnerableApplierOnlyPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        DebilitatePower __instance,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        ref decimal __result)
    {
        if (__result == amount)
        {
            return;
        }

        if (dealer == __instance.Applier)
        {
            return;
        }

        __result = amount;
    }
}

/// <summary>
/// Second half of the Debilitate nerf - see <see cref="DebilitateVulnerableApplierOnlyPatch"/>
/// for the full write-up. This one gates the Weak amplification to attacks the debuffed
/// owner makes against the applier; swings at any other target see vanilla Weak only.
/// </summary>
[HarmonyPatch(typeof(DebilitatePower), nameof(DebilitatePower.ModifyWeakMultiplier))]
public static class DebilitateWeakApplierOnlyPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        DebilitatePower __instance,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        ref decimal __result)
    {
        if (__result == amount)
        {
            return;
        }

        if (target == __instance.Applier)
        {
            return;
        }

        __result = amount;
    }
}
