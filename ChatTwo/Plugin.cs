using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ChatTwo.Code;
using ChatTwo.DM;
using ChatTwo.Http;
using ChatTwo.Ipc;
using ChatTwo.Resources;
using ChatTwo.Ui;
using ChatTwo.Util;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace ChatTwo;

// ReSharper disable once ClassNeverInstantiated.Global  
public sealed class Plugin : IDalamudPlugin
{
    internal const string PluginName = "(Pox4eveR) Chat 2";

    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notification { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    internal static Configuration Config = null!;

    public readonly WindowSystem WindowSystem = new(PluginName);
    public SettingsWindow SettingsWindow { get; }
    public ChatLogWindow ChatLogWindow { get; }
    public DbViewer DbViewer { get; }
    public InputPreview InputPreview { get; }
    public CommandHelpWindow CommandHelpWindow { get; }
    public SeStringDebugger SeStringDebugger { get; }
    public DebuggerWindow DebuggerWindow { get; }
    public DMSectionWindow DMSectionWindow { get; }

    internal Commands Commands { get; }
    internal GameFunctions.GameFunctions Functions { get; }
    internal MessageManager MessageManager { get; }
    internal DMMessageRouter DMMessageRouter { get; }
    internal IpcManager Ipc { get; }
    internal ExtraChat ExtraChat { get; }
    internal TypingIpc TypingIpc { get; }
    internal FontManager FontManager { get; }

    internal ServerCore ServerCore { get; }

    internal int DeferredSaveFrames = -1;
    private bool _needsDMTabConversion = false;

    internal DateTime GameStarted { get; }

    // Tab management needs to happen outside the chatlog window class for access reasons
    internal int LastTab { get; set; }
    internal int? WantedTab { get; set; }
    internal Tab CurrentTab
    {
        get
        {
            var i = LastTab;
            if (i > -1 && i < Config.Tabs.Count)
                return Config.Tabs[i];
            
            // Return a properly initialized fallback tab
            var fallbackTab = new Tab();
            fallbackTab.CurrentChannel = new UsedChannel();
            return fallbackTab;
        }
    }

    public Plugin()
    {
        try
        {
            GameStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();

            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();

            if (Config.Tabs.Count == 0)
                Config.Tabs.Add(TabsUtil.VanillaGeneral);
            Config.InactivityHideChannels ??= TabsUtil.AllChannels();

            LanguageChanged(Interface.UiLanguage);
            ImGuiUtil.Initialize(this);

            // Functions calls this in its ctor if the player is already logged in
            ServerCore = new ServerCore(this);

            Commands = new Commands(this);
            Functions = new GameFunctions.GameFunctions(this);
            DMMessageRouter = new DMMessageRouter(this);
            
            // Initialize DMManager with Plugin instance
            DMManager.Instance.Initialize(this);
            
            // Mark that we need to fix DM tabs on the first frame update (when we're on main thread)
            _needsDMTabConversion = true;
            
            Ipc = new IpcManager();
            TypingIpc = new TypingIpc(this);
            ExtraChat = new ExtraChat(this);
            FontManager = new FontManager();

            ChatLogWindow = new ChatLogWindow(this);
            SettingsWindow = new SettingsWindow(this);
            DbViewer = new DbViewer(this);
            InputPreview = new InputPreview(ChatLogWindow);
            CommandHelpWindow = new CommandHelpWindow(ChatLogWindow);
            SeStringDebugger = new SeStringDebugger(this);
            DebuggerWindow = new DebuggerWindow(this);
            DMSectionWindow = new DMSectionWindow(this, ChatLogWindow);

            WindowSystem.AddWindow(ChatLogWindow);
            WindowSystem.AddWindow(SettingsWindow);
            WindowSystem.AddWindow(DbViewer);
            WindowSystem.AddWindow(InputPreview);
            WindowSystem.AddWindow(CommandHelpWindow);
            WindowSystem.AddWindow(SeStringDebugger);
            WindowSystem.AddWindow(DebuggerWindow);
            WindowSystem.AddWindow(DMSectionWindow);

            FontManager.BuildFonts();

            Interface.UiBuilder.DisableCutsceneUiHide = true;
            Interface.UiBuilder.DisableGposeUiHide = true;

            MessageManager = new MessageManager(this); // requires Ui

            // let all the other components register, then initialize commands
            Commands.Initialise();

            if (Interface.Reason is not PluginLoadReason.Boot)
                MessageManager.FilterAllTabsAsync();

            Framework.Update += FrameworkUpdate;
            Interface.UiBuilder.Draw += Draw;
            Interface.LanguageChanged += LanguageChanged;

            if (Config.ShowEmotes)
                Task.Run(EmoteCache.LoadData);

            #if !DEBUG
            // Avoid 300ms hitch when sending first message by preloading the
            // auto-translate cache. Don't do this in debug because it makes
            // profiling difficult.
            AutoTranslate.PreloadCache();
            #endif

            // Automatically start the webserver if requested
            if (Config.WebinterfaceAutoStart)
            {
                Task.Run(() =>
                {
                    ServerCore.Start();
                    ServerCore.Run();
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Plugin load threw an error, turning off plugin");
            Dispose();

            // Re-throw the exception to fail the plugin load.
            throw;
        }
    }

    // Suppressing this warning because Dispose() is called in Plugin() if the
    // load fails, so some values may not be initialized.
    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    public void Dispose()
    {
        Interface.LanguageChanged -= LanguageChanged;
        Interface.UiBuilder.Draw -= Draw;
        Framework.Update -= FrameworkUpdate;
        GameFunctions.GameFunctions.SetChatInteractable(true);

        WindowSystem?.RemoveAllWindows();
        ChatLogWindow?.Dispose();
        DbViewer?.Dispose();
        InputPreview?.Dispose();
        SettingsWindow?.Dispose();
        DebuggerWindow?.Dispose();
        SeStringDebugger?.Dispose();

        TypingIpc?.Dispose();
        ExtraChat?.Dispose();
        Ipc?.Dispose();
        MessageManager?.DisposeAsync().AsTask().Wait();
        Functions?.Dispose();
        Commands?.Dispose();

        EmoteCache.Dispose();
        ServerCore?.DisposeAsync().AsTask().Wait();
    }

    private void Draw()
    {
        ChatLogWindow.BeginFrame();

        if (Config.HideInLoadingScreens && Condition[ConditionFlag.BetweenAreas])
        {
            ChatLogWindow.FinalizeFrame();
            TypingIpc?.Update();
            return;
        }

        ChatLogWindow.HideStateCheck();

        Interface.UiBuilder.DisableUserUiHide = !Config.HideWhenUiHidden;
        ChatLogWindow.DefaultText = ImGui.GetStyle().Colors[(int) ImGuiCol.Text];

        using ((Config.FontsEnabled ? FontManager.RegularFont : FontManager.Axis).Push())
        {
            WindowSystem.Draw();
        }

        ChatLogWindow.FinalizeFrame();
        TypingIpc?.Update();
    }

    internal void SaveConfig()
    {
        Interface.SavePluginConfig(Config);
    }
    
    /// <summary>
    /// Debug method to log the current state of all tabs.
    /// </summary>
    internal void DebugLogTabState()
    {
        try
        {
            Plugin.Log.Info($"=== DEBUG TAB STATE ===");
            Plugin.Log.Info($"Total tabs: {Config.Tabs.Count}");
            Plugin.Log.Info($"DMSectionPoppedOut: {Config.DMSectionPoppedOut}");
            
            for (int i = 0; i < Config.Tabs.Count; i++)
            {
                var tab = Config.Tabs[i];
                if (tab is DMTab dmTab)
                {
                    Plugin.Log.Info($"Tab {i}: DMTab '{dmTab.Name}' for {dmTab.Player.DisplayName} (PopOut={dmTab.PopOut})");
                }
                else
                {
                    Plugin.Log.Info($"Tab {i}: Regular Tab '{tab.Name}' (PopOut={tab.PopOut})");
                }
            }
            
            Plugin.Log.Info($"=== END DEBUG TAB STATE ===");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugLogTabState: Error logging tab state: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Fixes DM tabs that were deserialized as regular Tab objects due to JSON serialization limitations.
    /// This should be called after loading the configuration to restore proper DMTab instances.
    /// </summary>
    private void FixDeserializedDMTabs()
    {
        // Defer the conversion to the first framework update to ensure we're on the main thread
        // and all systems are properly initialized
        var conversionScheduled = false;
        
        Framework.Update += OnFrameworkUpdateForTabConversion;
        
        void OnFrameworkUpdateForTabConversion(IFramework framework)
        {
            if (conversionScheduled)
                return;
                
            conversionScheduled = true;
            Framework.Update -= OnFrameworkUpdateForTabConversion;
            
            try
            {
                PerformTabConversion();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error during tab conversion: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Performs the actual tab conversion on the main thread.
    /// </summary>
    private void PerformTabConversion()
    {
        Plugin.Log.Info($"PerformTabConversion: Starting with {Config.Tabs.Count} total tabs");
        
        // First, let's log all tabs and their types
        for (int i = 0; i < Config.Tabs.Count; i++)
        {
            var tab = Config.Tabs[i];
            Plugin.Log.Info($"PerformTabConversion: Tab {i}: '{tab.Name}' (Type: {tab.GetType().Name})");
            if (tab is DMTab dmTabInfo)
            {
                Plugin.Log.Info($"PerformTabConversion: DMTab '{tab.Name}' -> Player: {dmTabInfo.Player.DisplayName}, PopOut: {dmTabInfo.PopOut}");
            }
        }
        
        var tabsToReplace = new List<(int index, DMTab dmTab)>();
        
        for (int i = 0; i < Config.Tabs.Count; i++)
        {
            var tab = Config.Tabs[i];
            
            Plugin.Log.Debug($"PerformTabConversion: Checking tab {i}: '{tab.Name}' (Type: {tab.GetType().Name})");
            
            // Check if this looks like a DM tab that was deserialized as a regular Tab
            // We identify DM tabs by checking if they have tell-only chat codes and player-like names
            if (tab is not DMTab)
            {
                Plugin.Log.Debug($"PerformTabConversion: Checking if tab '{tab.Name}' is a likely DM tab");
                var isLikelyDM = IsLikelyDMTabByPattern(tab);
                Plugin.Log.Debug($"PerformTabConversion: Tab '{tab.Name}' is likely DM tab: {isLikelyDM}");
                
                if (isLikelyDM)
                {
                    Plugin.Log.Info($"Converting deserialized Tab '{tab.Name}' back to DMTab (pattern-based detection)");
                    
                    // Try to extract player info from the tab
                    var dmPlayer = TryCreateDMPlayerFromTab(tab);
                    if (dmPlayer != null)
                    {
                        Plugin.Log.Info($"Successfully created DMPlayer for '{tab.Name}': {dmPlayer.DisplayName}");
                        
                        // Create a new DMTab with the same properties as the original tab
                        var dmTab = new DMTab(dmPlayer)
                        {
                            Name = tab.Name,
                            ChatCodes = tab.ChatCodes.Count > 0 ? tab.ChatCodes : CreateDMChatCodes(), // Use existing codes or create DM-specific ones
                            ExtraChatAll = tab.ExtraChatAll,
                            ExtraChatChannels = tab.ExtraChatChannels,
                            UnreadMode = tab.UnreadMode,
                            UnhideOnActivity = tab.UnhideOnActivity,
                            Unread = tab.Unread,
                            LastActivity = tab.LastActivity,
                            DisplayTimestamp = tab.DisplayTimestamp,
                            Channel = tab.Channel,
                            PopOut = false, // CRITICAL: Set to false so it appears in DM Section Window
                            IndependentOpacity = tab.IndependentOpacity,
                            Opacity = tab.Opacity,
                            InputDisabled = tab.InputDisabled,
                            CurrentChannel = tab.CurrentChannel,
                            CanMove = tab.CanMove,
                            CanResize = tab.CanResize,
                            IndependentHide = tab.IndependentHide,
                            HideDuringCutscenes = tab.HideDuringCutscenes,
                            HideWhenNotLoggedIn = tab.HideWhenNotLoggedIn,
                            HideWhenUiHidden = tab.HideWhenUiHidden,
                            HideInLoadingScreens = tab.HideInLoadingScreens,
                            HideInBattle = tab.HideInBattle,
                            HideWhenInactive = tab.HideWhenInactive,
                            Identifier = tab.Identifier
                        };
                        
                        // Copy messages if any exist, but only messages related to this specific player
                        if (tab.Messages != null)
                        {
                            try
                            {
                                using var existingMessages = tab.Messages.GetReadOnly(1000); // 1 second timeout
                                var filteredMessages = new List<Message>();
                                
                                // Only copy messages that are related to this specific player
                                foreach (var message in existingMessages)
                                {
                                    // Only copy messages that are related to this specific player
                                    if (message.IsRelatedToPlayer(dmPlayer))
                                    {
                                        filteredMessages.Add(message);
                                    }
                                    else
                                    {
                                        // Log messages that are being filtered out for debugging
                                        Plugin.Log.Debug($"Filtering out message from {message.Code.Type}: not related to {dmPlayer.DisplayName}");
                                    }
                                }
                                
                                Plugin.Log.Info($"Filtered {filteredMessages.Count} messages out of {existingMessages.Count} for player '{dmPlayer.DisplayName}'");
                                
                                // Add filtered messages to the new DMTab
                                foreach (var message in filteredMessages)
                                {
                                    dmTab.Messages.AddPrune(message, MessageManager.MessageDisplayLimit);
                                }
                            }
                            catch (TimeoutException)
                            {
                                Plugin.Log.Warning($"Timeout reading messages for tab '{tab.Name}', skipping message copy");
                            }
                        }
                        
                        // Load message history from database (defer to avoid blocking)
                        Task.Run(() =>
                        {
                            try
                            {
                                dmTab.LoadMessageHistoryFromStore();
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Error($"Error loading message history for {dmTab.Player.DisplayName}: {ex.Message}");
                            }
                        });
                        
                        // Register the DMTab with DMManager for proper tracking
                        try
                        {
                            DMManager.Instance.RegisterConvertedDMTab(dmTab);
                            Plugin.Log.Debug($"Registered converted DMTab for {dmTab.Player.DisplayName} with DMManager");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warning($"Failed to register DMTab with DMManager: {ex.Message}");
                        }
                        
                        tabsToReplace.Add((i, dmTab));
                    }
                    else
                    {
                        Plugin.Log.Warning($"Failed to create DMPlayer for tab '{tab.Name}'");
                    }
                }
            }
            else if (tab is DMTab)
            {
                Plugin.Log.Debug($"PerformTabConversion: Tab '{tab.Name}' is already a DMTab");
            }
            else
            {
                Plugin.Log.Debug($"PerformTabConversion: Tab '{tab.Name}' does not match DM tab pattern");
            }
        }
        
        // Replace the tabs and remove duplicates
        var replacedTabs = new HashSet<string>(); // Track which player names we've already processed
        
        foreach (var (index, dmTab) in tabsToReplace)
        {
            var playerKey = $"{dmTab.Player.Name}@{dmTab.Player.HomeWorld}";
            
            if (replacedTabs.Contains(playerKey))
            {
                Plugin.Log.Info($"PerformTabConversion: Skipping duplicate tab at index {index} for '{playerKey}' - already processed");
                // Mark this tab for removal by setting it to null (we'll clean up later)
                Config.Tabs[index] = null!;
            }
            else
            {
                Plugin.Log.Info($"PerformTabConversion: Replacing tab at index {index} with DMTab for '{playerKey}'");
                Config.Tabs[index] = dmTab;
                replacedTabs.Add(playerKey);
            }
        }
        
        // Remove null tabs (duplicates we marked for removal)
        Config.Tabs = Config.Tabs.Where(tab => tab != null).ToList();
        
        if (tabsToReplace.Count > 0)
        {
            Plugin.Log.Info($"Converted {tabsToReplace.Count} deserialized Tab objects back to DMTab objects");
            
            // Log details about each converted tab
            foreach (var (index, dmTab) in tabsToReplace)
            {
                Plugin.Log.Info($"Converted tab at index {index}: '{dmTab.Name}' -> DMTab for {dmTab.Player.DisplayName} (PopOut={dmTab.PopOut})");
            }
            
            SaveConfig(); // Save the corrected configuration
            
            // Debug log the final tab state
            DebugLogTabState();
            
            // Force refresh of DM Section Window if it exists
            try
            {
                if (Config.DMSectionPoppedOut && DMSectionWindow != null)
                {
                    Plugin.Log.Info("PerformTabConversion: Forcing DM Section Window refresh");
                    DMSectionWindow.IsOpen = true; // Ensure it's marked as open
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"PerformTabConversion: Failed to refresh DM Section Window: {ex.Message}");
            }
        }
        else
        {
            Plugin.Log.Info("PerformTabConversion: No tabs needed conversion");
        }
    }
    
    /// <summary>
    /// Checks if a tab is likely a DM tab based on its configuration pattern.
    /// DM tabs typically have only Tell chat codes and player-like names, but during deserialization
    /// they may lose their chat codes entirely.
    /// </summary>
    private bool IsLikelyDMTabByPattern(Tab tab)
    {
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Analyzing tab '{tab.Name}'");
        
        // Log all chat codes for this tab
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has {tab.ChatCodes.Count} chat codes:");
        foreach (var chatCode in tab.ChatCodes)
        {
            Plugin.Log.Info($"  - {chatCode.Key}: {chatCode.Value}");
        }
        
        // Check if the name doesn't match common tab names
        var commonTabNames = new[] { "General", "Battle", "Event", "Say", "Shout", "Tell", "Party", "Alliance", "FC", "LS", "CWLS", "Novice Network", "Custom", "Geral", "Chats", "Fight", "MB", "Harumi Aoi", "Hanekawa Kett" };
        var isNotCommonTabName = !commonTabNames.Any(common => tab.Name.Contains(common, StringComparison.OrdinalIgnoreCase));
        
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' is not common tab name: {isNotCommonTabName}");
        
        // Check if the name looks like a player name (contains letters and possibly spaces, but not special symbols)
        var looksLikePlayerName = !string.IsNullOrEmpty(tab.Name) && 
                                 tab.Name.All(c => char.IsLetter(c) || char.IsWhiteSpace(c) || c == '\'' || c == '-') &&
                                 tab.Name.Any(char.IsLetter);
        
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' looks like player name: {looksLikePlayerName}");
        
        // For debugging, let's also check if this tab has messages and what types they are
        try
        {
            using var messages = tab.Messages.GetReadOnly(100); // 100ms timeout
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has {messages.Count} messages");
            
            // IMPROVED APPROACH: Check if it has tell-related chat codes or content
            var hasTellCodes = tab.ChatCodes.ContainsKey(ChatType.TellIncoming) || 
                              tab.ChatCodes.ContainsKey(ChatType.TellOutgoing);
            
            var hasTellContent = false;
            var hasOnlyTellContent = true;
            var tellMessageCount = 0;
            var totalMessageCount = messages.Count;
            
            // Check if the tab has any tell messages and if it has ONLY tell messages
            foreach (var message in messages)
            {
                if (message.Code.Type == ChatType.TellIncoming || message.Code.Type == ChatType.TellOutgoing)
                {
                    hasTellContent = true;
                    tellMessageCount++;
                }
                else
                {
                    hasOnlyTellContent = false;
                }
            }
            
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has tell codes: {hasTellCodes}");
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has tell content: {hasTellContent} ({tellMessageCount}/{totalMessageCount} messages)");
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has only tell content: {hasOnlyTellContent}");
            
            // MAIN CRITERIA: 
            // 1. Must look like a player name
            // 2. Must not be a common tab name
            // 3. Must have tell-related content (either codes or messages)
            // 4. If it has messages, they should be predominantly or exclusively tells
            var hasTellRelatedContent = hasTellCodes || hasTellContent;
            var isPredominantlyTells = totalMessageCount == 0 || (tellMessageCount > 0 && (double)tellMessageCount / totalMessageCount >= 0.8);
            
            var result = isNotCommonTabName && looksLikePlayerName && hasTellRelatedContent && isPredominantlyTells;
            
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' analysis:");
            Plugin.Log.Info($"  - isNotCommonTabName: {isNotCommonTabName}");
            Plugin.Log.Info($"  - looksLikePlayerName: {looksLikePlayerName}");
            Plugin.Log.Info($"  - hasTellRelatedContent: {hasTellRelatedContent}");
            Plugin.Log.Info($"  - isPredominantlyTells: {isPredominantlyTells}");
            Plugin.Log.Info($"  - final result: {result}");
            
            return result;
        }
        catch (TimeoutException)
        {
            Plugin.Log.Warning($"IsLikelyDMTabByPattern: Timeout reading messages for tab '{tab.Name}', skipping conversion");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"IsLikelyDMTabByPattern: Error analyzing tab '{tab.Name}': {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Creates the default chat codes for a DM tab (Tell incoming and outgoing).
    /// </summary>
    private Dictionary<ChatType, ChatSource> CreateDMChatCodes()
    {
        return new Dictionary<ChatType, ChatSource>
        {
            { ChatType.TellIncoming, ChatSource.Self | ChatSource.PartyMember | ChatSource.AllianceMember | ChatSource.Other | ChatSource.EngagedEnemy | ChatSource.UnengagedEnemy | ChatSource.FriendlyNpc | ChatSource.SelfPet | ChatSource.PartyPet | ChatSource.AlliancePet | ChatSource.OtherPet },
            { ChatType.TellOutgoing, ChatSource.Self | ChatSource.PartyMember | ChatSource.AllianceMember | ChatSource.Other | ChatSource.EngagedEnemy | ChatSource.UnengagedEnemy | ChatSource.FriendlyNpc | ChatSource.SelfPet | ChatSource.PartyPet | ChatSource.AlliancePet | ChatSource.OtherPet }
        };
    }
    
    /// <summary>
    /// Tries to create a DMPlayer from a tab's information by looking up previous tell messages
    /// to find the correct world ID for the player.
    /// </summary>
    private DMPlayer? TryCreateDMPlayerFromTab(Tab tab)
    {
        if (string.IsNullOrEmpty(tab.Name))
            return null;
            
        try
        {
            // First, try to find the player's world ID from previous tell messages in the database
            var worldId = FindPlayerWorldFromDatabase(tab.Name);
            
            // SPECIAL CASE: Manual overrides for known players with wrong database data
            var manualOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Ruyu Hayer", "Behemoth" },
                // Add more manual overrides here if needed
                // { "PlayerName", "WorldName" },
            };
            
            if (manualOverrides.TryGetValue(tab.Name, out var overrideWorldName))
            {
                var overrideWorldId = FindWorldIdByName(overrideWorldName);
                if (overrideWorldId != null)
                {
                    Plugin.Log.Info($"Manual override: Setting {tab.Name} to {overrideWorldName} (world {overrideWorldId}) instead of detected world {worldId}");
                    worldId = overrideWorldId;
                }
                else
                {
                    Plugin.Log.Warning($"Manual override failed: Could not find world ID for '{overrideWorldName}'");
                }
            }
            
            if (worldId == null)
            {
                // Fallback: Use current player's world as last resort
                if (ClientState.LocalPlayer?.HomeWorld == null)
                {
                    Plugin.Log.Warning($"Cannot create DMPlayer for '{tab.Name}' - no valid player or world available");
                    return null;
                }
                
                worldId = ClientState.LocalPlayer.HomeWorld.RowId;
                Plugin.Log.Warning($"Could not find world for '{tab.Name}' in message history, using current player's world {worldId} as fallback");
            }
            else
            {
                Plugin.Log.Info($"Found world {worldId} for '{tab.Name}' from message history");
            }
            
            return new DMPlayer(tab.Name, worldId.Value);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to create DMPlayer from tab '{tab.Name}': {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Searches the message database for previous tell messages to find the world ID of a player.
    /// Prioritizes incoming tells (which have correct ContentId) over outgoing tells (which may have wrong world names).
    /// </summary>
    private uint? FindPlayerWorldFromDatabase(string playerName)
    {
        try
        {
            Plugin.Log.Debug($"Searching message database for world ID of player '{playerName}'");
            
            // Get recent messages (last 30 days should be enough)
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            using var messageEnumerator = MessageManager.Store.GetMostRecentMessages(since: since, count: 20000); // Increased search limit
            
            var messageCount = 0;
            var tellMessageCount = 0;
            var incomingWorlds = new List<(uint worldId, DateTimeOffset date, string source)>();
            var outgoingWorlds = new List<(uint worldId, DateTimeOffset date, string source)>();
            
            // Look for tell messages (incoming or outgoing) that involve this player
            foreach (var message in messageEnumerator)
            {
                messageCount++;
                
                // Check if this is a tell message
                if (message.Code.Type != ChatType.TellIncoming && message.Code.Type != ChatType.TellOutgoing)
                    continue;
                
                tellMessageCount++;
                
                // PRIORITY 1: For incoming tells, check the sender (most reliable)
                if (message.Code.Type == ChatType.TellIncoming)
                {
                    var senderName = ExtractPlayerNameFromChunks(message.Sender);
                    if (!string.IsNullOrEmpty(senderName) && senderName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                    {
                        var worldId = ExtractWorldIdFromContentId(message.ContentId);
                        if (worldId != null)
                        {
                            incomingWorlds.Add((worldId.Value, message.Date, "incoming tell"));
                            Plugin.Log.Debug($"Found world {worldId} for player '{playerName}' from incoming tell dated {message.Date}");
                        }
                    }
                }
                // PRIORITY 2: For outgoing tells, check the content for the target player name (less reliable)
                else if (message.Code.Type == ChatType.TellOutgoing)
                {
                    // Outgoing tells have the target player name in the sender field (format: ">> PlayerName@World:")
                    var senderText = ExtractFullTextFromChunks(message.Sender);
                    if (!string.IsNullOrEmpty(senderText))
                    {
                        // Parse format like ">> PlayerName@WorldName:" or ">> PlayerName🌐WorldName:"
                        var match = System.Text.RegularExpressions.Regex.Match(senderText, @">> (.+?)[@🌐](.+?):");
                        if (match.Success)
                        {
                            var targetPlayerName = match.Groups[1].Value.Trim();
                            var worldName = match.Groups[2].Value.Trim();
                            
                            if (targetPlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Try to find world ID by world name
                                var worldId = FindWorldIdByName(worldName);
                                if (worldId != null)
                                {
                                    outgoingWorlds.Add((worldId.Value, message.Date, $"outgoing tell to {worldName}"));
                                    Plugin.Log.Debug($"Found world {worldId} ({worldName}) for player '{playerName}' from outgoing tell dated {message.Date}");
                                }
                                else
                                {
                                    Plugin.Log.Debug($"Could not find world ID for world name '{worldName}' from outgoing tell to '{playerName}'");
                                }
                            }
                        }
                    }
                }
            }
            
            Plugin.Log.Debug($"Searched {messageCount} total messages, {tellMessageCount} tell messages. Found {incomingWorlds.Count} incoming + {outgoingWorlds.Count} outgoing world matches for player '{playerName}'");
            
            // PRIORITIZE INCOMING TELLS: They have the correct ContentId and are more reliable
            if (incomingWorlds.Count > 0)
            {
                var mostRecentIncoming = incomingWorlds.OrderByDescending(w => w.date).First();
                Plugin.Log.Info($"Selected world {mostRecentIncoming.worldId} for player '{playerName}' from {mostRecentIncoming.source} (most recent incoming: {mostRecentIncoming.date})");
                return mostRecentIncoming.worldId;
            }
            
            // FALLBACK: Use outgoing tells only if no incoming tells found
            if (outgoingWorlds.Count > 0)
            {
                var mostRecentOutgoing = outgoingWorlds.OrderByDescending(w => w.date).First();
                Plugin.Log.Info($"Selected world {mostRecentOutgoing.worldId} for player '{playerName}' from {mostRecentOutgoing.source} (fallback from outgoing: {mostRecentOutgoing.date})");
                return mostRecentOutgoing.worldId;
            }
            
            Plugin.Log.Debug($"No world ID found for player '{playerName}' in message history");
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error searching database for player '{playerName}': {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Extracts the player name from message sender chunks.
    /// </summary>
    private string? ExtractPlayerNameFromChunks(List<Chunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk is TextChunk textChunk && !string.IsNullOrEmpty(textChunk.Content))
            {
                // Remove any world name suffix (e.g., "PlayerName@WorldName" -> "PlayerName")
                var name = textChunk.Content;
                var atIndex = name.IndexOf('@');
                if (atIndex > 0)
                    name = name.Substring(0, atIndex);
                
                return name.Trim();
            }
        }
        return null;
    }
    
    /// <summary>
    /// Extracts the full text content from message chunks.
    /// </summary>
    private string? ExtractFullTextFromChunks(List<Chunk> chunks)
    {
        var text = new System.Text.StringBuilder();
        foreach (var chunk in chunks)
        {
            if (chunk is TextChunk textChunk && !string.IsNullOrEmpty(textChunk.Content))
            {
                text.Append(textChunk.Content);
            }
        }
        return text.Length > 0 ? text.ToString() : null;
    }
    
    /// <summary>
    /// Finds a world ID by world name using the game's world sheet.
    /// </summary>
    private uint? FindWorldIdByName(string worldName)
    {
        try
        {
            var worldSheet = Sheets.WorldSheet;
            if (worldSheet == null)
                return null;
            
            foreach (var world in worldSheet)
            {
                if (world.Name.ToString().Equals(worldName, StringComparison.OrdinalIgnoreCase))
                {
                    return world.RowId;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error finding world ID for '{worldName}': {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Extracts the world ID from a ContentId.
    /// ContentId format: [WorldId (16 bits)][PlayerId (48 bits)]
    /// </summary>
    private uint? ExtractWorldIdFromContentId(ulong contentId)
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

    internal void LanguageChanged(string langCode)
    {
        var info = Config.LanguageOverride is LanguageOverride.None
            ? new CultureInfo(langCode)
            : new CultureInfo(Config.LanguageOverride.Code());

        Language.Culture = info;
    }

    private static readonly string[] ChatAddonNames =
    [
        "ChatLog",
        "ChatLogPanel_0",
        "ChatLogPanel_1",
        "ChatLogPanel_2",
        "ChatLogPanel_3"
    ];

    private void FrameworkUpdate(IFramework framework)
    {
        if (DeferredSaveFrames >= 0 && DeferredSaveFrames-- == 0)
            SaveConfig();

        // Fix DM tabs on the first frame update when we're on the main thread
        if (_needsDMTabConversion)
        {
            _needsDMTabConversion = false;
            FixDeserializedDMTabs();
        }

        if (!Config.HideChat)
            return;

        foreach (var name in ChatAddonNames)
            if (GameFunctions.GameFunctions.IsAddonInteractable(name))
                GameFunctions.GameFunctions.SetAddonInteractable(name, false);
    }

    public static bool InBattle => Condition[ConditionFlag.InCombat];
    public static bool GposeActive => Condition[ConditionFlag.WatchingCutscene];
    public static bool CutsceneActive => Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
}
