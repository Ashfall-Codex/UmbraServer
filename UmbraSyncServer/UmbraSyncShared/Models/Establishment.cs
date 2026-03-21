using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class Establishment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(10)]
    public string OwnerUID { get; set; }

    [ForeignKey(nameof(OwnerUID))]
    public User Owner { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; }

    public string? Description { get; set; }

    public EstablishmentCategory Category { get; set; }

    public string[] Languages { get; set; } = ["FR"];

    public string[] Tags { get; set; } = [];

    [MaxLength(50)]
    public string? FactionTag { get; set; }

    [MaxLength(200)]
    public string? Schedule { get; set; }

    public bool IsPublic { get; set; } = true;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    // Location
    public EstablishmentLocationType LocationType { get; set; }
    public uint TerritoryId { get; set; }

    // Housing
    public ulong? ServerId { get; set; }
    public uint? WardId { get; set; }
    public uint? PlotId { get; set; }
    public uint? DivisionId { get; set; }
    public bool? IsApartment { get; set; }
    public uint? RoomId { get; set; }

    // Zone
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? Z { get; set; }
    public float? Radius { get; set; }

    // Images
    public string? LogoImageBase64 { get; set; }
    public string? BannerImageBase64 { get; set; }

    // Manager
    [MaxLength(10)]
    public string? ManagerUID { get; set; }
    public bool ShowManagerOnProfile { get; set; } = true;

    public ICollection<EstablishmentEvent> Events { get; set; } = [];
}

public enum EstablishmentLocationType
{
    Housing,
    Zone
}

public enum EstablishmentCategory
{
    Taverne,
    Boutique,
    Temple,
    Académie,
    Guilde,
    Résidence,
    Atelier,
    Autre
}
