using System;
using System.Linq;
using ChatTwo.Code;
using Dalamud.Game.Text.SeStringHandling;

namespace ChatTwo.DM;

/// <summary>
/// Extension methods for Message class to support DM functionality.
/// </summary>
internal static class MessageExtensions
{
    /// <summary>
    /// Determines if a message is a tell (incoming or outgoing).
    /// </summary>
    /// <param name="message">The message to check</param>
    /// <returns>True if the message is a tell, false otherwise</returns>
    public static bool IsTell(this Message message)
    {
        if (message == null)
            return false;

        return message.Code.Type == ChatType.TellIncoming || message.Code.Type == ChatType.TellOutgoing;
    }

    /// <summary>
    /// Determines if a message is from a specific player.
    /// </summary>
    /// <param name="message">The message to check</param>
    /// <param name="player">The player to check against</param>
    /// <returns>True if the message is from the specified player, false otherwise</returns>
    public static bool IsFromPlayer(this Message message, DMPlayer player)
    {
        if (message == null || player == null || !message.IsTell())
            return false;

        var messagePlayer = message.ExtractPlayerFromMessage();
        return messagePlayer != null && messagePlayer.Equals(player);
    }

    /// <summary>
    /// Checks if a message is related to the specified player (either from or to them).
    /// </summary>
    /// <param name="message">The message to check</param> 
    /// <param name="player">The player to check against</param> 
    /// <returns>True if the message is from or to the specified player, false otherwise</returns>
    public static bool IsRelatedToPlayer(this Message message, DMPlayer player)
    {
        if (message == null || player == null || !message.IsTell())
            return false;

        // For incoming tells, check if it's from this player
        if (message.Code.Type == ChatType.TellIncoming)
        {
            return message.IsFromPlayer(player);
        }
        
        // For outgoing tells, check if it's to this player
        if (message.Code.Type == ChatType.TellOutgoing)
        {
            return message.IsToPlayer(player);
        }

        return false;
    }

