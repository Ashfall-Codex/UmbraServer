using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto;
using UmbraSync.API.SignalR;
using MareSynchronosServer.Services;
using MareSynchronosServer.Services.AutoDetect;
using MareSynchronosServer.Utils;
using MareSynchronosShared;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis.Extensions.Core.Abstractions;

namespace MareSynchronosServer.Hubs;

[Authorize(Policy = "Authenticated")]
public partial class MareHub : Hub<IMareHub>, IMareHub
{
    private readonly MareMetrics _mareMetrics;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly MareHubLogger _logger;
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;
    private readonly int _defaultGroupUserCount;
    private readonly int _absoluteMaxGroupUserCount;
    private readonly IRedisDatabase _redis;
    private readonly GPoseLobbyDistributionService _gPoseLobbyDistributionService;
    private readonly OnlineSyncedPairCacheService _pairCacheService;
    private readonly Uri _fileServerAddress;
    private readonly Version _expectedClientVersion;
    private readonly int _maxCharaDataByUser;
    private readonly AutoDetectScheduleCache _autoDetectScheduleCache;
    private readonly bool _broadcastPresenceOnPermissionChange;

    private readonly Lazy<MareDbContext> _dbContextLazy;
    private MareDbContext DbContext => _dbContextLazy.Value;

