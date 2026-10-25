using System;
using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

/// <summary>
/// Affiliation d'un personnage (sa fiche RP) à un établissement dont il n'est pas forcément le propriétaire.
/// Demandée par le joueur, acceptée par le propriétaire ; une demande refusée est supprimée.
/// </summary>
public class EstablishmentAffiliation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstablishmentId { get; set; }
    public Establishment Establishment { get; set; }

    public int RpProfileId { get; set; }
    public CharacterRpProfileData RpProfile { get; set; }

    // Propriétaire de la fiche, dénormalisé pour les listes « mes affiliations » et la purge du compte.
    [Required]
    [MaxLength(10)]
    public string UserUID { get; set; }
    public User User { get; set; }

    public EstablishmentAffiliationStatus Status { get; set; }
    public bool ShowOnProfile { get; set; } = true;

    [MaxLength(200)]
    public string? Message { get; set; }

    public DateTime RequestedUtc { get; set; }
    public DateTime? RespondedUtc { get; set; }
}

public enum EstablishmentAffiliationStatus
{
    Pending,
    Accepted
}
