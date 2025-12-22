using System;

namespace ChatTwo.DM;

/// <summary>
/// Represents a player for DM conversations, identified by ContentId (primary) and name/world (fallback).
/// </summary>
[Serializable]
internal class DMPlayer : IEquatable<DMPlayer>
{
    public string Name { get; set; } = string.Empty;
    public uint HomeWorld { get; set; }
    public ulong ContentId { get; set; } // Primary identifier

    public DMPlayer()
    {
    }

    public DMPlayer(string name, uint homeWorld, ulong contentId = 0)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        HomeWorld = homeWorld;
        ContentId = contentId;
    }

    /// <summary>
    /// Gets the display name in format "PlayerName@WorldName"
    /// </summary>
    public string DisplayName
    {
        get
        {
            var worldName = GetWorldName();
            return string.IsNullOrEmpty(worldName) ? Name : $"{Name}@{worldName}";
        }
    }

    /// <summary>
    /// Gets just the player name for tab display
    /// </summary>
    public string TabName => Name;

    /// <summary>
    /// Gets the world name from the world ID
    /// </summary>
    public string GetWorldName()
    {
        if (HomeWorld == 0)
            return string.Empty;

        try
        {
            // Check if we're in a test environment or Dalamud is not available
            if (Sheets.WorldSheet == null)
                return $"World{HomeWorld}";
                
            if (Sheets.WorldSheet.TryGetRow(HomeWorld, out var world))
                return world.Name.ToString();
        }
        catch (Exception ex)
        {
            // In test environment, Plugin.Log might not be available
            try
            {
                Plugin.Log?.Warning($"Failed to get world name for world ID {HomeWorld}: {ex.Message}");
            }
            catch
            {
                // Ignore logging errors in test environment
            }
        }

        return $"World{HomeWorld}";
    }

    public bool Equals(DMPlayer? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        
        // PRIORITY 1: If both players have valid ContentIds, use ContentId for comparison
        if (ContentId != 0 && other.ContentId != 0)
        {
            return ContentId == other.ContentId;
        }
        
        // PRIORITY 2: If one or both have no ContentId, fall back to name comparison
        var nameMatch = string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        
        // If names don't match, players are different
        if (!nameMatch) return false;
        
        // PRIORITY 3: If either player has world 0 (unknown/unavailable), only compare names
        // This handles cases where world information couldn't be extracted
        if (HomeWorld == 0 || other.HomeWorld == 0)
            return true;
        
        // PRIORITY 4: Both players have valid world information, compare both name and world
        return HomeWorld == other.HomeWorld;
    }

    public override bool Equals(object? obj)
    {
        return obj is DMPlayer other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Use ContentId for hash if available, otherwise use name
        if (ContentId != 0)
        {
            return ContentId.GetHashCode();
        }
        
        // Fallback to name-based hash for consistency with Equals logic
        return Name?.ToLowerInvariant()?.GetHashCode() ?? 0;
    }

    public static bool operator ==(DMPlayer? left, DMPlayer? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(DMPlayer? left, DMPlayer? right)
    {
        return !Equals(left, right);
    }

    public override string ToString()
    {
        return DisplayName;
    }
}