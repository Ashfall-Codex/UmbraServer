using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class GroupProfile
{
    [Key]
    [MaxLength(20)]
    public string GroupGID { get; set; } = string.Empty;

    public Group Group { get; set; } = null!;

    public string? Description { get; set; }

    public string[]? Tags { get; set; }

    public string? Base64ProfileImage { get; set; }

    public string? Base64BannerImage { get; set; }

    public bool IsNSFW { get; set; }

    public bool IsDisabled { get; set; }

    [MaxLength(9)]
    public string? BorderColor { get; set; }
}
