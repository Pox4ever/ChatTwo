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
    private bool _dmSectionInputFocused = false;
    
    // Independent tab tracking for DM section (don't interfere with main window)
    private int _activeDMTabIndex = -1;
    private bool _forceTabSelection = false;
    private bool _focusInputField = false;
    private bool _maintainInputFocus = false;

    public DMSectionWindow(Plugin plugin, ChatLogWindow chatLogWindow) 
        : base($"Direct Messages###dm-section-window")
    {
        Plugin = plugin;
        ChatLogWindow = chatLogWindow;
        
        // Copy the EXACT same window setup as ChatLogWindow
        Size = new Vector2(500, 250);
        SizeCondition = ImGuiCond.FirstUseEver;
        
        // Use the same position condition as main chat
        PositionCondition = ImGuiCond.FirstUseEver;
        Position = new Vector2(550, 100);
        
        // Copy the exact same window properties as main chat
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        
        if (!Plugin.Config.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;
        if (!Plugin.Config.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;
        
        // Conditionally show title bar based on collapse buttons setting
        UpdateTitleBarVisibility();
    }

    /// <summary>
    /// Sets the active DM tab in the DM Section Window.
    /// </summary>
    /// <param name="dmTab">The DM tab to make active</param>
    internal void SetActiveTab(DMTab dmTab)
    {
        if (dmTab == null)
            return;

        try
        {
            // Find the index of this DM tab in the configuration
            if (Plugin.Config?.Tabs != null)
            {
                var tabIndex = Plugin.Config.Tabs.IndexOf(dmTab);
                if (tabIndex >= 0)
                {
                    _activeDMTabIndex = tabIndex;
                    _forceTabSelection = true;
                    _focusInputField = true;
                    Plugin.Log.Info($"DMSectionWindow.SetActiveTab: Set active tab index to {tabIndex} for {dmTab.Player.DisplayName}");
                }
                else
                {
                    Plugin.Log.Warning($"DMSectionWindow.SetActiveTab: Could not find tab index for {dmTab.Player.DisplayName}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DMSectionWindow.SetActiveTab: Failed to set active tab: {ex.Message}");
        }
    }

    public override bool DrawConditions()
    {
        // Use the same hiding logic as the main chat window for consistency
        // This includes checks for cutscenes, user hide, battle, and login state
        if (ChatLogWindow.IsHidden)
            return false;
            
        // Check if we should show the DM section window
        var shouldShow = Plugin.Config.DMSectionPoppedOut && HasDMTabs();
        
        // AGGRESSIVE FIX: Completely remove/add the window from WindowSystem based on whether it should be shown
        // This prevents FFXIV from trying to activate it when it's not needed
        if (!shouldShow && IsOpen)
        {
            IsOpen = false;
            
            // Remove from WindowSystem completely
            if (Plugin.WindowSystem != null)
            {
                try
                {
                    // Check if the window is actually registered before trying to remove it
                    if (Plugin.WindowSystem.Windows.Contains(this))
                    {
                        Plugin.WindowSystem.RemoveWindow(this);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"Failed to remove DM Section Window from WindowSystem: {ex.Message}");
                }
            }
        }
        else if (shouldShow && !IsOpen)
        {
            IsOpen = true;
            
            // Add back to WindowSystem
            if (Plugin.WindowSystem != null && !Plugin.WindowSystem.Windows.Contains(this))
            {
                Plugin.WindowSystem.AddWindow(this);
            }
        }
        
        return shouldShow;
    }

    public override void OnClose()
    {
        // When the DM Section Window is closed via the native close button,
        // we should just close the DM tabs but keep the setting enabled
        // The user can disable the setting manually if they want to
        Plugin.Log.Info("DMSectionWindow: Window closed via native close button, closing DM tabs but keeping setting enabled");
        
        // Close all DM tabs that were in the DM section window
        // This ensures proper cleanup and prevents "Focus Existing DM" from showing
        var dmTabsToClose = Plugin.Config.Tabs
            .Where(tab => tab is DMTab dmTab && !dmTab.PopOut)
            .Cast<DMTab>()
            .ToList();
            
        Plugin.Log.Debug($"DMSectionWindow: Closing {dmTabsToClose.Count} DM tabs from section window");
        
        foreach (var dmTab in dmTabsToClose)
        {
            Plugin.Log.Debug($"DMSectionWindow: Closing DM tab for {dmTab.Player.DisplayName}");
            DMManager.Instance.CloseDMTab(dmTab.Player);
        }
        
        // Save the configuration (to persist the closed tabs)
        Plugin.SaveConfig();
        
        // CRITICAL FIX: Clean up any stale references to prevent "Focus Existing DM" issues
        DMManager.Instance.CleanupStaleReferences();
        
        // Note: We do NOT turn off Plugin.Config.DMSectionPoppedOut here
        // The user can disable that setting manually if they want to
        // This allows them to close the window temporarily and reopen it later
        
        base.OnClose();
    }

    public override void Draw()
    {
        // Use Dalamud's native collapse functionality - no need for custom button management
        // Update title bar visibility based on settings
        UpdateTitleBarVisibility();
        
        // If collapsed, don't draw the main content
        if (ImGui.IsWindowCollapsed())
        {
            return;
        }
        
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
                
                // CRITICAL FIX: If this tab should be active based on _activeDMTabIndex, force it to be selected
                if (_forceTabSelection && _activeDMTabIndex == tabIndex)
                {
                    flags |= ImGuiTabItemFlags.SetSelected;
                    Plugin.Log.Info($"DMSectionWindow: Forcing tab {tabIndex} ({dmTab.Player.Name}) to be selected");
                }
                
                using var tabItem = ImRaii.TabItem($"{tabLabel}###dm-section-tab-{tabIndex}", flags);
                
                if (tabItem.Success)
                {
                    activeTabIndex = tabIndex;
                    activeTab = dmTab;
                    
                    // CRITICAL FIX: Only update _activeDMTabIndex if we're not forcing a selection
                    // When forcing selection, we want to keep the target index until the force is applied
                    if (!_forceTabSelection)
                    {
                        _activeDMTabIndex = tabIndex;
                    }
                    
                    // Clear unread for this DM tab
                    dmTab.Unread = 0;
                    dmTab.MarkAsRead();
                }
                
                // Add context menu for DM tabs
                DrawDMTabContextMenu(dmTab, tabIndex);
            }
        }

        // Reset the force selection flag after processing
        if (_forceTabSelection)
        {
            _forceTabSelection = false;
            Plugin.Log.Info($"DMSectionWindow: Reset force tab selection flag");
        }

        // Draw the content of the active tab (buttons are now integrated into the content layout)
        var finalActiveTab = dmTabs.FirstOrDefault(x => _activeDMTabIndex == x.index);
        if (finalActiveTab != null)
        {
            DrawDMTabContentWithoutButtons((DMTab)finalActiveTab.tab);
        }
        else if (dmTabs.Count > 0)
        {
            // If no active tab is set, default to the first one
            _activeDMTabIndex = dmTabs[0].index;
            DrawDMTabContentWithoutButtons((DMTab)dmTabs[0].tab);
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
            
            // CRITICAL FIX: Focus input field when tab is focused via "Focus DM" OR maintain focus after sending (if enabled)
            if (_focusInputField || (_maintainInputFocus && Plugin.Config.KeepInputFocus))
            {
                ImGui.SetKeyboardFocusHere();
                _focusInputField = false;
                _maintainInputFocus = false;
                Plugin.Log.Info($"DMSectionWindow: Focused input field for DM tab");
            }
            else if (_maintainInputFocus)
            {
                // Reset the flag even if we don't focus (when KeepInputFocus is disabled)
                _maintainInputFocus = false;
            }
            
            // Enhanced input field with better visual feedback (same as main chat and DM windows)
            var isCommand = _dmSectionInput.StartsWith('/');
            var hasError = isCommand && !ChatLogWindow.IsValidCommand(_dmSectionInput.Split(' ')[0]);
            var (styleScope, colorScope) = ModernUI.PushEnhancedInputStyle(Plugin.Config, _dmSectionInputFocused, hasError);
            
            bool inputResult;
            using (styleScope)
            using (colorScope)
            {
                // Use independent input field for DM section window
                inputResult = ImGui.InputTextWithHint("##dm-section-input", placeholder, ref _dmSectionInput, 500, inputFlags);
            }
            
            var inputActive = ImGui.IsItemActive();
            _dmSectionInputFocused = inputActive;
            
            if (inputResult && !string.IsNullOrWhiteSpace(_dmSectionInput))
            {
                // Send as tell to the DM target
                var tellCommand = $"/tell {dmTab.Player.DisplayName} {_dmSectionInput.Trim()}";
                Plugin.DMMessageRouter.TrackOutgoingTell(dmTab.Player);
                ChatBox.SendMessage(tellCommand);
                _dmSectionInput = string.Empty;
                _maintainInputFocus = true; // Maintain focus after sending
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
                _maintainInputFocus = true; // Maintain focus after sending
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
        var dmTabs = Plugin.Config.Tabs.Where(tab => !tab.PopOut && tab is DMTab).ToList();
        return dmTabs.Any();
    }
    
    /// <summary>
    /// Updates the title bar visibility based on the collapse buttons setting.
    /// </summary>
    private void UpdateTitleBarVisibility()
    {
        if (Plugin.Config.ShowDMSectionCollapseButtons)
        {
            // Show title bar when collapse buttons are enabled
            Flags &= ~ImGuiWindowFlags.NoTitleBar;
        }
        else
        {
            // Hide title bar when collapse buttons are disabled (follow main chat setting)
            if (!Plugin.Config.ShowTitleBar)
                Flags |= ImGuiWindowFlags.NoTitleBar;
            else
                Flags &= ~ImGuiWindowFlags.NoTitleBar;
        }
    }
}