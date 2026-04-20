using BaseLib.Config;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode;

/// <summary>
/// Player-facing toggles for each patch group. BaseLib auto-generates a checkbox per
/// <c>bool</c> property in the in-game Mod Configuration menu (main menu → Settings).
/// Defaults are all <c>true</c> so existing installs see no behaviour change after upgrade.
///
/// Multiplayer note: STS2 runs lockstep-deterministic co-op and cross-checks state with
/// <c>ChecksumTracker</c>. Rule-affecting patches must therefore execute on every client,
/// which means **all players in a co-op run must have the same toggles set** — otherwise
/// the host and client compute divergent state and the run errors out. An earlier
/// host-authoritative gate here caused exactly that divergence, so we intentionally do
/// not gate by role. The Flanking/Knockdown text-override patches are client-local
/// (cosmetic only), so mismatches there are cosmetic rather than fatal.
/// </summary>
public class BalancerConfig : SimpleModConfig
{
    public static bool TeamDeathEnabled { get; set; } = true;
    public static bool FlankingNerfEnabled { get; set; } = true;
    public static bool KnockdownNerfEnabled { get; set; } = true;
    public static bool EnemyDoublingEnabled { get; set; } = true;
}
