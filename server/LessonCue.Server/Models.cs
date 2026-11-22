using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace LessonCue.Server;

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(100)] public string TimeZone { get; set; } = "America/New_York";
    [MaxLength(160)] public string SiteName { get; set; } = "Main Site";
    [MaxLength(16)] public string WeekStartsOn { get; set; } = "Sunday";
    public int DefaultLessonDurationMinutes { get; set; } = 60;
    public int DefaultRetentionDays { get; set; } = 30;
    public long StorageLimitBytes { get; set; }
    [MaxLength(16)] public string PrimaryColor { get; set; } = "#25302d";
    [MaxLength(16)] public string AccentColor { get; set; } = "#d89127";
    [MaxLength(16)] public string NavigationTextColor { get; set; } = "#aac0bb";
    [MaxLength(16)] public string SelectedTabColor { get; set; } = "#3a4541";
    [MaxLength(240)] public string WelcomeMessage { get; set; } = "Welcome";
    public bool AdaptiveTranscodingEnabled { get; set; } = true;
    public int TranscodeLeadDays { get; set; } = 7;
    public bool HardwareAccelerationEnabled { get; set; } = true;
    [MaxLength(12000)] public string MediaFoldersJson { get; set; } = "[\"General\",\"Lessons\",\"Signage\"]";
    [MaxLength(12000)] public string MediaTagsJson { get; set; } = "[\"Reusable\",\"Intro\",\"Outro\",\"Reference\"]";
    [MaxLength(12000)] public string SignageSourceAllowlistJson { get; set; } = "[]";
    // Retained for database and API compatibility. Signage is now always live.
    public bool SignageEnabled { get; set; } = true;
    // New databases already contain the current signage model. Legacy
    // databases are deliberately created at 0 by DatabaseUpgrade so the
    // one-time compatibility purge still runs before being promoted to 1.
    public int SignageModelVersion { get; set; } = 1;
    [JsonIgnore] public string? ControllerPinHash { get; set; }
    [JsonIgnore, MaxLength(2048)] public string? ControllerPinProtected { get; set; }
    public bool RequireLocalRoomControllers { get; set; }
    /// <summary>When enabled, every active account must have enrolled authenticator MFA.</summary>
    public bool RequireMfaForAllUsers { get; set; }
    [MaxLength(16)] public string RegistrationMode { get; set; } = "closed";
    [MaxLength(253)] public string PublicBaseUrl { get; set; } = "";
    [MaxLength(200)] public string EmailFromAddress { get; set; } = "";
    [MaxLength(120)] public string EmailFromName { get; set; } = "LessonCue";
    [MaxLength(16)] public string EmailProvider { get; set; } = "none";
    /// <summary>Opt-in daily failures-only troubleshooting report.</summary>
    public bool DailyTroubleshootingEmailEnabled { get; set; }
    [MaxLength(200)] public string DailyTroubleshootingEmailRecipient { get; set; } = "";
    [MaxLength(5)] public string DailyTroubleshootingEmailTime { get; set; } = "07:00";
    public DateTimeOffset? DailyTroubleshootingEmailLastSentAt { get; set; }
    [MaxLength(1000)] public string? DailyTroubleshootingEmailLastError { get; set; }
    /// <summary>Opt-in provider-neutral AI troubleshooting review configuration.</summary>
    [MaxLength(24000)] public string TroubleshootingReviewSettingsJson { get; set; } = "{}";
    public DateTimeOffset? TroubleshootingReviewLastRunAt { get; set; }
    [MaxLength(24)] public string TroubleshootingReviewLastStatus { get; set; } = "never";
    [MaxLength(1000)] public string? TroubleshootingReviewLastError { get; set; }
    [MaxLength(512)] public string? TroubleshootingReviewLastArtifact { get; set; }
    [MaxLength(24000)] public string UploadQuotaPolicyJson { get; set; } = "{}";

    // The optional self-hosted URL shortener. Every value here is an
    // administrator's to set: nothing about a particular domain belongs in the
    // application, and an installation that never turns this on is unaffected.
    public bool ShortenerEnabled { get; set; }
    /// <summary>The public short domain, stored bare: "go.example.org", never with a scheme.</summary>
    [MaxLength(253)] public string ShortDomain { get; set; } = "";
    /// <summary>Where the management UI lives. Defaults to short.{ShortDomain}, and can be overridden.</summary>
    [MaxLength(253)] public string ShortenerAdminHost { get; set; } = "";
    /// <summary>Where LessonCue reaches the shortener's API from inside the deployment.</summary>
    [MaxLength(253)] public string ShortDomainUpstream { get; set; } = "";
    /// <summary>What the bare short domain does: lessoncue, organization, custom, or notfound.</summary>
    [MaxLength(16)] public string ShortenerRootRedirectMode { get; set; } = "notfound";
    /// <summary>The destination for the organization and custom root modes.</summary>
    [MaxLength(2048)] public string ShortDomainRootRedirectUrl { get; set; } = "";
    /// <summary>Which reserved-code pool this installation provisioned, for backup and upgrade.</summary>
    public int ShortenerPoolVersion { get; set; }

    // Retained so an installation that ran 0.41.0 keeps its columns; the
    // shortener answers its own root now, so nothing reads these.
    public bool ShortDomainRootRedirectEnabled { get; set; } = true;
    [MaxLength(16)] public string ShortDomainRootFallback { get; set; } = "shortener";
    public bool ShortDomainRootRedirectPermanent { get; set; }
    public bool ShortDomainRootPreserveQuery { get; set; } = true;
}

public sealed class AdminAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string Username { get; set; }
    [MaxLength(120)] public string DisplayName { get; set; } = "Administrator";
    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(32)] public string Role { get; set; } = "Service Admin";
    [MaxLength(512)] public string? PermissionsCsv { get; set; }
    public bool Disabled { get; set; }
    public required string PasswordHash { get; set; }
    public int SessionVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool EmailVerified { get; set; } = true;
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public bool PendingApproval { get; set; }
    public bool PendingSetup { get; set; }
    public bool MustChangePassword { get; set; }
    [JsonIgnore, MaxLength(2048)] public string? TotpSecretProtected { get; set; }
    public bool TotpEnabled { get; set; }
    public long TotpLastCounter { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
}

