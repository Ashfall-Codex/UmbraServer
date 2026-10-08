using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using UmbraSync.API.Data.Enum;

namespace MareSynchronosShared.Models;

public class CharacterRpProfileData
{
    [Key]
    public int Id { get; set; }

    public string UserUID { get; set; }

    [ForeignKey(nameof(UserUID))]
    public User User { get; set; }

    public string CharacterName { get; set; }
    public uint WorldId { get; set; }

    public string? RpProfilePictureBase64 { get; set; }
    public string? RpDescription { get; set; }
    public bool IsRpNSFW { get; set; }
    public string? RpFirstName { get; set; }
    public string? RpLastName { get; set; }
    public string? RpTitle { get; set; }
    public string? RpAge { get; set; }
    public string? RpRace { get; set; }
    public string? RpEthnicity { get; set; }
    public string? RpHeight { get; set; }
    public string? RpBuild { get; set; }
    public string? RpResidence { get; set; }
    public string? RpOccupation { get; set; }
    public string? RpAffiliation { get; set; }
    public string? RpAlignment { get; set; }
    public string? RpAdditionalInfo { get; set; }
    public string? RpNameColor { get; set; }
    public string? RpCustomFields { get; set; }
    public string? MoodlesData { get; set; }
    public ushort ChatIcon { get; set; }
    public byte RpLevel { get; set; }
    public string? EnrichedProfileJson { get; set; }
    public string? EnrichedProfileVisibility { get; set; }
    public RpProfileVisibility Visibility { get; set; }
}
