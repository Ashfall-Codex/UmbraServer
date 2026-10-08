using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.Slot;
using UmbraSync.API.Dto.WildRp;
using MareSynchronosShared.Models;

namespace MareSynchronosServer.Utils
{
    public static class Extensions
    {
        public static SlotInfoResponseDto ToSlotInfoDto(this Slot slot)
        {
            return new SlotInfoResponseDto
            {
                SlotId = slot.SlotId,
                SlotName = slot.SlotName,
                SlotDescription = slot.SlotDescription,
                Location = new SlotLocationDto
                {
                    ServerId = slot.ServerId,
                    TerritoryId = slot.TerritoryId,
                    DivisionId = slot.DivisionId,
                    WardId = slot.WardId,
                    PlotId = slot.PlotId,
                    X = slot.X,
                    Y = slot.Y,
                    Z = slot.Z,
                    Radius = slot.Radius
                },
                AssociatedSyncshell = new SlotSyncshellDto
                {
                    Gid = slot.GroupGID,
                    Name = slot.Group?.Alias ?? slot.GroupGID
                }
            };
        }

        public static EstablishmentDto ToEstablishmentDto(this Establishment establishment, string currentUserUID)
        {
            // Le gérant n'est exposé aux tiers que si le propriétaire l'a choisi ; lui-même voit toujours ses données.
            bool isOwner = string.Equals(establishment.OwnerUID, currentUserUID, StringComparison.Ordinal);
            var manager = isOwner || establishment.ShowManagerOnProfile ? establishment.ManagerRpProfile : null;
            // Un profil RP masqué par son propriétaire n'est exposé à personne d'autre, même via un établissement.
            if (!isOwner && manager is { Visibility: RpProfileVisibility.Hidden }) manager = null;

            return new EstablishmentDto
            {
                Id = establishment.Id,
                OwnerUID = establishment.OwnerUID,
                OwnerAlias = establishment.Owner?.Alias,
                Name = establishment.Name,
                Description = establishment.Description,
                Category = (int)establishment.Category,
                Languages = establishment.Languages,
                Tags = establishment.Tags,
                FactionTag = establishment.FactionTag,
                Schedule = establishment.Schedule,
                IsPublic = establishment.IsPublic,
                CreatedUtc = establishment.CreatedUtc,
                UpdatedUtc = establishment.UpdatedUtc,
                LogoImageBase64 = establishment.LogoImageBase64,
                BannerImageBase64 = establishment.BannerImageBase64,
                ManagerRpProfileId = isOwner || (establishment.ShowManagerOnProfile && manager != null) ? establishment.ManagerRpProfileId : null,
                ManagerCharacterName = manager?.CharacterName,
                ManagerRpFirstName = manager?.RpFirstName,
                ManagerRpLastName = manager?.RpLastName,
                ManagerRpProfilePictureBase64 = manager?.RpProfilePictureBase64,
                ShowManagerOnProfile = establishment.ShowManagerOnProfile,
                Location = new EstablishmentLocationDto
                {
                    LocationType = (int)establishment.LocationType,
                    TerritoryId = establishment.TerritoryId,
                    ServerId = establishment.ServerId,
                    WardId = establishment.WardId,
                    PlotId = establishment.PlotId,
                    DivisionId = establishment.DivisionId,
                    IsApartment = establishment.IsApartment,
                    RoomId = establishment.RoomId,
                    X = establishment.X,
                    Y = establishment.Y,
                    Z = establishment.Z,
                    Radius = establishment.Radius
                },
                Events = establishment.Events?.Select(e => e.ToEstablishmentEventDto()).ToList() ?? []
            };
        }

        public static EstablishmentEventDto ToEstablishmentEventDto(this EstablishmentEvent evt)
        {
            return new EstablishmentEventDto
            {
                Id = evt.Id,
                EstablishmentId = evt.EstablishmentId,
                Title = evt.Title,
                Description = evt.Description,
                StartsAtUtc = evt.StartsAtUtc,
                EndsAtUtc = evt.EndsAtUtc,
                Recurrence = evt.Recurrence,
                CreatedUtc = evt.CreatedUtc
            };
        }