public sealed class AccountToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public AdminAccount? Account { get; set; }
    [MaxLength(32)] public required string Purpose { get; set; }
    [MaxLength(64)] public required string TokenHash { get; set; }
    [MaxLength(200)] public string? PendingEmail { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class RegistrationCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public required string CodeHash { get; set; }
    [MaxLength(16)] public required string Hint { get; set; }
    [MaxLength(120)] public string Label { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int Uses { get; set; }
    public int? MaxUses { get; set; }
}

public sealed class LessonClass
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public required string Name { get; set; }
    [MaxLength(1000)] public string Description { get; set; } = "";
    [MaxLength(63)] public string ControllerSlug { get; set; } = "";
    [MaxLength(16)] public string ControllerColor { get; set; } = "#2d6a4f";
    [MaxLength(253)] public string? ControllerHostname { get; set; }
    [JsonIgnore, MaxLength(48)] public string? PermanentControllerToken { get; set; }
    public Guid? PermanentControllerLessonId { get; set; }
    public DateTimeOffset? PermanentControllerCreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    [MaxLength(80)] public string? DeletedBy { get; set; }
}

public sealed class Lesson
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClassId { get; set; }
    public LessonClass? Class { get; set; }
    public DateOnly Date { get; set; }
    [MaxLength(160)] public required string Title { get; set; }
    public DateTimeOffset? AvailableFrom { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? DesignatedStartAt { get; set; }
    public DateTimeOffset? PreRollStartsAt { get; set; }
    public bool PreRollEnabled { get; set; }
    public Guid? CountdownItemId { get; set; }
    public int Version { get; set; } = 1;
    public bool Archived { get; set; }
    public bool KeepOffline { get; set; }
    public int DownloadDaysBefore { get; set; } = 7;
    public int VolumePercent { get; set; } = 100;
    public bool Muted { get; set; }
    [MaxLength(8000)] public string SubstituteNotes { get; set; } = "";
    [MaxLength(2000)] public string? PreRollMonitorUrl { get; set; }
    public Guid? GeneratedByScheduleId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    [MaxLength(80)] public string? DeletedBy { get; set; }
    public List<PlaylistItem> Items { get; set; } = [];
}

public sealed class LessonTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(1000)] public string Description { get; set; } = "";
    [MaxLength(160)] public string DefaultTitle { get; set; } = "Lesson";
    public int? DefaultStartMinutes { get; set; }
    public int? PreRollLeadMinutes { get; set; }
    public int? AvailableLeadMinutes { get; set; }
    public int? ExpiresAfterMinutes { get; set; }
    public bool PreRollEnabled { get; set; }
    public bool KeepOffline { get; set; }
    public int DownloadDaysBefore { get; set; } = 7;
    public int VolumePercent { get; set; } = 100;
    public bool Muted { get; set; }
    [MaxLength(8000)] public string SubstituteNotes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LessonTemplateItem> Items { get; set; } = [];
    public List<RecurringLessonSchedule> Schedules { get; set; } = [];
}

public sealed class LessonTemplateItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public LessonTemplate? Template { get; set; }
    [MaxLength(160)] public required string Title { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "video";
    [MaxLength(32)] public string Role { get; set; } = "lesson";
    public decimal Position { get; set; }
    public Guid? MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }
    public long? DurationMs { get; set; }
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
    public int VolumePercent { get; set; } = 100;
    public int? ImageDurationSeconds { get; set; }
    public int? EstimatedDurationSeconds { get; set; }
    [MaxLength(24)] public string EndBehavior { get; set; } = "pause";
    public bool AllowSkip { get; set; } = true;
    [MaxLength(2000)] public string Notes { get; set; } = "";
    public int FadeInMs { get; set; }
    public int FadeOutMs { get; set; }
    public bool NormalizeAudio { get; set; }
    [MaxLength(8000)] public string CuePointsJson { get; set; } = "[]";
    [MaxLength(16)] public string FitMode { get; set; } = "fit";
    public int RotationDegrees { get; set; }
    public int CropLeftPercent { get; set; }
    public int CropTopPercent { get; set; }
    public int CropRightPercent { get; set; }
    public int CropBottomPercent { get; set; }
    public bool Muted { get; set; }
    public int PlaybackRatePercent { get; set; } = 100;
    public int RepeatCount { get; set; } = 1;
    [MaxLength(16)] public string BackgroundColor { get; set; } = "#000000";
    [MaxLength(24)] public string TransitionStyle { get; set; } = "cut";
    public int TransitionDurationMs { get; set; } = 500;
    public bool FlexibleTime { get; set; }
    public Guid? ActivityDefinitionId { get; set; }
    public Activities.ActivityDefinition? ActivityDefinition { get; set; }
}

public sealed class RecurringLessonSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public LessonTemplate? Template { get; set; }
    public Guid ClassId { get; set; }
    public LessonClass? Class { get; set; }
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(32)] public string Frequency { get; set; } = "weekly";
    public int Interval { get; set; } = 1;
    public int? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? StartMinutes { get; set; }
    [MaxLength(240)] public string TitlePattern { get; set; } = "{template} — {date}";
    [MaxLength(12000)] public string CustomDatesJson { get; set; } = "[]";
    [MaxLength(12000)] public string ExcludedDatesJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public int GenerateDaysAhead { get; set; } = 90;
    public DateTimeOffset? LastGeneratedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlaylistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    [MaxLength(160)] public required string Title { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "video";
    [MaxLength(32)] public string Role { get; set; } = "lesson";
    public decimal Position { get; set; }
    public Guid? MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }
    public Guid? ActivityDefinitionId { get; set; }
    public Activities.ActivityDefinition? ActivityDefinition { get; set; }
    public long? DurationMs { get; set; }
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
    public int VolumePercent { get; set; } = 100;
    public int? ImageDurationSeconds { get; set; }
    public int? EstimatedDurationSeconds { get; set; }
    [MaxLength(24)] public string EndBehavior { get; set; } = "pause";
    public bool AllowSkip { get; set; } = true;
    [MaxLength(2000)] public string Notes { get; set; } = "";
    public int FadeInMs { get; set; }
    public int FadeOutMs { get; set; }
    public bool NormalizeAudio { get; set; }
    [MaxLength(8000)] public string CuePointsJson { get; set; } = "[]";
    [MaxLength(16)] public string FitMode { get; set; } = "fit";
    public int RotationDegrees { get; set; }
    public int CropLeftPercent { get; set; }
    public int CropTopPercent { get; set; }
    public int CropRightPercent { get; set; }
    public int CropBottomPercent { get; set; }
    public bool Muted { get; set; }
    public int PlaybackRatePercent { get; set; } = 100;
    public int RepeatCount { get; set; } = 1;
    [MaxLength(16)] public string BackgroundColor { get; set; } = "#000000";
    [MaxLength(24)] public string TransitionStyle { get; set; } = "cut";
    public int TransitionDurationMs { get; set; } = 500;
    public bool FlexibleTime { get; set; }
}

