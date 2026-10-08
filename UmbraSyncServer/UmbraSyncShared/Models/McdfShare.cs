using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class McdfShare
{
    [Key]
    public Guid Id { get; set; }
    [MaxLength(10)]
    public string OwnerUID { get; set; } = string.Empty;
    public User Owner { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public byte[] CipherData { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] Tag { get; set; } = Array.Empty<byte>();
    // Partage volumineux : le chiffré est stocké sur disque ({McdfStorageDirectory}/{Id:N}.bin), CipherData reste vide
    public bool IsFileBacked { get; set; }
    public long CipherLength { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int DownloadCount { get; set; }
    public ICollection<McdfShareAllowedUser> AllowedIndividuals { get; set; } = new HashSet<McdfShareAllowedUser>();
    public ICollection<McdfShareAllowedGroup> AllowedSyncshells { get; set; } = new HashSet<McdfShareAllowedGroup>();
}

public class McdfShareAllowedUser
{
    public Guid ShareId { get; set; }
    public McdfShare Share { get; set; } = null!;
    [MaxLength(10)]
    public string AllowedIndividualUid { get; set; } = string.Empty;
}

public class McdfShareAllowedGroup
{
    public Guid ShareId { get; set; }
    public McdfShare Share { get; set; } = null!;
    [MaxLength(20)]
    public string AllowedGroupGid { get; set; } = string.Empty;
}
