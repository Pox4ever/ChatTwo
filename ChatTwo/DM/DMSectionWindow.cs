using System.Numerics;
using ChatTwo.Ui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using ChatTwo.GameFunctions;
using Dalamud.Interface.Style;
using Dalamud.Interface;
using ChatTwo.Util;

namespace ChatTwo.DM;

/// <summary>
/// A separate window that contains all DM tabs when the DM section is popped out. 
/// This allows users to have regular chat in the main window and all DMs in a separate window.
/// </summary>
public sealed class DMSectionWindow : Window
{
    private Plugin Plugin { get; }
    private ChatLogWindow ChatLogWindow { get; }
    
    // Independent input field for DM section window
    private string _dmSectionInput = string.Empty;

    public DMSectionWindow(Plugin plugin, ChatLogWindow chatLogWindow) 
        : base($"Direct Messages###dm-section-window")
    {
        Plugin = plugin;
        ChatLogWindow = chatLogWindow;
        
        // Set initial size and position to be independent from main chat window
        Size = new Vector2(500, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
        
        // Position the DM window to the right of the main chat window by default
        // This will only apply on first use, after that user positioning is remembered
        Position = new Vector2(550, 100); // Offset from typical main chat position
        PositionCondition = ImGuiCond.FirstUseEver;
        
        // Make sure the window is always considered "open" - DrawConditions will control visibility
        IsOpen = true;
        RespectCloseHotkey = false; // Don't let users close it with hotkey
        
        // Apply same window settings as main chat but ensure it's independently resizable
        Flags = ImGuiWindowFlags.None;
        if (!Plugin.Config.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;
        if (!Plugin.Config.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;
        if (!Plugin.Config.ShowTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;
    }

    public override void PreDraw()
    {
        // Apply same styling as main chat window
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();
            
        ModernUI.BeginModernStyle(Plugin.Config);
        
        // Apply transparency like main chat window
        // BgAlpha is now set in Draw() method for proper focus-based transparency
        // Don't set it here as it would override the focus-based transparency
    }

    public override void PostDraw()
    {
        ModernUI.EndModernStyle();
        
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();
    }

    public override bool DrawConditions()
    {
        // Only show when DM section is popped out and there are DM tabs
        var shouldShow = Plugin.Config.DMSectionPoppedOut && HasDMTabs();
        
        Plugin.Log.Debug($"DMSectionWindow DrawConditions: DMSectionPoppedOut={Plugin.Config.DMSectionPoppedOut}, HasDMTabs={HasDMTabs()}, ShouldShow={shouldShow}");
        
        return shouldShow;
    }

    public override void Draw()
    {
        // Update focus state and apply transparency
        var isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var alpha = Plugin.Config.WindowAlpha / 100f;
        
        if (!isWindowFocused)
        {
            var transparencyFactor = Plugin.Config.UnfocusedTransparency / 100f;
            BgAlpha = alpha * transparencyFactor;
        }
        else
        {
            BgAlpha = alpha;
        }
        
        // Apply transparency to UI elements based on focus state
        var uiAlpha = isWindowFocused ? 1.0f : (Plugin.Config.UnfocusedTransparency / 100f);
        
        // Push style colors for UI elements with transparency
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.Text) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var buttonColor = ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.Button) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var buttonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.ButtonHovered) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var buttonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.ButtonActive) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var frameColor = ImRaii.PushColor(ImGuiCol.FrameBg, ImGui.GetColorU32(ImGuiCol.FrameBg) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var frameHoveredColor = ImRaii.PushColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var frameActiveColor = ImRaii.PushColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var tabColor = ImRaii.PushColor(ImGuiCol.Tab, ImGui.GetColorU32(ImGuiCol.Tab) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var tabHoveredColor = ImRaii.PushColor(ImGuiCol.TabHovered, ImGui.GetColorU32(ImGuiCol.TabHovered) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var tabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, ImGui.GetColorU32(ImGuiCol.TabActive) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var separatorColor = ImRaii.PushColor(ImGuiCol.Separator, ImGui.GetColorU32(ImGuiCol.Separator) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        
        // Get all DM tabs that are not popped out individually
        var dmTabs = Plugin.Config.Tabs
            .Select((tab, index) => new { tab, index })
            .Where(x => !x.tab.PopOut && x.tab is DMTab)
            .ToList();

        if (dmTabs.Count == 0)
        {
            ImGui.TextUnformatted("No DM conversations");
            return;
        }

        // Draw DM tabs using the same system as main chat
        var activeTabIndex = -1;
        DMTab? activeTab = null;
        
        using (ModernUI.PushModernTabStyle(Plugin.Config))
        using (var tabBar = ImRaii.TabBar("##dm-section-tabs"))
        {
            if (!tabBar.Success)
                return;

            // First pass: Draw all tabs and find the active one
            foreach (var dmTabInfo in dmTabs)
            {
                var dmTab = (DMTab)dmTabInfo.tab;
                var tabIndex = dmTabInfo.index;
                
                var unread = dmTab.Unread == 0 ? "" : $" •{dmTab.Unread}";
                var tabLabel = $"💬 {dmTab.Player.Name}{unread}";
                
                var flags = ImGuiTabItemFlags.None;
                
                using var tabItem = ImRaii.TabItem($"{tabLabel}###dm-section-tab-{tabIndex}", flags);
                
                if (tabItem.Success)
                {
                    activeTabIndex = tabIndex;
                    activeTab = dmTab;
                    
                    // Set this as the active tab
                    Plugin.LastTab = tabIndex;
                    
                    // Clear unread for this DM tab
                    dmTab.Unread = 0;
                    dmTab.MarkAsRead();
                }
                
                // Add context menu for DM tabs
                DrawDMTabContextMenu(dmTab, tabIndex);
            }
        }

        // Draw the content of the active tab (buttons are now integrated into the content layout)
        var finalActiveTab = dmTabs.FirstOrDefault(x => Plugin.LastTab == x.index);
        if (finalActiveTab != null)
        {
            DrawDMTabContentWithoutButtons((DMTab)finalActiveTab.tab);
        }
    }

    private void DrawDMTabContent(DMTab dmTab)
    {
        // Add DM tab action buttons at the top
        var buttonSize = ImGui.GetFrameHeight() * 0.8f;
        
        // Pop out button
        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.ExternalLinkAlt, id: "##dm-section-popout", width: (int)buttonSize))
            {
                DMManager.Instance.ConvertTabToWindow(dmTab.Player);
            }
        }
        
        if (ImGui.IsItemHovered())
            ModernUI.DrawModernTooltip("Pop Out to DM Window", Plugin.Config);
        
        ImGui.SameLine();
        
        // Close button
        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, id: "##dm-section-close", width: (int)buttonSize))
            {
                DMManager.Instance.CloseDMTab(dmTab.Player);
            }
        }
        
        if (ImGui.IsItemHovered())
            ModernUI.DrawModernTooltip("Close DM Tab", Plugin.Config);
        
        // Add some spacing after buttons
        ImGui.Spacing();
        
        // Calculate space for input area
        var inputHeight = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y * 0.5f;
        var messageHeight = ImGui.GetContentRegionAvail().Y - inputHeight;
        
        // Ensure minimum space for input
        if (messageHeight < 50)
        {
            messageHeight = 50;
            inputHeight = ImGui.GetContentRegionAvail().Y - messageHeight;
        }
        
        // Draw message log
        ChatLogWindow.DrawMessageLog(dmTab, ChatLogWindow.PayloadHandler, messageHeight, false);
        
        // Draw channel name (always shows "Tell PlayerName" for DM tabs)
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2)))
        {
            DrawDMChannelName(dmTab);
        }
        
        // Draw input area for DM
        DrawDMInput(dmTab);
    }

    private void DrawDMTabContentWithoutButtons(DMTab dmTab)
    {
        // Calculate space for input area
        var inputHeight = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y * 0.5f;
        var messageHeight = ImGui.GetContentRegionAvail().Y - inputHeight;
        
        // Ensure minimum space for input
        if (messageHeight < 50)
        {
            messageHeight = 50;
            inputHeight = ImGui.GetContentRegionAvail().Y - messageHeight;
        }
        
        // Draw message log (full width now)
        ChatLogWindow.DrawMessageLog(dmTab, ChatLogWindow.PayloadHandler, messageHeight, false);
        
        // Draw channel name with buttons (always shows "Tell PlayerName" for DM tabs)
        // Use tighter spacing to bring the text closer to the input field
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1)))
        {
            DrawDMChannelNameWithButtons(dmTab);
        }
        
        // Draw input area for DM (back to simple version)
        DrawDMInput(dmTab);
    }

    private void DrawDMInput(DMTab dmTab)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2)))
        {
            // Calculate the actual Send button width to ensure consistent alignment
            var sendButtonTextSize = ImGui.CalcTextSize("Send");
            var sendButtonWidth = sendButtonTextSize.X + (ImGui.GetStyle().FramePadding.X * 2);
            var inputWidth = ImGui.GetContentRegionAvail().X - sendButtonWidth - ImGui.GetStyle().ItemSpacing.X;
            
            ImGui.SetNextItemWidth(inputWidth);
            
            var placeholder = $"Message {dmTab.Player.Name}...";
            var inputFlags = ImGuiInputTextFlags.EnterReturnsTrue;
            
            // Track if we should maintain focus
            var shouldMaintainFocus = false;
            
            // Use independent input field for DM section window
            var inputResult = ImGui.InputTextWithHint("##dm-section-input", placeholder, ref _dmSectionInput, 500, inputFlags);
            
            if (inputResult && !string.IsNullOrWhiteSpace(_dmSectionInput))
            {
                // Send as tell to the DM target
                var tellCommand = $"/tell {dmTab.Player.DisplayName} {_dmSectionInput.Trim()}";
                Plugin.DMMessageRouter.TrackOutgoingTell(dmTab.Player);
                ChatBox.SendMessage(tellCommand);
                _dmSectionInput = string.Empty;
                shouldMaintainFocus = true; // Maintain focus after sending
            }
            
            ImGui.SameLine();
            
            // Send button
            var sendEnabled = !string.IsNullOrWhiteSpace(_dmSectionInput);
            using var disabled = ImRaii.Disabled(!sendEnabled);
            
            if (ImGui.Button("Send") && sendEnabled)
            {
                var tellCommand = $"/tell {dmTab.Player.DisplayName} {_dmSectionInput.Trim()}";
                Plugin.DMMessageRouter.TrackOutgoingTell(dmTab.Player);
                ChatBox.SendMessage(tellCommand);
                _dmSectionInput = string.Empty;
                shouldMaintainFocus = true; // Maintain focus after sending
            }
            
            // Maintain focus on the input field after sending a message
            if (shouldMaintainFocus)
            {
                ImGui.SetKeyboardFocusHere(-2); // Focus the input field (2 items back: send button, input field)
            }
        }
    }

    private void DrawDMInputWithButtons(DMTab dmTab)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2)))
        {
            // First, draw the action buttons on their own line, aligned to the right
            var buttonSize = ImGui.GetFrameHeight() * 0.8f;
            var totalButtonWidth = (buttonSize * 2) + ImGui.GetStyle().ItemSpacing.X;
            var availableWidth = ImGui.GetContentRegionAvail().X;
            
            // Position buttons on the right side
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - totalButtonWidth);
            
            // Pop out button with proper FontAwesome icon
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.ExternalLinkAlt, id: "##dm-popout-input", width: (int)buttonSize))
                {
                    DMManager.Instance.ConvertTabToWindow(dmTab.Player);
                }
            }
            
            if (ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip("Pop Out to DM Window", Plugin.Config);
            
            ImGui.SameLine();
            
            // Close button with proper FontAwesome icon
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, id: "##dm-close-input", width: (int)buttonSize))
                {
                    DMManager.Instance.CloseDMTab(dmTab.Player);
                }
            }
            
            if (ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip("Close DM Tab", Plugin.Config);
            
            // Now draw the input field and Send button on the next line
            var sendButtonWidth = 80f;
            var inputWidth = ImGui.GetContentRegionAvail().X - sendButtonWidth;
            
            ImGui.SetNextItemWidth(inputWidth);
            
            var placeholder = $"Message {dmTab.Player.Name}...";
            var inputFlags = ImGuiInputTextFlags.EnterReturnsTrue;
            
            // Track if we should maintain focus
            var shouldMaintainFocus = false;
            
            // Use independent input field for DM section window
            var inputResult = ImGui.InputTextWithHint("##dm-section-input", placeholder, ref _dmSectionInput, 500, inputFlags);
            
            if (inputResult && !string.IsNullOrWhiteSpace(_dmSectionInput))
            {
                // Send as tell to the DM target
                var tellCommand = $"/tell {dmTab.Player.DisplayName} {_dmSectionInput.Trim()}";
                Plugin.DMMessageRouter.TrackOutgoingTell(dmTab.Player);
                ChatBox.SendMessage(tellCommand);
                _dmSectionInput = string.Empty;
                shouldMaintainFocus = true; // Maintain focus after sending
            }
            
            ImGui.SameLine();
            
            // Send button
            var sendEnabled = !string.IsNullOrWhiteSpace(_dmSectionInput);
            using var disabled = ImRaii.Disabled(!sendEnabled);
            
            if (ImGui.Button("Send", new Vector2(sendButtonWidth, ImGui.GetFrameHeight())) && sendEnabled)
            {
                var tellCommand = $"/tell {dmTab.Player.DisplayName} {_dmSectionInput.Trim()}";
                Plugin.DMMessageRouter.TrackOutgoingTell(dmTab.Player);
                ChatBox.SendMessage(tellCommand);
                _dmSectionInput = string.Empty;
                shouldMaintainFocus = true; // Maintain focus after sending
            }
            
            // Maintain focus on the input field after sending a message
            if (shouldMaintainFocus)
            {
                ImGui.SetKeyboardFocusHere(-2); // Focus the input field (2 items back: send button, input field)
            }
        }
    }

    /// <summary>
    /// Draws the channel name for DM tabs, always showing "Tell PlayerName" format.
    /// </summary>
    private void DrawDMChannelName(DMTab dmTab)
    {
        // Always show "Tell PlayerName" for DM tabs, regardless of what CurrentChannel.Name contains
        var chunks = new List<Chunk>
        {
            new TextChunk(ChunkSource.None, null, "Tell "),
            new TextChunk(ChunkSource.None, null, dmTab.Player.DisplayName)
        };
        
        ChatLogWindow.DrawChunks(chunks);
    }

    /// <summary>
    /// Draws the channel name for DM tabs with action buttons on the same line.
    /// </summary>
    private void DrawDMChannelNameWithButtons(DMTab dmTab)
    {
        // Always show "Tell PlayerName" for DM tabs, regardless of what CurrentChannel.Name contains
        var chunks = new List<Chunk>
        {
            new TextChunk(ChunkSource.None, null, "Tell "),
            new TextChunk(ChunkSource.None, null, dmTab.Player.DisplayName)
        };
        
        // Draw the channel name
        ChatLogWindow.DrawChunks(chunks);
        
        // Add buttons on the same line, aligned with the Send button's width
        ImGui.SameLine();
        
        // Calculate the actual Send button width to match it exactly
        var sendButtonTextSize = ImGui.CalcTextSize("Send");
        var sendButtonWidth = sendButtonTextSize.X + (ImGui.GetStyle().FramePadding.X * 2);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var spacing = 4f; // Spacing between buttons
        
        // Calculate button sizes to fit exactly within the Send button width
        var totalButtonWidth = sendButtonWidth;
        var individualButtonWidth = (totalButtonWidth - spacing) / 2f; // Two buttons with spacing between
        var buttonHeight = ImGui.GetFrameHeight() * 0.7f; // Smaller height
        var customButtonSize = new Vector2(individualButtonWidth, buttonHeight);
        
        // Position buttons to align with the Send button's right edge
        var buttonStartX = availableWidth - sendButtonWidth;
        if (buttonStartX > 0)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + buttonStartX);
            
            // Close button using regular button with custom size and manually centered text
            var closeButtonText = "x";
            var closeTextSize = ImGui.CalcTextSize(closeButtonText);
            var closeButtonPos = ImGui.GetCursorScreenPos();
            
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGui.Button("##close-btn", customButtonSize))
                {
                    DMManager.Instance.CloseDMTab(dmTab.Player);
                }
            }
            
            // Draw the text manually centered on the button
            var closeTextPos = new Vector2(
                closeButtonPos.X + (customButtonSize.X - closeTextSize.X) * 0.5f,
                closeButtonPos.Y + (customButtonSize.Y - closeTextSize.Y) * 0.5f
            );
            ImGui.GetWindowDrawList().AddText(closeTextPos, ImGui.GetColorU32(ImGuiCol.Text), closeButtonText);
            
            if (ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip("Close DM Tab", Plugin.Config);
            
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(spacing, 0)); // Add spacing between buttons
            ImGui.SameLine();
            
            // Pop out button using regular button with custom size and manually centered text
            var buttonText = ">";
            var textSize = ImGui.CalcTextSize(buttonText);
            var buttonPos = ImGui.GetCursorScreenPos();
            
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGui.Button("##popout-btn", customButtonSize))
                {
                    DMManager.Instance.ConvertTabToWindow(dmTab.Player);
                }
            }
            
            // Draw the text manually centered on the button
            var textPos = new Vector2(
                buttonPos.X + (customButtonSize.X - textSize.X) * 0.5f,
                buttonPos.Y + (customButtonSize.Y - textSize.Y) * 0.5f
            );
            ImGui.GetWindowDrawList().AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), buttonText);
            
            if (ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip("Pop Out to DM Window", Plugin.Config);
        }
    }

    private void DrawDMTabContextMenu(DMTab dmTab, int tabIndex)
    {
        using var contextMenu = ImRaii.ContextPopupItem($"dm-section-tab-context-menu-{tabIndex}");
        if (!contextMenu.Success)
            return;

        if (ImGui.MenuItem("Pop Out to Window"))
        {
            DMManager.Instance.ConvertTabToWindow(dmTab.Player);
        }
        
        if (ImGui.MenuItem("Close DM"))
        {
            DMManager.Instance.CloseDMTab(dmTab.Player);
        }
        
        ImGui.Separator();
        
        if (ImGui.MenuItem("Add Friend"))
        {
            try
            {
                var friendCommand = $"/friendlist add {dmTab.Player.DisplayName}";
                ChatBox.SendMessage(friendCommand);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to add {dmTab.Player} as friend: {ex.Message}");
            }
        }
        
        if (ImGui.MenuItem("Invite to Party"))
        {
            try
            {
                var inviteCommand = $"/invite {dmTab.Player.DisplayName}";
                ChatBox.SendMessage(inviteCommand);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to invite {dmTab.Player} to party: {ex.Message}");
            }
        }
    }

    private bool HasDMTabs()
    {
        return Plugin.Config.Tabs.Any(tab => !tab.PopOut && tab is DMTab);
    }
}