public sealed class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(255)] public required string FileName { get; set; }
    [MaxLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    [MaxLength(512)] public required string RelativePath { get; set; }
    [MaxLength(64)] public string? Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public long? DurationMs { get; set; }
    public bool OfflineEligible { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    [MaxLength(80)] public string? DeletedBy { get; set; }
    [MaxLength(32)] public string ProcessingStatus { get; set; } = "pending";
    [MaxLength(1000)] public string? ProcessingError { get; set; }
    [MaxLength(40)] public string? VideoCodec { get; set; }
    [MaxLength(40)] public string? AudioCodec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? LoudnessLufs { get; set; }
    [MaxLength(512)] public string? ThumbnailPath { get; set; }
    [MaxLength(512)] public string? FilmstripPath { get; set; }
    [MaxLength(512)] public string? WaveformPath { get; set; }
    [MaxLength(512)] public string? CompatibilityPath { get; set; }
    [MaxLength(64)] public string? CompatibilitySha256 { get; set; }
    public long? CompatibilitySizeBytes { get; set; }
    [MaxLength(24)] public string CompatibilityStatus { get; set; } = "pending";
    [MaxLength(1000)] public string? CompatibilityError { get; set; }
    public DateTimeOffset? CompatibilityTranscodedAt { get; set; }
    [MaxLength(32)] public string? CompatibilityTranscodeEngine { get; set; }
    [MaxLength(32)] public string SourceKind { get; set; } = "upload";
    [MaxLength(2048)] public string? SourceUrl { get; set; }
    [MaxLength(32)] public string? LinkKind { get; set; }
    [MaxLength(32)] public string? YouTubeRecoveryVersion { get; set; }
    public int YouTubeRecoveryAttempts { get; set; }
    public DateTimeOffset? YouTubeRecoveryLastAttemptAt { get; set; }
    [MaxLength(32)] public string StoragePolicy { get; set; } = "persistent";
    public Guid? OriginLessonId { get; set; }
    public DateTimeOffset? DeleteAfter { get; set; }
    public bool RetentionDateIsManual { get; set; }
    [MaxLength(120)] public string Folder { get; set; } = "";
    [MaxLength(500)] public string TagsCsv { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTimeOffset? ReplacedAt { get; set; }
    [MaxLength(24)] public string ConversionStatus { get; set; } = "none";
    [MaxLength(1000)] public string? ConversionError { get; set; }
    [MaxLength(24000)] public string ConvertedSlidesJson { get; set; } = "[]";
    public DateTimeOffset? ConvertedAt { get; set; }
    public Guid? ConversionLessonId { get; set; }
    public int ConversionSlideDurationSeconds { get; set; } = 10;
    public List<MediaAssetVersion> Versions { get; set; } = [];
    public List<MediaTranscodeVariant> TranscodeVariants { get; set; } = [];
}

public sealed class UploadSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerAccountId { get; set; }
    [MaxLength(80)] public string OwnerUsername { get; set; } = "";
    [MaxLength(32)] public string OwnerRole { get; set; } = "";
    [MaxLength(255)] public required string FileName { get; set; }
    [MaxLength(100)] public string DeclaredContentType { get; set; } = "application/octet-stream";
    public long ExpectedLength { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    [MaxLength(100000)] public string ChunkBitmap { get; set; } = "";
    [MaxLength(64)] public string? ExpectedSha256 { get; set; }
    [MaxLength(64)] public string? ContentSha256 { get; set; }
    public long ReceivedBytes { get; set; }
    public long ReservedBytes { get; set; }
    [MaxLength(24)] public string State { get; set; } = UploadSessionStates.Active;
    [MaxLength(1000)] public string? FailureReason { get; set; }
    public bool Persistent { get; set; }
    public Guid? LessonId { get; set; }
    public Guid? ClassId { get; set; }
    [MaxLength(120)] public string Folder { get; set; } = "";
    [MaxLength(500)] public string TagsCsv { get; set; } = "";
    public long? DurationMs { get; set; }
    public Guid? MediaAssetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class MediaTranscodeVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }
    [MaxLength(32)] public required string Profile { get; set; }
    [MaxLength(24)] public string Status { get; set; } = "pending";
    [MaxLength(512)] public string? RelativePath { get; set; }
    [MaxLength(64)] public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int VideoBitrateKbps { get; set; }
    public int SourceVersion { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
    [MaxLength(32)] public string? TranscodeEngine { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class MediaAssetVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }
    public int VersionNumber { get; set; }
    [MaxLength(255)] public required string FileName { get; set; }
    [MaxLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    [MaxLength(512)] public required string RelativePath { get; set; }
    [MaxLength(64)] public string? Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public long? DurationMs { get; set; }
    [MaxLength(32)] public string SourceKind { get; set; } = "upload";
    [MaxLength(2048)] public string? SourceUrl { get; set; }
    [MaxLength(32)] public string? LinkKind { get; set; }
    public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(80)] public string ArchivedBy { get; set; } = "admin";
}

public sealed class Screen
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public required string Name { get; set; }
    [MaxLength(32)] public string Platform { get; set; } = "android-tv";
    public Guid? AssignedClassId { get; set; }
    public bool VolunteerMode { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }
    public long FreeBytes { get; set; }
    public int FailedDownloads { get; set; }
    public bool Revoked { get; set; }
    public bool SignageOnly { get; set; }
    public bool PermanentPairing { get; set; }
    public Guid? AssignedSignageId { get; set; }
    [MaxLength(32)] public string AppVersion { get; set; } = "unknown";
    public int ManifestVersion { get; set; }
    [MaxLength(500)] public string TagsCsv { get; set; } = "";
    [MaxLength(100)] public string Site { get; set; } = "Main Site";
    [MaxLength(24)] public string SignageOrientation { get; set; } = "auto";
    public int? SignageWidth { get; set; }
    public int? SignageHeight { get; set; }
    [MaxLength(64)] public string? LastIpAddress { get; set; }
    public int ControlVersion { get; set; }
    [MaxLength(24)] public string ControlAction { get; set; } = "none";
    public Guid? ControlLessonId { get; set; }
    public Guid? ControlItemId { get; set; }
    public long? ControlPositionMs { get; set; }
    public DateTimeOffset? ControlIssuedAt { get; set; }
    public int AcknowledgedControlVersion { get; set; }
    [MaxLength(24)] public string PlaybackState { get; set; } = "idle";
    public Guid? PlaybackLessonId { get; set; }
    public Guid? PlaybackItemId { get; set; }
    public long PlaybackPositionMs { get; set; }
    public long? PlaybackDurationMs { get; set; }
    public int PlaybackVolumePercent { get; set; } = 100;
    public DateTimeOffset? PlaybackUpdatedAt { get; set; }
    [MaxLength(1000)] public string? PlaybackError { get; set; }
    public int CachedItems { get; set; }
    public int TotalItems { get; set; }
    [MaxLength(160)] public string? DeviceModel { get; set; }
    [MaxLength(80)] public string? OsVersion { get; set; }
    public string CacheInventoryJson { get; set; } = "[]";
    public string DownloadQueueJson { get; set; } = "[]";
    public string CodecCapabilitiesJson { get; set; } = "[]";
    public string RecentErrorsJson { get; set; } = "[]";
    public string ConnectionDiagnosticsJson { get; set; } = "{}";
    public long? ClockOffsetMs { get; set; }
    public int? NetworkLatencyMs { get; set; }
    [MaxLength(24)] public string NetworkQuality { get; set; } = "unknown";
    public DateTimeOffset? DiagnosticsUpdatedAt { get; set; }
    public bool AllowDiagnosticScreenshots { get; set; }
    public Guid? ScreenshotRequestId { get; set; }
    public DateTimeOffset? ScreenshotRequestedAt { get; set; }
    public DateTimeOffset? ScreenshotExpiresAt { get; set; }
    [MaxLength(24)] public string ScreenshotStatus { get; set; } = "none";
    public DateTimeOffset? ScreenshotCapturedAt { get; set; }
    [MaxLength(255)] public string? ScreenshotRelativePath { get; set; }
}

public sealed class PlaybackCommandRecord
{
    public long Id { get; set; }
    public Guid ScreenId { get; set; }
    public Screen? Screen { get; set; }
    public int Version { get; set; }
    [MaxLength(24)] public string Action { get; set; } = "none";
    public Guid? LessonId { get; set; }
    public Guid? ItemId { get; set; }
    public long? PositionMs { get; set; }
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SignagePlaylist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(32)] public string Mode { get; set; } = "scheduled";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    [MaxLength(2000)] public string Message { get; set; } = "";
    [MaxLength(16)] public string BackgroundColor { get; set; } = "#25302d";
    [MaxLength(16)] public string TextColor { get; set; } = "#ffffff";
    public Guid? MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }
    [MaxLength(2000)] public string TargetTagsCsv { get; set; } = "";
    [MaxLength(16)] public string Recurrence { get; set; } = "once";
    public DateOnly? ScheduleStartDate { get; set; }
    public DateOnly? ScheduleEndDate { get; set; }
    public int? StartMinutes { get; set; }
    public int? EndMinutes { get; set; }
    [MaxLength(64)] public string DaysOfWeekCsv { get; set; } = "";
    [MaxLength(12000)] public string ExcludedDatesJson { get; set; } = "[]";
    [MaxLength(12000)] public string TargetScreenIdsJson { get; set; } = "[]";
    [MaxLength(32)] public string LayoutPreset { get; set; } = "single";
    [MaxLength(32000)] public string ZonesJson { get; set; } = "[]";
    [MaxLength(64000)] public string WidgetCacheJson { get; set; } = "[]";
    public DateTimeOffset? WidgetCacheUpdatedAt { get; set; }
    [MaxLength(2000)] public string? WidgetCacheError { get; set; }
    public Guid? LayoutId { get; set; }
    public Guid? ContentPlaylistId { get; set; }
    [MaxLength(12000)] public string ZonePlaylistAssignmentsJson { get; set; } = "{}";
    public int VolumePercent { get; set; } = 100;
    [MaxLength(16)] public string DisplayPower { get; set; } = "unchanged";
    public int Version { get; set; } = 1;
    public int PublishedVersion { get; set; } = 1;
    [MaxLength(24)] public string PublishState { get; set; } = "published";
    public DateTimeOffset? PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPushedAt { get; set; }
    public bool KioskEnabled { get; set; }
    [MaxLength(2000)] public string? KioskInteractionUrl { get; set; }
    public int KioskTimeoutSeconds { get; set; } = 60;
    public bool KioskShowCloseButton { get; set; } = true;
    public bool KioskShowTouchIndicator { get; set; } = true;
    public bool KioskVirtualKeyboard { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SignageLayoutResource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(160)] public string Folder { get; set; } = "";
    [MaxLength(1000)] public string Description { get; set; } = "";
    public bool IsTemplate { get; set; }
    public bool IsStarter { get; set; }
    [MaxLength(80)] public string? TemplateKey { get; set; }
    [MaxLength(16)] public string BackgroundColor { get; set; } = "#25302d";
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    [MaxLength(24)] public string Orientation { get; set; } = "landscape";
    public int SafeAreaPercent { get; set; } = 5;
    [MaxLength(32000)] public string DraftZonesJson { get; set; } = "[]";
    [MaxLength(32000)] public string PublishedZonesJson { get; set; } = "[]";
    public Guid? BackgroundAudioAssetId { get; set; }
    public int Version { get; set; } = 1;
    public int PublishedVersion { get; set; }
    [MaxLength(24)] public string PublishState { get; set; } = "draft";
    public DateTimeOffset? PublishedAt { get; set; }
    [MaxLength(12000)] public string ThumbnailDataUrl { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SignageContentPlaylist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(160)] public string Folder { get; set; } = "";
    [MaxLength(24)] public string PlaybackMode { get; set; } = "ordered";
    [MaxLength(24)] public string Synchronization { get; set; } = "screen";
    [MaxLength(64000)] public string DraftItemsJson { get; set; } = "[]";
    [MaxLength(64000)] public string PublishedItemsJson { get; set; } = "[]";
    public int Version { get; set; } = 1;
    public int PublishedVersion { get; set; }
    [MaxLength(24)] public string PublishState { get; set; } = "draft";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SignageEmergencyTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Name { get; set; }
    [MaxLength(80)] public string Severity { get; set; } = "urgent";
    [MaxLength(2000)] public string Message { get; set; } = "";
    [MaxLength(16)] public string BackgroundColor { get; set; } = "#9b1c1c";
    [MaxLength(16)] public string TextColor { get; set; } = "#ffffff";
    public Guid? MediaAssetId { get; set; }
    [MaxLength(2000)] public string TargetTagsCsv { get; set; } = "";
    public int DefaultDurationMinutes { get; set; } = 30;
    public Guid? ActiveSignageId { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SignageProofRecord
{
    public long Id { get; set; }
    public Guid ScreenId { get; set; }
    public Guid SignageId { get; set; }
    public int Version { get; set; }
    [MaxLength(160)] public string SignageName { get; set; } = "";
    [MaxLength(24)] public string Event { get; set; } = "shown";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public long DurationMs { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
}

public sealed class BackupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(255)] public required string FileName { get; set; }
    [MaxLength(32)] public string Kind { get; set; } = "configuration";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(80)] public string CreatedBy { get; set; } = "system";
}

