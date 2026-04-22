using System;
using System.IO;
using System.Reflection;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Config;

/// <summary>
/// Persistent on/off toggles for the mod's tunable features. The settings are
/// surfaced in the in-game General settings panel and mirrored to a simple INI
/// file that lives next to the mod DLL, so player choices survive relaunches
/// without requiring any external config editor.
///
/// Each feature defaults to <c>true</c> so a fresh install behaves identically
/// to prior versions of the mod - the toggles only exist to let players opt out
/// of individual balancing systems.
/// </summary>
internal static class BalancerSettings
{
    private const string ModFolderName = "STS2MultiplayerBalancer";

    private const string ConfigFileName = "config.ini";

    private const string FeaturesSection = "features";

    private const string CardModsKey = "card_mods";

    private const string EnemyDoublingKey = "enemy_doubling";

    private const string TeamDeathKey = "team_death";

    /// <summary>
    /// Gates the Debilitate / Flanking / Knockdown rebalancing patches as a
    /// single group. They all nerf cross-player multiplier behaviour and make
    /// sense to toggle together.
    /// </summary>
    public static bool CardModsEnabled { get; set; } = true;

    /// <summary>
    /// Gates the non-boss encounter doubling system (extra monsters, halved
    /// damage, trimmed HP, visual spread).
    /// </summary>
    public static bool EnemyDoublingEnabled { get; set; } = true;

    /// <summary>
    /// Gates the "any teammate dead ⇒ whole run ends" override.
    /// </summary>
    public static bool TeamDeathEnabled { get; set; } = true;

    private static string? ConfigFilePath { get; set; }

    /// <summary>
    /// Loads settings from disk on first call, creating the file with defaults
    /// if it doesn't exist yet. Safe to call multiple times - subsequent calls
    /// just re-read the file.
    /// </summary>
    public static void Load()
    {
        string modDirectory = ResolveModDirectory();
        Directory.CreateDirectory(modDirectory);
        ConfigFilePath = Path.Combine(modDirectory, ConfigFileName);

        if (!File.Exists(ConfigFilePath))
        {
            Save();
            return;
        }

        try
        {
            ParseIni(ConfigFilePath);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{nameof(BalancerSettings)}] Failed to parse {ConfigFilePath}: {ex.Message}. Rewriting with defaults.");
            Save();
        }
    }

    /// <summary>
    /// Writes the current in-memory toggle state out to disk. Called from the
    /// settings UI whenever the player changes a value so we never lose a tweak
    /// even if the game process is killed before a clean shutdown.
    /// </summary>
    public static void Save()
    {
        if (string.IsNullOrEmpty(ConfigFilePath))
        {
            return;
        }

        try
        {
            using StreamWriter writer = new(ConfigFilePath, append: false);
            writer.WriteLine($"[{FeaturesSection}]");
            writer.WriteLine($"{CardModsKey}={BoolToString(CardModsEnabled)}");
            writer.WriteLine($"{EnemyDoublingKey}={BoolToString(EnemyDoublingEnabled)}");
            writer.WriteLine($"{TeamDeathKey}={BoolToString(TeamDeathEnabled)}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{nameof(BalancerSettings)}] Failed to save config: {ex.Message}");
        }
    }

    private static void ParseIni(string path)
    {
        string currentSection = "";
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (currentSection != FeaturesSection)
            {
                continue;
            }

            switch (key)
            {
                case CardModsKey:
                    CardModsEnabled = ParseBool(value, CardModsEnabled);
                    break;
                case EnemyDoublingKey:
                    EnemyDoublingEnabled = ParseBool(value, EnemyDoublingEnabled);
                    break;
                case TeamDeathKey:
                    TeamDeathEnabled = ParseBool(value, TeamDeathEnabled);
                    break;
            }
        }
    }

    private static bool ParseBool(string value, bool fallback)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
            value == "1")
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
            value == "0")
        {
            return false;
        }

        return fallback;
    }

    private static string BoolToString(bool value)
    {
        return value ? "true" : "false";
    }

    /// <summary>
    /// Mirrors the resolution strategy used by other StS2 mods: prefer the
    /// directory the loaded assembly already lives in (this is the mods folder
    /// at runtime), then fall back to an <c>AppContext.BaseDirectory\mods\</c>
    /// layout, and finally to a per-user AppData path so the config has
    /// somewhere writeable even in odd install configurations.
    /// </summary>
    private static string ResolveModDirectory()
    {
        string? assemblyLocation = Assembly.GetExecutingAssembly().Location;
        string? assemblyDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? null
            : Path.GetDirectoryName(assemblyLocation);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory) && Directory.Exists(assemblyDirectory))
        {
            return assemblyDirectory;
        }

        string fallbackModDirectory = Path.Combine(AppContext.BaseDirectory, "mods", ModFolderName);
        if (Directory.Exists(fallbackModDirectory))
        {
            return fallbackModDirectory;
        }

        string appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataRoot, "StS2Mods", ModFolderName);
    }
}
