using System.Numerics;
using System.Runtime.InteropServices;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

public static class ModernUI
{
    private static ImRaii.Style? _modernStyleScope;
    
    internal static void BeginModernStyle(Configuration config)
    {
        if (!config.ModernUIEnabled)
            return;
            
        // Apply colors first for immediate visual feedback
        if (config.UseModernColors)
            ApplyModernColors(config);
            
        _modernStyleScope = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, config.UIRounding)
            .Push(ImGuiStyleVar.ChildRounding, config.UIRounding)
            .Push(ImGuiStyleVar.FrameRounding, config.UIRounding)
            .Push(ImGuiStyleVar.PopupRounding, config.UIRounding)
            .Push(ImGuiStyleVar.ScrollbarRounding, config.UIRounding)
            .Push(ImGuiStyleVar.GrabRounding, config.UIRounding)
            .Push(ImGuiStyleVar.TabRounding, config.UIRounding)
            .Push(ImGuiStyleVar.WindowBorderSize, config.UIBorderSize)
            .Push(ImGuiStyleVar.ChildBorderSize, config.UIBorderSize)
            .Push(ImGuiStyleVar.PopupBorderSize, config.UIBorderSize)
            .Push(ImGuiStyleVar.FrameBorderSize, config.UIBorderSize)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6))
            .Push(ImGuiStyleVar.ItemInnerSpacing, new Vector2(6, 4))
            .Push(ImGuiStyleVar.IndentSpacing, 20)
            .Push(ImGuiStyleVar.ScrollbarSize, 14)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(12, 12))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(8, 6))
            .Push(ImGuiStyleVar.CellPadding, new Vector2(8, 4));
    }
    
    internal static void EndModernStyle()
    {
        _modernStyleScope?.Dispose();
        _modernStyleScope = null;
    }
    internal static void ApplyModernStyle(Configuration config)
    {
        if (!config.ModernUIEnabled)
            return;

        var style = ImGui.GetStyle();
        
        // Store original values to avoid cumulative changes
        var originalWindowRounding = style.WindowRounding;
        var originalChildRounding = style.ChildRounding;
        var originalFrameRounding = style.FrameRounding;
        var originalPopupRounding = style.PopupRounding;
        var originalScrollbarRounding = style.ScrollbarRounding;
        var originalGrabRounding = style.GrabRounding;
        var originalTabRounding = style.TabRounding;
        
        // Modern rounded corners
        style.WindowRounding = config.UIRounding;
        style.ChildRounding = config.UIRounding;
        style.FrameRounding = config.UIRounding;
        style.PopupRounding = config.UIRounding;
        style.ScrollbarRounding = config.UIRounding;
        style.GrabRounding = config.UIRounding;
        style.TabRounding = config.UIRounding;
        
        // Modern borders
        style.WindowBorderSize = config.UIBorderSize;
        style.ChildBorderSize = config.UIBorderSize;
        style.PopupBorderSize = config.UIBorderSize;
        style.FrameBorderSize = config.UIBorderSize;
        
        // Better spacing for modern look
        style.ItemSpacing = new Vector2(8, 6);
        style.ItemInnerSpacing = new Vector2(6, 4);
        style.IndentSpacing = 20;
        style.ScrollbarSize = 14;
        
        // Modern padding
        style.WindowPadding = new Vector2(12, 12);
        style.FramePadding = new Vector2(8, 6);
        style.CellPadding = new Vector2(8, 4);
        
        if (config.UseModernColors)
            ApplyModernColors(config);
    }
    
    internal static void ApplyModernColors(Configuration config)
    {
        var colors = ImGui.GetStyle().Colors;
        
        // Modern dark theme with subtle accents - more aggressive application
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.11f, 0.11f, 0.13f, 0.95f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.13f, 0.13f, 0.15f, 0.8f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.09f, 0.09f, 0.11f, 0.98f);
        
        // Modern frame colors - more noticeable
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.25f, 0.25f, 0.28f, 0.9f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.30f, 0.30f, 0.35f, 1.0f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.35f, 0.35f, 0.42f, 1.0f);
        
        // Modern tab colors - more visible
        colors[(int)ImGuiCol.Tab] = new Vector4(0.20f, 0.20f, 0.25f, 0.9f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.30f, 0.30f, 0.38f, 1.0f);
        colors[(int)ImGuiCol.TabActive] = new Vector4(0.25f, 0.25f, 0.32f, 1.0f);
        colors[(int)ImGuiCol.TabUnfocused] = new Vector4(0.15f, 0.15f, 0.18f, 0.8f);
        colors[(int)ImGuiCol.TabUnfocusedActive] = new Vector4(0.20f, 0.20f, 0.25f, 0.9f);
        
        // Modern button colors - more prominent
        colors[(int)ImGuiCol.Button] = new Vector4(0.25f, 0.25f, 0.30f, 0.9f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.35f, 0.35f, 0.42f, 1.0f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.45f, 0.45f, 0.55f, 1.0f);
        
        // Modern header colors
        colors[(int)ImGuiCol.Header] = new Vector4(0.25f, 0.25f, 0.30f, 0.8f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.30f, 0.30f, 0.38f, 0.9f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.35f, 0.35f, 0.45f, 1.0f);
        
        // Modern scrollbar
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.10f, 0.10f, 0.12f, 0.6f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.30f, 0.30f, 0.35f, 0.8f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.35f, 0.35f, 0.42f, 0.9f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.40f, 0.40f, 0.50f, 1.0f);
        
        // Modern borders - more visible
        colors[(int)ImGuiCol.Border] = new Vector4(0.35f, 0.35f, 0.40f, 0.8f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0.0f, 0.0f, 0.0f, config.ShadowStrength);
        
        // Modern text selection
        colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.40f, 0.40f, 0.50f, 0.7f);
        
        // Modern title bar
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.15f, 0.15f, 0.18f, 0.9f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.20f, 0.20f, 0.25f, 1.0f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.12f, 0.12f, 0.15f, 0.7f);
    }
    
    internal static ImRaii.Style PushModernInputStyle(Configuration config)
    {
        if (!config.ModernUIEnabled)
            return new ImRaii.Style();
            
        return ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, config.UIRounding)
            .Push(ImGuiStyleVar.FrameBorderSize, config.UIBorderSize)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
    }
    
    internal static ImRaii.Style PushModernButtonStyle(Configuration config)
    {
        if (!config.ModernUIEnabled)
            return new ImRaii.Style();
            
        return ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, config.UIRounding)
            .Push(ImGuiStyleVar.FrameBorderSize, config.UIBorderSize)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(10, 6));
    }
    
    internal static ImRaii.Style PushModernTabStyle(Configuration config)
    {
        if (!config.ModernUIEnabled)
            return new ImRaii.Style();
            
        return ImRaii.PushStyle(ImGuiStyleVar.TabRounding, config.UIRounding);
    }
    
    internal static void DrawModernSeparator(Configuration config)
    {
        if (!config.ModernUIEnabled)
        {
            ImGui.Separator();
            return;
        }
        
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        
        var color = ImGui.GetColorU32(ImGuiCol.Border);
        drawList.AddLine(
            pos + new Vector2(0, 1),
            pos + new Vector2(size.X, 1),
            color,
            1.0f
        );
        
        ImGui.Dummy(new Vector2(0, 2));
    }
    
    internal static void DrawFloatingChannelIndicator(string channelName, Configuration config, float offsetX = 4.0f)
    {
        if (!config.ModernUIEnabled || !config.ShowFloatingChannelIndicator)
            return;
            
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(channelName);
        
        // Create a floating pill-shaped indicator with adjustable X offset
        var padding = new Vector2(8, 4);
        var pillSize = textSize + padding * 2;
        var pillPos = pos + new Vector2(offsetX, -pillSize.Y - 4);
        
        // Draw pill background
        var pillColor = ImGui.GetColorU32(new Vector4(0.2f, 0.4f, 0.8f, 0.8f));
        drawList.AddRectFilled(
            pillPos,
            pillPos + pillSize,
            pillColor,
            config.UIRounding
        );
        
        // Draw pill border
        var borderColor = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.9f, 1.0f));
        drawList.AddRect(
            pillPos,
            pillPos + pillSize,
            borderColor,
            config.UIRounding,
            ImDrawFlags.None,
            1.0f
        );
        
        // Draw text
        var textPos = pillPos + padding;
        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), channelName);
    }
    
    internal static void DrawTypingIndicator(bool isTyping, Configuration config)
    {
        if (!config.ModernUIEnabled || !config.ShowTypingIndicator || !isTyping)
            return;
            
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var time = (float)ImGui.GetTime();
        
        // Animated typing dots
        var dotSize = 3.0f;
        var dotSpacing = 8.0f;
        var basePos = pos + new Vector2(4, 4);
        
        for (int i = 0; i < 3; i++)
        {
            var dotPos = basePos + new Vector2(i * dotSpacing, 0);
            var animOffset = (float)Math.Sin(time * 3.0f + i * 0.5f) * 2.0f;
            dotPos.Y += animOffset;
            
            var alpha = 0.3f + 0.7f * (float)Math.Sin(time * 2.0f + i * 0.3f);
            var dotColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, alpha));
            
            drawList.AddCircleFilled(dotPos, dotSize, dotColor);
        }
    }
    
    internal static (ImRaii.Style, ImRaii.Color) PushEnhancedInputStyle(Configuration config, bool isFocused, bool hasError = false)
    {
        if (!config.ModernUIEnabled || !config.EnhancedInputFeedback)
            return (PushModernInputStyle(config), new ImRaii.Color());
            
        var borderColor = hasError 
            ? new Vector4(0.8f, 0.2f, 0.2f, 1.0f)  // Red for errors
            : isFocused 
                ? new Vector4(0.2f, 0.6f, 1.0f, 1.0f)  // Blue when focused
                : new Vector4(0.3f, 0.3f, 0.3f, 0.8f);  // Gray when not focused
                
        var bgColor = isFocused 
            ? new Vector4(0.15f, 0.15f, 0.20f, 1.0f)  // Slightly lighter when focused
            : new Vector4(0.12f, 0.12f, 0.15f, 1.0f);  // Normal background
            
        var styleScope = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, config.UIRounding + (isFocused ? 1.0f : 0.0f))
            .Push(ImGuiStyleVar.FrameBorderSize, config.UIBorderSize + (isFocused ? 0.5f : 0.0f))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
            
        var colorScope = ImRaii.PushColor(ImGuiCol.FrameBg, ImGui.GetColorU32(bgColor))
            .Push(ImGuiCol.Border, ImGui.GetColorU32(borderColor));
            
        return (styleScope, colorScope);
    }
    
    internal static void DrawModernTooltip(string text, Configuration config)
    {
        if (!config.ModernUIEnabled)
        {
            ImGuiUtil.Tooltip(text);
            return;
        }
        
        if (!ImGui.IsItemHovered())
            return;
            
        ImGui.SetNextWindowBgAlpha(0.95f);
        using var tooltip = ImRaii.Tooltip();
        if (!tooltip)
            return;
            
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, config.UIRounding)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
            
        ImGui.TextUnformatted(text);
    }
    
    internal static FontAwesomeIcon GetTabIcon(Tab tab)
    {
        // Determine the primary chat type for this tab
        var primaryType = ChatType.Say; // Default
        
        if (tab.ChatCodes.Count > 0)
        {
            // Get the first chat type as primary
            primaryType = tab.ChatCodes.Keys.First();
        }
        else if (tab.Channel.HasValue)
        {
            primaryType = tab.Channel.Value.ToChatType();
        }
        
        return primaryType switch
        {
            ChatType.Say => FontAwesomeIcon.Comment,
            ChatType.Shout => FontAwesomeIcon.Bullhorn,
            ChatType.Yell => FontAwesomeIcon.ExclamationTriangle,
            ChatType.TellIncoming or ChatType.TellOutgoing => FontAwesomeIcon.Envelope,
            ChatType.Party => FontAwesomeIcon.Users,
            ChatType.Alliance => FontAwesomeIcon.Shield,
            ChatType.FreeCompany => FontAwesomeIcon.Home,
            ChatType.Linkshell1 or ChatType.Linkshell2 or ChatType.Linkshell3 or ChatType.Linkshell4 or 
            ChatType.Linkshell5 or ChatType.Linkshell6 or ChatType.Linkshell7 or ChatType.Linkshell8 => FontAwesomeIcon.Link,
            ChatType.CrossLinkshell1 or ChatType.CrossLinkshell2 or ChatType.CrossLinkshell3 or ChatType.CrossLinkshell4 or
            ChatType.CrossLinkshell5 or ChatType.CrossLinkshell6 or ChatType.CrossLinkshell7 or ChatType.CrossLinkshell8 => FontAwesomeIcon.Globe,
            ChatType.NoviceNetwork => FontAwesomeIcon.Leaf,
            ChatType.Echo => FontAwesomeIcon.Terminal,
            ChatType.System => FontAwesomeIcon.Cog,
            ChatType.Debug => FontAwesomeIcon.Bug,
            ChatType.Urgent => FontAwesomeIcon.ExclamationCircle,
            ChatType.Notice => FontAwesomeIcon.InfoCircle,
            _ => FontAwesomeIcon.CommentDots
        };
    }
    
    internal static void DrawTabIcon(FontAwesomeIcon icon, Configuration config)
    {
        if (!config.ModernUIEnabled || !config.ShowTabIcons)
            return;
            
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var iconText = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconText);
        
        // Draw icon with slight spacing
        ImGui.TextUnformatted(iconText);
        ImGui.SameLine(0, 4); // Small spacing after icon
    }
    
    internal static unsafe bool HandleTabDragDrop(int tabIndex, List<Tab> tabs, Configuration config)
    {
        if (!config.ModernUIEnabled || !config.EnableTabDragReorder)
            return false;
            
        var moved = false;
        
        // Drag source
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
        {
            var indexBytes = BitConverter.GetBytes(tabIndex);
            ImGui.SetDragDropPayload("TAB_REORDER", indexBytes);
            ImGui.Text($"Moving: {tabs[tabIndex].Name}");
            
            // Show different hints based on modifier keys
            if (ImGui.GetIO().KeyCtrl)
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f), "Release to pop out tab");
            else
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Hold Ctrl while dragging to pop out");
                
            ImGui.EndDragDropSource();
        }
        
        // Drop target - only accept drops if not holding Ctrl (to allow popout)
        if (!ImGui.GetIO().KeyCtrl && ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("TAB_REORDER");
            // Add proper null checks to prevent crashes
            unsafe
            {
                if (payload.Handle != null && payload.Data != null && payload.DataSize == sizeof(int))
                {
                    var indexBytes = new byte[sizeof(int)];
                    Marshal.Copy((nint)payload.Data, indexBytes, 0, sizeof(int));
                    var sourceIndex = BitConverter.ToInt32(indexBytes, 0);
                    
                    if (sourceIndex != tabIndex && sourceIndex >= 0 && sourceIndex < tabs.Count)
                    {
                        // Move the tab
                        var tab = tabs[sourceIndex];
                        tabs.RemoveAt(sourceIndex);
                        
                        var insertIndex = sourceIndex < tabIndex ? tabIndex - 1 : tabIndex;
                        tabs.Insert(insertIndex, tab);
                        moved = true;
                    }
                }
            }
            ImGui.EndDragDropTarget();
        }
        
        return moved;
    }
    
    internal static unsafe bool CheckDragToPopout(List<Tab> tabs, Configuration config)
    {
        if (!config.ModernUIEnabled || !config.EnableTabDragReorder)
            return false;

        // Only check for popout if Ctrl is held
        if (!ImGui.GetIO().KeyCtrl)
            return false;

        // Check if we have an active drag payload
        var payload = ImGui.GetDragDropPayload();
        unsafe
        {
            if (payload.Handle == null || payload.Data == null || payload.DataSize != sizeof(int))
                return false;
        }

        // Check if mouse is released while holding Ctrl and we have a valid payload
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var indexBytes = new byte[sizeof(int)];
            Marshal.Copy((nint)payload.Data, indexBytes, 0, sizeof(int));
            var sourceIndex = BitConverter.ToInt32(indexBytes, 0);

            if (sourceIndex >= 0 && sourceIndex < tabs.Count)
            {
                // Pop out the tab
                tabs[sourceIndex].PopOut = true;
                return true;
            }
        }

        return false;
    }
    
    internal static void DrawInlineEmotePreview(string emoteName, Configuration config)
    {
        if (!config.ModernUIEnabled || !config.ShowInlineEmotePreviews)
            return;
            
        // Get emote from cache
        if (!EmoteCache.Exists(emoteName))
            return;
            
        var emote = EmoteCache.GetEmote(emoteName);
        if (emote == null || emote.Failed)
            return;
            
        // Draw small inline emote preview
        var previewSize = new Vector2(16, 16);
        
        if (emote.IsLoaded)
            emote.Draw(previewSize);
        else
            ImGui.Dummy(previewSize);
        
        // Add tooltip with emote name
        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            if (tooltip)
            {
                ImGui.Text($":{emoteName}:");
                if (emote.IsLoaded)
                    emote.Draw(new Vector2(32, 32));
            }
        }
    }
    
    internal static bool DrawEmotePickerButton(Configuration config)
    {
        if (!config.ModernUIEnabled || !config.EnableEmotePickerPopup)
            return false;
            
        using var style = PushModernButtonStyle(config);
        var clicked = ImGuiUtil.IconButton(FontAwesomeIcon.Smile, tooltip: "Open Emote Picker");
        
        return clicked;
    }
    
    internal static void DrawEmotePickerPopup(Configuration config, ref string searchFilter, ref int selectedCategory)
    {
        if (!config.ModernUIEnabled || !config.EnableEmotePickerPopup)
            return;
            
        var popupSize = new Vector2(400, 300) * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(popupSize);
        
        if (config.ModernUIEnabled)
        {
            ImGui.SetNextWindowBgAlpha(0.95f);
            using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, config.UIRounding)
                .Push(ImGuiStyleVar.WindowPadding, new Vector2(12, 12));
        }
        
        using var popup = ImRaii.Popup("EmotePickerPopup");
        if (!popup.Success)
            return;
            
        // Search bar
        if (config.EmotePickerSearchEnabled)
        {
            ImGui.Text("Search Emotes:");
            ImGui.SetNextItemWidth(-1);
            using (PushModernInputStyle(config))
            {
                ImGui.InputTextWithHint("##emote-search", "Type emote name...", ref searchFilter, 256);
            }
            ImGui.Spacing();
        }
        
        // Category tabs
        using (var tabBar = ImRaii.TabBar("##emote-categories"))
        {
            if (tabBar.Success)
            {
                var categories = new[] { "All", "BetterTTV", "7TV", "Recent" };
                
                for (int i = 0; i < categories.Length; i++)
                {
                    var flags = selectedCategory == i ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    using var tabItem = ImRaii.TabItem($"{categories[i]}###emote-cat-{i}", flags);
                    
                    if (tabItem.Success)
                    {
                        selectedCategory = i;
                        DrawEmoteGrid(config, searchFilter, selectedCategory);
                    }
                }
            }
        }
    }
    
    private static void DrawEmoteGrid(Configuration config, string searchFilter, int category)
    {
        if (EmoteCache.State != EmoteCache.LoadingState.Done)
        {
            ImGui.Text("Loading emotes...");
            return;
        }
            
        var emotes = EmoteCache.SortedCodeArray;
        if (emotes == null || emotes.Length == 0)
        {
            ImGui.Text("No emotes available");
            return;
        }
            
        // Filter emotes based on search and category
        var filteredEmotes = emotes.AsEnumerable();
        
        if (!string.IsNullOrEmpty(searchFilter))
        {
            filteredEmotes = filteredEmotes.Where(e => e.Contains(searchFilter, StringComparison.OrdinalIgnoreCase));
        }
        
        // Apply category filter (simplified for now - would need access to EmoteCache internals for proper filtering)
        switch (category)
        {
            case 3: // Recent (simplified - just show first 20)
                filteredEmotes = filteredEmotes.Take(20);
                break;
        }
        
        var emotesArray = filteredEmotes.ToArray();
        
        // Draw emote grid
        var emoteSize = new Vector2(32, 32);
        var spacing = 4.0f;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var emotesPerRow = Math.Max(1, (int)((availableWidth + spacing) / (emoteSize.X + spacing)));
        
        using var child = ImRaii.Child("##emote-grid", new Vector2(-1, -1));
        if (!child.Success)
            return;
            
        for (int i = 0; i < emotesArray.Length; i++)
        {
            var emoteName = emotesArray[i];
            var emote = EmoteCache.GetEmote(emoteName);
            
            if (emote == null || emote.Failed || !emote.IsLoaded)
                continue;
                
            if (i > 0 && i % emotesPerRow != 0)
                ImGui.SameLine(0, spacing);
                
            var cursorPos = ImGui.GetCursorScreenPos();
            
            // Draw emote button using a dummy button with custom drawing
            ImGui.PushID($"emote-{emoteName}");
            if (ImGui.Button("", emoteSize))
            {
                // Insert emote into chat - this would need to be handled by the calling code
                ImGui.CloseCurrentPopup();
                // Return the selected emote name somehow
            }
            
            // Draw the emote over the button
            var buttonMin = ImGui.GetItemRectMin();
            var buttonMax = ImGui.GetItemRectMax();
            var drawList = ImGui.GetWindowDrawList();
            
            // Calculate centered position for emote
            var emotePos = buttonMin + (buttonMax - buttonMin - emoteSize) * 0.5f;
            ImGui.SetCursorScreenPos(emotePos);
            emote.Draw(emoteSize);
            
            ImGui.PopID();
            
            // Tooltip with emote name
            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                if (tooltip)
                {
                    ImGui.Text($":{emoteName}:");
                    emote.Draw(new Vector2(48, 48));
                }
            }
        }
    }
    
    internal static void ApplySmoothTabTransition(Configuration config, bool isActive)
    {
        if (!config.ModernUIEnabled || !config.SmoothTabTransitions)
            return;
            
        var time = (float)ImGui.GetTime();
        var alpha = isActive ? 1.0f : 0.7f + 0.3f * (float)Math.Sin(time * 2.0f);
        
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.Text) & 0x00FFFFFF | ((uint)(alpha * 255) << 24));
    }
}