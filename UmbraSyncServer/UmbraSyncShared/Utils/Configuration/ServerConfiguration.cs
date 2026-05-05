using System.Text;

namespace MareSynchronosShared.Utils.Configuration;

public class ServerConfiguration : MareConfigurationBase
{
    [RemoteConfiguration]
    public Uri CdnFullUrl { get; set; } = null;

    [RemoteConfiguration]
    public Version ExpectedClientVersion { get; set; } = new Version(0, 0, 0);

    [RemoteConfiguration]
    public int MaxExistingGroupsByUser { get; set; } = 3;

    [RemoteConfiguration]
    public int MaxGroupUserCount { get; set; } = 100;

    // Default max users assigned to a newly created syncshell (per-group default)
    [RemoteConfiguration]
    public int DefaultGroupUserCount { get; set; } = 100;

    // Absolute ceiling enforced by the server; a group cannot exceed this value
    [RemoteConfiguration]
    public int AbsoluteMaxGroupUserCount { get; set; } = 200;

    [RemoteConfiguration]
    public int MaxJoinedGroupsByUser { get; set; } = 6;

    [RemoteConfiguration]
    public bool PurgeUnusedAccounts { get; set; } = false;

    [RemoteConfiguration]
    public int PurgeUnusedAccountsPeriodInDays { get; set; } = 14;

    [RemoteConfiguration]
    public int MaxCharaDataByUser { get; set; } = 30;
    
    [RemoteConfiguration]
    public bool BroadcastPresenceOnPermissionChange { get; set; } = false;
    public int HubExecutionConcurrencyLimit { get; set; } = 50;

    /// <summary>
    /// Padding bytes attached to each Client_KeepAlive push from the server.
    /// Default 256 — assez pour être compté comme du trafic applicatif par les
    /// middleboxes stateful sans gaspiller de la bande passante. Peut être bumpé à
    /// 2048+ via remote-config si on découvre un middlebox plus pointilleux.
    /// Set to 0 to disable padding (only the keep-alive frame envelope is sent).
    /// </summary>
    [RemoteConfiguration]
    public int KeepAlivePaddingBytes { get; set; } = 256;

    /// <summary>
    /// Interval between two keep-alive pushes per active connection. Default 5s.
    /// Should remain well below the most aggressive middlebox conntrack timeout
    /// observed in the wild for this hub (~14s on certain Free FR + AV combos).
    /// </summary>
    [RemoteConfiguration]
    public int KeepAliveIntervalSeconds { get; set; } = 5;
    
    public Uri ConnectBaseUrl { get; set; }
    [SensitiveConfiguration]
    public string ConnectServiceToken { get; set; } = string.Empty;
    
    [SensitiveConfiguration]
    public string ConnectIncomingServiceToken { get; set; } = string.Empty;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(CdnFullUrl)} => {CdnFullUrl}");
        sb.AppendLine($"{nameof(RedisConnectionString)} => {RedisConnectionString}");
        sb.AppendLine($"{nameof(ExpectedClientVersion)} => {ExpectedClientVersion}");
        sb.AppendLine($"{nameof(MaxExistingGroupsByUser)} => {MaxExistingGroupsByUser}");
        sb.AppendLine($"{nameof(MaxJoinedGroupsByUser)} => {MaxJoinedGroupsByUser}");
        sb.AppendLine($"{nameof(MaxGroupUserCount)} => {MaxGroupUserCount}");
        sb.AppendLine($"{nameof(DefaultGroupUserCount)} => {DefaultGroupUserCount}");
        sb.AppendLine($"{nameof(AbsoluteMaxGroupUserCount)} => {AbsoluteMaxGroupUserCount}");
        sb.AppendLine($"{nameof(PurgeUnusedAccounts)} => {PurgeUnusedAccounts}");
        sb.AppendLine($"{nameof(PurgeUnusedAccountsPeriodInDays)} => {PurgeUnusedAccountsPeriodInDays}");
        sb.AppendLine($"{nameof(MaxCharaDataByUser)} => {MaxCharaDataByUser}");
        sb.AppendLine($"{nameof(BroadcastPresenceOnPermissionChange)} => {BroadcastPresenceOnPermissionChange}");
        sb.AppendLine($"{nameof(HubExecutionConcurrencyLimit)} => {HubExecutionConcurrencyLimit}");
        sb.AppendLine($"{nameof(ConnectBaseUrl)} => {ConnectBaseUrl}");
        sb.AppendLine($"{nameof(ConnectServiceToken)} => ***");
        sb.AppendLine($"{nameof(ConnectIncomingServiceToken)} => ***");
        return sb.ToString();
    }
}