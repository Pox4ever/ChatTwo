using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Ui;
using ChatTwo.Util;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text;

namespace ChatTwo.DM;

/// <summary>
/// Extends the Popout class to provide DM-specific window functionality for individual player conversations.
/// </summary>
internal class DMWindow : Window
{
    private readonly ChatLogWindow ChatLogWindow;
    private readonly DMPlayer Player;
    private readonly DMMessageHistory History;
    public readonly DMTab DMTab; // Made public so DMMessageRouter can access it

    private long FrameTime; // set every frame
    private long LastActivityTime = Environment.TickCount64;
    private bool _wasOpen = true; // Track if window was open in previous frame
    
    // Input state for DM window
    private string DMChat = string.Empty;
    private bool DMInputFocused;
    
    // Input focus activation (similar to ChatLogWindow.Activate)
    internal bool Activate = false;

    // Window positioning for cascading
    private static int WindowCount = 0;
    private static readonly object WindowCountLock = new();

    // Performance caching
    private bool _cachedIsFriend = false;
    private long _lastFriendCheck = 0;
    private const long FriendCheckInterval = 5000; // Check every 5 seconds
    
    // UI transparency state
    private float _currentUIAlpha = 1.0f;

    // OPTIMIZATION: Simplified state tracking (removed expensive animation dictionaries)
    private int _lastMessageCount = 0;

    public DMWindow(ChatLogWindow chatLogWindow, DMPlayer player) : base($"DM: {player.DisplayName}##dm-window-{player.GetHashCode()}")
    {
        ChatLogWindow = chatLogWindow ?? throw new ArgumentNullException(nameof(chatLogWindow));
        Player = player ?? throw new ArgumentNullException(nameof(player));
        
        // Try to get history from DMManager, but handle initialization issues
        try
        {
            History = DMManager.Instance.GetOrCreateHistory(player);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"DMWindow constructor: Could not get history from DMManager: {ex.Message}");
            // Create a temporary history that will be replaced when DMManager is properly initialized
            History = new DMMessageHistory(player);
        }
        
        // Create a DMTab for this window to handle message filtering and display
        DMTab = new DMTab(player);

