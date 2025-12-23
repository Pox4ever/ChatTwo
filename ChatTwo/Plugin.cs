using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
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
            
            // Clean up any stale DM references from previous sessions
            // This prevents "Focus Existing DM" issues after plugin reload
            DMManager.Instance.CleanupStaleReferences();
            
            // CRITICAL FIX: Restore existing DM tabs after cleanup
            // This ensures that DM tabs that were open before plugin reload are properly tracked
            DMManager.Instance.RestoreExistingDMTabs();
            
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

            // PERSISTENCE: Restore existing DM windows after MessageManager is created
            // This ensures that DM windows can properly load message history from the MessageStore
            DMManager.Instance.RestoreExistingDMWindows();

            // Register debug command BEFORE Commands.Initialise() so it gets properly added
            Commands.Register("/chat2debugdm", "Debug DM tab conversion", false).Execute += DebugDMCommand;
            
            // Register DM window restoration command
            Commands.Register("/chat2dmrestore", "Restore DM windows after plugin reload", false).Execute += RestoreDMWindowsCommand;

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
        // Clean up debug commands
        Commands.Register("/chat2debugdm", "Debug DM tab conversion", false).Execute -= DebugDMCommand;
        Commands.Register("/chat2dmrestore", "Restore DM windows after plugin reload", false).Execute -= RestoreDMWindowsCommand;
        
        Interface.LanguageChanged -= LanguageChanged;
        Interface.UiBuilder.Draw -= Draw;
        Framework.Update -= FrameworkUpdate;
        GameFunctions.GameFunctions.SetChatInteractable(true);

        // Clean up all DM windows before removing all windows
        DMManager.Instance.CleanupAllDMWindows();
        
        // Also ensure DM Section Window is removed
        if (DMSectionWindow != null)
        {
            DMSectionWindow.IsOpen = false;
            try
            {
                // Check if the window is actually registered before trying to remove it
                if (WindowSystem?.Windows.Contains(DMSectionWindow) == true)
                {
                    WindowSystem?.RemoveWindow(DMSectionWindow);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to remove DM Section Window from WindowSystem during disposal: {ex.Message}");
            }
        }

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
    /// Debug command handler for testing DM functionality.
    /// </summary>
    private void DebugDMCommand(string command, string arguments)
    {
        try
        {
            var args = arguments.Trim().ToLowerInvariant();
            
            switch (args)
            {
                case "convert":
                case "":
                    Plugin.Log.Info("Debug: Triggering DM tab conversion");
                    DebugTriggerDMTabConversion();
                    break;
                    
                case "state":
                    Plugin.Log.Info("Debug: Logging current tab state");
                    DebugLogTabState();
                    break;
                    
                case "test":
                    Plugin.Log.Info("Debug: Running DM conversion test");
                    DMManager.Instance.TestDMConversion();
                    break;
                    
                case "reload":
                case "history":
                    Plugin.Log.Info("Debug: Reloading history for current DM tab");
                    DebugReloadCurrentDMHistory();
                    break;
                    
                case "testregex":
                case "regex":
                    Plugin.Log.Info("Debug: Testing CrossWorld regex pattern");
                    DebugTestCrossWorldRegex();
                    break;
                    
                case "cleanup":
                case "merge":
                    Plugin.Log.Info("Debug: Cleaning up duplicate DM tabs");
                    DebugCleanupDuplicateDMTabs();
                    break;
                    
                case "aggressive":
                    Plugin.Log.Info("Debug: Running aggressive DM cleanup");
                    DMManager.Instance.AggressiveCleanupAllDMTabs();
                    break;
                    
                case "stale":
                    Plugin.Log.Info("Debug: Running stale reference cleanup");
                    DMManager.Instance.CleanupStaleReferences();
                    break;
                    
                case "debug":
                case "show":
                    Plugin.Log.Info("Debug: Showing current DM state");
                    DebugShowDMState();
                    break;
                    
                case "window":
                case "dmwindow":
                    Plugin.Log.Info("Debug: Checking DM Section Window state");
                    DebugDMSectionWindowState();
                    break;
                    
                case "restore":
                    Plugin.Log.Info("Debug: Manually triggering DM tab restoration");
                    DMManager.Instance.RestoreExistingDMTabs();
                    break;
                    
                case "reloadplugin":
                case "plugin":
                    Plugin.Log.Info("Debug: Plugin reload requested");
                    DebugShowReloadInstructions();
                    break;
                    
                case "force":
                case "remove":
                    Plugin.Log.Info("Debug: Force removing all DM tabs");
                    DebugForceRemoveAllDMTabs();
                    break;
                    
                default:
                    Plugin.Log.Info("Debug DM Commands:");
                    Plugin.Log.Info("  /chat2debugdm convert - Trigger DM tab conversion");
                    Plugin.Log.Info("  /chat2debugdm state - Log current tab state");
                    Plugin.Log.Info("  /chat2debugdm test - Run DM conversion test");
                    Plugin.Log.Info("  /chat2debugdm reload - Reload history for current DM tab");
                    Plugin.Log.Info("  /chat2debugdm testregex - Test CrossWorld regex pattern");
                    Plugin.Log.Info("  /chat2debugdm cleanup - Clean up duplicate DM tabs");
                    Plugin.Log.Info("  /chat2debugdm aggressive - Run aggressive DM cleanup");
                    Plugin.Log.Info("  /chat2debugdm stale - Run stale reference cleanup");
                    Plugin.Log.Info("  /chat2debugdm debug - Show current DM state");
                    Plugin.Log.Info("  /chat2debugdm window - Debug DM Section Window state");
                    Plugin.Log.Info("  /chat2debugdm restore - Manually restore existing DM tabs");
                    Plugin.Log.Info("  /chat2debugdm plugin - Show plugin reload instructions");
                    Plugin.Log.Info("  /chat2debugdm force - Force remove all DM tabs");
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugDMCommand: Error executing debug command: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug method to reload history for the current DM tab.
    /// </summary>
    private void DebugReloadCurrentDMHistory()
    {
        try
        {
            var currentTab = CurrentTab;
            if (currentTab is DMTab dmTab)
            {
                Plugin.Log.Info($"DebugReloadCurrentDMHistory: Reloading history for current DM tab: {dmTab.Player.DisplayName}");
                dmTab.DebugReloadHistory();
            }
            else
            {
                Plugin.Log.Info("DebugReloadCurrentDMHistory: Current tab is not a DM tab");
                
                // Try to find any DM tab
                var firstDMTab = Config.Tabs.OfType<DMTab>().FirstOrDefault();
                if (firstDMTab != null)
                {
                    Plugin.Log.Info($"DebugReloadCurrentDMHistory: Found DM tab for {firstDMTab.Player.DisplayName}, reloading its history");
                    firstDMTab.DebugReloadHistory();
                }
                else
                {
                    Plugin.Log.Info("DebugReloadCurrentDMHistory: No DM tabs found");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugReloadCurrentDMHistory: Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug method to test the CrossWorld regex pattern with sample data.
    /// </summary>
    private void DebugTestCrossWorldRegex()
    {
        try
        {
            Plugin.Log.Info("=== TESTING OUTGOING TELL PARSING ===");
            
            // Test cases from the actual logs
            var testCases = new[]
            {
                new { Chunk = ">> ♣Hanekawa KettCrossWorldJenova: ", PlayerName = "Hanekawa Kett" },
                new { Chunk = ">> Harumi Aoi: ", PlayerName = "Harumi Aoi" },
                new { Chunk = ">> ♣TestPlayerCrossWorldBehemoth: ", PlayerName = "TestPlayer" },
                new { Chunk = ">> ♠John SmithCrossWorldPhoenix: ", PlayerName = "John Smith" }
            };
            
            foreach (var testCase in testCases)
            {
                Plugin.Log.Info($"Testing: '{testCase.Chunk}' for player '{testCase.PlayerName}'");
                
                // Test the new parsing logic
                if (testCase.Chunk.StartsWith(">>") && testCase.Chunk.Contains(":"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(testCase.Chunk, @">>\s*(.+?):");
                    if (match.Success)
                    {
                        var extractedText = match.Groups[1].Value.Trim();
                        Plugin.Log.Info($"  ✓ Extracted text: '{extractedText}'");
                        
                        // Clean up the extracted text
                        var cleanedName = CleanPlayerNameForComparison(extractedText);
                        Plugin.Log.Info($"  ✓ Cleaned name: '{cleanedName}'");
                        Plugin.Log.Info($"  ✓ Name matches: {string.Equals(cleanedName, testCase.PlayerName, StringComparison.OrdinalIgnoreCase)}");
                    }
                    else
                    {
                        Plugin.Log.Info($"  ✗ Regex did not match");
                    }
                }
                else
                {
                    Plugin.Log.Info($"  ✗ Does not match expected format");
                }
                Plugin.Log.Info("");
            }
            
            Plugin.Log.Info("=== OUTGOING TELL PARSING TEST COMPLETE ===");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugTestCrossWorldRegex: Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Helper method for testing player name cleaning logic.
    /// </summary>
    private static string CleanPlayerNameForComparison(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return string.Empty;

        var cleaned = rawText.Trim();
        
        // Remove friend category symbols (♣, ♠, ♦, ♥, etc.)
        // These are user-defined friend categories in FFXIV
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[♣♠♦♥♤♧♢♡]", "");
        
        // Handle CrossWorld format: "PlayerNameCrossWorldWorldName" -> "PlayerName"
        if (cleaned.Contains("CrossWorld"))
        {
            var crossWorldMatch = System.Text.RegularExpressions.Regex.Match(cleaned, @"(.+?)CrossWorld");
            if (crossWorldMatch.Success)
            {
                cleaned = crossWorldMatch.Groups[1].Value.Trim();
            }
        }
        
        // Remove any @ symbol and everything after it (world name)
        var atIndex = cleaned.IndexOf('@');
        if (atIndex >= 0)
        {
            cleaned = cleaned.Substring(0, atIndex);
        }
        
        // Remove any remaining special characters and whitespace
        cleaned = cleaned.Trim();
        
        return cleaned;
    }

    /// <summary>
    /// Shows instructions for reloading the plugin.
    /// </summary>
    private void DebugShowReloadInstructions()
    {
        try
        {
            Plugin.Log.Info("=== PLUGIN RELOAD INSTRUCTIONS ===");
            Plugin.Log.Info("To reload ChatTwo plugin, use one of these commands:");
            Plugin.Log.Info("  /xlplugins reload ChatTwo");
            Plugin.Log.Info("  /xlplugins reload \"(Pox4eveR) Chat 2\"");
            Plugin.Log.Info("");
            Plugin.Log.Info("Alternative methods:");
            Plugin.Log.Info("  1. Open Plugin Installer (/xlplugins)");
            Plugin.Log.Info("  2. Find ChatTwo in the list");
            Plugin.Log.Info("  3. Click the reload button");
            Plugin.Log.Info("");
            Plugin.Log.Info("After reload, your DM tabs should automatically reappear!");
            Plugin.Log.Info("=== END RELOAD INSTRUCTIONS ==="); 
            
            // Also show a chat message for convenience
            try
            {
                var reloadMessage = Message.FakeMessage(
                    new List<Chunk>
                    {
                        new TextChunk(ChunkSource.None, null, "[ChatTwo] To reload plugin: "),
                        new TextChunk(ChunkSource.None, null, "/xlplugins reload ChatTwo")
                    },
                    new ChatCode((ushort)ChatType.System)
                );
                
                // Add to current tab if available
                var currentTab = CurrentTab;
                if (currentTab != null)
                {
                    currentTab.AddMessage(reloadMessage, unread: false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Failed to show reload message in chat: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugShowReloadInstructions: Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Debug method to show the current DM Section Window state.  
    /// </summary>
    private void DebugDMSectionWindowState()
    {
        try
        {
            Plugin.Log.Info("=== DEBUG DM SECTION WINDOW STATE ===");
            
            if (DMSectionWindow == null)
            {
                Plugin.Log.Info("DMSectionWindow: NULL");
                return;
            }
            
            Plugin.Log.Info($"DMSectionWindow.IsOpen: {DMSectionWindow.IsOpen}");
            Plugin.Log.Info($"DMSectionWindow.Collapsed: {DMSectionWindow.Collapsed}");
            Plugin.Log.Info($"DMSectionWindow.Position: {DMSectionWindow.Position}");
            Plugin.Log.Info($"DMSectionWindow.Size: {DMSectionWindow.Size}");
            Plugin.Log.Info($"DMSectionWindow.Flags: {DMSectionWindow.Flags}");
            
            // Check if it's in WindowSystem
            var isInWindowSystem = WindowSystem?.Windows.Contains(DMSectionWindow) == true;
            Plugin.Log.Info($"DMSectionWindow in WindowSystem: {isInWindowSystem}");
            
            if (WindowSystem != null)
            {
                Plugin.Log.Info($"Total windows in WindowSystem: {WindowSystem.Windows.Count}");
                foreach (var window in WindowSystem.Windows)
                {
                    Plugin.Log.Info($"  - Window: {window.WindowName} (IsOpen: {window.IsOpen})");
                }
            }
            
            // Force the window to be visible and reset position
            Plugin.Log.Info("Forcing DM Section Window to be visible and resetting position...");
            DMSectionWindow.IsOpen = true;
            DMSectionWindow.Position = new Vector2(100, 100);
            DMSectionWindow.Size = new Vector2(400, 300);
            
            // Ensure it's in WindowSystem
            if (WindowSystem != null && !WindowSystem.Windows.Contains(DMSectionWindow))
            {
                WindowSystem.AddWindow(DMSectionWindow);
                Plugin.Log.Info("Added DM Section Window to WindowSystem");
            }
            
            Plugin.Log.Info("=== END DEBUG DM SECTION WINDOW STATE ===");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugDMSectionWindowState: Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Debug method to show the current DM state for troubleshooting.
    /// </summary> 
    private void DebugShowDMState()
    {
        try
        {
            Plugin.Log.Info("=== DEBUG DM STATE ===");
            
            var dmManager = DMManager.Instance;
            
            Plugin.Log.Info($"DMSectionPoppedOut: {Config.DMSectionPoppedOut}");
            Plugin.Log.Info($"DMSectionWindow IsOpen: {DMSectionWindow?.IsOpen}");
            
            // Show all tabs in configuration
            Plugin.Log.Info($"Total tabs in config: {Config.Tabs.Count}");
            var dmTabsInConfig = Config.Tabs.OfType<DMTab>().ToList();
            Plugin.Log.Info($"DM tabs in config: {dmTabsInConfig.Count}");
            
            foreach (var dmTab in dmTabsInConfig)
            {
                Plugin.Log.Info($"  Config DM Tab: {dmTab.Player.DisplayName} (PopOut: {dmTab.PopOut})");
            }
            
            // Show tracking state 
            var openTabs = dmManager.GetOpenDMTabs().ToList(); 
            var openWindows = dmManager.GetOpenDMWindows().ToList();
            
            Plugin.Log.Info($"Tracked DM tabs: {openTabs.Count}");
            foreach (var tab in openTabs)
            {
                Plugin.Log.Info($"  Tracked Tab: {tab.Player.DisplayName}");
            }
            
            Plugin.Log.Info($"Tracked DM windows: {openWindows.Count}"); 
            foreach (var window in openWindows)
            {
                Plugin.Log.Info($"  Tracked Window: {window.DMTab.Player.DisplayName} (IsOpen: {window.IsOpen})");
            }
            
            // Check what tabs would be shown in DM Section Window  
            if (Config.DMSectionPoppedOut && DMSectionWindow?.IsOpen == true)
            {
                var dmTabsForSection = Config.Tabs
                    .Where(tab => !tab.PopOut && tab is DMTab)
                    .Cast<DMTab>()
                    .ToList();
                    
                Plugin.Log.Info($"Tabs that should be in DM Section Window: {dmTabsForSection.Count}");
                foreach (var tab in dmTabsForSection)
                {
                    Plugin.Log.Info($"  DM Section Tab: {tab.Player.DisplayName}");
                }
            }
            
            Plugin.Log.Info("=== END DEBUG DM STATE ===");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugShowDMState: Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Debug method to force remove all DM tabs regardless of state.
    /// </summary>
    private void DebugForceRemoveAllDMTabs()
    {
        try
        {
            Plugin.Log.Info("DebugForceRemoveAllDMTabs: Force removing all DM tabs");
            
            var dmTabsToRemove = Config.Tabs.OfType<DMTab>().ToList();
            Plugin.Log.Info($"DebugForceRemoveAllDMTabs: Found {dmTabsToRemove.Count} DM tabs to remove");
            
            foreach (var dmTab in dmTabsToRemove)
            {
                Plugin.Log.Info($"DebugForceRemoveAllDMTabs: Removing DM tab for {dmTab.Player.DisplayName}");
                Config.Tabs.Remove(dmTab);
                
                // Also remove from DMManager tracking
                DMManager.Instance.ForceCleanupPlayer(dmTab.Player);
            }
            
            if (dmTabsToRemove.Count > 0)
            {
                SaveConfig();
                Plugin.Log.Info($"DebugForceRemoveAllDMTabs: Removed {dmTabsToRemove.Count} DM tabs and saved config");
            }
            else
            {
                Plugin.Log.Info("DebugForceRemoveAllDMTabs: No DM tabs found to remove");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugForceRemoveAllDMTabs: Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Debug method to clean up duplicate DM tabs for the same player.
    /// </summary>
    private void DebugCleanupDuplicateDMTabs()
    {
        try
        {
            Plugin.Log.Info("DebugCleanupDuplicateDMTabs: Starting cleanup of duplicate DM tabs");
            
            var dmTabs = Config.Tabs.OfType<DMTab>().ToList();
            Plugin.Log.Info($"DebugCleanupDuplicateDMTabs: Found {dmTabs.Count} DM tabs");
            
            // Group DM tabs by normalized player name
            var groupedTabs = dmTabs.GroupBy(tab => tab.GetNormalizedPlayerName()).ToList();
            
            var duplicatesFound = 0;
            var tabsRemoved = 0;
            
            foreach (var group in groupedTabs)
            {
                var tabsForPlayer = group.ToList();
                if (tabsForPlayer.Count > 1)
                {
                    duplicatesFound++;
                    Plugin.Log.Info($"DebugCleanupDuplicateDMTabs: Found {tabsForPlayer.Count} duplicate tabs for player '{group.Key}':");
                    
                    // Log all duplicate tabs
                    foreach (var tab in tabsForPlayer)
                    {
                        Plugin.Log.Info($"  - Tab: '{tab.Name}' (Player: '{tab.Player.Name}', World: {tab.Player.HomeWorld})");
                    }
                    
                    // Keep the tab with the cleanest name (shortest, most basic)
                    var tabToKeep = tabsForPlayer.OrderBy(t => t.Name.Length).ThenBy(t => t.Name).First();
                    var tabsToRemove = tabsForPlayer.Where(t => t != tabToKeep).ToList();
                    
                    Plugin.Log.Info($"DebugCleanupDuplicateDMTabs: Keeping tab '{tabToKeep.Name}', removing {tabsToRemove.Count} duplicates");
                    
                    // Merge messages from duplicate tabs into the kept tab
                    foreach (var tabToRemove in tabsToRemove)
                    {
                        try
                        {
                            using var messages = tabToRemove.Messages.GetReadOnly(1000);
                            foreach (var message in messages)
                            {
                                tabToKeep.AddMessage(message, unread: false);
                            }
                            Plugin.Log.Info($"DebugCleanupDuplicateDMTabs: Merged {messages.Count} messages from '{tabToRemove.Name}' to '{tabToKeep.Name}'");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warning($"DebugCleanupDuplicateDMTabs: Failed to merge messages from '{tabToRemove.Name}': {ex.Message}");
                        }
                        
                        // Remove the duplicate tab
                        Config.Tabs.Remove(tabToRemove);
                        tabsRemoved++;
                        Plugin.Log.Info($"DebugCleanupDuplicateDMTabs: Removed duplicate tab '{tabToRemove.Name}'");
                    }
                }
            }
            
            if (tabsRemoved > 0)
            {
                SaveConfig();
                Plugin.Log.Info($"DebugCleanupDuplicateDMTabs: Cleanup complete. Found {duplicatesFound} sets of duplicates, removed {tabsRemoved} duplicate tabs");
            }
            else
            {
                Plugin.Log.Info("DebugCleanupDuplicateDMTabs: No duplicate DM tabs found");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugCleanupDuplicateDMTabs: Error during cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug method to manually trigger DM tab conversion for testing.
    /// </summary>
    internal void DebugTriggerDMTabConversion()
    {
        try
        {
            Plugin.Log.Info("DebugTriggerDMTabConversion: Manually triggering DM tab conversion");
            DebugLogTabState();
            PerformTabConversion();
            DebugLogTabState();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DebugTriggerDMTabConversion: Error during manual conversion: {ex.Message}");
        }
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
            
            // Also check what HasDMTabs would return
            var dmTabsForSection = Config.Tabs.Where(tab => !tab.PopOut && tab is DMTab).ToList();
            Plugin.Log.Info($"DM tabs that should be in DM Section Window: {dmTabsForSection.Count}");
            foreach (var tab in dmTabsForSection)
            {
                if (tab is DMTab dmTab)
                {
                    Plugin.Log.Info($"  - {dmTab.Player.DisplayName} (PopOut={dmTab.PopOut})");
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
    /// Fixes PopOut settings for existing DMTabs to ensure they appear in the DM Section Window.
    /// </summary>
    internal void FixDMTabPopOutSettings()
    {
        try
        {
            Plugin.Log.Info("FixDMTabPopOutSettings: Starting PopOut fix for existing DMTabs");
            
            var fixedCount = 0;
            for (int i = 0; i < Config.Tabs.Count; i++)
            {
                var tab = Config.Tabs[i];
                if (tab is DMTab dmTab && dmTab.PopOut)
                {
                    Plugin.Log.Info($"FixDMTabPopOutSettings: Fixing PopOut for DMTab '{dmTab.Name}' (was PopOut={dmTab.PopOut})");
                    dmTab.PopOut = false; // Set to false so it appears in DM Section Window
                    fixedCount++;
                }
            }
            
            if (fixedCount > 0)
            {
                Plugin.Log.Info($"FixDMTabPopOutSettings: Fixed PopOut setting for {fixedCount} DMTabs");
                SaveConfig();
                
                // Force refresh of DM Section Window
                if (Config.DMSectionPoppedOut && DMSectionWindow != null)
                {
                    Plugin.Log.Info("FixDMTabPopOutSettings: Forcing DM Section Window refresh");
                    DMSectionWindow.IsOpen = true;
                }
            }
            else
            {
                Plugin.Log.Info("FixDMTabPopOutSettings: No DMTabs needed PopOut fixing");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"FixDMTabPopOutSettings: Error fixing PopOut settings: {ex.Message}");
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
        
        // Safety check: Don't convert if we already have too many DM tabs
        var existingDMTabCount = Config.Tabs.Count(t => t is DMTab);
        if (existingDMTabCount > 5)
        {
            Plugin.Log.Warning($"PerformTabConversion: Already have {existingDMTabCount} DM tabs, skipping conversion to prevent spam");
            return;
        }
        
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
        var maxConversions = 3; // Limit conversions per session
        var conversionsPerformed = 0;
        
        for (int i = 0; i < Config.Tabs.Count && conversionsPerformed < maxConversions; i++)
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
                        
                        // Load message history from database (defer to avoid blocking, but serialize to prevent concurrency issues)
                        var loadHistoryTask = Task.Run(async () =>
                        {
                            try
                            {
                                // Add a small delay to prevent concurrent MessageStore access
                                await Task.Delay(100 * (i % 5)); // Stagger by 100ms intervals, max 500ms
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
                        conversionsPerformed++;
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
            
            // CRITICAL FIX: Reset LastTab to prevent scroll issues
            // When tabs are replaced, the LastTab index may become invalid or point to a different tab,
            // causing hasTabSwitched to be true inappropriately and triggering unwanted auto-scroll
            var oldLastTab = LastTab;
            LastTab = -1; // Reset to force proper tab detection on next frame
            Plugin.Log.Info($"PerformTabConversion: Reset LastTab from {oldLastTab} to -1 to prevent scroll issues");
            
            SaveConfig(); // Save the corrected configuration
            
            // Debug log the final tab state
            DebugLogTabState();
            
            // Fix PopOut settings for all DMTabs to ensure they appear in DM Section Window
            FixDMTabPopOutSettings();
            
            // Force refresh of DM Section Window if it exists
            try
            {
                if (Config.DMSectionPoppedOut && DMSectionWindow != null)
                {
                    Plugin.Log.Info("PerformTabConversion: Forcing DM Section Window refresh");
                    DMSectionWindow.IsOpen = true; // Ensure it's marked as open
                    
                    // CRITICAL FIX: Ensure the DM Section Window is properly registered in WindowSystem
                    if (WindowSystem != null && !WindowSystem.Windows.Contains(DMSectionWindow))
                    {
                        WindowSystem.AddWindow(DMSectionWindow);
                        Plugin.Log.Info("PerformTabConversion: Added DM Section Window to WindowSystem after tab conversion");
                    }
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
            
            // Even if no conversion happened, fix existing DMTab PopOut settings
            FixDMTabPopOutSettings();
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
        
        // SPECIAL CASE: If the tab name contains "@", it's definitely a DM tab (PlayerName@WorldName format)
        if (tab.Name.Contains('@'))
        {
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' contains '@' - definitely a DM tab");
            return true;
        }
        
        // BLACKLIST: Never convert these tabs regardless of content
        var blacklistedTabs = new[] { 
            "General", "Battle", "Event", "Say", "Shout", "Tell", "Party", "Alliance", 
            "FC", "LS", "CWLS", "Novice Network", "Custom", "Geral", "Chats", "Fight", 
            "MB" // Removed "Ruyu Hayer" from blacklist
        };
        
        if (blacklistedTabs.Any(blacklisted => tab.Name.Equals(blacklisted, StringComparison.OrdinalIgnoreCase)))
        {
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' is blacklisted, skipping conversion");
            return false;
        }
        
        // Log all chat codes for this tab
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has {tab.ChatCodes.Count} chat codes:");
        foreach (var chatCode in tab.ChatCodes)
        {
            Plugin.Log.Info($"  - {chatCode.Key}: {chatCode.Value}");
        }
        
        // Check if the name doesn't match common tab names
        var isNotCommonTabName = !blacklistedTabs.Any(common => tab.Name.Contains(common, StringComparison.OrdinalIgnoreCase));
        
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' is not common tab name: {isNotCommonTabName}");
        
        // Check if the name looks like a player name (contains letters and possibly spaces, but not special symbols)
        // Allow @ symbol for world names like "PlayerName@WorldName"
        var looksLikePlayerName = !string.IsNullOrEmpty(tab.Name) && 
                                 tab.Name.All(c => char.IsLetter(c) || char.IsWhiteSpace(c) || c == '\'' || c == '-' || c == '@') &&
                                 tab.Name.Any(char.IsLetter) &&
                                 tab.Name.Length >= 3 && tab.Name.Length <= 30; // Allow longer names for "Name@World" format
        
        Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' looks like player name: {looksLikePlayerName}");
        
        // CONSERVATIVE APPROACH: Only convert if we have strong evidence
        try
        {
            using var messages = tab.Messages.GetReadOnly(100); // 100ms timeout
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has {messages.Count} messages");
            
            // Must have tell-related chat codes
            var hasTellCodes = tab.ChatCodes.ContainsKey(ChatType.TellIncoming) || 
                              tab.ChatCodes.ContainsKey(ChatType.TellOutgoing);
            
            // Must have tell content and ONLY tell content (very strict)
            var tellMessageCount = 0;
            var totalMessageCount = messages.Count;
            var hasOnlyTellContent = totalMessageCount > 0; // Start with true if there are messages
            
            foreach (var message in messages)
            {
                if (message.Code.Type == ChatType.TellIncoming || message.Code.Type == ChatType.TellOutgoing)
                {
                    tellMessageCount++;
                }
                else
                {
                    hasOnlyTellContent = false; // Found non-tell message
                }
            }
            
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has tell codes: {hasTellCodes}");
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has {tellMessageCount}/{totalMessageCount} tell messages");
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' has only tell content: {hasOnlyTellContent}");
            
            // VERY STRICT CRITERIA: 
            // 1. Must look like a player name
            // 2. Must not be blacklisted
            // 3. Must have tell codes OR (have messages AND all messages are tells)
            // 4. Must have at least some tell content
            var hasStrongTellEvidence = hasTellCodes || (totalMessageCount > 0 && hasOnlyTellContent && tellMessageCount > 0);
            var hasMinimumTellContent = tellMessageCount >= 1; // At least 1 tell message
            
            var result = isNotCommonTabName && looksLikePlayerName && hasStrongTellEvidence && hasMinimumTellContent;
            
            Plugin.Log.Info($"IsLikelyDMTabByPattern: Tab '{tab.Name}' analysis:");
            Plugin.Log.Info($"  - isNotCommonTabName: {isNotCommonTabName}");
            Plugin.Log.Info($"  - looksLikePlayerName: {looksLikePlayerName}");
            Plugin.Log.Info($"  - hasStrongTellEvidence: {hasStrongTellEvidence}");
            Plugin.Log.Info($"  - hasMinimumTellContent: {hasMinimumTellContent}");
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
    /// to find the correct world ID and ContentId for the player.
    /// </summary>
    private DMPlayer? TryCreateDMPlayerFromTab(Tab tab)
    {
        if (string.IsNullOrEmpty(tab.Name))
            return null;
            
        try
        {
            // SPECIAL CASE: If tab name contains "@", extract player name and world name directly
            if (tab.Name.Contains('@'))
            {
                var parts = tab.Name.Split('@');
                if (parts.Length == 2)
                {
                    var playerName = parts[0].Trim();
                    var worldName = parts[1].Trim();
                    
                    Plugin.Log.Info($"TryCreateDMPlayerFromTab: Extracting from '@' format - Player: '{playerName}', World: '{worldName}'");
                    
                    var extractedWorldId = FindWorldIdByName(worldName);
                    if (extractedWorldId != null)
                    {
                        Plugin.Log.Info($"TryCreateDMPlayerFromTab: Successfully created DMPlayer from '@' format: {playerName}@{worldName} (world {extractedWorldId})");
                        return new DMPlayer(playerName, extractedWorldId.Value, 0);
                    }
                    else
                    {
                        Plugin.Log.Warning($"TryCreateDMPlayerFromTab: Could not find world ID for '{worldName}', using fallback");
                        // Fall through to database lookup with just the player name
                    }
                }
            }
            
            // Extract just the player name (remove world if present)
            var nameToSearch = tab.Name;
            if (nameToSearch.Contains('@'))
            {
                nameToSearch = nameToSearch.Split('@')[0].Trim();
            }
            
            // PRIORITY 1: Try to find the player using ContentId from database (most reliable)
            var dmPlayerFromContentId = FindPlayerFromDatabaseByContentId(nameToSearch);
            if (dmPlayerFromContentId != null)
            {
                Plugin.Log.Info($"Found player '{nameToSearch}' using ContentId: {dmPlayerFromContentId.DisplayName} (ContentId: {dmPlayerFromContentId.ContentId})");
                return dmPlayerFromContentId;
            }
            
            // PRIORITY 2: Fallback to world ID lookup from database
            var worldId = FindPlayerWorldFromDatabase(nameToSearch);
            
            // SPECIAL CASE: Manual overrides for known players with wrong database data
            var manualOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Ruyu Hayer", "Behemoth" },
                // Add more manual overrides here if needed
                // { "PlayerName", "WorldName" },
            };
            
            if (manualOverrides.TryGetValue(nameToSearch, out var overrideWorldName))
            {
                var overrideWorldId = FindWorldIdByName(overrideWorldName);
                if (overrideWorldId != null)
                {
                    Plugin.Log.Info($"Manual override: Setting {nameToSearch} to {overrideWorldName} (world {overrideWorldId}) instead of detected world {worldId}");
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
                    Plugin.Log.Warning($"Cannot create DMPlayer for '{nameToSearch}' - no valid player or world available");
                    return null;
                }
                
                worldId = ClientState.LocalPlayer.HomeWorld.RowId;
                Plugin.Log.Warning($"Could not find world for '{nameToSearch}' in message history, using current player's world {worldId} as fallback");
            }
            else
            {
                Plugin.Log.Info($"Found world {worldId} for '{nameToSearch}' from message history");
            }
            
            // Create DMPlayer without ContentId (will be 0) - ContentId will be populated when new messages arrive
            return new DMPlayer(nameToSearch, worldId.Value, 0);
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
            var incomingWorlds = new List<(uint worldId, DateTimeOffset date, string source, ulong contentId)>();
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
                            incomingWorlds.Add((worldId.Value, message.Date, "incoming tell", message.ContentId));
                            Plugin.Log.Debug($"Found world {worldId} for player '{playerName}' from incoming tell dated {message.Date} (ContentId: {message.ContentId})");
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
                Plugin.Log.Info($"Selected world {mostRecentIncoming.worldId} for player '{playerName}' from {mostRecentIncoming.source} (most recent incoming: {mostRecentIncoming.date}, ContentId: {mostRecentIncoming.contentId})");
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
    /// Finds a DMPlayer from the database using ContentId for more reliable identification.
    /// </summary>
    private DMPlayer? FindPlayerFromDatabaseByContentId(string playerName)
    {
        try
        {
            Plugin.Log.Debug($"Searching for player '{playerName}' using ContentId from database");
            
            // Get recent messages (last 30 days should be enough)
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            using var messageEnumerator = MessageManager.Store.GetMostRecentMessages(since: since, count: 20000);
            
            // Look for the most recent incoming tell from this player
            foreach (var message in messageEnumerator)
            {
                if (message.Code.Type != ChatType.TellIncoming)
                    continue;
                
                var senderName = ExtractPlayerNameFromChunks(message.Sender);
                if (!string.IsNullOrEmpty(senderName) && senderName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                {
                    var worldId = ExtractWorldIdFromContentId(message.ContentId);
                    if (worldId != null && message.ContentId != 0)
                    {
                        Plugin.Log.Info($"Found player '{playerName}' with ContentId {message.ContentId} and world {worldId}");
                        return new DMPlayer(playerName, worldId.Value, message.ContentId);
                    }
                }
            }
            
            Plugin.Log.Debug($"No ContentId-based match found for player '{playerName}'");
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error searching for player '{playerName}' by ContentId: {ex.Message}");
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
            Plugin.Log.Info("FrameworkUpdate: Triggering DM tab conversion");
            FixDeserializedDMTabs();
        }

        if (!Config.HideChat)
            return;

        foreach (var name in ChatAddonNames)
            if (GameFunctions.GameFunctions.IsAddonInteractable(name))
                GameFunctions.GameFunctions.SetAddonInteractable(name, false);
    }

    public static bool InBattle => Condition[ConditionFlag.InCombat];

    /// <summary>
    /// Command handler for restoring DM windows after plugin reload.
    /// </summary>
    private void RestoreDMWindowsCommand(string command, string arguments)
    {
        try
        {
            Log.Info("RestoreDMWindowsCommand: Manually restoring DM windows");
            DMManager.Instance.RestoreExistingDMWindows();
            Log.Info("RestoreDMWindowsCommand: DM window restoration completed");
        }
        catch (Exception ex)
        {
            Log.Error($"RestoreDMWindowsCommand: Error restoring DM windows: {ex.Message}");
        }
    }

    public static bool GposeActive => Condition[ConditionFlag.WatchingCutscene];
    public static bool CutsceneActive => Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
}
