using System;
using System.Collections.Generic;
using System.Linq;

namespace ChatTwo.DM;

/// <summary>
/// Manages message history for a specific DM conversation with a player.
/// </summary>
[Serializable]
internal class DMMessageHistory
{
    public DMPlayer Player { get; set; } = null!;
    public List<Message> Messages { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public int UnreadCount { get; set; }

    [NonSerialized]
    private readonly object _lock = new();

    public DMMessageHistory()
    {
    }

    public DMMessageHistory(DMPlayer player)
    {
        Player = player ?? throw new ArgumentNullException(nameof(player));
    }

    /// <summary>
    /// Adds a message to the history.
    /// </summary>
    /// <param name="message">The message to add</param>
    /// <param name="isIncoming">True if the message is incoming, false if outgoing</param>
    public void AddMessage(Message message, bool isIncoming)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        lock (_lock)
        {
            Messages.Add(message);
            LastActivity = DateTime.UtcNow;

            if (isIncoming)
            {
                UnreadCount++;
            }
        }
    }

    /// <summary>
    /// Marks all messages as read by clearing the unread count.
    /// </summary>
    public void MarkAsRead()
    {
        lock (_lock)
        {
            UnreadCount = 0;
        }
    }

    /// <summary>
    /// Gets the most recent messages from the history.
    /// </summary>
    /// <param name="count">Maximum number of messages to retrieve (default: 50)</param>
    /// <returns>Array of recent messages</returns>
    public Message[] GetRecentMessages(int count = 50)
    {
        lock (_lock)
        {
            return Messages
                .OrderByDescending(m => m.Date)
                .Take(count)
                .OrderBy(m => m.Date)
                .ToArray();
        }
    }

    /// <summary>
    /// Gets all messages in the history.
    /// </summary>
    /// <returns>Array of all messages</returns>
    public Message[] GetAllMessages()
    {
        lock (_lock)
        {
            return Messages.ToArray();
        }
    }

    /// <summary>
    /// Clears all messages from the history.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Messages.Clear();
            UnreadCount = 0;
        }
    }
}