public sealed class AudienceSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public required string Title { get; set; }
    [MaxLength(12)] public required string Code { get; set; }
    [MaxLength(16)] public string Status { get; set; } = "draft";
    public bool ShowLiveResults { get; set; }
    public bool AllowResponseChanges { get; set; } = true;
    public int RetentionDays { get; set; } = 7;
    [MaxLength(80)] public string CreatedBy { get; set; } = "admin";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset PurgeAt { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
    public List<AudienceQuestion> Questions { get; set; } = [];
}

public sealed class AudienceQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AudienceSession? Session { get; set; }
    public int Position { get; set; }
    [MaxLength(16)] public string Type { get; set; } = "single";
    [MaxLength(500)] public required string Prompt { get; set; }
    [MaxLength(8000)] public string OptionsJson { get; set; } = "[]";
    public bool Required { get; set; } = true;
    public int MaxSelections { get; set; } = 1;
    public bool ModerateResponses { get; set; } = true;
    public List<AudienceResponse> Responses { get; set; } = [];
}

public sealed class AudienceResponse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AudienceSession? Session { get; set; }
    public Guid QuestionId { get; set; }
    public AudienceQuestion? Question { get; set; }
    [MaxLength(64)] public required string ParticipantTokenHash { get; set; }
    [MaxLength(8000)] public string AnswerJson { get; set; } = "[]";
    [MaxLength(1000)] public string TextAnswer { get; set; } = "";
    [MaxLength(16)] public string ModerationStatus { get; set; } = "approved";
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PairingAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public required string DeviceName { get; set; }
    [MaxLength(32)] public required string Platform { get; set; }
    [MaxLength(32)] public required string AppVersion { get; set; }
    public string PinHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
    public bool Completed { get; set; }
}

