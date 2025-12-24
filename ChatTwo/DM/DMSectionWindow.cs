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
        
        // Set base window flags to prevent outer scrolling
        Flags = ImGuiWindowFlags.NoScrollbar;
        
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

    // REMOVED: This method was creating duplicate buttons above the input field

    private void DrawDMTabContentWithoutButtons(DMTab dmTab)
    {
        // Calculate space for input area (increased to account for bottom spacing)
        var inputHeight = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2.5f;
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
            
            var placeholder = $"Message {dmTab.Player.Name}...";
            var inputFlags = ImGuiInputTextFlags.EnterReturnsTrue;
            
            // Calculate input width by measuring button size (same approach as main chat)
            // We need to account for 2 buttons on the right side
            var numButtons = 2; // Close and Pop Out buttons
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            
            // Estimate button width based on frame height (this is close enough for width calculation)
            var estimatedButtonWidth = ImGui.GetFrameHeight();
            var totalButtonWidth = estimatedButtonWidth * numButtons + spacing * (numButtons - 1);
            // Add extra margin to ensure buttons and resize grip don't overlap (increased margin for resize grip)
            var inputWidth = ImGui.GetContentRegionAvail().X - totalButtonWidth - spacing * 5;
            
            ImGui.SetNextItemWidth(inputWidth);
            
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
            
            // Close DM Tab button (same style as main chat settings button) - match input height
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.Y * 1.2f)))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Times))
                {
                    // Close the DM tab
                    Plugin.Config.Tabs.Remove(dmTab);
                    Plugin.SaveConfig();
                    
                    // Clean up tracking
                    DMManager.Instance.ForceCleanupPlayer(dmTab.Player);
                }
            }
            
            if (ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip("Close DM Tab", Plugin.Config);
            
            ImGui.SameLine();
            
            // Pop Out button (same style as main chat hide button) - match input height
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.Y * 1.2f)))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.ExternalLinkAlt))
                {
                    // Pop out to DM window
                    DMManager.Instance.ConvertTabToWindow(dmTab.Player);
                }
            }
            
            if (ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip("Pop Out DM", Plugin.Config);
            
        }
    }

    // REMOVED: This method was redundant and not used

    // REMOVED: This method was redundant - using DrawDMChannelNameWithButtons instead

    /// <summary>
    /// Draws the channel name for DM tabs, always showing "Tell PlayerName" format.
    /// </summary>
    private void DrawDMChannelNameWithButtons(DMTab dmTab)
    {
        // Always show "Tell PlayerName" for DM tabs, regardless of what CurrentChannel.Name contains
        var chunks = new List<Chunk>
        {
            new TextChunk(ChunkSource.None, null, "Tell "),
            new TextChunk(ChunkSource.None, null, dmTab.Player.DisplayName)
        };
        
        // Draw the channel name (buttons are now on the input line)
        ChatLogWindow.DrawChunks(chunks);
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