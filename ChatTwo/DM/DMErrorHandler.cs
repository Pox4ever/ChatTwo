using System;
using System.Collections.Generic;
using ChatTwo.Code;

namespace ChatTwo.DM;

/// <summary>
/// Centralized error handling for DM functionality with user-friendly error messages and logging.
/// </summary>
internal static class DMErrorHandler
{
    /// <summary>
    /// Handles player resolution errors (invalid names, offline players, etc.).
    /// </summary>
    /// <param name="playerName">The player name that failed to resolve</param>
    /// <param name="ex">The exception that occurred</param>
    public static void HandlePlayerResolutionError(string playerName, Exception ex)
    {
        var userMessage = ex.Message switch
        {
            var msg when msg.Contains("not found") => $"Player '{playerName}' not found. They may be offline or on a different data center.",
            var msg when msg.Contains("invalid") => $"Invalid player name '{playerName}'. Please check the spelling.",
            var msg when msg.Contains("world") => $"Could not determine world for player '{playerName}'. Please specify the full name with world.",
            var msg when msg.Contains("timeout") => $"Timed out while looking up player '{playerName}'. Please try again.",
            _ => $"Could not find player '{playerName}'. Please check the name and try again."
        };

        Plugin.Log.Warning($"Player resolution failed for '{playerName}': {ex.Message}");
        ShowUserError("Player Lookup Failed", userMessage);
    }

    /// <summary>
    /// Handles message sending errors with appropriate user feedback.
    /// </summary>
    /// <param name="player">The target player</param>
    /// <param name="message">The message that failed to send</param>
    /// <param name="ex">The exception that occurred</param>
    public static void HandleMessageSendError(DMPlayer player, string message, Exception ex)
    {
        var userMessage = ex.Message switch
        {
            var msg when msg.Contains("blocked") => $"{player.Name} has blocked you or is not accepting tells.",
            var msg when msg.Contains("offline") => $"{player.Name} is currently offline.",
            var msg when msg.Contains("busy") => $"{player.Name} is currently busy and may not receive tells.",
            var msg when msg.Contains("rate limit") => "You are sending messages too quickly. Please wait a moment.",
            var msg when msg.Contains("too long") => "Message is too long. Please shorten it and try again.",
            var msg when msg.Contains("invalid characters") => "Message contains invalid characters. Please check your text.",
            var msg when msg.Contains("network") => "Network error occurred. Please check your connection and try again.",
            _ => "Failed to send message. Please try again."
        };

        Plugin.Log.Error($"Message send failed to {player}: {ex.Message}");
        ShowUserError("Message Send Failed", userMessage);
        
        // Add error message to DM history for context
        AddErrorToHistory(player, $"Failed to send: {message.Substring(0, Math.Min(50, message.Length))}...");
    }

    /// <summary>
    /// Handles UI state errors with graceful degradation.
    /// </summary>
    /// <param name="operation">The UI operation that failed</param>
    /// <param name="ex">The exception that occurred</param>
    public static void HandleUIStateError(string operation, Exception ex)
    {
        var userMessage = operation switch
        {
            "window_creation" => "Could not create DM window. Using tab instead.",
            "tab_creation" => "Could not create DM tab. Please try again.",
            "drag_drop" => "Drag and drop operation failed. Please try again.",
            "context_menu" => "Context menu action failed. Please try the action again.",
            _ => "UI operation failed. Please try again."
        };

        Plugin.Log.Error($"UI operation '{operation}' failed: {ex.Message}");
        ShowUserError("Interface Error", userMessage);
    }

    /// <summary>
    /// Handles data persistence errors.
    /// </summary>
    /// <param name="operation">The data operation that failed</param>
    /// <param name="ex">The exception that occurred</param>
    public static void HandleDataPersistenceError(string operation, Exception ex)
    {
        var userMessage = operation switch
        {
            "save_history" => "Could not save message history. Messages may be lost on restart.",
            "load_history" => "Could not load message history. Starting with empty conversation.",
            "save_config" => "Could not save DM settings. Changes may be lost on restart.",
            "load_config" => "Could not load DM settings. Using default configuration.",
            _ => "Data operation failed. Some information may not be saved."
        };

        Plugin.Log.Error($"Data persistence operation '{operation}' failed: {ex.Message}");
        ShowUserError("Data Error", userMessage);
    }