    /// <summary>
    /// Checks if an outgoing tell message is to the specified player.
    /// </summary>
    /// <param name="message">The outgoing tell message to check</param>
    /// <param name="player">The player to check against</param>
    /// <returns>True if the message is to the specified player, false otherwise</returns>
    public static bool IsToPlayer(this Message message, DMPlayer player)
    {
        if (message == null || player == null || message.Code.Type != ChatType.TellOutgoing)
            return false;

        try
        {
            // For outgoing tells, we need to extract the target player from the message
            // The message sender chunks often contain the target information
            
            // Check if any sender chunk contains the player name
            foreach (var chunk in message.Sender)
            {
                var chunkText = chunk.StringValue();
                if (string.IsNullOrEmpty(chunkText))
                    continue;
                
                // Clean the chunk text and check if it matches our player
                var cleanedChunkText = CleanPlayerName(chunkText);
                
                // Check for exact name match
                if (string.Equals(cleanedChunkText, player.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                
                // Check for name@world format
                if (cleanedChunkText.Contains('@'))
                {
                    var parts = cleanedChunkText.Split('@');
                    if (parts.Length == 2)
                    {
                        var nameFromChunk = parts[0].Trim();
                        if (string.Equals(nameFromChunk, player.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            
            // Also check content chunks for player name (sometimes the target is mentioned there)
            foreach (var chunk in message.Content)
            {
                var chunkText = chunk.StringValue();
                if (string.IsNullOrEmpty(chunkText))
                    continue;
                
                // Look for ">> PlayerName:" pattern in content
                if (chunkText.Contains(">>") && chunkText.Contains(":"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(chunkText, @">>\s*([^:]+):");
                    if (match.Success)
                    {
                        var nameFromContent = CleanPlayerName(match.Groups[1].Value);
                        if (string.Equals(nameFromContent, player.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"IsToPlayer: Exception occurred: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Extracts player information from a tell message.
    /// </summary>
    /// <param name="message">The tell message</param>
    /// <returns>DMPlayer instance or null if extraction failed</returns>
    public static DMPlayer? ExtractPlayerFromMessage(this Message message)
    {
        if (message == null)
        {
            Plugin.Log.Debug("ExtractPlayerFromMessage: message is null");
            return null;
        }

        if (!message.IsTell())
        {
            Plugin.Log.Debug($"ExtractPlayerFromMessage: message is not a tell, type is {message.Code.Type}");
            return null;
        }

        try
        {
            Plugin.Log.Debug($"ExtractPlayerFromMessage: Processing {message.Code.Type} message with ContentId={message.ContentId}");
            
            // For incoming tells, extract from sender
            if (message.Code.Type == ChatType.TellIncoming)
            {
                Plugin.Log.Debug($"ExtractPlayerFromMessage: Processing incoming tell, sender chunks count: {message.Sender.Count}");
                
                // Extract player name from sender chunks - just get the text content
                var senderName = string.Join("", message.Sender.Select(c => c.StringValue())).Trim();
                Plugin.Log.Debug($"ExtractPlayerFromMessage: Raw sender name: '{senderName}'");
                
                // Clean up the sender name - remove any extra formatting
                senderName = CleanPlayerName(senderName);
                Plugin.Log.Debug($"ExtractPlayerFromMessage: Cleaned sender name: '{senderName}'");
                
                if (!string.IsNullOrEmpty(senderName))
                {
                    // Try to extract world from ContentId first (most reliable)
                    var worldFromContentId = ExtractWorldIdFromContentId(message.ContentId);
                    var world = worldFromContentId ?? GetCurrentPlayerWorld();
                    
                    Plugin.Log.Debug($"ExtractPlayerFromMessage: World from ContentId: {worldFromContentId}, fallback world: {world}");
                    
                    // Check if the sender name contains world information (PlayerName@WorldName)
                    if (senderName.Contains('@'))
                    {
                        var parts = senderName.Split('@');
                        if (parts.Length == 2)
                        {
                            var playerName = parts[0].Trim();
                            var worldName = parts[1].Trim();
                            Plugin.Log.Debug($"ExtractPlayerFromMessage: Found world info in name - player: '{playerName}', world: '{worldName}'");
                            
                            // Try to find world by name, but prefer ContentId-derived world if available
                            if (worldFromContentId == null)
                            {
                                var worldId = GetWorldIdByName(worldName);
                                if (worldId != 0)
                                {
                                    world = worldId;
                                    Plugin.Log.Debug($"ExtractPlayerFromMessage: Resolved world '{worldName}' to ID {worldId}");
                                }
                            }
                            
                            senderName = playerName;
                        }
                    }
                    
                    Plugin.Log.Debug($"ExtractPlayerFromMessage: Successfully extracted player '{senderName}' on world {world} with ContentId {message.ContentId}");
                    return new DMPlayer(senderName, world, message.ContentId);
                }
                else
                {
                    Plugin.Log.Warning("ExtractPlayerFromMessage: Sender name is empty after cleaning");
                }
            }
            else if (message.Code.Type == ChatType.TellOutgoing)
            {
                Plugin.Log.Debug("ExtractPlayerFromMessage: Outgoing tell - extraction not implemented, this is expected");
                return null;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"ExtractPlayerFromMessage: Exception occurred: {ex.Message}");
            Plugin.Log.Error($"ExtractPlayerFromMessage: Stack trace: {ex.StackTrace}");
        }

        Plugin.Log.Warning("ExtractPlayerFromMessage: Could not extract player information from tell message");
        return null;
    }

    /// <summary>
    /// Extracts the world ID from a ContentId.
    /// ContentId format: [WorldId (16 bits)][PlayerId (48 bits)]
    /// </summary>
    private static uint? ExtractWorldIdFromContentId(ulong contentId)
    {
        if (contentId == 0)
            return null;
        
        // Extract the world ID from the upper 16 bits of the ContentId
        var worldId = (uint)((contentId >> 48) & 0xFFFF);
        
        // Validate that it's a reasonable world ID (should be > 0 and < 10000)
        if (worldId > 0 && worldId < 10000)
            return worldId;
        
        return null;
    }

    /// <summary>
    /// Cleans up a player name by removing common formatting artifacts.
    /// </summary>
    /// <param name="playerName">The raw player name</param>
    /// <returns>Cleaned player name</returns>
    private static string CleanPlayerName(string playerName)
    {
        if (string.IsNullOrEmpty(playerName))
            return string.Empty;

        // Remove common prefixes/suffixes that might appear in tell formatting
        var cleaned = playerName.Trim();
        
        // Remove tell formatting characters (>>, <<, etc.)
        cleaned = cleaned.Replace(">>", "").Replace("<<", "").Replace(">", "").Replace("<", "");
        
        // Remove game-generated decorative emojis that appear around player names in tells
        // Common FFXIV decorative emojis: 🌸 (cherry blossom), 🌐 (globe for world), etc.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[🌐🌸🌺🌻🌷🌹💮🏵️⚘🌼🌙⭐✨💫🔥❄️💧🌊⚡🌟💎]", "");
        
        // Remove cross-world indicators that appear in FFXIV
        // Examples: "PlayerNameCrossWorldJenova", "PlayerNameCrossWorld", etc.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"CrossWorld\w*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove any @ symbol and everything after it (world name)
        var atIndex = cleaned.IndexOf('@');
        if (atIndex >= 0)
        {
            cleaned = cleaned.Substring(0, atIndex);
        }
        
        // Remove any leading/trailing brackets, quotes, or other formatting
        cleaned = cleaned.Trim('[', ']', '"', '\'', ' ', '\t', '\n', '\r');
        
        // Remove any remaining whitespace
        cleaned = cleaned.Trim();
        
        return cleaned;
    }

    /// <summary>
    /// Normalizes a player name for comparison purposes by applying the same cleaning logic.
    /// This helps identify duplicate players with different name formats.
    /// </summary>
    /// <param name="playerName">The player name to normalize</param>
    /// <returns>Normalized player name</returns>
    public static string NormalizePlayerName(string playerName)
    {
        return CleanPlayerName(playerName);
    }

    /// <summary>
    /// Gets the current player's world ID.
    /// </summary>
    /// <returns>World ID or 0 if not available</returns>
    private static uint GetCurrentPlayerWorld()
    {
        try
        {
            // Try to get from ObjectTable first (more reliable when logged in)
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer != null)
            {
                return localPlayer.HomeWorld.RowId;
            }
            
            // Fallback to ClientState (deprecated but might work in some cases)
            return Plugin.ClientState.LocalPlayer?.HomeWorld.RowId ?? 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to get current player world: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Gets a world ID by world name.
    /// </summary>
    /// <param name="worldName">The world name</param>
    /// <returns>World ID or 0 if not found</returns>
    private static uint GetWorldIdByName(string worldName)
    {
        try
        {
            var worldSheet = Sheets.WorldSheet;
            if (worldSheet != null)
            {
                var world = worldSheet.FirstOrDefault(w => 
                    string.Equals(w.Name.ToString(), worldName, StringComparison.OrdinalIgnoreCase));
                
                return world.RowId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to lookup world '{worldName}': {ex.Message}");
        }
        
        return 0;
    }
}