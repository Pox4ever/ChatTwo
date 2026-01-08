using System.Collections;
using System.Collections.Concurrent;
using ChatTwo.Code;
using ChatTwo.DM;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Bindings.ImGui;

namespace ChatTwo;

[Serializable]
internal class ConfigKeyBind
{
    public ModifierFlag Modifier;
    public VirtualKey Key;

    public override string ToString()
    {
        var modString = "";
        if (Modifier.HasFlag(ModifierFlag.Ctrl))
            modString += Language.Keybind_Modifier_Ctrl + " + ";
        if (Modifier.HasFlag(ModifierFlag.Shift))
            modString += Language.Keybind_Modifier_Shift + " + ";
        if (Modifier.HasFlag(ModifierFlag.Alt))
            modString += Language.Keybind_Modifier_Alt + " + ";
        return modString+Key.GetFancyName();
    }
}

[Serializable]
internal class Configuration : IPluginConfiguration
{
    private const int LatestVersion = 5;

    public int Version { get; set; } = LatestVersion;

    public bool HideChat = true;
    public bool HideDuringCutscenes = true;
    public bool HideWhenNotLoggedIn = true;
    public bool HideWhenUiHidden = true;
    public bool HideInLoadingScreens;
    public bool HideInBattle;
    public bool HideWhenInactive;
    public int InactivityHideTimeout = 10;
    public bool InactivityHideActiveDuringBattle = true;
    public Dictionary<ChatType, ChatSource>? InactivityHideChannels;
    public bool InactivityHideExtraChatAll = true;
    public HashSet<Guid> InactivityHideExtraChatChannels = [];
    public bool ShowHideButton = true;
    public bool NativeItemTooltips = true;
    public bool PrettierTimestamps = true;
    public bool MoreCompactPretty;
    public bool HideSameTimestamps;
    public bool ShowNoviceNetwork;
    public bool SidebarTabView;
    public bool PrintChangelog = true;
    public bool OnlyPreviewIf;
    public int PreviewMinimum = 1;
    public PreviewPosition PreviewPosition = PreviewPosition.Inside;
    public CommandHelpSide CommandHelpSide = CommandHelpSide.None;
    public KeybindMode KeybindMode = KeybindMode.Strict;
    public LanguageOverride LanguageOverride = LanguageOverride.None;
    public bool CanMove = true;
    public bool CanResize = true;
    public bool ShowTitleBar;
    public bool ShowPopOutTitleBar = true;
    public bool DatabaseBattleMessages;
    public bool LoadPreviousSession;
    public bool FilterIncludePreviousSessions;
    public bool SortAutoTranslate;
    public bool CollapseDuplicateMessages;
    public bool CollapseKeepUniqueLinks;
    public bool PlaySounds = true;
    public bool KeepInputFocus = true;
    public int MaxLinesToRender = 10_000; // 1-10000
    public bool Use24HourClock;

    public bool ShowEmotes = true;
    public HashSet<string> BlockedEmotes = [];
    
    // (Pox4eveR) Custom emote features
    public bool EnableBetterTTVEmotes = true;
    public bool EnableSevenTVEmotes = true;
    public float EmoteSize = 1.0f; // Emote size multiplier (1.0 = normal size)
    public string TwitchUsernames = ""; // Twitch usernames (comma-separated, e.g. "pox4ever,shroud") - will be automatically converted to user IDs
    public string TwitchUserIds = ""; // Twitch user IDs (comma-separated, e.g. "410517388,123456789") for loading personal 7TV emotes
    
    // Twitch OAuth integration
    public string TwitchAccessToken = "";
    public string TwitchUsername = "";
    public string TwitchUserId = "";
    public DateTime TwitchTokenExpiry = DateTime.MinValue;