        public static GroupData ToGroupData(this Group group)
        {
            return new GroupData(group.GID, group.Alias);
        }

        public static UserData ToUserData(this GroupPair pair)
        {
            return new UserData(pair.GroupUser.UID, pair.GroupUser.Alias);
        }

        public static UserData ToUserData(this User user)
        {
            return new UserData(user.UID, user.Alias);
        }

        public static GroupPermissions GetGroupPermissions(this Group group)
        {
            var permissions = GroupPermissions.NoneSet;
            permissions.SetDisableAnimations(group.PreferDisableAnimations);
            permissions.SetDisableSounds(group.PreferDisableSounds);
            permissions.SetDisableInvites(!group.InvitesEnabled);
            permissions.SetDisableVFX(group.PreferDisableVFX);
            permissions.SetPaused(group.IsPaused);
            return permissions;
        }

        public static GroupUserPermissions GetGroupPairPermissions(this GroupPair groupPair, GroupPairPreferredPermission? perms)
        {
            var permissions = GroupUserPermissions.NoneSet;
            if (perms != null)
            {
                permissions.SetDisableAnimations(perms.DisableAnimations);
                permissions.SetDisableSounds(perms.DisableSounds);
                permissions.SetPaused(perms.IsPaused);
                permissions.SetDisableVFX(perms.DisableVFX);
            }
            return permissions;
        }

        // Compat overload : retourne NoneSet quand on n'a pas chargé les preferred permissions.
        // Tous les appelants devraient idéalement pré-charger GroupPairPreferredPermission et utiliser l'overload ci-dessus.
        public static GroupUserPermissions GetGroupPairPermissions(this GroupPair groupPair) => GroupUserPermissions.NoneSet;

        public static UserPermissions ToUserPermissions(this UserPermissionSet? perms, bool setSticky = false)
        {
            var p = UserPermissions.NoneSet;
            if (perms == null) return p;
            p.SetPaired(true);
            p.SetPaused(perms.IsPaused);
            p.SetDisableAnimations(perms.DisableAnimations);
            p.SetDisableSounds(perms.DisableSounds);
            p.SetDisableVFX(perms.DisableVFX);
            if (setSticky) p.SetSticky(perms.Sticky);
            return p;
        }

        public static GroupUserInfo GetGroupPairUserInfo(this GroupPair groupPair)
        {
            var groupUserInfo = GroupUserInfo.None;
            groupUserInfo.SetPinned(groupPair.IsPinned);
            groupUserInfo.SetModerator(groupPair.IsModerator);
            return groupUserInfo;
        }

        public static WildRpAnnouncementDto ToWildRpAnnouncementDto(this WildRpAnnouncement announcement, bool forOwner = false)
        {
            // Un profil RP masqué par son propriétaire n'apparaît pas dans l'annonce vue par les autres.
            var rpProfile = forOwner || announcement.RpProfile is not { Visibility: RpProfileVisibility.Hidden } ? announcement.RpProfile : null;

            return new WildRpAnnouncementDto
            {
                Id = announcement.Id,
                UserUID = announcement.UserUID,
                UserAlias = announcement.Owner?.Alias,
                CharacterName = announcement.CharacterName,
                WorldId = announcement.WorldId,
                TerritoryId = announcement.TerritoryId,
                WardId = announcement.WardId,
                Message = announcement.Message,
                RpTitle = rpProfile?.RpTitle,
                RpFirstName = rpProfile?.RpFirstName,
                RpLastName = rpProfile?.RpLastName,
                RpProfilePictureBase64 = rpProfile?.RpProfilePictureBase64,
                RpLevel = rpProfile?.RpLevel ?? 0,
                CreatedAtUtc = announcement.CreatedAtUtc,
                ExpiresAtUtc = announcement.ExpiresAtUtc
            };
        }
    }
}