        // Configure window properties with same size as main chat window
        Size = new Vector2(500, 250);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Apply cascading positioning if enabled
        if (Plugin.Config.CascadeDMWindows)
        {
            lock (WindowCountLock)
            {
                var offset = Plugin.Config.DMWindowCascadeOffset * WindowCount;
                Position = new Vector2(100 + offset.X, 100 + offset.Y);
                PositionCondition = ImGuiCond.FirstUseEver;
                WindowCount++;
            }
        }

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        // Load recent message history into the DMTab
        // Only load if this is a fresh window (not converted from a tab with existing messages)
        LoadMessageHistory();
    }

    /// <summary>
    /// Updates the window title to include unread indicators if there are unread messages.
    /// </summary>
    private void UpdateWindowTitle()
    {
        var unreadCount = History?.UnreadCount ?? 0;
        var baseTitle = $"DM: {Player.DisplayName}";
        
        if (unreadCount > 0)
        {
            WindowName = $"{baseTitle} •{unreadCount}##dm-window-{Player.GetHashCode()}";
        }
        else
        {
            WindowName = $"{baseTitle}##dm-window-{Player.GetHashCode()}";
        }
    }

    /// <summary>
    /// Loads recent message history into the DMTab for display.
    /// This method loads messages from the persistent MessageStore database,
    /// so message history survives plugin reloads.
    /// </summary>
    private void LoadMessageHistory()
    {
        try
        {
            Plugin.Log.Debug($"LoadMessageHistory: Starting history load for {Player.DisplayName}");
            
            // Check if message history loading is enabled
            if (!Plugin.Config.LoadDMMessageHistory)
            {
                Plugin.Log.Debug($"Message history loading disabled for {Player.DisplayName}");
                return;
            }

            // First check if we already have messages to avoid unnecessary work
            var currentCount = 0;
            try
            {
                using var existingMessages = DMTab.Messages.GetReadOnly(3);
                currentCount = existingMessages.Count;
            }
            catch (TimeoutException)
            {
                // Timeout is fine, proceed with load
            }
            
            if (currentCount > 0)
            {
                Plugin.Log.Debug($"DMWindow: Skipping history load - already have {currentCount} messages for {Player.DisplayName}");
                return; // Already have messages, skip history load to avoid duplication
            }

            // Check if MessageManager is available
            if (ChatLogWindow?.Plugin?.MessageManager?.Store == null)
            {
                Plugin.Log.Warning($"LoadMessageHistory: MessageManager or Store is null for {Player.DisplayName} - cannot load history");
                return;
            }

            var historyCount = Math.Max(1, Math.Min(200, Plugin.Config.DMMessageHistoryCount));
            Plugin.Log.Debug($"Loading {historyCount} messages from history for {Player.DisplayName}");
            
            // Load messages from the persistent MessageStore database
            var loadedMessages = new List<Message>();
            
            // Query the MessageStore for tell messages with unlimited search to find all messages regardless of age
            var searchLimit = Math.Max(50000, historyCount * 100); // Significantly increased search limit to find all messages
            using (var messageEnumerator = ChatLogWindow.Plugin.MessageManager.Store.GetMostRecentMessages(count: searchLimit))
            {
                foreach (var message in messageEnumerator)
                {
                    // Check if this is a tell message related to our target player
                    if (message.IsTell() && message.IsRelatedToPlayer(Player))
                    {
                        loadedMessages.Add(message);
                        
                        // Stop after finding enough messages
                        if (loadedMessages.Count >= historyCount)
                            break;
                    }
                }
            }
            
            // Take the most recent messages as configured
            var recentMessages = loadedMessages
                .OrderByDescending(m => m.Date)
                .Take(historyCount)
                .OrderBy(m => m.Date)
                .ToArray();
            
            if (recentMessages.Length > 0)
            {
                Plugin.Log.Info($"Loaded {recentMessages.Length} message(s) from history for {Player.DisplayName}");
                
                foreach (var message in recentMessages)
                {
                    // Convert old outgoing messages to "You:" format for consistency
                    var processedMessage = ConvertOutgoingMessageFormat(message);
                    
                    DMTab.AddMessage(processedMessage, unread: false);
                    // Also add to in-memory history for consistency
                    History.AddMessage(processedMessage, isIncoming: processedMessage.IsFromPlayer(Player));
                }
            }
            else
            {
                Plugin.Log.Debug($"No message history found for DM with {Player.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to load message history for DM window: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts old outgoing messages to use "You:" format for consistency in DM windows.
    /// </summary>
    /// <param name="message">The message to potentially convert</param>
    /// <returns>The converted message or original if no conversion needed</returns>
    private Message ConvertOutgoingMessageFormat(Message message)
    {
        try
        {
            // Only convert outgoing tell messages
            if (message.Code.Type != ChatType.TellOutgoing)
                return message;
            
            // Check if this is an outgoing message (from us to the target player)
            if (!message.IsToPlayer(Player))
                return message;
            
            // Get the original sender text
            var originalSender = string.Join("", message.Sender.Select(c => c.StringValue()));
            
            // If it already uses "You:" format, no conversion needed
            if (originalSender.StartsWith("You:"))
                return message;
            
            // If it's the full format (>> PlayerName🌐World:), convert to "You:"
            if (originalSender.StartsWith(">> ") && originalSender.Contains(": "))
            {
                Plugin.Log.Debug($"Converting old outgoing message sender from '{originalSender}' to 'You: '");
                
                // Create new sender chunks with "You:" format
                var newSenderChunks = new List<Chunk>
                {
                    new TextChunk(ChunkSource.Sender, null, "You: ")
                };
                
                // Keep the original content chunks
                var contentChunks = message.Content.ToList();
                
                return new Message(
                    message.Receiver,
                    message.ContentId,
                    message.AccountId,
                    message.Code,
                    newSenderChunks,
                    contentChunks,
                    message.SenderSource,
                    message.ContentSource
                );
            }
            
            // No conversion needed
            return message;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to convert outgoing message format: {ex.Message}");
            return message; // Return original on error
        }
    }

    // Cache for DrawConditions to reduce overhead
    private long _lastConditionsCheck = 0;
    private bool _lastConditionsResult = true;

    public override bool DrawConditions()
    {
        FrameTime = Environment.TickCount64;
        
        // Use the same hiding logic as the main chat window for consistency
        // This includes checks for cutscenes, user hide, battle, and login state
        if (ChatLogWindow.IsHidden)
            return false;
        
        // Only check window closure state occasionally to reduce overhead
        if ((FrameTime % 200) == 0) // Check every ~200ms instead of every frame
        {
            if (_wasOpen && !IsOpen)
            {
                // Window was just closed, notify DMManager
                try
                {
                    DMManager.Instance.CloseDMWindow(Player);
                    Plugin.Log.Info($"DM window for {Player.DisplayName} was closed by user");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to notify DMManager of window closure: {ex.Message}");
                }
            }
            
            // Update the previous state
            _wasOpen = IsOpen;
        }
        
        // If window is closed, don't draw
        if (!IsOpen)
            return false;
        
        // Cache expensive condition checks
        if (FrameTime - _lastConditionsCheck > 100) // Update every ~100ms
        {
            _lastConditionsCheck = FrameTime;
            
            // Check if we should hide based on various conditions
            if (DMTab.IndependentHide ? HideStateCheck() : ChatLogWindow.IsHidden)
            {
                _lastConditionsResult = false;
                return false;
            }

            // Optimize activity checking - only update when necessary
            if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) || !DMTab.UnhideOnActivity)
            {
                LastActivityTime = FrameTime;
                _lastConditionsResult = true;
                return true;
            }

            // Activity in this DM window or the main chat log window
            var lastActivityTime = Math.Max(DMTab.LastActivity, LastActivityTime);
            lastActivityTime = Math.Max(lastActivityTime, ChatLogWindow.LastActivityTime);
            _lastConditionsResult = FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
        }
        
        return _lastConditionsResult;
    }

    // Cache for PreDraw calculations to avoid recalculating every frame
    private bool _lastModernUIEnabled = false;
    private float _cachedBgAlpha = 1.0f;
    private bool _isWindowFocused = true; // Track window focus state for transparency

    public override void PreDraw()
    {
        // Only apply style changes when necessary
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();

        // OPTIMIZATION: Cache ModernUI state and only apply when needed
        var modernUIEnabled = Plugin.Config.ModernUIEnabled;
        if (modernUIEnabled != _lastModernUIEnabled)
        {
            _lastModernUIEnabled = modernUIEnabled;
        }
        
        if (modernUIEnabled)
        {
            ModernUI.BeginModernStyle(Plugin.Config);
        }

        // Set window flags (these rarely change, but we need to set them each frame)
        Flags = ImGuiWindowFlags.None;
        if (!DMTab.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;
        if (!DMTab.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;

        // OPTIMIZATION: Simplified alpha calculation
        var alpha = DMTab.IndependentOpacity ? DMTab.Opacity : Plugin.Config.WindowAlpha;
        
        // Apply unfocused transparency (simplified)
        if (!_isWindowFocused)
        {
            var transparencyFactor = Plugin.Config.UnfocusedTransparency / 100f;
            _cachedBgAlpha = (alpha / 100f) * transparencyFactor;
        }
        else
        {
            _cachedBgAlpha = alpha / 100f;
        }
        
        // OPTIMIZATION: Remove window entrance animation
        BgAlpha = _cachedBgAlpha;
    }

    public override void Draw()
    {
        using var id = ImRaii.PushId($"dm-window-{Player.GetHashCode()}");

        // Update focus state for transparency and mark as read when focused
        _isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        
        // Apply transparency to UI elements based on focus state (OPTIMIZED: Only when needed)
        var uiAlpha = _isWindowFocused ? 1.0f : (Plugin.Config.UnfocusedTransparency / 100f);
        _currentUIAlpha = uiAlpha;
        
        // OPTIMIZATION: Only push color styles when transparency is actually needed
        var needsTransparency = uiAlpha < 1.0f;
        var colorScopes = new List<IDisposable>();
        
        if (needsTransparency)
        {
            // Only push the essential color styles to reduce overhead
            colorScopes.Add(ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.Text) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24)));
            colorScopes.Add(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.Button) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24)));
            colorScopes.Add(ImRaii.PushColor(ImGuiCol.FrameBg, ImGui.GetColorU32(ImGuiCol.FrameBg) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24)));
        }

        try
        {
            // Calculate space for input area
            var inputHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
            var messageLogHeight = ImGui.GetContentRegionAvail().Y - inputHeight;

            // Draw message log or empty state using optimized rendering
            try
            {
                // Use no timeout to avoid blocking - if messages aren't available immediately, show empty state
                using var messages = DMTab.Messages.GetReadOnly(0); // No timeout - immediate return
                if (messages.Count == 0)
                {
                    DrawEmptyState(messageLogHeight);
                }
                else
                {
                    // Use optimized message rendering
                    DrawOptimizedMessageLog(messages, messageLogHeight);
                }
            }
            catch (TimeoutException)
            {
                // If we can't get messages immediately, show empty state to avoid blocking
                DrawEmptyState(messageLogHeight);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"DMWindow.Draw: Error accessing messages: {ex.Message}");
                DrawEmptyState(messageLogHeight);
            }

            // Draw DM-specific input area
            DrawDMInputArea();
            
            if (_isWindowFocused)
            {
                LastActivityTime = FrameTime;
                // Only mark as read every few frames to reduce overhead
                if ((FrameTime % 100) == 0)
                {
                    History.MarkAsRead();
                    DMTab.MarkAsRead();
                }
            }
        }
        finally
        {
            // Dispose color scopes
            foreach (var scope in colorScopes)
            {
                scope.Dispose();
            }
        }
    }

    /// <summary>
    /// Draws a minimize button in the content area at the top-right.
    /// </summary>
    private void DrawMinimizeButtonInContent()
    {
        // Get available width and calculate button position
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var buttonSize = 20f;
        
        // Position button at top-right of content area
        ImGui.SetCursorPosX(availableWidth - buttonSize);
        
        // Draw the minimize button with visible styling
        ImGui.PushStyleColor(ImGuiCol.Button, 0xFF333333); // Dark background
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xFF555555); // Lighter when hovered
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xFF777777); // Even lighter when clicked
        ImGui.PushStyleColor(ImGuiCol.Text, 0xFFFFFFFF); // White text
        
        if (ImGui.Button("−", new Vector2(buttonSize, buttonSize)))
        {
            ImGui.SetWindowCollapsed(true);
        }
        
        ImGui.PopStyleColor(4);
        
        // Tooltip
        if (ImGui.IsItemHovered())
        {
            ImGuiUtil.Tooltip("Minimize");
        }
        
        // Add some spacing after the button
        ImGui.Spacing();
    }

    /// <summary>
    /// Handles window-to-tab conversion when the window is dragged back to the main chat window.
    /// </summary>
    private void HandleWindowToTabConversion()
    {
        // Check if the window is being dragged
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootWindow) && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // Get the main chat window position and size
            var mainChatWindow = ChatLogWindow;
            if (mainChatWindow != null)
            {
                // Check if the mouse is over the main chat window
                var mousePos = ImGui.GetMousePos();
                
                // If mouse is released over the main chat window, convert to tab
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    // Simple check: if mouse is within reasonable bounds of the main window
                    // This is a simplified implementation - a more robust version would check actual window bounds
                    var shouldConvert = false;
                    
                    // Check if we should convert (this is a placeholder - actual implementation would need proper bounds checking)
                    if (shouldConvert)
                    {
                        DMManager.Instance.ConvertWindowToTab(Player);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converts this DM window back to a tab in the main chat window.
    /// </summary>
    private void ConvertWindowToTab()
    {
        try
        {
            Plugin.Log.Debug($"ConvertWindowToTab: Converting DM window for {Player.DisplayName} back to tab");
            
            // Use DMManager to convert window to tab
            DMManager.Instance.ConvertWindowToTab(Player);
            
            // Close this window
            IsOpen = false;
            
            Plugin.Log.Debug($"ConvertWindowToTab: Successfully converted DM window for {Player.DisplayName} to tab");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"ConvertWindowToTab: Failed to convert DM window to tab: {ex.Message}");
            Plugin.Log.Error($"ConvertWindowToTab: Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Draws the action buttons (invite, add friend, character card) in a compact layout.
    /// </summary>
    private void DrawActionButtons()
    {
        var buttonSize = new Vector2(24, 24); // Increased from 20x20 to 24x24 for better visibility
        
        if (Plugin.Config.ModernUIEnabled)
        {
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                DrawActionButtonsInternal(buttonSize);
            }
        }
        else
        {
            DrawActionButtonsInternal(buttonSize);
        }
    }

    /// <summary>
    /// Internal method to draw the actual action buttons with hover animations.
    /// </summary>
    private void DrawActionButtonsInternal(Vector2 buttonSize)
    {
        var time = (float)(FrameTime / 1000.0);
        
        // Invite to Party button with hover animation
        var inviteHovered = false;
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, GetButtonAlpha("invite", out inviteHovered)))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Users, "invite-party"))
            {
                InviteToParty();
            }
        }
        
        if (ImGui.IsItemHovered())
        {
            if (Plugin.Config.ModernUIEnabled)
                ModernUI.DrawModernTooltip("Invite to Party", Plugin.Config);
            else
                ImGuiUtil.Tooltip("Invite to Party");
        }

        ImGui.SameLine();

        // Add Friend button - only show if not already a friend
        if (!IsPlayerAlreadyFriend())
        {
            var friendHovered = false;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, GetButtonAlpha("friend", out friendHovered)))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Heart, "add-friend"))
                {
                    AddFriend();
                }
            }
            
            if (ImGui.IsItemHovered())
            {
                if (Plugin.Config.ModernUIEnabled)
                    ModernUI.DrawModernTooltip("Add Friend", Plugin.Config);
                else
                    ImGuiUtil.Tooltip("Add Friend");
            }

            ImGui.SameLine();
        }

        // Open Character Card button with hover animation
        var plateHovered = false;
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, GetButtonAlpha("plate", out plateHovered)))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.IdCard, "adventurer-plate"))
            {
                OpenAdventurerPlate();
            }
        }
        
        if (ImGui.IsItemHovered())
        {
            if (Plugin.Config.ModernUIEnabled)
                ModernUI.DrawModernTooltip("View Adventurer Plate", Plugin.Config);
            else
                ImGuiUtil.Tooltip("View Adventurer Plate");
        }
    }

    /// <summary>
    /// Gets animated alpha for button hover effects.
    /// </summary>
    private float GetButtonAlpha(string buttonId, out bool isHovered)
    {
        isHovered = ImGui.IsItemHovered();
        
        if (!Plugin.Config.ModernUIEnabled)
        {
            return 1f; // No animation when ModernUI is disabled
        }
        
        // Smooth hover animation
        var baseAlpha = 0.8f;
        var hoverAlpha = 1f;
        var time = (float)(FrameTime / 1000.0);
        
        if (isHovered)
        {
            // Animate to hover state
            var pulse = (float)(Math.Sin(time * 4f) * 0.1f + 0.9f); // Subtle pulse
            return Math.Min(hoverAlpha, baseAlpha + pulse);
        }
        
        return baseAlpha;
    }

    /// <summary>
    /// Optimized message rendering specifically for DM windows.
    /// Removes expensive animations and complex processing for better performance.
    /// </summary>
    private void DrawOptimizedMessageLog(IReadOnlyList<Message> messages, float childHeight)
    {
        // Add NoBackground flag to prevent darker background
        using var child = ImRaii.Child("##dm-messages", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoBackground);
        if (!child.Success)
            return;

        // OPTIMIZATION: Remove animation system for better performance
        // Simple message rendering without animations
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        {
            var maxMessages = Math.Min(messages.Count, Plugin.Config.MaxLinesToRender);
            var startIndex = Math.Max(0, messages.Count - maxMessages);
            
            // Track last timestamp to avoid repetition (like regular chat)
            string? lastTimestamp = null;
            
            for (var i = startIndex; i < messages.Count; i++)
            {
                var message = messages[i];
                
                // Draw timestamp like regular chat (24h format, no repetition, proper spacing)
                if (DMTab.DisplayTimestamp)
                {
                    var localTime = message.Date.ToLocalTime();
                    var currentTimestamp = localTime.ToString("HH:mm"); // 24h format like regular chat
                    
                    // Only show timestamp if it's different from the last one (like regular chat)
                    if (currentTimestamp != lastTimestamp)
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, 0xFF888888)) // Gray color like regular chat
                        {
                            ImGui.TextUnformatted(currentTimestamp);
                        }
                        ImGui.SameLine();
                        
                        // Add proper spacing after timestamp (like regular chat)
                        ImGui.Dummy(new Vector2(8, 0)); // More spacing like regular chat
                        ImGui.SameLine();
                        
                        lastTimestamp = currentTimestamp;
                    }
                    else
                    {
                        // Same timestamp as previous message - add equivalent spacing
                        ImGui.Dummy(new Vector2(ImGui.CalcTextSize(currentTimestamp).X + 8, 0));
                        ImGui.SameLine();
                    }
                }

                // Draw sender (if present)
                if (message.Sender.Count > 0)
                {
                    DrawOptimizedChunks(message.Sender, message);
                    ImGui.SameLine();
                }

                // Draw content
                if (message.Content.Count > 0)
                {
                    DrawOptimizedChunks(message.Content, message);
                }
                else
                {
                    ImGui.TextUnformatted(" "); // Ensure something is drawn
                }
            }
        }

        // OPTIMIZATION: Remove smooth scrolling animation for better performance
        // Auto-scroll to bottom for new messages (instant)
        if (messages.Count != _lastMessageCount)
        {
            _lastMessageCount = messages.Count;
            ImGui.SetScrollHereY(1.0f); // Scroll to bottom instantly
        }
    }

    /// <summary>
    /// Optimized chunk rendering without animations for better performance.
    /// </summary>
    private void DrawOptimizedChunks(IReadOnlyList<Chunk> chunks, Message message)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            
            if (i > 0)
                ImGui.SameLine();
            
            // Handle different chunk types with minimal processing
            switch (chunk)
            {
                case TextChunk textChunk:
                    // Determine color based on message type (simplified)
                    if (message.Code.Type == ChatType.Error)
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, 0xFF4444FF)) // Red for errors
                        {
                            ImGui.TextUnformatted(textChunk.Content);
                        }
                    }
                    else
                    {
                        ImGui.TextUnformatted(textChunk.Content);
                    }
                    break;
                    
                case IconChunk iconChunk:
                    // Simplified icon handling
                    if (iconChunk.Icon == BitmapFontIcon.CrossWorld)
                    {
                        ImGui.TextUnformatted($"{(char)SeIconChar.CrossWorld}");
                    }
                    else
                    {
                        ImGui.TextUnformatted($"[{iconChunk.Icon}]");
                    }
                    break;
                    
                default:
                    // Fallback for unknown chunk types
                    ImGui.TextUnformatted(chunk.StringValue());
                    break;
            }
        }
    }

    private void DrawSimpleChunks(IReadOnlyList<Chunk> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            
            if (i > 0)
                ImGui.SameLine();
            
            // Handle different chunk types
            switch (chunk)
            {
                case TextChunk textChunk:
                    // Get chunk color - use white for DM windows for better readability
                    var color = 0xFFFFFFFF; // Always use white in DM context
                    
                    using (ImRaii.PushColor(ImGuiCol.Text, ColourUtil.RgbaToAbgr(color)))
                    {
                        ImGui.TextUnformatted(textChunk.Content);
                    }
                    break;
                    
                case IconChunk iconChunk:
                    // Simple icon rendering - just show the icon ID as text for now
                    // In a full implementation, you'd render the actual icon
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xFF888888))
                    {
                        ImGui.TextUnformatted($"[{iconChunk.Icon}]");
                    }
                    break;
                    
                default:
                    // Fallback for unknown chunk types
                    ImGui.TextUnformatted(chunk.StringValue());
                    break;
            }
        }
    }

    /// <summary>
    /// Draws the empty state when no messages have been exchanged.
    /// </summary>
    private void DrawEmptyState(float availableHeight)
    {
        var windowWidth = ImGui.GetContentRegionAvail().X;
        var centerY = availableHeight / 2;

        // Center the empty state message
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + centerY - 40);

        var text = $"Beginning of conversation with {Player.DisplayName}";
        var textSize = ImGui.CalcTextSize(text);
        var centerX = (windowWidth - textSize.X) / 2;
        
        ImGui.SetCursorPosX(centerX);
        
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextUnformatted(text);
        }

        // Add a friendly message
        var friendlyText = "Send a message to start chatting!";
        var friendlyTextSize = ImGui.CalcTextSize(friendlyText);
        var friendlyCenterX = (windowWidth - friendlyTextSize.X) / 2;
        
        ImGui.SetCursorPosX(friendlyCenterX);
        
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextUnformatted(friendlyText);
        }
    }

    /// <summary>
    /// Draws the minimize button on the left side of the input area.
    /// </summary>
    private void DrawMinimizeButton(Vector2 buttonSize)
    {
        if (Plugin.Config.ModernUIEnabled)
        {
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                var minimizeHovered = false;
                using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, GetButtonAlpha("minimize", out minimizeHovered)))
                {
                    if (ImGuiUtil.IconButton(FontAwesomeIcon.Minus, "convert-to-tab"))
                    {
                        ConvertWindowToTab();
                    }
                }
            }
        }
        else
        {
            var minimizeHovered = false;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, GetButtonAlpha("minimize", out minimizeHovered)))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Minus, "convert-to-tab"))
                {
                    ConvertWindowToTab();
                }
            }
        }
        
        if (ImGui.IsItemHovered())
        {
            if (Plugin.Config.ModernUIEnabled)
                ModernUI.DrawModernTooltip("Convert to Tab", Plugin.Config);
            else
                ImGuiUtil.Tooltip("Convert to Tab");
        }
    }

    /// <summary>
    /// Draws the DM-specific input area that sends tells to the target player.
    /// </summary>
    private void DrawDMInputArea()
    {
        if (Plugin.Config.ModernUIEnabled)
        {
            ModernUI.DrawModernSeparator(Plugin.Config);
        }
        else
        {
            ImGui.Separator();
        }
        
        // Better button layout calculation - give even more space to buttons
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var buttonSize = new Vector2(24, 24);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        
        // Count buttons more accurately (right side buttons only)
        var buttonCount = IsPlayerAlreadyFriend() ? 2 : 3; // invite, (friend), adventurer plate
        var totalButtonWidth = (buttonSize.X * buttonCount) + (spacing * (buttonCount + 3)); // More extra spacing for safety
        
        // Account for minimize button on the left side
        var minimizeButtonWidth = buttonSize.X + spacing;
        
        // Make input field shorter to accommodate both left minimize button and right action buttons
        // Increased margin from 20 to 40 to give more space for buttons
        var inputWidth = Math.Max(80, availableWidth - totalButtonWidth - minimizeButtonWidth - 40);

        // Draw minimize button on the left side
        DrawMinimizeButton(buttonSize);
        ImGui.SameLine();

        // Input field for DM messages - always treats non-command input as tells
        var inputType = ChatType.TellOutgoing;
        var isCommand = DMChat.Trim().StartsWith('/');
        
        // Validate commands and set appropriate color (only when needed)
        if (isCommand)
        {
            var command = DMChat.Split(' ')[0];
            if (ChatLogWindow.TextCommandChannels.TryGetValue(command, out var channel))
                inputType = channel;

            if (!ChatLogWindow.IsValidCommand(command))
                inputType = ChatType.Error;
        }

        var inputColour = Plugin.Config.ChatColours.TryGetValue(inputType, out var inputCol) ? inputCol : inputType.DefaultColor();

        if (isCommand && ChatLogWindow.Plugin.ExtraChat.ChannelCommandColours.TryGetValue(DMChat.Split(' ')[0], out var ecColour))
            inputColour = ecColour;

        var push = inputColour != null;
        using (ImRaii.PushColor(ImGuiCol.Text, push ? ColourUtil.RgbaToAbgr(inputColour!.Value) : 0, push))
        {
            var isChatEnabled = !DMTab.InputDisabled;
            
            var flags = ImGuiInputTextFlags.EnterReturnsTrue | (!isChatEnabled ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
            ImGui.SetNextItemWidth(inputWidth);
            
            // Enhanced input field with better visual feedback
            var hasError = isCommand && !ChatLogWindow.IsValidCommand(DMChat.Split(' ')[0]);
            
            if (Plugin.Config.ModernUIEnabled)
            {
                var (styleScope, colorScope) = ModernUI.PushEnhancedInputStyle(Plugin.Config, DMInputFocused, hasError, _currentUIAlpha);
                using (styleScope)
                using (colorScope)
                {
                    DrawInputField(isChatEnabled, flags);
                }
            }
            else
            {
                DrawInputField(isChatEnabled, flags);
            }
            
            var inputActive = ImGui.IsItemActive();
            DMInputFocused = isChatEnabled && inputActive;

            // Process keybinds that have modifiers while the chat is focused.
            if (inputActive)
            {
                ChatLogWindow.Plugin.Functions.KeybindManager.HandleKeybinds(KeyboardSource.ImGui, true, true);
                LastActivityTime = FrameTime;
            }
        }

        // Draw action buttons on the same line as the input field
        ImGui.SameLine();
        DrawActionButtons();

        // Typing indicator removed per user request - was showing "Typing..." animation below input
    }

    /// <summary>
    /// Draws the input field with placeholder text.
    /// </summary>
    /// <param name="isChatEnabled">Whether chat input is enabled</param>
    /// <param name="flags">Input text flags</param>
    private void DrawInputField(bool isChatEnabled, ImGuiInputTextFlags flags)
    {
        // Track if we should maintain focus after sending
        var shouldMaintainFocus = false;
        
        // Focus input field if Activate is set (similar to ChatLogWindow)
        if (isChatEnabled && Activate)
        {
            ImGui.SetKeyboardFocusHere();
            Activate = false; // Reset after use
        }
        
        // Placeholder shows that this is a direct message input
        var placeholder = isChatEnabled ? $"Message {Player.TabName}..." : Language.ChatLog_DisabledInput;
        
        var inputResult = ImGui.InputTextWithHint("##dm-chat-input", placeholder, ref DMChat, 500, flags);
        
        if (inputResult)
        {
            // Send message on Enter - but only if not empty
            if (!string.IsNullOrWhiteSpace(DMChat))
            {
                SendDMMessage();
                shouldMaintainFocus = true; // Maintain focus after sending
            }
        }
        
        // FALLBACK: Check if Enter key is being pressed manually (in case EnterReturnsTrue flag isn't working)
        if (ImGui.IsItemActive() && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
        {
            if (!string.IsNullOrWhiteSpace(DMChat))
            {
                SendDMMessage();
                shouldMaintainFocus = true; // Maintain focus after sending
            }
        }
        
        // Maintain focus on the input field after sending a message (only if KeepInputFocus is enabled)
        if (shouldMaintainFocus && Plugin.Config.KeepInputFocus)
        {
            ImGui.SetKeyboardFocusHere(-1); // Focus the input field (1 item back)
        }

        // Handle Escape key to clear input
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyDown(ImGuiKey.Escape))
        {
            DMChat = string.Empty;
        }
    }

    /// <summary>
    /// Sends a DM message to the target player.
    /// </summary>
    private void SendDMMessage()
    {
        if (string.IsNullOrWhiteSpace(DMChat))
            return;

        var trimmedMessage = DMChat.Trim();
        
        try
        {
            // If it's a command, send it as-is (but not as a tell)
            if (trimmedMessage.StartsWith('/'))
            {
                // Don't send commands as tells - send them directly
                ChatBox.SendMessage(trimmedMessage);
            }
            else
            {
                // Send as a tell to the target player
                var tellCommand = $"/tell {Player.DisplayName} {trimmedMessage}";
                
                Plugin.Log.Debug($"SendDMMessage: Sending tell command: {tellCommand}");
                
                // Track this as a recent receiver BEFORE sending so error messages can be routed
                ChatLogWindow.Plugin.DMMessageRouter.TrackOutgoingTell(Player);
                
                // Send the tell command - the game will echo it back as TellOutgoing
                // which will be processed by the message system and routed to this DM
                ChatBox.SendMessage(tellCommand);
                
                Plugin.Log.Debug($"SendDMMessage: Tell command sent, waiting for game to echo back as TellOutgoing");
                
                // DON'T display the message immediately - let the game's echo handle it
                // This prevents duplicate messages
            }

            // Clear input after sending
            DMChat = string.Empty;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"SendDMMessage: Exception occurred: {ex.Message}");
            Plugin.Log.Error($"SendDMMessage: Stack trace: {ex.StackTrace}");
            DMErrorHandler.HandleMessageSendError(Player, trimmedMessage, ex);
            DMErrorHandler.LogDetailedError("SendDMMessage", ex, new Dictionary<string, object>
            {
                ["PlayerName"] = Player.Name,
                ["MessageLength"] = trimmedMessage.Length,
                ["IsCommand"] = trimmedMessage.StartsWith('/')
            });
        }
    }

    /// <summary>
    /// Displays an error message in the DM interface.
    /// </summary>
    public void DisplayErrorMessage(string errorText)
    {
        try
        {
            var errorMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, errorText)
                },
                new ChatCode((ushort)ChatType.Error)
            );
            
            DMTab.AddMessage(errorMessage, unread: false);
            History.AddMessage(errorMessage, isIncoming: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to display error message: {ex.Message}");
        }
    }

    /// <summary>
    /// Displays an informational message in the DM interface.
    /// </summary>
    public void DisplayInfoMessage(string infoText)
    {
        try
        {
            var infoMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, $"[Info] {infoText}")
                },
                new ChatCode((ushort)ChatType.System)
            );
            
            DMTab.AddMessage(infoMessage, unread: false);
            History.AddMessage(infoMessage, isIncoming: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to display info message: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts technical exceptions to user-friendly error messages.
    /// </summary>
    private string GetUserFriendlyErrorMessage(Exception ex)
    {
        return ex.Message switch
        {
            var msg when msg.Contains("message is empty") => "Cannot send empty message",
            var msg when msg.Contains("message is longer than 500 bytes") => "Message is too long (max 500 characters)",
            var msg when msg.Contains("message contained invalid characters") => "Message contains invalid characters",
            _ => "Unable to send message. Please try again."
        };
    }

    /// <summary>
    /// Invites the player to party.
    /// </summary>
    private void InviteToParty()
    {
        try
        {
            // Get current player's world
            var currentPlayerWorld = GetCurrentPlayerWorld();
            var currentPlayerHomeWorld = GetCurrentPlayerHomeWorld();
            
            // Check if we're on our home world and the target's home world matches
            var isOnHomeWorld = currentPlayerWorld == currentPlayerHomeWorld;
            var targetIsFromSameHomeWorld = currentPlayerHomeWorld == Player.HomeWorld;
            var shouldUseSameWorldInvite = isOnHomeWorld && targetIsFromSameHomeWorld;
            
            Plugin.Log.Debug($"Party invite analysis - Your current world: {currentPlayerWorld}, Your home world: {currentPlayerHomeWorld}, Target's home world: {Player.HomeWorld}");
            Plugin.Log.Debug($"On home world: {isOnHomeWorld}, Target from same home world: {targetIsFromSameHomeWorld}, Use same-world invite: {shouldUseSameWorldInvite}");
            
            if (shouldUseSameWorldInvite)
            {
                // Both on same home world - use the game function
                GameFunctions.Party.InviteSameWorld(Player.Name, (ushort)Player.HomeWorld, 0);
                Plugin.Log.Debug($"Sent same-world party invite to {Player.Name} on world {Player.HomeWorld}");
            }
            else
            {
                // Cross-world scenario - try to find content ID from recent messages
                Plugin.Log.Debug($"Cross-world invite scenario detected");
                
                var contentId = TryGetPlayerContentId();
                if (contentId != 0)
                {
                    Plugin.Log.Debug($"Found content ID {contentId:X16} for {Player.Name}, using cross-world game function");
                    GameFunctions.Party.InviteOtherWorld(contentId, (ushort)Player.HomeWorld);
                    Plugin.Log.Debug($"Sent cross-world party invite using game function with content ID");
                }
                else
                {
                    Plugin.Log.Debug($"No content ID available, falling back to chat command");
                    
                    // For cross-world invites without content ID, the /invite command often fails
                    // Let's try a few different approaches
                    var success = false;
                    
                    // Try 1: Simple invite command
                    try
                    {
                        var inviteCommand = $"/invite \"{Player.Name}\"";
                        ChatBox.SendMessage(inviteCommand);
                        Plugin.Log.Debug($"Sent fallback party invite command: {inviteCommand}");
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"Simple invite command failed: {ex.Message}");
                    }
                    
                    // If we reach here and the command was sent, we'll wait to see if it works
                    // The error handling will show if it failed
                    if (success)
                    {
                        // Show a helpful message to the user about cross-world limitations
                        DisplayInfoMessage("Cross-world party invite sent. If it fails, the player may need to be on the same world or you may need to use the in-game Party Finder.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to invite {Player} to party: {ex.Message}");
            
            // Final fallback to basic chat command
            try
            {
                var inviteCommand = $"/invite \"{Player.Name}\"";
                ChatBox.SendMessage(inviteCommand);
                Plugin.Log.Debug($"Final fallback: Sent party invite command: {inviteCommand}");
            }
            catch (Exception fallbackEx)
            {
                Plugin.Log.Error($"Final fallback invite command also failed: {fallbackEx.Message}");
            }
        }
    }
    
    /// <summary>
    /// Gets the most recent valid content ID from messages in this DM conversation.
    /// First tries DM messages, then searches the main chat log for messages from this player.
    /// </summary>
    /// <returns>Content ID or 0 if not found</returns>
    private ulong GetMostRecentValidContentId()
    {
        try
        {
            Plugin.Log.Debug($"GetMostRecentValidContentId: Searching for content ID for {Player.Name}");
            
            // First, check messages in the DM tab (most recent)
            using var dmMessages = DMTab.Messages.GetReadOnly(100);
            Plugin.Log.Debug($"GetMostRecentValidContentId: Checking {dmMessages.Count} DM tab messages");
            foreach (var message in dmMessages.Reverse()) // Check most recent first
            {
                if (message.ContentId != 0)
                {
                    Plugin.Log.Debug($"Found valid content ID {message.ContentId:X16} from DM tab message");
                    return message.ContentId;
                }
            }
            
            // If not found in DM tab, check DM history
            var recentMessages = History.GetRecentMessages(50);
            Plugin.Log.Debug($"GetMostRecentValidContentId: Checking {recentMessages.Length} DM history messages");
            foreach (var message in recentMessages.Reverse())
            {
                if (message.ContentId != 0)
                {
                    Plugin.Log.Debug($"Found valid content ID {message.ContentId:X16} from DM history");
                    return message.ContentId;
                }
            }
            
            // If still not found, search the main chat log for messages from this player
            // This is where the right-click context menu gets its content IDs from
            Plugin.Log.Debug($"No content ID found in DM messages, searching main chat log for {Player.Name}");
            var mainChatContentId = SearchMainChatForPlayerContentId();
            if (mainChatContentId != 0)
            {
                Plugin.Log.Debug($"Found valid content ID {mainChatContentId:X16} from main chat log");
                return mainChatContentId;
            }
            
            Plugin.Log.Debug($"No valid content ID found for {Player.Name} anywhere");
            return 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to get valid content ID for {Player.Name}: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Searches the main chat log for messages from this player to find a content ID.
    /// This mimics how the right-click context menu works.
    /// </summary>
    /// <returns>Content ID or 0 if not found</returns>
    private ulong SearchMainChatForPlayerContentId()
    {
        try
        {
            // Access the current tab from the Plugin instance (same way as ChatLogWindow does)
            var currentTab = ChatLogWindow.Plugin.CurrentTab;
            if (currentTab == null)
            {
                Plugin.Log.Debug("SearchMainChatForPlayerContentId: No current tab available in main chat");
                return 0;
            }
            
            Plugin.Log.Debug($"SearchMainChatForPlayerContentId: Searching main chat tab '{currentTab.Name}' for {Player.Name}");
            
            // Search recent messages in the main chat for this player
            using var mainMessages = currentTab.Messages.GetReadOnly(500); // Search more messages in main chat
            Plugin.Log.Debug($"SearchMainChatForPlayerContentId: Checking {mainMessages.Count} main chat messages");
            
            var messagesChecked = 0;
            var messagesFromPlayer = 0;
            
            foreach (var message in mainMessages.Reverse()) // Most recent first
            {
                messagesChecked++;
                
                if (message.ContentId != 0)
                {
                    // Check if this message is from our target player
                    if (message.IsFromPlayer(Player))
                    {
                        messagesFromPlayer++;
                        Plugin.Log.Debug($"Found content ID {message.ContentId:X16} for {Player.Name} in main chat (checked {messagesChecked} messages, found {messagesFromPlayer} from player)");
                        return message.ContentId;
                    }
                }
            }
            
            Plugin.Log.Debug($"SearchMainChatForPlayerContentId: No messages from {Player.Name} found in main chat with valid content ID (checked {messagesChecked} messages, found {messagesFromPlayer} from player)");
            return 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to search main chat for {Player.Name} content ID: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Tries to get the player's content ID from recent messages or other sources.
    /// </summary>
    /// <returns>Content ID or 0 if not found</returns>
    private ulong TryGetPlayerContentId()
    {
        try
        {
            // Try to get content ID from recent messages in this DM
            using var messages = DMTab.Messages.GetReadOnly(100); // Check last 100 messages
            foreach (var message in messages.Reverse()) // Check most recent first
            {
                if (message.ContentId != 0 && message.IsFromPlayer(Player))
                {
                    Plugin.Log.Debug($"Found content ID {message.ContentId:X16} from recent message");
                    return message.ContentId;
                }
            }
            
            // Try to get from DM history
            var recentMessages = History.GetRecentMessages(50);
            foreach (var message in recentMessages.Reverse())
            {
                if (message.ContentId != 0 && message.IsFromPlayer(Player))
                {
                    Plugin.Log.Debug($"Found content ID {message.ContentId:X16} from DM history");
                    return message.ContentId;
                }
            }
            
            Plugin.Log.Debug($"No content ID found for {Player.Name} in recent messages");
            return 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to get content ID for {Player.Name}: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Gets the current player's home world ID.
    /// </summary>
    /// <returns>Home world ID or 0 if not available</returns>
    private uint GetCurrentPlayerHomeWorld()
    {
        try
        {
            // Try to get from ObjectTable first (more reliable when logged in)
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer != null)
            {
                return localPlayer.HomeWorld.RowId;
            }
            
            // Fallback to ClientState
            if (Plugin.ClientState.LocalPlayer != null)
            {
                return Plugin.ClientState.LocalPlayer.HomeWorld.RowId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to get current player home world: {ex.Message}");
        }
        
        return 0;
    }
    
    /// <summary>
    /// Gets the current player's world ID (the world they are currently on, not their home world).
    /// </summary>
    /// <returns>World ID or 0 if not available</returns>
    private uint GetCurrentPlayerWorld()
    {
        try
        {
            // Try to get from ObjectTable first (more reliable when logged in)
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer != null)
            {
                // Use CurrentWorld for the world the player is currently on, not HomeWorld
                return localPlayer.CurrentWorld.RowId;
            }
            
            // Fallback to ClientState
            if (Plugin.ClientState.LocalPlayer != null)
            {
                return Plugin.ClientState.LocalPlayer.CurrentWorld.RowId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to get current player world: {ex.Message}");
        }
        
        return 0;
    }

    /// <summary>
    /// Checks if the player is already in the friend list (cached for performance).
    /// OPTIMIZED: Reduced frequency and simplified logic.
    /// </summary>
    /// <returns>True if the player is already a friend, false otherwise</returns>
    private bool IsPlayerAlreadyFriend()
    {
        // OPTIMIZATION: Use longer cache interval to reduce overhead
        if (FrameTime - _lastFriendCheck < FriendCheckInterval)
        {
            return _cachedIsFriend;
        }

        _lastFriendCheck = FrameTime;
        
        try
        {
            // OPTIMIZATION: Simplified friend checking using GameFunctions
            var friends = GameFunctions.GameFunctions.GetFriends();
            _cachedIsFriend = friends.Any(friend => 
            {
                // Convert the FixedSizeArray32<byte> to string
                var friendName = System.Text.Encoding.UTF8.GetString(friend.Name).TrimEnd('\0');
                return string.Equals(Player.Name, friendName, StringComparison.OrdinalIgnoreCase);
            });
            
            return _cachedIsFriend;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"IsPlayerAlreadyFriend: Failed to check friend status for {Player.Name}: {ex.Message}");
            _cachedIsFriend = false;
            return false; // If we can't check, show the button to be safe
        }
    }

    /// <summary>
    /// Adds the player as a friend.
    /// </summary>
    private void AddFriend()
    {
        try
        {
            // Use quotes around the name in case it contains spaces
            var friendCommand = $"/friendlist add \"{Player.Name}\"";
            ChatBox.SendMessage(friendCommand);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to add {Player} as friend: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the adventurer plate for the player using the same logic as the working context menu.
    /// </summary>
    private void OpenAdventurerPlate()
    {
        try
        {
            // Use the same approach as the working context menu in PayloadHandler.cs
            // Get the most recent message with a valid content ID from this player
            var validContentId = GetMostRecentValidContentId();
            
            if (validContentId != 0)
            {
                Plugin.Log.Debug($"Opening adventurer plate for {Player.Name} with content ID {validContentId:X16}");
                
                // Use the exact same logic as the working context menu
                if (!GameFunctions.GameFunctions.TryOpenAdventurerPlate(validContentId))
                {
                    WrapperUtil.AddNotification(Language.Context_AdventurerPlateError, NotificationType.Warning);
                    Plugin.Log.Warning($"TryOpenAdventurerPlate failed for {Player.Name} with content ID {validContentId:X16}");
                }
                else
                {
                    Plugin.Log.Debug($"Successfully opened adventurer plate for {Player.Name}");
                }
            }
            else
            {
                Plugin.Log.Debug($"No valid content ID found for {Player.Name}, showing error notification");
                WrapperUtil.AddNotification(Language.Context_AdventurerPlateError, NotificationType.Warning);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to open adventurer plate for {Player}: {ex.Message}");
            WrapperUtil.AddNotification(Language.Context_AdventurerPlateError, NotificationType.Warning);
        }
    }

    public override void PostDraw()
    {
        // PERSISTENCE: Update DM window state in configuration when position/size changes
        try
        {
            if (Position.HasValue && Size.HasValue)
            {
                DMManager.Instance.UpdateDMWindowState(Player, Position.Value, Size.Value);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"PostDraw: Failed to update DM window state: {ex.Message}");
        }

        // Pop custom font if it was pushed
        // TODO: Fix font manager integration
        // if (Plugin.Config.FontsEnabled && ChatLogWindow.Plugin.FontManager.HasFonts)
        // {
        //     ChatLogWindow.Plugin.FontManager.PopFont();
        // }

        // End modern styling
        ModernUI.EndModernStyle();

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();
    }

    public override void OnClose()
    {
        // Decrement window count for cascading
        lock (WindowCountLock)
        {
            WindowCount = Math.Max(0, WindowCount - 1);
        }

        // Remove from window system
        ChatLogWindow.Plugin.WindowSystem.RemoveWindow(this);

        // PERSISTENCE: Notify DMManager that this window is closed so it can be removed from persistence
        try
        {
            DMManager.Instance.CloseDMWindow(Player);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"OnClose: Failed to notify DMManager of window closure: {ex.Message}");
        }
    }

    private enum HideState
    {
        None,
        Cutscene,
        CutsceneOverride,
        User,
        Battle
    }

    private HideState CurrentHideState = HideState.None;

    private bool HideStateCheck()
    {
        // if the chat has no hide state set, and the player has entered battle, we hide chat if they have configured it
        if (DMTab.HideInBattle && CurrentHideState == HideState.None && Plugin.InBattle)
            CurrentHideState = HideState.Battle;

        // If the chat is hidden because of battle, we reset it here
        if (CurrentHideState is HideState.Battle && !Plugin.InBattle)
            CurrentHideState = HideState.None;

        // if the chat has no hide state and in a cutscene, set the hide state to cutscene
        if (DMTab.HideDuringCutscenes && CurrentHideState == HideState.None && (Plugin.CutsceneActive || Plugin.GposeActive))
        {
            if (ChatLogWindow.Plugin.Functions.Chat.CheckHideFlags())
                CurrentHideState = HideState.Cutscene;
        }

        // if the chat is hidden because of a cutscene and no longer in a cutscene, set the hide state to none
        if (CurrentHideState is HideState.Cutscene or HideState.CutsceneOverride && !Plugin.CutsceneActive && !Plugin.GposeActive)
            CurrentHideState = HideState.None;

        // if the chat is hidden because of a cutscene and the chat has been activated, show chat
        if (CurrentHideState == HideState.Cutscene && ChatLogWindow.Activate)
            CurrentHideState = HideState.CutsceneOverride;

        // if the user hid the chat and is now activating chat, reset the hide state
        if (CurrentHideState == HideState.User && ChatLogWindow.Activate)
            CurrentHideState = HideState.None;

        return CurrentHideState is HideState.Cutscene or HideState.User or HideState.Battle || (DMTab.HideWhenNotLoggedIn && !Plugin.ClientState.IsLoggedIn);
    }
}
