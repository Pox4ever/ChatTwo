using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ChatTwo.Code;
using ChatTwo.DM;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace ChatTwo.Ui;

public sealed class ChatLogWindow : Window
{
    private const string ChatChannelPicker = "chat-channel-picker";
    private const string AutoCompleteId = "##chat2-autocomplete";

    private const ImGuiInputTextFlags InputFlags = ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCharFilter |
                                                   ImGuiInputTextFlags.CallbackCompletion | ImGuiInputTextFlags.CallbackHistory;

    internal Plugin Plugin { get; }

    internal bool ScreenshotMode;
    private string Salt { get; }

    internal Vector4 DefaultText { get; set; }

    internal bool FocusedPreview;
    internal bool Activate;
    internal bool InputFocused { get; private set; }
    private int ActivatePos = -1;
    internal string Chat = string.Empty;
    private readonly List<string> InputBacklog = [];
    private int InputBacklogIdx = -1;
    public bool TellSpecial;
    private readonly Stopwatch LastResize = new();
    private AutoCompleteInfo? AutoCompleteInfo;
    private bool AutoCompleteOpen;
    private List<AutoTranslateEntry>? AutoCompleteList;
    private bool FixCursor;
    private int AutoCompleteSelection;
    private bool AutoCompleteShouldScroll;
    
    // UI transparency state
    private float CurrentUIAlpha = 1.0f;

    // Used to detect channel changes for the webinterface
    public Chunk[] PreviousChannel = [];

    public int CursorPos;

    public Vector2 LastWindowPos { get; private set; } = Vector2.Zero;
    public Vector2 LastWindowSize { get; private set; } = Vector2.Zero;

    public unsafe ImGuiViewport* LastViewport;
    private bool WasDocked;

    public PayloadHandler PayloadHandler { get; }
    internal Lender<PayloadHandler> HandlerLender { get; }
    internal Dictionary<string, ChatType> TextCommandChannels { get; } = new();
    private HashSet<string> AllCommands { get; } = [];

    private const uint ChatOpenSfx = 35u;
    private const uint ChatCloseSfx = 3u;
    private bool PlayedClosingSound = true;
    private bool DrewThisFrame;

    private long FrameTime; // set every frame
    internal long LastActivityTime = Environment.TickCount64;

