using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.Enemies;

/// <summary>
/// Doubles the enemy roster for every non-boss combat. The original generation logic
/// (each <see cref="EncounterModel"/> subclass's <c>GenerateMonsters</c>) is left
/// untouched; we just append a freshly-cloned mutable copy of every monster after
/// <see cref="EncounterModel.GenerateMonstersWithSlots"/> has finished populating
/// the encounter's monster list.
///
/// Boss rooms (<see cref="RoomType.Boss"/>) are skipped intentionally: doubling a
/// boss would invalidate scripted encounters and break victory conditions for the
/// act. Everything else - normal monsters, elites, and combat events whose
/// underlying <see cref="EncounterModel"/> reports a non-boss <c>RoomType</c> - is
/// in scope.
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
        return encounter.RoomType != RoomType.Boss;
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