public sealed class DeviceCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScreenId { get; set; }
    [MaxLength(64)] public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(100)] public string Actor { get; set; } = "system";
    [MaxLength(80)] public required string Action { get; set; }
    [MaxLength(160)] public required string Object { get; set; }
    [MaxLength(32)] public string Result { get; set; } = "success";
    public string? Summary { get; set; }
}

public sealed record ClassInput(string Name, string? Description, string? ControllerSlug = null,
    string? ControllerColor = null, string? ControllerHostname = null);
public sealed record LessonInput(Guid ClassId, DateOnly Date, string Title, DateTimeOffset? AvailableFrom,
    DateTimeOffset? ExpiresAt, DateTimeOffset? DesignatedStartAt, bool PreRollEnabled, Guid? CountdownItemId,
    DateTimeOffset? PreRollStartsAt = null, int VolumePercent = 100, bool Muted = false,
    string? SubstituteNotes = null, string? PreRollMonitorUrl = null);
public sealed record PlaylistItemInput(string Title, string Type, string? Role, decimal Position,
    Guid? MediaId, long? DurationMs, long StartMs, long? EndMs, int VolumePercent,
    int? ImageDurationSeconds, int? EstimatedDurationSeconds, string? EndBehavior, bool AllowSkip, string? FitMode = null,
    int RotationDegrees = 0, int CropLeftPercent = 0, int CropTopPercent = 0,
    int CropRightPercent = 0, int CropBottomPercent = 0, bool Muted = false,
    int PlaybackRatePercent = 100, int RepeatCount = 1, string? BackgroundColor = null,
    string? TransitionStyle = null, int TransitionDurationMs = 500, Guid? ActivityDefinitionId = null);
public sealed record PairingRequestInput(string DeviceName, string Platform, string AppVersion, string? DevicePublicKey);
public sealed record PairingConfirmInput(Guid RequestId, string Pin);
public sealed record PairingPinInput(string? Pin, bool Automatic = false);
public sealed record ControllerPinInput(string Pin);

/// <summary>Ask for the shortener to be installed on this server at the next update.</summary>
public sealed record ShortenerInstallInput(bool Requested, string? Domain = null);

/// <summary>The API key the shortener was started with. LessonCue records it; it cannot mint one.</summary>
public sealed record ShortenerKeyInput(string? ApiKey);

public sealed record ShortenerConsolePasswordInput(string Password);

public sealed record ShortenerConfigureInput(
    string? Domain,
    string? AdminHost,
    string? Upstream,
    string? RootRedirectMode,
    string? RootRedirectUrl,
    bool Enabled = true);

/// <summary>Stop, disable, or remove the integration. Distinct actions on purpose.</summary>
public sealed record ShortenerLifecycleInput(string Action, bool DeleteData = false, bool Confirm = false);
public sealed record TemporaryControllerSessionInput(Guid ClassId, Guid? LessonId, int ExpiresInMinutes = 60);
public sealed record PermanentControllerSessionInput(Guid ClassId, Guid? LessonId);
public sealed record RecycleBinItem(string Kind, Guid Id, string Title, string Detail,
    DateTimeOffset DeletedAt, string? DeletedBy);
