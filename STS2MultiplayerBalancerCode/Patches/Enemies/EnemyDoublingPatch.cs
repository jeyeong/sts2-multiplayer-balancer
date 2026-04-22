using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Config;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.Enemies;

/// <summary>
/// Doubles the enemy roster for every non-boss combat and, to keep the combat
/// math fair, halves every monster's outgoing damage and scales their max HP
/// down to 0.75x in the same encounters. The original generation logic (each
/// <see cref="EncounterModel"/> subclass's <c>GenerateMonsters</c>) is left
/// untouched; we just append a freshly-cloned mutable copy of every monster
/// after <see cref="EncounterModel.GenerateMonstersWithSlots"/> has finished
/// populating the encounter's monster list.
///
/// Boss rooms (<see cref="RoomType.Boss"/>) are skipped intentionally for every
/// part of the patch: doubling a boss would invalidate scripted encounters and
/// break victory conditions for the act, and conversely we don't want to nerf
/// boss damage or HP when we haven't given the player extra targets. Everything
/// else - normal monsters, elites, and combat events whose underlying
/// <see cref="EncounterModel"/> reports a non-boss <c>RoomType</c> - is in scope
/// by default; elite rooms can additionally be opted out of via
/// <see cref="Config.BalancerSettings.EnemyDoublingDoubleElitesEnabled"/> so
/// players can keep doubled normals without the doubled elite pairings.
///
/// Slot assignment for the duplicates mirrors the slot of the source monster so we
/// never reference a slot the encounter scene doesn't define (which would NRE in
/// <c>NCombatRoom.AddCreature</c>). The visual overlap that this introduces for
/// scene-anchored encounters is corrected by
/// <see cref="EncounterPositionCreaturesWithSlotsPatch"/>.
/// </summary>
internal static class EnemyDoublingHelpers
{
    /// <summary>
    /// Fraction of the vanilla max HP we leave monsters at in doubled encounters.
    /// Twice as many enemies with half the damage still adds up to a much
    /// longer grind than vanilla, so we trim HP a bit to keep fights snappy
    /// without erasing the "more bodies" feel of the doubled roster.
    /// </summary>
    internal const decimal MonsterHpScale = 0.75m;

    /// <summary>
    /// Symmetric counterpart to the doubling: with twice the monsters in the
    /// room, we want each one to hit for half as hard. Rounding up keeps tiny
    /// (1-damage) attacks from disappearing entirely after the halving and also
    /// preserves the multi-hit feel of moves like the Looter triple-strike.
    /// </summary>
    internal static decimal HalveOutgoingDamage(decimal amount)
    {
        if (amount <= 0m)
        {
            return amount;
        }

        return Math.Ceiling(amount / 2m);
    }

    /// <summary>
    /// Applies <see cref="MonsterHpScale"/> to a monster's max HP. Rounds up
    /// (and enforces a floor of 1) so 1-HP creatures don't scale to 0 and
    /// instantly die before the combat scene even finishes loading.
    /// </summary>
    internal static int ScaleMonsterMaxHp(int maxHp)
    {
        if (maxHp <= 1)
        {
            return maxHp;
        }

        int scaled = (int)Math.Ceiling(maxHp * MonsterHpScale);
        return Math.Max(1, scaled);
    }

    /// <summary>
    /// True when the dealer is a monster fighting in an encounter we also
    /// doubled. We deliberately mirror <see cref="ShouldDouble"/>'s gate so the
    /// two halves of the patch can never disagree about which fights to touch.
    /// Damage with no dealer (e.g. environment / scripted hits) and damage from
    /// player-side creatures is left alone.
    ///
    /// Player-side summons (Osty, Byrdpip, Pael's Legion, ...) are <see cref="MonsterModel"/>
    /// instances too, so <c>dealer.Monster != null</c> is not enough to separate
    /// them from hostile monsters. The engine tags every summon with a non-null
    /// <see cref="Creature.PetOwner"/> (surfaced via <see cref="Creature.IsPet"/>),
    /// and those creatures are spawned by card/relic effects rather than by the
    /// encounter's monster list, so our doubling never touches them. Excluding
    /// pets here keeps their damage at full strength without affecting any
    /// hostile monster.
    /// </summary>
    internal static bool ShouldHalveDamageFrom(Creature? dealer)
    {
        if (dealer?.Monster == null)
        {
            return false;
        }

        if (dealer.IsPet)
        {
            return false;
        }

        EncounterModel? encounter = dealer.CombatState?.Encounter;
        if (encounter == null)
        {
            return false;
        }

        return ShouldDouble(encounter);
    }

