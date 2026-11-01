using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class FileCache
{
    [Key]
    [MaxLength(40)]
    public string Hash { get; set; }
    [MaxLength(10)]
    public string UploaderUID { get; set; }
    public User Uploader { get; set; }
    public bool Uploaded { get; set; }
    public DateTime UploadDate { get; set; }
    [Timestamp]
    public byte[] Timestamp { get; set; }
    public long Size { get; set; }
    public bool S3Confirmed { get; set; }
    public DateTime? S3ConfirmedAt { get; set; }
    // BLAKE3 du contenu décompressé, calculé par le serveur lui-même (upload, conversion BC7). null tant
    // qu'il ne l'a pas calculé : jamais rempli à partir de ce qu'annonce un client.
    [MaxLength(64)]
    public string Blake3Hash { get; set; }
}
