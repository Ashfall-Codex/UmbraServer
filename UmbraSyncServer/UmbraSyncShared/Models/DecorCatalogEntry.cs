using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class DecorCatalogEntry
{
    [Key]
    public Guid Id { get; set; }
    [MaxLength(10)]
    public string OwnerUID { get; set; } = string.Empty;
    public User Owner { get; set; } = null!;
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PreviewImageBase64 { get; set; }
    [MaxLength(64)]
    public string? PreviewHash { get; set; }
    public int StageFormatVersion { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public float AnchorX { get; set; }
    public float AnchorY { get; set; }
    public float AnchorZ { get; set; }
    public int AnchorTerritory { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public int DisplayCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public ICollection<DecorCatalogFavorite> Favorites { get; set; } = new HashSet<DecorCatalogFavorite>();
    public ICollection<DecorCatalogReport> Reports { get; set; } = new HashSet<DecorCatalogReport>();
}

public class DecorCatalogFavorite
{
    public Guid EntryId { get; set; }
    public DecorCatalogEntry Entry { get; set; } = null!;
    [MaxLength(10)]
    public string UserUID { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public DateTime CreatedUtc { get; set; }
}

public class DecorCatalogReport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public Guid EntryId { get; set; }
    public DecorCatalogEntry Entry { get; set; } = null!;
    [MaxLength(10)]
    public string ReportingUserUID { get; set; } = string.Empty;
    public User ReportingUser { get; set; } = null!;
    public string Reason { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; }
}
