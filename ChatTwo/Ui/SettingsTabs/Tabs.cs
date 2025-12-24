using System.Linq;
using ChatTwo.Code;
using ChatTwo.DM;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

internal sealed class Tabs : ISettingsTab
{
    private readonly Plugin Plugin;
    private Configuration Mutable { get; }

    public string Name => Language.Options_Tabs_Tab + "###tabs-tabs";

    private int ToOpen = -2;

    internal Tabs(Plugin plugin, Configuration mutable)
    {
        Plugin = plugin;
        Mutable = mutable;
    }
    
    /// <summary>
    /// Checks if a tab is likely a DM tab based on its name pattern.
    /// This is used as a fallback when DM tabs are deserialized as regular Tab objects.
    /// </summary>
    private bool IsLikelyDMTab(Tab tab)
    {
        // First check if DMManager recognizes this as a DM player name
        try
        {
            if (DMManager.Instance.IsKnownDMPlayer(tab.Name))
                return true;
        }
        catch
        {
            // If DMManager isn't ready, fall back to pattern matching
        }
        
        // Pattern-based detection as fallback
        // Check if the tab has only Tell chat codes (typical for DM tabs)
        var hasTellCodes = tab.ChatCodes.ContainsKey(ChatType.TellIncoming) || 
                          tab.ChatCodes.ContainsKey(ChatType.TellOutgoing);
        
        // Check if it has very few chat codes (DM tabs typically only have Tell codes)
        var hasLimitedChatCodes = tab.ChatCodes.Count <= 3; // Tell incoming, outgoing, and maybe one more
        
        // Check if the name doesn't match common tab names
        var commonTabNames = new[] { "General", "Battle", "Event", "Say", "Shout", "Tell", "Party", "Alliance", "FC", "LS", "CWLS", "Novice Network", "Custom" };
        var isNotCommonTabName = !commonTabNames.Any(common => tab.Name.Contains(common, StringComparison.OrdinalIgnoreCase));
        
        // Check if the name looks like a player name (contains letters and possibly spaces, but not special symbols)
        var looksLikePlayerName = !string.IsNullOrEmpty(tab.Name) && 
                                 tab.Name.All(c => char.IsLetter(c) || char.IsWhiteSpace(c) || c == '\'' || c == '-') &&
                                 tab.Name.Any(char.IsLetter);
        
        return hasTellCodes && hasLimitedChatCodes && isNotCommonTabName && looksLikePlayerName;
    }

