using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class StageShare
{
    [Key]
    public Guid Id { get; set; }
    [MaxLength(10)]
    public string OwnerUID { get; set; } = string.Empty;
    public User Owner { get; set; } = null!;
    [MaxLength(128)]
    public string StageName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int StageFormatVersion { get; set; }
    public long WorldId { get; set; }
    public int TerritoryId { get; set; }
    public int WardId { get; set; }
    public int DivisionId { get; set; }
    public int HouseId { get; set; }
    public int RoomId { get; set; }
    public byte[] CipherData { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] Tag { get; set; } = Array.Empty<byte>();
    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public ICollection<StageShareAllowedUser> AllowedIndividuals { get; set; } = new HashSet<StageShareAllowedUser>();
    public ICollection<StageShareAllowedGroup> AllowedSyncshells { get; set; } = new HashSet<StageShareAllowedGroup>();
}

public class StageShareAllowedUser
{
    public Guid ShareId { get; set; }
    public StageShare Share { get; set; } = null!;
    [MaxLength(10)]
    public string AllowedIndividualUid { get; set; } = string.Empty;
}

public class StageShareAllowedGroup
{
    public Guid ShareId { get; set; }
    public StageShare Share { get; set; } = null!;
    [MaxLength(20)]
    public string AllowedGroupGid { get; set; } = string.Empty;
}
