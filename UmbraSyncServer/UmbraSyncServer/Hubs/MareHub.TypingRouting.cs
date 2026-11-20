using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MareSynchronosServer.Utils;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    // Track typing-related SignalR group memberships per connection
    private static readonly ConcurrentDictionary<string, HashSet<string>> TypingGroupsByConnection = new(StringComparer.Ordinal);

    private const int MaxTypingChannelIdLength = 64;
    private const int MaxTypingChannelsPerList = 8;

    private static string BuildTypingGroupName(TypingScope scope, string id)
        => $"typing:{scope}:{id}";

    private static string? NormalizeTypingChannelId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        string trimmed = id.Trim();
        if (trimmed.Length > MaxTypingChannelIdLength) return null;

        foreach (char c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not (':' or '-' or '_')) return null;
        }

        return trimmed.ToUpperInvariant();
    }

    private static void AddTypingGroup(HashSet<string> groups, TypingScope scope, string? id)
    {
        var normalized = NormalizeTypingChannelId(id);
        if (normalized != null) groups.Add(BuildTypingGroupName(scope, normalized));
    }

    [Authorize(Policy = "Identified")]
    public async Task UserUpdateTypingChannels(TypingChannelsDto channels)
    {
        _logger.LogCallInfo(MareHubLogger.Args("UpdateTypingChannels"));

        if (channels == null) return;

        var desired = new HashSet<string>(StringComparer.Ordinal);

        AddTypingGroup(desired, TypingScope.Party, channels.PartyId);
        AddTypingGroup(desired, TypingScope.Alliance, channels.AllianceId);
        AddTypingGroup(desired, TypingScope.FreeCompany, channels.FreeCompanyId);
        foreach (var id in (channels.CrossPartyIds ?? []).Take(MaxTypingChannelsPerList))
            AddTypingGroup(desired, TypingScope.CrossParty, id);
        foreach (var id in (channels.CustomGroupIds ?? []).Take(MaxTypingChannelsPerList))
            AddTypingGroup(desired, TypingScope.Unknown, id);

        var connId = Context.ConnectionId;
        var current = TypingGroupsByConnection.GetOrAdd(connId, _ => new HashSet<string>(StringComparer.Ordinal));

        // Leave groups not desired anymore
        foreach (var grp in current.Where(g => !desired.Contains(g)).ToList())
        {
            await Groups.RemoveFromGroupAsync(connId, grp).ConfigureAwait(false);
            current.Remove(grp);
        }

        // Join new groups
        foreach (var grp in desired.Where(g => !current.Contains(g)))
        {
            await Groups.AddToGroupAsync(connId, grp).ConfigureAwait(false);
            current.Add(grp);
        }

        _logger.LogCallInfo(MareHubLogger.Args("TypingChannelsUpdated", $"groups={current.Count}"));
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSetTypingStateEx(TypingStateExDto dto)
    {
        if (dto == null) return;

        _logger.LogCallInfo(MareHubLogger.Args("TypingEx", dto.IsTyping, dto.Scope));

        var sender = await DbContext.Users.AsNoTracking().SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        var scope = Enum.IsDefined(typeof(TypingScope), dto.Scope) ? dto.Scope : TypingScope.Unknown;
        var typingDto = new TypingStateDto(sender.ToUserData(), dto.IsTyping, scope);

        // Always echo to caller
        await Clients.Caller.Client_UserTypingState(typingDto).ConfigureAwait(false);

        switch (scope)
        {
            case TypingScope.Whisper:
            {
                if (string.IsNullOrWhiteSpace(dto.TargetUid)) return;

                var paired = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);
                var target = paired.FirstOrDefault(p => string.Equals(p.UID, dto.TargetUid, StringComparison.Ordinal));
                if (target == default || target.IsPaused) return;

                await Clients.User(dto.TargetUid!).Client_UserTypingState(typingDto).ConfigureAwait(false);
                break;
            }
            case TypingScope.Party:
            case TypingScope.Alliance:
            case TypingScope.FreeCompany:
            case TypingScope.CrossParty:
            {
                var channelId = NormalizeTypingChannelId(dto.ChannelId);
                if (channelId == null) return;
                var grp = BuildTypingGroupName(scope, channelId);
                await Clients.GroupExcept(grp, new[] { Context.ConnectionId }).Client_UserTypingState(typingDto).ConfigureAwait(false);
                break;
            }
            case TypingScope.Proximity:
            case TypingScope.Unknown:
            default:
            {
                // Fallback: route to paired, unpaused users (legacy behavior)
                var paired = await GetAllPairedClientsWithPauseState().ConfigureAwait(false);
                var recipients = paired.Where(p => !p.IsPaused).Select(p => p.UID).Distinct(StringComparer.Ordinal).ToList();
                if (recipients.Count == 0) return;
                await Clients.Users(recipients).Client_UserTypingState(typingDto).ConfigureAwait(false);
                break;
            }
        }
    }
}