    public bool FontsEnabled = true;
    public ExtraGlyphRanges ExtraGlyphRanges = 0;
    public float FontSizeV2 = 12.75f;
    public float SymbolsFontSizeV2 = 12.75f;
    public SingleFontSpec GlobalFontV2 = new()
    {
        // dalamud only ships KR as regular, which chat2 used previously for global fonts
        FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansKrRegular),
        SizePt = 12.75f,
    };
    public SingleFontSpec JapaneseFontV2 = new()
    {
        FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansJpMedium),
        SizePt = 12.75f,
    };
    public bool ItalicEnabled;
    public SingleFontSpec ItalicFontV2 = new()
    {
        FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansKrRegular),
        SizePt = 12.75f,
    };

    public float TooltipOffset;
    public float WindowAlpha = 100f;
    public Dictionary<ChatType, uint> ChatColours = new();
    public List<Tab> Tabs = [];

    public bool OverrideStyle;
    public string? ChosenStyle;
    
    // Modern UI Settings
    public bool ModernUIEnabled = true;
    public float UIRounding = 6.0f;
    public float UIBorderSize = 1.0f;
    public bool UseModernColors = true;
    public float ShadowStrength = 0.3f;
    
    // Enhanced Input Area Settings
    public bool ShowFloatingChannelIndicator = true;
    public bool ShowTypingIndicator = true;
    public bool EnhancedInputFeedback = true;
    public float UnfocusedTransparency = 70.0f; // Percentage of transparency for unfocused windows
    
    // Better Tab System Settings
    public bool ShowTabIcons = true;
    public bool EnableTabDragReorder = true;
    public bool SmoothTabTransitions = true;
    
    // Customizable Tab Icons
    public Dictionary<string, FontAwesomeIcon> CustomTabIcons = new()
    {
        { "Say", FontAwesomeIcon.Comment },
        { "Shout", FontAwesomeIcon.Bullhorn },
        { "Yell", FontAwesomeIcon.ExclamationTriangle },
        { "Tell", FontAwesomeIcon.Envelope },
        { "Party", FontAwesomeIcon.Users },
        { "Alliance", FontAwesomeIcon.Shield },
        { "FreeCompany", FontAwesomeIcon.Home },
        { "Linkshell", FontAwesomeIcon.Link },
        { "CrossLinkshell", FontAwesomeIcon.Globe },
        { "NoviceNetwork", FontAwesomeIcon.Leaf },
        { "Echo", FontAwesomeIcon.Terminal },
        { "System", FontAwesomeIcon.Cog },
        { "Debug", FontAwesomeIcon.Bug },
        { "Urgent", FontAwesomeIcon.ExclamationCircle },
        { "Notice", FontAwesomeIcon.InfoCircle },
        { "DM", FontAwesomeIcon.Envelope },
        { "Default", FontAwesomeIcon.CommentDots }
    };
    
    // Enhanced Emote Integration Settings
    public bool ShowInlineEmotePreviews = true;
    public bool EnableEmotePickerPopup = true;
    public bool EmotePickerSearchEnabled = true;
    
    // DM Section Pop-out Settings
    public bool DMSectionPoppedOut = true; // Whether the DM section is currently popped out
    public bool ShowDMSectionCollapseButtons = true; // Whether to show collapse/expand buttons in DM section window

    // DM Management Settings
    public bool CloseDMsOnLogout { get; set; } = false;
    public bool CloseDMsInCombat { get; set; } = false;
    public bool AutoOpenDMOnNewTell { get; set; } = false;
    public DMDefaultMode DefaultDMMode { get; set; } = DMDefaultMode.Tab;
    public ConfigKeyBind? OpenRecentDMKeybind { get; set; }

    public ConfigKeyBind? ChatTabForward;

    public enum DMDefaultMode
    {
        Tab,
        Window,
        Ask // Show both options in context menu
    }
    public ConfigKeyBind? ChatTabBackward;

    // Webinterface
    public bool WebinterfaceEnabled;
    public bool WebinterfaceAutoStart;
    public string WebinterfacePassword = WebinterfaceUtil.GenerateSimpleAuthCode();
    public int WebinterfacePort = 9000;
    public HashSet<string> AuthStore = [];
    public int WebinterfaceMaxLinesToSend = 1000; // 1-10000

    // DM Feature Settings
    public bool EnableDMWindows { get; set; } = true;
    public bool EnableDMTabs { get; set; } = true;
    
    // Message Routing Settings
    public bool ShowTellsInMainChat { get; set; } = true;
    public bool ShowTellsInDMOnly { get; set; } = false;
    
    // Window Management Settings
    public bool CascadeDMWindows { get; set; } = true;
    public System.Numerics.Vector2 DMWindowCascadeOffset { get; set; } = new(30, 30);
    
    // Appearance Settings
    public bool ShowDMTabIcons { get; set; } = true;
    public bool ShowUnreadIndicators { get; set; } = true;
    public string DMTabSuffix { get; set; } = " (DM)"; // For name conflicts
    
    // DM Message History Settings
    public bool LoadDMMessageHistory { get; set; } = true; // Whether to load previous DM messages when opening a DM tab/window
    public int DMMessageHistoryCount { get; set; } = 50; // Number of previous messages to load (1-200)
    public bool SaveDMMessages { get; set; } = true; // Whether to save DM messages to database (uses existing DatabaseBattleMessages logic)
    
    // DM Color Settings
    public bool UseDMCustomColors { get; set; } = true; // Whether to use custom colors for DM messages
    public uint DMIncomingColor { get; set; } = 0xFFFFB3FF; // Default FFXIV tell color (pinkish)
    public uint DMOutgoingColor { get; set; } = 0xFFFFB3FF; // Same color for consistency
    public uint DMErrorColor { get; set; } = 0xFF4444FF; // Red color for error messages
    
    // DM Window Persistence Settings
    public List<DMWindowState> OpenDMWindows { get; set; } = new(); // Persisted DM windows that should be restored on plugin reload
    
    // DM Performance Settings
    public bool EnableDMPerformanceLogging { get; set; } = false; // Whether to enable performance logging for DM windows
    public bool UseLightweightDMRendering { get; set; } = false; // Whether to use lightweight rendering for better FPS (now always enabled)
    public bool UseMinimalDMWindows { get; set; } = false; // Whether to use minimal window flags for maximum FPS

    internal void UpdateFrom(Configuration other, bool backToOriginal)
    {
        if (backToOriginal)
        {
            // Only reset PopOut for regular tabs, not DM tabs
            // DM tabs in the DM Section Window should stay there
            var tabsToReset = Tabs.Where(t => t.PopOut && !(t is ChatTwo.DM.DMTab)).ToList();
            
            foreach (var tab in tabsToReset)
            {
                tab.PopOut = false;
            }
        }

        HideChat = other.HideChat;
        HideDuringCutscenes = other.HideDuringCutscenes;
        HideWhenNotLoggedIn = other.HideWhenNotLoggedIn;
        HideWhenUiHidden = other.HideWhenUiHidden;
        HideInLoadingScreens = other.HideInLoadingScreens;
        HideInBattle = other.HideInBattle;
        HideWhenInactive = other.HideWhenInactive;
        InactivityHideTimeout = other.InactivityHideTimeout;
        InactivityHideActiveDuringBattle = other.InactivityHideActiveDuringBattle;
        InactivityHideChannels = other.InactivityHideChannels?.ToDictionary(entry => entry.Key, entry => entry.Value);
        InactivityHideExtraChatAll = other.InactivityHideExtraChatAll;
        InactivityHideExtraChatChannels = other.InactivityHideExtraChatChannels.ToHashSet();
        ShowHideButton = other.ShowHideButton;
        NativeItemTooltips = other.NativeItemTooltips;
        PrettierTimestamps = other.PrettierTimestamps;
        MoreCompactPretty = other.MoreCompactPretty;
        HideSameTimestamps = other.HideSameTimestamps;
        ShowNoviceNetwork = other.ShowNoviceNetwork;
        SidebarTabView = other.SidebarTabView;
        PrintChangelog = other.PrintChangelog;
        OnlyPreviewIf = other.OnlyPreviewIf;
        PreviewMinimum = other.PreviewMinimum;
        PreviewPosition = other.PreviewPosition;
        CommandHelpSide = other.CommandHelpSide;
        KeybindMode = other.KeybindMode;
        LanguageOverride = other.LanguageOverride;
        CanMove = other.CanMove;
        CanResize = other.CanResize;
        ShowTitleBar = other.ShowTitleBar;
        ShowPopOutTitleBar = other.ShowPopOutTitleBar;
        DatabaseBattleMessages = other.DatabaseBattleMessages;
        LoadPreviousSession = other.LoadPreviousSession;
        FilterIncludePreviousSessions = other.FilterIncludePreviousSessions;
        SortAutoTranslate = other.SortAutoTranslate;
        CollapseDuplicateMessages = other.CollapseDuplicateMessages;
        CollapseKeepUniqueLinks = other.CollapseKeepUniqueLinks;
        PlaySounds = other.PlaySounds;
        KeepInputFocus = other.KeepInputFocus;
        MaxLinesToRender = other.MaxLinesToRender;
        Use24HourClock = other.Use24HourClock;
        ShowEmotes = other.ShowEmotes;
        BlockedEmotes = other.BlockedEmotes;
        
        // (Pox4eveR) Custom emote features
        if (other is Configuration otherConfig)
        {
            EnableBetterTTVEmotes = otherConfig.EnableBetterTTVEmotes;
            EnableSevenTVEmotes = otherConfig.EnableSevenTVEmotes;
            EmoteSize = otherConfig.EmoteSize;
            TwitchUsernames = otherConfig.TwitchUsernames;
            TwitchUserIds = otherConfig.TwitchUserIds;
            TwitchAccessToken = otherConfig.TwitchAccessToken;
            TwitchUsername = otherConfig.TwitchUsername;
            TwitchUserId = otherConfig.TwitchUserId;
            TwitchTokenExpiry = otherConfig.TwitchTokenExpiry;
        }
        
        FontsEnabled = other.FontsEnabled;
        ItalicEnabled = other.ItalicEnabled;
        ExtraGlyphRanges = other.ExtraGlyphRanges;
        FontSizeV2 = other.FontSizeV2;
        GlobalFontV2 = other.GlobalFontV2;
        JapaneseFontV2 = other.JapaneseFontV2;
        ItalicFontV2 = other.ItalicFontV2;
        SymbolsFontSizeV2 = other.SymbolsFontSizeV2;
        TooltipOffset = other.TooltipOffset;
        WindowAlpha = other.WindowAlpha;
        ChatColours = other.ChatColours.ToDictionary(entry => entry.Key, entry => entry.Value);
        
        // CRITICAL: This line overwrites all our tab modifications!
        // We need to preserve DM tab states from the current config
        var preservedDMTabs = Tabs.OfType<ChatTwo.DM.DMTab>().ToList();
        
        Tabs = other.Tabs.Select(t => t.Clone()).ToList();
        
        // Re-add preserved DM tabs or update their states
        foreach (var preservedDMTab in preservedDMTabs)
        {
            // Find if this DM tab exists in the new tabs list
            var existingDMTab = Tabs.OfType<ChatTwo.DM.DMTab>()
                .FirstOrDefault(t => t.Player.Name == preservedDMTab.Player.Name && 
                                   t.Player.HomeWorld == preservedDMTab.Player.HomeWorld);
            
            if (existingDMTab != null)
            {
                // Update the existing DM tab to preserve its PopOut state
                existingDMTab.PopOut = preservedDMTab.PopOut;
            }
        }
        OverrideStyle = other.OverrideStyle;
        ChosenStyle = other.ChosenStyle;
        
        // Modern UI Settings
        ModernUIEnabled = other.ModernUIEnabled;
        UIRounding = other.UIRounding;
        UIBorderSize = other.UIBorderSize;
        UseModernColors = other.UseModernColors;
        ShadowStrength = other.ShadowStrength;
        
        // Enhanced Input Area Settings
        ShowFloatingChannelIndicator = other.ShowFloatingChannelIndicator;
        ShowTypingIndicator = other.ShowTypingIndicator;
        EnhancedInputFeedback = other.EnhancedInputFeedback;
        UnfocusedTransparency = other.UnfocusedTransparency;
        
        // Better Tab System Settings
        ShowTabIcons = other.ShowTabIcons;
        EnableTabDragReorder = other.EnableTabDragReorder;
        SmoothTabTransitions = other.SmoothTabTransitions;
        CustomTabIcons = new Dictionary<string, FontAwesomeIcon>(other.CustomTabIcons);
        
        // Enhanced Emote Integration Settings
        ShowInlineEmotePreviews = other.ShowInlineEmotePreviews;
        EnableEmotePickerPopup = other.EnableEmotePickerPopup;
        EmotePickerSearchEnabled = other.EmotePickerSearchEnabled;
        
        // DM Section Pop-out Settings
        DMSectionPoppedOut = other.DMSectionPoppedOut;
        ShowDMSectionCollapseButtons = other.ShowDMSectionCollapseButtons;
        
        // DM Management Settings
        CloseDMsOnLogout = other.CloseDMsOnLogout;
        CloseDMsInCombat = other.CloseDMsInCombat;
        AutoOpenDMOnNewTell = other.AutoOpenDMOnNewTell;
        DefaultDMMode = other.DefaultDMMode;
        OpenRecentDMKeybind = other.OpenRecentDMKeybind;
        
        ChatTabForward = other.ChatTabForward;
        ChatTabBackward = other.ChatTabBackward;
        WebinterfaceEnabled = other.WebinterfaceEnabled;
        WebinterfaceAutoStart = other.WebinterfaceAutoStart;
        WebinterfacePassword = other.WebinterfacePassword;
        WebinterfacePort = other.WebinterfacePort;
        WebinterfaceMaxLinesToSend = other.WebinterfaceMaxLinesToSend;
        
        // DM Feature Settings
        EnableDMWindows = other.EnableDMWindows;
        EnableDMTabs = other.EnableDMTabs;
        ShowTellsInMainChat = other.ShowTellsInMainChat;
        ShowTellsInDMOnly = other.ShowTellsInDMOnly;
        CascadeDMWindows = other.CascadeDMWindows;
        DMWindowCascadeOffset = other.DMWindowCascadeOffset;
        ShowDMTabIcons = other.ShowDMTabIcons;
        ShowUnreadIndicators = other.ShowUnreadIndicators;
        DMTabSuffix = other.DMTabSuffix;
        LoadDMMessageHistory = other.LoadDMMessageHistory;
        DMMessageHistoryCount = other.DMMessageHistoryCount;
        SaveDMMessages = other.SaveDMMessages;
        
        // DM Color Settings
        UseDMCustomColors = other.UseDMCustomColors;
        DMIncomingColor = other.DMIncomingColor;
        DMOutgoingColor = other.DMOutgoingColor;
        DMErrorColor = other.DMErrorColor;
        
        // DM Window Persistence Settings
        OpenDMWindows = other.OpenDMWindows?.Select(w => w.Clone()).ToList() ?? new();
        
        // DM Performance Settings
        EnableDMPerformanceLogging = other.EnableDMPerformanceLogging;
        UseLightweightDMRendering = other.UseLightweightDMRendering;
        UseMinimalDMWindows = other.UseMinimalDMWindows;
    }
}

