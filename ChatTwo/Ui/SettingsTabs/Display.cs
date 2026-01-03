using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

internal sealed class Display : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name => Language.Options_Display_Tab + "###tabs-display";

    internal Display(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImGuiUtil.TextWrapPos();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideChat, Language.Options_HideChat_Name, Language.Options_HideChat_Description);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideDuringCutscenes, Language.Options_HideDuringCutscenes_Name, string.Format(Language.Options_HideDuringCutscenes_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenNotLoggedIn, Language.Options_HideWhenNotLoggedIn_Name, string.Format(Language.Options_HideWhenNotLoggedIn_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenUiHidden, Language.Options_HideWhenUiHidden_Name, string.Format(Language.Options_HideWhenUiHidden_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideInLoadingScreens, Language.Options_HideInLoadingScreens_Name, string.Format(Language.Options_HideInLoadingScreens_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideInBattle, Language.Options_HideInBattle_Name, Language.Options_HideInBattle_Description);
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenInactive, Language.Options_HideWhenInactive_Name, Language.Options_HideWhenInactive_Description);
        ImGui.Spacing();

        if (Mutable.HideWhenInactive)
        {
            using var _ = ImRaii.PushIndent();
            ImGuiUtil.InputIntVertical(Language.Options_InactivityHideTimeout_Name,
                Language.Options_InactivityHideTimeout_Description, ref Mutable.InactivityHideTimeout, 1, 10);
            // Enforce a minimum of 2 seconds to avoid people soft locking
            // themselves.
            Mutable.InactivityHideTimeout = Math.Max(2, Mutable.InactivityHideTimeout);
            ImGui.Spacing();

            // This setting conflicts with HideInBattle, so it's disabled.
            using (ImRaii.Disabled(Mutable.HideInBattle))
            {
                ImGuiUtil.OptionCheckbox(ref Mutable.InactivityHideActiveDuringBattle,
                    Language.Options_InactivityHideActiveDuringBattle_Name,
                    Language.Options_InactivityHideActiveDuringBattle_Description);
                ImGui.Spacing();
            }

            using var channelTree = ImRaii.TreeNode(Language.Options_InactivityHideChannels_Name);
            if (channelTree.Success)
            {
                if (ImGuiUtil.CtrlShiftButton(Language.Options_InactivityHideChannels_All_Label,
                        Language.Options_InactivityHideChannels_Button_Tooltip))
                {
                    Mutable.InactivityHideChannels = TabsUtil.AllChannels();
                    Mutable.InactivityHideExtraChatAll = true;
                    Mutable.InactivityHideExtraChatChannels = [];
                }

                ImGui.SameLine();
                if (ImGuiUtil.CtrlShiftButton(Language.Options_InactivityHideChannels_None_Label,
                        Language.Options_InactivityHideChannels_Button_Tooltip))
                {
                    Mutable.InactivityHideChannels = new Dictionary<ChatType, ChatSource>();
                    Mutable.InactivityHideExtraChatAll = false;
                    Mutable.InactivityHideExtraChatChannels = [];
                }

                ImGui.Spacing();

                ImGuiUtil.ChannelSelector(Language.Options_Tabs_Channels, Mutable.InactivityHideChannels!);
                ImGuiUtil.ExtraChatSelector(Language.Options_Tabs_ExtraChatChannels,
                    ref Mutable.InactivityHideExtraChatAll, Mutable.InactivityHideExtraChatChannels);
            }
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.Use24HourClock, Language.Options_Use24HourClock_Name, Language.Options_Use24HourClock_Description);

        ImGuiUtil.OptionCheckbox(ref Mutable.PrettierTimestamps, Language.Options_PrettierTimestamps_Name, Language.Options_PrettierTimestamps_Description);

        if (Mutable.PrettierTimestamps)
        {
            using var _ = ImRaii.PushIndent();
            ImGuiUtil.OptionCheckbox(ref Mutable.MoreCompactPretty, Language.Options_MoreCompactPretty_Name, Language.Options_MoreCompactPretty_Description);
            ImGuiUtil.OptionCheckbox(ref Mutable.HideSameTimestamps, Language.Options_HideSameTimestamps_Name, Language.Options_HideSameTimestamps_Description);
        }
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.CollapseDuplicateMessages, Language.Options_CollapseDuplicateMessages_Name, Language.Options_CollapseDuplicateMessages_Description);
        if (Mutable.CollapseDuplicateMessages)
        {
            using var _ = ImRaii.PushIndent();
            ImGuiUtil.OptionCheckbox(ref Mutable.CollapseKeepUniqueLinks, Language.Options_CollapseDuplicateMsgUniqueLink_Name, Language.Options_CollapseDuplicateMsgUniqueLink_Description);
        }
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        // Modern UI Settings Section - directly visible
        ImGui.Text("Modern UI Settings");
        ImGui.Spacing();
        
        ImGuiUtil.OptionCheckbox(ref Mutable.ModernUIEnabled, "Enable Modern UI", "Enables modern styling with rounded corners, better colors, and improved visual hierarchy");
        ImGui.Spacing();

        if (Mutable.ModernUIEnabled)
        {
            using var _ = ImRaii.PushIndent();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.UseModernColors, "Use Modern Color Scheme", "Apply a modern dark color scheme with better contrast and visual appeal");
            ImGui.Spacing();

            ImGui.Text("UI Rounding");
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("##ui-rounding", ref Mutable.UIRounding, 0.0f, 12.0f, "%.1f");
            ImGuiUtil.HelpText("Controls the roundness of corners for UI elements");
            ImGui.Spacing();

            ImGui.Text("Border Size");
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("##ui-border", ref Mutable.UIBorderSize, 0.0f, 3.0f, "%.1f");
            ImGuiUtil.HelpText("Controls the thickness of borders around UI elements");
            ImGui.Spacing();

            ImGui.Text("Shadow Strength");
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("##shadow-strength", ref Mutable.ShadowStrength, 0.0f, 1.0f, "%.2f");
            ImGuiUtil.HelpText("Controls the intensity of shadows and depth effects");
            ImGui.Spacing();
            
            ImGui.Separator();
            ImGui.Text("Enhanced Input Area");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.ShowFloatingChannelIndicator, "Show Floating Channel Indicator", "Display a floating pill showing the current channel when typing");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.ShowTypingIndicator, "Show Typing Indicator", "Display animated dots when you're typing a message");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.EnhancedInputFeedback, "Enhanced Input Visual Feedback", "Improved visual feedback with focus states and error highlighting");
            ImGui.Spacing();
            
            ImGui.Text("Unfocused Window Transparency");
            ImGui.SetNextItemWidth(200);
            ImGui.SliderFloat("##unfocused-transparency", ref Mutable.UnfocusedTransparency, 10.0f, 100.0f, "%.0f%%");
            ImGuiUtil.HelpText("Transparency level for unfocused DM windows (lower = more transparent)");
            ImGui.Spacing();
            
            ImGui.Separator();
            ImGui.Text("Better Tab System");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.ShowTabIcons, "Show Tab Icons", "Display icons in tabs based on their primary chat type");
            ImGui.Spacing();
            
            if (Mutable.ShowTabIcons)
            {
                ImGui.Indent();
                if (ImGui.CollapsingHeader("Customize Channel Input Icons"))
                {
                    ImGui.Text("Customize icons for channel input buttons (click on an icon to change it):");
                    ImGui.Spacing();
                    
                    var iconTypes = new[]
                    {
                        ("Say", "Say chat"),
                        ("Shout", "Shout chat"),
                        ("Yell", "Yell chat"),
                        ("Tell", "Tell messages"),
                        ("Party", "Party chat"),
                        ("Alliance", "Alliance chat"),
                        ("FreeCompany", "Free Company chat"),
                        ("Linkshell", "Linkshell chat"),
                        ("CrossLinkshell", "Cross-world Linkshell"),
                        ("NoviceNetwork", "Novice Network"),
                        ("Echo", "Echo messages"),
                        ("System", "System messages"),
                        ("Debug", "Debug messages"),
                        ("Urgent", "Urgent messages"),
                        ("Notice", "Notice messages"),
                        ("DM", "Direct Messages"),
                        ("Default", "Default/Other")
                    };
                    
                    foreach (var (key, description) in iconTypes)
                    {
                        var currentIcon = Mutable.CustomTabIcons.GetValueOrDefault(key, FontAwesomeIcon.CommentDots);
                        
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            if (ImGui.Button($"{currentIcon.ToIconString()}##{key}"))
                            {
                                ImGui.OpenPopup($"IconPicker_{key}");
                            }
                        }
                        
                        ImGui.SameLine();
                        ImGui.Text($"{description}");
                        
                        // Icon picker popup
                        if (ImGui.BeginPopup($"IconPicker_{key}"))
                        {
                            ImGui.Text($"Choose icon for {description}:");
                            ImGui.Separator();
                            
                            var commonIcons = new[]
                            {
                                FontAwesomeIcon.Comment, FontAwesomeIcon.CommentDots, FontAwesomeIcon.Comments,
                                FontAwesomeIcon.Envelope, FontAwesomeIcon.EnvelopeOpen, FontAwesomeIcon.PaperPlane,
                                FontAwesomeIcon.Users, FontAwesomeIcon.User, FontAwesomeIcon.UserFriends,
                                FontAwesomeIcon.Home, FontAwesomeIcon.Shield, FontAwesomeIcon.ShieldAlt,
                                FontAwesomeIcon.Bullhorn, FontAwesomeIcon.ExclamationTriangle, FontAwesomeIcon.ExclamationCircle,
                                FontAwesomeIcon.Link, FontAwesomeIcon.Globe, FontAwesomeIcon.GlobeAmericas,
                                FontAwesomeIcon.Leaf, FontAwesomeIcon.Seedling, FontAwesomeIcon.Tree,
                                FontAwesomeIcon.Terminal, FontAwesomeIcon.Code, FontAwesomeIcon.Laptop,
                                FontAwesomeIcon.Cog, FontAwesomeIcon.Cogs, FontAwesomeIcon.Tools,
                                FontAwesomeIcon.Bug, FontAwesomeIcon.InfoCircle, FontAwesomeIcon.Info,
                                FontAwesomeIcon.Star, FontAwesomeIcon.Heart, FontAwesomeIcon.Fire,
                                FontAwesomeIcon.Bolt, FontAwesomeIcon.Magic, FontAwesomeIcon.Crown,
                                FontAwesomeIcon.Bullseye, FontAwesomeIcon.Coins
                            };
                            
                            var iconsPerRow = 8;
                            for (int i = 0; i < commonIcons.Length; i++)
                            {
                                var icon = commonIcons[i];
                                
                                using (ImRaii.PushFont(UiBuilder.IconFont))
                                {
                                    if (ImGui.Button($"{icon.ToIconString()}##{key}_{i}"))
                                    {
                                        Mutable.CustomTabIcons[key] = icon;
                                        ImGui.CloseCurrentPopup();
                                    }
                                }
                                
                                if ((i + 1) % iconsPerRow != 0 && i < commonIcons.Length - 1)
                                    ImGui.SameLine();
                            }
                            
                            ImGui.EndPopup();
                        }
                    }
                }
                ImGui.Unindent();
                ImGui.Spacing();
            }
            
            ImGuiUtil.OptionCheckbox(ref Mutable.EnableTabDragReorder, "Enable Tab Drag & Drop", "Allow reordering tabs by dragging and dropping them");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.SmoothTabTransitions, "Smooth Tab Transitions", "Apply subtle animations and transitions to tab interactions");
            ImGui.Spacing();
            
            ImGui.Separator();
            ImGui.Text("Enhanced Emote Integration");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.ShowInlineEmotePreviews, "Show Inline Emote Previews", "Display small emote previews next to emote names in the input field");
            ImGui.Spacing();
            
            ImGuiUtil.OptionCheckbox(ref Mutable.EnableEmotePickerPopup, "Enable Emote Picker Popup", "Show an emote picker button next to the input field for easy emote selection");
            ImGui.Spacing();
            
            if (Mutable.EnableEmotePickerPopup)
            {
                using var indent = ImRaii.PushIndent();
                ImGuiUtil.OptionCheckbox(ref Mutable.EmotePickerSearchEnabled, "Enable Emote Search", "Allow searching for emotes in the emote picker popup");
                ImGui.Spacing();
            }
        }
    }
}
