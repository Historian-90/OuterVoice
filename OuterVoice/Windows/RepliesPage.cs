using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OuterVoice.Windows;

/// <summary>
/// The Replies page: browse a friend's lines and write your own replies to them. A reply plays
/// right after their line finishes, said by your character.
/// </summary>
public sealed partial class ConfigWindow
{
    private string? replyFriend;
    private string replySearch = string.Empty;
    private string? replyLine; // "JOB/line id" of the friend's line being looked at

    private void DrawReplies()
    {
        var mine = plugin.Speaker.Pack;
        if (mine == null)
        {
            ImGui.TextWrapped("Load your own voice pack first (Voice Pack page): replies are saved in it.");
            return;
        }

        var friends = plugin.Speaker.Friends.Values
            .OrderBy(p => plugin.Speaker.DisplayName(p), StringComparer.OrdinalIgnoreCase).ToList();
        if (friends.Count == 0)
        {
            ImGui.TextWrapped("No friends' packs yet. Add a friend on the Friends page; once their pack has " +
                              "downloaded, their lines show up here and you can reply to them.");
            return;
        }

        var friend = friends.FirstOrDefault(p => p.Character.Equals(replyFriend, StringComparison.OrdinalIgnoreCase))
                     ?? friends[0];
        replyFriend = friend.Character;

        ImGui.BeginChild("##replylist", new Vector2(ListWidth, 0), true);
        DrawReplyList(mine, friend, friends);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##replydetail", Vector2.Zero, true);
        DrawReplyDetail(mine, friend);
        ImGui.EndChild();
    }

    private void DrawReplyList(VoicePack mine, VoicePack friend, List<VoicePack> friends)
    {
        ImGui.TextUnformatted("Show");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##replyfriend", plugin.Speaker.DisplayName(friend)))
        {
            foreach (var p in friends)
                if (ImGui.Selectable($"{plugin.Speaker.DisplayName(p)}  ({p.Character})##{p.Character}", p == friend))
                {
                    replyFriend = p.Character;
                    replyLine = null;
                }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##replysearch", "Search their lines...", ref replySearch, 100);

        ImGui.BeginChild("##replyrows", Vector2.Zero);
        var search = replySearch.Trim();
        var any = false;
        foreach (var ev in friend.AllEvents())
        {
            var lines = friend.AllLines.Where(l => l.Event == ev.Name && !string.IsNullOrWhiteSpace(l.Text))
                              .Where(l => search.Length == 0 ||
                                          l.Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                          ev.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(l => l.Job).ThenBy(l => l.Id).ToList();
            if (lines.Count == 0) continue;
            any = true;

            ImGui.Spacing();
            ImGui.TextColored(Accent, ev.Label.ToUpperInvariant());
            foreach (var l in lines)
            {
                var key = VoicePack.LineKeyOf(l);
                var replies = RepliesTo(mine, friend, key)?.Event is { } re
                    ? mine.AllLines.Count(x => x.Event == re) : 0;
                var label = l.Text.Length > 34 ? l.Text[..33] + "..." : l.Text;
                if (ImGui.Selectable($"{label}##{key}", replyLine == key))
                    replyLine = key;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{l.Text}\n{JobLabel(l.Job)}");
                if (replies > 0)
                {
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 24);
                    ImGui.TextColored(Good, $"{replies}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{replies} repl{(replies == 1 ? "y" : "ies")} from you");
                }
            }
        }
        if (!any)
            ImGui.TextColored(Muted, search.Length > 0 ? "No lines match." : "This pack has no lines with text yet.");
        ImGui.EndChild();
    }

    private static ReplyTo? RepliesTo(VoicePack mine, VoicePack friend, string key) =>
        mine.Replies.FirstOrDefault(r => r.To.Equals(friend.Character, StringComparison.OrdinalIgnoreCase) &&
                                         r.Line.Equals(key, StringComparison.OrdinalIgnoreCase));

    private void DrawReplyDetail(VoicePack mine, VoicePack friend)
    {
        var line = replyLine == null ? null
            : friend.AllLines.FirstOrDefault(l => VoicePack.LineKeyOf(l).Equals(replyLine, StringComparison.OrdinalIgnoreCase));
        if (line == null)
        {
            ImGui.TextColored(Muted, "Pick one of their lines on the left to reply to it.");
            ImGui.Spacing();
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Muted,
                "Your reply plays right after their line finishes, said by your character, whenever you're " +
                "nearby. Replies are saved in your pack, so friends who have your pack hear them too.");
            ImGui.PopTextWrapPos();
            return;
        }

        var ev = friend.AllEvents().FirstOrDefault(e => e.Name == line.Event);
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted(plugin.Speaker.DisplayName(friend));
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine();
        if (ImGui.Button("Play"))
            plugin.Speaker.PreviewLine(friend, line);
        ImGui.TextColored(Muted, $"{ev?.Label ?? line.Event}  >  {JobLabel(line.Job)}");
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted($"\"{line.Text}\"");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var key = VoicePack.LineKeyOf(line);
        var reply = RepliesTo(mine, friend, key);
        ImGui.TextUnformatted("Your replies");
        InfoMarker("One of these plays right after their line ends, when you're nearby.\n" +
                   "If you have several, one is picked at random (the same one for everyone).\n" +
                   "Other friends who have your pack hear your reply too.");

        if (reply != null)
        {
            var chance = reply.Chance;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Chance##replychance", ref chance, 1, 100, "%d%% of the time"))
            {
                reply.Chance = chance;
                Changed();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How often a reply follows their line. Lower it for lines that come up a lot.");

            var info = new KnownEvents.Info(reply.Event, $"Reply to {plugin.Speaker.DisplayName(friend)}", 2, 0,
                                            $"After {plugin.Speaker.DisplayName(friend)} says \"{line.Text}\"",
                                            "Replies");
            Line? toDelete = null;
            foreach (var mineLine in mine.AllLines.Where(l => l.Event == reply.Event).ToList())
                if (DrawLine(mine, mineLine, info))
                    toDelete = mineLine;
            if (toDelete != null)
            {
                if (mine.Jobs.TryGetValue(toDelete.Job, out var list)) list.Remove(toDelete);
                Changed();
            }
        }
        else
        {
            ImGui.TextColored(Muted, "No replies yet.");
        }

        ImGui.Spacing();
        if (ImGui.Button("+ Add reply"))
        {
            if (reply == null)
            {
                reply = new ReplyTo { To = friend.Character, Line = key, Text = line.Text };
                mine.Replies.Add(reply);
            }
            if (!mine.Jobs.TryGetValue(VoicePack.SharedJob, out var shared))
                mine.Jobs[VoicePack.SharedJob] = shared = new List<Line>();
            shared.Add(new Line
            {
                Id = mine.NextLineId(VoicePack.SharedJob, reply.Event),
                Event = reply.Event,
                Weight = 1,
                Job = VoicePack.SharedJob,
            });
            Changed();
        }
    }
}