[Serializable]
public enum UnreadMode
{
    All,
    Unseen,
    None,
}

internal static class UnreadModeExt
{
    internal static string Name(this UnreadMode mode) => mode switch
    {
        UnreadMode.All => Language.UnreadMode_All,
        UnreadMode.Unseen => Language.UnreadMode_Unseen,
        UnreadMode.None => Language.UnreadMode_None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    internal static string? Tooltip(this UnreadMode mode) => mode switch
    {
        UnreadMode.All => Language.UnreadMode_All_Tooltip,
        UnreadMode.Unseen => Language.UnreadMode_Unseen_Tooltip,
        UnreadMode.None => Language.UnreadMode_None_Tooltip,
        _ => null,
    };
}

[Serializable]
public class Tab
{
    public string Name = Language.Tab_DefaultName;
    public Dictionary<ChatType, ChatSource> ChatCodes = new();
    public bool ExtraChatAll;
    public HashSet<Guid> ExtraChatChannels = [];
    
    // Custom tab icon
    public FontAwesomeIcon? CustomIcon = null;

    public UnreadMode UnreadMode = UnreadMode.Unseen;
    public bool UnhideOnActivity;
    public bool DisplayTimestamp = true;
    public InputChannel? Channel;
    public bool PopOut;
    public bool IndependentOpacity;
    public float Opacity = 100f;
    public bool InputDisabled;

