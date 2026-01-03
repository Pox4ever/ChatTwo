using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
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
    
    // PERFORMANCE: Async initialization to prevent constructor stutter
    private bool _needsAsyncInitialization = false;
    private bool _isInitializing = false;
    private bool _initializationComplete = false;
    private long _initializationStartTime = 0;
    private Task? _initializationTask = null;
    
    // PERFORMANCE: Render caching to reduce FPS impact
    private bool _renderCacheValid = false;
    private int _lastRenderedMessageCount = 0;
    private long _lastRenderTime = 0;
    private const long RenderCacheInterval = 33; // ~30 FPS for message rendering (vs 60+ for UI)

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

        // PERFORMANCE FIX: Defer expensive operations to avoid constructor stutter
        // Mark that we need to load content asynchronously
        _needsAsyncInitialization = true;
        _initializationStartTime = Environment.TickCount64;
        
        Plugin.Log.Debug($"DMWindow constructor completed for {Player.DisplayName} - deferring expensive operations");
    }

    /// <summary>
    /// Starts the asynchronous initialization process to load message history without blocking the UI.
    /// </summary>
    private void StartAsyncInitialization()
    {
        if (_isInitializing || _initializationComplete)
            return;

        _isInitializing = true;
        _needsAsyncInitialization = false;

        Plugin.Log.Debug($"Starting async initialization for DM window: {Player.DisplayName}");

        // Start the initialization task on a background thread
        _initializationTask = Task.Run(async () =>
        {
            try
            {
                // Add a small delay to let the window render first
                await Task.Delay(50);

                // Load message history on background thread
                await LoadMessageHistoryAsync();

                // Reprocess existing messages for colors
                await ReprocessExistingMessagesForColorsAsync();

                _initializationComplete = true;
                _isInitializing = false;

                var elapsed = Environment.TickCount64 - _initializationStartTime;
                Plugin.Log.Debug($"Async initialization completed for {Player.DisplayName} in {elapsed}ms");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Async initialization failed for {Player.DisplayName}: {ex.Message}");
                _isInitializing = false;
                _initializationComplete = true; // Mark as complete even on error to stop loading state
            }
        });
    }

    /// <summary>
    /// Asynchronous version of LoadMessageHistory that doesn't block the UI thread.
    /// </summary>
    private async Task LoadMessageHistoryAsync()
    {
        try
        {
            Plugin.Log.Debug($"LoadMessageHistoryAsync: Starting history load for {Player.DisplayName}");
            
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
                using var existingMessages = DMTab.Messages.GetReadOnly(100); // Short timeout for async
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
                Plugin.Log.Warning($"LoadMessageHistoryAsync: MessageManager or Store is null for {Player.DisplayName} - cannot load history");
                return;
            }

            var historyCount = Math.Max(1, Math.Min(200, Plugin.Config.DMMessageHistoryCount));
            Plugin.Log.Debug($"Loading {historyCount} messages from history for {Player.DisplayName}");
            
            // Load messages from the persistent MessageStore database
            var loadedMessages = new List<Message>();
            
            // PERFORMANCE: Reduced search limit and add yielding for better responsiveness
            var searchLimit = Math.Min(10000, historyCount * 50); // Reduced from 50000 to 10000
            var processedCount = 0;
            
            await Task.Run(() =>
            {
                using var messageEnumerator = ChatLogWindow.Plugin.MessageManager.Store.GetMostRecentMessages(count: searchLimit);
                foreach (var message in messageEnumerator)
                {
                    // Yield control periodically to prevent blocking
                    if (++processedCount % 1000 == 0)
                    {
                        Task.Yield();
                    }

                    // Check if this is a tell message related to our target player
                    if (message.IsTell() && message.IsRelatedToPlayer(Player))
                    {
                        loadedMessages.Add(message);
                        
                        // Stop after finding enough messages
                        if (loadedMessages.Count >= historyCount)
                            break;
                    }
                }
            });
            
            // Take the most recent messages as configured
            var recentMessages = loadedMessages
                .OrderByDescending(m => m.Date)
                .Take(historyCount)
                .OrderBy(m => m.Date)
                .ToArray();
            
            if (recentMessages.Length > 0)
            {
                Plugin.Log.Info($"Loaded {recentMessages.Length} message(s) from history for {Player.DisplayName}");
                
                // Add messages on the main thread
                await Task.Run(() =>
                {
                    foreach (var message in recentMessages)
                    {
                        // Convert old outgoing messages to "You:" format for consistency
                        var processedMessage = ConvertOutgoingMessageFormat(message);
                        
                        DMTab.AddMessage(processedMessage, unread: false);
                        // Also add to in-memory history for consistency
                        History.AddMessage(processedMessage, isIncoming: processedMessage.IsFromPlayer(Player));
                    }
                });
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
    /// Asynchronous version of ReprocessExistingMessagesForColors that doesn't block the UI thread.
    /// </summary>
    private async Task ReprocessExistingMessagesForColorsAsync()
    {
        try
        {
            Plugin.Log.Debug($"ReprocessExistingMessagesForColorsAsync: Starting for {Player.DisplayName}");
            
            // Get all current messages from the DMTab
            var existingMessages = new List<Message>();
            try
            {
                using var messages = DMTab.Messages.GetReadOnly(1000); // Shorter timeout for async
                existingMessages.AddRange(messages);
                Plugin.Log.Debug($"Found {existingMessages.Count} existing messages to reprocess");
            }
            catch (TimeoutException)
            {
                Plugin.Log.Warning("Timeout getting existing messages for color reprocessing");
                return;
            }
            
            if (existingMessages.Count == 0)
            {
                Plugin.Log.Debug("No existing messages to reprocess");
                return;
            }
            
            // Process messages on background thread
            var processedMessages = await Task.Run(() =>
            {
                var processed = new List<Message>();
                var processedCount = 0;
                
                foreach (var message in existingMessages)
                {
                    // Yield control periodically
                    if (++processedCount % 100 == 0)
                    {
                        Task.Yield();
                    }
                    
                    // Apply colors and format conversion
                    var processedMessage = ConvertOutgoingMessageFormat(message);
                    processed.Add(processedMessage);
                }
                
                return processed;
            });
            
            // Update messages on main thread
            DMTab.Messages.Clear();
            foreach (var processedMessage in processedMessages)
            {
                DMTab.AddMessage(processedMessage, unread: false);
            }
            
            Plugin.Log.Info($"Reprocessed {processedMessages.Count} messages with DM colors for {Player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to reprocess existing messages for colors: {ex.Message}");
        }
    }

    /// <summary>
    /// Draws a loading state while async initialization is in progress.
    /// </summary>
    private void DrawLoadingState(float availableHeight)
    {
        var windowWidth = ImGui.GetContentRegionAvail().X;
        var centerY = availableHeight / 2;

        // Center the loading message
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + centerY - 40);

        var text = $"Loading conversation with {Player.DisplayName}...";
        var textSize = ImGui.CalcTextSize(text);
        var centerX = (windowWidth - textSize.X) / 2;
        
        ImGui.SetCursorPosX(centerX);
        
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextUnformatted(text);
        }

        // Add a simple progress indicator
        var elapsed = Environment.TickCount64 - _initializationStartTime;
        var dots = new string('.', (int)((elapsed / 500) % 4)); // Animate dots every 500ms
        var progressText = $"Please wait{dots}";
        var progressTextSize = ImGui.CalcTextSize(progressText);
        var progressCenterX = (windowWidth - progressTextSize.X) / 2;
        
        ImGui.SetCursorPosX(progressCenterX);
        
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextUnformatted(progressText);
        }
    }

    /// <summary>
    /// Updates the window title to include unread indicators if there are unread messages.
    /// OPTIMIZED: Only update title when unread count actually changes.
    /// </summary>
    private void UpdateWindowTitle()
    {
        var unreadCount = History?.UnreadCount ?? 0;
        var baseTitle = $"DM: {Player.DisplayName}";
        
        // OPTIMIZATION: Cache the last title to avoid unnecessary string operations
        string newTitle;
        if (unreadCount > 0)
        {
            newTitle = $"{baseTitle} •{unreadCount}##dm-window-{Player.GetHashCode()}";
        }
        else
        {
            newTitle = $"{baseTitle}##dm-window-{Player.GetHashCode()}";
        }
        
        // Only update if title actually changed
        if (WindowName != newTitle)
        {
            WindowName = newTitle;
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
    /// Reprocesses all existing messages in the DMTab to apply DM colors.
    /// This is needed when a DM tab is popped out to a window, as the existing messages
    /// might not have colors applied yet.
    /// </summary>
    private void ReprocessExistingMessagesForColors()
    {
        try
        {
            Plugin.Log.Debug($"ReprocessExistingMessagesForColors: Starting for {Player.DisplayName}");
            
            // Get all current messages from the DMTab
            var existingMessages = new List<Message>();
            try
            {
                using var messages = DMTab.Messages.GetReadOnly(5000); // 5 second timeout
                existingMessages.AddRange(messages);
                Plugin.Log.Debug($"Found {existingMessages.Count} existing messages to reprocess");
            }
            catch (TimeoutException)
            {
                Plugin.Log.Warning("Timeout getting existing messages for color reprocessing");
                return;
            }
            
            if (existingMessages.Count == 0)
            {
                Plugin.Log.Debug("No existing messages to reprocess");
                return;
            }
            
            // Clear the current messages and re-add them with colors applied
            DMTab.Messages.Clear();
            
            foreach (var message in existingMessages)
            {
                // Apply colors and format conversion
                var processedMessage = ConvertOutgoingMessageFormat(message);
                DMTab.AddMessage(processedMessage, unread: false);
            }
            
            Plugin.Log.Info($"Reprocessed {existingMessages.Count} messages with DM colors for {Player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to reprocess existing messages for colors: {ex.Message}");
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
            // Apply colors to all messages first (same as DMTab)
            var coloredMessage = ApplyDMColorsToMessage(message);
            
            // Only convert outgoing tell messages
            if (coloredMessage.Code.Type != ChatType.TellOutgoing)
                return coloredMessage;
            
            // Check if this is an outgoing message (from us to the target player)
            if (!coloredMessage.IsToPlayer(Player))
                return coloredMessage;
            
            // Get the original sender text
            var originalSender = string.Join("", coloredMessage.Sender.Select(c => c.StringValue()));
            
            // If it already uses "You:" format, no conversion needed
            if (originalSender.StartsWith("You:"))
                return coloredMessage;
            
            // If it's the full format (>> PlayerName🌐World:), convert to "You:"
            if (originalSender.StartsWith(">> ") && originalSender.Contains(": "))
            {
                Plugin.Log.Debug($"Converting old outgoing message sender from '{originalSender}' to 'You: '");
                
                // Create new sender chunks with "You:" format and custom color if enabled
                var newSenderChunks = new List<Chunk>();
                if (Plugin.Config.UseDMCustomColors)
                {
                    newSenderChunks.Add(new TextChunk(ChunkSource.Sender, null, "You: ")
                    {
                        Foreground = Plugin.Config.DMOutgoingColor
                    });
                }
                else
                {
                    newSenderChunks.Add(new TextChunk(ChunkSource.Sender, null, "You: "));
                }
                
                // Keep the original content chunks (already colored)
                var contentChunks = coloredMessage.Content.ToList();
                
                return new Message(
                    coloredMessage.Receiver,
                    coloredMessage.ContentId,
                    coloredMessage.AccountId,
                    coloredMessage.Code,
                    newSenderChunks,
                    contentChunks,
                    coloredMessage.SenderSource,
                    coloredMessage.ContentSource
                );
            }
            
            // No conversion needed
            return coloredMessage;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to convert outgoing message format: {ex.Message}");
            return message; // Return original on error
        }
    }

    /// <summary>
    /// Applies custom DM colors to a message if enabled in configuration.
    /// </summary>
    /// <param name="message">The message to apply colors to</param>
    /// <returns>A new message with colors applied, or the original message if custom colors are disabled</returns>
    private Message ApplyDMColorsToMessage(Message message)
    {
        if (!Plugin.Config.UseDMCustomColors)
            return message;

        try
        {
            // Determine if this is an incoming or outgoing message
            bool isIncoming = message.Code.Type == ChatType.TellIncoming;
            bool isError = message.Code.Type == ChatType.Error;
            
            // Determine the color to use
            uint color;
            if (isError)
            {
                color = Plugin.Config.DMErrorColor;
            }
            else if (isIncoming)
            {
                color = Plugin.Config.DMIncomingColor;
            }
            else
            {
                color = Plugin.Config.DMOutgoingColor;
            }

            // Create new sender chunks with custom color
            var newSenderChunks = message.Sender.Select(chunk =>
            {
                if (chunk is TextChunk textChunk)
                {
                    return new TextChunk(textChunk.Source, textChunk.Link, textChunk.Content)
                    {
                        Foreground = color,
                        Glow = textChunk.Glow,
                        Italic = textChunk.Italic
                    };
                }
                return chunk;
            }).ToList();

            // Create new content chunks with custom color
            var newContentChunks = message.Content.Select(chunk =>
            {
                if (chunk is TextChunk textChunk)
                {
                    return new TextChunk(textChunk.Source, textChunk.Link, textChunk.Content)
                    {
                        Foreground = color,
                        Glow = textChunk.Glow,
                        Italic = textChunk.Italic
                    };
                }
                return chunk;
            }).ToList();

            return new Message(
                message.Receiver,
                message.ContentId,
                message.AccountId,
                message.Code,
                newSenderChunks,
                newContentChunks,
                message.SenderSource,
                message.ContentSource
            );
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to apply DM colors to message: {ex.Message}");
            return message; // Return original if coloring fails
        }
    }

    // Cache for DrawConditions to reduce overhead
    private long _lastConditionsCheck = 0;
    private bool _lastConditionsResult = true;
    
    // Cache for position/size to avoid updating every frame
    private Vector2? _lastPosition = null;
    private Vector2? _lastSize = null;
    private long _lastPositionSizeCheck = 0;

    public override bool DrawConditions()
    {
        FrameTime = Environment.TickCount64;
        
        // OPTIMIZATION: Early exit for closed windows
        if (!IsOpen)
            return false;
        
        // Use the same hiding logic as the main chat window for consistency
        if (ChatLogWindow.IsHidden)
            return false;
        
        // OPTIMIZATION: Reduce frequency of expensive checks to every 500ms instead of 100-200ms
        var shouldCheckExpensiveConditions = (FrameTime % 500) == 0;
        
        if (shouldCheckExpensiveConditions)
        {
            // Check window closure state
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
            _wasOpen = IsOpen;
            
            // Update cached conditions
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
        // PERFORMANCE: Minimize window flags to reduce ImGui overhead
        // Remove expensive window features that cause FPS drops
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        
        // OPTIMIZATION: Only apply expensive flags when necessary
        if (!DMTab.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;
        if (!DMTab.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;
            
        // PERFORMANCE: Minimal window flags while preserving functionality
        if (Plugin.Config.UseMinimalDMWindows)
        {
            // Reduce overhead but keep essential functionality
            Flags |= ImGuiWindowFlags.NoNav | 
                     ImGuiWindowFlags.NoFocusOnAppearing;
        }
        
        // DISABLED: Aggressive mode was breaking functionality
        // if (Plugin.Config.UseAggressivePerformanceMode)
        // {
        //     // This mode broke window headers and made windows transparent
        // }

        // OPTIMIZATION: Cache style application to avoid repeated work
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();

        // OPTIMIZATION: Only apply ModernUI when state changes
        var modernUIEnabled = Plugin.Config.ModernUIEnabled;
        if (modernUIEnabled != _lastModernUIEnabled)
        {
            _lastModernUIEnabled = modernUIEnabled;
        }
        
        if (modernUIEnabled)
        {
            ModernUI.BeginModernStyle(Plugin.Config);
        }

        // OPTIMIZATION: Simplified alpha calculation - only recalculate when focus changes
        var currentFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (currentFocused != _isWindowFocused)
        {
            _isWindowFocused = currentFocused;
            
            var alpha = DMTab.IndependentOpacity ? DMTab.Opacity : Plugin.Config.WindowAlpha;
            
            if (!_isWindowFocused)
            {
                var transparencyFactor = Plugin.Config.UnfocusedTransparency / 100f;
                _cachedBgAlpha = (alpha / 100f) * transparencyFactor;
            }
            else
            {
                _cachedBgAlpha = alpha / 100f;
            }
        }
        
        BgAlpha = _cachedBgAlpha;
    }

    public override void Draw()
    {
        // PERFORMANCE: Start frame timing for performance monitoring
        var frameStartTime = Environment.TickCount64;
        
        try
        {
            using var performanceScope = DMPerformanceProfiler.MeasureOperation($"DMWindow.Draw({Player.DisplayName})");
            
            // PERFORMANCE: Skip expensive ID push in minimal mode
            using var id = Plugin.Config.UseMinimalDMWindows ? null : ImRaii.PushId($"dm-window-{Player.GetHashCode()}");

            // PERFORMANCE: Handle async initialization to prevent stutter
            if (_needsAsyncInitialization && !_isInitializing)
            {
                StartAsyncInitialization();
            }

            // OPTIMIZATION: Skip focus calculations only in broken aggressive mode (disabled)
            // Always calculate focus properly to maintain functionality
            var currentFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            var focusChanged = currentFocused != _isWindowFocused;
            if (focusChanged)
            {
                _isWindowFocused = currentFocused;
            }
            
            // OPTIMIZATION: Only recalculate UI alpha when focus changes or transparency settings change
            if (focusChanged)
            {
                _currentUIAlpha = _isWindowFocused ? 1.0f : (Plugin.Config.UnfocusedTransparency / 100f);
            }
            
            // OPTIMIZATION: Skip transparency only when explicitly disabled
            var needsTransparency = _currentUIAlpha < 1.0f;
            var colorScopes = new List<IDisposable>();
            
            if (needsTransparency)
            {
                // Only push the essential color styles to reduce overhead
                var alphaValue = (uint)(255 * _currentUIAlpha) << 24;
                colorScopes.Add(ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.Text) & 0x00FFFFFF | alphaValue));
                colorScopes.Add(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.Button) & 0x00FFFFFF | alphaValue));
                colorScopes.Add(ImRaii.PushColor(ImGuiCol.FrameBg, ImGui.GetColorU32(ImGuiCol.FrameBg) & 0x00FFFFFF | alphaValue));
            }

            try
            {
                // Calculate space for input area (always show input)
                var inputHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
                var messageLogHeight = ImGui.GetContentRegionAvail().Y - inputHeight;

                // OPTIMIZATION: Use immediate timeout to avoid blocking the UI thread
                try
                {
                    // Show loading state during async initialization
                    if (!_initializationComplete)
                    {
                        DrawLoadingState(messageLogHeight);
                    }
                    else
                    {
                        using var messages = DMTab.Messages.GetReadOnly(0); // No timeout - immediate return
                        if (messages.Count == 0)
                        {
                            DrawEmptyState(messageLogHeight);
                        }
                        else
                        {
                            // PERFORMANCE: Smart message rendering with caching
                            var currentMessageCount = messages.Count;
                            var shouldRender = ShouldRenderMessages(currentMessageCount);
                            
                            if (shouldRender)
                            {
                                using var renderScope = DMPerformanceProfiler.MeasureOperation("MessageRendering");
                                DrawOptimizedMessageLog(messages, messageLogHeight);
                                _lastRenderedMessageCount = currentMessageCount;
                                _lastRenderTime = FrameTime;
                                _renderCacheValid = true;
                            }
                            else
                            {
                                // Skip expensive message rendering, just maintain scroll position
                                DrawCachedMessageArea(messageLogHeight);
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // If we can't get messages immediately, show appropriate state
                    if (!_initializationComplete)
                    {
                        DrawLoadingState(messageLogHeight);
                    }
                    else
                    {
                        DrawEmptyState(messageLogHeight);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"DMWindow.Draw: Error accessing messages: {ex.Message}");
                    if (!_initializationComplete)
                    {
                        DrawLoadingState(messageLogHeight);
                    }
                    else
                    {
                        DrawEmptyState(messageLogHeight);
                    }
                }

                // Draw DM-specific input area (always show)
                using var inputScope = DMPerformanceProfiler.MeasureOperation("InputArea");
                DrawDMInputArea();
                
                // OPTIMIZATION: Reduce activity tracking overhead
                if (_isWindowFocused)
                {
                    LastActivityTime = FrameTime;
                    // Only mark as read every 500ms to reduce overhead
                    if ((FrameTime % 500) == 0)
                    {
                        History.MarkAsRead();
                        DMTab.MarkAsRead();
                    }
                }
                
                // OPTIMIZATION: Only update window title occasionally to reduce string operations
                if ((FrameTime % 1000) == 0) // Update title every second
                {
                    UpdateWindowTitle();
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
        finally
        {
            // PERFORMANCE: Record frame time for monitoring
            var frameEndTime = Environment.TickCount64;
            var frameTimeMs = frameEndTime - frameStartTime;
            DMPerformanceProfiler.RecordFrameTime(frameTimeMs);
            
            // Log performance warnings if frame time is too high
            if (Plugin.Config.EnableDMPerformanceLogging && frameTimeMs > 16.67) // Slower than 60 FPS
            {
                Plugin.Log.Warning($"DM Window slow frame: {frameTimeMs:F2}ms for {Player.DisplayName}");
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
    /// OPTIMIZED: Reduced expensive time calculations.
    /// </summary>
    private void DrawActionButtonsInternal(Vector2 buttonSize)
    {
        // OPTIMIZATION: Remove expensive time calculation since we simplified GetButtonAlpha
        
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
    /// OPTIMIZED: Simplified animation to reduce per-frame calculations.
    /// </summary>
    private float GetButtonAlpha(string buttonId, out bool isHovered)
    {
        isHovered = ImGui.IsItemHovered();
        
        if (!Plugin.Config.ModernUIEnabled)
        {
            return 1f; // No animation when ModernUI is disabled
        }
        
        // OPTIMIZATION: Simplified hover animation without expensive math operations
        var baseAlpha = 0.8f;
        var hoverAlpha = 1f;
        
        if (isHovered)
        {
            // Simple linear interpolation instead of sine wave
            return hoverAlpha;
        }
        
        return baseAlpha;
    }

    /// <summary>
    /// Determines if we should render messages this frame based on performance heuristics.
    /// </summary>
    private bool ShouldRenderMessages(int currentMessageCount)
    {
        // Always render if message count changed
        if (currentMessageCount != _lastRenderedMessageCount)
        {
            _renderCacheValid = false;
            return true;
        }
        
        // Always render if cache is invalid
        if (!_renderCacheValid)
        {
            return true;
        }
        
        // Render at reduced frequency to maintain performance
        var timeSinceLastRender = FrameTime - _lastRenderTime;
        if (timeSinceLastRender >= RenderCacheInterval)
        {
            return true;
        }
        
        // Skip rendering this frame
        return false;
    }

    /// <summary>
    /// Draws a cached message area that maintains scroll position without expensive rendering.
    /// </summary>
    private void DrawCachedMessageArea(float childHeight)
    {
        // Create a child window to maintain scroll state without expensive message rendering
        using var child = ImRaii.Child("##cached-messages", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoBackground);
        if (!child.Success)
            return;
            
        // Just maintain the scroll area without rendering messages
        // This keeps the scrollbar and interaction working while skipping expensive rendering
        var messageCount = _lastRenderedMessageCount;
        if (messageCount > 0)
        {
            // Estimate content height based on message count to maintain scroll behavior
            var estimatedLineHeight = ImGui.GetTextLineHeightWithSpacing();
            var estimatedContentHeight = messageCount * estimatedLineHeight * 1.5f; // Rough estimate
            
            // Create invisible dummy to maintain scroll area size
            ImGui.Dummy(new Vector2(0, estimatedContentHeight));
        }
    }

    /// <summary>
    /// Draws the message log using optimized rendering with reduced frequency.
    /// </summary>
    private void DrawOptimizedMessageLog(IReadOnlyList<Message> messages, float childHeight)
    {
        // PERFORMANCE: Always use lightweight rendering - the ChatLogWindow.DrawMessageLog is the FPS killer
        // The issue is that ChatLogWindow.DrawMessageLog is designed for single-window rendering (tabs)
        // but when called from multiple DM windows, each window creates rendering overhead
        
        if (Plugin.Config.EnableDMPerformanceLogging)
        {
            using var renderScope = DMPerformanceProfiler.MeasureOperation("LightweightMessageLog");
            DrawLightweightMessageLog(messages, childHeight);
        }
        else
        {
            DrawLightweightMessageLog(messages, childHeight);
        }
        
        // DISABLED: This is the FPS killer - ChatLogWindow.DrawMessageLog from multiple windows
        // if (!Plugin.Config.UseLightweightDMRendering)
        // {
        //     ChatLogWindow.DrawMessageLog(DMTab, ChatLogWindow.PayloadHandler, childHeight, false);
        // }
    }

    /// <summary>
    /// Lightweight message renderer inspired by XIVInstantMessenger's approach.
    /// Uses display cap and virtual scrolling for better performance.
    /// </summary>
    private void DrawLightweightMessageLog(IReadOnlyList<Message> messages, float childHeight)
    {
        using var child = ImRaii.Child("##dm-messages-lightweight", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoBackground);
        if (!child.Success)
            return;

        try
        {
            using var messagesReadOnly = DMTab.Messages.GetReadOnly(0); // Immediate timeout
            
            // PERFORMANCE: XIVInstantMessenger-inspired display cap system
            // Only render the most recent N messages for performance
            var displayCap = Math.Min(messagesReadOnly.Count, Plugin.Config.MaxLinesToRender);
            var startIndex = Math.Max(0, messagesReadOnly.Count - displayCap);
            
            // Show performance warning if messages are hidden
            if (startIndex > 0)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
                {
                    ImGui.TextWrapped($"For performance, {startIndex} older messages are hidden.");
                }
                
                if (ImGui.Button($"Show {Math.Min(startIndex, 50)} more messages"))
                {
                    // Increase display cap temporarily
                    displayCap = Math.Min(messagesReadOnly.Count, displayCap + 50);
                    startIndex = Math.Max(0, messagesReadOnly.Count - displayCap);
                }
                ImGui.Separator();
            }
            
            // PERFORMANCE: Always use standard lightweight rendering with virtual scrolling
            DrawVirtualScrolledMessages(messagesReadOnly, startIndex, displayCap, childHeight);
            
            // Auto-scroll to bottom for new messages (XIVInstantMessenger style)
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1.0f)
            {
                ImGui.SetScrollHereY(1.0f);
            }
        }
        catch (TimeoutException)
        {
            // If we can't get messages immediately, show placeholder
            ImGui.TextDisabled("Loading messages...");
        }
    }

    /// <summary>
    /// Ultra-lightweight message rendering for aggressive performance mode.
    /// Sacrifices features for maximum FPS.
    /// </summary>
    private void DrawUltraLightweightMessages(IReadOnlyList<Message> messages, int startIndex, int displayCap, float childHeight)
    {
        // EXTREME PERFORMANCE: Render only text, no colors, no formatting, no virtual scrolling
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        
        for (var i = startIndex; i < Math.Min(messages.Count, startIndex + displayCap); i++)
        {
            var message = messages[i];
            
            // Ultra-simple rendering: just timestamp and text
            var timestamp = message.Date.ToString("HH:mm");
            var isIncoming = message.Code.Type == ChatType.TellIncoming;
            var sender = isIncoming ? Player.DisplayName : "You";
            var content = string.Join("", message.Content.Select(chunk => chunk.StringValue()));
            
            // Single line with minimal formatting
            ImGui.TextUnformatted($"[{timestamp}] {sender}: {content}");
        }
    }

    /// <summary>
    /// Standard virtual scrolled message rendering with XIVInstantMessenger optimizations.
    /// </summary>
    private void DrawVirtualScrolledMessages(IReadOnlyList<Message> messages, int startIndex, int displayCap, float childHeight)
    {
        // PERFORMANCE: Only render visible messages with optimized rendering
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var scrollY = ImGui.GetScrollY();
        var visibleStart = Math.Max(startIndex, (int)(scrollY / lineHeight) - 2);
        var visibleEnd = Math.Min(messages.Count, visibleStart + (int)(childHeight / lineHeight) + 4);
        
        // Add invisible spacer for messages before visible area
        if (visibleStart > startIndex)
        {
            ImGui.Dummy(new Vector2(0, (visibleStart - startIndex) * lineHeight));
        }
        
        // Render only visible messages with XIVInstantMessenger-style optimization
        bool? lastIsIncoming = null;
        for (var i = visibleStart; i < visibleEnd; i++)
        {
            if (i >= messages.Count) break;
            
            var message = messages[i];
            DrawOptimizedMessage(message, ref lastIsIncoming);
        }
        
        // Add invisible spacer for messages after visible area
        var remainingMessages = messages.Count - visibleEnd;
        if (remainingMessages > 0)
        {
            ImGui.Dummy(new Vector2(0, remainingMessages * lineHeight));
        }
    }

    /// <summary>
    /// Draws a single message with optimized rendering that mimics tab behavior.
    /// This avoids the expensive ChatLogWindow.DrawMessageLog overhead.
    /// </summary>
    private void DrawOptimizedMessage(Message message, ref bool? lastIsIncoming)
    {
        try
        {
            var isIncoming = message.Code.Type == ChatType.TellIncoming;
            var showSender = lastIsIncoming != isIncoming;
            lastIsIncoming = isIncoming;
            
            // Show sender header when switching between incoming/outgoing (XIVInstantMessenger style)
            if (showSender)
            {
                var senderText = isIncoming ? $"From {Player.DisplayName}" : "From You";
                var senderColor = isIncoming ? Plugin.Config.DMIncomingColor : Plugin.Config.DMOutgoingColor;
                
                if (Plugin.Config.UseDMCustomColors)
                {
                    // Use custom colors if enabled
                    ImGui.PushStyleColor(ImGuiCol.Text, senderColor);
                    ImGui.TextUnformatted(senderText);
                    ImGui.PopStyleColor();
                }
                else
                {
                    // Use default disabled text color
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                    ImGui.TextUnformatted(senderText);
                    ImGui.PopStyleColor();
                }
            }
            
            // Message content with indentation
            ImGui.Dummy(new Vector2(20f, 0f)); // Indent messages
            ImGui.SameLine();
            
            // Timestamp and message on same line
            if (DMTab.DisplayTimestamp)
            {
                var timestamp = message.Date.ToString("HH:mm");
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                ImGui.TextUnformatted($"[{timestamp}] ");
                ImGui.PopStyleColor();
                ImGui.SameLine();
            }
            
            // Message content with color - this is the key optimization
            // Instead of using ChatLogWindow's complex payload rendering, use simple text
            var contentText = string.Join("", message.Content.Select(chunk => chunk.StringValue()));
            var messageColor = isIncoming ? Plugin.Config.DMIncomingColor : Plugin.Config.DMOutgoingColor;
            
            if (Plugin.Config.UseDMCustomColors)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, messageColor);
                ImGui.TextWrapped(contentText);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextWrapped(contentText);
            }
        }
        catch (Exception ex)
        {
            // Fallback for any rendering errors
            ImGui.TextDisabled($"[Error rendering message: {ex.Message}]");
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
    /// OPTIMIZED: Increased cache interval and simplified logic to reduce overhead.
    /// </summary>
    /// <returns>True if the player is already a friend, false otherwise</returns>
    private bool IsPlayerAlreadyFriend()
    {
        // OPTIMIZATION: Use much longer cache interval to reduce overhead (30 seconds)
        const long ExtendedFriendCheckInterval = 30000; // 30 seconds
        
        if (FrameTime - _lastFriendCheck < ExtendedFriendCheckInterval)
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
            Plugin.Log.Warning($"IsPlayerAlreadyFriend: Failed to check friend status: {ex.Message}");
            // Return cached value on error to avoid repeated failures
            return _cachedIsFriend;
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
        // PERSISTENCE: Update DM window state in configuration ONLY when position/size changes
        // PERFORMANCE: Only check position/size every 10 frames (6 times per second at 60 FPS)
        var currentFrameTime = Environment.TickCount64;
        var shouldCheckPositionSize = (currentFrameTime - _lastPositionSizeCheck) > 166; // ~6 times per second
        
        if (shouldCheckPositionSize)
        {
            _lastPositionSizeCheck = currentFrameTime;
            
            try
            {
                if (Position.HasValue && Size.HasValue)
                {
                    // PERFORMANCE FIX: Only update when position or size actually changes
                    var currentPosition = Position.Value;
                    var currentSize = Size.Value;
                    
                    // Use larger threshold to avoid constant updates due to floating-point precision
                    var positionThreshold = 2.0f; // Increased from 1f to 2f
                    var sizeThreshold = 2.0f;     // Increased from 1f to 2f
                    
                    var positionChanged = _lastPosition == null || 
                        Math.Abs(_lastPosition.Value.X - currentPosition.X) > positionThreshold || 
                        Math.Abs(_lastPosition.Value.Y - currentPosition.Y) > positionThreshold;
                        
                    var sizeChanged = _lastSize == null || 
                        Math.Abs(_lastSize.Value.X - currentSize.X) > sizeThreshold || 
                        Math.Abs(_lastSize.Value.Y - currentSize.Y) > sizeThreshold;
                    
                    if (positionChanged || sizeChanged)
                    {
                        DMManager.Instance.UpdateDMWindowState(Player, currentPosition, currentSize);
                        _lastPosition = currentPosition;
                        _lastSize = currentSize;
                        Plugin.Log.Debug($"PostDraw: Updated position/size for {Player.DisplayName} - Position: {currentPosition}, Size: {currentSize}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"PostDraw: Failed to update DM window state: {ex.Message}");
            }
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
        // PERFORMANCE: Cancel any ongoing async initialization
        if (_initializationTask != null && !_initializationTask.IsCompleted)
        {
            try
            {
                // Don't wait for the task, just let it complete naturally
                Plugin.Log.Debug($"OnClose: Async initialization task still running for {Player.DisplayName}, letting it complete naturally");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"OnClose: Exception while handling async task cleanup: {ex.Message}");
            }
        }

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
