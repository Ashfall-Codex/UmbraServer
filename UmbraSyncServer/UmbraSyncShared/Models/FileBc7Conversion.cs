using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public enum Bc7TextureRole
{
    Unknown = 0,
    Diffuse = 1,
    Normal = 2,
    Mask = 3,
    Other = 4,
}

public enum Bc7ConversionState
{
    Pending = 0,
    Converted = 1,
    Skipped = 2,
    Failed = 3,
}

public class FileBc7Conversion
{
    [Key]
    [MaxLength(40)]
    public string SourceHash { get; set; }
    // Rôle de la texture, déterminé au hub à partir des GamePaths (seul endroit qui le connaît).
    public Bc7TextureRole Role { get; set; }
    public Bc7ConversionState State { get; set; }
    // Hash SHA1 du blob BC7 converti. null tant que State != Converted.
    [MaxLength(40)]
    public string? AlternateHash { get; set; }
    public DateTime UpdatedAt { get; set; }
}