public sealed record BackupCreateInput(
    bool Full = false,
    string? Password = null,
    string SecretHandling = "exclude");
public sealed record BackupPasswordInput(string? Password = null);
public sealed record BackupRestoreInput(Guid RestoreId, string Confirmation);
public sealed record TvStatusInput(Guid ScreenId, string AppVersion, bool Online, long FreeBytes,
    int ManifestVersion, int FailedDownloads, int? AcknowledgedControlVersion = null,
    string? PlaybackState = null, Guid? LessonId = null, Guid? ItemId = null,
    long? PositionMs = null, long? DurationMs = null, int? VolumePercent = null,
    string? PlaybackError = null, int? CachedItems = null, int? TotalItems = null,
    string? DeviceModel = null, string? OsVersion = null, long? ClientTimeUnixMs = null,
    int? NetworkLatencyMs = null, string? NetworkQuality = null,
    List<TvCacheItemInput>? CacheInventory = null, List<TvDownloadItemInput>? DownloadQueue = null,
    List<TvCodecCapabilityInput>? CodecCapabilities = null, List<TvDiagnosticErrorInput>? RecentErrors = null,
    Guid? SignageId = null, int? SignageVersion = null, string? SignageName = null, string? SignageError = null,
    string? ServerHostRequested = null, string? SelectedServerEndpoint = null,
    List<TvEndpointCandidateInput>? ConnectionCandidates = null);
public sealed record TvCacheItemInput(string ItemId, string Title, string State, long SizeBytes,
    long? ExpectedBytes = null, string? Error = null);
public sealed record TvDownloadItemInput(string ItemId, string Title, string State, long BytesDownloaded = 0,
    long? ExpectedBytes = null, string? Error = null);
public sealed record TvCodecCapabilityInput(string Kind, string Codec, bool Supported, string? Detail = null);
public sealed record TvDiagnosticErrorInput(DateTimeOffset Timestamp, string Area, string Message, string? ItemId = null);
public sealed record TvEndpointCandidateInput(string Endpoint, string Source, string AddressFamily,
    string Outcome, string? Reason = null);
public sealed record AdminSetupInput(string OrganizationName, string Username, string Password,
    string? DisplayName = null, string? TimeZone = null, string? Email = null,
    string? SiteName = null, string? WeekStartsOn = null);
public sealed record AdminLoginInput(string Username, string Password, string? MfaCode = null);
public sealed record MfaRequirementInput(string? Username);
public sealed record MfaPolicyInput(bool RequireForAllUsers);
public sealed record MfaSetupInput(string CurrentPassword);
public sealed record MfaCodeInput(string Code);
public sealed record MfaDisableInput(string CurrentPassword, string Code);
public sealed record RegistrationInput(string Username, string DisplayName, string Email, string Password, string? Code);
public sealed record VerifyAccountInput(string Token);
public sealed record PasswordRecoveryInput(string Email);
public sealed record PasswordResetInput(string Token, string Password);
public sealed record RequiredPasswordChangeInput(string CurrentPassword, string NewPassword);
public sealed record AccountSetupInput(string Token, string Username, string DisplayName, string Password);
public sealed record ProfileUpdateInput(string DisplayName, string Username, string Email, string? CurrentPassword,
    string? NewPassword);
public sealed record RegistrationSettingsInput(string Mode, string PublicBaseUrl, string EmailProvider,
    string EmailFromAddress, string EmailFromName, string? ApiKey);
public sealed record RegistrationModeInput(string Mode);
public sealed record RegistrationSectionInput(string Mode, string PublicBaseUrl);
public sealed record EmailSettingsInput(string EmailProvider, string EmailFromAddress,
    string EmailFromName, string? ApiKey);
public sealed record TestAccountEmailInput(string Recipient);
public sealed record TroubleshootingEmailSettingsInput(bool Enabled, string Recipient, string TimeLocal);
public sealed record TroubleshootingReviewSettingsInput(bool Enabled, string Provider, string Frequency,
    string TimeLocal, int WeeklyDay = 1, int MonthlyDay = 1, int CustomInterval = 1,
    string CustomUnit = "days");
public sealed record RegistrationCodeInput(string Label, DateTimeOffset? ExpiresAt, int? MaxUses);
public sealed record AudienceSessionInput(string Title, bool ShowLiveResults = false,
    bool AllowResponseChanges = true, int RetentionDays = 7, List<AudienceQuestionInput>? Questions = null);
public sealed record AudienceQuestionInput(Guid? Id, string Type, string Prompt, List<string>? Options,
    bool Required = true, int MaxSelections = 1, bool ModerateResponses = true);
public sealed record AudienceResponseInput(string ParticipantToken, List<AudienceAnswerInput>? Answers);
public sealed record AudienceAnswerInput(Guid QuestionId, List<string>? Choices, string? Text);
public sealed record AudienceModerationInput(string? Status);
public sealed record AudienceDisplayMediaInput(bool ShowResults = true, int ResultDelaySeconds = 0);
public sealed record LessonUpdateInput(string? Title, DateOnly? Date, DateTimeOffset? AvailableFrom,
    DateTimeOffset? ExpiresAt, DateTimeOffset? DesignatedStartAt, bool? PreRollEnabled, Guid? CountdownItemId,
    bool ClearCountdown = false, bool ClearAvailableFrom = false, bool ClearExpiresAt = false,
    bool ClearDesignatedStartAt = false, DateTimeOffset? PreRollStartsAt = null, bool ClearPreRollStartsAt = false,
    int? VolumePercent = null, bool? Muted = null, string? SubstituteNotes = null,
    string? PreRollMonitorUrl = null, bool ClearPreRollMonitorUrl = false);
public sealed record PlaylistItemUpdateInput(string? Title, string? Type, string? Role, Guid? MediaId,
    long? DurationMs, long? StartMs, long? EndMs, int? VolumePercent, int? ImageDurationSeconds,
    int? EstimatedDurationSeconds, string? EndBehavior, bool? AllowSkip, bool ClearEndMs = false,
    bool ClearImageDuration = false, bool ClearEstimatedDuration = false, string? Notes = null,
    int? FadeInMs = null, int? FadeOutMs = null, bool? NormalizeAudio = null,
    List<CuePointInput>? CuePoints = null, string? FitMode = null, int? RotationDegrees = null,
    int? CropLeftPercent = null, int? CropTopPercent = null, int? CropRightPercent = null,
    int? CropBottomPercent = null, bool? Muted = null, int? PlaybackRatePercent = null,
    int? RepeatCount = null, string? BackgroundColor = null, string? TransitionStyle = null,
    int? TransitionDurationMs = null, bool? FlexibleTime = null, Guid? ActivityDefinitionId = null);
