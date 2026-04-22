using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Config;

namespace STS2MultiplayerBalancer.STS2MultiplayerBalancerCode.Patches.Settings;

/// <summary>
/// Injects a dedicated section into the vanilla General settings panel that
/// exposes this mod's feature toggles (Card mods / Enemy doubling / Team
/// death) as the same OFF/ON paginators the base game uses for its own
/// settings.
///
/// The approach is lifted from the <c>sts2-RMP</c> mod's settings injection
/// (which is the only widely-used pattern we found for bolting custom rows
/// onto <see cref="NSettingsPanel"/> without rewriting the whole screen):
/// the <c>screens/paginator.tscn</c> template is instantiated, its visual
/// children are re-parented onto a freshly constructed <see cref="NPaginator"/>,
/// the result is dropped into the General panel's VBox, and index changes are
/// caught via a postfix on the internal <c>OnIndexChanged</c> hook.
///
/// Several <c>NPaginator</c> / <see cref="NSettingsPanel"/> members the patch
/// needs to reach are non-public, and the project intentionally keeps
/// Publicize disabled (see <c>STS2MultiplayerBalancer.csproj</c>). We pull them
/// via Harmony's reflective accessors so toggling Publicize doesn't change the
/// surface area or break the patch.
///
/// This patch intentionally stays cosmetic: flipping a toggle updates
/// <see cref="BalancerSettings"/> in memory and persists to disk, and every
/// gameplay patch reads <see cref="BalancerSettings"/> fresh on each invocation,
/// so the effect is visible in-game the moment the player backs out of the
/// settings screen.
/// </summary>
internal enum ModToggleKind
{
    CardMods,
    EnemyDoubling,
    TeamDeath,
}

internal static class ModSettingsInjectionHelpers
{
    /// <summary>
    /// Soft divider above our injected block so the mod's rows read as a
    /// distinct section rather than blending into the vanilla options.
    /// Matches the alpha/colour profile used by the RMP mod.
    /// </summary>
    internal static readonly Color SectionDividerColor = new(0.91f, 0.86f, 0.75f, 0.25f);

    internal static readonly FieldInfo? PaginatorOptionsField =
        AccessTools.Field(typeof(NPaginator), "_options");

    internal static readonly FieldInfo? PaginatorCurrentIndexField =
        AccessTools.Field(typeof(NPaginator), "_currentIndex");

    internal static readonly FieldInfo? PaginatorLabelField =
        AccessTools.Field(typeof(NPaginator), "_label");

    internal static readonly MethodInfo? GetSettingsOptionsMethod =
        AccessTools.Method(typeof(NSettingsPanel), "GetSettingsOptionsRecursive");

    internal static readonly FieldInfo? PanelFirstControlField =
        AccessTools.Field(typeof(NSettingsPanel), "_firstControl");

    /// <summary>
    /// Live registry of every <see cref="NPaginator"/> we inject, mapped back
    /// to the toggle it drives. We need this in the
    /// <c>OnIndexChanged</c> postfix to know which <see cref="BalancerSettings"/>
    /// field to update without relying on node name string matching.
    /// </summary>
    internal static readonly Dictionary<NPaginator, ModToggleKind> Tracked = new();

    internal const string OffLabel = "OFF";

    internal const string OnLabel = "ON";

    internal static bool GetToggleValue(ModToggleKind kind)
    {
        return kind switch
        {
            ModToggleKind.CardMods => BalancerSettings.CardModsEnabled,
            ModToggleKind.EnemyDoubling => BalancerSettings.EnemyDoublingEnabled,
            ModToggleKind.TeamDeath => BalancerSettings.TeamDeathEnabled,
            _ => true,
        };
    }

    internal static void SetToggleValue(ModToggleKind kind, bool enabled)
    {
        switch (kind)
        {
            case ModToggleKind.CardMods:
                BalancerSettings.CardModsEnabled = enabled;
                break;
            case ModToggleKind.EnemyDoubling:
                BalancerSettings.EnemyDoublingEnabled = enabled;
                break;
            case ModToggleKind.TeamDeath:
                BalancerSettings.TeamDeathEnabled = enabled;
                break;
        }
    }
}

/// <summary>
/// Kicks in once the settings screen is built: finds the General panel and
/// appends our toggle rows. Wrapped in a try/catch because a failure here must
/// not crash into the settings screen - the mod can still work via the config
/// file even if the UI injection goes wrong.
/// </summary>
[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready))]
public static class NSettingsScreenReadyInjectModSettingsPatch
{
    [HarmonyPostfix]
    public static void Postfix(NSettingsScreen __instance)
    {
        try
        {
            ModSettingsInjector.Inject(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[{nameof(NSettingsScreenReadyInjectModSettingsPatch)}] Failed to inject mod settings UI: {ex}");
        }
    }
}

/// <summary>
/// Persists any toggle changes as soon as the player backs out of the settings
/// submenu. We also save on every individual index change (see
/// <see cref="NPaginatorOnIndexChangedModSettingsPatch"/>), but re-saving here
/// is cheap and guarantees a consistent on-disk state even if the per-change
/// save somehow failed.
/// </summary>
[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen.OnSubmenuClosed))]
public static class NSettingsScreenSubmenuClosedModSettingsPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        BalancerSettings.Save();
    }
}

