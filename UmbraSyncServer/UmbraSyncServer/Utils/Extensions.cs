using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.Slot;
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
                ManagerUID = establishment.ManagerUID,
                ManagerAlias = string.Equals(establishment.ManagerUID, establishment.OwnerUID, StringComparison.Ordinal)
                    ? establishment.Owner?.Alias : null,
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
            permissions.SetDisableAnimations(group.DisableAnimations);
            permissions.SetDisableSounds(group.DisableSounds);
            permissions.SetDisableInvites(!group.InvitesEnabled);
            permissions.SetDisableVFX(group.DisableVFX);
            permissions.SetPaused(group.IsPaused);
            return permissions;
        }

        public static GroupUserPermissions GetGroupPairPermissions(this GroupPair groupPair)
        {
            var permissions = GroupUserPermissions.NoneSet;
            permissions.SetDisableAnimations(groupPair.DisableAnimations);
            permissions.SetDisableSounds(groupPair.DisableSounds);
            permissions.SetPaused(groupPair.IsPaused);
            permissions.SetDisableVFX(groupPair.DisableVFX);
            return permissions;
        }

        public static GroupUserInfo GetGroupPairUserInfo(this GroupPair groupPair)
        {
            var groupUserInfo = GroupUserInfo.None;
            groupUserInfo.SetPinned(groupPair.IsPinned);
            groupUserInfo.SetModerator(groupPair.IsModerator);
            return groupUserInfo;
        }
    }
}
