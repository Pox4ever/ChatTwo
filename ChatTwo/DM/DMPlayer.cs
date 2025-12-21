using System;

namespace ChatTwo.DM;

/// <summary>
/// Represents a player for DM conversations, identified by name and home world.
/// </summary>
[Serializable]
internal class DMPlayer : IEquatable<DMPlayer>
{
    public string Name { get; set; } = string.Empty;
    public uint HomeWorld { get; set; }

    public DMPlayer()
    {
    }

    public DMPlayer(string name, uint homeWorld)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        HomeWorld = homeWorld;
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
        
        // Compare names (case-insensitive)
        var nameMatch = string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        
        // If names don't match, players are different
        if (!nameMatch) return false;
        
        // If either player has world 0 (unknown/unavailable), only compare names
        // This handles cases where world information couldn't be extracted
        if (HomeWorld == 0 || other.HomeWorld == 0)
            return true;
        
        // Both players have valid world information, compare both name and world
        return HomeWorld == other.HomeWorld;
    }

    public override bool Equals(object? obj)
    {
        return obj is DMPlayer other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Only use name for hash code to be consistent with Equals logic
        // This ensures that players with the same name but different worlds
        // (especially when one has world 0) can be found in dictionaries
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