/// <summary>
/// Reacts to index changes on our own paginators. The base <c>NPaginator</c>
/// implementation of <c>OnIndexChanged</c> is empty, so the label visual doesn't
/// update on its own: we have to push the new option string into the label
/// ourselves (mirroring RMP). Paginators we didn't register are left untouched
/// so we never interfere with vanilla settings.
/// </summary>
[HarmonyPatch(typeof(NPaginator), "OnIndexChanged")]
public static class NPaginatorOnIndexChangedModSettingsPatch
{
    [HarmonyPostfix]
    public static void Postfix(NPaginator __instance, int index)
    {
        if (!ModSettingsInjectionHelpers.Tracked.TryGetValue(__instance, out ModToggleKind kind))
        {
            return;
        }

        if (ModSettingsInjectionHelpers.PaginatorOptionsField?.GetValue(__instance) is not List<string> options)
        {
            return;
        }

        if (index < 0 || index >= options.Count)
        {
            return;
        }

        if (ModSettingsInjectionHelpers.PaginatorLabelField?.GetValue(__instance) is MegaLabel label)
        {
            label.SetTextAutoSize(options[index]);
        }

        bool enabled = options[index] == ModSettingsInjectionHelpers.OnLabel;
        ModSettingsInjectionHelpers.SetToggleValue(kind, enabled);
        BalancerSettings.Save();
    }
}

internal static class ModSettingsInjector
{
    internal static void Inject(NSettingsScreen screen)
    {
        NSettingsPanel? generalPanel = screen.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
        if (generalPanel == null)
        {
            MainFile.Logger.Warn($"[{nameof(ModSettingsInjector)}] General settings panel not found; skipping mod settings UI injection.");
            return;
        }

        VBoxContainer vbox = generalPanel.Content;

        // Pick an anchor row to paste our rows after: Modding is the cleanest
        // landing point (it's the dedicated mod section in the panel). Fall
        // back to SendFeedback for older builds that don't expose Modding yet.
        Control? anchorNode = screen.GetNodeOrNull<Control>("%Modding")
                              ?? screen.GetNodeOrNull<Control>("%SendFeedback");
        if (anchorNode == null)
        {
            MainFile.Logger.Warn($"[{nameof(ModSettingsInjector)}] No anchor node found; skipping mod settings UI injection.");
            return;
        }

        int insertIndex = anchorNode.GetIndex() + 1;

        // Screenshake is a reliably-present row with the exact label styling
        // we want to clone. If the vanilla layout ever renames it we fall back
        // to whatever label is under the anchor node, which is still close
        // enough stylistically.
        RichTextLabel? templateLabel = vbox.GetNodeOrNull<RichTextLabel>("Screenshake/Label")
                                       ?? anchorNode.GetNodeOrNull<RichTextLabel>("Label");

        ColorRect divider = new()
        {
            Name = "Sts2MultiplayerBalancerDivider",
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = ModSettingsInjectionHelpers.SectionDividerColor,
        };
        vbox.AddChild(divider);
        vbox.MoveChild(divider, insertIndex);

        int nextIndex = insertIndex + 1;

        nextIndex = AddToggleRow(vbox, templateLabel, "Sts2MultiplayerBalancerCardMods", "Card Mods",
            ModToggleKind.CardMods, nextIndex);
        nextIndex = AddToggleRow(vbox, templateLabel, "Sts2MultiplayerBalancerEnemyDoubling",
            "Enemy Doubling (experimental)", ModToggleKind.EnemyDoubling, nextIndex);
        _ = AddToggleRow(vbox, templateLabel, "Sts2MultiplayerBalancerTeamDeath", "Team Death",
            ModToggleKind.TeamDeath, nextIndex);

        RebuildPanelFocusChain(generalPanel);
    }

    /// <summary>
    /// Builds a single label + OFF/ON paginator row styled to match vanilla
    /// general-panel rows, drops it into the VBox at the requested index, and
    /// returns the next slot index callers should use.
    /// </summary>
    private static int AddToggleRow(
        VBoxContainer vbox,
        RichTextLabel? templateLabel,
        string rowName,
        string labelText,
        ModToggleKind kind,
        int insertIndex)
    {
        MarginContainer row = new()
        {
            Name = rowName,
            CustomMinimumSize = new Vector2(0, 64),
        };
        row.AddThemeConstantOverride("margin_left", 12);
        row.AddThemeConstantOverride("margin_top", 0);
        row.AddThemeConstantOverride("margin_right", 12);
        row.AddThemeConstantOverride("margin_bottom", 0);

        if (templateLabel != null)
        {
            RichTextLabel label = (RichTextLabel)templateLabel.Duplicate();
            label.Text = labelText;
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            row.AddChild(label);
        }

        NPaginator? paginator = CreateOnOffPaginator(rowName + "Paginator");
        if (paginator == null)
        {
            MainFile.Logger.Warn($"[{nameof(ModSettingsInjector)}] Failed to build paginator for {kind}; toggle unavailable in UI.");
            return insertIndex;
        }

        row.AddChild(paginator);

        vbox.AddChild(row);
        vbox.MoveChild(row, insertIndex);

        ConfigureOnOffPaginator(paginator, kind);
        return insertIndex + 1;
    }

