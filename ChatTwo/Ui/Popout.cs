using System.Numerics;
using System.Text;
using ChatTwo.Code;
using ChatTwo.DM;
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
using Lumina.Excel.Sheets;

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
            // BgAlpha is now set in Draw() method for proper focus-based transparency
            // Don't set it here as it would override the focus-based transparency
        }
    }

    public override void Draw()
    {
        using var id = ImRaii.PushId($"popout-{Tab.Identifier}");

        // Calculate UI alpha for transparency
        var isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var uiAlpha = isWindowFocused ? 1.0f : (Plugin.Config.UnfocusedTransparency / 100f);

        // Update focus state and apply transparency
        if (!ChatLogWindow.PopOutDocked[Idx])
        {
            var alpha = Tab.IndependentOpacity ? Tab.Opacity : Plugin.Config.WindowAlpha;
            
            if (!isWindowFocused)
            {
                var transparencyFactor = Plugin.Config.UnfocusedTransparency / 100f;
                BgAlpha = (alpha / 100f) * transparencyFactor;
            }
            else
            {
                BgAlpha = alpha / 100f;
            }
            
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

            DrawPopoutContent(uiAlpha);
        }
        else
        {
            DrawPopoutContent(uiAlpha);
        }

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            LastActivityTime = FrameTime;
    }
    
    private void DrawPopoutContent(float uiAlpha = 1.0f)
    {
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
        DrawPopoutInputArea(uiAlpha);
    }
    
    private void DrawPopoutInputArea(float uiAlpha = 1.0f)
    {
        ImGui.Separator();
        
        // Hide channel selector for DM tabs - they always send tells
        var isDMTab = Tab is DMTab;
        
        // Channel selector button (back to simple icon) - hidden for DM tabs
        var beforeIcon = ImGui.GetCursorPos();
        if (!isDMTab)
        {
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
                        {
                            // Set channel for this specific tab, not the main chat window
                            Tab.CurrentChannel.SetChannel(channel);
                        }
                }
            }

            ImGui.SameLine();
        }
        var afterIcon = ImGui.GetCursorPos();
        var buttonWidth = isDMTab ? 0 : afterIcon.X - beforeIcon.X;
        var inputWidth = ImGui.GetContentRegionAvail().X;

        // Input field - for DM tabs, always use TellOutgoing color
        var inputType = isDMTab ? ChatType.TellOutgoing : (Tab.CurrentChannel.UseTempChannel ? Tab.CurrentChannel.TempChannel.ToChatType() : Tab.CurrentChannel.Channel.ToChatType());
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
                var (styleScope, colorScope) = ModernUI.PushEnhancedInputStyle(Plugin.Config, PopoutInputFocused, hasError, uiAlpha);
                using (styleScope)
                using (colorScope)
                {
                    // Different placeholder for DM tabs to indicate direct messaging
                    var placeholder = isChatEnabled 
                        ? (isDMTab && Tab is DMTab dmTab ? $"Message {dmTab.Player.TabName}..." : "Type your message...") 
                        : Language.ChatLog_DisabledInput;
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
            }
        }
    }
    
    private void SendPopoutMessage()
    {
        if (string.IsNullOrWhiteSpace(PopoutChat))
            return;
            
        // Handle DM tabs specially in popout windows
        if (Tab is DMTab dmTab)
        {
            var trimmedMessage = PopoutChat.Trim();
            
            try
            {
                if (trimmedMessage.StartsWith('/'))
                {
                    // Commands are sent as-is (not as tells)
                    ChatBox.SendMessage(trimmedMessage);
                }
                else
                {
                    // Non-command messages are sent as tells to the DM target
                    var tellCommand = $"/tell {dmTab.Player.DisplayName} {trimmedMessage}";
                    ChatBox.SendMessage(tellCommand);
                    
                    // Display outgoing message in the DM tab
                    DisplayOutgoingMessageInPopout(dmTab, trimmedMessage);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to send DM message from popout to {dmTab.Player}: {ex.Message}");
                DisplayErrorMessageInPopout(dmTab, $"Failed to send message: {GetUserFriendlyErrorMessage(ex)}");
            }
            
            PopoutChat = string.Empty;
            return;
        }
            
        // For regular tabs, send using the tab's own channel state (same logic as main chat)
        try
        {
            var trimmed = PopoutChat.Trim();
            
            // If it's a command, send it directly
            if (trimmed.StartsWith('/'))
            {
                ChatBox.SendMessage(trimmed);
            }
            else
            {
                // Handle tell targets
                var target = Tab.CurrentChannel.TempTellTarget ?? Tab.CurrentChannel.TellTarget;
                if (target != null)
                {
                    // Use the same logic as main chat for tell targets
                    if (target.ContentId == 0)
                    {
                        trimmed = $"/tell {target.ToTargetString()} {trimmed}";
                        var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                        AutoTranslate.ReplaceWithPayload(ref tellBytes);
                        ChatBox.SendMessageUnsafe(tellBytes);
                    }
                    else
                    {
                        var reason = target.Reason;
                        var world = Sheets.WorldSheet.GetRow(target.World);
                        if (world is { IsPublic: true })
                        {
                            if (reason == TellReason.Reply && GameFunctions.GameFunctions.GetFriends().Any(friend => friend.ContentId == target.ContentId))
                                reason = TellReason.Friend;

                            var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                            AutoTranslate.ReplaceWithPayload(ref tellBytes);
                            ChatLogWindow.Plugin.Functions.Chat.SendTell(reason, target.ContentId, target.Name, (ushort) world.RowId, tellBytes, trimmed);
                        }
                    }
                }
                else
                {
                    // Use channel prefix (same logic as main chat)
                    if (Tab.CurrentChannel.UseTempChannel)
                        trimmed = $"{Tab.CurrentChannel.TempChannel.Prefix()} {trimmed}";
                    else
                        trimmed = $"{Tab.CurrentChannel.Channel.Prefix()} {trimmed}";
                    
                    var bytes = Encoding.UTF8.GetBytes(trimmed);
                    AutoTranslate.ReplaceWithPayload(ref bytes);
                    ChatBox.SendMessageUnsafe(bytes);
                }
            }
            
            // Reset temp channel and clear input (same as main chat)
            Tab.CurrentChannel.ResetTempChannel();
            PopoutChat = string.Empty;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to send message from popout: {ex.Message}");
        }
    }

    /// <summary>
    /// Displays an outgoing message in a DM tab from popout with appropriate styling.
    /// </summary>
    private void DisplayOutgoingMessageInPopout(DMTab dmTab, string messageContent)
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
            Plugin.Log.Error($"Failed to display outgoing message in DM popout: {ex.Message}");
        }
    }

    /// <summary>
    /// Displays an error message in a DM tab from popout.
    /// </summary>
    private void DisplayErrorMessageInPopout(DMTab dmTab, string errorText)
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
            Plugin.Log.Error($"Failed to display error message in DM popout: {ex.Message}");
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
