using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.CardNerfs;

/// <summary>
/// Tames <see cref="FlankingPower"/> (applied by the Silent's Flanking), which is
/// considered broken because in vanilla every stack acts as a multiplier that fires on
/// every powered attack from a non-applier until end of turn, chaining into very large
/// damage swings.
///
/// We restrict it to a single payoff: the first powered attack from a non-applier that
/// actually lands on the owner consumes the power, exactly mirroring the conditions in
/// <c>FlankingPower.ModifyDamageMultiplicative</c>. The damage modification itself is
/// left untouched, so the multiplier value (and any upgrade behaviour) keeps working as
/// designed.
/// </summary>
internal static class FlankingNerfHelpers
{
    /// <summary>
    /// Same gating predicate as <see cref="FlankingPower"/>'s own
    /// <c>ModifyDamageMultiplicative</c>: the hit must be on the power's owner, must be
    /// a powered attack, and must come from someone other than the player who applied
    /// the debuff. Anything else leaves the power in place so it can still pay off on a
    /// "real" qualifying hit.
    /// </summary>
    internal static bool ShouldConsume(PowerModel power, Creature target, ValueProp props, Creature? dealer)
    {
        if (target != power.Owner)
        {
            return false;
        }

        if (!props.IsPoweredAttack())
        {
            return false;
        }

        if (dealer == null || dealer == power.Applier)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Postfix on <see cref="AbstractModel.AfterDamageReceived"/>. <see cref="FlankingPower"/>
/// doesn't override this hook, so dispatch falls through to the base virtual and our
/// patch sees every invocation for it. We filter aggressively up-front so the runtime
/// cost for every other model in the game is a single type check.
///
/// We chain a continuation onto the original task (rather than awaiting eagerly) so that
/// the original work order is preserved and the framework still observes a single Task
/// completing in the correct sequence relative to other hooks.
/// </summary>
[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.AfterDamageReceived))]
public static class FlankingPowerAfterDamageReceivedPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        AbstractModel __instance,
        PlayerChoiceContext choiceContext,
        Creature target,
        ValueProp props,
        Creature? dealer)
    {
        if (__instance is not FlankingPower power)
        {
            return;
        }

        if (!FlankingNerfHelpers.ShouldConsume(power, target, props, dealer))
        {
            return;
        }

        __result = ConsumePowerAfter(__result, power);
    }

    private static async Task ConsumePowerAfter(Task original, PowerModel power)
    {
        await original;
        await PowerCmd.Remove(power);
    }
}
