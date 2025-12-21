using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

internal sealed class DirectMessages(Configuration mutable) : ISettingsTab
{
    private Configuration Mutable { get; } = mutable;
    public string Name => "Direct Messages###tabs-direct-messages";

    public void Draw(bool changed)
    {
        ImGui.TextUnformatted("Direct Message Settings");
        ImGui.Separator();
        ImGui.Spacing();

        // Auto-open DM on new tell
        var autoOpenDM = Mutable.AutoOpenDMOnNewTell;
        ImGui.Checkbox("Auto-open DM when receiving new tells", ref autoOpenDM);
        Mutable.AutoOpenDMOnNewTell = autoOpenDM;
        ImGuiUtil.HelpText("Automatically opens a DM window or tab when you receive a tell from a new player. The type (window or tab) is determined by the 'Default DM Mode' setting below.");
        ImGui.Spacing();

        // Default DM mode
        using (var combo = ImGuiUtil.BeginComboVertical("Default DM Mode", Mutable.DefaultDMMode.Name()))
        {
            if (combo.Success)
            {
                foreach (var mode in Enum.GetValues<Configuration.DMDefaultMode>())
                {
                    if (ImGui.Selectable(mode.Name(), Mutable.DefaultDMMode == mode))
                        Mutable.DefaultDMMode = mode;

                    if (ImGui.IsItemHovered())
                        ImGuiUtil.Tooltip(mode.Tooltip() ?? "");
                }
            }
        }
        ImGuiUtil.HelpText("Choose how DMs should be opened by default when using context menus or auto-opening.");
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        // DM Window Management
        ImGui.TextUnformatted("Window Management");
        ImGui.Spacing();

        var enableDMWindows = Mutable.EnableDMWindows;
        ImGui.Checkbox("Enable DM Windows", ref enableDMWindows);
        Mutable.EnableDMWindows = enableDMWindows;
        ImGuiUtil.HelpText("Allow DM conversations to be opened in separate windows that can be moved and resized independently.");
        ImGui.Spacing();

        var enableDMTabs = Mutable.EnableDMTabs;
        ImGui.Checkbox("Enable DM Tabs", ref enableDMTabs);
        Mutable.EnableDMTabs = enableDMTabs;
        ImGuiUtil.HelpText("Allow DM conversations to be opened as tabs in the main chat window.");
        ImGui.Spacing();

        var cascadeDMWindows = Mutable.CascadeDMWindows;
        ImGui.Checkbox("Cascade DM Windows", ref cascadeDMWindows);
        Mutable.CascadeDMWindows = cascadeDMWindows;
        ImGuiUtil.HelpText("Position new DM windows in a cascading pattern to avoid overlapping.");
        ImGui.Spacing();

        if (Mutable.CascadeDMWindows)
        {
            ImGui.Indent();
            var cascadeOffset = Mutable.DMWindowCascadeOffset;
            ImGui.DragFloat2("Cascade Offset", ref cascadeOffset, 1.0f, 0.0f, 100.0f, "%.0f");
            Mutable.DMWindowCascadeOffset = cascadeOffset;
            ImGuiUtil.HelpText("The X and Y offset between cascaded DM windows.");
            ImGui.Unindent();
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        // Behavior Settings
        ImGui.TextUnformatted("Behavior Settings");
        ImGui.Spacing();

        var closeDMsOnLogout = Mutable.CloseDMsOnLogout;
        ImGui.Checkbox("Close DMs on logout", ref closeDMsOnLogout);
        Mutable.CloseDMsOnLogout = closeDMsOnLogout;
        ImGuiUtil.HelpText("Automatically close all DM windows and tabs when you log out of the game.");
        ImGui.Spacing();

        var closeDMsInCombat = Mutable.CloseDMsInCombat;
        ImGui.Checkbox("Close DM windows in combat", ref closeDMsInCombat);
        Mutable.CloseDMsInCombat = closeDMsInCombat;
        ImGuiUtil.HelpText("Automatically close DM windows (but not tabs) when entering combat to reduce screen clutter.");
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        // Display Settings
        ImGui.TextUnformatted("Display Settings");
        ImGui.Spacing();

        var showTellsInMainChat = Mutable.ShowTellsInMainChat;
        ImGui.Checkbox("Show tells in main chat", ref showTellsInMainChat);
        Mutable.ShowTellsInMainChat = showTellsInMainChat;
        ImGuiUtil.HelpText("Display tell messages in the main chat window in addition to DM interfaces.");
        ImGui.Spacing();

        var showDMTabIcons = Mutable.ShowDMTabIcons;
        ImGui.Checkbox("Show DM tab icons", ref showDMTabIcons);
        Mutable.ShowDMTabIcons = showDMTabIcons;
        ImGuiUtil.HelpText("Display envelope icons next to DM tab names to distinguish them from regular tabs.");
        ImGui.Spacing();

        var showUnreadIndicators = Mutable.ShowUnreadIndicators;
        ImGui.Checkbox("Show unread indicators", ref showUnreadIndicators);
        Mutable.ShowUnreadIndicators = showUnreadIndicators;
        ImGuiUtil.HelpText("Display unread message counts on DM tabs and windows.");
        ImGui.Spacing();

        // DM Tab Suffix
        var dmTabSuffix = Mutable.DMTabSuffix;
        ImGui.InputText("DM Tab Suffix", ref dmTabSuffix, 20);
        Mutable.DMTabSuffix = dmTabSuffix;
        ImGuiUtil.HelpText("Text to append to DM tab names to distinguish them from regular tabs (e.g., ' (DM)').");
        ImGui.Spacing();
    }
}