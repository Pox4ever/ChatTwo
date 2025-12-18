using System.Numerics;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

internal sealed class Emote : ISettingsTab
{
    private readonly Plugin Plugin;
    private Configuration Mutable { get; }

    public string Name => Language.Options_Emote_Tab + "###tabs-emote";

    private static SearchSelector.SelectorPopupOptions? WordPopupOptions;

    internal Emote(Plugin plugin, Configuration mutable)
    {
        Plugin = plugin;
        Mutable = mutable;

        WordPopupOptions = new SearchSelector.SelectorPopupOptions
        {
            FilteredSheet = EmoteCache.SortedCodeArray.Where(w => !Mutable.BlockedEmotes.Contains(w)).ToArray()
        };
    }

    private SearchSelector.SelectorPopupOptions RefillSheet()
    {
        return new SearchSelector.SelectorPopupOptions
        {
            FilteredSheet = EmoteCache.SortedCodeArray.Where(w => !Mutable.BlockedEmotes.Contains(w)).ToArray()
        };
    }

    public void Draw(bool changed)
    {
        using var wrap = ImGuiUtil.TextWrapPos();

        ImGuiUtil.OptionCheckbox(ref Mutable.ShowEmotes, Language.Options_ShowEmotes_Name, Language.Options_ShowEmotes_Desc);
        
        if (Mutable.ShowEmotes)
        {
            ImGui.Indent();
            
            var betterTTVChanged = ImGuiUtil.OptionCheckbox(ref Mutable.EnableBetterTTVEmotes, "Enable BetterTTV Emotes", "Load emotes from BetterTTV (includes global and popular emotes)");
            var sevenTVChanged = ImGuiUtil.OptionCheckbox(ref Mutable.EnableSevenTVEmotes, "Enable 7TV Emotes", "Load emotes from 7TV (includes global and channel emotes)");
            
            ImGui.Separator();
            
            // Emote size configuration
            ImGui.TextUnformatted("Emote Size:");
            ImGui.SetNextItemWidth(200);
            var emoteSizeChanged = ImGui.SliderFloat("##EmoteSize", ref Mutable.EmoteSize, 0.5f, 3.0f, "%.1fx");
            ImGuiUtil.HelpText("Adjust the size of emotes in chat (0.5x = half size, 1.0x = normal, 3.0x = triple size)");
            
            if (Mutable.EnableSevenTVEmotes)
            {
                ImGui.Indent();
                
                // Simple Configuration
                ImGui.TextUnformatted("Twitch User IDs:");
                ImGui.SetNextItemWidth(300);
                var twitchUserIdChanged = ImGui.InputText("##TwitchUserIds", ref Mutable.TwitchUserIds, 200);
                ImGuiUtil.HelpText("Enter your Twitch user ID(s) separated by commas (e.g. \"410517388,123456789\")");
                
                if (ImGui.Button("🔍 Find My Twitch User ID"))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://www.streamweasels.com/tools/convert-twitch-username-to-user-id/",
                        UseShellExecute = true
                    });
                }
                ImGui.SameLine();
                ImGuiUtil.HelpText("Opens a webpage where you can convert your Twitch username to a user ID");
                
                var twitchUsernameChanged = false;
                
                var totalConfigured = 0;
                if (!string.IsNullOrWhiteSpace(Mutable.TwitchUserIds))
                {
                    totalConfigured = Mutable.TwitchUserIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
                }
                
                if (totalConfigured > 0)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f), $"✓ Will load 7TV emotes from {totalConfigured} streamer(s)");
                }
                else
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1f), "⚠ Enter your Twitch user ID to load 7TV emotes");
                }
                
                if (twitchUsernameChanged || twitchUserIdChanged)
                    sevenTVChanged = true;
                
                ImGui.Unindent();
            }
            
            if (betterTTVChanged || sevenTVChanged || emoteSizeChanged)
            {
                // Reload emotes when source selection changes
                if (betterTTVChanged || sevenTVChanged)
                {
                    EmoteCache.State = EmoteCache.LoadingState.Unloaded;
                    Task.Run(EmoteCache.LoadData);
                }
            }
        }
        
        ImGui.Spacing();

        ImGui.TextUnformatted(Language.Options_Emote_BlockedEmotes);
        ImGui.Spacing();

        WordPopupOptions ??= RefillSheet();
        if (EmoteCache.State is EmoteCache.LoadingState.Done && WordPopupOptions.FilteredSheet.Length == 0)
            WordPopupOptions = RefillSheet();

        var buttonWidth = ImGui.GetContentRegionAvail().X / 3;
        using (Plugin.FontManager.FontAwesome.Push())
            ImGui.Button(FontAwesomeIcon.Plus.ToIconString(), new Vector2(buttonWidth, 0));

        if (SearchSelector.SelectorPopup("WordAddPopup", out var newWord, WordPopupOptions))
            Mutable.BlockedEmotes.Add(newWord);

        using(var table = ImRaii.Table("##BlockedWords", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInner))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Language.Options_Emote_EmoteTable);
                ImGui.TableSetupColumn("##Del", ImGuiTableColumnFlags.WidthStretch, 0.07f);

                ImGui.TableHeadersRow();

                var copiedList = Mutable.BlockedEmotes.ToArray();
                foreach (var word in copiedList)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(word);

                    ImGui.TableNextColumn();
                    if (ImGuiUtil.Button($"##{word}Del", FontAwesomeIcon.Trash, !ImGui.GetIO().KeyCtrl))
                        Mutable.BlockedEmotes.Remove(word);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Language.Options_Emote_EmoteStats);
        ImGui.Spacing();

        if (EmoteCache.State is EmoteCache.LoadingState.Done)
            ImGui.TextColored(ImGuiColors.HealerGreen, Language.Options_Emote_Ready);
        else
            ImGui.TextColored(ImGuiColors.DPSRed, Language.Options_Emote_NotReady);

        ImGui.TextUnformatted($"{Language.Options_Emote_Loaded} {EmoteCache.SortedCodeArray.Length}");
        using (var emoteTable = ImRaii.Table("##LoadedEmotes", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInner))
        {
            if (emoteTable)
            {
                ImGui.TableSetupColumn("##word1");
                ImGui.TableSetupColumn("##word2");
                ImGui.TableSetupColumn("##word3");
                ImGui.TableSetupColumn("##word4");
                ImGui.TableSetupColumn("##word5");

                foreach (var word in EmoteCache.SortedCodeArray)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(word);
                }
            }
        }
    }
}