    public bool CanMove = true;
    public bool CanResize = true;

    public bool IndependentHide;
    public bool HideDuringCutscenes = true;
    public bool HideWhenNotLoggedIn = true;
    public bool HideWhenUiHidden = true;
    public bool HideInLoadingScreens;
    public bool HideInBattle;
    public bool HideWhenInactive;

    [NonSerialized] public uint Unread;
    [NonSerialized] public uint LastSendUnread;
    [NonSerialized] public long LastActivity;
    [NonSerialized] public MessageList Messages = new();

    [NonSerialized] public UsedChannel CurrentChannel = new();

    [NonSerialized] public Guid Identifier = Guid.NewGuid();

    internal bool Matches(Message message) => message.Matches(ChatCodes, ExtraChatAll, ExtraChatChannels);

    internal void AddMessage(Message message, bool unread = true)
    {
        Messages.AddPrune(message, MessageManager.MessageDisplayLimit);
        if (!unread)
            return;

        Unread += 1;
        if (message.Matches(Plugin.Config.InactivityHideChannels!, Plugin.Config.InactivityHideExtraChatAll, Plugin.Config.InactivityHideExtraChatChannels))
            LastActivity = Environment.TickCount64;
    }

    internal void Clear() => Messages.Clear();

    internal virtual Tab Clone()
    {
        return new Tab
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
            CustomIcon = CustomIcon,
        };
    }

