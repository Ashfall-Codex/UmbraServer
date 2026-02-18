using UmbraSync.API.Data;
using UmbraSync.API.Dto.QuestSync;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private async Task<string?> GetUserQuestSession()
    {
        return await _redis.GetAsync<string>(QuestSessionUserKey).ConfigureAwait(false);
    }

    private async Task<List<string>> GetUsersInQuestSession(string sessionId, bool includeSelf = false)
    {
        var users = await _redis.GetAsync<List<string>>($"QuestSession:{sessionId}").ConfigureAwait(false);
        return users?.Where(u => includeSelf || !string.Equals(u, UserUID, StringComparison.Ordinal)).ToList() ?? [];
    }

    private async Task<string?> GetQuestSessionHost(string sessionId)
    {
        return await _redis.GetAsync<string>($"QuestSessionHost:{sessionId}").ConfigureAwait(false);
    }

    private async Task AddUserToQuestSession(string sessionId, List<string> priorUsers)
    {
        _mareMetrics.IncGauge(MetricsAPI.GaugeQuestSessionUsers);
        if (priorUsers.Count == 0)
            _mareMetrics.IncGauge(MetricsAPI.GaugeQuestSessions);

        await _redis.AddAsync(QuestSessionUserKey, sessionId).ConfigureAwait(false);
        await _redis.AddAsync($"QuestSession:{sessionId}", priorUsers.Concat([UserUID])).ConfigureAwait(false);
    }

    private async Task RemoveUserFromQuestSession(string sessionId, List<string> priorUsers)
    {
        await _redis.RemoveAsync(QuestSessionUserKey).ConfigureAwait(false);

        _mareMetrics.DecGauge(MetricsAPI.GaugeQuestSessionUsers);

        var host = await GetQuestSessionHost(sessionId).ConfigureAwait(false);
        var isHost = string.Equals(host, UserUID, StringComparison.Ordinal);

        if (priorUsers.Count == 1 || isHost)
        {
            await _redis.RemoveAsync($"QuestSession:{sessionId}").ConfigureAwait(false);
            await _redis.RemoveAsync($"QuestSessionHost:{sessionId}").ConfigureAwait(false);
            _mareMetrics.DecGauge(MetricsAPI.GaugeQuestSessions);

            priorUsers.Remove(UserUID);
            if (priorUsers.Count > 0)
            {
                foreach (var uid in priorUsers)
                {
                    await _redis.RemoveAsync($"QuestSessionUser:{uid}").ConfigureAwait(false);
                    _mareMetrics.DecGauge(MetricsAPI.GaugeQuestSessionUsers);
                }
                await Clients.Users(priorUsers).Client_QuestSessionLeave(new(UserUID)).ConfigureAwait(false);
            }
        }
        else
        {
            priorUsers.Remove(UserUID);
            await _redis.AddAsync($"QuestSession:{sessionId}", priorUsers).ConfigureAwait(false);
            await Clients.Users(priorUsers).Client_QuestSessionLeave(new(UserUID)).ConfigureAwait(false);
        }
    }

    private string QuestSessionUserKey => $"QuestSessionUser:{UserUID}";

    [Authorize(Policy = "Identified")]
    public async Task<string> QuestSessionCreate(string questId, string questName)
    {
        _logger.LogCallInfo(MareHubLogger.Args(questId, questName));
        var alreadyInSession = await GetUserQuestSession().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(alreadyInSession))
        {
            throw new HubException("Already in a Quest Session, cannot create another");
        }

        string sessionId = string.Empty;
        while (string.IsNullOrEmpty(sessionId))
        {
            sessionId = StringUtils.GenerateRandomString(30, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
            var result = await _redis.GetAsync<List<string>>($"QuestSession:{sessionId}").ConfigureAwait(false);
            if (result != null)
                sessionId = string.Empty;
        }

        await AddUserToQuestSession(sessionId, []).ConfigureAwait(false);
        await _redis.AddAsync($"QuestSessionHost:{sessionId}", UserUID).ConfigureAwait(false);

        return sessionId;
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserData>> QuestSessionJoin(string sessionId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(sessionId));
        var existingSessionId = await GetUserQuestSession().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existingSessionId))
            await QuestSessionLeave().ConfigureAwait(false);

        var sessionUsers = await GetUsersInQuestSession(sessionId).ConfigureAwait(false);
        if (!sessionUsers.Any())
            return [];

        await AddUserToQuestSession(sessionId, sessionUsers).ConfigureAwait(false);

        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        await Clients.Users(sessionUsers.Where(u => !string.Equals(u, UserUID, StringComparison.Ordinal)))
            .Client_QuestSessionJoin(user.ToUserData()).ConfigureAwait(false);

        var users = await DbContext.Users.Where(u => sessionUsers.Contains(u.UID))
            .Select(u => u.ToUserData())
            .ToListAsync()
            .ConfigureAwait(false);

        return users;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> QuestSessionLeave()
    {
        var sessionId = await GetUserQuestSession().ConfigureAwait(false);
        if (string.IsNullOrEmpty(sessionId))
            return true;

        _logger.LogCallInfo();

        var sessionUsers = await GetUsersInQuestSession(sessionId, true).ConfigureAwait(false);
        await RemoveUserFromQuestSession(sessionId, sessionUsers).ConfigureAwait(false);

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task QuestSessionPushState(QuestSessionStateDto state)
    {
        _logger.LogCallInfo(MareHubLogger.Args(state.QuestId, state.CurrentObjectiveIndex, state.CurrentEventIndex));
        var sessionId = await GetUserQuestSession().ConfigureAwait(false);
        if (string.IsNullOrEmpty(sessionId))
            return;

        var host = await GetQuestSessionHost(sessionId).ConfigureAwait(false);
        if (!string.Equals(host, UserUID, StringComparison.Ordinal))
            return;

        var sessionUsers = await GetUsersInQuestSession(sessionId).ConfigureAwait(false);
        await Clients.Users(sessionUsers).Client_QuestSessionStateUpdate(new(UserUID), state).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task QuestSessionTriggerEvent(QuestEventTriggerDto trigger)
    {
        _logger.LogCallInfo(MareHubLogger.Args(trigger.TriggerType, trigger.ObjectiveId));
        var sessionId = await GetUserQuestSession().ConfigureAwait(false);
        if (string.IsNullOrEmpty(sessionId))
            return;

        var sessionUsers = await GetUsersInQuestSession(sessionId, true).ConfigureAwait(false);
        var recipients = sessionUsers.Where(u => !string.Equals(u, UserUID, StringComparison.Ordinal)).ToList();
        if (recipients.Count > 0)
            await Clients.Users(recipients).Client_QuestSessionEventTriggered(new(UserUID), trigger).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task QuestSessionBranchingChoice(QuestBranchingChoiceDto choice)
    {
        _logger.LogCallInfo(MareHubLogger.Args(choice.ObjectiveId, choice.ChoiceIndex, choice.DiceRollResult));
        var sessionId = await GetUserQuestSession().ConfigureAwait(false);
        if (string.IsNullOrEmpty(sessionId))
            return;

        var host = await GetQuestSessionHost(sessionId).ConfigureAwait(false);
        if (!string.Equals(host, UserUID, StringComparison.Ordinal))
            return;

        var sessionUsers = await GetUsersInQuestSession(sessionId).ConfigureAwait(false);
        await Clients.Users(sessionUsers).Client_QuestSessionBranchingChoice(new(UserUID), choice).ConfigureAwait(false);
    }
}