    /// <summary>
    /// Backing field on <see cref="EncounterModel"/> that stores the generated
    /// monster list. The class only exposes a getter for the public property, so
    /// we rewrite the field directly via Harmony's reflective accessor.
    /// </summary>
    private static readonly FieldInfo MonstersWithSlotsField =
        AccessTools.Field(typeof(EncounterModel), "_monstersWithSlots")
        ?? throw new InvalidOperationException(
            "EncounterModel._monstersWithSlots field not found; enemy doubling patch cannot install.");

    internal static bool ShouldDouble(EncounterModel encounter)
    {
        // Also acts as the on/off gate for every sibling patch in this file:
        // each of them early-outs whenever ShouldDouble does, so flipping the
        // user toggle off here disables doubling, damage halving, HP trimming
        // and the duplicate-spread placement in a single place.
        if (!BalancerSettings.EnemyDoublingEnabled)
        {
            return false;
        }

        if (encounter.RoomType == RoomType.Boss)
        {
            return false;
        }

        // Elite rooms are optionally excluded so players can keep the doubled
        // normals without the tougher elite pairings. Gated on the sub-toggle
        // so the common case (both toggles on) still doubles elites too.
        if (encounter.RoomType == RoomType.Elite &&
            !BalancerSettings.EnemyDoublingDoubleElitesEnabled)
        {
            return false;
        }

        return true;
    }

    internal static void DoubleMonsters(EncounterModel encounter)
    {
        if (MonstersWithSlotsField.GetValue(encounter) is not IReadOnlyList<(MonsterModel, string?)> original)
        {
            return;
        }

        if (original.Count == 0)
        {
            return;
        }

        List<(MonsterModel, string?)> doubled = new(original.Count * 2);
        foreach ((MonsterModel monster, string? slot) entry in original)
        {
            doubled.Add(entry);

            MonsterModel? duplicate = TryClone(entry.monster);
            if (duplicate == null)
            {
                continue;
            }

            doubled.Add((duplicate, entry.slot));
        }

        MonstersWithSlotsField.SetValue(encounter, doubled);
    }

    /// <summary>
    /// Always clone from the canonical instance so the duplicate starts in the
    /// same pristine state any other freshly-generated mutable monster would
    /// have. Cloning the already-mutable instance instead can carry over
    /// encounter-specific tweaks (e.g. <c>Nibbit.IsFront</c>) that we don't want
    /// to copy into the spawned twin.
    /// </summary>
    private static MonsterModel? TryClone(MonsterModel source)
    {
        try
        {
            MonsterModel canonical = source.CanonicalInstance;
            return canonical.ToMutable();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn(
                $"[{nameof(EnemyDoublingHelpers)}] Failed to clone monster {source.Id}; skipping duplicate. {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Postfix on <see cref="EncounterModel.GenerateMonstersWithSlots"/>. The original
/// method does the heavy lifting (RNG seeding, calling the encounter's own
/// <c>GenerateMonsters</c>, asserting mutability) and leaves the result in the
/// private <c>_monstersWithSlots</c> field. Running our doubling logic afterward
/// keeps every existing encounter implementation oblivious to the patch and means
/// we don't have to special-case any individual encounter.
/// </summary>
[HarmonyPatch(typeof(EncounterModel), nameof(EncounterModel.GenerateMonstersWithSlots))]
public static class EncounterGenerateMonstersWithSlotsPatch
{
    [HarmonyPostfix]
    public static void Postfix(EncounterModel __instance)
    {
        if (!EnemyDoublingHelpers.ShouldDouble(__instance))
        {
            return;
        }

        EnemyDoublingHelpers.DoubleMonsters(__instance);
    }
}

/// <summary>
/// Spreads creatures that share a slot horizontally, so duplicates introduced by
/// <see cref="EncounterGenerateMonstersWithSlotsPatch"/> remain individually
/// targetable in scene-anchored encounters.
///
/// Vanilla <c>NCombatRoom.PositionCreaturesWithSlots</c> snaps every enemy to its
/// slot's <c>Marker2D</c>, so two creatures with the same slot would land exactly
/// on top of each other - hover, click, and intent UI would only ever address the
/// topmost one. We re-run after the original placement and walk each over-occupied
/// slot, leaving the first creature on the marker and offsetting the rest to the
/// right by their own visual bounds (with a small gap), keeping the cluster
/// roughly centred on the original anchor.
///
/// Sceneless encounters route through <c>NCombatRoom.PositionEnemies</c> instead,
/// which already lays out an arbitrary number of creatures evenly, so no patch is
/// needed there.
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), "PositionCreaturesWithSlots")]
public static class EncounterPositionCreaturesWithSlotsPatch
{
    private const float DuplicateGapPx = 25f;