    internal ChatLogWindow(Plugin plugin) : base($"{Plugin.PluginName}###chat2")
    {
        Plugin = plugin;
        Salt = new Random().Next().ToString();

        Size = new Vector2(500, 250);
        SizeCondition = ImGuiCond.FirstUseEver;

        PositionCondition = ImGuiCond.Always;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        PayloadHandler = new PayloadHandler(this);
        HandlerLender = new Lender<PayloadHandler>(() => new PayloadHandler(this));

        SetUpTextCommandChannels();
        SetUpAllCommands();

        Plugin.Commands.Register("/clearlog2", "Clear the Chat 2 chat log").Execute += ClearLog;
        Plugin.Commands.Register("/chat2").Execute += ToggleChat;

        Plugin.ClientState.Login += Login;
        Plugin.ClientState.Logout += Logout;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "ItemDetail", PayloadHandler.MoveTooltip);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "ActionDetail", PayloadHandler.MoveTooltip);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "ItemDetail", PayloadHandler.MoveTooltip);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "ActionDetail", PayloadHandler.MoveTooltip);
        Plugin.ClientState.Logout -= Logout;
        Plugin.ClientState.Login -= Login;
        Plugin.Commands.Register("/chat2").Execute -= ToggleChat;
        Plugin.Commands.Register("/clearlog2").Execute -= ClearLog;
    }

    private void Logout(int _, int __)
    {
        Plugin.MessageManager.ClearAllTabs();
    }

    private void Login()
    {
        Plugin.MessageManager.FilterAllTabsAsync();
    }

    internal unsafe void Activated(ChatActivatedArgs args)
    {
        TellSpecial = args.TellSpecial;

        Activate = true;
        PlayedClosingSound = false;
        if (Plugin.Config.PlaySounds)
            UIGlobals.PlaySoundEffect(ChatOpenSfx);

        // Don't set the channel or text content when activating a disabled tab.
        if (Plugin.CurrentTab.InputDisabled)
        {
            // The closing sound would've been immediately played in this case.
            PlayedClosingSound = true;
            return;
        }

        if (args.AddIfNotPresent != null && !Chat.Contains(args.AddIfNotPresent))
            Chat += args.AddIfNotPresent;

        if (args.Input != null)
            Chat += args.Input;

        var (info, reason, target) = (args.ChannelSwitchInfo, args.TellReason, args.TellTarget);

        if (info.Channel != null)
        {
            var targetChannel = info.Channel;
            if (info.Channel is InputChannel.Tell)
            {
                if (info.Rotate != RotateMode.None)
                {
                    var idx = Plugin.CurrentTab.CurrentChannel.TempChannel != InputChannel.Tell
                        ? 0 : info.Rotate == RotateMode.Reverse
                            ? -1 : 1;

                    var tellInfo = Plugin.Functions.Chat.GetTellHistoryInfo(idx);
                    if (tellInfo != null && reason != null)
                        Plugin.CurrentTab.CurrentChannel.TempTellTarget = new TellTarget(tellInfo.Name, (ushort) tellInfo.World, tellInfo.ContentId, reason.Value);
                }
                else
                {
                    Plugin.CurrentTab.CurrentChannel.TellTarget = null;
                    if (target != null)
                    {
                        if (info.Permanent)
                        {
                            Plugin.CurrentTab.CurrentChannel.TellTarget = target;
                        }
                        else
                        {
                            Plugin.CurrentTab.CurrentChannel.UseTempChannel = true;
                            Plugin.CurrentTab.CurrentChannel.TempTellTarget = target;
                        }
                    }
                }
            }
            else
            {
                Plugin.CurrentTab.CurrentChannel.TellTarget = null;
            }

            if (info.Channel is InputChannel.Linkshell1 or InputChannel.CrossLinkshell1 && info.Rotate != RotateMode.None)
            {
                var module = UIModule.Instance();

                // If any of these operations fail, do nothing.
                if (info.Permanent)
                {
                    // Rotate using the game's code.
                    if (info.Channel == InputChannel.Linkshell1)
                    {
                        GameFunctions.Chat.RotateLinkshellHistory(info.Rotate);
                        targetChannel = info.Channel + (uint)module->LinkshellCycle;
                    }
                    else
                    {
                        GameFunctions.Chat.RotateCrossLinkshellHistory(info.Rotate);
                        targetChannel = info.Channel + (uint)module->CrossWorldLinkshellCycle;
                    }
                }
                else
                {
                    targetChannel = GameFunctions.Chat.ResolveTempInputChannel(Plugin.CurrentTab.CurrentChannel.TempChannel, info.Channel.Value, info.Rotate);
                }
            }

            if (targetChannel == null || !GameFunctions.Chat.ValidAnyLinkshell(targetChannel.Value))
            {
                Plugin.Log.Warning($"Channel was set to an invalid value '{targetChannel}', ignoring");
                return;
            }

            if (info.Permanent)
            {
                SetChannel(targetChannel);
            }
            else
            {
                Plugin.CurrentTab.CurrentChannel.UseTempChannel = true;
                Plugin.CurrentTab.CurrentChannel.TempChannel = targetChannel.Value;
            }
        }

        if (info.Text != null && Chat.Length == 0)
            Chat = info.Text;
    }

    internal bool IsValidCommand(string command)
    {
        return Plugin.CommandManager.Commands.ContainsKey(command) || AllCommands.Contains(command);
    }

    private void ClearLog(string command, string arguments)
    {
        switch (arguments)
        {
            case "all":
                Plugin.MessageManager.ClearAllTabs();
                break;
            case "help":
                Plugin.ChatGui.Print("- /clearlog2: clears the active tab's log");
                Plugin.ChatGui.Print("- /clearlog2 all: clears all tabs' logs and the global history");
                Plugin.ChatGui.Print("- /clearlog2 help: shows this help");
                break;
            default:
                if (Plugin.LastTab > -1 && Plugin.LastTab < Plugin.Config.Tabs.Count)
                    Plugin.Config.Tabs[Plugin.LastTab].Clear();
                break;
        }
    }

    private void ToggleChat(string _, string arguments)
    {
        switch (arguments)
        {
            case "hide":
                CurrentHideState = HideState.User;
                break;
            case "show":
                CurrentHideState = HideState.None;
                break;
            case "toggle":
                CurrentHideState = CurrentHideState switch
                {
                    HideState.User or HideState.CutsceneOverride => HideState.None,
                    HideState.Cutscene => HideState.CutsceneOverride,
                    HideState.None => HideState.User,
                    _ => CurrentHideState,
                };
                break;
        }
    }

    private void SetUpTextCommandChannels()
    {
        TextCommandChannels.Clear();

        foreach (var input in Enum.GetValues<InputChannel>())
        {
            var commands = input.TextCommands(Plugin.DataManager);
            if (commands == null)
                continue;

            var type = input.ToChatType();
            foreach (var command in commands)
                AddTextCommandChannel(command, type);
        }

        if (Sheets.TextCommandSheet.HasRow(116))
        {
            var echo = Sheets.TextCommandSheet.GetRow(116);
            AddTextCommandChannel(echo, ChatType.Echo);
        }
    }

    private void AddTextCommandChannel(TextCommand command, ChatType type)
    {
        TextCommandChannels[command.Command.ExtractText()] = type;
        TextCommandChannels[command.ShortCommand.ExtractText()] = type;
        TextCommandChannels[command.Alias.ExtractText()] = type;
        TextCommandChannels[command.ShortAlias.ExtractText()] = type;
    }

    private void SetUpAllCommands()
    {
        if (Plugin.DataManager.GetExcelSheet<TextCommand>() is not { } commands)
            return;

        var commandNames = commands.SelectMany(cmd => new[]
        {
            cmd.Command.ExtractText(),
            cmd.ShortCommand.ExtractText(),
            cmd.Alias.ExtractText(),
            cmd.ShortAlias.ExtractText(),
        });

        foreach (var command in commandNames)
            AllCommands.Add(command);
    }

    private void AddBacklog(string message)
    {
        for (var i = 0; i < InputBacklog.Count; i++)
        {
            if (InputBacklog[i] != message)
                continue;

            InputBacklog.RemoveAt(i);
            break;
        }

        InputBacklog.Add(message);
    }

    private float GetRemainingHeightForMessageLog()
    {
        var lineHeight = ImGui.CalcTextSize("A").Y;
        var height = ImGui.GetContentRegionAvail().Y - lineHeight * 2 - ImGui.GetStyle().ItemSpacing.Y - ImGui.GetStyle().FramePadding.Y * 2;

        if (Plugin.Config.PreviewPosition is PreviewPosition.Inside)
            height -= Plugin.InputPreview.PreviewHeight;

        return height;
    }

    internal void ChangeTab(int index) {
        Plugin.WantedTab = index;
        LastActivityTime = FrameTime;
    }

    internal void ChangeTabDelta(int offset)
    {
        var newIndex = (Plugin.LastTab + offset) % Plugin.Config.Tabs.Count;
        while (newIndex < 0)
            newIndex += Plugin.Config.Tabs.Count;
        ChangeTab(newIndex);
    }

    private void TabSwitched(Tab newTab, Tab previousTab)
    {
        // Use the fixed channel if set by the user, or set it to the current tabs channel if this tab wasn't accessed before
        if (newTab.Channel is not null)
            newTab.CurrentChannel.Channel = newTab.Channel.Value;
        else if (newTab.CurrentChannel.Channel is InputChannel.Invalid)
            newTab.CurrentChannel = previousTab.CurrentChannel;

        SetChannel(newTab.CurrentChannel.Channel);

        // Inform the webinterface about tab switch
        // TODO implement tabs in the webinterface
        Plugin.ServerCore.SendNewLogin();
    }

    private enum HideState
    {
        None,
        Cutscene,
        CutsceneOverride,
        User,
        Battle
    }

    private HideState CurrentHideState = HideState.None;

    public bool IsHidden;
    public void HideStateCheck()
    {
        // if the chat has no hide state set, and the player has entered battle, we hide chat if they have configured it
        if (Plugin.Config.HideInBattle && CurrentHideState == HideState.None && Plugin.InBattle)
            CurrentHideState = HideState.Battle;

        // If the chat is hidden because of battle, we reset it here
        if (CurrentHideState is HideState.Battle && !Plugin.InBattle)
            CurrentHideState = HideState.None;

        // if the chat has no hide state and in a cutscene, set the hide state to cutscene
        if (Plugin.Config.HideDuringCutscenes && CurrentHideState == HideState.None && (Plugin.CutsceneActive || Plugin.GposeActive))
        {
            if (Plugin.Functions.Chat.CheckHideFlags())
                CurrentHideState = HideState.Cutscene;
        }

        // if the chat is hidden because of a cutscene and no longer in a cutscene, set the hide state to none
        if (CurrentHideState is HideState.Cutscene or HideState.CutsceneOverride && !Plugin.CutsceneActive && !Plugin.GposeActive)
            CurrentHideState = HideState.None;

        // if the chat is hidden because of a cutscene and the chat has been activated, show chat
        if (CurrentHideState == HideState.Cutscene && Activate)
            CurrentHideState = HideState.CutsceneOverride;

        // if the user hid the chat and is now activating chat, reset the hide state
        if (CurrentHideState == HideState.User && Activate)
            CurrentHideState = HideState.None;

        if (CurrentHideState is HideState.Cutscene or HideState.User or HideState.Battle || (Plugin.Config.HideWhenNotLoggedIn && !Plugin.ClientState.IsLoggedIn))
        {
            IsHidden = true;
            return;
        }

        IsHidden = false;
    }

    internal void BeginFrame()
    {
        DrewThisFrame = false;
    }

    internal void FinalizeFrame()
    {
        if (!DrewThisFrame)
            InputFocused = false;
    }

    public override unsafe void PreOpenCheck()
    {
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing;
        
        if (!Plugin.Config.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;

        if (!Plugin.Config.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;

        if (!Plugin.Config.ShowTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;

        if (LastViewport == ImGuiHelpers.MainViewport.Handle && !WasDocked)
        {
            // BgAlpha is now set in Draw() method for proper focus-based transparency
            // Don't set it here as it would override the focus-based transparency
        }

        LastViewport = ImGui.GetWindowViewport().Handle;
        WasDocked = ImGui.IsWindowDocked();
    }

    public override bool DrawConditions()
    {
        FrameTime = Environment.TickCount64;
        if (IsHidden)
            return false;

        if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) || Activate)
        {
            LastActivityTime = FrameTime;
            return true;
        }

        var currentTab = Plugin.CurrentTab; // local to avoid calling the getter repeatedly
        var lastActivityTime = Plugin.Config.Tabs
            .Where(tab => !tab.PopOut && (tab.UnhideOnActivity || tab == currentTab))
            .Select(tab => tab.LastActivity)
            .Append(LastActivityTime)
            .Max();
        return FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
    }

    public override void PreDraw()
    {
        // Apply existing style first
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();
            
        // Apply modern styling using ImRaii for proper scoping
        ModernUI.BeginModernStyle(Plugin.Config);
    }

    public override void PostDraw()
    {
        // Set Activate to false after draw to avoid repeatedly trying to focus
        // the text input in a tab with input disabled. The usual way that
        // Activate gets disabled is via the text input callback, but that
        // doesn't get called if the input is disabled.
        if (Plugin.CurrentTab.InputDisabled)
            Activate = false;

        // End modern styling
        ModernUI.EndModernStyle();

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();
    }

    public override void OnClose()
    {
        // We force the main log to be always open
        IsOpen = true;
    }

    public override void Draw()
    {
        DrewThisFrame = true;
        
        // Update focus state and apply transparency
        var isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var alpha = Plugin.Config.WindowAlpha / 100f;
        
        if (!isWindowFocused)
        {
            var transparencyFactor = Plugin.Config.UnfocusedTransparency / 100f;
            BgAlpha = alpha * transparencyFactor;
        }
        else
        {
            BgAlpha = alpha;
        }
        
        // Apply transparency to UI elements based on focus state
        var uiAlpha = isWindowFocused ? 1.0f : (Plugin.Config.UnfocusedTransparency / 100f);
        CurrentUIAlpha = uiAlpha; // Store for use in nested methods
        
        // Push style colors for UI elements with transparency
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.Text) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var buttonColor = ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.Button) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var buttonHoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.ButtonHovered) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var buttonActiveColor = ImRaii.PushColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.ButtonActive) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var frameColor = ImRaii.PushColor(ImGuiCol.FrameBg, ImGui.GetColorU32(ImGuiCol.FrameBg) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var frameHoveredColor = ImRaii.PushColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var frameActiveColor = ImRaii.PushColor(ImGuiCol.FrameBgActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var tabColor = ImRaii.PushColor(ImGuiCol.Tab, ImGui.GetColorU32(ImGuiCol.Tab) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var tabHoveredColor = ImRaii.PushColor(ImGuiCol.TabHovered, ImGui.GetColorU32(ImGuiCol.TabHovered) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var tabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, ImGui.GetColorU32(ImGuiCol.TabActive) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        using var separatorColor = ImRaii.PushColor(ImGuiCol.Separator, ImGui.GetColorU32(ImGuiCol.Separator) & 0x00FFFFFF | ((uint)(255 * uiAlpha) << 24));
        
        try
        {
            DrawChatLog();
            AddPopOutsToDraw();
            DrawAutoComplete();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing Chat Log window");
            // Prevent recurring draw failures from constantly trying to grab
            // input focus, which breaks every other ImGui window.
            Activate = false;
        }
    }

    private static bool IsChatMode => Plugin.Config.PreviewPosition is PreviewPosition.Inside or PreviewPosition.Tooltip;
    
    private unsafe void DrawChatLog()
    {
        // Position change has applied, so we set it to null again
        Position = null;

        var currentSize = ImGui.GetWindowSize();
        var resized = LastWindowSize != currentSize;
        LastWindowSize = currentSize;
        LastWindowPos = ImGui.GetWindowPos();

        if (resized)
            LastResize.Restart();

        LastViewport = ImGui.GetWindowViewport().Handle;
        WasDocked = ImGui.IsWindowDocked();

        if (IsChatMode && Plugin.InputPreview.IsDrawable)
            Plugin.InputPreview.CalculatePreview();

        // Always use the original single-row tab system
        // DM tabs are handled via the popped-out DM section window
        DrawNormalChatPane();
        
        // CRITICAL FIX: Draw popups at parent window level to avoid child window conflicts
        // This ensures popups work correctly even when multiple child windows are present
        PayloadHandler.Draw();
    }
    
    /// <summary>
    /// Checks if a tab is likely a DM tab based on its name pattern.
    /// This is used as a fallback when DM tabs are deserialized as regular Tab objects.
    /// </summary>
    private bool IsLikelyDMTab(Tab tab)
    {
        // First check if DMManager recognizes this as a DM player name
        try
        {
            if (DMManager.Instance.IsKnownDMPlayer(tab.Name))
                return true;
        }
        catch
        {
            // If DMManager isn't ready, fall back to pattern matching
        }
        
        // Pattern-based detection as fallback
        // Check if the tab has only Tell chat codes (typical for DM tabs)
        var hasTellCodes = tab.ChatCodes.ContainsKey(ChatType.TellIncoming) || 
                          tab.ChatCodes.ContainsKey(ChatType.TellOutgoing);
        
        // Check if it has very few chat codes (DM tabs typically only have Tell codes)
        var hasLimitedChatCodes = tab.ChatCodes.Count <= 3; // Tell incoming, outgoing, and maybe one more
        
        // Check if the name doesn't match common tab names
        var commonTabNames = new[] { "General", "Battle", "Event", "Say", "Shout", "Tell", "Party", "Alliance", "FC", "LS", "CWLS", "Novice Network", "Custom" };
        var isNotCommonTabName = !commonTabNames.Any(common => tab.Name.Contains(common, StringComparison.OrdinalIgnoreCase));
        
        // Check if the name looks like a player name (contains letters and possibly spaces, but not special symbols)
        var looksLikePlayerName = !string.IsNullOrEmpty(tab.Name) && 
                                 tab.Name.All(c => char.IsLetter(c) || char.IsWhiteSpace(c) || c == '\'' || c == '-') &&
                                 tab.Name.Any(char.IsLetter);
        
        return hasTellCodes && hasLimitedChatCodes && isNotCommonTabName && looksLikePlayerName;
    }

    private unsafe void DrawNormalChatPane()
    {
        if (Plugin.Config.SidebarTabView)
            DrawTabSidebar();
        else
            DrawTabBar();

        var activeTab = Plugin.CurrentTab;

        // This tab has a fixed channel, so we force this channel to be always set as current
        if (activeTab.Channel is not null)
            activeTab.CurrentChannel.SetChannel(activeTab.Channel.Value);

        if (Plugin.Config.PreviewPosition is PreviewPosition.Inside && Plugin.InputPreview.IsDrawable)
            Plugin.InputPreview.DrawPreview();

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        {
            DrawChannelName(activeTab);
        }

        // Hide channel selector for DM tabs - they always send tells
        var isDMTab = activeTab is DMTab;
        
        var beforeIcon = ImGui.GetCursorPos();
        if (!isDMTab)
        {
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Comment) && activeTab.Channel is null)
                    ImGui.OpenPopup(ChatChannelPicker);
            }

            if (activeTab.Channel is not null && ImGui.IsItemHovered())
                ModernUI.DrawModernTooltip(Language.ChatLog_SwitcherDisabled, Plugin.Config);

            using (var popup = ImRaii.Popup(ChatChannelPicker))
            {
                if (popup)
                {
                    var channels = GetValidChannels();
                    foreach (var (name, channel) in channels)
                        if (ImGui.Selectable(name))
                            SetChannel(channel);
                }
            }

            ImGui.SameLine();
        }
        var afterIcon = ImGui.GetCursorPos();

        var buttonWidth = isDMTab ? 0 : afterIcon.X - beforeIcon.X;
        var showNovice = Plugin.Config.ShowNoviceNetwork && GameFunctions.GameFunctions.IsMentor();
        var buttonsRight = (showNovice ? 1 : 0) + (Plugin.Config.ShowHideButton ? 1 : 0);
        var inputWidth = ImGui.GetContentRegionAvail().X - buttonWidth * (isDMTab ? 0 : (1 + buttonsRight));

        // For DM tabs, always use TellOutgoing color
        var inputType = isDMTab ? ChatType.TellOutgoing : (activeTab.CurrentChannel.UseTempChannel ? activeTab.CurrentChannel.TempChannel.ToChatType() : activeTab.CurrentChannel.Channel.ToChatType());
        var isCommand = Chat.Trim().StartsWith('/');
        if (isCommand)
        {
            var command = Chat.Split(' ')[0];
            if (TextCommandChannels.TryGetValue(command, out var channel))
                inputType = channel;

            if (!IsValidCommand(command))
                inputType = ChatType.Error;
        }

        var normalColor = ImGui.GetColorU32(ImGuiCol.Text);
        var inputColour = Plugin.Config.ChatColours.TryGetValue(inputType, out var inputCol) ? inputCol : inputType.DefaultColor();

        if (!isCommand && Plugin.ExtraChat.ChannelOverride is var (_, overrideColour))
            inputColour = overrideColour;

        if (isCommand && Plugin.ExtraChat.ChannelCommandColours.TryGetValue(Chat.Split(' ')[0], out var ecColour))
            inputColour = ecColour;

        var push = inputColour != null;
        using (ImRaii.PushColor(ImGuiCol.Text, push ? ColourUtil.RgbaToAbgr(inputColour!.Value) : 0, push))
        {
            var isChatEnabled = activeTab is { InputDisabled: false };
            // CRITICAL FIX: Don't set keyboard focus when DM windows are active to prevent focus fights
            // This was causing the "has focus now" -> "lost focus" cycle that breaks context menus
            // EXCEPTION: Allow focus when Activate is true (from global Enter key) even with DM windows open
            var hasDMWindows = DMManager.Instance.GetOpenDMWindows().Any();
            if (isChatEnabled && (Activate || (FocusedPreview && !hasDMWindows)))
            {
                FocusedPreview = false;
                ImGui.SetKeyboardFocusHere();
            }

            var chatCopy = Chat;
            using (ImRaii.Disabled(!isChatEnabled))
            {
                var flags = InputFlags | (!isChatEnabled ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
                ImGui.SetNextItemWidth(inputWidth);
                
                // Enhanced input field with better visual feedback
                var hasError = isCommand && !IsValidCommand(Chat.Split(' ')[0]);
                var (styleScope, colorScope) = ModernUI.PushEnhancedInputStyle(Plugin.Config, InputFocused, hasError, CurrentUIAlpha);
                using (styleScope)
                using (colorScope)
                {
                    // Different placeholder for DM tabs to indicate direct messaging
                    var placeholder = isChatEnabled 
                        ? (isDMTab && activeTab is DMTab dmTab ? $"Message {dmTab.Player.TabName}..." : "Type your message...") 
                        : Language.ChatLog_DisabledInput;
                    ImGui.InputTextWithHint("##chat2-input", placeholder, ref Chat, 500, flags, Callback);
                }
            }
            var inputActive = ImGui.IsItemActive();
            InputFocused = isChatEnabled && inputActive;

            // Draw typing indicator with proper positioning to avoid cutoff
            if (isChatEnabled && !string.IsNullOrEmpty(Chat))
            {
                // Position typing indicator below the input field with proper spacing
                var currentPos = ImGui.GetCursorPos();
                ImGui.SetCursorPos(currentPos + new Vector2(8, 2)); // Offset from left edge, small gap
                ModernUI.DrawTypingIndicator(true, Plugin.Config);
                ImGui.SetCursorPos(currentPos); // Reset cursor position
            }

            var tooltipDraw = Plugin.Config.PreviewPosition is PreviewPosition.Tooltip && Plugin.InputPreview.IsDrawable;
            if (tooltipDraw && ImGui.IsItemHovered())
            {
                ImGui.SetNextWindowSize(new Vector2(500 * ImGuiHelpers.GlobalScale, -1));
                using var tooltip = ImRaii.Tooltip();
                if (tooltip)
                    Plugin.InputPreview.DrawPreview();
            }

            if (ImGui.IsItemDeactivated())
            {
                if (ImGui.IsKeyDown(ImGuiKey.Escape))
                {
                    Chat = chatCopy;

                    if (activeTab.CurrentChannel.UseTempChannel)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        SetChannel(activeTab.CurrentChannel.Channel);
                    }
                }

                if (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter))
                {
                    Plugin.CommandHelpWindow.IsOpen = false;
                    SendChatBox(activeTab);

                    if (activeTab.CurrentChannel.UseTempChannel)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        SetChannel(activeTab.CurrentChannel.Channel);
                    }
                }
            }

            // Process keybinds that have modifiers while the chat is focused.
            if (inputActive)
            {
                Plugin.Functions.KeybindManager.HandleKeybinds(KeyboardSource.ImGui, true, true);
                LastActivityTime = FrameTime;
            }

            // Only trigger unfocused if we are currently not calling the auto complete
            if (!Activate && !inputActive && AutoCompleteInfo == null)
            {
                if (Plugin.Config.PlaySounds && !PlayedClosingSound)
                {
                    PlayedClosingSound = true;
                    UIGlobals.PlaySoundEffect(ChatCloseSfx);
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                {
                    activeTab.CurrentChannel.ResetTempChannel();
                    SetChannel(Plugin.CurrentTab.CurrentChannel.Channel);
                }
            }

            using (var context = ImRaii.ContextPopupItem("ChatInputContext"))
            {
                if (context)
                {
                    using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, normalColor);
                    if (ImGui.Selectable(Language.ChatLog_HideChat))
                        UserHide();
                }
            }
        }

        ImGui.SameLine();

        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Cog, width: (int)(buttonWidth * 1.1f)))
                Plugin.SettingsWindow.Toggle();
        }

        if (Plugin.Config.ShowHideButton)
        {
            ImGui.SameLine();
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.EyeSlash, width: (int)(buttonWidth * 1.1f)))
                    UserHide();
            }
        }

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            LastActivityTime = FrameTime;

        if (!showNovice)
            return;

        ImGui.SameLine();

        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Leaf))
                GameFunctions.GameFunctions.ClickNoviceNetworkButton();
        }
    }

    private void DrawRegularChatInput(Tab activeTab)
    {
        var beforeIcon = ImGui.GetCursorPos();
        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Comment) && activeTab.Channel is null)
                ImGui.OpenPopup(ChatChannelPicker);
        }

        if (activeTab.Channel is not null && ImGui.IsItemHovered())
            ModernUI.DrawModernTooltip(Language.ChatLog_SwitcherDisabled, Plugin.Config);

        using (var popup = ImRaii.Popup(ChatChannelPicker))
        {
            if (popup)
            {
                var channels = GetValidChannels();
                foreach (var (name, channel) in channels)
                    if (ImGui.Selectable(name))
                        SetChannel(channel);
            }
        }

        ImGui.SameLine();
        var afterIcon = ImGui.GetCursorPos();

        var buttonWidth = afterIcon.X - beforeIcon.X;
        var showNovice = Plugin.Config.ShowNoviceNetwork && GameFunctions.GameFunctions.IsMentor();
        var buttonsRight = (showNovice ? 1 : 0) + (Plugin.Config.ShowHideButton ? 1 : 0);
        var inputWidth = ImGui.GetContentRegionAvail().X - buttonWidth * (1 + buttonsRight);

        var inputType = activeTab.CurrentChannel.UseTempChannel ? activeTab.CurrentChannel.TempChannel.ToChatType() : activeTab.CurrentChannel.Channel.ToChatType();
        var isCommand = Chat.Trim().StartsWith('/');
        if (isCommand)
        {
            var command = Chat.Split(' ')[0];
            if (TextCommandChannels.TryGetValue(command, out var channel))
                inputType = channel;

            if (!IsValidCommand(command))
                inputType = ChatType.Error;
        }

        var normalColor = ImGui.GetColorU32(ImGuiCol.Text);
        var inputColour = Plugin.Config.ChatColours.TryGetValue(inputType, out var inputCol) ? inputCol : inputType.DefaultColor();

        if (!isCommand && Plugin.ExtraChat.ChannelOverride is var (_, overrideColour))
            inputColour = overrideColour;

        if (isCommand && Plugin.ExtraChat.ChannelCommandColours.TryGetValue(Chat.Split(' ')[0], out var ecColour))
            inputColour = ecColour;

        var push = inputColour != null;
        using (ImRaii.PushColor(ImGuiCol.Text, push ? ColourUtil.RgbaToAbgr(inputColour!.Value) : 0, push))
        {
            var isChatEnabled = activeTab is { InputDisabled: false };
            // CRITICAL FIX: Don't set keyboard focus when DM windows are active to prevent focus fights
            // This was causing the "has focus now" -> "lost focus" cycle that breaks context menus
            // EXCEPTION: Allow focus when Activate is true (from global Enter key) even with DM windows open
            var hasDMWindows = DMManager.Instance.GetOpenDMWindows().Any();
            if (isChatEnabled && (Activate || (FocusedPreview && !hasDMWindows)))
            {
                FocusedPreview = false;
                ImGui.SetKeyboardFocusHere();
            }

            using (ImRaii.Disabled(!isChatEnabled))
            {
                var flags = InputFlags | (!isChatEnabled ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
                ImGui.SetNextItemWidth(inputWidth);
                
                var hasError = isCommand && !IsValidCommand(Chat.Split(' ')[0]);
                var (styleScope, colorScope) = ModernUI.PushEnhancedInputStyle(Plugin.Config, InputFocused, hasError, CurrentUIAlpha);
                using (styleScope)
                using (colorScope)
                {
                    var placeholder = isChatEnabled ? "Type your message..." : Language.ChatLog_DisabledInput;
                    ImGui.InputTextWithHint("##regular-chat-input", placeholder, ref Chat, 500, flags, Callback);
                }
            }
            var inputActive = ImGui.IsItemActive();
            InputFocused = isChatEnabled && inputActive;

            // Handle input deactivation
            if (ImGui.IsItemDeactivated())
            {
                if (ImGui.IsKeyDown(ImGuiKey.Escape))
                {
                    Chat = "";
                    if (activeTab.CurrentChannel.UseTempChannel)
                        activeTab.CurrentChannel.ResetTempChannel();
                }

                if (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter))
                {
                    Plugin.CommandHelpWindow.IsOpen = false;
                    SendChatBox(activeTab);

                    if (activeTab.CurrentChannel.UseTempChannel)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        SetChannel(activeTab.CurrentChannel.Channel);
                    }
                }
            }

            // Process keybinds that have modifiers while the chat is focused.
            if (inputActive)
            {
                Plugin.Functions.KeybindManager.HandleKeybinds(KeyboardSource.ImGui, true, true);
                LastActivityTime = FrameTime;
            }

            // Only trigger unfocused if we are currently not calling the auto complete
            if (!Activate && !inputActive && AutoCompleteInfo == null)
            {
                if (Plugin.Config.PlaySounds && !PlayedClosingSound)
                {
                    PlayedClosingSound = true;
                    UIGlobals.PlaySoundEffect(ChatCloseSfx);
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                {
                    activeTab.CurrentChannel.ResetTempChannel();
                    SetChannel(Plugin.CurrentTab.CurrentChannel.Channel);
                }
            }

            using (var context = ImRaii.ContextPopupItem("ChatInputContext"))
            {
                if (context)
                {
                    using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, normalColor);
                    if (ImGui.Selectable(Language.ChatLog_HideChat))
                        UserHide();
                }
            }
        }

        ImGui.SameLine();

        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Cog, width: (int)buttonWidth))
                Plugin.SettingsWindow.Toggle();
        }

        if (Plugin.Config.ShowHideButton)
        {
            ImGui.SameLine();
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.EyeSlash, width: (int)buttonWidth))
                    UserHide();
            }
        }

        if (showNovice)
        {
            ImGui.SameLine();
            using (ModernUI.PushModernButtonStyle(Plugin.Config))
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Leaf))
                    GameFunctions.GameFunctions.ClickNoviceNetworkButton();
            }
        }
    }

    internal Dictionary<string, InputChannel> GetValidChannels()
    {
        var channels = new Dictionary<string, InputChannel>();
        foreach (var channel in Enum.GetValues<InputChannel>())
        {
            if (!channel.IsValid())
                continue;

            var name = Sheets.LogFilterSheet.FirstOrNull(row => row.LogKind == (byte) channel.ToChatType())?.Name.ExtractText() ?? channel.ToChatType().Name();
            if (channel.IsLinkshell())
            {
                var lsName = Plugin.Functions.Chat.GetLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            if (channel.IsCrossLinkshell())
            {
                var lsName = Plugin.Functions.Chat.GetCrossLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            // Check if the linkshell with this index is registered in
            // the ExtraChat plugin by seeing if the command is
            // registered. The command gets registered only if a
            // linkshell is assigned (and even gets unassigned if the
            // index changes!).
            if (channel.IsExtraChatLinkshell())
                if (!Plugin.CommandManager.Commands.ContainsKey(channel.Prefix()))
                    continue;

            channels.Add(name, channel);
        }

        return channels;
    }

    public void DrawChannelName(Tab activeTab)
    {
        var currentChannel = ReadChannelName(activeTab);
        if (!currentChannel.SequenceEqual(PreviousChannel))
        {
            PreviousChannel = currentChannel;
            Plugin.ServerCore?.SendChannelSwitch(currentChannel);
        }

        DrawChunks(currentChannel);
    }

    private Chunk[] ReadChannelName(Tab activeTab)
    {
        Chunk[] channelNameChunks;
        // Check the temp channel before others
        if (activeTab.CurrentChannel.UseTempChannel)
        {
            if (activeTab.CurrentChannel.TempTellTarget != null && activeTab.CurrentChannel.TempTellTarget.IsSet())
            {
                channelNameChunks = GenerateTellTargetName(activeTab.CurrentChannel.TempTellTarget);
            }
            else
            {
                string name;
                if (activeTab.CurrentChannel.TempChannel.IsLinkshell())
                {
                    var idx = (uint) activeTab.CurrentChannel.TempChannel - (uint) InputChannel.Linkshell1;
                    var lsName = Plugin.Functions.Chat.GetLinkshellName(idx);
                    name = $"LS #{idx + 1}: {lsName}";
                }
                else if (activeTab.CurrentChannel.TempChannel.IsCrossLinkshell())
                {
                    var idx = (uint) activeTab.CurrentChannel.TempChannel - (uint) InputChannel.CrossLinkshell1;
                    var cwlsName = Plugin.Functions.Chat.GetCrossLinkshellName(idx);
                    name = $"CWLS [{idx + 1}]: {cwlsName}";
                }
                else
                {
                    name = activeTab.CurrentChannel.TempChannel.ToChatType().Name();
                }

                channelNameChunks = [new TextChunk(ChunkSource.None, null, name)];
            }
        }
        else if (activeTab.CurrentChannel.TellTarget?.IsSet() == true)
        {
            channelNameChunks = GenerateTellTargetName(activeTab.CurrentChannel.TellTarget);
        }
        else if (activeTab is { Channel: { } channel })
        {
            // We cannot lookup ExtraChat channel names from index over
            // IPC so we just don't show the name if it's the tabs channel.
            //
            // We don't call channel.ToChatType().Name() as it has the
            // long name as used in the settings window.
            channelNameChunks = [new TextChunk(ChunkSource.None, null, channel.IsExtraChatLinkshell() ? $"ECLS [{channel.LinkshellIndex() + 1}]" : channel.ToChatType().Name())];
        }
        else if (Plugin.ExtraChat.ChannelOverride is var (overrideName, _))
        {
            // If the current channel is not an ExtraChat Linkshell add a warning for the user
            var warning = activeTab.CurrentChannel.Channel.IsExtraChatLinkshell()
                ? ""
                : $" (Warning: {activeTab.CurrentChannel.Channel.ToChatType().Name()})";

            channelNameChunks = [new TextChunk(ChunkSource.None, null, $"{overrideName}{warning}")];
        }
        else if (ScreenshotMode && activeTab.CurrentChannel.Channel is InputChannel.Tell && activeTab.CurrentChannel.TellTarget != null)
        {
            if (!string.IsNullOrWhiteSpace(activeTab.CurrentChannel.TellTarget.Name) && activeTab.CurrentChannel.TellTarget.World != 0)
            {
                // Note: don't use HidePlayerInString here because abbreviation settings do not affect this.
                var playerName = HashPlayer(activeTab.CurrentChannel.TellTarget.Name, activeTab.CurrentChannel.TellTarget.World);
                var world = Sheets.WorldSheet.TryGetRow(activeTab.CurrentChannel.TellTarget.World, out var worldRow)
                    ? worldRow.Name.ExtractText()
                    : "???";

                channelNameChunks =
                [
                    new TextChunk(ChunkSource.None, null, "Tell "),
                    new TextChunk(ChunkSource.None, null, playerName),
                    new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
                    new TextChunk(ChunkSource.None, null, world)
                ];
            }
            else
            {
                // We still need to censor the name if we couldn't read valid data.
                channelNameChunks = [new TextChunk(ChunkSource.None, null, "Tell")];
            }
        }
        else
        {
            channelNameChunks = activeTab.CurrentChannel.Name.Count > 0
                ? activeTab.CurrentChannel.Name.ToArray()
                : [new TextChunk(ChunkSource.None, null, activeTab.CurrentChannel.Channel.ToChatType().Name())];
        }

        return channelNameChunks;
    }

    internal void SetChannel(InputChannel? channel)
    {
        channel ??= InputChannel.Say;
        if (channel != InputChannel.Tell)
        {
            Plugin.CurrentTab.CurrentChannel.TellTarget = null;
            Plugin.CurrentTab.CurrentChannel.TempTellTarget = null;
        }

        // Instead of calling SetChannel(), we ask the ExtraChat plugin to set a
        // channel override by just calling the command directly.
        if (channel.Value.IsExtraChatLinkshell())
        {
            // Check that the command is registered in Dalamud so the game code
            // never sees the command itself.
            if (!Plugin.CommandManager.Commands.ContainsKey(channel.Value.Prefix()))
                return;

            // Send the command through the game chat. We can't call
            // ICommandManager.ProcessCommand() here because ExtraChat only
            // registers stub handlers and actually processes its commands in a
            // SendMessage detour.
            var bytes = Encoding.UTF8.GetBytes(channel.Value.Prefix());
            ChatBox.SendMessageUnsafe(bytes);

            Plugin.CurrentTab.CurrentChannel.Channel = channel.Value;
            return;
        }

        var target = Plugin.CurrentTab.CurrentChannel.TempTellTarget ?? Plugin.CurrentTab.CurrentChannel.TellTarget;
        Plugin.Functions.Chat.SetChannel(channel.Value, target);
    }

    private Chunk[] GenerateTellTargetName(TellTarget tellTarget)
    {
        var playerName = tellTarget.Name;
        if (ScreenshotMode)
            // Note: don't use HidePlayerInString here because
            // abbreviation settings do not affect this.
            playerName = HashPlayer(tellTarget.Name, tellTarget.World);

        var world = Sheets.WorldSheet.TryGetRow(tellTarget.World, out var worldRow)
            ? worldRow.Name.ExtractText()
            : "???";

        return
        [
            new TextChunk(ChunkSource.None, null, "Tell "),
            new TextChunk(ChunkSource.None, null, playerName),
            new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
            new TextChunk(ChunkSource.None, null, world)
        ];
    }

    internal void SendChatBox(Tab activeTab)
    {
        if (!string.IsNullOrWhiteSpace(Chat))
        {
            var trimmed = Chat.Trim();
            AddBacklog(trimmed);
            InputBacklogIdx = -1;

            // Handle DM tabs specially - always send as tells to the target player
            if (activeTab is DMTab dmTab)
            {
                try
                {
                    // Handle DM tab message sending
                    if (trimmed.StartsWith('/'))
                    {
                        // Commands are sent as-is (not as tells)
                        ChatBox.SendMessage(trimmed);
                    }
                    else
                    {
                        // Non-command messages are sent as tells to the DM target
                        var tellCommand = $"/tell {dmTab.Player.DisplayName} {trimmed}";
                        
                        // Track this outgoing tell BEFORE sending for error message routing
                        Plugin.DMMessageRouter.TrackOutgoingTell(dmTab.Player);
                        
                        // Send the tell command - the game will echo it back as TellOutgoing
                        ChatBox.SendMessage(tellCommand);
                        
                        // NOTE: We don't display the outgoing message here because the game
                        // will echo the tell back as a TellOutgoing message which will be
                        // processed by DMMessageRouter.ProcessIncomingMessage() and displayed properly
                        // with the correct sender name format
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to send DM message to {dmTab.Player}: {ex.Message}");
                    DisplayErrorMessageInTab(dmTab, $"Failed to send message: {GetUserFriendlyErrorMessage(ex)}");
                }
                
                Chat = string.Empty;
                if (Plugin.Config.KeepInputFocus)
                {
                    Activate = true; // Maintain focus after sending DM message
                }
                return;
            }

            // AUTO-CREATE DM TAB: Check if this is a /tell command from main chat
            if (trimmed.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Parse: /tell PlayerName@World message or /tell PlayerName message
                    // Handle player names with spaces by looking for @ symbol or using a more sophisticated approach
                    var tellContent = trimmed.Substring(6); // Remove "/tell "
                    
                    string targetName;
                    string message;
                    
                    // Check if there's an @ symbol (indicating world name)
                    var atIndex = tellContent.IndexOf('@');
                    if (atIndex != -1)
                    {
                        // Find the space after the world name
                        var spaceAfterWorld = tellContent.IndexOf(' ', atIndex);
                        if (spaceAfterWorld != -1)
                        {
                            targetName = tellContent.Substring(0, spaceAfterWorld);
                            message = tellContent.Substring(spaceAfterWorld + 1);
                        }
                        else
                        {
                            // No message, just the target name
                            targetName = tellContent;
                            message = "";
                        }
                    }
                    else
                    {
                        // No @ symbol, so we need to guess where the player name ends
                        // This is tricky because player names can have spaces
                        // For now, assume the last word is the message and everything before is the player name
                        var lastSpaceIndex = tellContent.LastIndexOf(' ');
                        if (lastSpaceIndex != -1)
                        {
                            targetName = tellContent.Substring(0, lastSpaceIndex);
                            message = tellContent.Substring(lastSpaceIndex + 1);
                        }
                        else
                        {
                            // No spaces, so it's just the target name with no message
                            targetName = tellContent;
                            message = "";
                        }
                    }
                    
                    // Try to parse the player name and create a DMPlayer
                    var targetPlayer = ParsePlayerNameWithWorld(targetName);
                    if (targetPlayer != null)
                    {
                        // Check if we already have a DM tab for this player using DMManager
                        var dmManager = DMManager.Instance;
                        if (!dmManager.HasOpenDMTab(targetPlayer))
                        {
                            // Use DMManager to properly create and register the DM tab
                            var newDMTab = dmManager.OpenDMTab(targetPlayer);
                            if (newDMTab != null)
                            {
                                Plugin.Log.Info($"Auto-created DM tab for {targetPlayer.DisplayName}");
                            }
                        }
                        
                        // Track this outgoing tell for error message routing
                        Plugin.DMMessageRouter.TrackOutgoingTell(targetPlayer);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to process /tell command for auto-tab creation: {ex.Message}");
                }
                
                // Continue with normal tell processing - don't return here
            }

            if (TellSpecial)
            {
                var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref tellBytes);

                Plugin.Functions.Chat.SendTellUsingCommandInner(tellBytes);

                TellSpecial = false;

                activeTab.CurrentChannel.ResetTempChannel();
                Chat = string.Empty;
                if (Plugin.Config.KeepInputFocus)
                {
                    Activate = true; // Maintain focus after sending tell
                }
                return;
            }

            if (!trimmed.StartsWith('/'))
            {
                var target = activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;
                if (target != null)
                {
                    // ContentId 0 is a case where we can't directly send messages, so we send a /tell formatted message and let the game handle it
                    if (target.ContentId == 0)
                    {
                        trimmed = $"/tell {target.ToTargetString()} {trimmed}";
                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);

                        ChatBox.SendMessageUnsafe(tellBytes);

                        activeTab.CurrentChannel.ResetTempChannel();
                        Chat = string.Empty;
                        if (Plugin.Config.KeepInputFocus)
                        {
                            Activate = true; // Maintain focus after sending tell
                        }
                        return;
                    }

                    var reason = target.Reason;
                    var world = Sheets.WorldSheet.GetRow(target.World);
                    if (world is { IsPublic: true })
                    {
                        if (reason == TellReason.Reply && GameFunctions.GameFunctions.GetFriends().Any(friend => friend.ContentId == target.ContentId))
                            reason = TellReason.Friend;

                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);

                        Plugin.Functions.Chat.SendTell(reason, target.ContentId, target.Name, (ushort) world.RowId, tellBytes, trimmed);
                    }

                    activeTab.CurrentChannel.ResetTempChannel();
                    Chat = string.Empty;
                    if (Plugin.Config.KeepInputFocus)
                    {
                        Activate = true; // Maintain focus after sending tell
                    }
                    return;
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                    trimmed = $"{activeTab.CurrentChannel.TempChannel.Prefix()} {trimmed}";
                else
                    trimmed = $"{activeTab.CurrentChannel.Channel.Prefix()} {trimmed}";
            }

            var bytes = Encoding.UTF8.GetBytes(trimmed);
            AutoTranslate.ReplaceWithPayload(ref bytes);

            ChatBox.SendMessageUnsafe(bytes);
        }

        activeTab.CurrentChannel.ResetTempChannel();
        Chat = string.Empty;
        // For regular chat messages (non-DM), always return focus to game
        // KeepInputFocus only applies to DM tabs/windows and tell messages
        // (Regular chat focus behavior is handled above for DM/tell cases)
    }

    /// <summary>
    /// Displays an outgoing message in a DM tab with appropriate styling.
    /// </summary>
    private void DisplayOutgoingMessageInTab(DMTab dmTab, string messageContent)
    {
        try
        {
            // Create a fake outgoing tell message for display
            var outgoingMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, $">> {messageContent}")
                },
                new ChatCode((ushort)ChatType.TellOutgoing)
            );
            
            // Add to both the tab and history
            dmTab.AddMessage(outgoingMessage, unread: false);
            dmTab.History.AddMessage(outgoingMessage, isIncoming: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to display outgoing message in DM tab: {ex.Message}");
        }
    }

    /// <summary>
    /// Displays an error message in a DM tab.
    /// </summary>
    private void DisplayErrorMessageInTab(DMTab dmTab, string errorText)
    {
        try
        {
            var errorMessage = Message.FakeMessage(
                new List<Chunk>
                {
                    new TextChunk(ChunkSource.None, null, errorText)
                },
                new ChatCode((ushort)ChatType.Error)
            );
            
            dmTab.AddMessage(errorMessage, unread: false);
            dmTab.History.AddMessage(errorMessage, isIncoming: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to display error message in DM tab: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts technical exceptions to user-friendly error messages.
    /// </summary>
    private string GetUserFriendlyErrorMessage(Exception ex)
    {
        return ex.Message switch
        {
            var msg when msg.Contains("message is empty") => "Cannot send empty message",
            var msg when msg.Contains("message is longer than 500 bytes") => "Message is too long (max 500 characters)",
            var msg when msg.Contains("message contained invalid characters") => "Message contains invalid characters",
            _ => "Unable to send message. Please try again."
        };
    }

    internal void UserHide()
    {
        CurrentHideState = HideState.User;
    }

    /// <summary>
    /// Parses a player name that may include world information for auto-tab creation.
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

    public void DrawMessageLog(Tab tab, PayloadHandler handler, float childHeight, bool switchedTab)
    {
        // Use unique child window IDs to prevent conflicts between DM pane and regular chat
        var childId = tab is DMTab ? "##chat2-dm-messages" : "##chat2-messages";
        // Disable child window background to match the parent window
        using var child = ImRaii.Child(childId, new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoBackground);
        if (!child.Success)
            return;

        if (tab.DisplayTimestamp && Plugin.Config.PrettierTimestamps)
            DrawLogTableStyle(tab, handler, switchedTab);
        else
            DrawLogNormalStyle(tab, handler, switchedTab);
    }

    private void DrawLogNormalStyle(Tab tab, PayloadHandler handler, bool switchedTab)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            DrawMessages(tab, handler, false);

        // Simple and direct approach: Check mouse wheel input to prevent auto-scroll
        var io = ImGui.GetIO();
        var userScrollingUp = io.MouseWheel > 0.0f; // Positive wheel = scrolling up
        var userScrollingDown = io.MouseWheel < 0.0f; // Negative wheel = scrolling down
        
        var currentScrollY = ImGui.GetScrollY();
        var maxScrollY = ImGui.GetScrollMaxY();
        var isAtBottom = currentScrollY >= maxScrollY - 1.0f;
        
        // CRITICAL FIX: Never auto-scroll if user is actively scrolling up with mouse wheel
        if (userScrollingUp)
        {
            return; // Exit early, don't auto-scroll at all
        }
        
        // Only auto-scroll if we switched tabs OR if we're at bottom (and user isn't scrolling up)
        if (switchedTab || isAtBottom)
        {
            ImGui.SetScrollHereY(1f);
        }

        // Popup handling moved to parent window level to avoid child window conflicts
    }

    private void DrawLogTableStyle(Tab tab, PayloadHandler handler, bool switchedTab)
    {
        var compact = Plugin.Config.MoreCompactPretty;
        var oldItemSpacing = ImGui.GetStyle().ItemSpacing;
        var oldCellPadding = ImGui.GetStyle().CellPadding;

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, oldCellPadding with { Y = 0 }, compact))
        {
            using var table = ImRaii.Table("timestamp-table", 2, ImGuiTableFlags.PreciseWidths);
            if (!table.Success)
                return;

            ImGui.TableSetupColumn("timestamps", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("messages", ImGuiTableColumnFlags.WidthStretch);

            DrawMessages(tab, handler, true, compact, oldCellPadding.Y);

            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, oldItemSpacing))
            using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, oldCellPadding))
            {
                // Custom styles can have cellPadding that go above 4, which GetScrollY isn't respecting
                var cellPaddingOffset = !compact && oldCellPadding.Y > 4f ? oldCellPadding.Y - 4f : 0f;
                
                // Simple and direct approach: Check mouse wheel input to prevent auto-scroll
                var io = ImGui.GetIO();
                var userScrollingUp = io.MouseWheel > 0.0f; // Positive wheel = scrolling up
                
                var currentScrollY = ImGui.GetScrollY();
                var maxScrollY = ImGui.GetScrollMaxY();
                var isAtBottom = currentScrollY + cellPaddingOffset >= maxScrollY - 1.0f;
                
                // CRITICAL FIX: Never auto-scroll if user is actively scrolling up with mouse wheel
                if (userScrollingUp)
                {
                    return; // Exit early, don't auto-scroll at all
                }
                
                // Only auto-scroll if we switched tabs OR if we're at bottom (and user isn't scrolling up)
                if (switchedTab || isAtBottom)
                {
                    ImGui.SetScrollHereY(1f);
                }

                // Popup handling moved to parent window level to avoid child window conflicts
            }
        }
    }

    private void DrawMessages(Tab tab, PayloadHandler handler, bool isTable, bool moreCompact = false, float oldCellPaddingY = 0)
    {
        try
        {
            // This may produce ApplicationException which is catched below.
            using var messages = tab.Messages.GetReadOnly(3);

            var reset = false;
            if (LastResize is { IsRunning: true, Elapsed.TotalSeconds: > 0.25 })
            {
                LastResize.Stop();
                LastResize.Reset();
                reset = true;
            }

            var lastPosY = ImGui.GetCursorPosY();
            var lastTimestamp = string.Empty;
            int? lastMessageHash = null;
            var sameCount = 0;

            var maxLines = Plugin.Config.MaxLinesToRender;
            var startLine = messages.Count > maxLines ? messages.Count - maxLines : 0;
            for (var i = startLine; i < messages.Count; i++)
            {
                var message = messages[i];
                if (reset)
                {
                    message.Height[tab.Identifier] = null;
                    message.IsVisible[tab.Identifier] = false;
                }

                if (Plugin.Config.CollapseDuplicateMessages)
                {
                    var messageHash = message.Hash;
                    var same = lastMessageHash == messageHash;
                    if (same)
                    {
                        sameCount += 1;
                        message.IsVisible[tab.Identifier] = false;
                        if (i != messages.Count - 1)
                            continue;
                    }

                    if (sameCount > 0)
                    {
                        ImGui.SameLine();
                        DrawChunks(
                            [new TextChunk(ChunkSource.None, null, $" ({sameCount + 1}x)") { FallbackColour = ChatType.System, Italic = true, }],
                            true,
                            handler,
                            ImGui.GetContentRegionAvail().X
                        );
                        sameCount = 0;
                    }

                    lastMessageHash = messageHash;
                    if (same && i == messages.Count - 1)
                        continue;
                }

                // go to next row
                if (isTable)
                    ImGui.TableNextColumn();

                // Set the height of the previous message. `lastPosY` is set to
                // the top of the previous message, and the current cursor is at
                // the top of the current message.
                if (i > 0)
                {
                    var prevMessage = messages[i - 1];
                    prevMessage.Height.TryGetValue(tab.Identifier, out var prevHeight);
                    if (prevHeight == null || (prevMessage.IsVisible.TryGetValue(tab.Identifier, out var prevVisible) && prevVisible))
                    {
                        var newHeight = ImGui.GetCursorPosY() - lastPosY;

                        // Remove the padding from the bottom of the previous row and the top of the current row.
                        if (isTable && !moreCompact)
                            newHeight -= oldCellPaddingY * 2;

                        if (newHeight != 0)
                            prevMessage.Height[tab.Identifier] = newHeight;
                    }
                }
                lastPosY = ImGui.GetCursorPosY();

                // message has rendered once
                // message isn't visible, so render dummy
                message.Height.TryGetValue(tab.Identifier, out var height);
                message.IsVisible.TryGetValue(tab.Identifier, out var visible);
                if (height != null && !visible)
                {
                    var beforeDummy = ImGui.GetCursorPos();

                    // skip to the message column for vis test
                    if (isTable)
                        ImGui.TableNextColumn();

                    ImGui.Dummy(new Vector2(10f, height.Value));

                    var nowVisible = ImGui.IsItemVisible();
                    if (!nowVisible)
                        continue;

                    if (isTable)
                        ImGui.TableSetColumnIndex(0);

                    ImGui.SetCursorPos(beforeDummy);
                    message.IsVisible[tab.Identifier] = nowVisible;
                }

                if (tab.DisplayTimestamp)
                {
                    var localTime = message.Date.ToLocalTime();
                    var timestamp = localTime.ToString("t", !Plugin.Config.Use24HourClock ? null : CultureInfo.CreateSpecificCulture("de-DE"));
                    if (isTable)
                    {
                        if (!Plugin.Config.HideSameTimestamps || timestamp != lastTimestamp)
                        {
                            lastTimestamp = timestamp;
                            ImGui.TextUnformatted(timestamp);

                            // We use an IsItemHovered() check here instead of
                            // just calling Tooltip() to avoid computing the
                            // tooltip string for all visible items on every
                            // frame.
                            if (ImGui.IsItemHovered())
                                ImGuiUtil.Tooltip(localTime.ToString("F"));
                        }
                        else
                        {
                            // Avoids rendering issues caused by emojis in
                            // message content.
                            ImGui.TextUnformatted("");
                        }
                    }
                    else
                    {
                        DrawChunk(new TextChunk(ChunkSource.None, null, $"[{timestamp}] ") { Foreground = 0xFFFFFFFF, });
                        ImGui.SameLine();
                    }
                }

                if (isTable)
                    ImGui.TableNextColumn();

                var lineWidth = ImGui.GetContentRegionAvail().X;
                if (message.Sender.Count > 0)
                {
                    DrawChunks(message.Sender, true, handler, lineWidth);
                    ImGui.SameLine();
                }

                // We need to draw something otherwise the item visibility check below won't work.
                if (message.Content.Count == 0)
                    DrawChunks([new TextChunk(ChunkSource.Content, null, " ")], true, handler, lineWidth);
                else
                    DrawChunks(message.Content, true, handler, lineWidth);

                message.IsVisible[tab.Identifier] = ImGui.IsItemVisible();
            }
        }
        catch (ApplicationException)
        {
            // We couldn't get a reader lock on messages within 3ms, so
            // don't draw anything (and don't log a warning either).
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Error drawing chat log");
        }
    }

    private void DrawTabBar()
    {
        using (ModernUI.PushModernTabStyle(Plugin.Config))
        using (var tabBar = ImRaii.TabBar("##chat2-tabs"))
        {
            if (!tabBar.Success)
                return;

            var previousTab = Plugin.CurrentTab;
            
            for (var tabI = 0; tabI < Plugin.Config.Tabs.Count; tabI++)
            {
                var tab = Plugin.Config.Tabs[tabI];
                if (tab.PopOut)
                    continue;
                
                // Skip DM tabs if DM section is popped out to separate window
                if (Plugin.Config.DMSectionPoppedOut && (tab is DMTab || IsLikelyDMTab(tab)))
                    continue;

                // Modern unread indicator with badge styling
                var unread = tabI == Plugin.LastTab || tab.UnreadMode == UnreadMode.None || tab.Unread == 0 
                    ? "" 
                    : Plugin.Config.ModernUIEnabled 
                        ? $" •{tab.Unread}" 
                        : $" ({tab.Unread})";

                // For DM tabs, use their custom display name which includes unread indicators
                var tabName = tab.Name;
                if (tab is DMTab dmTab)
                {
                    // DM tabs handle their own unread indicators, so we don't add the standard unread suffix
                    tabName = dmTab.GetDisplayName();
                    unread = ""; // Clear the standard unread indicator since DM tabs handle it themselves
                }
                        
                var flags = ImGuiTabItemFlags.None;
                if (Plugin.WantedTab == tabI)
                    flags |= ImGuiTabItemFlags.SetSelected;

                // Apply smooth transition effects
                var isActive = Plugin.LastTab == tabI;
                if (Plugin.Config.ModernUIEnabled && Plugin.Config.SmoothTabTransitions && !isActive)
                {
                    var time = (float)ImGui.GetTime();
                    var alpha = 0.7f + 0.1f * (float)Math.Sin(time * 1.5f + tabI * 0.5f);
                    using var color = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.Text) & 0x00FFFFFF | ((uint)(alpha * 255) << 24));
                }

                // Create tab label with optional icon
                var tabLabel = tabName + unread;
                if (Plugin.Config.ModernUIEnabled && Plugin.Config.ShowTabIcons)
                {
                    var icon = ModernUI.GetTabIcon(tab);
                    tabLabel = $"{icon.ToIconString()} {tabName}{unread}";
                }

                // For DM tabs, add inline action buttons
                if (tab is DMTab dmTabForButtons)
                {
                    using var tabItem = ImRaii.TabItem($"{tabLabel}###log-tab-{tabI}", flags);
                    
                    // Handle drag and drop reordering
                    if (ModernUI.HandleTabDragDrop(tabI, Plugin.Config.Tabs, Plugin.Config))
                        Plugin.SaveConfig();
                    
                    // Check for popout during each tab's drag operation
                    if (ModernUI.CheckDragToPopout(Plugin.Config.Tabs, Plugin.Config))
                        Plugin.SaveConfig();
                    
                    DrawTabContextMenu(tab, tabI);

                    if (!tabItem.Success)
                        continue;

                    var hasTabSwitched = Plugin.LastTab != tabI;
                    Plugin.LastTab = tabI;

                    if (hasTabSwitched)
                        TabSwitched(tab, previousTab);

                    // Clear unread indicators - for DM tabs, also clear the DM history unread count
                    tab.Unread = 0;
                    dmTabForButtons.MarkAsRead();
                    
                    DrawMessageLog(tab, PayloadHandler, GetRemainingHeightForMessageLog(), hasTabSwitched);
                }
                else
                {
                    // Regular tab handling
                    using var tabItem = ImRaii.TabItem($"{tabLabel}###log-tab-{tabI}", flags);
                    
                    // Handle drag and drop reordering
                    if (ModernUI.HandleTabDragDrop(tabI, Plugin.Config.Tabs, Plugin.Config))
                        Plugin.SaveConfig();
                    
                    // Check for popout during each tab's drag operation
                    if (ModernUI.CheckDragToPopout(Plugin.Config.Tabs, Plugin.Config))
                        Plugin.SaveConfig();
                    
                    DrawTabContextMenu(tab, tabI);

                    if (!tabItem.Success)
                        continue;

                    var hasTabSwitched = Plugin.LastTab != tabI;
                    Plugin.LastTab = tabI;

                    if (hasTabSwitched)
                        TabSwitched(tab, previousTab);

                    // Clear unread indicators
                    tab.Unread = 0;
                    
                    DrawMessageLog(tab, PayloadHandler, GetRemainingHeightForMessageLog(), hasTabSwitched);
                }
            }

            Plugin.WantedTab = null;
        }
    }

    private void DrawTabSidebar()
    {
        var currentTab = -1;
        using var tabTable = ImRaii.Table("tabs-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable);
        if (!tabTable.Success)
            return;

        ImGui.TableSetupColumn("tabs", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("chat", ImGuiTableColumnFlags.WidthStretch, 4);

        ImGui.TableNextColumn();

        var hasTabSwitched = false;
        var childHeight = GetRemainingHeightForMessageLog();
        using (var child = ImRaii.Child("##chat2-tab-sidebar", new Vector2(-1, childHeight)))
        {
            if (child)
            {
                var previousTab = Plugin.CurrentTab;
                
                for (var tabI = 0; tabI < Plugin.Config.Tabs.Count; tabI++)
                {
                    var tab = Plugin.Config.Tabs[tabI];
                    if (tab.PopOut)
                        continue;
                    
                    // Skip DM tabs if DM section is popped out to separate window
                    if (Plugin.Config.DMSectionPoppedOut && (tab is DMTab || IsLikelyDMTab(tab)))
                        continue;

                    var unread = tabI == Plugin.LastTab || tab.UnreadMode == UnreadMode.None || tab.Unread == 0 ? "" : $" ({tab.Unread})";
                    
                    // For DM tabs, use their custom display name which includes unread indicators
                    var tabName = tab.Name;
                    if (tab is DMTab dmTab)
                    {
                        // DM tabs handle their own unread indicators, so we don't add the standard unread suffix
                        tabName = dmTab.GetDisplayName();
                        unread = ""; // Clear the standard unread indicator since DM tabs handle it themselves
                    }
                    
                    // Create tab label with optional icon for sidebar
                    var tabLabel = tabName + unread;
                    if (Plugin.Config.ModernUIEnabled && Plugin.Config.ShowTabIcons)
                    {
                        var icon = ModernUI.GetTabIcon(tab);
                        tabLabel = $"{icon.ToIconString()} {tabName}{unread}";
                    }
                    
                    // For DM tabs, handle selection and buttons differently
                    if (tab is DMTab dmTabForSidebar)
                    {
                        var clicked = ImGui.Selectable($"{tabLabel}###log-tab-{tabI}", Plugin.LastTab == tabI || Plugin.WantedTab == tabI);
                        
                        // Add DM tab buttons below the selectable
                        var buttonSize = ImGui.GetFrameHeight() * 0.6f; // Smaller buttons for sidebar
                        
                        // Pop out button
                        using (ModernUI.PushModernButtonStyle(Plugin.Config))
                        {
                            if (ImGuiUtil.IconButton(FontAwesomeIcon.ExternalLinkAlt, id: "##dm-sidebar-popout-" + tabI, width: (int)buttonSize))
                            {
                                DMManager.Instance.ConvertTabToWindow(dmTabForSidebar.Player);
                                Plugin.SaveConfig();
                            }
                        }
                        
                        if (ImGui.IsItemHovered())
                            ModernUI.DrawModernTooltip("Pop Out to DM Window", Plugin.Config);
                        
                        ImGui.SameLine();
                        
                        // Close button
                        using (ModernUI.PushModernButtonStyle(Plugin.Config))
                        {
                            if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, id: "##dm-sidebar-close-" + tabI, width: (int)buttonSize))
                            {
                                DMManager.Instance.CloseDMTab(dmTabForSidebar.Player);
                                Plugin.SaveConfig();
                            }
                        }
                        
                        if (ImGui.IsItemHovered())
                            ModernUI.DrawModernTooltip("Close DM Tab", Plugin.Config);
                        
                        // Handle drag and drop reordering for sidebar
                        if (ModernUI.HandleTabDragDrop(tabI, Plugin.Config.Tabs, Plugin.Config))
                            Plugin.SaveConfig();
                        
                        // Check for popout during each tab's drag operation
                        if (ModernUI.CheckDragToPopout(Plugin.Config.Tabs, Plugin.Config))
                            Plugin.SaveConfig();
                        
                        DrawTabContextMenu(tab, tabI);

                        if (!clicked && Plugin.WantedTab != tabI)
                            continue;

                        currentTab = tabI;
                        hasTabSwitched = Plugin.LastTab != tabI;
                        Plugin.LastTab = tabI;
                        if (hasTabSwitched)
                            TabSwitched(tab, previousTab);
                    }
                    else
                    {
                        // Regular tab handling for sidebar
                        var clicked = ImGui.Selectable($"{tabLabel}###log-tab-{tabI}", Plugin.LastTab == tabI || Plugin.WantedTab == tabI);
                        
                        // Handle drag and drop reordering for sidebar
                        if (ModernUI.HandleTabDragDrop(tabI, Plugin.Config.Tabs, Plugin.Config))
                            Plugin.SaveConfig();
                        
                        // Check for popout during each tab's drag operation
                        if (ModernUI.CheckDragToPopout(Plugin.Config.Tabs, Plugin.Config))
                            Plugin.SaveConfig();
                        
                        DrawTabContextMenu(tab, tabI);

                        if (!clicked && Plugin.WantedTab != tabI)
                            continue;

                        currentTab = tabI;
                        hasTabSwitched = Plugin.LastTab != tabI;
                        Plugin.LastTab = tabI;
                        if (hasTabSwitched)
                            TabSwitched(tab, previousTab);
                    }
                }
                
                // Check for drag-to-popout in sidebar
                if (ModernUI.CheckDragToPopout(Plugin.Config.Tabs, Plugin.Config))
                    Plugin.SaveConfig();
            }
        }

        ImGui.TableNextColumn();

        if (currentTab == -1 && Plugin.LastTab < Plugin.Config.Tabs.Count)
        {
            currentTab = Plugin.LastTab;
            var tab = Plugin.Config.Tabs[currentTab];
            tab.Unread = 0;
            
            // For DM tabs, also clear the DM history unread count
            if (tab is DMTab dmTabForClear)
            {
                dmTabForClear.MarkAsRead();
            }
        }

        if (currentTab > -1)
            DrawMessageLog(Plugin.Config.Tabs[currentTab], PayloadHandler, childHeight, hasTabSwitched);

        Plugin.WantedTab = null;
    }

    private void DrawTabContextMenu(Tab tab, int i)
    {
        using var contextMenu = ImRaii.ContextPopupItem($"tab-context-menu-{i}");
        if (!contextMenu.Success)
            return;

        var anyChanged = false;
        var tabs = Plugin.Config.Tabs;
        var isDMTab = tab is DMTab;
        var dmTab = tab as DMTab;

        ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##tab-name", ref tab.Name, 128))
            anyChanged = true;

        if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, tooltip: Language.ChatLog_Tabs_Delete))
        {
            // For DM tabs, use DMManager to properly close them
            if (isDMTab && dmTab != null)
            {
                DMManager.Instance.CloseDMTab(dmTab.Player);
            }
            else
            {
                tabs.RemoveAt(i);
            }
            Plugin.WantedTab = 0;
            anyChanged = true;
        }

        ImGui.SameLine();

        var (leftIcon, leftTooltip) = Plugin.Config.SidebarTabView
            ? (FontAwesomeIcon.ArrowUp, Language.ChatLog_Tabs_MoveUp)
            : (FontAwesomeIcon.ArrowLeft, Language.ChatLog_Tabs_MoveLeft);
        if (ImGuiUtil.IconButton(leftIcon, tooltip: leftTooltip) && i > 0)
        {
            (tabs[i - 1], tabs[i]) = (tabs[i], tabs[i - 1]);
            ImGui.CloseCurrentPopup();
            anyChanged = true;
        }

        ImGui.SameLine();

        var (rightIcon, rightTooltip) = Plugin.Config.SidebarTabView
            ? (FontAwesomeIcon.ArrowDown, Language.ChatLog_Tabs_MoveDown)
            : (FontAwesomeIcon.ArrowRight, Language.ChatLog_Tabs_MoveRight);
        if (ImGuiUtil.IconButton(rightIcon, tooltip: rightTooltip) && i < tabs.Count - 1)
        {
            (tabs[i + 1], tabs[i]) = (tabs[i], tabs[i + 1]);
            ImGui.CloseCurrentPopup();
            anyChanged = true;
        }

        ImGui.SameLine();

        // For DM tabs, show "Pop Out to Window" instead of generic popout
        if (isDMTab)
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.ExternalLinkAlt, tooltip: "Pop Out to DM Window"))
            {
                DMManager.Instance.ConvertTabToWindow(dmTab.Player);
                anyChanged = true;
            }
        }
        else
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.WindowRestore, tooltip: Language.ChatLog_Tabs_PopOut))
            {
                tab.PopOut = true;
                anyChanged = true;
            }
        }

        // Add DM-specific context menu options
        if (isDMTab)
        {
            ImGui.Separator();
            
            // Add friend option
            if (ImGui.MenuItem("Add Friend"))
            {
                try
                {
                    var friendCommand = $"/friendlist add {dmTab.Player.DisplayName}";
                    ChatBox.SendMessage(friendCommand);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to add {dmTab.Player} as friend: {ex.Message}");
                }
            }
            
            // Invite to party option
            if (ImGui.MenuItem("Invite to Party"))
            {
                try
                {
                    var inviteCommand = $"/invite {dmTab.Player.DisplayName}";
                    ChatBox.SendMessage(inviteCommand);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to invite {dmTab.Player} to party: {ex.Message}");
                }
            }
            
            // Open character card option
            if (ImGui.MenuItem("Open Character Card"))
            {
                try
                {
                    var examineCommand = $"/examine {dmTab.Player.DisplayName}";
                    ChatBox.SendMessage(examineCommand);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to open character card for {dmTab.Player}: {ex.Message}");
                }
            }
        }

        if (anyChanged)
            Plugin.SaveConfig();
    }

    internal readonly List<bool> PopOutDocked = [];
    internal readonly HashSet<Guid> PopOutWindows = [];
    private void AddPopOutsToDraw()
    {
        HandlerLender.ResetCounter();

        if (PopOutDocked.Count != Plugin.Config.Tabs.Count)
        {
            PopOutDocked.Clear();
            PopOutDocked.AddRange(Enumerable.Repeat(false, Plugin.Config.Tabs.Count));
        }

        for (var i = 0; i < Plugin.Config.Tabs.Count; i++)
        {
            var tab = Plugin.Config.Tabs[i];
            if (!tab.PopOut)
                continue;

            if (PopOutWindows.Contains(tab.Identifier))
                continue;

            var window = new Popout(this, tab, i);

            Plugin.WindowSystem.AddWindow(window);
            PopOutWindows.Add(tab.Identifier);
        }
    }

    private unsafe void DrawAutoComplete()
    {
        if (AutoCompleteInfo == null)
            return;

        AutoCompleteList ??= AutoTranslate.Matching(AutoCompleteInfo.ToComplete, Plugin.Config.SortAutoTranslate);
        if (AutoCompleteOpen)
        {
            ImGui.OpenPopup(AutoCompleteId);
            AutoCompleteOpen = false;
        }

        // Modern autocomplete popup with better sizing and styling
        var popupSize = new Vector2(450, 350) * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(popupSize);
        
        if (Plugin.Config.ModernUIEnabled)
        {
            ImGui.SetNextWindowBgAlpha(0.95f);
            using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Plugin.Config.UIRounding)
                .Push(ImGuiStyleVar.WindowPadding, new Vector2(12, 12));
        }
        
        using var popup = ImRaii.Popup(AutoCompleteId);
        if (!popup.Success)
        {
            if (ActivatePos == -1)
                ActivatePos = AutoCompleteInfo.EndPos;

            AutoCompleteInfo = null;
            AutoCompleteList = null;
            Activate = true;
            return;
        }

        // Modern search input
        ImGui.SetNextItemWidth(-1);
        using (ModernUI.PushModernInputStyle(Plugin.Config))
        {
            if (ImGui.InputTextWithHint("##auto-complete-filter", Language.AutoTranslate_Search_Hint, ref AutoCompleteInfo.ToComplete, 256, ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackHistory, AutoCompleteCallback))
            {
                AutoCompleteList = AutoTranslate.Matching(AutoCompleteInfo.ToComplete, Plugin.Config.SortAutoTranslate);
                AutoCompleteSelection = 0;
                AutoCompleteShouldScroll = true;
            }
        }

        var selected = -1;
        if (ImGui.IsItemActive() && ImGui.GetIO().KeyCtrl)
        {
            for (var i = 0; i < 10 && i < AutoCompleteList.Count; i++)
            {
                var num = (i + 1) % 10;
                var key = ImGuiKey.Key0 + num;
                var key2 = ImGuiKey.Keypad0 + num;
                if (ImGui.IsKeyDown(key) || ImGui.IsKeyDown(key2))
                    selected = i;
            }
        }

        if (ImGui.IsItemDeactivated())
        {
            if (ImGui.IsKeyDown(ImGuiKey.Escape))
            {
                ImGui.CloseCurrentPopup();
                return;
            }

            var enter = ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter);
            if (AutoCompleteList.Count > 0 && enter)
                selected = AutoCompleteSelection;
        }

        if (ImGui.IsWindowAppearing())
        {
            FixCursor = true;
            ImGui.SetKeyboardFocusHere(-1);
        }

        // Modern separator between search and results
        ModernUI.DrawModernSeparator(Plugin.Config);

        using var child = ImRaii.Child("##auto-complete-list", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child.Success)
            return;

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());

        clipper.Begin(AutoCompleteList.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = AutoCompleteList[i];

                var highlight = AutoCompleteSelection == i;
                
                // Check if this is an emote entry (Group=0, Row=0)
                var isEmote = entry.Group == 0 && entry.Row == 0;
                
                var clicked = false;
                
                if (isEmote)
                {
                    // Draw emote with image
                    var emoteImage = EmoteCache.GetEmote(entry.String);
                    if (emoteImage != null)
                    {
                        // Create a selectable area that covers the whole row
                        clicked = ImGui.Selectable($"##emote-{i}", highlight, ImGuiSelectableFlags.SpanAllColumns) || selected == i;
                        
                        // Draw the emote image and text on top of the selectable
                        var cursor = ImGui.GetCursorPos();
                        ImGui.SetCursorPos(cursor - new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
                        
                        var emoteSize = new Vector2(18, 18) * ImGuiHelpers.GlobalScale; // Small size for autocomplete
                        emoteImage.Draw(emoteSize);
                        
                        ImGui.SameLine();
                        ImGui.SetCursorPosY(cursor.Y - ImGui.GetTextLineHeightWithSpacing() + (emoteSize.Y - ImGui.GetTextLineHeight()) / 2);
                        ImGui.TextUnformatted(entry.String);
                    }
                    else
                    {
                        // Fallback if emote image not available
                        clicked = ImGui.Selectable($"{entry.String} (emote)##{entry.Group}/{entry.Row}", highlight) || selected == i;
                    }
                }
                else
                {
                    // Regular auto-translate entry
                    clicked = ImGui.Selectable($"{entry.String}##{entry.Group}/{entry.Row}", highlight) || selected == i;
                }
                
                // Show keyboard shortcut for first 10 items
                if (i < 10)
                {
                    var button = (i + 1) % 10;
                    var text = string.Format(Language.AutoTranslate_Completion_Key, button);
                    var size = ImGui.CalcTextSize(text);
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - size.X);
                    ImGui.PushStyleColor(ImGuiCol.Text, *ImGui.GetStyleColorVec4(ImGuiCol.TextDisabled));
                    ImGui.TextUnformatted(text);
                    ImGui.PopStyleColor();
                }

                if (!clicked)
                    continue;

                // Handle selection
                var before = Chat[..AutoCompleteInfo.StartPos];
                var after = Chat[AutoCompleteInfo.EndPos..];
                
                string replacement;
                if (isEmote)
                {
                    // This is an emote - just insert the emote name directly
                    replacement = entry.String;
                }
                else
                {
                    // This is a regular auto-translate entry
                    replacement = $"<at:{entry.Group},{entry.Row}>";
                }
                
                Chat = $"{before}{replacement}{after}";
                ImGui.CloseCurrentPopup();
                Activate = true;
                ActivatePos = AutoCompleteInfo.StartPos + replacement.Length;
            }
        }

        if (!AutoCompleteShouldScroll)
            return;

        AutoCompleteShouldScroll = false;
        var selectedPos = clipper.StartPosY + clipper.ItemsHeight * (AutoCompleteSelection * 1f);
        ImGui.SetScrollFromPosY(selectedPos - ImGui.GetWindowPos().Y);
    }

    private int AutoCompleteCallback(scoped ref ImGuiInputTextCallbackData data)
    {
        if (FixCursor && AutoCompleteInfo != null)
        {
            FixCursor = false;
            data.CursorPos = AutoCompleteInfo.ToComplete.Length;
            data.SelectionStart = data.SelectionEnd = data.CursorPos;
        }

        if (AutoCompleteList == null)
            return 0;

        switch (data.EventKey)
        {
            case ImGuiKey.UpArrow:
                if (AutoCompleteSelection == 0)
                    AutoCompleteSelection = AutoCompleteList.Count - 1;
                else
                    AutoCompleteSelection--;

                AutoCompleteShouldScroll = true;
                return 1;
            case ImGuiKey.DownArrow:
                if (AutoCompleteSelection == AutoCompleteList.Count - 1)
                    AutoCompleteSelection = 0;
                else
                    AutoCompleteSelection++;

                AutoCompleteShouldScroll = true;
                return 1;
            default:
                if(ImGui.IsKeyPressed(ImGuiKey.Tab))
                {
                    if (AutoCompleteSelection == AutoCompleteList.Count - 1)
                        AutoCompleteSelection = 0;
                    else
                        AutoCompleteSelection++;

                    AutoCompleteShouldScroll = true;
                    return 1;
                }
                break;
        }

        return 0;
    }

    public unsafe int Callback(scoped ref ImGuiInputTextCallbackData data)
    {
        // We play the opening sound here only if closing sound has been played before
        if (Plugin.Config.PlaySounds && PlayedClosingSound)
        {
            PlayedClosingSound = false;
            UIGlobals.PlaySoundEffect(ChatOpenSfx);
        }

        // Set the cursor pos to the user selected
        if (Plugin.InputPreview.SelectedCursorPos != -1)
            data.CursorPos = Plugin.InputPreview.SelectedCursorPos;
        Plugin.InputPreview.SelectedCursorPos = -1;

        CursorPos = data.CursorPos;
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (data.CursorPos == 0)
            {
                AutoCompleteInfo = new AutoCompleteInfo(
                    string.Empty,
                    data.CursorPos,
                    data.CursorPos
                );
                AutoCompleteOpen = true;
                AutoCompleteSelection = 0;

                return 0;
            }

            int white;
            for (white = data.CursorPos - 1; white >= 0; white--)
                if (data.Buf[white] == ' ')
                    break;

            var start = data.Buf + white + 1;
            var end = data.CursorPos - white - 1;
            var utf8Message = Marshal.PtrToStringUTF8((nint)start, end);
            var correctedCursor = data.CursorPos - (end - utf8Message.Length);
            AutoCompleteInfo = new AutoCompleteInfo(
                utf8Message,
                white + 1,
                correctedCursor
            );
            AutoCompleteOpen = true;
            AutoCompleteSelection = 0;
            return 0;
        }

        if (data.EventFlag == ImGuiInputTextFlags.CallbackCharFilter)
            if (!Plugin.Functions.Chat.IsCharValid((char) data.EventChar))
                return 1;

        if (Activate)
        {
            Activate = false;
            data.CursorPos = ActivatePos > -1 ? ActivatePos : Chat.Length;
            data.SelectionStart = data.SelectionEnd = data.CursorPos;
            ActivatePos = -1;
        }

        Plugin.CommandHelpWindow.IsOpen = false;
        var text = MemoryHelper.ReadString((nint) data.Buf, data.BufTextLen);
        if (text.StartsWith('/'))
        {
            var command = text.Split(' ')[0];
            var cmd = Sheets.TextCommandSheet.FirstOrNull(cmd =>
                cmd.Command.ExtractText() == command || cmd.Alias.ExtractText() == command ||
                cmd.ShortCommand.ExtractText() == command || cmd.ShortAlias.ExtractText() == command);

            if (cmd != null)
                Plugin.CommandHelpWindow.UpdateContent(cmd.Value);
        }

        if (data.EventFlag != ImGuiInputTextFlags.CallbackHistory)
            return 0;

        var prevPos = InputBacklogIdx;
        switch (data.EventKey)
        {
            case ImGuiKey.UpArrow:
                switch (InputBacklogIdx)
                {
                    case -1:
                        var offset = 0;

                        if (!string.IsNullOrWhiteSpace(Chat))
                        {
                            AddBacklog(Chat);
                            offset = 1;
                        }

                        InputBacklogIdx = InputBacklog.Count - 1 - offset;
                        break;
                    case > 0:
                        InputBacklogIdx--;
                        break;
                }
                break;
            case ImGuiKey.DownArrow:
                if (InputBacklogIdx != -1)
                    if (++InputBacklogIdx >= InputBacklog.Count)
                        InputBacklogIdx = -1;
                break;
        }

        if (prevPos == InputBacklogIdx)
            return 0;

        var historyStr = InputBacklogIdx >= 0 ? InputBacklog[InputBacklogIdx] : string.Empty;
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, historyStr);

        return 0;
    }

    internal void DrawChunks(IReadOnlyList<Chunk> chunks, bool wrap = true, PayloadHandler? handler = null, float lineWidth = 0f)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content))
                continue;

            DrawChunk(chunks[i], wrap, handler, lineWidth);

            if (i < chunks.Count - 1)
            {
                ImGui.SameLine();
            }
            else if (chunks[i].Link is EmotePayload && Plugin.Config.ShowEmotes)
            {
                // Emote payloads seem to not automatically put newlines, which
                // is an issue when modern mode is disabled.
                ImGui.SameLine();
                // Use default ImGui behavior for newlines.
                ImGui.TextUnformatted("");
            }
        }
    }

    private void DrawChunk(Chunk chunk, bool wrap = true, PayloadHandler? handler = null, float lineWidth = 0f)
    {
        if (chunk is IconChunk icon)
        {
            DrawIcon(chunk, icon, handler);
            return;
        }

        if (chunk is not TextChunk text)
            return;

        if (chunk.Link is EmotePayload emotePayload && Plugin.Config.ShowEmotes)
        {
            var emoteSize = ImGui.CalcTextSize("W");
            emoteSize = emoteSize with { Y = emoteSize.X } * 1.5f * Plugin.Config.EmoteSize;

            // TextWrap doesn't work for emotes, so we have to wrap them manually
            if (ImGui.GetContentRegionAvail().X < emoteSize.X)
                ImGui.NewLine();

            // We only draw a dummy if it is still loading, in the case it failed we draw the actual name
            var image = EmoteCache.GetEmote(emotePayload.Code);
            if (image is { Failed: false })
            {
                if (image.IsLoaded)
                    image.Draw(emoteSize);
                else
                    ImGui.Dummy(emoteSize);

                if (ImGui.IsItemHovered())
                    ImGuiUtil.Tooltip(emotePayload.Code);

                return;
            }
        }

        var colour = text.Foreground;
        if (colour == null && text.FallbackColour != null)
        {
            var type = text.FallbackColour.Value;
            colour = Plugin.Config.ChatColours.TryGetValue(type, out var col) ? col : type.DefaultColor();
        }

        var push = colour != null;
        var uColor = push ? ColourUtil.RgbaToAbgr(colour!.Value) : 0;
        using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, uColor, push);

        var useCustomItalicFont = Plugin.Config.FontsEnabled && Plugin.FontManager.ItalicFont != null;
        if (text.Italic)
            (useCustomItalicFont ? Plugin.FontManager.ItalicFont! : Plugin.FontManager.AxisItalic).Push();

        // Check for contains here as sometimes there are multiple
        // TextChunks with the same PlayerPayload but only one has the name.
        // E.g. party chat with cross world players adds extra chunks.
        //
        // Note: This has been null before, I'm guessing due to some issues with
        // other plugins. New TextChunks will now enforce empty string in ctor,
        // but old ones may still be null.
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        var content = text.Content ?? "";
        if (ScreenshotMode)
        {
            if (chunk.Link is PlayerPayload playerPayload)
                content = HidePlayerInString(content, playerPayload.PlayerName, playerPayload.World.RowId);
            else if (Plugin.ObjectTable.LocalPlayer is { } player)
                content = HidePlayerInString(content, player.Name.TextValue, player.HomeWorld.RowId);
        }

        if (wrap)
        {
            ImGuiUtil.WrapText(content, chunk, handler, DefaultText, lineWidth);
        }
        else
        {
            ImGui.TextUnformatted(content);
            ImGuiUtil.PostPayload(chunk, handler);
        }

        if (text.Italic)
            (useCustomItalicFont ? Plugin.FontManager.ItalicFont! : Plugin.FontManager.AxisItalic).Pop();
    }

    internal void DrawIcon(Chunk chunk, IconChunk icon, PayloadHandler? handler)
    {
        if (!IconUtil.GfdFileView.TryGetEntry((uint) icon.Icon, out var entry))
            return;

        var iconTexture = Plugin.TextureProvider.GetFromGame("common/font/fonticon_ps5.tex").GetWrapOrDefault();
        if (iconTexture == null)
            return;

        var texSize = new Vector2(iconTexture.Width, iconTexture.Height);

        var sizeRatio = FontManager.GetFontSize() / entry.Height;
        var size = new Vector2(entry.Width, entry.Height) * sizeRatio * ImGuiHelpers.GlobalScale;

        var uv0 = new Vector2(entry.Left, entry.Top + 170) * 2 / texSize;
        var uv1 = new Vector2(entry.Left + entry.Width, entry.Top + entry.Height + 170) * 2 / texSize;

        ImGui.Image(iconTexture.Handle, size, uv0, uv1);
        ImGuiUtil.PostPayload(chunk, handler);

    }

    internal string HidePlayerInString(string str, string playerName, uint worldId)
    {
        var expected = Plugin.Functions.Chat.AbbreviatePlayerName(playerName);
        var hash = HashPlayer(playerName, worldId);
        return str.Replace(playerName, expected).Replace(expected, hash);
    }

    private string HashPlayer(string playerName, uint worldId)
    {
        var hashCode = $"{Salt}{playerName}{worldId}".GetHashCode();
        return $"Player {hashCode:X8}";
    }
}