public sealed record CuePointInput(string Name, long PositionMs);
public sealed record PlaylistReorderInput(List<Guid>? ItemIds);
public sealed record LessonBulkInput(List<Guid> LessonIds, string Action, Guid? ClassId = null,
    int? ShiftDays = null, string? TitlePrefix = null);
public sealed record LessonRelocateInput(string Action, Guid ClassId, DateOnly Date, string? Title = null);
public sealed record PlaylistItemBulkInput(List<Guid> ItemIds, string Action, string? Role = null,
    int? VolumePercent = null, string? EndBehavior = null, bool? AllowSkip = null, string? TitlePrefix = null);
public sealed record ScreenUpdateInput(string? Name, Guid? AssignedClassId, bool? VolunteerMode,
    bool ClearAssignment = false, string? TagsCsv = null, string? Site = null,
    bool? AllowDiagnosticScreenshots = null, string? SignageOrientation = null,
    int? SignageWidth = null, int? SignageHeight = null, bool? SignageOnly = null,
    bool? PermanentPairing = null, bool AllowUnsupportedContent = false,
    Guid? AssignedSignageId = null, bool ClearSignageAssignment = false);
public sealed record ScreenAssignmentCheckInput(Guid? AssignedClassId);
public sealed record ScreenControlInput(string Action, Guid? LessonId = null, Guid? ItemId = null, long? PositionMs = null);
public sealed record UserInput(string Username, string DisplayName, string? Email, string Role, string? Password,
    bool Disabled = false, List<string>? Permissions = null);
public sealed record UserInvitationInput(string Email, string Role, List<string>? Permissions = null,
    string? DisplayName = null);
public sealed record TemporaryPasswordInput(string Password);
public sealed record OrganizationInput(string Name, string SiteName, string TimeZone, string WeekStartsOn,
    int DefaultLessonDurationMinutes, int DefaultRetentionDays, string PrimaryColor, string AccentColor,
    string? NavigationTextColor, string? SelectedTabColor, string WelcomeMessage,
    bool? AdaptiveTranscodingEnabled = null, int? TranscodeLeadDays = null,
    bool? RequireLocalRoomControllers = null, bool? HardwareAccelerationEnabled = null,
    List<string>? SignageSourceAllowlist = null, bool? SignageEnabled = null);
public sealed record SignageAvailabilityInput(bool Enabled);
public sealed record StorageLimitInput(long LimitBytes);
public sealed record LocalHostnameInput(string Hostname, bool? Ipv6Enabled = null);
public sealed record HttpPortInput(int Port);
public sealed record CloudflareTunnelInput(bool Enabled, string? PublicHostname, string? Token,
    bool AcknowledgedRemoteExposure = false);
public sealed record SignageInput(string Name, string Mode, bool Enabled, int Priority, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, string? Message, string? BackgroundColor, string? TextColor, Guid? MediaAssetId,
    string? TargetTagsCsv, string? Recurrence = null, DateOnly? ScheduleStartDate = null, DateOnly? ScheduleEndDate = null,
    int? StartMinutes = null, int? EndMinutes = null, List<int>? DaysOfWeek = null,
    List<DateOnly>? ExcludedDates = null, List<Guid>? TargetScreenIds = null,
    string? LayoutPreset = null, List<SignageZoneInput>? Zones = null, Guid? LayoutId = null,
    Guid? ContentPlaylistId = null, int VolumePercent = 100, string? DisplayPower = null,
    bool KioskEnabled = false, string? KioskInteractionUrl = null, int KioskTimeoutSeconds = 60,
    bool KioskShowCloseButton = true, bool KioskShowTouchIndicator = true, bool KioskVirtualKeyboard = false);
public sealed record SignageZoneInput(string Id, string Type, string? Title = null, string? Content = null,
    Guid? MediaAssetId = null, string? SourceUrl = null, int X = 0, int Y = 0, int Width = 100, int Height = 100,
    string? BackgroundColor = null, string? TextColor = null, string? AccentColor = null, int RefreshMinutes = 15,
    int Rotation = 0, int ZIndex = 0, int Opacity = 100, string? Fit = null,
    bool Locked = false, bool Hidden = false, bool FlipX = false, bool FlipY = false,
    string? GroupId = null, string? LockMode = null, string? RichTextJson = null,
    string? FontFamily = null, int FontSize = 48, int FontWeight = 600, bool Italic = false,
    bool Underline = false, int LineHeightPercent = 120, string? TextAlign = null,
    string? Shape = null, string? StrokeColor = null, int StrokeWidth = 0, int CornerRadius = 0,
    string? IconName = null, string? QrValue = null, int TickerSpeed = 60,
    string? QrLabelTop = null, string? QrLabelBottom = null, string? QrLabelLeft = null, string? QrLabelRight = null,
    string? QrPlacement = null, int QrSizePercent = 42,
    DateTimeOffset? CounterTargetAt = null, bool CounterRepeatWeekly = false, string? CredentialKey = null,
    string? ClockDisplay = null, string? ClockTimeFormat = null, string? ClockDateFormat = null,
    string? ClockOrder = null, int ClockTimeFontSize = 64, int ClockDateFontSize = 28,
    string? WeatherProvider = null, string? WeatherLocation = null,
    double? WeatherLatitude = null, double? WeatherLongitude = null, string? WeatherPostalCode = null,
    string? WeatherUnits = null, string? WeatherFields = null,
    Guid? ContentPlaylistId = null, bool StreamOverrideWhenLive = false,
    int ContentPadding = 6, int ContentScale = 100, string? VerticalAlign = null,
    DateTimeOffset? StreamOverrideStartsAt = null, DateTimeOffset? StreamOverrideEndsAt = null,
    int MediaScale = 100, int MediaOffsetX = 0, int MediaOffsetY = 0, bool MediaAllowOverflow = false,
    string? WifiNetworkName = null, string? WifiPassword = null, string? WifiSecurity = null,
    bool WifiHidden = false, string? WeatherIconStyle = null, string? WeatherLayout = null,
    int WeatherIconSize = 84, int WeatherTitleSize = 28, int WeatherTemperatureSize = 58,
    int WeatherDetailsSize = 22,
    bool ClockShowPeriod = true, bool ClockShowWeekday = true, bool ClockShowYear = true,
    int CalendarMaxItems = 0, string? CalendarFields = null,
    Guid? AudienceSessionId = null, string? AudienceCode = null, bool AudienceShowResults = true,
    int AudienceResultDelaySeconds = 0,
    int FontScalePercent = 10);