    /// <summary>
    /// The paginator scene in <c>screens/paginator.tscn</c> is rooted on a
    /// plain <see cref="Control"/> (no <see cref="NPaginator"/> script
    /// attached), so we instantiate it, re-parent the visual children onto a
    /// real <see cref="NPaginator"/>, adopt ownership so unique-name lookups
    /// (<c>%Label</c> / <c>%VfxLabel</c>) resolve against the new root, and
    /// throw the template away. This mirrors the approach used by the RMP mod.
    /// </summary>
    private static NPaginator? CreateOnOffPaginator(string name)
    {
        string scenePath = SceneHelper.GetScenePath("screens/paginator");
        PackedScene? scene = ResourceLoader.Load<PackedScene>(scenePath, null, ResourceLoader.CacheMode.Reuse);
        if (scene == null)
        {
            MainFile.Logger.Warn($"[{nameof(ModSettingsInjector)}] Paginator scene not found at {scenePath}.");
            return null;
        }

        Node template = scene.Instantiate();

        NPaginator paginator = new()
        {
            Name = name,
            CustomMinimumSize = new Vector2(324, 64),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        foreach (Node child in new List<Node>(template.GetChildren()))
        {
            template.RemoveChild(child);
            paginator.AddChild(child);
            AdoptOwnership(child, template, paginator);
        }

        template.Free();
        return paginator;
    }

    /// <summary>
    /// Recursively rewrites any child whose Owner still points at the throw-away
    /// template to instead point at the real paginator. Without this the engine
    /// ignores unique-name (<c>%Foo</c>) lookups that the paginator needs to
    /// locate its own label node.
    /// </summary>
    private static void AdoptOwnership(Node node, Node oldOwner, Node newOwner)
    {
        if (node.Owner == oldOwner)
        {
            node.Owner = newOwner;
        }

        foreach (Node child in node.GetChildren())
        {
            AdoptOwnership(child, oldOwner, newOwner);
        }
    }

    private static void ConfigureOnOffPaginator(NPaginator paginator, ModToggleKind kind)
    {
        if (ModSettingsInjectionHelpers.PaginatorOptionsField?.GetValue(paginator) is not List<string> options)
        {
            return;
        }

        options.Clear();
        options.Add(ModSettingsInjectionHelpers.OffLabel);
        options.Add(ModSettingsInjectionHelpers.OnLabel);

        int currentIndex = ModSettingsInjectionHelpers.GetToggleValue(kind) ? 1 : 0;
        ModSettingsInjectionHelpers.PaginatorCurrentIndexField?.SetValue(paginator, currentIndex);

        if (ModSettingsInjectionHelpers.PaginatorLabelField?.GetValue(paginator) is MegaLabel label)
        {
            label.SetTextAutoSize(options[currentIndex]);
        }

        ModSettingsInjectionHelpers.Tracked[paginator] = kind;
        paginator.TreeExiting += () => ModSettingsInjectionHelpers.Tracked.Remove(paginator);
    }

    /// <summary>
    /// Rebuilds the gamepad/keyboard focus chain so our injected rows are
    /// reachable and don't orphan the focus on the last vanilla row. Without
    /// this the cursor can get stuck at the bottom of the vanilla list and
    /// never reach the mod toggles.
    /// </summary>
    private static void RebuildPanelFocusChain(NSettingsPanel panel)
    {
        if (ModSettingsInjectionHelpers.GetSettingsOptionsMethod == null ||
            ModSettingsInjectionHelpers.PanelFirstControlField == null)
        {
            return;
        }

        List<Control> controls = new();
        ModSettingsInjectionHelpers.GetSettingsOptionsMethod.Invoke(panel, new object[] { panel.Content, controls });

        for (int i = 0; i < controls.Count; i++)
        {
            controls[i].FocusNeighborLeft = controls[i].GetPath();
            controls[i].FocusNeighborRight = controls[i].GetPath();
            controls[i].FocusNeighborTop = i > 0 ? controls[i - 1].GetPath() : controls[i].GetPath();
            controls[i].FocusNeighborBottom = i < controls.Count - 1 ? controls[i + 1].GetPath() : controls[i].GetPath();
        }

        if (controls.Count > 0)
        {
            ModSettingsInjectionHelpers.PanelFirstControlField.SetValue(panel, controls[0]);
        }
    }
}