    [HarmonyPostfix]
    public static void Postfix(List<NCreature> creatures)
    {
        if (creatures.Count <= 1)
        {
            return;
        }

        IEnumerable<IGrouping<string?, NCreature>> bySlot = creatures
            .Where(c => c.Entity.SlotName != null)
            .GroupBy(c => c.Entity.SlotName);

        foreach (IGrouping<string?, NCreature> group in bySlot)
        {
            List<NCreature> sharing = group.ToList();
            if (sharing.Count <= 1)
            {
                continue;
            }

            SpreadHorizontally(sharing);
        }
    }

    private static void SpreadHorizontally(List<NCreature> sharing)
    {
        Vector2 anchor = sharing[0].GlobalPosition;

        float totalWidth = sharing.Sum(c => GetWidth(c)) + DuplicateGapPx * (sharing.Count - 1);
        float cursorX = anchor.X - totalWidth * 0.5f;

        foreach (NCreature creature in sharing)
        {
            float width = GetWidth(creature);
            float centerX = cursorX + width * 0.5f;

            creature.GlobalPosition = new Vector2(centerX, anchor.Y);
            cursorX += width + DuplicateGapPx;
        }
    }

    /// <summary>
    /// Falls back to a sensible default if the visuals haven't reported a width
    /// yet (the bounds Control is populated lazily via spine bounds updates), so
    /// we never compute a zero-width layout that stacks duplicates back onto each
    /// other.
    /// </summary>
    private static float GetWidth(NCreature creature)
    {
        float width = creature.Visuals?.Bounds?.Size.X ?? 0f;
        return width > 1f ? width : 200f;
    }
}

/// <summary>
/// Halves the actual damage dealt by every monster attack in a doubled
/// encounter. We hook the deepest <see cref="CreatureCmd.Damage"/> overload -
/// every other <c>Damage</c> entry point funnels into it - so the patch covers
/// melee attacks, ranged attacks, and any homemade damage paths an encounter
/// might add, with a single touch point.
///
/// Halving the input <c>amount</c> (rather than e.g. post-block) means damage
/// modifiers chained onto <c>Hook.ModifyDamage</c> downstream (Vulnerable,
/// player-side defensive powers, etc.) still scale relative to the new base, so
/// the relationship between "raw monster swing" and "after debuffs" stays the
/// same as vanilla, just at half the magnitude.
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
    typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal),
    typeof(ValueProp), typeof(Creature), typeof(CardModel))]
public static class CreatureCmdDamageHalvingPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref decimal amount, Creature? dealer)
    {
        if (!EnemyDoublingHelpers.ShouldHalveDamageFrom(dealer))
        {
            return;
        }

        amount = EnemyDoublingHelpers.HalveOutgoingDamage(amount);
    }
}

