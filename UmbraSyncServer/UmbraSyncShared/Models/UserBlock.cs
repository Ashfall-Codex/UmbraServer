using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

/// <summary>
/// Blocage d'un utilisateur par un autre : aucune demande d'appairage n'est livrée entre eux, dans un sens comme dans l'autre.
/// </summary>
public class UserBlock
{
    [MaxLength(10)]
    public string UserUID { get; set; }
    public User User { get; set; }
    [MaxLength(10)]
    public string BlockedUserUID { get; set; }
    public User BlockedUser { get; set; }
    public DateTime CreatedAt { get; set; }
}
