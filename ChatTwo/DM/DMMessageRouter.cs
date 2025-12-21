using System;
using ChatTwo.Code;
using Lumina.Excel.Sheets;

namespace ChatTwo.DM;

/// <summary>
/// Routes incoming tell messages to appropriate DM interfaces while preserving main chat display.
/// </summary>
internal class DMMessageRouter
{
    private readonly Plugin _plugin;
    private DMPlayer? _recentReceiver; // Track the last tell recipient for error message routing

    public DMMessageRouter(Plugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    /// <summary>
    /// Processes an incoming message and routes tells to DM interfaces if appropriate.
    /// Also handles error messages related to tells.
    /// </summary>
    /// <param name="message">The message to process</param>
    public void ProcessIncomingMessage(Message message)
    {
        if (message == null)
            return;

        try
        {
            // Handle tell messages
            if (message.IsTell())
            {
                ProcessTellMessage(message);
                return;
            }

            // Handle error messages that might be related to tells
            if (message.Code.Type == ChatType.Error)
            {
                ProcessErrorMessage(message);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to process incoming message: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes tell messages and routes them to DM interfaces.
    /// </summary>
    /// <param name="message">The tell message to process</param>
    private void ProcessTellMessage(Message message)
    {
        // Handle incoming and outgoing tells differently
        if (message.Code.Type == ChatType.TellIncoming)
        {
            ProcessIncomingTell(message);
        }
        else if (message.Code.Type == ChatType.TellOutgoing)
        {
            ProcessOutgoingTell(message);
        }
    }

    /// <summary>
    /// Processes incoming tell messages.
    /// </summary>
    /// <param name="message">The incoming tell message</param>
    private void ProcessIncomingTell(Message message)
    {
        // Extract player information from the message
        var player = message.ExtractPlayerFromMessage();
        if (player == null)
        {
            Plugin.Log.Warning("Could not extract player information from incoming tell message");
            return;
        }

        // Route the message to DM history
        var dmManager = DMManager.Instance;
        dmManager.RouteIncomingTell(message);

        // Check if we should auto-open a DM for new tells
        var hasExistingDM = dmManager.HasOpenDMTab(player) || dmManager.HasOpenDMWindow(player);
        if (!hasExistingDM && Plugin.Config.AutoOpenDMOnNewTell)
        {
            // Auto-open DM based on default mode
            switch (Plugin.Config.DefaultDMMode)
            {
                case Configuration.DMDefaultMode.Window:
                    // Auto-open DM window for new tells
                    var dmWindow = dmManager.OpenDMWindow(player, _plugin.ChatLogWindow);
                    if (dmWindow != null)
                    {
                        Plugin.Log.Info($"Auto-opened DM window for new tell from {player.DisplayName}");
                    }
                    else
                    {
                        Plugin.Log.Warning($"Failed to auto-open DM window for {player.DisplayName}, falling back to tab");
                        dmManager.OpenDMTab(player);
                    }
                    break;
                case Configuration.DMDefaultMode.Tab:
                default:
                    dmManager.OpenDMTab(player);
                    Plugin.Log.Info($"Auto-opened DM tab for new tell from {player.DisplayName}");
                    break;
            }
        }

        // Route to open DM tabs if they exist
        var dmTab = dmManager.GetDMTab(player);
        if (dmTab != null)
        {
            dmTab.AddMessage(message, unread: true);
        }

        // Route to open DM windows if they exist
        var dmWindow2 = dmManager.GetDMWindow(player);
        if (dmWindow2 != null)
        {
            // Add the message to the DM window's internal DMTab
            dmWindow2.DMTab.AddMessage(message, unread: true);
        }
    }

    /// <summary>
    /// Processes outgoing tell messages.
    /// </summary>
    /// <param name="message">The outgoing tell message</param>
    private void ProcessOutgoingTell(Message message)
    {
        // For outgoing tells, try to extract the target from the message content
        // If we don't have a recent receiver, try to parse it from the tell command
        DMPlayer? targetPlayer = _recentReceiver;
        
        if (targetPlayer == null)
        {
            targetPlayer = TryExtractTargetFromOutgoingTell(message);
            
            if (targetPlayer != null)
            {
                // Set as recent receiver for error routing
                _recentReceiver = targetPlayer;
            }
            else
            {
                Plugin.Log.Warning("ProcessOutgoingTell: Could not extract target from outgoing tell");
                return;
            }
        }

        try
        {
            var dmManager = DMManager.Instance;
            
            // Create a properly formatted outgoing message
            var modifiedMessage = CreateOutgoingTellMessage(message);
            
            // Route to open DM tabs if they exist
            var dmTab = dmManager.GetDMTab(targetPlayer);
            if (dmTab != null)
            {
                dmTab.AddMessage(modifiedMessage, unread: false);
            }

            // Route to open DM windows if they exist
            var dmWindow = dmManager.GetDMWindow(targetPlayer);
            if (dmWindow != null)
            {
                // The message will be displayed through the DMTab that the window uses
            }

            // Add to DM history
            var history = dmManager.GetHistory(targetPlayer);
            if (history != null)
            {
                history.AddMessage(modifiedMessage, isIncoming: false);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"ProcessOutgoingTell: Exception occurred: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to extract the target player from an outgoing tell message by parsing recent chat commands.
    /// </summary>
    /// <param name="message">The outgoing tell message</param>
    /// <returns>The target player if found, null otherwise</returns>
    private DMPlayer? TryExtractTargetFromOutgoingTell(Message message)
    {
        try
        {
            // Get the last chat message that was sent to see if it was a tell command
            var lastMessage = _plugin.MessageManager.LastMessage;
            if (lastMessage.Message != null)
            {
                var lastMessageText = lastMessage.Message.ToString();
                Plugin.Log.Debug($"TryExtractTargetFromOutgoingTell: Last message was: '{lastMessageText}'");
                
                // Check if it's a tell command
                if (lastMessageText.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse: /tell PlayerName@World message or /tell PlayerName message
                    var parts = lastMessageText.Split(' ', 3);
                    if (parts.Length >= 2)
                    {
                        var targetName = parts[1];
                        Plugin.Log.Debug($"TryExtractTargetFromOutgoingTell: Extracted target name: '{targetName}'");
                        
                        return ParsePlayerNameWithWorld(targetName);
                    }
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"TryExtractTargetFromOutgoingTell: Exception occurred: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a properly formatted outgoing tell message with sender name.
    /// </summary>
    /// <param name="originalMessage">The original outgoing tell message</param>
    /// <returns>Modified message with proper sender formatting</returns>
    private Message CreateOutgoingTellMessage(Message originalMessage)
    {
        try
        {
            // Get the current player's name - handle threading issues gracefully
            string playerName;
            try
            {
                var localPlayer = Plugin.ObjectTable.LocalPlayer;
                playerName = localPlayer?.Name.ToString() ?? "You";
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"Could not access LocalPlayer (likely threading issue): {ex.Message}");
                playerName = "You";
            }
            
            // Extract the message content (without the >> prefix that might be there)
            var messageContent = string.Join("", originalMessage.Content.Select(c => c.StringValue()));
            
            // Remove any existing >> prefix
            if (messageContent.StartsWith(">> "))
            {
                messageContent = messageContent.Substring(3);
            }
            
            // Create proper sender chunks with player name
            var senderChunks = new List<Chunk>
            {
                new TextChunk(ChunkSource.Sender, null, $">> {playerName}: ")
            };
            
            var contentChunks = new List<Chunk>
            {
                new TextChunk(ChunkSource.Content, null, messageContent)
            };
            
            return new Message(
                originalMessage.Receiver,
                originalMessage.ContentId,
                originalMessage.AccountId,
                originalMessage.Code,
                senderChunks,
                contentChunks,
                originalMessage.SenderSource,
                originalMessage.ContentSource
            );
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to create outgoing tell message: {ex.Message}");
            return originalMessage; // Return original if modification fails
        }
    }

    /// <summary>
    /// Processes error messages and routes tell-related errors to DM interfaces.
    /// </summary>
    /// <param name="message">The error message to process</param>
    private void ProcessErrorMessage(Message message)
    {
        var messageText = ExtractTextFromMessage(message);
        
        // Check if this is a tell-related error message and we have a recent receiver
        if (_recentReceiver == null)
        {
            return;
        }
            
        var tellErrorPatterns = new[]
        {
            $"Message to {_recentReceiver.DisplayName} could not be sent.",
            "Unable to send /tell. Recipient is in a restricted area.",
            "Your message was not heard. You must wait before using /tell, /say, /yell, or /shout again."
        };

        var isTellError = tellErrorPatterns.Any(pattern => 
            messageText.Contains(pattern, StringComparison.OrdinalIgnoreCase));

        if (!isTellError)
        {
            // Also check generic patterns
            var genericPatterns = new[]
            {
                "could not be sent",
                "is not online",
                "is not accepting tells",
                "has blocked you",
                "is busy"
            };
            
            isTellError = genericPatterns.Any(pattern => 
                messageText.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        if (!isTellError)
        {
            _recentReceiver = null; // Clear if not a tell error
            return;
        }

        var targetPlayer = _recentReceiver;
        _recentReceiver = null; // Clear after processing

        // Route the error to the appropriate DM interface
        var dmManager = DMManager.Instance;
        
        // Add to DM tab if it exists
        var dmTab = dmManager.GetDMTab(targetPlayer);
        if (dmTab != null)
        {
            dmTab.AddMessage(message, unread: false);
        }

        // Add to DM window if it exists (via its internal DMTab)
        var dmWindow = dmManager.GetDMWindow(targetPlayer);

        // Also add to DM history for persistence
        var history = dmManager.GetHistory(targetPlayer);
        if (history != null)
        {
            history.AddMessage(message, isIncoming: false);
        }
    }

    /// <summary>
    /// Extracts text content from a message.
    /// </summary>
    /// <param name="message">The message to extract text from</param>
    /// <returns>The text content of the message</returns>
    private string ExtractTextFromMessage(Message message)
    {
        try
        {
            return string.Join("", message.Content.Select(chunk => chunk.StringValue()));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to extract text from message: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Extracts a player name from an error message.
    /// </summary>
    /// <param name="errorMessage">The error message text</param>
    /// <returns>The DMPlayer if found, null otherwise</returns>
    private DMPlayer? ExtractPlayerFromErrorMessage(string errorMessage)
    {
        try
        {
            // Pattern: "Message to PlayerName could not be sent."
            var messageToPattern = @"Message to ([^@\s]+(?:@[^@\s]+)?) could not be sent";
            var match = System.Text.RegularExpressions.Regex.Match(errorMessage, messageToPattern);
            
            if (match.Success)
            {
                var playerNameWithWorld = match.Groups[1].Value;
                return ParsePlayerNameWithWorld(playerNameWithWorld);
            }

            // Pattern: "PlayerName is not online" or similar
            var isNotPattern = @"^([^@\s]+(?:@[^@\s]+)?) is (?:not online|not accepting tells|busy)";
            match = System.Text.RegularExpressions.Regex.Match(errorMessage, isNotPattern);
            
            if (match.Success)
            {
                var playerNameWithWorld = match.Groups[1].Value;
                return ParsePlayerNameWithWorld(playerNameWithWorld);
            }

            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to extract player from error message '{errorMessage}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses a player name that may include world information.
    /// </summary>
    /// <param name="playerNameWithWorld">Player name potentially with @World suffix</param>
    /// <returns>DMPlayer instance or null if parsing fails</returns>
    private DMPlayer? ParsePlayerNameWithWorld(string playerNameWithWorld)
    {
        try
        {
            if (playerNameWithWorld.Contains('@'))
            {
                var parts = playerNameWithWorld.Split('@');
                if (parts.Length == 2)
                {
                    var playerName = parts[0];
                    var worldName = parts[1];
                    
                    // Try to find the world ID from the world name
                    var worldSheet = Sheets.WorldSheet;
                    if (worldSheet != null)
                    {
                        var world = worldSheet.FirstOrDefault(w => 
                            string.Equals(w.Name.ToString(), worldName, StringComparison.OrdinalIgnoreCase));
                        
                        if (world.RowId != 0)
                        {
                            return new DMPlayer(playerName, world.RowId);
                        }
                    }
                }
            }
            else
            {
                // No world specified, use current player's world
                var currentWorld = Plugin.ClientState.LocalPlayer?.HomeWorld.RowId ?? 0;
                if (currentWorld != 0)
                {
                    return new DMPlayer(playerNameWithWorld, currentWorld);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to parse player name '{playerNameWithWorld}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Tracks an outgoing tell for error message routing.
    /// </summary>
    /// <param name="player">The target player</param>
    public void TrackOutgoingTell(DMPlayer player)
    {
        if (player == null)
        {
            Plugin.Log.Warning("TrackOutgoingTell: player is null");
            return;
        }

        try
        {
            // Track the recipient for error message routing
            _recentReceiver = player;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"TrackOutgoingTell: Exception occurred: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles an outgoing tell message and tracks the recipient for error routing.
    /// </summary>
    /// <param name="player">The target player</param>
    /// <param name="message">The outgoing message</param>
    public void HandleOutgoingTell(DMPlayer player, Message message)
    {
        if (player == null || message == null)
            return;

        try
        {
            // Track the recipient for error message routing
            _recentReceiver = player;
            
            var dmManager = DMManager.Instance;
            dmManager.HandleOutgoingTell(player, message);
            
            Plugin.Log.Debug($"Handled outgoing tell to {player.DisplayName} and set as recent receiver");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to handle outgoing tell: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines if a message should be displayed in main chat based on configuration.
    /// </summary>
    /// <param name="message">The message to check</param>
    /// <returns>True if the message should be displayed in main chat, false otherwise</returns>
    public bool ShouldDisplayInMainChat(Message message)
    {
        if (message == null || !message.IsTell())
            return true; // Non-tell messages always display in main chat

        // Check configuration settings for tell display behavior
        // For now, always show tells in main chat (default behavior)
        // This will be enhanced when configuration options are implemented
        return Plugin.Config.ShowTellsInMainChat;
    }
}