    public MareHub(MareMetrics mareMetrics,
        IDbContextFactory<MareDbContext> mareDbContextFactory, ILogger<MareHub> logger, SystemInfoService systemInfoService,
        IConfigurationService<ServerConfiguration> configuration, IHttpContextAccessor contextAccessor,
        IRedisDatabase redisDb, GPoseLobbyDistributionService gPoseLobbyDistributionService,
        AutoDetectScheduleCache autoDetectScheduleCache, OnlineSyncedPairCacheService pairCacheService)
    {
        _mareMetrics = mareMetrics;
        _systemInfoService = systemInfoService;
        _shardName = configuration.GetValue<string>(nameof(ServerConfiguration.ShardName));
        _maxExistingGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3);
        _maxJoinedGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxJoinedGroupsByUser), 6);
        _maxGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 200);
        _defaultGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.DefaultGroupUserCount), 100);
        _absoluteMaxGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.AbsoluteMaxGroupUserCount), 200);
        _fileServerAddress = configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        _expectedClientVersion = configuration.GetValueOrDefault(nameof(ServerConfiguration.ExpectedClientVersion), new Version(0, 0, 0));
        _maxCharaDataByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxCharaDataByUser), 10);
        _contextAccessor = contextAccessor;
        _redis = redisDb;
        _gPoseLobbyDistributionService = gPoseLobbyDistributionService;
        _pairCacheService = pairCacheService;
        _autoDetectScheduleCache = autoDetectScheduleCache;
        _logger = new MareHubLogger(this, logger);
        _dbContextLazy = new Lazy<MareDbContext>(() => mareDbContextFactory.CreateDbContext());
        _broadcastPresenceOnPermissionChange = configuration.GetValueOrDefault(nameof(ServerConfiguration.BroadcastPresenceOnPermissionChange), false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DbContext.Dispose();
        }

        base.Dispose(disposing);
    }

    [Authorize(Policy = "Identified")]
    public async Task<ConnectionDto> GetConnectionDto()
    {
        _logger.LogCallInfo();

        _mareMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        await Clients.Caller.Client_UpdateSystemInfo(_systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var dbUser = DbContext.Users.SingleOrDefault(f => f.UID == UserUID);
        dbUser.LastLoggedIn = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Information, "Bienvenue sur UmbraSync ! Utilisateurs en ligne : " + _systemInfoService.SystemInfoDto.OnlineUsers).ConfigureAwait(false);

        return new ConnectionDto(new UserData(dbUser.UID, string.IsNullOrWhiteSpace(dbUser.Alias) ? null : dbUser.Alias))
        {
            CurrentClientVersion = _expectedClientVersion,
            ServerVersion = IMareHub.ApiVersion,
            IsAdmin = dbUser.IsAdmin,
            IsModerator = dbUser.IsModerator,
            ServerInfo = new ServerInfo()
            {
                MaxGroupsCreatedByUser = _maxExistingGroupsByUser,
                ShardName = _shardName,
                MaxGroupsJoinedByUser = _maxJoinedGroupsByUser,
                // Expose absolute max to the client (UI can cap sliders, etc.)
                MaxGroupUserCount = _absoluteMaxGroupUserCount,
                FileServerAddress = _fileServerAddress,
                MaxCharaData = _maxCharaDataByUser
            },
        };
    }

    [Authorize(Policy = "Authenticated")]
    public async Task<bool> CheckClientHealth()
    {
        try
        {
            var exists = await _redis.ExistsAsync("UID:" + UserUID).ConfigureAwait(false);
            if (!exists)
            {
                return false;
            }

            await UpdateUserOnRedis().ConfigureAwait(false);
            await _redis.AddAsync($"active:{UserCharaIdent}", Context.ConnectionId, expiresIn: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnConnectedAsync()
    {
        _mareMetrics.IncGaugeWithLabels(MetricsAPI.GaugeConnections, labels: Continent);

        try
        {
            _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress(), UserCharaIdent));

            await _pairCacheService.InitPlayer(UserUID).ConfigureAwait(false);
            await UpdateUserOnRedis().ConfigureAwait(false);
            try
            {
                await _redis.SetAddAsync($"connections:{UserCharaIdent}", Context.ConnectionId).ConfigureAwait(false);
                await _redis.AddAsync($"active:{UserCharaIdent}", Context.ConnectionId, expiresIn: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                var connections = await _redis.SetMembersAsync<string>($"connections:{UserCharaIdent}").ConfigureAwait(false);

                if (connections?.Length == 1)
                {
                    try
                    {
                        await SendOnlineToAllPairedUsers().ConfigureAwait(false);
                    }
                    catch { }
                }
                try
                {
                    var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
                    var onlinePairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);
                    foreach (var kvp in onlinePairs)
                    {
                        await Clients.Caller
                            .Client_UserSendOnline(new(new(kvp.Key), kvp.Value))
                            .ConfigureAwait(false);
                    }
                }
                catch { }
            }
            catch { }
        }
        catch { }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _mareMetrics.DecGaugeWithLabels(MetricsAPI.GaugeConnections, labels: Continent);

        try
        {
            _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress(), UserCharaIdent));
            if (exception != null)
                _logger.LogCallWarning(MareHubLogger.Args(_contextAccessor.GetIpAddress(), exception.Message, exception.StackTrace));

            try
            {
                await _redis.SetRemoveAsync($"connections:{UserCharaIdent}", Context.ConnectionId).ConfigureAwait(false);
                var connections = await _redis.SetMembersAsync<string>($"connections:{UserCharaIdent}").ConfigureAwait(false);
                if (connections == null || connections.Length == 0)
                {
                    var activeConnectionId = await _redis.GetAsync<string>($"active:{UserCharaIdent}").ConfigureAwait(false);
                    if (string.IsNullOrEmpty(activeConnectionId) || string.Equals(activeConnectionId, Context.ConnectionId, StringComparison.Ordinal))
                    {
                        await GposeLobbyLeave().ConfigureAwait(false);
                        await QuestSessionLeave().ConfigureAwait(false);
                        await RemoveUserFromRedis().ConfigureAwait(false);
                        await SendOfflineToAllPairedUsers().ConfigureAwait(false);
                        await _pairCacheService.DisposePlayer(UserUID).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogCallWarning(MareHubLogger.Args("ObsoleteConnectionId", Context.ConnectionId, "Active", activeConnectionId));
                    }
                }
            }
            catch { }

            _ = TypingGroupsByConnection.TryRemove(Context.ConnectionId, out _);
        }
        catch { }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