    /// <summary>
    /// MessageList provides an ordered list of messages with duplicate ID
    /// tracking, sorting and mutex protection.
    /// </summary>
    public class MessageList
    {
        private readonly SemaphoreSlim LockSlim = new(1, 1);

        private readonly List<Message> Messages;
        private readonly HashSet<Guid> TrackedMessageIds;

        public MessageList()
        {
            Messages = [];
            TrackedMessageIds = [];
        }

        public MessageList(int initialCapacity)
        {
            Messages = new List<Message>(initialCapacity);
            TrackedMessageIds = new HashSet<Guid>(initialCapacity);
        }

        public void AddPrune(Message message, int max)
        {
            LockSlim.Wait(-1);
            try
            {
                AddLocked(message);
                PruneMaxLocked(max);
            }
            finally
            {
                LockSlim.Release();
            }
        }

        public void AddSortPrune(IEnumerable<Message> messages, int max)
        {
            LockSlim.Wait(-1);
            try
            {
                foreach (var message in messages)
                    AddLocked(message);

                SortLocked();
                PruneMaxLocked(max);
            }
            finally
            {
                LockSlim.Release();
            }
        }

        private void AddLocked(Message message)
        {
            if (TrackedMessageIds.Contains(message.Id))
                return;

            Messages.Add(message);
            TrackedMessageIds.Add(message.Id);
        }