    public void Draw(bool changed)
    {
        const string addTabPopup = "add-tab-popup";

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Plus, tooltip: Language.Options_Tabs_Add))
            ImGui.OpenPopup(addTabPopup);

        using (var popup = ImRaii.Popup(addTabPopup))
        {
            if (popup)
            {
                if (ImGui.Selectable(Language.Options_Tabs_NewTab))
                    Mutable.Tabs.Add(new Tab());

                ImGui.Separator();

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_General)))
                    Mutable.Tabs.Add(TabsUtil.VanillaGeneral);

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_Event)))
                    Mutable.Tabs.Add(TabsUtil.VanillaEvent);
            }
        }

        var toRemove = -1;
        var doOpens = ToOpen > -2;
        
        // Filter out DM tabs from settings display - they shouldn't be configurable here
        // DM tabs are temporary conversation tabs managed by the DM system, not permanent user-configured tabs
        // Also filter out tabs that look like DM tabs but were deserialized as regular Tab objects
        var configurableTabs = Mutable.Tabs
            .Select((tab, index) => new { tab, index })
            .Where(x => !(x.tab is DMTab) && !IsLikelyDMTab(x.tab))
            .ToList();
        
        for (var i = 0; i < configurableTabs.Count; i++)
        {
            var tabInfo = configurableTabs[i];
            var tab = tabInfo.tab;
            var originalIndex = tabInfo.index;

            if (doOpens)
                ImGui.SetNextItemOpen(i == ToOpen);

            using var treeNode = ImRaii.TreeNode($"{tab.Name}###tab-{originalIndex}");
            if (!treeNode.Success)
                continue;

            using var pushedId = ImRaii.PushId($"tab-{originalIndex}");

            if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, tooltip: Language.Options_Tabs_Delete))
            {
                toRemove = originalIndex; // Use original index for removal
                ToOpen = -1;
            }

            ImGui.SameLine();

            // For move up/down, we need to work with the original tab list but only among non-DM tabs
            var canMoveUp = i > 0;
            var canMoveDown = i < configurableTabs.Count - 1;

            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowUp, tooltip: Language.Options_Tabs_MoveUp) && canMoveUp)
            {
                var prevTabInfo = configurableTabs[i - 1];
                var prevOriginalIndex = prevTabInfo.index;
                
                (Mutable.Tabs[prevOriginalIndex], Mutable.Tabs[originalIndex]) = (Mutable.Tabs[originalIndex], Mutable.Tabs[prevOriginalIndex]);
                ToOpen = i - 1;
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowDown, tooltip: Language.Options_Tabs_MoveDown) && canMoveDown)
            {
                var nextTabInfo = configurableTabs[i + 1];
                var nextOriginalIndex = nextTabInfo.index;
                
                (Mutable.Tabs[nextOriginalIndex], Mutable.Tabs[originalIndex]) = (Mutable.Tabs[originalIndex], Mutable.Tabs[nextOriginalIndex]);
                ToOpen = i + 1;
            }

            ImGui.InputText(Language.Options_Tabs_Name, ref tab.Name, 512, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.Checkbox(Language.Options_Tabs_ShowTimestamps, ref tab.DisplayTimestamp);
            ImGui.Checkbox(Language.Options_Tabs_PopOut, ref tab.PopOut);
            if (tab.PopOut)
            {
                using var _ = ImRaii.PushIndent(10.0f);
                ImGui.Checkbox(Language.Options_Tabs_IndependentOpacity, ref tab.IndependentOpacity);
                if (tab.IndependentOpacity)
                    ImGuiUtil.DragFloatVertical(Language.Options_Tabs_Opacity, ref tab.Opacity, 0.25f, 0f, 100f, $"{tab.Opacity:N2}%%", ImGuiSliderFlags.AlwaysClamp);

                ImGui.Checkbox(Language.Options_Tabs_IndependentHide, ref tab.IndependentHide);
                if (tab.IndependentHide)
                {
                    using var __ = ImRaii.PushIndent(10.0f);
                    ImGuiUtil.OptionCheckbox(ref tab.HideDuringCutscenes, Language.Options_HideDuringCutscenes_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideWhenNotLoggedIn, Language.Options_HideWhenNotLoggedIn_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideWhenUiHidden, Language.Options_HideWhenUiHidden_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideInLoadingScreens, Language.Options_HideInLoadingScreens_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideInBattle, Language.Options_HideInBattle_Name);
                    ImGui.Spacing();
                }

                ImGuiUtil.OptionCheckbox(ref tab.CanMove, Language.Popout_CanMove_Name);
                ImGui.Spacing();

                ImGuiUtil.OptionCheckbox(ref tab.CanResize, Language.Popout_CanResize_Name);
                ImGui.Spacing();
            }

            using (var combo = ImGuiUtil.BeginComboVertical(Language.Options_Tabs_UnreadMode, tab.UnreadMode.Name()))
            {
                if (combo)
                {
                    foreach (var mode in Enum.GetValues<UnreadMode>())
                    {
                        if (ImGui.Selectable(mode.Name(), tab.UnreadMode == mode))
                            tab.UnreadMode = mode;

                        if (mode.Tooltip() is { } tooltip && ImGui.IsItemHovered())
                            ImGuiUtil.Tooltip(tooltip);
                    }
                }
            }

            if (Mutable.HideWhenInactive)
                ImGui.Checkbox(Language.Options_Tabs_InactivityBehaviour, ref tab.UnhideOnActivity);

            ImGui.Checkbox(Language.Options_Tabs_NoInput, ref tab.InputDisabled);
            if (!tab.InputDisabled)
            {
                var input = tab.Channel?.ToChatType().Name() ?? Language.Options_Tabs_NoInputChannel;
                using var combo = ImGuiUtil.BeginComboVertical(Language.Options_Tabs_InputChannel, input);
                if (combo)
                {
                    if (ImGui.Selectable(Language.Options_Tabs_NoInputChannel, tab.Channel == null))
                        tab.Channel = null;

                    foreach (var channel in Enum.GetValues<InputChannel>())
                        if (ImGui.Selectable(channel.ToChatType().Name(), tab.Channel == channel))
                            tab.Channel = channel;
                }
            }

            ImGuiUtil.ChannelSelector(Language.Options_Tabs_Channels, tab.ChatCodes);
            ImGuiUtil.ExtraChatSelector(Language.Options_Tabs_ExtraChatChannels, ref tab.ExtraChatAll, tab.ExtraChatChannels);
        }

        if (toRemove > -1)
        {
            // Don't allow removal of DM tabs through settings - they should be managed through DM system
            var tabToRemove = Mutable.Tabs[toRemove];
            if (!(tabToRemove is DMTab))
            {
                Mutable.Tabs.RemoveAt(toRemove);
                Plugin.WantedTab = 0;
            }
        }

        if (doOpens)
            ToOpen = -2;
    }
}
