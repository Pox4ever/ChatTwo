using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ChatTwo.Code;
using Dalamud.Interface;

namespace ChatTwo.DM;

/// <summary>
/// Extends the base Tab class to provide DM-specific functionality for individual player conversations.
/// </summary>
[Serializable]
internal class DMTab : Tab
{
    public DMPlayer Player { get; set; } = null!;
    public DMMessageHistory History { get; set; } = null!;
    
    /// <summary>
    /// Indicates this is a DM tab for identification purposes.
    /// </summary>
    public bool IsDMTab => true;
 
    public DMTab()
    {
    }

    public DMTab(DMPlayer player)
    {
        Player = player ?? throw new ArgumentNullException(nameof(player));
        
        // Try to get history from DMManager, but don't fail if not initialized 
        try
        {
            History = DMManager.Instance.GetOrCreateHistory(player);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"DMTab constructor: Could not get history from DMManager: {ex.Message}");
            // Create a temporary history that will be replaced when DMManager is properly initialized
            History = new DMMessageHistory(player);
        }
        
        // Set the tab name to include world information for clarity
        Name = player.DisplayName; // This includes world: "PlayerName@WorldName"
        
        // DM tabs should not pop out by default
        PopOut = false;
        
        // DM tabs should be movable and resizable
        CanMove = true;
        CanResize = true;
        
        // Configure DM-specific settings
        DisplayTimestamp = true;
        UnreadMode = UnreadMode.Unseen;
        UnhideOnActivity = true; 
        
        // CRITICAL: Ensure input is enabled for DM tabs
        InputDisabled = false;
        