/// <summary>
/// Keeps the damage shown on enemy intents in sync with the halved hits they
/// actually land. We only need to postfix <see cref="AttackIntent.GetSingleDamage"/>:
/// both shipped attack intents (<see cref="SingleAttackIntent"/>,
/// <see cref="MultiAttackIntent"/>) compute <c>GetTotalDamage</c> by delegating
/// to <c>GetSingleDamage</c>, so halving the single damage automatically feeds
/// into the tier/art swap (the "small/medium/large/huge attack" icon, driven by
/// <see cref="AttackIntent.GetTotalDamage"/> via
/// <see cref="AttackIntent.GetTexture"/>) as well as the tooltip number. The
/// actual damage path is halved separately in
/// <see cref="CreatureCmdDamageHalvingPatch"/>.
///
/// We intentionally do NOT patch <c>GetTotalDamage</c> directly:
/// <list type="bullet">
///   <item><description>As of STS2 v0.103.2 it is <c>abstract</c> on
///     <see cref="AttackIntent"/>, and Harmony refuses to patch abstract methods
///     ("Abstract methods cannot be prepared"), which previously crashed the
///     entire <c>PatchAll</c> and wiped out every other patch in this mod.</description></item>
///   <item><description>Even if we walked every concrete override, patching those
///     would double-halve damage for the shipped intents (the override already
///     sees the halved <c>GetSingleDamage</c>), quartering the displayed value.</description></item>
/// </list>
///
/// Owner-side filtering reuses <see cref="EnemyDoublingHelpers.ShouldHalveDamageFrom"/>
/// so a monster that somehow exists outside a doubled encounter (test scenes,
/// future boss exemptions, etc.) keeps showing its true damage.
/// </summary>
[HarmonyPatch(typeof(AttackIntent), nameof(AttackIntent.GetSingleDamage))]
public static class AttackIntentDisplayHalvingPatch
{
    [HarmonyPostfix]
    public static void Postfix(Creature owner, ref int __result)
    {
        if (__result <= 0)
        {
            return;
        }

        if (!EnemyDoublingHelpers.ShouldHalveDamageFrom(owner))
        {
            return;
        }

        __result = (int)EnemyDoublingHelpers.HalveOutgoingDamage(__result);
    }
}

/// <summary>
/// Scales every freshly-spawned enemy's max HP down to
/// <see cref="EnemyDoublingHelpers.MonsterHpScale"/> of its vanilla value in
/// doubled encounters. We postfix <see cref="CombatState.CreateCreature"/>
/// because it is the single choke point for enemy instantiation (every enemy
/// in <c>CombatRoom.StartCombat</c> is funneled through it) and, crucially, it
/// runs <i>after</i> both <see cref="Creature.SetUniqueMonsterHpValue"/> and
/// <see cref="Creature.ScaleMonsterHpForMultiplayer"/>, so our factor stacks
/// on top of the game's own per-monster HP randomisation and any multiplayer
/// scaling rather than being overwritten by them.
///
/// Gating on <see cref="EnemyDoublingHelpers.ShouldDouble"/> keeps the HP trim
/// exactly aligned with the doubling/damage-halving patches: bosses (and
/// anything else we chose not to double) still fight at full vanilla HP, so
/// scripted boss encounters aren't invalidated by a surprise health cut.
///
/// Player-side creatures (<c>side == CombatSide.Player</c>) skip the branch
/// that runs monster-HP setup in the original method and naturally fall
/// through our filter too, since we read the encounter off <c>__instance</c>
/// rather than off the creature.
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.CreateCreature))]
public static class CombatStateCreateCreatureHpScalingPatch
{
    [HarmonyPostfix]
    public static void Postfix(CombatState __instance, CombatSide side, Creature __result)
    {
        if (side != CombatSide.Enemy)
        {
            return;
        }

        EncounterModel? encounter = __instance.Encounter;
        if (encounter == null || !EnemyDoublingHelpers.ShouldDouble(encounter))
        {
            return;
        }

        int scaled = EnemyDoublingHelpers.ScaleMonsterMaxHp(__result.MaxHp);
        if (scaled == __result.MaxHp)
        {
            return;
        }

        __result.SetMaxHpInternal(scaled);
        __result.SetCurrentHpInternal(scaled);
    }
}
