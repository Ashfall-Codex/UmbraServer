using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class WildRpAnnouncement
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(10)]
    public string UserUID { get; set; }

    [ForeignKey(nameof(UserUID))]
    public User Owner { get; set; }

    public uint WorldId { get; set; }

    public uint TerritoryId { get; set; }

    [MaxLength(60)]
    public string? CharacterName { get; set; }

    [MaxLength(200)]
    public string? Message { get; set; }

    public int? RpProfileId { get; set; }

    [ForeignKey(nameof(RpProfileId))]
    public CharacterRpProfileData? RpProfile { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}
