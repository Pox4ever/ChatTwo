using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

internal class Popout : Window
{
    private readonly ChatLogWindow ChatLogWindow;
    private readonly Tab Tab;
    private readonly int Idx;

    private long FrameTime; // set every frame
    private long LastActivityTime = Environment.TickCount64;
    
    // Input state for popout
    private string PopoutChat = string.Empty;
    private bool PopoutInputFocused;

    public Popout(ChatLogWindow chatLogWindow, Tab tab, int idx) : base($"{tab.Name}##popout")
    {
        ChatLogWindow = chatLogWindow;
        Tab = tab;
        Idx = idx;

        Size = new Vector2(350, 350);
        SizeCondition = ImGuiCond.FirstUseEver;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override void PreOpenCheck()
    {
        if (!Tab.PopOut)
            IsOpen = false;
    }

    public override bool DrawConditions()
    {
        FrameTime = Environment.TickCount64;
        if (Tab.IndependentHide ? HideStateCheck() : ChatLogWindow.IsHidden)
            return false;

        if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) || !Tab.UnhideOnActivity)
        {
            LastActivityTime = FrameTime;
            return true;
        }

        // Activity in the tab, this popout window, or the main chat log window.
        var lastActivityTime = Math.Max(Tab.LastActivity, LastActivityTime);
        lastActivityTime = Math.Max(lastActivityTime, ChatLogWindow.LastActivityTime);
        return FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
    }

    public override void PreDraw()
    {
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();

        // Apply modern styling to popout windows
        ModernUI.BeginModernStyle(Plugin.Config);

        Flags = ImGuiWindowFlags.None;
        if (!Plugin.Config.ShowPopOutTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;

        if (!Tab.CanMove)
            Flags |= ImGuiWindowFlags.NoMove;

        if (!Tab.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;

        if (!ChatLogWindow.PopOutDocked[Idx])
        {
            var alpha = Tab.IndependentOpacity ? Tab.Opacity : Plugin.Config.WindowAlpha;
            BgAlpha = alpha / 100f;
        }
    }

    public override void Draw()
    {
        using var id = ImRaii.PushId($"popout-{Tab.Identifier}");

        if (!Plugin.Config.ShowPopOutTitleBar)
        {
            ImGui.TextUnformatted(Tab.Name);
            ImGui.Separator();
        }

        // Calculate space for input area (including typing indicator space)
        var inputHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2 + 16; // Extra space for typing indicator
        var messageLogHeight = ImGui.GetContentRegionAvail().Y - inputHeight;

        var handler = ChatLogWindow.HandlerLender.Borrow();
        ChatLogWindow.DrawMessageLog(Tab, handler, messageLogHeight, false);

        // Draw input area
        DrawPopoutInputArea();

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            LastActivityTime = FrameTime;
    }
    
    private void DrawPopoutInputArea()
    {
        ImGui.Separator();
        
        // Channel selector button (back to simple icon)
        var beforeIcon = ImGui.GetCursorPos();
        using (ModernUI.PushModernButtonStyle(Plugin.Config))
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Comment) && Tab.Channel is null)
                ImGui.OpenPopup("PopoutChannelPicker");
        }

        if (Tab.Channel is not null && ImGui.IsItemHovered())
            ModernUI.DrawModernTooltip(Language.ChatLog_SwitcherDisabled, Plugin.Config);

        using (var popup = ImRaii.Popup("PopoutChannelPicker"))
        {
            if (popup)
            {
                var channels = ChatLogWindow.GetValidChannels();
                foreach (var (name, channel) in channels)
                    if (ImGui.Selectable(name))
                        ChatLogWindow.SetChannel(channel);
            }
        }

        ImGui.SameLine();
        var afterIcon = ImGui.GetCursorPos();
        var buttonWidth = afterIcon.X - beforeIcon.X;
        var inputWidth = ImGui.GetContentRegionAvail().X;

        // Input field
        var inputType = Tab.CurrentChannel.UseTempChannel ? Tab.CurrentChannel.TempChannel.ToChatType() : Tab.CurrentChannel.Channel.ToChatType();
        var isCommand = PopoutChat.Trim().StartsWith('/');
        if (isCommand)
        {
            var command = PopoutChat.Split(' ')[0];
            if (ChatLogWindow.TextCommandChannels.TryGetValue(command, out var channel))
                inputType = channel;

            if (!ChatLogWindow.IsValidCommand(command))
                inputType = ChatType.Error;
        }

        var normalColor = ImGui.GetColorU32(ImGuiCol.Text);
        var inputColour = Plugin.Config.ChatColours.TryGetValue(inputType, out var inputCol) ? inputCol : inputType.DefaultColor();

        if (!isCommand && ChatLogWindow.Plugin.ExtraChat.ChannelOverride is var (_, overrideColour))
            inputColour = overrideColour;

        if (isCommand && ChatLogWindow.Plugin.ExtraChat.ChannelCommandColours.TryGetValue(PopoutChat.Split(' ')[0], out var ecColour))
            inputColour = ecColour;

        var push = inputColour != null;
        using (ImRaii.PushColor(ImGuiCol.Text, push ? ColourUtil.RgbaToAbgr(inputColour!.Value) : 0, push))
        {
            var isChatEnabled = Tab is { InputDisabled: false };
            
            var chatCopy = PopoutChat;
            using (ImRaii.Disabled(!isChatEnabled))
            {
                var flags = ImGuiInputTextFlags.EnterReturnsTrue | (!isChatEnabled ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
                ImGui.SetNextItemWidth(inputWidth);
                
                // Enhanced input field with better visual feedback
                var hasError = isCommand && !ChatLogWindow.IsValidCommand(PopoutChat.Split(' ')[0]);
                var (styleScope, colorScope) = ModernUI.PushEnhancedInputStyle(Plugin.Config, PopoutInputFocused, hasError);
                using (styleScope)
                using (colorScope)
                {
                    var placeholder = isChatEnabled ? "Type your message..." : Language.ChatLog_DisabledInput;
                    if (ImGui.InputTextWithHint("##popout-chat-input", placeholder, ref PopoutChat, 500, flags))
                    {
                        // Send message on Enter
                        if (!string.IsNullOrWhiteSpace(PopoutChat))
                        {
                            SendPopoutMessage();
                        }
                    }
                }
            }
            
            var inputActive = ImGui.IsItemActive();
            PopoutInputFocused = isChatEnabled && inputActive;

            // Draw typing indicator with proper positioning to avoid cutoff
            if (isChatEnabled && !string.IsNullOrEmpty(PopoutChat))
            {
                // Reserve space for typing indicator to prevent cutoff
                var typingHeight = 12.0f; // Height needed for typing dots
                var currentPos = ImGui.GetCursorPos();
                ImGui.SetCursorPos(currentPos + new Vector2(8, 4)); // Offset from left edge
                ModernUI.DrawTypingIndicator(true, Plugin.Config);
                ImGui.SetCursorPos(currentPos + new Vector2(0, typingHeight)); // Move cursor down to reserve space
            }

            if (ImGui.IsItemDeactivated())
            {
                if (ImGui.IsKeyDown(ImGuiKey.Escape))
                {
                    PopoutChat = chatCopy;

                    if (Tab.CurrentChannel.UseTempChannel)
                    {
                        Tab.CurrentChannel.ResetTempChannel();
                        ChatLogWindow.SetChannel(Tab.CurrentChannel.Channel);
                    }
                }
            }

            // Process keybinds that have modifiers while the chat is focused.
            if (inputActive)
            {
                ChatLogWindow.Plugin.Functions.KeybindManager.HandleKeybinds(KeyboardSource.ImGui, true, true);
                LastActivityTime = FrameTime;
            }

            // Reset temp channel when unfocused
            if (!inputActive && Tab.CurrentChannel.UseTempChannel)
            {
                Tab.CurrentChannel.ResetTempChannel();
                ChatLogWindow.SetChannel(Tab.CurrentChannel.Channel);
            }
        }
    }
    
    private void SendPopoutMessage()
    {
        if (string.IsNullOrWhiteSpace(PopoutChat))
            return;
            
        // Use the main chat window's send functionality
        var originalChat = ChatLogWindow.Chat;
        ChatLogWindow.Chat = PopoutChat;
        
        try
        {
            ChatLogWindow.SendChatBox(Tab);
            PopoutChat = string.Empty; // Clear input after sending
        }
        finally
        {
            ChatLogWindow.Chat = originalChat; // Restore original chat
        }
    }

    public override void PostDraw()
    {
        ChatLogWindow.PopOutDocked[Idx] = ImGui.IsWindowDocked();

        // End modern styling
        ModernUI.EndModernStyle();

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();
    }

    public override void OnClose()
    {
        ChatLogWindow.PopOutWindows.Remove(Tab.Identifier);
        ChatLogWindow.Plugin.WindowSystem.RemoveWindow(this);

        Tab.PopOut = false;
        ChatLogWindow.Plugin.SaveConfig();
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

    private bool HideStateCheck()
    {
        // if the chat has no hide state set, and the player has entered battle, we hide chat if they have configured it
        if (Tab.HideInBattle && CurrentHideState == HideState.None && Plugin.InBattle)
            CurrentHideState = HideState.Battle;

        // If the chat is hidden because of battle, we reset it here
        if (CurrentHideState is HideState.Battle && !Plugin.InBattle)
            CurrentHideState = HideState.None;

        // if the chat has no hide state and in a cutscene, set the hide state to cutscene
        if (Tab.HideDuringCutscenes && CurrentHideState == HideState.None && (Plugin.CutsceneActive || Plugin.GposeActive))
        {
            if (ChatLogWindow.Plugin.Functions.Chat.CheckHideFlags())
                CurrentHideState = HideState.Cutscene;
        }

        // if the chat is hidden because of a cutscene and no longer in a cutscene, set the hide state to none
        if (CurrentHideState is HideState.Cutscene or HideState.CutsceneOverride && !Plugin.CutsceneActive && !Plugin.GposeActive)
            CurrentHideState = HideState.None;

        // if the chat is hidden because of a cutscene and the chat has been activated, show chat
        if (CurrentHideState == HideState.Cutscene && ChatLogWindow.Activate)
            CurrentHideState = HideState.CutsceneOverride;

        // if the user hid the chat and is now activating chat, reset the hide state
        if (CurrentHideState == HideState.User && ChatLogWindow.Activate)
            CurrentHideState = HideState.None;

        return CurrentHideState is HideState.Cutscene or HideState.User or HideState.Battle || (Tab.HideWhenNotLoggedIn && !Plugin.ClientState.IsLoggedIn);
    }
}
