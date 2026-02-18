using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosShared.Data;

public class MareDbContext : DbContext
{
#if DEBUG
    public MareDbContext() { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
        {
            base.OnConfiguring(optionsBuilder);
            return;
        }

        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=mare;Username=postgres", builder =>
        {
            builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            builder.MigrationsAssembly("UmbraSyncShared");
        }).UseSnakeCaseNamingConvention();
        optionsBuilder.EnableThreadSafetyChecks(false);

        base.OnConfiguring(optionsBuilder);
    }
#endif

    public MareDbContext(DbContextOptions<MareDbContext> options) : base(options)
    {
    }

    public DbSet<Auth> Auth { get; set; }
    public DbSet<BannedRegistrations> BannedRegistrations { get; set; }
    public DbSet<Banned> BannedUsers { get; set; }
    public DbSet<ClientPair> ClientPairs { get; set; }
    public DbSet<FileCache> Files { get; set; }
    public DbSet<ForbiddenUploadEntry> ForbiddenUploadEntries { get; set; }
    public DbSet<GroupBan> GroupBans { get; set; }
    public DbSet<GroupPair> GroupPairs { get; set; }
    public DbSet<Group> Groups { get; set; }
    public DbSet<GroupTempInvite> GroupTempInvites { get; set; }
    public DbSet<AutoDetectSchedule> AutoDetectSchedules { get; set; }
    public DbSet<LodeStoneAuth> LodeStoneAuth { get; set; }
    public DbSet<UserProfileData> UserProfileData { get; set; }
    public DbSet<CharacterRpProfileData> CharacterRpProfiles { get; set; }
    public DbSet<UserProfileDataReport> UserProfileReports { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<Slot> Slots { get; set; }
    public DbSet<CharaData> CharaData { get; set; }
    public DbSet<CharaDataFile> CharaDataFiles { get; set; }
    public DbSet<CharaDataFileSwap> CharaDataFileSwaps { get; set; }
    public DbSet<CharaDataOriginalFile> CharaDataOriginalFiles { get; set; }
    public DbSet<CharaDataPose> CharaDataPoses { get; set; }
    public DbSet<CharaDataAllowance> CharaDataAllowances { get; set; }
    public DbSet<McdfShare> McdfShares { get; set; }
    public DbSet<McdfShareAllowedUser> McdfShareAllowedUsers { get; set; }
    public DbSet<McdfShareAllowedGroup> McdfShareAllowedGroups { get; set; }
    public DbSet<GroupProfile> GroupProfiles { get; set; }
    public DbSet<HousingShare> HousingShares { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        ConfigureCoreEntities(mb);
        ConfigureGroupEntities(mb);
        ConfigureUserProfileEntities(mb);
        ConfigureCharaDataEntities(mb);
        ConfigureMcdfShareEntities(mb);
        ConfigureSlotEntities(mb);
        ConfigureHousingShareEntities(mb);
    }

    private static void ConfigureSlotEntities(ModelBuilder mb)
    {
        mb.Entity<Slot>().ToTable("slots");
        mb.Entity<Slot>().HasIndex(s => s.GroupGID);
        mb.Entity<Slot>().HasIndex(s => new { s.ServerId, s.TerritoryId, s.WardId, s.PlotId });
    }

    private static void ConfigureCoreEntities(ModelBuilder mb)
    {
        mb.Entity<Auth>().ToTable("auth");
        mb.Entity<User>().ToTable("users");
        mb.Entity<FileCache>().ToTable("file_caches");
        mb.Entity<FileCache>().HasIndex(c => c.UploaderUID);
        mb.Entity<ClientPair>().ToTable("client_pairs");
        mb.Entity<ClientPair>().HasKey(u => new { u.UserUID, u.OtherUserUID });
        mb.Entity<ClientPair>().HasIndex(c => c.UserUID);
        mb.Entity<ClientPair>().HasIndex(c => c.OtherUserUID);
        mb.Entity<ForbiddenUploadEntry>().ToTable("forbidden_upload_entries");
        mb.Entity<Banned>().ToTable("banned_users");
        mb.Entity<LodeStoneAuth>().ToTable("lodestone_auth");
        mb.Entity<BannedRegistrations>().ToTable("banned_registrations");
    }

    private static void ConfigureGroupEntities(ModelBuilder mb)
    {
        mb.Entity<Group>().ToTable("groups");
        mb.Entity<Group>().HasIndex(c => c.OwnerUID);
        mb.Entity<AutoDetectSchedule>().ToTable("autodetect_schedules");
        mb.Entity<AutoDetectSchedule>().HasKey(s => s.GroupGID);
        mb.Entity<AutoDetectSchedule>().HasOne<Group>().WithOne().HasForeignKey<AutoDetectSchedule>(s => s.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<GroupPair>().ToTable("group_pairs");
        mb.Entity<GroupPair>().HasKey(u => new { u.GroupGID, u.GroupUserUID });
        mb.Entity<GroupPair>().HasIndex(c => c.GroupUserUID);
        mb.Entity<GroupPair>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupBan>().ToTable("group_bans");
        mb.Entity<GroupBan>().HasKey(u => new { u.GroupGID, u.BannedUserUID });
        mb.Entity<GroupBan>().HasIndex(c => c.BannedUserUID);
        mb.Entity<GroupBan>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupTempInvite>().ToTable("group_temp_invites");
        mb.Entity<GroupTempInvite>().HasKey(u => new { u.GroupGID, u.Invite });
        mb.Entity<GroupTempInvite>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupTempInvite>().HasIndex(c => c.Invite);
        mb.Entity<GroupProfile>().ToTable("group_profiles");
        mb.Entity<GroupProfile>().HasKey(p => p.GroupGID);
        mb.Entity<GroupProfile>().HasOne(p => p.Group).WithOne().HasForeignKey<GroupProfile>(p => p.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<GroupProfile>().Property(p => p.Description).HasColumnType("text");
        mb.Entity<GroupProfile>().Property(p => p.Tags).HasColumnType("text[]");
        mb.Entity<GroupProfile>().Property(p => p.Base64ProfileImage).HasColumnType("text");
        mb.Entity<GroupProfile>().Property(p => p.Base64BannerImage).HasColumnType("text");
    }

    private static void ConfigureUserProfileEntities(ModelBuilder mb)
    {
        mb.Entity<UserProfileData>().ToTable("user_profile_data");
        mb.Entity<UserProfileData>().HasKey(c => c.UserUID);
        mb.Entity<CharacterRpProfileData>().ToTable("character_rp_profiles");
        mb.Entity<CharacterRpProfileData>().HasKey(c => c.Id);
        mb.Entity<CharacterRpProfileData>().HasIndex(c => new { c.UserUID, c.CharacterName, c.WorldId }).IsUnique();
        mb.Entity<UserProfileDataReport>().ToTable("user_profile_data_reports");
    }

    private static void ConfigureCharaDataEntities(ModelBuilder mb)
    {
        mb.Entity<CharaData>().ToTable("chara_data");
        mb.Entity<CharaData>()
            .HasMany(p => p.Poses)
            .WithOne(c => c.Parent)
            .HasForeignKey(c => new { c.ParentId, c.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.Files)
            .WithOne(c => c.Parent)
            .HasForeignKey(c => new { c.ParentId, c.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.OriginalFiles)
            .WithOne(p => p.Parent)
            .HasForeignKey(p => new { p.ParentId, p.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.AllowedIndividiuals)
            .WithOne(p => p.Parent)
            .HasForeignKey(p => new { p.ParentId, p.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.FileSwaps)
            .WithOne(p => p.Parent)
            .HasForeignKey(p => new { p.ParentId, p.ParentUploaderUID });
        mb.Entity<CharaData>().HasKey(p => new { p.Id, p.UploaderUID });
        mb.Entity<CharaData>().HasIndex(p => p.UploaderUID);
        mb.Entity<CharaData>().HasIndex(p => p.Id);
        mb.Entity<CharaDataFile>().ToTable("chara_data_files");
        mb.Entity<CharaDataFile>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.GamePath });
        mb.Entity<CharaDataFile>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataFile>().HasOne(f => f.FileCache).WithMany().HasForeignKey(f => f.FileCacheHash).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CharaDataFileSwap>().ToTable("chara_data_file_swaps");
        mb.Entity<CharaDataFileSwap>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.GamePath });
        mb.Entity<CharaDataFileSwap>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataPose>().ToTable("chara_data_poses");
        mb.Entity<CharaDataPose>().Property(p => p.Id).ValueGeneratedOnAdd();
        mb.Entity<CharaDataPose>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.Id });
        mb.Entity<CharaDataPose>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataOriginalFile>().ToTable("chara_data_orig_files");
        mb.Entity<CharaDataOriginalFile>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.GamePath });
        mb.Entity<CharaDataOriginalFile>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataAllowance>().ToTable("chara_data_allowance");
        mb.Entity<CharaDataAllowance>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.Id });
        mb.Entity<CharaDataAllowance>().Property(p => p.Id).ValueGeneratedOnAdd();
        mb.Entity<CharaDataAllowance>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataAllowance>().HasOne(u => u.AllowedGroup).WithMany().HasForeignKey(u => u.AllowedGroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CharaDataAllowance>().HasOne(u => u.AllowedUser).WithMany().HasForeignKey(u => u.AllowedUserUID).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMcdfShareEntities(ModelBuilder mb)
    {
        mb.Entity<McdfShare>().ToTable("mcdf_shares");
        mb.Entity<McdfShare>().HasIndex(s => s.OwnerUID);
        mb.Entity<McdfShare>().HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<McdfShare>().Property(s => s.Description).HasColumnType("text");
        mb.Entity<McdfShare>().Property(s => s.CipherData).HasColumnType("bytea");
        mb.Entity<McdfShare>().Property(s => s.Nonce).HasColumnType("bytea");
        mb.Entity<McdfShare>().Property(s => s.Salt).HasColumnType("bytea");
        mb.Entity<McdfShare>().Property(s => s.Tag).HasColumnType("bytea");
        mb.Entity<McdfShare>().Property(s => s.CreatedUtc).HasColumnType("timestamp with time zone");
        mb.Entity<McdfShare>().Property(s => s.UpdatedUtc).HasColumnType("timestamp with time zone");
        mb.Entity<McdfShare>().Property(s => s.ExpiresAtUtc).HasColumnType("timestamp with time zone");
        mb.Entity<McdfShare>().Property(s => s.DownloadCount).HasColumnType("integer");
        mb.Entity<McdfShare>().HasMany(s => s.AllowedIndividuals).WithOne(a => a.Share).HasForeignKey(a => a.ShareId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<McdfShare>().HasMany(s => s.AllowedSyncshells).WithOne(a => a.Share).HasForeignKey(a => a.ShareId).OnDelete(DeleteBehavior.Cascade);

        mb.Entity<McdfShareAllowedUser>().ToTable("mcdf_share_allowed_users");
        mb.Entity<McdfShareAllowedUser>().HasKey(u => new { u.ShareId, u.AllowedIndividualUid });
        mb.Entity<McdfShareAllowedUser>().HasIndex(u => u.AllowedIndividualUid);

        mb.Entity<McdfShareAllowedGroup>().ToTable("mcdf_share_allowed_groups");
        mb.Entity<McdfShareAllowedGroup>().HasKey(g => new { g.ShareId, g.AllowedGroupGid });
        mb.Entity<McdfShareAllowedGroup>().HasIndex(g => g.AllowedGroupGid);
    }

    private static void ConfigureHousingShareEntities(ModelBuilder mb)
    {
        mb.Entity<HousingShare>().ToTable("housing_shares");
        mb.Entity<HousingShare>().HasIndex(s => s.OwnerUID);
        mb.Entity<HousingShare>().HasIndex(s => new { s.ServerId, s.TerritoryId, s.DivisionId, s.WardId, s.HouseId });
        mb.Entity<HousingShare>().HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<HousingShare>().Property(s => s.Description).HasColumnType("text");
        mb.Entity<HousingShare>().Property(s => s.ServerId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.MapId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.TerritoryId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.DivisionId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.WardId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.HouseId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.RoomId).HasColumnType("bigint");
        mb.Entity<HousingShare>().Property(s => s.CipherData).HasColumnType("bytea");
        mb.Entity<HousingShare>().Property(s => s.Nonce).HasColumnType("bytea");
        mb.Entity<HousingShare>().Property(s => s.Salt).HasColumnType("bytea");
        mb.Entity<HousingShare>().Property(s => s.Tag).HasColumnType("bytea");
        mb.Entity<HousingShare>().Property(s => s.CreatedUtc).HasColumnType("timestamp with time zone");
        mb.Entity<HousingShare>().Property(s => s.UpdatedUtc).HasColumnType("timestamp with time zone");
    }
}