    /// <summary>
    /// Handles general DM operation errors.
    /// </summary>
    /// <param name="operation">The operation that failed</param>
    /// <param name="ex">The exception that occurred</param>
    /// <param name="showToUser">Whether to show the error to the user</param>
    public static void HandleGeneralError(string operation, Exception ex, bool showToUser = true)
    {
        Plugin.Log.Error($"DM operation '{operation}' failed: {ex.Message}");
        
        if (showToUser)
        {
            var userMessage = "An unexpected error occurred. Please try again or restart the plugin if the problem persists.";
            ShowUserError("DM Error", userMessage);
        }
    }

    /// <summary>
    /// Shows a user-friendly error message in the chat.
    /// </summary>
    /// <param name="title">The error title</param>
    /// <param name="message">The error message</param>
    private static void ShowUserError(string title, string message)
    {
        try
        {
            var errorMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, $"[{title}] {message}")
                },
                new ChatCode((ushort)ChatType.Error)
            );
            
            // Try to add to current tab - for now just log since we can't access Plugin statically
            // In a real implementation, this would need to be refactored to pass the plugin instance
            Console.WriteLine($"[DM Error] {title}: {message}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to show error message to user: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds an error message to a specific DM history for context.
    /// </summary>
    /// <param name="player">The player whose DM history to add the error to</param>
    /// <param name="errorText">The error text to add</param>
    private static void AddErrorToHistory(DMPlayer player, string errorText)
    {
        try
        {
            var history = DMManager.Instance.GetHistory(player);
            if (history != null)
            {
                var errorMessage = Message.FakeMessage(
                    new List<Chunk>
                    {
                        new TextChunk(ChunkSource.None, null, $"[Error] {errorText}")
                    },
                    new ChatCode((ushort)ChatType.Error)
                );
                
                history.AddMessage(errorMessage, isIncoming: false);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to add error to DM history: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs detailed error information for debugging purposes.
    /// </summary>
    /// <param name="context">The context where the error occurred</param>
    /// <param name="ex">The exception that occurred</param>
    /// <param name="additionalInfo">Additional debugging information</param>
    public static void LogDetailedError(string context, Exception ex, Dictionary<string, object>? additionalInfo = null)
    {
        var logMessage = $"[DM Error] Context: {context}, Exception: {ex.GetType().Name}, Message: {ex.Message}";
        
        if (additionalInfo != null && additionalInfo.Count > 0)
        {
            var infoString = string.Join(", ", additionalInfo.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            logMessage += $", Additional Info: {infoString}";
        }
        
        if (ex.StackTrace != null)
        {
            logMessage += $", Stack Trace: {ex.StackTrace}";
        }
        
        Plugin.Log.Error(logMessage);
    }

    /// <summary>
    /// Validates player input and provides specific error messages.
    /// </summary>
    /// <param name="playerName">The player name to validate</param>
    /// <returns>True if valid, false if invalid (with error shown to user)</returns>
    public static bool ValidatePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            ShowUserError("Invalid Input", "Player name cannot be empty.");
            return false;
        }

        if (playerName.Length > 21) // FFXIV character name limit
        {
            ShowUserError("Invalid Input", "Player name is too long (maximum 21 characters).");
            return false;
        }

        if (playerName.Contains("@") && playerName.Split('@').Length != 2)
        {
            ShowUserError("Invalid Input", "Invalid format. Use 'PlayerName@WorldName' or just 'PlayerName'.");
            return false;
        }

        // Check for invalid characters (basic validation)
        var invalidChars = new[] { '<', '>', '/', '\\', '|', '?', '*', '"' };
        if (playerName.Any(c => invalidChars.Contains(c)))
        {
            ShowUserError("Invalid Input", "Player name contains invalid characters.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Handles errors that occur during plugin initialization or shutdown.
    /// </summary>
    /// <param name="phase">The initialization/shutdown phase</param>
    /// <param name="ex">The exception that occurred</param>
    public static void HandleInitializationError(string phase, Exception ex)
    {
        Plugin.Log.Error($"DM system initialization failed during '{phase}': {ex.Message}");
        
        // For initialization errors, we may not be able to show UI messages
        // So we just log them for now
        LogDetailedError($"Initialization-{phase}", ex);
    }
}