        public void Clear()
        {
            LockSlim.Wait(-1);
            try
            {
                Messages.Clear();
                TrackedMessageIds.Clear();
            }
            finally
            {
                LockSlim.Release();
            }
        }

        private void SortLocked()
        {
            Messages.Sort((a, b) => a.Date.CompareTo(b.Date));
        }

        private void PruneMaxLocked(int max)
        {
            while (Messages.Count > max)
            {
                TrackedMessageIds.Remove(Messages[0].Id);
                Messages.RemoveAt(0);
            }
        }

        /// <summary>
        /// Returns an array copy of the message list for usage outside of main thread
        /// </summary>
        public async Task<Message[]> GetCopy(int millisecondsTimeout = -1)
        {
            await LockSlim.WaitAsync(millisecondsTimeout);
            try
            {
                return Messages.ToArray();
            }
            finally
            {
                LockSlim.Release();
            }
        }

        /// <summary>
        /// GetReadOnly returns a read-only list of messages while holding a
        /// reader lock. The list should be used with a using statement.
        /// </summary>
        public RLockedMessageList GetReadOnly(int millisecondsTimeout = -1)
        {
            LockSlim.Wait(millisecondsTimeout);
            return new RLockedMessageList(LockSlim, Messages);
        }

        public class RLockedMessageList(SemaphoreSlim lockSlim, List<Message> messages) : IReadOnlyList<Message>, IDisposable
        {
            private bool _disposed = false;
            
            public IEnumerator<Message> GetEnumerator()
            {
                return messages.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public int Count => messages.Count;

            public Message this[int index] => messages[index];

            public void Dispose()
            {
                if (!_disposed)
                {
                    try
                    {
                        lockSlim.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Semaphore was already disposed, ignore
                    }
                    catch (SemaphoreFullException)
                    {
                        // Semaphore is already at max count, ignore
                    }
                    _disposed = true;
                }
            }
        }
    }
}

public class UsedChannel
{
    internal InputChannel Channel = InputChannel.Invalid;
    internal List<Chunk> Name = [];
    internal TellTarget? TellTarget;

    internal bool UseTempChannel;
    internal InputChannel TempChannel = InputChannel.Invalid;
    internal TellTarget? TempTellTarget;

    internal void ResetTempChannel()
    {
        UseTempChannel = false;
        TempTellTarget = null;
        TempChannel = InputChannel.Invalid;
    }