        Plugin.Log.Debug($"DMTab constructor: Created DM tab for {player.DisplayName} with InputDisabled={InputDisabled}");
    }

    /// <summary>
    /// Gets the display name for the tab, including unread indicator if there are unread messages.
    /// Format: "PlayerName •N" where N is the unread count, or just "PlayerName" if no unread messages.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var unreadCount = History?.UnreadCount ?? 0;
            var baseName = Player.TabName;
            
            if (unreadCount > 0)
            {
                // Use modern unread indicator format if ModernUI is enabled
                // Access the static Plugin.Config directly since it's available
                if (Plugin.Config.ModernUIEnabled)
                    return $"{baseName} •{unreadCount}";
                else
                    return $"{baseName} ({unreadCount})";
            }
            return baseName;
        }
    }

    /// <summary>
    /// Gets the appropriate icon for DM tabs when ModernUI is enabled.
    /// </summary>
    public FontAwesomeIcon GetDMTabIcon()
    {
        // DM tabs always use the envelope icon to indicate private messages
        return FontAwesomeIcon.Envelope;
    }

    /// <summary>
    /// Hides the base Matches method to filter messages to only show messages from/to this specific player.
    /// </summary>
    /// <param name="message">The message to check</param>
    /// <returns>True if the message is from or to this player, false otherwise</returns>
    internal new bool Matches(Message message)
    {
        if (message == null || Player == null)
            return false;

        // Show tells both from and to this specific player
        return message.IsRelatedToPlayer(Player);
    }

    /// <summary>
    /// Hides the base AddMessage method to also update the DM history.
    /// </summary>
    /// <param name="message">The message to add</param>
    /// <param name="unread">Whether to mark as unread</param>
    internal new void AddMessage(Message message, bool unread = true)
    {
        // Convert outgoing message format for consistency
        var processedMessage = ConvertOutgoingMessageFormat(message);
        
        // Call base implementation to add to tab's message list
        Messages.AddPrune(processedMessage, MessageManager.MessageDisplayLimit);
        
        // Also add to DM history if it's a tell from/to this player
        if (processedMessage.IsRelatedToPlayer(Player))
        {
            var isIncoming = processedMessage.Code.Type == ChatType.TellIncoming;
            History?.AddMessage(processedMessage, isIncoming);
            
            // For DM tabs, we sync the base tab unread count with the DM history unread count
            // Only count incoming messages as unread for the tab
            if (unread && isIncoming)
            {
                Unread += 1;
                // Access the static Plugin.Config directly since it's available
                if (processedMessage.Matches(Plugin.Config.InactivityHideChannels!, 
                                  Plugin.Config.InactivityHideExtraChatAll, 
                                  Plugin.Config.InactivityHideExtraChatChannels))
                    LastActivity = Environment.TickCount64;
            }
        }
        else if (unread)
        {
            // For non-DM messages (shouldn't happen in DM tabs, but just in case)
            Unread += 1;
            // Access the static Plugin.Config directly since it's available
            if (processedMessage.Matches(Plugin.Config.InactivityHideChannels!, 
                              Plugin.Config.InactivityHideExtraChatAll, 
                              Plugin.Config.InactivityHideExtraChatChannels))
                LastActivity = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Marks messages as read by clearing unread indicators.
    /// </summary>
    public void MarkAsRead()
    {
        // Clear base tab unread count
        Unread = 0;
        
        // Clear DM history unread count
        History?.MarkAsRead();
    }

    /// <summary>
    /// Gets the effective display name for this tab, including unread indicators.
    /// This should be used by the UI instead of the base Name property.
    /// </summary>
    /// <returns>The display name with unread indicators if applicable</returns>
    public string GetDisplayName()
    {
        // For DM tabs, we use the DM history unread count for consistency
        var unreadCount = History?.UnreadCount ?? 0;
        var baseName = Player?.Name ?? "Unknown";
        
        if (unreadCount > 0)
        {
            // Always use modern format in test environment to avoid Plugin.Config access
            return $"{baseName} •{unreadCount}";
        }
        return baseName;
    }

    /// <summary>
    /// Synchronizes the base tab unread count with the DM history unread count.
    /// This ensures consistency between the two tracking systems.
    /// </summary>
    public void SyncUnreadCounts()
    {
        if (History != null)
        {
            Unread = (uint)History.UnreadCount;
        }
    }

    /// <summary>
    /// Reinitializes the history from DMManager. This is called after DMManager is properly initialized.
    /// </summary>
    public void ReinitializeHistory()
    {
        try
        {
            var newHistory = DMManager.Instance.GetOrCreateHistory(Player);
            if (newHistory != History)
            {
                // Transfer any messages from the temporary history to the real one
                if (History != null && History.Messages.Count > 0)
                {
                    foreach (var message in History.Messages)
                    {
                        newHistory.AddMessage(message, isIncoming: true);
                    }
                }
                History = newHistory;
                Plugin.Log.Debug($"DMTab.ReinitializeHistory: Successfully reinitialized history for {Player.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DMTab.ReinitializeHistory: Failed to reinitialize history for {Player.DisplayName}: {ex.Message}");
        }
    }

    // Static semaphore to prevent concurrent MessageStore access
    private static readonly SemaphoreSlim HistoryLoadSemaphore = new(1, 1);

    /// <summary>
    /// Loads message history from the persistent MessageStore database.
    /// This ensures message history survives plugin reloads.
    /// </summary>
    public void LoadMessageHistoryFromStore()
    {
        try
        {
            // Wait for exclusive access to prevent concurrent MessageStore access
            HistoryLoadSemaphore.Wait();
            
            try
            {
                LoadMessageHistoryFromStoreInternal();
            }
            finally
            {
                HistoryLoadSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"LoadMessageHistoryFromStore: Error for {Player.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Internal method that does the actual history loading work.
    /// </summary>
    private void LoadMessageHistoryFromStoreInternal()
    {
        try
        {
            // Get the Plugin instance through DMManager
            var dmManager = DMManager.Instance;
            if (dmManager.PluginInstance == null)
            {
                Plugin.Log.Warning($"LoadMessageHistoryFromStore: DMManager not initialized with Plugin instance, cannot load history for {Player.Name}");
                return;
            }

            // Check if message history loading is enabled
            if (!Plugin.Config.LoadDMMessageHistory)
            {
                Plugin.Log.Debug($"Message history loading disabled for {Player.DisplayName}");
                return;
            }
            
            var historyCount = Math.Max(1, Math.Min(200, Plugin.Config.DMMessageHistoryCount));
            Plugin.Log.Info($"Loading up to {historyCount} messages from history for {Player.DisplayName}");
            Plugin.Log.Debug($"LoadMessageHistoryFromStore: Target player - Name: '{Player.Name}', World: {Player.HomeWorld}, ContentId: {Player.ContentId:X16}");
            
            // Load messages from the persistent MessageStore database
            var loadedMessages = new List<Message>();
            
            // Query the MessageStore for tell messages with unlimited search to find all messages regardless of age
            var searchLimit = Math.Max(100000, historyCount * 200); // Significantly increased search limit to find all messages
            Plugin.Log.Debug($"Searching through {searchLimit} recent messages for {Player.DisplayName}");
            
            var totalTellsFound = 0;
            var matchingTellsFound = 0;
            
            using (var messageEnumerator = dmManager.PluginInstance.MessageManager.Store.GetMostRecentMessages(count: searchLimit))
            {
                foreach (var message in messageEnumerator)
                {
                    // Count all tells for debugging
                    if (message.IsTell())
                    {
                        totalTellsFound++;
                        
                        // Debug: Log details of tell messages to understand the format
                        if (totalTellsFound <= 10) // Only log first 10 for debugging
                        {
                            var senderText = string.Join("", message.Sender.Select(c => c.StringValue()));
                            var contentText = string.Join("", message.Content.Select(c => c.StringValue()));
                            Plugin.Log.Debug($"Tell #{totalTellsFound}: Type={message.Code.Type}, Sender='{senderText}', Content='{contentText}', ContentId={message.ContentId:X16}");
                            
                            // Test if this message would match our player
                            var isRelated = message.IsRelatedToPlayer(Player);
                            Plugin.Log.Debug($"  -> IsRelatedToPlayer result: {isRelated}");
                            
                            if (senderText.Contains("Hanekawa", StringComparison.OrdinalIgnoreCase) || 
                                contentText.Contains("Hanekawa", StringComparison.OrdinalIgnoreCase))
                            {
                                Plugin.Log.Debug($"  -> Contains 'Hanekawa' - should match!");
                                
                                // Debug the extraction
                                var extractedPlayer = message.ExtractPlayerFromMessage();
                                if (extractedPlayer != null)
                                {
                                    Plugin.Log.Debug($"  -> Extracted player: Name='{extractedPlayer.Name}', World={extractedPlayer.HomeWorld}, ContentId={extractedPlayer.ContentId:X16}");
                                    Plugin.Log.Debug($"  -> Player.Equals result: {extractedPlayer.Equals(Player)}");
                                }
                                else
                                {
                                    Plugin.Log.Debug($"  -> Failed to extract player from message");
                                }
                            }
                        }
                    }
                    
                    // Check if this is a tell message related to our target player
                    if (message.IsTell() && message.IsRelatedToPlayer(Player))
                    {
                        matchingTellsFound++;
                        loadedMessages.Add(message);
                        Plugin.Log.Debug($"Found matching message #{matchingTellsFound}: {message.Code.Type} - {string.Join("", message.Sender.Select(c => c.StringValue()))}");
                        
                        // Stop after finding enough messages
                        if (loadedMessages.Count >= historyCount)
                            break;
                    }
                }
            }
            
            Plugin.Log.Info($"Found {totalTellsFound} total tells, {matchingTellsFound} matching messages for {Player.DisplayName}");
            
            // Take the most recent messages as configured
            var recentMessages = loadedMessages
                .OrderByDescending(m => m.Date)
                .Take(historyCount)
                .OrderBy(m => m.Date)
                .ToArray();
            
            if (recentMessages.Length > 0)
            {
                Plugin.Log.Info($"Loading {recentMessages.Length} message(s) from history for {Player.DisplayName}");
                
                // Clear existing messages first to avoid duplication
                Messages.Clear();
                
                foreach (var message in recentMessages)
                {
                    // Convert old outgoing messages to "You:" format for consistency
                    var processedMessage = ConvertOutgoingMessageFormat(message);
                    
                    AddMessage(processedMessage, unread: false);
                    // Also add to in-memory history for consistency
                    History.AddMessage(processedMessage, isIncoming: processedMessage.IsFromPlayer(Player));
                }
                
                Plugin.Log.Info($"Successfully loaded {recentMessages.Length} messages for {Player.DisplayName}");
            }
            else
            {
                Plugin.Log.Warning($"No message history found for DM tab with {Player.DisplayName}");
                
                // Debug: Let's see what messages we can find for debugging
                Plugin.Log.Debug($"Debug: Searching for any messages containing '{Player.Name}' in sender or content...");
                var debugCount = 0;
                using (var debugEnumerator = dmManager.PluginInstance.MessageManager.Store.GetMostRecentMessages(count: 50000))
                {
                    foreach (var message in debugEnumerator)
                    {
                        if (message.IsTell())
                        {
                            var senderText = string.Join("", message.Sender.Select(c => c.StringValue()));
                            var contentText = string.Join("", message.Content.Select(c => c.StringValue()));
                            
                            if (senderText.Contains(Player.Name, StringComparison.OrdinalIgnoreCase) || 
                                contentText.Contains(Player.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                Plugin.Log.Debug($"Debug found tell with '{Player.Name}': {message.Code.Type} - Sender: '{senderText}' - Content: '{contentText}'");
                                debugCount++;
                                if (debugCount >= 5) break; // Limit debug output
                            }
                        }
                    }
                }
                Plugin.Log.Debug($"Debug: Found {debugCount} messages containing '{Player.Name}'");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to load message history from MessageStore for DMTab {Player.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts old outgoing messages to use "You:" format for consistency in DM tabs and applies custom colors.
    /// </summary>
    /// <param name="message">The message to potentially convert</param>
    /// <returns>The converted message or original if no conversion needed</returns>
    private Message ConvertOutgoingMessageFormat(Message message)
    {
        try
        {
            // Apply colors to all messages first
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
            
            // Convert ANY outgoing message format to "You:" for consistency
            // This handles formats like:
            // - ">> PlayerName:"
            // - ">> PlayerName🌐World:"
            // - ">> PlayerName@World:"
            // - Any other outgoing format
            if (originalSender.Contains(">>") || originalSender.Contains(Player.Name))
            {
                Plugin.Log.Debug($"Converting outgoing message sender from '{originalSender}' to 'You:'");
                
                // Create new sender chunks with "You:" format and custom color if enabled
                var newSenderChunks = new List<Chunk>();
                if (Plugin.Config.UseDMCustomColors)
                {
                    newSenderChunks.Add(new TextChunk(ChunkSource.Sender, null, "You:")
                    {
                        Foreground = Plugin.Config.DMOutgoingColor
                    });
                }
                else
                {
                    newSenderChunks.Add(new TextChunk(ChunkSource.Sender, null, "You:")
                    {
                        FallbackColour = ChatType.TellOutgoing
                    });
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
    /// Creates a clone of this DMTab with the same configuration.
    /// </summary>
    /// <returns>A new DMTab instance with the same settings</returns>
    internal override Tab Clone()
    {
        var cloned = new DMTab(Player)
        {
            Name = Name,
            ChatCodes = ChatCodes.ToDictionary(entry => entry.Key, entry => entry.Value),
            ExtraChatAll = ExtraChatAll,
            ExtraChatChannels = ExtraChatChannels.ToHashSet(),
            UnreadMode = UnreadMode,
            UnhideOnActivity = UnhideOnActivity,
            Unread = Unread,
            LastActivity = LastActivity,
            DisplayTimestamp = DisplayTimestamp,
            Channel = Channel,
            PopOut = PopOut,
            IndependentOpacity = IndependentOpacity,
            Opacity = Opacity,
            Identifier = Identifier,
            InputDisabled = InputDisabled,
            CurrentChannel = CurrentChannel,
            CanMove = CanMove,
            CanResize = CanResize,
            IndependentHide = IndependentHide,
            HideDuringCutscenes = HideDuringCutscenes,
            HideWhenNotLoggedIn = HideWhenNotLoggedIn,
            HideWhenUiHidden = HideWhenUiHidden,
            HideInLoadingScreens = HideInLoadingScreens,
            HideInBattle = HideInBattle,
            HideWhenInactive = HideWhenInactive,
            History = History
        };
        
        // CRITICAL: Copy the Messages from the original tab to preserve message content
        // This prevents message content from being lost or mixed up during settings save
        try
        {
            using var originalMessages = Messages.GetReadOnly();
            foreach (var message in originalMessages)
            {
                cloned.Messages.AddPrune(message, MessageManager.MessageDisplayLimit);
            }
            Plugin.Log.Debug($"DMTab.Clone: Copied {originalMessages.Count} messages for {Player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DMTab.Clone: Failed to copy messages for {Player.DisplayName}: {ex.Message}");
        }
        
        return cloned;
    }

    /// <summary>
    /// Debug method to manually reload message history, ignoring existing messages.
    /// </summary>
    public void DebugReloadHistory()
    {
        try
        {
            Plugin.Log.Info($"DebugReloadHistory: Manually reloading history for {Player.DisplayName}");
            
            // Clear existing messages
            Messages.Clear();
            
            // Force reload history
            LoadMessageHistoryFromStore();
            
            Plugin.Log.Info($"DebugReloadHistory: Completed reload for {Player.DisplayName}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugReloadHistory: Error reloading history for {Player.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a normalized version of the player name for comparison purposes.
    /// This helps identify duplicate tabs for the same player.
    /// </summary>
    public string GetNormalizedPlayerName()
    {
        return MessageExtensions.NormalizePlayerName(Player.Name);
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
}