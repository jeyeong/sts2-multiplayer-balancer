using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Config;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.TeamDeath;

/// <summary>
/// In a multiplayer run, the death of a single player should end the whole team's run.
///
/// Vanilla Slay the Spire 2 only considers the run "game over" once every hero is dead
/// (<see cref="RunState.IsGameOver"/>) and only asks <see cref="MegaCrit.Sts2.Core.Combat.CombatManager"/>
/// to lose combat from inside <c>CreatureCmd.Kill</c> when <c>Players.All(p =&gt; p.Creature.IsDead)</c>
/// is true. We override both of those checks so a single dead hero is sufficient to
/// trigger combat loss and bring everyone to the Game Over screen.
///
/// Single player runs are intentionally left untouched: <c>Players.Count == 1</c> means
/// "any dead" is equivalent to "all dead", so our check collapses to the vanilla behaviour.
/// </summary>
internal static class TeamDeathHelpers
{
    internal static bool TeamShouldBeConsideredDead(IRunState? runState)
    {
        // Single gate for both `RunState.IsGameOver` and the `CreatureCmd.Kill`
        // predicate patch below; flipping the user toggle off restores vanilla
        // team-death behaviour (everyone has to die).
        if (!BalancerSettings.TeamDeathEnabled)
        {
            return false;
        }

        if (runState == null)
        {
            return false;
        }

        IReadOnlyList<Player> players = runState.Players;
        return players.Count > 1 && players.Any(p => p.Creature.IsDead);
    }
}

/// <summary>
/// Redefines the run's "game over" flag: in multiplayer, a dead teammate means the whole
/// run is over. This is what gates the Game Over screen, post-run flow, and various
/// multiplayer synchronizers.
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.IsGameOver), MethodType.Getter)]
public static class RunStateIsGameOverPatch
{
    [HarmonyPrefix]
    public static bool Prefix(RunState __instance, ref bool __result)
    {
        if (TeamDeathHelpers.TeamShouldBeConsideredDead(__instance))
        {
            __result = true;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Patches the compiler-generated lambda <c>CreatureCmd.&lt;&gt;c.&lt;Kill&gt;b__14_1</c>
/// (the predicate handed to <c>Players.All(...)</c> inside <c>CreatureCmd.Kill</c>).
///
/// When that <c>All</c> returns true the game immediately calls
/// <c>CombatManager.Instance.LoseCombat()</c>, so by making the predicate report "this
/// player is effectively dead" as soon as any teammate has fallen, we trigger the normal
/// loss path the moment the first hero dies in a multiplayer run.
///
/// The lambda is referenced by a compiler-mangled name so we resolve it reflectively to
/// stay resilient to unrelated recompiles of the game assembly.
/// </summary>
[HarmonyPatch]
public static class CreatureCmdKillPredicatePatch
{
    private const string CreatureCmdFullName = "MegaCrit.Sts2.Core.Commands.CreatureCmd";

    public static MethodBase? TargetMethod()
    {
        Type? cmdType = AccessTools.TypeByName(CreatureCmdFullName);
        if (cmdType == null)
        {
            MainFile.Logger.Warn($"[{nameof(CreatureCmdKillPredicatePatch)}] Could not locate {CreatureCmdFullName}; team-death patch inactive.");
            return null;
        }

        Type? displayClass = cmdType.GetNestedType("<>c", BindingFlags.NonPublic);
        if (displayClass == null)
        {
            MainFile.Logger.Warn($"[{nameof(CreatureCmdKillPredicatePatch)}] Could not locate {CreatureCmdFullName}+<>c; team-death patch inactive.");
            return null;
        }

        MethodInfo? lambda = displayClass
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m =>
                m.Name.StartsWith("<Kill>b__", StringComparison.Ordinal) &&
                m.ReturnType == typeof(bool) &&
                m.GetParameters() is { Length: 1 } parameters &&
                parameters[0].ParameterType == typeof(Player));

        if (lambda == null)
        {
            MainFile.Logger.Warn($"[{nameof(CreatureCmdKillPredicatePatch)}] Could not find <Kill>b__* predicate lambda; team-death patch inactive.");
        }

        return lambda;
    }

    [HarmonyPrefix]
    public static bool Prefix(Player p, ref bool __result)
    {
        if (TeamDeathHelpers.TeamShouldBeConsideredDead(p.RunState))
        {
            __result = true;
            return false;
        }

        return true;
    }
}