    internal void SetChannel(InputChannel channel)
    {
        Channel = channel;
    }
}

[Serializable]
internal enum PreviewPosition
{
    None,
    Inside,
    Top,
    Bottom,
    Tooltip,
}

internal static class PreviewPositionExt
{
    internal static string Name(this PreviewPosition position) => position switch
    {
        PreviewPosition.None => Language.Options_Preview_None,
        PreviewPosition.Inside => Language.Options_Preview_Inside,
        PreviewPosition.Top => Language.Options_Preview_Top,
        PreviewPosition.Bottom => Language.Options_Preview_Bottom,
        PreviewPosition.Tooltip => Language.Options_Preview_Tooltip,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };
}

[Serializable]
internal enum CommandHelpSide
{
    None,
    Left,
    Right,
}

internal static class CommandHelpSideExt
{
    internal static string Name(this CommandHelpSide side) => side switch
    {
        CommandHelpSide.None => Language.CommandHelpSide_None,
        CommandHelpSide.Left => Language.CommandHelpSide_Left,
        CommandHelpSide.Right => Language.CommandHelpSide_Right,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };
}

[Serializable]
internal enum KeybindMode
{
    Flexible,
    Strict,
}

internal static class KeybindModeExt
{
    internal static string Name(this KeybindMode mode) => mode switch
    {
        KeybindMode.Flexible => Language.KeybindMode_Flexible_Name,
        KeybindMode.Strict => Language.KeybindMode_Strict_Name,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    internal static string? Tooltip(this KeybindMode mode) => mode switch
    {
        KeybindMode.Flexible => Language.KeybindMode_Flexible_Tooltip,
        KeybindMode.Strict => Language.KeybindMode_Strict_Tooltip,
        _ => null,
    };
}

[Serializable]
internal enum LanguageOverride
{
    None,
    ChineseSimplified,
    ChineseTraditional,
    Dutch,
    English,
    French,
    German,
    Greek,

    // Italian,
    Japanese,

