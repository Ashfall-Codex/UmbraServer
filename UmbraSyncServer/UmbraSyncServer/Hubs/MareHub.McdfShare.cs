using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronosServer.Services;
using UmbraSync.API.Dto.McdfShare;
using MareSynchronosServer.Utils;
using MareSynchronosShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    [Authorize(Policy = "Identified")]
    public async Task<List<McdfShareEntryDto>> McdfShareGetOwn()
    {
        _logger.LogCallInfo();

        var shares = await DbContext.McdfShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID == UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        return shares.Select(s => MapShareEntryDto(s, true)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<McdfShareEntryDto>> McdfShareGetShared()
    {
        _logger.LogCallInfo();

        var userGroups = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupUserUID == UserUID)
            .Select(p => p.GroupGID.ToUpperInvariant())
            .ToListAsync().ConfigureAwait(false);

        var shares = await DbContext.McdfShares.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Where(s => s.OwnerUID != UserUID)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync().ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var accessible = shares.Where(s => ShareAccessibleToUser(s, userGroups) && (!s.ExpiresAtUtc.HasValue || s.ExpiresAtUtc > now)).ToList();

        return accessible.Select(s => MapShareEntryDto(s, false)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> McdfShareUpload(McdfShareUploadRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));
        return await UpsertShareAsync(dto, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Upload en streaming : le header porte les métadonnées (CipherData vide), le chiffré arrive par morceaux.
    /// Chaque morceau est un message distinct, donc MaximumReceiveMessageSize ne borne plus la taille du MCDF.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> McdfShareUploadStream(McdfShareUploadRequestDto header, IAsyncEnumerable<byte[]> chunks)
    {
        _logger.LogCallInfo(MareHubLogger.Args(header.ShareId));

        if (header.Nonce is not { Length: 12 } || header.Tag is not { Length: 16 } || header.Salt is not { Length: > 0 })
        {
            return false;
        }

        var ct = Context.ConnectionAborted;
        var tempPath = _mcdfStorage.CreateTempPath(header.ShareId);
        var committed = false;
        try
        {
            long total = 0;
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (chunk == null || chunk.Length == 0) continue;

                    total += chunk.Length;
                    if (total > _mcdfStorage.MaxBytes)
                    {
                        _logger.LogCallWarning(MareHubLogger.Args(header.ShareId, "MCDF stream exceeds limit", _mcdfStorage.MaxBytes));
                        return false;
                    }

                    await fs.WriteAsync(chunk, ct).ConfigureAwait(false);
                }
            }

            if (total == 0) return false;

            committed = await UpsertShareAsync(header, new StagedMcdfFile(tempPath, total)).ConfigureAwait(false);
            return committed;
        }
        finally
        {
            if (!committed) _mcdfStorage.TryDelete(tempPath);
        }
    }

    private sealed record StagedMcdfFile(string TempPath, long Length);

    private async Task<bool> UpsertShareAsync(McdfShareUploadRequestDto dto, StagedMcdfFile? staged)
    {
        var normalizedUsers = (dto.AllowedIndividuals ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeUid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedGroups = (dto.AllowedSyncshells ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var share = await DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId)
            .ConfigureAwait(false);

        if (share != null && !string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal))
        {
            return false;
        }

        var wasFileBacked = share?.IsFileBacked ?? false;
        var now = DateTime.UtcNow;
        if (share == null)
        {
            share = new McdfShare
            {
                Id = dto.ShareId,
                OwnerUID = UserUID,
                CreatedUtc = now,
            };
            DbContext.McdfShares.Add(share);
        }

        share.Description = dto.Description ?? string.Empty;
        if (staged != null)
        {
            share.CipherData = Array.Empty<byte>();
            share.IsFileBacked = true;
            share.CipherLength = staged.Length;
        }
        else
        {
            share.CipherData = dto.CipherData ?? Array.Empty<byte>();
            share.IsFileBacked = false;
            share.CipherLength = share.CipherData.Length;
        }
        share.Nonce = dto.Nonce ?? Array.Empty<byte>();
        share.Salt = dto.Salt ?? Array.Empty<byte>();
        share.Tag = dto.Tag ?? Array.Empty<byte>();
        share.ExpiresAtUtc = dto.ExpiresAtUtc;
        share.UpdatedUtc = now;

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new McdfShareAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new McdfShareAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (staged != null)
        {
            _mcdfStorage.Commit(staged.TempPath, share.Id);
        }
        else if (wasFileBacked)
        {
            _mcdfStorage.Delete(share.Id);
        }

        // Notify recipients
        var recipientUids = new HashSet<string>(normalizedUsers, StringComparer.OrdinalIgnoreCase);
        foreach (var gid in normalizedGroups)
        {
            var members = await DbContext.GroupPairs
                .Where(p => p.GroupGID == gid)
                .Select(p => p.GroupUserUID)
                .ToListAsync().ConfigureAwait(false);
            foreach (var m in members) recipientUids.Add(m);
        }
        recipientUids.Remove(UserUID);

        if (recipientUids.Count > 0)
        {
            await Clients.Users(recipientUids.ToList())
                .Client_McdfShareReceived(UserUID, share.Description)
                .ConfigureAwait(false);
        }

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<McdfShareEntryDto?> McdfShareUpdate(McdfShareUpdateRequestDto dto)
    {
        _logger.LogCallInfo(MareHubLogger.Args(dto.ShareId));

        var share = await DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells)
            .Include(s => s.Owner)
            .SingleOrDefaultAsync(s => s.Id == dto.ShareId && s.OwnerUID == UserUID)
            .ConfigureAwait(false);

        if (share == null) return null;

        share.Description = dto.Description ?? string.Empty;
        share.ExpiresAtUtc = dto.ExpiresAtUtc;
        share.UpdatedUtc = DateTime.UtcNow;

        var normalizedUsers = dto.AllowedIndividuals
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeUid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedGroups = dto.AllowedSyncshells
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        share.AllowedIndividuals.Clear();
        foreach (var uid in normalizedUsers)
        {
            share.AllowedIndividuals.Add(new McdfShareAllowedUser
            {
                ShareId = share.Id,
                AllowedIndividualUid = uid,
            });
        }

        share.AllowedSyncshells.Clear();
        foreach (var gid in normalizedGroups)
        {
            share.AllowedSyncshells.Add(new McdfShareAllowedGroup
            {
                ShareId = share.Id,
                AllowedGroupGid = gid,
            });
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return MapShareEntryDto(share, true);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> McdfShareDelete(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await DbContext.McdfShares.SingleOrDefaultAsync(s => s.Id == shareId && s.OwnerUID == UserUID).ConfigureAwait(false);
        if (share == null) return false;

        var wasFileBacked = share.IsFileBacked;
        DbContext.McdfShares.Remove(share);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        if (wasFileBacked) _mcdfStorage.Delete(shareId);
        return true;
    }

    // En-dessous : le chiffré est renvoyé dans le payload (anciens clients compris). Au-dessus : McdfShareDownloadStream.
    private const long McdfInlineDownloadLimit = 90L * 1024 * 1024;
    private const int McdfStreamChunkSize = 1024 * 1024;

    private async Task<McdfShare?> GetAccessibleShareAsync(Guid shareId, bool track)
    {
        IQueryable<McdfShare> query = DbContext.McdfShares
            .Include(s => s.AllowedIndividuals)
            .Include(s => s.AllowedSyncshells);
        if (!track) query = query.AsNoTracking();

        var share = await query.SingleOrDefaultAsync(s => s.Id == shareId).ConfigureAwait(false);
        if (share == null) return null;

        var userGroups = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupUserUID == UserUID)
            .Select(p => p.GroupGID.ToUpperInvariant())
            .ToListAsync().ConfigureAwait(false);

        bool isOwner = string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal);
        if (!isOwner && (!ShareAccessibleToUser(share, userGroups) || (share.ExpiresAtUtc.HasValue && share.ExpiresAtUtc.Value <= DateTime.UtcNow)))
        {
            return null;
        }

        return share;
    }

    [Authorize(Policy = "Identified")]
    public async Task<McdfSharePayloadDto?> McdfShareDownload(Guid shareId)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await GetAccessibleShareAsync(shareId, track: true).ConfigureAwait(false);
        if (share == null) return null;

        byte[] cipher = share.CipherData;
        long cipherLength = share.IsFileBacked ? share.CipherLength : share.CipherData.LongLength;
        if (share.IsFileBacked)
        {
            var path = _mcdfStorage.GetPath(share.Id);
            if (!File.Exists(path))
            {
                _logger.LogCallWarning(MareHubLogger.Args(shareId, "MCDF file missing on disk"));
                return null;
            }

            cipher = cipherLength <= McdfInlineDownloadLimit
                ? await File.ReadAllBytesAsync(path).ConfigureAwait(false)
                : Array.Empty<byte>();
        }

        share.DownloadCount++;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return new McdfSharePayloadDto
        {
            ShareId = share.Id,
            Description = share.Description,
            CipherData = cipher,
            CipherLength = cipherLength,
            Nonce = share.Nonce,
            Salt = share.Salt,
            Tag = share.Tag,
            CreatedUtc = share.CreatedUtc,
            ExpiresAtUtc = share.ExpiresAtUtc,
        };
    }

    /// <summary>Télécharge le chiffré par morceaux (à appeler quand CipherLength > CipherData.Length). Ne compte pas comme un téléchargement.</summary>
    [Authorize(Policy = "Identified")]
    public async IAsyncEnumerable<byte[]> McdfShareDownloadStream(Guid shareId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogCallInfo(MareHubLogger.Args(shareId));

        var share = await GetAccessibleShareAsync(shareId, track: false).ConfigureAwait(false);
        if (share == null) yield break;

        if (!share.IsFileBacked)
        {
            for (int offset = 0; offset < share.CipherData.Length; offset += McdfStreamChunkSize)
            {
                var length = Math.Min(McdfStreamChunkSize, share.CipherData.Length - offset);
                yield return share.CipherData.AsSpan(offset, length).ToArray();
            }
            yield break;
        }

        var path = _mcdfStorage.GetPath(share.Id);
        if (!File.Exists(path))
        {
            _logger.LogCallWarning(MareHubLogger.Args(shareId, "MCDF file missing on disk"));
            yield break;
        }

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var buffer = new byte[McdfStreamChunkSize];
            int read = await fs.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0) yield break;
            if (read < buffer.Length) Array.Resize(ref buffer, read);
            yield return buffer;
        }
    }

    private static string NormalizeUid(string candidate) => candidate.Trim().ToUpperInvariant();
    private static string NormalizeGroup(string candidate) => candidate.Trim().ToUpperInvariant();

    private static McdfShareEntryDto MapShareEntryDto(McdfShare share, bool isOwner)
    {
        return new McdfShareEntryDto
        {
            Id = share.Id,
            Description = share.Description,
            CreatedUtc = share.CreatedUtc,
            UpdatedUtc = share.UpdatedUtc,
            ExpiresAtUtc = share.ExpiresAtUtc,
            DownloadCount = share.DownloadCount,
            IsOwner = isOwner,
            OwnerUid = share.OwnerUID,
            OwnerAlias = share.Owner?.Alias ?? string.Empty,
            AllowedIndividuals = share.AllowedIndividuals.Select(i => i.AllowedIndividualUid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedSyncshells = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            DataSize = share.IsFileBacked ? share.CipherLength : share.CipherData?.Length ?? 0,
        };
    }

    private bool ShareAccessibleToUser(McdfShare share, IReadOnlyCollection<string> userGroups)
    {
        if (string.Equals(share.OwnerUID, UserUID, StringComparison.Ordinal)) return true;

        bool allowedByUser = share.AllowedIndividuals.Any(i => string.Equals(i.AllowedIndividualUid, UserUID, StringComparison.OrdinalIgnoreCase));
        if (allowedByUser) return true;

        if (share.AllowedSyncshells.Count == 0) return false;
        var allowedGroups = share.AllowedSyncshells.Select(g => g.AllowedGroupGid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return userGroups.Any(g => allowedGroups.Contains(g));
    }
}