public sealed record SignageLayoutResourceInput(string Name, string? Folder, string? Description,
    bool IsTemplate, string? BackgroundColor, int CanvasWidth, int CanvasHeight, int SafeAreaPercent,
    List<SignageZoneInput>? Zones, Guid? BackgroundAudioAssetId = null, string? ThumbnailDataUrl = null);
public sealed record SignageLayoutSavePublishInput(Guid? Id, SignageLayoutResourceInput Layout,
    bool PushToScreens = true);
public sealed record SignageContentPlaylistItemInput(string Id, string Kind, string? Title = null,
    Guid? LayoutId = null, Guid? MediaAssetId = null, Guid? NestedPlaylistId = null,
    string? AppType = null, string? SourceUrl = null, int DurationSeconds = 10,
    string? Transition = null, bool Hidden = false, bool Transparent = false, string? TagsCsv = null,
    int VolumePercent = 100, bool Muted = false, int FadeInMs = 0, int FadeOutMs = 0,
    string? Fit = null, string? Notes = null);
public sealed record SignageContentPlaylistInput(string Name, string? Folder, string? PlaybackMode,
    string? Synchronization, List<SignageContentPlaylistItemInput>? Items);
public sealed record SignagePlaylistSaveInput(Guid? Id, SignageContentPlaylistInput Playlist);
public sealed record SignageSignInput(string Name, Guid LayoutId,
    Dictionary<string, Guid>? PlaylistAssignments, List<Guid>? ScreenIds,
    bool AllowUnsupportedContent = false);
public sealed record SignagePublishInput(bool PushToScreens = true);
public sealed record SignageBulkAssignmentInput(List<Guid>? SignageIds, List<Guid>? ScreenIds,
    string? TargetTagsCsv, bool Publish = true);
public sealed record SignageSeriesEditInput(string Scope, DateOnly EffectiveDate, SignageInput Changes);
public sealed record SignageEmergencyTemplateInput(string Name, string? Severity, string? Message,
    string? BackgroundColor, string? TextColor, Guid? MediaAssetId, string? TargetTagsCsv,
    int DefaultDurationMinutes = 30);
public sealed record SignageEmergencyActivateInput(int? DurationMinutes = null, List<Guid>? ScreenIds = null,
    string? TargetTagsCsv = null);
public sealed record SignageProofInput(Guid ScreenId, Guid SignageId, int Version, string? SignageName,
    string Event, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null,
    long DurationMs = 0, string? Error = null);
public sealed record SignageScreenFormatInput(string? Orientation, int? Width = null, int? Height = null);
public sealed record SignageCredentialInput(string Key, string Kind, string? Username, string? HeaderName, string Secret);
public sealed record LinkInput(string Url, string? Title, bool Download = false, bool Persistent = true,
    Guid? LessonId = null, string? Folder = null, string? TagsCsv = null, bool ImportPresentation = false);
public sealed record UploadCreateInput(string FileName, long TotalBytes, string? ContentType = null,
    string? ExpectedSha256 = null, bool Persistent = false, Guid? LessonId = null,
    string? Folder = null, string? TagsCsv = null, long? DurationMs = null);
public sealed record MediaPreflightInput(string FileName, long TotalBytes,
    string? ContentType = null, bool Persistent = false, Guid? LessonId = null);
public sealed record UploadCompleteInput(string? FileName = null, string? ContentType = null, int? TotalChunks = null,
    long? DurationMs = null, bool? Persistent = null, Guid? LessonId = null,
    string? Folder = null, string? TagsCsv = null);
public sealed record UploadQuotaPolicyInput(long MaxFileBytes = 0, long MaxDailyBytes = 0,
    int MaxActiveSessionsPerUser = 3, Dictionary<string, long>? UserDailyBytes = null,
    Dictionary<string, long>? RoleDailyBytes = null, Dictionary<string, long>? ClassDailyBytes = null,
    List<string>? AllowedVideoCodecs = null, List<string>? AllowedAudioCodecs = null);
public sealed record MediaRenameInput(Guid MediaId, string FileName);
public sealed record MediaBulkInput(List<Guid> MediaIds, string? Action, DateOnly? DeleteOn = null,
    string? Folder = null, string? TagsCsv = null, List<MediaRenameInput>? Renames = null);
public sealed record MediaOrganizeInput(string? FileName, string? Folder, string? TagsCsv);
public sealed record MediaTaxonomyInput(List<string>? Folders, List<string>? Tags);
public sealed record PresentationLessonInput(Guid LessonId, int? ImageDurationSeconds = null);
public sealed record LessonTemplateFromLessonInput(Guid LessonId, string Name, string? Description = null);
public sealed record LessonTemplateReplaceInput(Guid LessonId);
public sealed record LessonTemplateUpdateInput(string Name, string? Description, string? DefaultTitle,
    int? DefaultStartMinutes, int? PreRollLeadMinutes, bool PreRollEnabled, bool KeepOffline, int DownloadDaysBefore,
    int? VolumePercent = null, bool? Muted = null);
public sealed record LessonTemplateInstantiateInput(Guid ClassId, DateOnly Date, string? Title = null, int? StartMinutes = null);
public sealed record RecurringScheduleInput(Guid TemplateId, Guid ClassId, string Name, string Frequency,
    int Interval, int? DayOfWeek, int? DayOfMonth, DateOnly StartDate, DateOnly? EndDate,
    int? StartMinutes, string? TitlePattern, List<DateOnly>? CustomDates, List<DateOnly>? ExcludedDates,
    bool Enabled = true, int GenerateDaysAhead = 90);
public sealed record RecurringScheduleGenerateInput(DateOnly? ThroughDate = null);
public sealed record RecurringScheduleExceptionInput(DateOnly Date, bool Excluded = true);