    // Korean,
    // Norwegian,
    PortugueseBrazil,
    Romanian,
    Russian,
    Spanish,
    Swedish,
}

internal static class LanguageOverrideExt
{
    internal static string Name(this LanguageOverride mode) => mode switch
    {
        LanguageOverride.None => Language.LanguageOverride_None,
        LanguageOverride.ChineseSimplified => "简体中文",
        LanguageOverride.ChineseTraditional => "繁體中文",
        LanguageOverride.Dutch => "Nederlands",
        LanguageOverride.English => "English",
        LanguageOverride.French => "Français",
        LanguageOverride.German => "Deutsch",
        LanguageOverride.Greek => "Ελληνικά",
        // LanguageOverride.Italian => "Italiano",
        LanguageOverride.Japanese => "日本語",
        // LanguageOverride.Korean => "한국어 (Korean)",
        // LanguageOverride.Norwegian => "Norsk",
        LanguageOverride.PortugueseBrazil => "Português do Brasil",
        LanguageOverride.Romanian => "Română",
        LanguageOverride.Russian => "Русский",
        LanguageOverride.Spanish => "Español",
        LanguageOverride.Swedish => "Svenska",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    internal static string Code(this LanguageOverride mode) => mode switch
    {
        LanguageOverride.None => "",
        LanguageOverride.ChineseSimplified => "zh-hans",
        LanguageOverride.ChineseTraditional => "zh-hant",
        LanguageOverride.Dutch => "nl",
        LanguageOverride.English => "en",
        LanguageOverride.French => "fr",
        LanguageOverride.German => "de",
        LanguageOverride.Greek => "el",
        // LanguageOverride.Italian => "it",
        LanguageOverride.Japanese => "ja",
        // LanguageOverride.Korean => "ko",
        // LanguageOverride.Norwegian => "no",
        LanguageOverride.PortugueseBrazil => "pt-br",
        LanguageOverride.Romanian => "ro",
        LanguageOverride.Russian => "ru",
        LanguageOverride.Spanish => "es",
        LanguageOverride.Swedish => "sv",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

[Serializable]
[Flags]
internal enum ExtraGlyphRanges
{
    ChineseFull = 1 << 0,
    ChineseSimplifiedCommon = 1 << 1,
    Cyrillic = 1 << 2,
    Japanese = 1 << 3,
    Korean = 1 << 4,
    Thai = 1 << 5,
    Vietnamese = 1 << 6,
}

internal static class ExtraGlyphRangesExt
{
    internal static string Name(this ExtraGlyphRanges ranges) => ranges switch
    {
        ExtraGlyphRanges.ChineseFull => Language.ExtraGlyphRanges_ChineseFull_Name,
        ExtraGlyphRanges.ChineseSimplifiedCommon => Language.ExtraGlyphRanges_ChineseSimplifiedCommon_Name,
        ExtraGlyphRanges.Cyrillic => Language.ExtraGlyphRanges_Cyrillic_Name,
        ExtraGlyphRanges.Japanese => Language.ExtraGlyphRanges_Japanese_Name,
        ExtraGlyphRanges.Korean => Language.ExtraGlyphRanges_Korean_Name,
        ExtraGlyphRanges.Thai => Language.ExtraGlyphRanges_Thai_Name,
        ExtraGlyphRanges.Vietnamese => Language.ExtraGlyphRanges_Vietnamese_Name,
        _ => throw new ArgumentOutOfRangeException(nameof(ranges), ranges, null),
    };

    internal static unsafe nint Range(this ExtraGlyphRanges ranges) => ranges switch
    {
        ExtraGlyphRanges.ChineseFull => (nint)ImGui.GetIO().Fonts.GetGlyphRangesChineseFull(),
        ExtraGlyphRanges.ChineseSimplifiedCommon => (nint)ImGui.GetIO().Fonts.GetGlyphRangesChineseSimplifiedCommon(),
        ExtraGlyphRanges.Cyrillic => (nint)ImGui.GetIO().Fonts.GetGlyphRangesCyrillic(),
        ExtraGlyphRanges.Japanese => (nint)ImGui.GetIO().Fonts.GetGlyphRangesJapanese(),
        ExtraGlyphRanges.Korean => (nint)ImGui.GetIO().Fonts.GetGlyphRangesKorean(),
        ExtraGlyphRanges.Thai => (nint)ImGui.GetIO().Fonts.GetGlyphRangesThai(),
        ExtraGlyphRanges.Vietnamese => (nint)ImGui.GetIO().Fonts.GetGlyphRangesVietnamese(),
        _ => throw new ArgumentOutOfRangeException(nameof(ranges), ranges, null),
    };
}


internal static class DMDefaultModeExt
{
    internal static string Name(this Configuration.DMDefaultMode mode) => mode switch
    {
        Configuration.DMDefaultMode.Tab => "Tab",
        Configuration.DMDefaultMode.Window => "Window", 
        Configuration.DMDefaultMode.Ask => "Ask Each Time",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    internal static string? Tooltip(this Configuration.DMDefaultMode mode) => mode switch
    {
        Configuration.DMDefaultMode.Tab => "Open DMs as tabs in the main chat window by default",
        Configuration.DMDefaultMode.Window => "Open DMs as separate windows by default",
        Configuration.DMDefaultMode.Ask => "Show both tab and window options in context menu",
        _ => null,
    };
}

/// <summary>
/// Represents the persistent state of a DM Window that should be restored after plugin reload.
/// </summary>
[Serializable]
internal class DMWindowState
{
    public string PlayerName { get; set; } = "";
    public uint WorldId { get; set; }
    public ulong ContentId { get; set; }
    public System.Numerics.Vector2 Position { get; set; }
    public System.Numerics.Vector2 Size { get; set; }
    public bool IsOpen { get; set; } = true;
    
    public DMWindowState() { }
    
    public DMWindowState(DMPlayer player, System.Numerics.Vector2 position, System.Numerics.Vector2 size, bool isOpen = true)
    {
        PlayerName = player.Name;
        WorldId = player.HomeWorld;
        ContentId = player.ContentId;
        Position = position;
        Size = size;
        IsOpen = isOpen;
    }
    
    public DMPlayer ToDMPlayer()
    {
        return new DMPlayer(PlayerName, WorldId, ContentId);
    }
    
    public DMWindowState Clone()
    {
        return new DMWindowState
        {
            PlayerName = PlayerName,
            WorldId = WorldId,
            ContentId = ContentId,
            Position = Position,
            Size = Size,
            IsOpen = IsOpen
        };
    }
}
