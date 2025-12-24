using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ChatTwo.Code;
using ChatTwo.DM;
using ChatTwo.Ui;

namespace ChatTwo.DM;

/// <summary>
/// Singleton service for managing DM state, histories, and routing.
/// </summary>
internal class DMManager
{
    private static DMManager? _instance;
    private static readonly object _instanceLock = new();
    private Plugin? _plugin;

    /// <summary>
    /// Gets the Plugin instance if DMManager has been initialized.
    /// </summary>
    public Plugin? PluginInstance => _plugin;

    public static DMManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new DMManager();
                }
            }
            return _instance;
        }
    }

    private readonly ConcurrentDictionary<DMPlayer, DMMessageHistory> _histories = new();
    private readonly ConcurrentDictionary<DMPlayer, DMTab> _openTabs = new();
    private readonly ConcurrentDictionary<DMPlayer, DMWindow> _openWindows = new();
    private readonly object _lock = new();

    private DMManager()
    {
        // Plugin will be set later via Initialize method
    }

    /// <summary>
    /// Initializes the DMManager with a Plugin instance.
    /// This must be called before using any methods that require Plugin access.
    /// </summary>
    /// <param name="plugin">The Plugin instance</param>
    public void Initialize(Plugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        
        // Register DM commands
        RegisterCommands();
        
        // Subscribe to game events
        SubscribeToEvents();
    }

    /// <summary>
    /// Gets or creates a message history for the specified player.
    /// </summary>
    /// <param name="player">The player to get history for</param>
    /// <returns>The message history for the player</returns>
    public DMMessageHistory GetOrCreateHistory(DMPlayer player)
    {
        if (player == null)
            throw new ArgumentNullException(nameof(player));

        return _histories.GetOrAdd(player, p => new DMMessageHistory(p));
    }

    /// <summary>
    /// Gets the message history for a player if it exists.
    /// </summary>
    /// <param name="player">The player to get history for</param>
    /// <returns>The message history or null if not found</returns>
    public DMMessageHistory? GetHistory(DMPlayer player)
    {
        if (player == null)
            return null;

        _histories.TryGetValue(player, out var history);
        return history;
    }

    /// <summary>
    /// Gets all players with message histories.
    /// </summary>
    /// <returns>Collection of players with histories</returns>
    public IEnumerable<DMPlayer> GetPlayersWithHistory()
    {
        return _histories.Keys.ToList();
    }
    
    /// <summary>
    /// Checks if a player name corresponds to a known DM player.
    /// </summary>
    public bool IsKnownDMPlayer(string playerName)
    {
        return _histories.Keys.Any(player => player.Name == playerName || player.TabName == playerName) ||
               _openTabs.Keys.Any(player => player.Name == playerName || player.TabName == playerName);
    }
    
    /// <summary>
    /// Gets a DMPlayer by their name or tab name.
    /// </summary>
    public DMPlayer? GetDMPlayerByName(string playerName)
    {
        return _histories.Keys.FirstOrDefault(player => player.Name == playerName || player.TabName == playerName) ??
               _openTabs.Keys.FirstOrDefault(player => player.Name == playerName || player.TabName == playerName);
    }

    /// <summary>
    /// Removes the history for a specific player.
    /// </summary>
    /// <param name="player">The player whose history to remove</param>
    /// <returns>True if the history was removed, false if it didn't exist</returns>
    public bool RemoveHistory(DMPlayer player)
    {
        if (player == null)
            return false;

        return _histories.TryRemove(player, out _);
    }

    /// <summary>
    /// Clears all message histories.
    /// </summary>
    public void ClearAllHistories()
    {
        _histories.Clear();
    }

    /// <summary>
    /// Routes an incoming tell message to the appropriate DM history.
    /// </summary>
    /// <param name="message">The incoming tell message</param>
    public void RouteIncomingTell(Message message)
    {
        if (message == null || !message.IsTell())
            return;

        try
        {
            var player = message.ExtractPlayerFromMessage();
            if (player != null)
            {
                var history = GetOrCreateHistory(player);
                history.AddMessage(message, isIncoming: true);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to route incoming tell: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles an outgoing tell message.
    /// </summary>
    /// <param name="player">The target player</param>
    /// <param name="message">The outgoing message</param>
    public void HandleOutgoingTell(DMPlayer player, Message message)
    {
        if (player == null || message == null)
            return;

        try
        {
            var history = GetOrCreateHistory(player);
            history.AddMessage(message, isIncoming: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to handle outgoing tell: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens or focuses a DM window for the specified player.
    /// </summary>
    /// <param name="player">The player to open a DM window for</param>
    /// <param name="chatLogWindow">The main chat log window instance</param>
    /// <returns>The DMWindow instance, or null if creation failed</returns>
    public DMWindow? OpenDMWindow(DMPlayer player, ChatLogWindow chatLogWindow)
    {
        if (player == null || chatLogWindow == null)
            return null;

        try
        {
            // Check if window already exists
            if (_openWindows.TryGetValue(player, out var existingWindow))
            {
                // Focus the existing window
                existingWindow.IsOpen = true;
                existingWindow.BringToFront();
                return existingWindow;
            }

            // Create new DM window
            var dmWindow = new DMWindow(chatLogWindow, player);
            
            // Add to open windows tracking
            _openWindows.TryAdd(player, dmWindow);
            
            // Add to the window system
            chatLogWindow.Plugin.WindowSystem.AddWindow(dmWindow);
            
            // PERSISTENCE: Save DM window state to configuration for restoration after plugin reload
            SaveDMWindowState(player, dmWindow);
            
            return dmWindow;
        }
        catch (Exception ex)
        {
            DMErrorHandler.HandleUIStateError("window_creation", ex);
            DMErrorHandler.LogDetailedError("OpenDMWindow", ex, new Dictionary<string, object>
            {
                ["PlayerName"] = player.Name,
                ["PlayerWorld"] = player.HomeWorld
            });
            return null;
        }
    }

    /// <summary>
    /// Closes a DM window for the specified player.
    /// </summary>
    /// <param name="player">The player whose DM window to close</param>
    /// <returns>True if the window was closed, false if it didn't exist</returns>
    public bool CloseDMWindow(DMPlayer player)
    {
        if (player == null)
            return false;

        try
        {
            if (_openWindows.TryRemove(player, out var dmWindow))
            {
                // Only set IsOpen to false if it's not already closed
                if (dmWindow.IsOpen)
                {
                    dmWindow.IsOpen = false;
                }
                
                // CRITICAL FIX: Remove the window from the WindowSystem to prevent FFXIV from trying to activate it
                if (_plugin?.WindowSystem != null)
                {
                    try
                    {
                        // Check if the window is actually registered before trying to remove it
                        if (_plugin.WindowSystem.Windows.Contains(dmWindow))
                        {
                            _plugin.WindowSystem.RemoveWindow(dmWindow);
                            Plugin.Log.Debug($"Removed DM window for {player.DisplayName} from WindowSystem");
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"Failed to remove DM window from WindowSystem: {ex.Message}");
                    }
                }
                
                // PERSISTENCE: Remove DM window state from configuration
                RemoveDMWindowState(player);
                
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to close DM window for {player}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the DM window for a specific player if it exists.
    /// Uses enhanced matching logic to handle ContentId and world mismatches.
    /// </summary>
    /// <param name="player">The player to get the DM window for</param>
    /// <returns>The DMWindow instance or null if not found</returns>
    public DMWindow? GetDMWindow(DMPlayer player)
    {
        if (player == null)
            return null;

        // First try direct lookup
        if (_openWindows.TryGetValue(player, out var dmWindow))
            return dmWindow;

        // If direct lookup fails and we have a ContentId, try to find by ContentId
        if (player.ContentId != 0)
        {
            foreach (var kvp in _openWindows)
            {
                var existingPlayer = kvp.Key;
                var existingWindow = kvp.Value;
                
                // Match by ContentId if both have it
                if (existingPlayer.ContentId != 0 && existingPlayer.ContentId == player.ContentId)
                {
                    Plugin.Log.Debug($"GetDMWindow: Found existing window by ContentId match: {existingPlayer.DisplayName} -> {player.DisplayName}");
                    return existingWindow;
                }
                
                // Match by name if ContentId not available but names match
                if (string.Equals(existingPlayer.Name, player.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Debug($"GetDMWindow: Found existing window by name match: {existingPlayer.DisplayName} -> {player.DisplayName}");
                    return existingWindow;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a DM window is open for the specified player.
    /// </summary>
    /// <param name="player">The player to check</param>
    /// <returns>True if a DM window is open, false otherwise</returns>
    public bool HasOpenDMWindow(DMPlayer player)
    {
        if (player == null)
            return false;
            
        if (_openWindows.TryGetValue(player, out var window))
        {
            // Check if the window is actually open AND registered in WindowSystem
            if (window.IsOpen && _plugin?.WindowSystem?.Windows.Contains(window) == true)
            {
                return true;
            }
            else
            {
                // Window exists but is closed or not properly registered, remove it from tracking
                Plugin.Log.Debug($"HasOpenDMWindow: Removing stale window reference for {player.DisplayName}");
                _openWindows.TryRemove(player, out _);
                return false;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Gets all currently open DM windows.
    /// </summary>
    /// <returns>Collection of open DM windows</returns>
    public IEnumerable<DMWindow> GetOpenDMWindows()
    {
        return _openWindows.Values.ToList();
    }

    /// <summary>
    /// Checks if a DM tab is open for the specified player.
    /// </summary>
    /// <param name="player">The player to check</param>
    /// <returns>True if a DM tab is open, false otherwise</returns>
    public bool HasOpenDMTab(DMPlayer player)
    {
        if (player == null)
            return false;
            
        if (_openTabs.TryGetValue(player, out var dmTab))
        {
            // Check if the tab still exists in the configuration
            if (Plugin.Config?.Tabs != null && Plugin.Config.Tabs.Contains(dmTab))
            {
                return true;
            }
            else
            {
                // Tab exists in tracking but not in config, remove it from tracking
                Plugin.Log.Debug($"HasOpenDMTab: Removing stale tab reference for {player.DisplayName}");
                _openTabs.TryRemove(player, out _);
                return false;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Checks if any DM interface (tab or window) is open for the specified player.
    /// </summary>
    /// <param name="player">The player to check</param>
    /// <returns>True if any DM interface is open, false otherwise</returns>
    public bool HasOpenDM(DMPlayer player)
    {
        return HasOpenDMTab(player) || HasOpenDMWindow(player);
    }
    
    /// <summary>
    /// Handles the DM section pop-out setting change.
    /// Converts DM tabs between section window and main window based on the new setting and Default DM Mode.
    /// </summary>
    public void OnDMSectionToggled()
    {
        try
        {
            Plugin.Log.Info($"OnDMSectionToggled: Handling DM section toggle. DMSectionPoppedOut={Plugin.Config.DMSectionPoppedOut}, DefaultDMMode={Plugin.Config.DefaultDMMode}");
            
            if (Plugin.Config.DMSectionPoppedOut)
            {
                // Setting was turned ON - move DM tabs from main window to DM section window
                Plugin.Log.Debug("OnDMSectionToggled: DM section popped out - tabs will be displayed in separate DM section window");
                // The existing logic in ChatLogWindow and DMSectionWindow should handle this automatically
                // Just ensure tracking is correct
                if (Plugin.Config?.Tabs != null)
                {
                    foreach (var tab in Plugin.Config.Tabs)
                    {
                        if (tab is DMTab dmTab && !dmTab.PopOut)
                        {
                            // Ensure DM tab is tracked
                            if (!_openTabs.ContainsKey(dmTab.Player))
                            {
                                _openTabs.TryAdd(dmTab.Player, dmTab);
                                Plugin.Log.Debug($"OnDMSectionToggled: Added DM tab for {dmTab.Player.DisplayName} to tracking");
                            }
                        }
                    }
                }
            }
            else
            {
                // Setting was turned OFF - convert DM tabs based on Default DM Mode
                Plugin.Log.Debug("OnDMSectionToggled: DM section closed - converting DM tabs based on Default DM Mode");
                
                // Get all current DM tabs that would have been in the DM section
                var dmTabsToConvert = new List<DMTab>();
                if (Plugin.Config?.Tabs != null)
                {
                    foreach (var tab in Plugin.Config.Tabs.ToList())
                    {
                        if (tab is DMTab dmTab && !dmTab.PopOut)
                        {
                            dmTabsToConvert.Add(dmTab);
                        }
                    }
                }
                
                Plugin.Log.Debug($"OnDMSectionToggled: Found {dmTabsToConvert.Count} DM tabs to convert");
                
                // Convert each DM tab based on Default DM Mode
                foreach (var dmTab in dmTabsToConvert)
                {
                    try
                    {
                        switch (Plugin.Config.DefaultDMMode)
                        {
                            case Configuration.DMDefaultMode.Window:
                                Plugin.Log.Debug($"OnDMSectionToggled: Converting DM tab for {dmTab.Player.DisplayName} to window");
                                
                                // Ensure the tab is tracked before conversion
                                if (!_openTabs.ContainsKey(dmTab.Player))
                                {
                                    _openTabs.TryAdd(dmTab.Player, dmTab);
                                }
                                
                                // Convert tab to window
                                var dmWindow = ConvertTabToWindow(dmTab.Player);
                                if (dmWindow != null)
                                {
                                    Plugin.Log.Info($"OnDMSectionToggled: Successfully converted DM tab for {dmTab.Player.DisplayName} to window");
                                }
                                else
                                {
                                    Plugin.Log.Warning($"OnDMSectionToggled: Failed to convert DM tab for {dmTab.Player.DisplayName} to window");
                                }
                                break;
                                
                            case Configuration.DMDefaultMode.Tab:
                            default:
                                Plugin.Log.Debug($"OnDMSectionToggled: Keeping DM tab for {dmTab.Player.DisplayName} as tab in main window");
                                // Tab stays as tab but will now appear in main window instead of DM section
                                // Ensure it's properly tracked
                                if (!_openTabs.ContainsKey(dmTab.Player))
                                {
                                    _openTabs.TryAdd(dmTab.Player, dmTab);
                                }
                                Plugin.Log.Debug($"OnDMSectionToggled: DM tab for {dmTab.Player.DisplayName} will remain as tab in main window");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error($"OnDMSectionToggled: Failed to convert DM tab for {dmTab.Player.DisplayName}: {ex.Message}");
                    }
                }
            }
            
            // Clean up any stale references after conversions
            CleanupStaleReferences();
            
            Plugin.Log.Info("OnDMSectionToggled: DM section toggle handling completed");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"OnDMSectionToggled: Error handling DM section toggle: {ex.Message}");
        }
    }

    /// <summary>
    /// Manual test method to trigger DM conversion for debugging.
    /// This can be called from a command or debug interface.
    /// </summary>
    public void TestDMConversion()
    {
        try
        {
            Plugin.Log.Info("TestDMConversion: Starting manual DM conversion test");
            
            // Get all current DM tabs
            var dmTabs = new List<DMTab>();
            if (Plugin.Config?.Tabs != null)
            {
                foreach (var tab in Plugin.Config.Tabs.ToList())
                {
                    if (tab is DMTab dmTab)
                    {
                        dmTabs.Add(dmTab);
                        Plugin.Log.Info($"TestDMConversion: Found DM tab for {dmTab.Player.DisplayName}, PopOut={dmTab.PopOut}");
                    }
                }
            }
            
            Plugin.Log.Info($"TestDMConversion: Found {dmTabs.Count} DM tabs total");
            Plugin.Log.Info($"TestDMConversion: Current DefaultDMMode={Plugin.Config.DefaultDMMode}");
            
            // Try to convert the first DM tab to a window for testing
            if (dmTabs.Count > 0)
            {
                var testTab = dmTabs[0];
                Plugin.Log.Info($"TestDMConversion: Attempting to convert {testTab.Player.DisplayName} to window");
                
                // Ensure tracking
                if (!_openTabs.ContainsKey(testTab.Player))
                {
                    _openTabs.TryAdd(testTab.Player, testTab);
                    Plugin.Log.Info($"TestDMConversion: Added {testTab.Player.DisplayName} to tracking");
                }
                
                var result = ConvertTabToWindow(testTab.Player);
                if (result != null)
                {
                    Plugin.Log.Info($"TestDMConversion: Successfully converted {testTab.Player.DisplayName} to window");
                }
                else
                {
                    Plugin.Log.Error($"TestDMConversion: Failed to convert {testTab.Player.DisplayName} to window");
                }
            }
            else
            {
                Plugin.Log.Info("TestDMConversion: No DM tabs found to test");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"TestDMConversion: Error during test: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts a DM tab to a DM window.
    /// </summary>
    /// <param name="player">The player whose DM tab to convert</param>
    /// <param name="chatLogWindow">The main chat log window instance</param>
    /// <returns>The new DMWindow instance, or null if conversion failed</returns>
    public DMWindow? ConvertTabToWindow(DMPlayer player, ChatLogWindow chatLogWindow)
    {
        if (player == null || chatLogWindow == null)
            return null;

        try
        {
            // Close the existing tab
            if (CloseDMTab(player))
            {
                // Open a new window
                return OpenDMWindow(player, chatLogWindow);
            }
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to convert DM tab to window for {player}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Converts a DM window to a DM tab.
    /// </summary>
    /// <param name="player">The player whose DM window to convert</param>
    /// <returns>The new DMTab instance, or null if conversion failed</returns>
    public DMTab? ConvertWindowToTab(DMPlayer player)
    {
        if (player == null)
            return null;

        try
        {
            Plugin.Log.Debug($"ConvertWindowToTab: Starting conversion for {player.DisplayName}");
            
            // Get the existing DM window
            if (!_openWindows.TryGetValue(player, out var dmWindow))
            {
                Plugin.Log.Warning($"Cannot convert window to tab: No DM window found for {player}");
                return null;
            }
            
            // Get all messages from the window before closing it
            Message[] windowMessages = [];
            try
            {
                using var messages = dmWindow.DMTab.Messages.GetReadOnly(1000);
                windowMessages = messages.ToArray();
                Plugin.Log.Debug($"ConvertWindowToTab: Retrieved {windowMessages.Length} messages from window");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"ConvertWindowToTab: Failed to retrieve messages from window: {ex.Message}");
                // Continue with conversion, new tab will load from history
            }
            
            // Close the existing window first
            if (!CloseDMWindow(player))
            {
                Plugin.Log.Error($"ConvertWindowToTab: Failed to close DM window for {player}");
                return null;
            }
            
            // Create a new tab WITHOUT using OpenDMTab to avoid automatic history loading
            var newTab = new DMTab(player);
            
            // Add to open tabs tracking BEFORE transferring messages
            _openTabs.TryAdd(player, newTab);
            
            // Add to the main configuration tabs list
            Plugin.Config.Tabs.Add(newTab);
            Plugin.Log.Debug($"ConvertWindowToTab: Added new tab to config. Total tabs: {Plugin.Config.Tabs.Count}");
            
            // Save configuration to persist the new tab
            _plugin.SaveConfig();
            Plugin.Log.Debug($"ConvertWindowToTab: Saved configuration with new DM tab");
            
            // Ensure DM Section Window is properly registered and visible if needed
            if (Plugin.Config.DMSectionPoppedOut)
            {
                Plugin.Log.Debug($"ConvertWindowToTab: DM Section is popped out, ensuring DM Section Window is visible");
                
                // Force the DM Section Window to be visible
                if (_plugin.DMSectionWindow != null)
                {
                    _plugin.DMSectionWindow.IsOpen = true;
                    
                    // Ensure it's in the WindowSystem
                    if (!_plugin.WindowSystem.Windows.Contains(_plugin.DMSectionWindow))
                    {
                        _plugin.WindowSystem.AddWindow(_plugin.DMSectionWindow);
                        Plugin.Log.Debug($"ConvertWindowToTab: Added DM Section Window to WindowSystem");
                    }
                }
            }
            
            // Transfer messages from window to new tab BEFORE any history loading
            if (windowMessages.Length > 0)
            {
                try
                {
                    Plugin.Log.Debug($"ConvertWindowToTab: Transferring {windowMessages.Length} messages to new tab");
                    
                    // Add all messages from the window to the new tab
                    foreach (var message in windowMessages)
                    {
                        newTab.AddMessage(message, unread: false);
                    }
                    
                    Plugin.Log.Debug($"ConvertWindowToTab: Successfully transferred {windowMessages.Length} messages to new tab");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"ConvertWindowToTab: Failed to transfer messages to tab: {ex.Message}");
                    
                    // If transfer fails, load from history as fallback
                    try
                    {
                        var history = GetOrCreateHistory(player);
                        var recentMessages = history.GetRecentMessages(50);
                        foreach (var message in recentMessages)
                        {
                            newTab.AddMessage(message, unread: false);
                        }
                        Plugin.Log.Debug($"ConvertWindowToTab: Loaded {recentMessages.Length} messages from history as fallback");
                    }
                    catch (Exception historyEx)
                    {
                        Plugin.Log.Error($"ConvertWindowToTab: Failed to load history as fallback: {historyEx.Message}");
                    }
                }
            }
            else
            {
                // No messages in window, load from history
                try
                {
                    var history = GetOrCreateHistory(player);
                    var recentMessages = history.GetRecentMessages(50);
                    foreach (var message in recentMessages)
                    {
                        newTab.AddMessage(message, unread: false);
                    }
                    Plugin.Log.Debug($"ConvertWindowToTab: No window messages, loaded {recentMessages.Length} messages from history");
                }
                catch (Exception historyEx)
                {
                    Plugin.Log.Error($"ConvertWindowToTab: Failed to load history: {historyEx.Message}");
                }
            }
            
            // Focus the new tab
            FocusDMTab(newTab);
            
            Plugin.Log.Info($"ConvertWindowToTab: Successfully converted DM window to tab for {player.DisplayName}");
            return newTab;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to convert DM window to tab for {player}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Opens or focuses a DM tab for the specified player.
    /// </summary>
    /// <param name="player">The player to open a DM tab for</param>
    /// <returns>The DMTab instance, or null if creation failed</returns>
    public DMTab? OpenDMTab(DMPlayer player)
    {
        if (player == null)
            return null;

        if (_plugin == null)
        {
            Plugin.Log.Error("OpenDMTab: DMManager not initialized with Plugin instance");
            return null;
        }

        try
        {
            Plugin.Log.Debug($"OpenDMTab: Attempting to open DM tab for {player.DisplayName}");
            
            // Check if tab already exists
            if (_openTabs.TryGetValue(player, out var existingTab))
            {
                Plugin.Log.Debug($"OpenDMTab: Found existing tab for {player.DisplayName}, focusing it");
                // Focus the existing tab
                FocusDMTab(existingTab);
                return existingTab;
            }

            Plugin.Log.Debug($"OpenDMTab: Creating new DM tab for {player.DisplayName}");
            
            // Create new DM tab
            var dmTab = new DMTab(player);
            
            // Load message history from MessageStore database
            // This ensures message history survives plugin reloads
            dmTab.LoadMessageHistoryFromStore();
            
            // Add to open tabs tracking
            _openTabs.TryAdd(player, dmTab);
            Plugin.Log.Debug($"OpenDMTab: Added {player.DisplayName} to open tabs tracking");
            
            // Add to the main configuration tabs list
            Plugin.Config.Tabs.Add(dmTab);
            Plugin.Log.Debug($"OpenDMTab: Added {player.DisplayName} to configuration tabs list. Total tabs: {Plugin.Config.Tabs.Count}");
            
            // Save configuration to persist the new tab
            _plugin.SaveConfig();
            Plugin.Log.Debug($"OpenDMTab: Saved configuration");
            
            // Ensure the DM Section Window is visible if DM section is popped out
            if (Plugin.Config.DMSectionPoppedOut)
            {
                var dmSectionWindow = _plugin.DMSectionWindow;
                if (dmSectionWindow != null)
                {
                    // Make sure the window is open and will be drawn
                    dmSectionWindow.IsOpen = true;
                    
                    // CRITICAL FIX: Ensure the window is properly registered in WindowSystem
                    if (_plugin.WindowSystem != null && !_plugin.WindowSystem.Windows.Contains(dmSectionWindow))
                    {
                        Plugin.Log.Info($"OpenDMTab: DMSectionWindow not in WindowSystem, adding it");
                        _plugin.WindowSystem.AddWindow(dmSectionWindow);
                    }
                }
                else
                {
                    Plugin.Log.Warning($"OpenDMTab: DMSectionWindow is null!");
                }
            }
            
            // Focus the new tab
            FocusDMTab(dmTab);
            Plugin.Log.Debug($"OpenDMTab: Focused new DM tab for {player.DisplayName}");
            
            return dmTab;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to open DM tab for {player}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Closes a DM tab for the specified player.
    /// </summary>
    /// <param name="player">The player whose DM tab to close</param>
    /// <returns>True if the tab was closed, false if it didn't exist</returns>
    public bool CloseDMTab(DMPlayer player)
    {
        if (player == null)
            return false;

        try
        {
            if (_openTabs.TryRemove(player, out var dmTab))
            {
                // Remove from main configuration tabs list
                Plugin.Config.Tabs.Remove(dmTab);
                
                // CRITICAL FIX: Save configuration and check if DM Section Window should be hidden
                _plugin?.SaveConfig();
                
                // If there are no more DM tabs, ensure the DM Section Window is properly hidden
                if (!HasAnyDMTabs())
                {
                    Plugin.Log.Debug("CloseDMTab: No more DM tabs, aggressively removing DM Section Window from WindowSystem");
                    var dmSectionWindow = _plugin?.DMSectionWindow;
                    if (dmSectionWindow != null)
                    {
                        dmSectionWindow.IsOpen = false;
                        
                        // Aggressively remove from WindowSystem
                        if (_plugin?.WindowSystem != null)
                        {
                            try
                            {
                                // Check if the window is actually registered before trying to remove it
                                if (_plugin.WindowSystem.Windows.Contains(dmSectionWindow))
                                {
                                    _plugin.WindowSystem.RemoveWindow(dmSectionWindow);
                                    Plugin.Log.Debug("CloseDMTab: Removed DMSectionWindow from WindowSystem");
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Warning($"Failed to remove DM Section Window from WindowSystem: {ex.Message}");
                            }
                        }
                    }
                }
                
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to close DM tab for {player}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the DM tab for a specific player if it exists.
    /// Uses enhanced matching logic to handle ContentId and world mismatches.
    /// </summary>
    /// <param name="player">The player to get the DM tab for</param>
    /// <returns>The DMTab instance or null if not found</returns>
    public DMTab? GetDMTab(DMPlayer player)
    {
        if (player == null)
            return null;

        // First try direct lookup
        if (_openTabs.TryGetValue(player, out var dmTab))
            return dmTab;

        // If direct lookup fails and we have a ContentId, try to find by ContentId
        if (player.ContentId != 0)
        {
            foreach (var kvp in _openTabs)
            {
                var existingPlayer = kvp.Key;
                var existingTab = kvp.Value;
                
                // Match by ContentId if both have it
                if (existingPlayer.ContentId != 0 && existingPlayer.ContentId == player.ContentId)
                {
                    Plugin.Log.Debug($"GetDMTab: Found existing tab by ContentId match: {existingPlayer.DisplayName} -> {player.DisplayName}");
                    return existingTab;
                }
                
                // Match by name if ContentId not available but names match
                if (string.Equals(existingPlayer.Name, player.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Debug($"GetDMTab: Found existing tab by name match: {existingPlayer.DisplayName} -> {player.DisplayName}");
                    return existingTab;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a DM tab to a DM window, preserving all data.
    /// </summary>
    /// <param name="player">The player whose DM tab to convert</param>
    /// <returns>The created DMWindow or null if conversion failed</returns>
    public DMWindow? ConvertTabToWindow(DMPlayer player)
    {
        if (player == null)
            return null;

        if (_plugin == null)
        {
            Plugin.Log.Error("ConvertTabToWindow: DMManager not initialized with Plugin instance");
            return null;
        }

        try
        {
            Plugin.Log.Debug($"ConvertTabToWindow: Starting conversion for {player.DisplayName}");
            
            // Get the existing DM tab
            if (!_openTabs.TryGetValue(player, out var dmTab))
            {
                Plugin.Log.Warning($"Cannot convert tab to window: No DM tab found for {player}");
                return null;
            }

            // Get the ChatLogWindow instance from the plugin
            var chatLogWindow = _plugin.ChatLogWindow;
            if (chatLogWindow == null)
            {
                Plugin.Log.Error("Cannot convert tab to window: ChatLogWindow is null");
                return null;
            }

            Plugin.Log.Debug($"ConvertTabToWindow: Creating DM window for {player.DisplayName}");
            
            // Create the DM window
            var dmWindow = new DMWindow(chatLogWindow, player);
            
            // Transfer existing messages from the tab to the window to avoid duplication
            try
            {
                using var tabMessages = dmTab.Messages.GetReadOnly(1000); // Get all messages from tab
                if (tabMessages.Count > 0)
                {
                    Plugin.Log.Debug($"ConvertTabToWindow: Transferring {tabMessages.Count} messages from tab to window");
                    
                    // Clear any messages that might have been loaded from history
                    // We'll replace them with the actual tab messages
                    dmWindow.DMTab.Messages.Clear();
                    
                    // Add all messages from the tab
                    foreach (var message in tabMessages)
                    {
                        dmWindow.DMTab.AddMessage(message, unread: false);
                    }
                    
                    Plugin.Log.Debug($"ConvertTabToWindow: Successfully transferred {tabMessages.Count} messages");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"ConvertTabToWindow: Failed to transfer messages from tab: {ex.Message}");
                // If transfer fails, the window will have loaded messages from history, which is acceptable
            }
            
            Plugin.Log.Debug($"ConvertTabToWindow: Removing DM tab from tracking");
            
            // Remove the tab from our tracking
            if (!_openTabs.TryRemove(player, out _))
            {
                Plugin.Log.Warning($"Failed to remove DM tab from tracking for {player}");
            }
            
            // Remove the tab from the configuration
            Plugin.Config.Tabs.Remove(dmTab);
            _plugin.SaveConfig();
            
            // Add the window to our tracking
            if (!_openWindows.ContainsKey(player))
            {
                _openWindows[player] = dmWindow;
            }
            
            // Add the window to the window system
            _plugin.WindowSystem.AddWindow(dmWindow);
            
            // PERSISTENCE: Save DM window state to configuration for restoration after plugin reload
            SaveDMWindowState(player, dmWindow);
            
            Plugin.Log.Info($"ConvertTabToWindow: Successfully converted DM tab to window for {player.DisplayName}");
            return dmWindow;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to convert DM tab to window for {player}: {ex.Message}");
            Plugin.Log.Error($"ConvertTabToWindow: Stack trace: {ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Handles drag and drop operations for DM tabs, including conversion to windows.
    /// </summary>
    /// <param name="dmTab">The DM tab being dragged</param>
    /// <param name="isCtrlHeld">Whether Ctrl key is held during drag</param>
    /// <param name="isOutsideWindow">Whether the drag is outside the main chat window</param>
    /// <returns>True if the operation was handled, false otherwise</returns>
    public bool HandleDMTabDragDrop(DMTab dmTab, bool isCtrlHeld, bool isOutsideWindow)
    {
        if (dmTab?.Player == null)
            return false;

        try
        {
            // If Ctrl is held or dragged outside window, convert to window
            if (isCtrlHeld || isOutsideWindow)
            {
                var dmWindow = ConvertTabToWindow(dmTab.Player);
                if (dmWindow != null)
                {
                    if (_plugin != null)
                    {
                        _plugin.SaveConfig();
                    }
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to handle DM tab drag and drop for {dmTab.Player}: {ex.Message}");
            
            // Show user-friendly error message
            ShowDragDropError("Failed to convert tab to window", ex);
            return false;
        }
    }

    /// <summary>
    /// Shows a user-friendly error message for drag and drop operations.
    /// </summary>
    /// <param name="operation">The operation that failed</param>
    /// <param name="ex">The exception that occurred</param>
    private void ShowDragDropError(string operation, Exception ex)
    {
        try
        {
            var errorMessage = $"{operation}: {GetUserFriendlyErrorMessage(ex)}";
            
            // Create a system message to display the error
            var systemMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, $"[DM Error] {errorMessage}")
                },
                new ChatCode((ushort)ChatType.Error)
            );
            
            // Add to the current tab if available
            // TODO: Fix CurrentTab access - need Plugin instance
            // var currentTab = _plugin?.CurrentTab;
            // if (currentTab != null)
            // {
            //     currentTab.AddMessage(systemMessage, unread: false);
            // }
            
            // For now, just log the error
            Plugin.Log.Warning($"[DM Error] {errorMessage}");
        }
        catch (Exception logEx)
        {
            Plugin.Log.Error($"Failed to show drag drop error message: {logEx.Message}");
        }
    }

    /// <summary>
    /// Handles logout behavior for DM windows and tabs based on configuration.
    /// </summary>
    public void HandleLogout()
    {
        try
        {
            if (Plugin.Config.CloseDMsOnLogout == true)
            {
                Plugin.Log.Info("Closing all DM windows and tabs due to logout");
                
                // Close all DM windows
                var windowsToClose = _openWindows.Keys.ToList();
                foreach (var player in windowsToClose)
                {
                    CloseDMWindow(player);
                }
                
                // Close all DM tabs
                var tabsToClose = _openTabs.Keys.ToList();
                foreach (var player in tabsToClose)
                {
                    CloseDMTab(player);
                }
                
                _plugin.SaveConfig();
            }
            else
            {
                Plugin.Log.Info("Preserving DM windows and tabs after logout");
                // DM histories are automatically preserved via the _histories dictionary
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to handle logout behavior: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles combat state changes for DM windows and tabs based on configuration.
    /// </summary>
    /// <param name="inCombat">Whether the player is entering or leaving combat</param>
    public void HandleCombatStateChange(bool inCombat)
    {
        try
        {
            if (inCombat && Plugin.Config.CloseDMsInCombat == true)
            {
                Plugin.Log.Info("Closing all DM windows due to combat");
                
                // Only close windows, not tabs (tabs can stay open but hidden)
                var windowsToClose = _openWindows.Keys.ToList();
                foreach (var player in windowsToClose)
                {
                    CloseDMWindow(player);
                }
                
                _plugin.SaveConfig();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to handle combat state change: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the most recently active DM player for keyboard shortcuts.
    /// </summary>
    /// <returns>The most recently active DM player or null if none found</returns>
    public DMPlayer? GetMostRecentDMPlayer()
    {
        try
        {
            return _histories.Values
                .Where(h => h.Messages.Count > 0)
                .OrderByDescending(h => h.LastActivity)
                .FirstOrDefault()?.Player;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to get most recent DM player: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Focuses a DM window by bringing it to front.
    /// </summary>
    /// <param name="dmWindow">The DM window to focus</param>
    private void FocusDMWindow(DMWindow dmWindow)
    {
        if (dmWindow == null)
            return;

        try
        {
            dmWindow.IsOpen = true;
            dmWindow.BringToFront();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to focus DM window: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens or focuses the most recent DM conversation.
    /// </summary>
    /// <returns>True if a DM was opened/focused, false otherwise</returns>
    public bool OpenRecentDM()
    {
        try
        {
            var recentPlayer = GetMostRecentDMPlayer();
            if (recentPlayer == null)
            {
                Plugin.Log.Info("No recent DM conversations found");
                return false;
            }

            // Check if there's already an open window or tab
            if (HasOpenDMWindow(recentPlayer))
            {
                FocusDMWindow(_openWindows[recentPlayer]);
                return true;
            }
            
            if (HasOpenDMTab(recentPlayer))
            {
                FocusDMTab(_openTabs[recentPlayer]);
                return true;
            }

            // Open new DM based on default mode
            switch (Plugin.Config.DefaultDMMode)
            {
                case Configuration.DMDefaultMode.Window:
                    // TODO: Fix ChatLogWindow access - need Plugin instance
                    Plugin.Log.Warning("Cannot open DM window without Plugin instance");
                    break;
                case Configuration.DMDefaultMode.Tab:
                default:
                    OpenDMTab(recentPlayer);
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to open recent DM: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Searches for players by partial name matching.
    /// </summary>
    /// <param name="partialName">The partial name to search for</param>
    /// <returns>List of matching players from DM history</returns>
    public List<DMPlayer> SearchPlayersByName(string partialName)
    {
        if (string.IsNullOrWhiteSpace(partialName))
            return new List<DMPlayer>();

        try
        {
            return _histories.Keys
                .Where(player => player.Name.Contains(partialName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(player => _histories[player].LastActivity)
                .ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to search players by name '{partialName}': {ex.Message}");
            return new List<DMPlayer>();
        }
    }

    /// <summary>
    /// Registers DM-related commands.
    /// </summary>
    private void RegisterCommands()
    {
        try
        {
            // TODO: Fix Commands access - need Plugin instance
            // _plugin?.Commands.Register("/dm", "Open a DM window or tab with a player. Usage: /dm <player name>").Execute += OnDMCommand;
            Plugin.Log.Info("DM command registration disabled - need Plugin instance");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to register DM commands: {ex.Message}");
        }
    }

    /// <summary>
    /// Subscribes to game events for DM management.
    /// </summary>
    private void SubscribeToEvents()
    {
        try
        {
            // TODO: Fix event subscription - need proper delegate signatures
            // Plugin.ClientState.Logout += OnLogout;
            // _plugin?.Condition.ConditionChange += OnConditionChange;
            Plugin.Log.Info("Event subscription disabled - need proper delegate signatures");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to subscribe to game events: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the /dm command.
    /// </summary>
    /// <param name="command">The command that was executed</param>
    /// <param name="arguments">The arguments provided with the command</param>
    private void OnDMCommand(string command, string arguments)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                // No arguments - open recent DM
                if (!OpenRecentDM())
                {
                    ShowCommandError("No recent DM conversations found. Usage: /dm <player name>");
                }
                return;
            }

            var playerName = arguments.Trim();
            
            // Try to find exact match first
            var exactMatch = _histories.Keys.FirstOrDefault(p => 
                string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase));
            
            if (exactMatch != null)
            {
                OpenDMForPlayer(exactMatch);
                return;
            }

            // Try partial match
            var partialMatches = SearchPlayersByName(playerName);
            if (partialMatches.Count == 1)
            {
                OpenDMForPlayer(partialMatches[0]);
                return;
            }
            
            if (partialMatches.Count > 1)
            {
                var matchNames = string.Join(", ", partialMatches.Take(5).Select(p => p.Name));
                ShowCommandError($"Multiple players found: {matchNames}. Please be more specific.");
                return;
            }

            // No matches found - try to create new DM with current world
            if (Plugin.ClientState.LocalPlayer != null)
            {
                var currentWorld = Plugin.ClientState.LocalPlayer.HomeWorld.RowId;
                var newPlayer = new DMPlayer(playerName, currentWorld);
                OpenDMForPlayer(newPlayer);
            }
            else
            {
                ShowCommandError($"Player '{playerName}' not found in DM history. Make sure you're logged in to start a new conversation.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to handle /dm command: {ex.Message}");
            ShowCommandError("An error occurred while processing the command.");
        }
    }

    /// <summary>
    /// Opens a DM for the specified player based on default mode.
    /// </summary>
    /// <param name="player">The player to open a DM for</param>
    private void OpenDMForPlayer(DMPlayer player)
    {
        switch (Plugin.Config.DefaultDMMode)
        {
            case Configuration.DMDefaultMode.Window:
                // TODO: Fix ChatLogWindow access - need Plugin instance
                Plugin.Log.Warning("Cannot open DM window without Plugin instance");
                break;
            case Configuration.DMDefaultMode.Tab:
            default:
                OpenDMTab(player);
                break;
        }
    }

    /// <summary>
    /// Shows a command error message to the user.
    /// </summary>
    /// <param name="message">The error message to show</param>
    private void ShowCommandError(string message)
    {
        try
        {
            var errorMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, $"[DM] {message}")
                },
                new ChatCode((ushort)ChatType.Error)
            );
            
            // TODO: Fix CurrentTab access - need Plugin instance
            // var currentTab = _plugin?.CurrentTab;
            // if (currentTab != null)
            // {
            //     currentTab.AddMessage(errorMessage, unread: false);
            // }
            
            // For now, just log the error
            Plugin.Log.Warning($"[DM] {message}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to show command error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles logout events.
    /// </summary>
    private void OnLogout()
    {
        HandleLogout();
    }

    /// <summary>
    /// Handles condition changes (like combat state).
    /// </summary>
    /// <param name="flag">The condition flag that changed</param>
    /// <param name="value">The new value of the condition</param>
    private void OnConditionChange(Dalamud.Game.ClientState.Conditions.ConditionFlag flag, bool value)
    {
        if (flag == Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat)
        {
            HandleCombatStateChange(value);
        }
    }

    /// <summary>
    /// Converts technical exceptions to user-friendly error messages for drag and drop operations.
    /// </summary>
    /// <param name="ex">The exception to convert</param>
    /// <returns>A user-friendly error message</returns>
    private string GetUserFriendlyErrorMessage(Exception ex)
    {
        return ex.Message switch
        {
            var msg when msg.Contains("already exists") => "A window for this player is already open",
            var msg when msg.Contains("not found") => "Could not find the DM conversation",
            var msg when msg.Contains("access") => "Unable to access DM data",
            var msg when msg.Contains("memory") => "Insufficient memory to complete operation",
            _ => "An unexpected error occurred. Please try again."
        };
    }

    /// <summary>
    /// Gets all currently open DM tabs.
    /// </summary>
    /// <returns>Collection of open DM tabs</returns>
    public IEnumerable<DMTab> GetOpenDMTabs()
    {
        return _openTabs.Values.ToList();
    }

    /// <summary>
    /// Focuses a DM tab by setting it as the current tab.
    /// </summary>
    /// <param name="dmTab">The DM tab to focus</param>
    private void FocusDMTab(DMTab dmTab)
    {
        if (dmTab == null)
            return;

        try
        {
            // Mark messages as read when focusing
            dmTab.MarkAsRead();
            
            // Find the tab index in the configuration and set it as wanted
            if (Plugin.Config?.Tabs != null)
            {
                var tabIndex = Plugin.Config.Tabs.IndexOf(dmTab);
                if (tabIndex >= 0)
                {
                    _plugin.WantedTab = tabIndex;
                    Plugin.Log.Info($"FocusDMTab: Set WantedTab to {tabIndex} for {dmTab.Player.DisplayName}");
                    
                    // CRITICAL FIX: If DM Section is popped out, also set the active tab in the DM Section Window
                    if (Plugin.Config.DMSectionPoppedOut && _plugin.DMSectionWindow != null)
                    {
                        _plugin.DMSectionWindow.SetActiveTab(dmTab);
                        Plugin.Log.Info($"FocusDMTab: Also set active tab in DM Section Window for {dmTab.Player.DisplayName}");
                    }
                    else
                    {
                        // DM Section is not popped out, focus input in main chat window
                        _plugin.ChatLogWindow.Activate = true;
                        Plugin.Log.Info($"FocusDMTab: Set Activate=true for main chat window for {dmTab.Player.DisplayName}");
                    }
                }
                else
                {
                    Plugin.Log.Warning($"FocusDMTab: Could not find tab index for {dmTab.Player.DisplayName}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to focus DM tab: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles cleanup when a player logs out or the plugin is disposed.
    /// </summary>
    public void CleanupDMTabs()
    {
        try
        {
            if (Plugin.Config.CloseDMsOnLogout == true)
            {
                // Close all DM tabs
                var tabPlayersToClose = _openTabs.Keys.ToList();
                foreach (var player in tabPlayersToClose)
                {
                    CloseDMTab(player);
                }

                // Close all DM windows
                var windowPlayersToClose = _openWindows.Keys.ToList();
                foreach (var player in windowPlayersToClose)
                {
                    CloseDMWindow(player);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to cleanup DM tabs and windows: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers a converted DMTab with the manager for proper tracking.
    /// This is used when converting deserialized regular tabs back to DMTabs.
    /// </summary>
    /// <param name="dmTab">The DMTab to register</param>
    public void RegisterConvertedDMTab(DMTab dmTab)
    {
        if (dmTab?.Player == null)
            return;

        try
        {
            Plugin.Log.Debug($"RegisterConvertedDMTab: Registering {dmTab.Player.DisplayName}");
            
            // Add to open tabs tracking
            _openTabs.TryAdd(dmTab.Player, dmTab);
            
            // Ensure history exists
            GetOrCreateHistory(dmTab.Player);
            
            Plugin.Log.Debug($"RegisterConvertedDMTab: Successfully registered {dmTab.Player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"RegisterConvertedDMTab: Failed to register {dmTab.Player.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a DM tab for a player from their name and world ID.
    /// This is a convenience method for UI integration.
    /// </summary>
    /// <param name="playerName">The player's name</param>
    /// <param name="worldId">The player's world ID</param>
    /// <returns>The created DMTab or null if creation failed</returns>
    public DMTab? CreateDMTabFromPlayerInfo(string playerName, uint worldId)
    {
        if (string.IsNullOrEmpty(playerName) || worldId == 0)
            return null;

        try
        {
            var player = new DMPlayer(playerName, worldId);
            return OpenDMTab(player);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to create DM tab for {playerName}@{worldId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a DM window for a player from their name and world ID.
    /// This is a convenience method for UI integration.
    /// </summary>
    /// <param name="playerName">The player's name</param>
    /// <param name="worldId">The player's world ID</param>
    /// <param name="chatLogWindow">The main chat log window instance</param>
    /// <returns>The created DMWindow or null if creation failed</returns>
    public DMWindow? CreateDMWindowFromPlayerInfo(string playerName, uint worldId, ChatLogWindow chatLogWindow)
    {
        if (string.IsNullOrEmpty(playerName) || worldId == 0 || chatLogWindow == null)
            return null;

        try
        {
            var player = new DMPlayer(playerName, worldId);
            return OpenDMWindow(player, chatLogWindow);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to create DM window for {playerName}@{worldId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if a DM interface already exists for a player and focuses it.
    /// This is used for duplicate prevention in context menus.
    /// </summary>
    /// <param name="playerName">The player's name</param>
    /// <param name="worldId">The player's world ID</param>
    /// <returns>True if an existing DM interface was found and focused, false otherwise</returns>
    public bool FocusExistingDMInterface(string playerName, uint worldId)
    {
        if (string.IsNullOrEmpty(playerName) || worldId == 0)
            return false;

        try
        {
            var player = new DMPlayer(playerName, worldId);
            
            Plugin.Log.Info($"FocusExistingDMInterface: Checking for existing DM interface for {player.DisplayName}");
            Plugin.Log.Info($"FocusExistingDMInterface: Looking for player - Name: '{playerName}', WorldId: {worldId}");
            
            // DEBUG: Log all currently tracked DM tabs
            Plugin.Log.Info($"FocusExistingDMInterface: Currently tracked DM tabs ({_openTabs.Count}):");
            foreach (var kvp in _openTabs)
            {
                var trackedPlayer = kvp.Key;
                var trackedTab = kvp.Value;
                Plugin.Log.Info($"  - Tracked: '{trackedPlayer.Name}' (World: {trackedPlayer.HomeWorld}, ContentId: {trackedPlayer.ContentId}) -> Tab: {trackedTab.Name}");
                Plugin.Log.Info($"    Equals check: {player.Equals(trackedPlayer)}");
            }
            
            // CRITICAL FIX: Clean up stale references BEFORE checking for existing interfaces
            // This prevents the "Focus Existing DM" issue when windows/tabs were closed but not properly cleaned up
            CleanupStaleReferences();
            
            // ENHANCED CHECK: Only consider DM interfaces as "existing" if they are actually visible and usable
            
            // Check for existing DM window first - must be open AND registered in WindowSystem
            if (_openWindows.TryGetValue(player, out var existingWindow))
            {
                Plugin.Log.Debug($"FocusExistingDMInterface: Found existing window for {player.DisplayName}, checking if open and registered");
                
                if (existingWindow.IsOpen && _plugin?.WindowSystem?.Windows.Contains(existingWindow) == true)
                {
                    Plugin.Log.Info($"FocusExistingDMInterface: Focusing existing open window for {player.DisplayName}");
                    existingWindow.BringToFront();
                    existingWindow.Activate = true; // Focus the input field
                    return true;
                }
                else
                {
                    Plugin.Log.Debug($"FocusExistingDMInterface: Window exists but is closed or not in WindowSystem, removing from tracking");
                    // Window exists but is closed or not properly registered, remove it from tracking
                    _openWindows.TryRemove(player, out _);
                }
            }
            
            // Check for existing DM tab - must be in config AND DM Section Window must be open
            if (_openTabs.TryGetValue(player, out var existingTab))
            {
                Plugin.Log.Debug($"FocusExistingDMInterface: Found existing tab for {player.DisplayName}, checking if in config and DM section is open");
                
                // CRITICAL FIX: Only consider the tab as "existing" if:
                // 1. It's in the configuration
                // 2. The DM Section Window is actually open (if it's not a popped-out tab)
                // 3. OR it's a popped-out tab with an active window
                
                var isInConfig = Plugin.Config?.Tabs?.Contains(existingTab) == true;
                var isDMSectionOpen = Plugin.Config.DMSectionPoppedOut && 
                                     _plugin?.DMSectionWindow?.IsOpen == true;
                var isPopOutWithWindow = existingTab.PopOut && 
                                        _openWindows.TryGetValue(player, out var popOutWindow) && 
                                        popOutWindow.IsOpen;
                
                Plugin.Log.Debug($"FocusExistingDMInterface: Tab analysis - InConfig: {isInConfig}, DMSectionOpen: {isDMSectionOpen}, PopOut: {existingTab.PopOut}, PopOutWithWindow: {isPopOutWithWindow}");
                
                if (isInConfig && (isDMSectionOpen || isPopOutWithWindow))
                {
                    Plugin.Log.Info($"FocusExistingDMInterface: Focusing existing tab for {player.DisplayName}");
                    FocusDMTab(existingTab);
                    return true;
                }
                else
                {
                    Plugin.Log.Info($"FocusExistingDMInterface: Tab exists but DM section is not open and no pop-out window, removing from tracking and config");
                    // Tab exists but DM section is not open and no active window, remove it
                    _openTabs.TryRemove(player, out _);
                    if (isInConfig)
                    {
                        Plugin.Config.Tabs.Remove(existingTab);
                        _plugin?.SaveConfig();
                    }
                }
            }
            
            Plugin.Log.Debug($"FocusExistingDMInterface: No existing DM interface found for {player.DisplayName}");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to focus existing DM interface for {playerName}@{worldId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Cleans up all open DM windows and removes them from the WindowSystem.
    /// This should be called when the plugin is being disposed or when doing a full cleanup.
    /// </summary>
    public void CleanupAllDMWindows()
    {
        try
        {
            var windowsToClose = _openWindows.Values.ToList();
            
            foreach (var dmWindow in windowsToClose)
            {
                dmWindow.IsOpen = false;
                
                if (_plugin?.WindowSystem != null)
                {
                    try
                    {
                        // Check if the window is actually registered before trying to remove it
                        if (_plugin.WindowSystem.Windows.Contains(dmWindow))
                        {
                            _plugin.WindowSystem.RemoveWindow(dmWindow);
                            Plugin.Log.Debug($"Removed DM window for {dmWindow.DMTab.Player.DisplayName} from WindowSystem");
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"Failed to remove DM window from WindowSystem: {ex.Message}");
                    }
                }
            }
            
            _openWindows.Clear();
            Plugin.Log.Info($"Cleaned up {windowsToClose.Count} DM windows from WindowSystem");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to cleanup DM windows: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores existing DM tabs from configuration to tracking after plugin reload.
    /// This ensures that DM tabs that were open before plugin reload are properly tracked and visible.
    /// </summary>
    public void RestoreExistingDMTabs()
    {
        try
        {
            if (Plugin.Config?.Tabs == null)
            {
                Plugin.Log.Debug("RestoreExistingDMTabs: No configuration or tabs available");
                return;
            }

            var existingDMTabs = Plugin.Config.Tabs.OfType<DMTab>().ToList();
            Plugin.Log.Info($"RestoreExistingDMTabs: Found {existingDMTabs.Count} existing DM tabs to restore");

            foreach (var dmTab in existingDMTabs)
            {
                try
                {
                    // Add to tracking if not already tracked
                    if (!_openTabs.ContainsKey(dmTab.Player))
                    {
                        _openTabs.TryAdd(dmTab.Player, dmTab);
                        Plugin.Log.Info($"RestoreExistingDMTabs: Restored tracking for DM tab: {dmTab.Player.DisplayName}");
                        
                        // Ensure history exists
                        GetOrCreateHistory(dmTab.Player);
                        
                        // Reinitialize the tab's history connection to DMManager
                        dmTab.ReinitializeHistory();
                    }
                    else
                    {
                        Plugin.Log.Debug($"RestoreExistingDMTabs: DM tab already tracked: {dmTab.Player.DisplayName}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"RestoreExistingDMTabs: Failed to restore DM tab for {dmTab.Player.DisplayName}: {ex.Message}");
                }
            }

            // If we have DM tabs and DM section is popped out, ensure the DM Section Window is visible
            if (existingDMTabs.Count > 0 && Plugin.Config.DMSectionPoppedOut)
            {
                var dmSectionWindow = _plugin?.DMSectionWindow;
                if (dmSectionWindow != null)
                {
                    Plugin.Log.Info($"RestoreExistingDMTabs: Ensuring DM Section Window is visible for {existingDMTabs.Count} restored tabs");
                    dmSectionWindow.IsOpen = true;
                    
                    // Ensure it's in the WindowSystem
                    if (_plugin?.WindowSystem != null && !_plugin.WindowSystem.Windows.Contains(dmSectionWindow))
                    {
                        _plugin.WindowSystem.AddWindow(dmSectionWindow);
                        Plugin.Log.Info("RestoreExistingDMTabs: Added DM Section Window to WindowSystem");
                    }
                }
            }

            Plugin.Log.Info($"RestoreExistingDMTabs: Successfully restored {existingDMTabs.Count} DM tabs");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"RestoreExistingDMTabs: Error restoring existing DM tabs: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleans up stale references and ensures tracking is synchronized with actual state.
    /// This should be called after plugin reload or when there are state inconsistencies.
    /// </summary>
    public void CleanupStaleReferences()
    {
        try
        {
            Plugin.Log.Debug("CleanupStaleReferences: Starting cleanup of stale DM references");
            
            // Clean up windows that are no longer open or registered
            var staleWindows = _openWindows.Where(kvp => 
                !kvp.Value.IsOpen || 
                (_plugin?.WindowSystem != null && !_plugin.WindowSystem.Windows.Contains(kvp.Value))
            ).ToList();
            
            foreach (var kvp in staleWindows)
            {
                Plugin.Log.Debug($"CleanupStaleReferences: Removing stale window reference for {kvp.Key.DisplayName}");
                _openWindows.TryRemove(kvp.Key, out _);
            }
            
            // Clean up tabs that are no longer in the configuration
            var staleTabs = _openTabs.Where(kvp => 
                !Plugin.Config.Tabs.Contains(kvp.Value)
            ).ToList();
            
            foreach (var kvp in staleTabs)
            {
                Plugin.Log.Debug($"CleanupStaleReferences: Removing stale tab reference for {kvp.Key.DisplayName}");
                _openTabs.TryRemove(kvp.Key, out _);
            }
            
            // CRITICAL FIX: Remove orphaned DM tabs from configuration instead of re-adding them to tracking
            // If a DM tab exists in config but not in tracking, it means it was closed and should be removed
            var orphanedDMTabs = Plugin.Config.Tabs.OfType<DMTab>().Where(dmTab => 
                !_openTabs.ContainsKey(dmTab.Player)
            ).ToList();
            
            foreach (var dmTab in orphanedDMTabs)
            {
                Plugin.Log.Info($"CleanupStaleReferences: Removing orphaned DM tab for {dmTab.Player.DisplayName} from configuration");
                Plugin.Config.Tabs.Remove(dmTab);
            }
            
            // Save configuration if we removed any orphaned tabs
            if (orphanedDMTabs.Count > 0)
            {
                _plugin?.SaveConfig();
                Plugin.Log.Info($"CleanupStaleReferences: Saved configuration after removing {orphanedDMTabs.Count} orphaned DM tabs");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to cleanup stale references: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if there are any DM tabs (either in tracking or in configuration).
    /// </summary>
    /// <returns>True if there are any DM tabs, false otherwise</returns>
    private bool HasAnyDMTabs()
    {
        try
        {
            // Check both our tracking and the configuration
            var hasTrackedTabs = _openTabs.Count > 0;
            var hasConfigTabs = Plugin.Config.Tabs.Any(tab => tab is DMTab) == true;
            
            return hasTrackedTabs || hasConfigTabs;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to check for DM tabs: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Forces cleanup of a specific player's DM state to resolve "Focus Existing DM" issues.
    /// </summary>
    /// <param name="player">The player to force cleanup for</param>
    public void ForceCleanupPlayer(DMPlayer player)
    {
        try
        {
            Plugin.Log.Info($"ForceCleanupPlayer: Forcing cleanup for {player.DisplayName}");
            
            // Remove from all tracking
            _openTabs.TryRemove(player, out var removedTab);
            _openWindows.TryRemove(player, out var removedWindow);
            
            // Remove from configuration
            if (removedTab != null)
            {
                Plugin.Config.Tabs.Remove(removedTab);
            }
            
            // Clean up window from WindowSystem if it exists
            if (removedWindow != null)
            {
                removedWindow.IsOpen = false;
                if (_plugin?.WindowSystem != null && _plugin.WindowSystem.Windows.Contains(removedWindow))
                {
                    _plugin.WindowSystem.RemoveWindow(removedWindow);
                }
            }
            
            _plugin?.SaveConfig();
            Plugin.Log.Info($"ForceCleanupPlayer: Completed cleanup for {player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to force cleanup player {player}: {ex.Message}");
        }
    }

    /// <summary>
    /// Aggressively removes all DM tabs from configuration that don't have active windows.
    /// This is a more thorough cleanup that can be used when the normal cleanup isn't working.
    /// </summary>
    public void AggressiveCleanupAllDMTabs()
    {
        try
        {
            Plugin.Log.Info("AggressiveCleanupAllDMTabs: Starting aggressive cleanup of all DM tabs");
            
            // First, log the current state for debugging
            Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Current state - {_openTabs.Count} tracked tabs, {_openWindows.Count} tracked windows");
            Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Configuration has {Plugin.Config.Tabs.Count} total tabs, {Plugin.Config.Tabs.OfType<DMTab>().Count()} DM tabs");
            
            foreach (var kvp in _openTabs)
            {
                Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Tracked tab: {kvp.Key.DisplayName}");
            }
            
            foreach (var kvp in _openWindows)
            {
                Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Tracked window: {kvp.Key.DisplayName} (IsOpen: {kvp.Value.IsOpen})");
            }
            
            foreach (var tab in Plugin.Config.Tabs.OfType<DMTab>())
            {
                Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Config DM tab: {tab.Player.DisplayName}");
            }
            
            var dmTabsToRemove = new List<DMTab>();
            
            // Find all DM tabs in configuration
            foreach (var tab in Plugin.Config.Tabs.ToList())
            {
                if (tab is DMTab dmTab)
                {
                    // Check if this DM tab has an active window
                    var hasActiveWindow = _openWindows.TryGetValue(dmTab.Player, out var window) && 
                                         window.IsOpen && 
                                         _plugin?.WindowSystem?.Windows.Contains(window) == true;
                    
                    // Check if DM Section Window is open and should contain this tab
                    var dmSectionOpen = Plugin.Config.DMSectionPoppedOut && 
                                       _plugin?.DMSectionWindow?.IsOpen == true &&
                                       !dmTab.PopOut;
                    
                    Plugin.Log.Info($"AggressiveCleanupAllDMTabs: {dmTab.Player.DisplayName} - HasActiveWindow: {hasActiveWindow}, DMSectionOpen: {dmSectionOpen}, PopOut: {dmTab.PopOut}");
                    
                    if (!hasActiveWindow && !dmSectionOpen)
                    {
                        Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Marking DM tab for {dmTab.Player.DisplayName} for removal (no active window and DM section not open)");
                        dmTabsToRemove.Add(dmTab);
                    }
                    else
                    {
                        Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Keeping DM tab for {dmTab.Player.DisplayName} (has active window or DM section is open)");
                    }
                }
            }
            
            // Remove all marked DM tabs
            foreach (var dmTab in dmTabsToRemove)
            {
                Plugin.Config.Tabs.Remove(dmTab);
                _openTabs.TryRemove(dmTab.Player, out _);
                Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Removed DM tab for {dmTab.Player.DisplayName}");
            }
            
            // Clear all tracking that doesn't have corresponding windows or DM section
            var trackingToRemove = _openTabs.Where(kvp => 
            {
                var hasActiveWindow = _openWindows.TryGetValue(kvp.Key, out var window) && window.IsOpen;
                var dmSectionOpen = Plugin.Config.DMSectionPoppedOut && 
                                   _plugin?.DMSectionWindow?.IsOpen == true;
                return !hasActiveWindow && !dmSectionOpen;
            }).ToList();
            
            foreach (var kvp in trackingToRemove)
            {
                _openTabs.TryRemove(kvp.Key, out _);
                Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Removed tracking for {kvp.Key.DisplayName}");
            }
            
            if (dmTabsToRemove.Count > 0 || trackingToRemove.Count > 0)
            {
                _plugin?.SaveConfig();
                Plugin.Log.Info($"AggressiveCleanupAllDMTabs: Completed aggressive cleanup. Removed {dmTabsToRemove.Count} DM tabs and {trackingToRemove.Count} tracking entries");
            }
            else
            {
                Plugin.Log.Info("AggressiveCleanupAllDMTabs: No cleanup needed");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to perform aggressive cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves the state of a DM window to configuration for persistence across plugin reloads.
    /// </summary>
    /// <param name="player">The player whose DM window to save</param>
    /// <param name="dmWindow">The DM window to save state for</param>
    private void SaveDMWindowState(DMPlayer player, DMWindow dmWindow)
    {
        try
        {
            if (Plugin.Config == null)
                return;

            Plugin.Log.Debug($"SaveDMWindowState: Saving state for DM window: {player.DisplayName}");

            // Remove any existing state for this player
            RemoveDMWindowState(player);

            // Create new window state
            var windowState = new DMWindowState(
                player,
                dmWindow.Position ?? new System.Numerics.Vector2(100, 100),
                dmWindow.Size ?? new System.Numerics.Vector2(400, 300),
                dmWindow.IsOpen
            );

            // Add to configuration
            Plugin.Config.OpenDMWindows.Add(windowState);

            // Save configuration
            _plugin.SaveConfig();

            Plugin.Log.Debug($"SaveDMWindowState: Successfully saved state for {player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"SaveDMWindowState: Failed to save DM window state for {player.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the state of a DM window from configuration.
    /// </summary>
    /// <param name="player">The player whose DM window state to remove</param>
    private void RemoveDMWindowState(DMPlayer player)
    {
        try
        {
            if (Plugin.Config == null)
                return;

            Plugin.Log.Debug($"RemoveDMWindowState: Removing state for DM window: {player.DisplayName}");

            // Find and remove existing state for this player
            var existingState = Plugin.Config.OpenDMWindows.FirstOrDefault(w => 
                w.PlayerName == player.Name && w.WorldId == player.HomeWorld);

            if (existingState != null)
            {
                Plugin.Config.OpenDMWindows.Remove(existingState);
                _plugin.SaveConfig();
                Plugin.Log.Debug($"RemoveDMWindowState: Successfully removed state for {player.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"RemoveDMWindowState: Failed to remove DM window state for {player.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores DM windows from configuration after plugin reload.
    /// This ensures that DM windows that were open before plugin reload are properly restored.
    /// </summary>
    public void RestoreExistingDMWindows()
    {
        try
        {
            if (Plugin.Config?.OpenDMWindows == null)
            {
                Plugin.Log.Debug("RestoreExistingDMWindows: No configuration or DM windows to restore");
                return;
            }

            var windowsToRestore = Plugin.Config.OpenDMWindows.ToList();
            Plugin.Log.Info($"RestoreExistingDMWindows: Found {windowsToRestore.Count} DM windows to restore");

            foreach (var windowState in windowsToRestore)
            {
                try
                {
                    var player = windowState.ToDMPlayer();
                    Plugin.Log.Info($"RestoreExistingDMWindows: Restoring DM window for {player.DisplayName}");

                    // Check if window is already open (shouldn't happen, but safety check)
                    if (_openWindows.ContainsKey(player))
                    {
                        Plugin.Log.Debug($"RestoreExistingDMWindows: DM window already exists for {player.DisplayName}, skipping");
                        continue;
                    }

                    // Create and restore the DM window
                    var chatLogWindow = _plugin.ChatLogWindow;
                    if (chatLogWindow == null)
                    {
                        Plugin.Log.Error("RestoreExistingDMWindows: ChatLogWindow is null, cannot restore DM windows");
                        break;
                    }

                    var dmWindow = new DMWindow(chatLogWindow, player);
                    
                    // Restore window position and size
                    dmWindow.Position = windowState.Position;
                    dmWindow.Size = windowState.Size;
                    dmWindow.IsOpen = windowState.IsOpen;

                    // Add to tracking
                    _openWindows.TryAdd(player, dmWindow);

                    // Add to WindowSystem
                    _plugin.WindowSystem.AddWindow(dmWindow);

                    Plugin.Log.Info($"RestoreExistingDMWindows: Successfully restored DM window for {player.DisplayName}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"RestoreExistingDMWindows: Failed to restore DM window for {windowState.PlayerName}: {ex.Message}");
                }
            }

            Plugin.Log.Info($"RestoreExistingDMWindows: Completed restoration of {windowsToRestore.Count} DM windows");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"RestoreExistingDMWindows: Error restoring DM windows: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the position and size of a DM window in the configuration.
    /// This should be called when a DM window is moved or resized.
    /// </summary>
    /// <param name="player">The player whose DM window was updated</param>
    /// <param name="position">The new position of the window</param>
    /// <param name="size">The new size of the window</param>
    public void UpdateDMWindowState(DMPlayer player, System.Numerics.Vector2 position, System.Numerics.Vector2 size)
    {
        try
        {
            if (Plugin.Config == null)
                return;

            var existingState = Plugin.Config.OpenDMWindows.FirstOrDefault(w => 
                w.PlayerName == player.Name && w.WorldId == player.HomeWorld);

            if (existingState != null)
            {
                existingState.Position = position;
                existingState.Size = size;
                _plugin.SaveConfig();
                Plugin.Log.Debug($"UpdateDMWindowState: Updated position/size for {player.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"UpdateDMWindowState: Failed to update DM window state for {player.DisplayName}: {ex.Message}");
        }
    }
}