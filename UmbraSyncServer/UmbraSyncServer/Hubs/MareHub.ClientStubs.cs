using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.QuestSync;
using UmbraSync.API.Dto.User;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        public Task Client_DownloadReady(Guid requestId) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupChangePermissions(GroupPermissionDto groupPermission) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupDelete(GroupDto groupDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairChangePermissions(GroupPairUserPermissionDto permissionDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairLeft(GroupPairDto groupPairDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupSendInfo(GroupInfoDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupSendProfile(GroupProfileDto profile) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_KeepAlive(byte[] padding) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserAddClientPair(UserPairDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_ReceivePairRequest(UserDto requester) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserReceiveCharacterData(OnlineUserCharaDataDto dataDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserReceiveUploadStatus(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserRemoveClientPair(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserSendOffline(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserSendOnline(OnlineUserIdentDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateOtherPairPermissions(UserPermissionsDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateProfile(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateSelfPairPermissions(UserPermissionsDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateDefaultPermissions(DefaultPermissionsDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UpdateUserIndividualPairStatusDto(UserIndividualPairStatusDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserTypingState(TypingStateDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");


        public Task Client_GposeLobbyJoin(UserData userData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyLeave(UserData userData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyPushPoseData(UserData userData, PoseData poseData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyPushWorldData(UserData userData, WorldData worldData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_QuestSessionJoin(UserData userData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_QuestSessionLeave(UserData userData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_QuestSessionStateUpdate(UserData sender, QuestSessionStateDto state) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_QuestSessionEventTriggered(UserData sender, QuestEventTriggerDto trigger) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_QuestSessionBranchingChoice(UserData sender, QuestBranchingChoiceDto choice) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_McdfShareReceived(string ownerUid, string description) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
    }
}