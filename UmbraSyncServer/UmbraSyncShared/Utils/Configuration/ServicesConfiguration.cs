using MareSynchronosShared.Utils;
using System.Text;

namespace MareSynchronosShared.Utils.Configuration;

public class ServicesConfiguration : MareConfigurationBase
{
    [SensitiveConfiguration]
    public string DiscordBotToken { get; set; } = string.Empty;
    public ulong? DiscordChannelForMessages { get; set; } = null;
    public ulong? DiscordChannelForReports { get; set; } = null;

    /// <summary>
    /// Identifiants Discord autorisés à diffuser une annonce à tous les clients (/broadcast).
    /// Volontairement distinct du flag IsAdmin en base : tant que cette liste est vide,
    /// la commande est refusée à tout le monde.
    /// </summary>
    public List<ulong> BroadcastAllowedDiscordIds { get; set; } = new();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(DiscordBotToken)} => ***");
        sb.AppendLine($"{nameof(MainServerAddress)} => {MainServerAddress}");
        sb.AppendLine($"{nameof(DiscordChannelForMessages)} => {DiscordChannelForMessages}");
        sb.AppendLine($"{nameof(DiscordChannelForReports)} => {DiscordChannelForReports}");
        sb.AppendLine($"{nameof(BroadcastAllowedDiscordIds)} => {string.Join(',', BroadcastAllowedDiscordIds)}");
        return sb.ToString();
    }
}