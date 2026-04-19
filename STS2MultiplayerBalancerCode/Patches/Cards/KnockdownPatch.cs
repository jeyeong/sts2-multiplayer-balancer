using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.Cards;

/// <summary>
/// Tames <see cref="KnockdownPower"/> (applied by the Colorless Knockdown), which is
/// considered broken because in vanilla every stack acts as a multiplier that
/// fires on every powered attack from a non-applier until end of turn, chaining into
/// very large damage swings.
///
/// We restrict it to a single payoff: the first powered attack from a non-applier that
/// actually lands on the owner consumes the power, exactly mirroring the conditions in
/// <c>KnockdownPower.ModifyDamageMultiplicative</c>. The damage modification itself is
/// left untouched, so the multiplier value (and any upgrade behaviour) keeps working as
/// designed.
/// </summary>
internal static class KnockdownNerfHelpers
{
    /// <summary>
    /// Same gating predicate as <see cref="KnockdownPower"/>'s own
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
/// Postfix on <see cref="AbstractModel.AfterDamageReceived"/>. <see cref="KnockdownPower"/>
/// doesn't override this hook, so dispatch falls through to the base virtual and our
/// patch sees every invocation for it. We filter aggressively up-front so the runtime
/// cost for every other model in the game is a single type check.
///
/// We chain a continuation onto the original task (rather than awaiting eagerly) so that
/// the original work order is preserved and the framework still observes a single Task
/// completing in the correct sequence relative to other hooks.
/// </summary>
[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.AfterDamageReceived))]
public static class KnockdownPowerAfterDamageReceivedPatch
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
        if (__instance is not KnockdownPower power)
        {
            return;
        }

        if (!BalancerConfig.KnockdownNerfEnabled)
        {
            return;
        }

        if (!KnockdownNerfHelpers.ShouldConsume(power, target, props, dealer))
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

/// <summary>
/// Reflects the gameplay nerf in the displayed text for the Knockdown card and the
/// <see cref="KnockdownPower"/> debuff. The vanilla strings say the multiplier lasts
/// "this turn"; once we limit the payoff to a single hit, that wording is misleading,
/// so we rewrite it to "on the next attack".
///
/// We intercept at <see cref="LocTable.GetRawText"/>, which is the single chokepoint
/// that <c>LocString.GetRawText</c> / <c>LocManager.SmartFormat</c> route through for
/// both card and power lookups. Filtering by exact key name keeps the override scoped
/// (the keys are unique across tables) and lets SmartFormat continue to splice in
/// dynamic vars like <c>{Amount}</c> from the original template.
///
/// English-only by design: the substring swap silently no-ops in other languages,
/// which is acceptable here since the rest of the mod is also English-only.
/// </summary>
[HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
public static class KnockdownTextOverridePatch
{
    private static readonly HashSet<string> TargetKeys = new()
    {
        "KNOCKDOWN.description",
        "KNOCKDOWN_POWER.description",
        "KNOCKDOWN_POWER.smartDescription",
        "KNOCKDOWN_POWER.remoteDescription",
    };

    private const string OriginalPhrase = "this turn";
    private const string ReplacementPhrase = "on the next attack";

    [HarmonyPostfix]
    public static void Postfix(string key, ref string __result)
    {
        if (!TargetKeys.Contains(key))
        {
            return;
        }

        if (!BalancerConfig.KnockdownNerfEnabled)
        {
            return;
        }

        if (string.IsNullOrEmpty(__result))
        {
            return;
        }

        __result = __result.Replace(OriginalPhrase, ReplacementPhrase);
    }
}
