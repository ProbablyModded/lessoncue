import { confirmAction } from "../../AccessibleDialogs";
import { FormEvent, useEffect, useState } from "react";
import { api, waitForVersion } from "../api";
import { Audit, Backup, BackupDestinationProvider, BackupPolicyStatus, BackupPreview, BackupRestoreResult, Bootstrap, CloudflareTunnelStatus, HardwareAccelerationStatus, HttpPortStatus, JoinAddressStatus, LocalAddressStatus, MediaTaxonomy, MigrationTransferGrant, RecycleItem, SettingsSection, ShortenerReport, ShortenerSettings, ShortenerTestResult, ShortenerTunnelPlan, StorageStatus, SupportBundle, UpdateStatus, UploadQuotaPolicy, YouTubeRuntimeStatus } from "../models";
import { CollapsibleSettingsSection, Definition, Empty, Field, Modal, PageHead, StorageMeter } from "../ui";
import { RegistrationSettingsPanel, TroubleshootingLogPanel } from "./Users";
import { AuthenticatorMfaPanel } from "./Mfa";
import { cleanReleaseNotes, errorText, formatBytes, parseStringArray, quotaLimitsFromText, quotaLimitsToText, timeAgo } from "../utils";

type RemoteBackupForm = {
  url: string;
  folderName: string;
  authentication: "none" | "basic" | "bearer" | "oauth";
  username: string;
  secret: string;
  retentionCount: string;
  retentionDays: string;
  googleDriveClientId: string;
  googleDriveClientSecret: string;
};

const emptyRemoteBackupForm = (): RemoteBackupForm => ({
  url: "",
  folderName: "",
  authentication: "basic",
  username: "",
  secret: "",
  retentionCount: "7",
  retentionDays: "30",
  googleDriveClientId: "",
  googleDriveClientSecret: "",
});

const remoteProviderLabel = (provider: BackupDestinationProvider) =>
  provider === "googledrive"
    ? "Google Drive"
    : provider === "nextcloud"
      ? "Nextcloud"
      : provider === "owncloud"
        ? "ownCloud"
        : "Other WebDAV destination";

export function Settings({
  bootstrap,
  backups,
  audit,
  refresh,
  notify,
  canAppSettings,
  canServiceSettings,
  canBackups,
  canUpdates,
  settingsSection,
  setSettingsSection,
}: {
  bootstrap: Bootstrap;
  backups: Backup[];
  audit: Audit[];
  refresh: () => void;
  notify: (s: string) => void;
  canAppSettings: boolean;
  canServiceSettings: boolean;
  canBackups: boolean;
  canUpdates: boolean;
  settingsSection: SettingsSection;
  setSettingsSection: (section: SettingsSection) => void;
}) {
  const canManageApp = canAppSettings || canServiceSettings;
  const [automaticStorage, setAutomaticStorage] = useState(
    bootstrap.storage.automaticAllocation,
  );
  const [allocationGb, setAllocationGb] = useState(
    (bootstrap.storage.allocationBytes / 1024 ** 3).toFixed(1),
  );
  const uploadPolicy = bootstrap.uploadQuotaPolicy;
  const [maxUploadFileGb, setMaxUploadFileGb] = useState(
    uploadPolicy?.maxFileBytes
      ? (uploadPolicy.maxFileBytes / 1024 ** 3).toFixed(1)
      : "0",
  );
  const [maxUploadDailyGb, setMaxUploadDailyGb] = useState(
    uploadPolicy?.maxDailyBytes
      ? (uploadPolicy.maxDailyBytes / 1024 ** 3).toFixed(1)
      : "0",
  );
  const [maxActiveUploads, setMaxActiveUploads] = useState(
    String(uploadPolicy?.maxActiveSessionsPerUser || 3),
  );
  const [uploadUserLimits, setUploadUserLimits] = useState(
    quotaLimitsToText(uploadPolicy?.userDailyBytes),
  );
  const [uploadRoleLimits, setUploadRoleLimits] = useState(
    quotaLimitsToText(uploadPolicy?.roleDailyBytes),
  );
  const [uploadClassLimits, setUploadClassLimits] = useState(
    quotaLimitsToText(uploadPolicy?.classDailyBytes),
  );
  const [allowedVideoCodecs, setAllowedVideoCodecs] = useState(
    uploadPolicy?.allowedVideoCodecs.join(", ") || "",
  );
  const [allowedAudioCodecs, setAllowedAudioCodecs] = useState(
    uploadPolicy?.allowedAudioCodecs.join(", ") || "",
  );
  const [adaptiveTranscoding, setAdaptiveTranscoding] = useState(
    bootstrap.settings.adaptiveTranscodingEnabled,
  );
  const [transcodeLeadDays, setTranscodeLeadDays] = useState(
    String(bootstrap.settings.transcodeLeadDays),
  );
  const [hardwareAcceleration, setHardwareAcceleration] = useState(
    bootstrap.settings.hardwareAccelerationEnabled,
  );
  const [checkingHardware, setCheckingHardware] = useState(false);
  const [mediaFolders, setMediaFolders] = useState(
    bootstrap.mediaTaxonomy.folders.join("\n"),
  );
  const [mediaTags, setMediaTags] = useState(
    bootstrap.mediaTaxonomy.tags.join("\n"),
  );
  const [checking, setChecking] = useState(false);
  const [installing, setInstalling] = useState(false);
  const [youtubeRuntime, setYoutubeRuntime] = useState<YouTubeRuntimeStatus | undefined>(
    bootstrap.youtubeRuntime,
  );
  const [checkingYoutubeRuntime, setCheckingYoutubeRuntime] = useState(false);
  const [updatingYoutubeRuntime, setUpdatingYoutubeRuntime] = useState(false);
  const [fixedPairing, setFixedPairing] = useState(bootstrap.pairingFixed);
  const [pairingPin, setPairingPin] = useState(bootstrap.pairingPin || "");
  const [controllerPin, setControllerPin] = useState(bootstrap.controllerPin || "");
  const [localHostname, setLocalHostname] = useState(
    bootstrap.localAddress.hostname,
  );
  const [localIpv6Enabled, setLocalIpv6Enabled] = useState(bootstrap.localAddress.ipv6Enabled ?? true);
  const [httpPort, setHttpPort] = useState(String(bootstrap.httpPort.port));
  const [tunnelEnabled, setTunnelEnabled] = useState(
    bootstrap.cloudflareTunnel.enabled,
  );
  const [tunnelHostname, setTunnelHostname] = useState(
    bootstrap.cloudflareTunnel.publicHostname || "",
  );
  const [tunnelToken, setTunnelToken] = useState("");
  const [tunnelAcknowledged, setTunnelAcknowledged] = useState(false);
  const [tunnelBusy, setTunnelBusy] = useState(false);
  const [restorePreview, setRestorePreview] = useState<BackupPreview>();
  const [restoreResult, setRestoreResult] = useState<BackupRestoreResult>();
  const [restoreConfirmation, setRestoreConfirmation] = useState("");
  const [restoreBusy, setRestoreBusy] = useState(false);
  const [backupBusy, setBackupBusy] = useState(false);
  const [backupPassword, setBackupPassword] = useState("");
  const [backupPasswordConfirmation, setBackupPasswordConfirmation] =
    useState("");
  const [backupIncludeSecrets, setBackupIncludeSecrets] = useState(false);
  const [restorePassword, setRestorePassword] = useState("");
  const [backupPolicy, setBackupPolicy] = useState<BackupPolicyStatus>();
  const [backupDrill, setBackupDrill] = useState<BackupPreview>();
  const [backupPolicyBusy, setBackupPolicyBusy] = useState(false);
  const [googleDriveBusy, setGoogleDriveBusy] = useState(false);
  const [policyEnabled, setPolicyEnabled] = useState(false);
  const [policyFrequency, setPolicyFrequency] = useState<"daily" | "weekly">(
    "daily",
  );
  const [policyHour, setPolicyHour] = useState("2");
  const [policyWeeklyDay, setPolicyWeeklyDay] = useState("0");
  const [policyMediaMode, setPolicyMediaMode] = useState<
    "backup" | "sync" | "exclude"
  >("backup");
  const [policyRetentionCount, setPolicyRetentionCount] = useState("7");
  const [policyRetentionDays, setPolicyRetentionDays] = useState("30");
  const [policyIncludeSecrets, setPolicyIncludeSecrets] = useState(false);
  const [policyPassword, setPolicyPassword] = useState("");
  const [policyRemotes, setPolicyRemotes] = useState<
    Record<BackupDestinationProvider, RemoteBackupForm>
  >({
    googledrive: emptyRemoteBackupForm(),
    nextcloud: emptyRemoteBackupForm(),
    owncloud: emptyRemoteBackupForm(),
    webdav: emptyRemoteBackupForm(),
  });
  const [migrationGrant, setMigrationGrant] =
    useState<MigrationTransferGrant>();
  const [migrationSourceAddress, setMigrationSourceAddress] = useState("");
  const [migrationToken, setMigrationToken] = useState("");
  const [migrationPassword, setMigrationPassword] = useState("");
  const [migrationBusy, setMigrationBusy] = useState(false);
  const [diagnostics, setDiagnostics] = useState<SupportBundle>();
  const [diagnosticsBusy, setDiagnosticsBusy] = useState(false);
  const [diagnosticsError, setDiagnosticsError] = useState("");
  const [recycleItems, setRecycleItems] = useState<RecycleItem[]>([]);
  const loadRecycleBin = () =>
    canManageApp
      ? api<RecycleItem[]>("/api/v1/recycle-bin")
          .then(setRecycleItems)
          .catch(() => undefined)
      : Promise.resolve();
  useEffect(() => {
    if (canManageApp)
      void api<RecycleItem[]>("/api/v1/recycle-bin")
        .then(setRecycleItems)
        .catch(() => undefined);
  }, [canManageApp]);
  useEffect(() => {
    if (!canBackups) return;
    void api<BackupPolicyStatus>("/api/v1/backups/policy")
      .then((status) => {
        setBackupPolicy(status);
        setPolicyEnabled(status.enabled);
        setPolicyFrequency(status.frequency);
        setPolicyHour(String(status.hourLocal));
        setPolicyWeeklyDay(String(status.weeklyDay ?? 0));
        setPolicyMediaMode(
          status.mediaMode || (status.includeMedia ? "backup" : "exclude"),
        );
        setPolicyRetentionCount(String(status.retentionCount));
        setPolicyRetentionDays(String(status.retentionDays));
        setPolicyIncludeSecrets(status.secretHandling === "include");
        const remotes: Record<BackupDestinationProvider, RemoteBackupForm> = {
          googledrive: emptyRemoteBackupForm(),
          nextcloud: emptyRemoteBackupForm(),
          owncloud: emptyRemoteBackupForm(),
          webdav: emptyRemoteBackupForm(),
        };
        const destinations = status.destinations?.length
          ? status.destinations
          : status.remoteWebDavUrl
            ? [
                {
                  provider: "webdav" as const,
                  enabled: true,
                  webDavUrl: status.remoteWebDavUrl,
                  authentication: status.remoteAuthentication,
                  username: status.remoteUsername,
                  secretConfigured: status.remoteSecretConfigured,
                  retentionCount: status.retentionCount,
                  retentionDays: status.retentionDays,
                },
              ]
            : [];
        destinations.forEach((destination) => {
          remotes[destination.provider] = {
            url: destination.webDavUrl || "",
            folderName: destination.folderName || "",
            authentication: destination.authentication,
            username: destination.username || "",
            secret: "",
            retentionCount: String(destination.retentionCount),
            retentionDays: String(destination.retentionDays),
            googleDriveClientId: destination.googleDriveClientId || "",
            googleDriveClientSecret: "",
          };
        });
        setPolicyRemotes(remotes);
      })
      .catch((error) => notify(errorText(error)));
  }, [canBackups, notify]);
  useEffect(() => {
    if (!canServiceSettings) return;
    void api<SupportBundle>("/api/v1/support/bundle")
      .then((snapshot) => {
        setDiagnostics(snapshot);
        setDiagnosticsError("");
      })
      .catch((error) => setDiagnosticsError(errorText(error)));
  }, [canServiceSettings]);
  useEffect(() => {
    setYoutubeRuntime(bootstrap.youtubeRuntime);
  }, [bootstrap.youtubeRuntime]);
  async function refreshDiagnostics() {
    setDiagnosticsBusy(true);
    try {
      setDiagnostics(await api<SupportBundle>("/api/v1/support/bundle"));
      setDiagnosticsError("");
      notify("System diagnostics refreshed.");
    } catch (error) {
      setDiagnosticsError(errorText(error));
    } finally {
      setDiagnosticsBusy(false);
    }
  }
  async function saveOrganization(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const values = Object.fromEntries(form);
    const value = (name: string, fallback: string) =>
      String(values[name] ?? fallback);
    const controllerValues = form.getAll("requireLocalRoomControllers");
    try {
      await api("/api/v1/organization", {
        method: "PUT",
        body: JSON.stringify({
          // The settings pages deliberately split one legacy form into
          // focused cards. Keep the server's complete input contract intact
          // while letting each card submit only the fields it owns.
          name: value("name", o.name),
          siteName: value("siteName", o.siteName),
          timeZone: value("timeZone", o.timeZone),
          weekStartsOn: value("weekStartsOn", o.weekStartsOn),
          welcomeMessage: value("welcomeMessage", o.welcomeMessage),
          defaultLessonDurationMinutes: Number(
            values.defaultLessonDurationMinutes ?? o.defaultLessonDurationMinutes,
          ),
          defaultRetentionDays: Number(
            values.defaultRetentionDays ?? o.defaultRetentionDays,
          ),
          primaryColor: value("primaryColor", o.primaryColor),
          accentColor: value("accentColor", o.accentColor),
          navigationTextColor: value("navigationTextColor", o.navigationTextColor),
          selectedTabColor: value("selectedTabColor", o.selectedTabColor),
          requireLocalRoomControllers: form.has("requireLocalRoomControllers")
            ? controllerValues.includes("on")
            : o.requireLocalRoomControllers,
          signageSourceAllowlist: form.has("signageSourceAllowlist")
            ? String(values.signageSourceAllowlist || "")
            .split(/[\s,]+/)
            .map((value) => value.trim())
            .filter(Boolean)
            : parseStringArray(o.signageSourceAllowlistJson),
        }),
      });
      refresh();
      notify("Organization settings saved.");
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function backup(full: boolean) {
    if (backupPassword.length < 12) {
      notify("Use a backup password with at least 12 characters.");
      return;
    }
    if (backupPassword !== backupPasswordConfirmation) {
      notify("The backup password confirmation does not match.");
      return;
    }
    setBackupBusy(true);
    try {
      await api("/api/v1/backups", {
        method: "POST",
        body: JSON.stringify({
          full,
          password: backupPassword,
          secretHandling: backupIncludeSecrets ? "include" : "exclude",
        }),
      });
      setBackupPassword("");
      setBackupPasswordConfirmation("");
      refresh();
      notify(
        full
          ? "Encrypted full backup created."
          : "Encrypted configuration backup created.",
      );
    } catch (e) {
      notify(errorText(e));
    } finally {
      setBackupBusy(false);
    }
  }

  async function verifyBackup(item: Backup) {
    try {
      const preview = await api<BackupPreview>(`/api/v1/backups/${item.id}/verify`, {
        method: "POST",
        body: JSON.stringify({ password: restorePassword }),
      });
      setBackupDrill(preview);
      notify(
        `Backup verified: ${preview.fileCount} files, authenticated manifest, and a healthy database.`,
      );
    } catch (error) {
      notify(errorText(error));
    }
  }
  function updatePolicyRemote(
    provider: BackupDestinationProvider,
    patch: Partial<RemoteBackupForm>,
  ) {
    setPolicyRemotes((current) => ({
      ...current,
      [provider]: { ...current[provider], ...patch },
    }));
  }

  async function connectGoogleDrive() {
    const popup = window.open(
      "about:blank",
      "lessoncue-google-drive-oauth",
      "popup,width=600,height=700",
    );
    if (!popup) {
      notify("Allow pop-ups for LessonCue, then choose Connect Google Drive again.");
      return;
    }

    setGoogleDriveBusy(true);
    const timeout = window.setTimeout(() => {
      window.removeEventListener("message", onMessage);
      popup.close();
      setGoogleDriveBusy(false);
      notify("Google Drive authorization timed out. Try connecting again.");
    }, 5 * 60 * 1000);
    const finish = () => {
      window.clearTimeout(timeout);
      window.removeEventListener("message", onMessage);
      setGoogleDriveBusy(false);
    };
    const onMessage = (event: MessageEvent) => {
      const result = event.data as {
        type?: string;
        connected?: boolean;
      } | null;
      if (
        event.origin !== window.location.origin ||
        event.source !== popup ||
        result?.type !== "lessoncue-google-drive-oauth"
      ) return;
      finish();
      popup.close();
      if (!result.connected) {
        notify("Google Drive was not connected. Check the OAuth settings and try again.");
        return;
      }
      void api<BackupPolicyStatus>("/api/v1/backups/policy")
        .then((status) => {
          setBackupPolicy(status);
          notify("Google Drive connected. It is ready as a backup destination.");
        })
        .catch((error) => notify(errorText(error)));
    };
    window.addEventListener("message", onMessage);

    try {
      const authorization = await api<{
        authorizationUrl: string;
        redirectUri: string;
      }>("/api/v1/backups/google-drive/connect", { method: "POST" });
      popup.location.replace(authorization.authorizationUrl);
    } catch (error) {
      finish();
      popup.close();
      notify(errorText(error));
    }
  }

  async function saveBackupPolicy(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBackupPolicyBusy(true);
    try {
      const status = await api<BackupPolicyStatus>("/api/v1/backups/policy", {
        method: "PUT",
        body: JSON.stringify({
          enabled: policyEnabled,
          frequency: policyFrequency,
          hourLocal: Number(policyHour),
          weeklyDay:
            policyFrequency === "weekly" ? Number(policyWeeklyDay) : null,
          includeMedia: policyMediaMode === "backup",
          mediaMode: policyMediaMode,
          retentionCount: Number(policyRetentionCount),
          retentionDays: Number(policyRetentionDays),
          secretHandling: policyIncludeSecrets ? "include" : "exclude",
          backupPassword: policyPassword || null,
          remoteWebDavUrl: policyRemotes.webdav.url || null,
          remoteAuthentication: policyRemotes.webdav.authentication,
          remoteUsername: policyRemotes.webdav.username || null,
          remoteSecret: policyRemotes.webdav.secret || null,
          destinations: (Object.keys(policyRemotes) as BackupDestinationProvider[])
            .map((provider) => ({ provider, form: policyRemotes[provider] }))
            .filter(({ provider, form }) => provider === "googledrive"
              ? form.googleDriveClientId.trim()
              : form.url.trim())
            .map(({ provider, form }) => ({
              provider,
              webDavUrl: provider === "googledrive" ? null : form.url.trim(),
              folderName: form.folderName.trim() || null,
              authentication: provider === "googledrive" ? "oauth" as const : form.authentication,
              username: form.username || null,
              secret: form.secret || null,
              retentionCount: Number(form.retentionCount),
              retentionDays: Number(form.retentionDays),
              googleDriveClientId: form.googleDriveClientId.trim() || null,
              googleDriveClientSecret: form.googleDriveClientSecret || null,
            })),
        }),
      });
      setBackupPolicy(status);
      setPolicyPassword("");
      setPolicyRemotes((current) =>
        Object.fromEntries(
        Object.entries(current).map(([provider, form]) => [
          provider,
          { ...form, secret: "", googleDriveClientSecret: "" },
        ]),
        ) as Record<BackupDestinationProvider, RemoteBackupForm>,
      );
      notify("Scheduled backup policy saved.");
    } catch (error) {
      notify(errorText(error));
    } finally {
      setBackupPolicyBusy(false);
    }
  }
  async function runBackupPolicy() {
    setBackupPolicyBusy(true);
    try {
      const status = await api<BackupPolicyStatus>(
        "/api/v1/backups/policy/run",
        { method: "POST", body: "{}" },
      );
      setBackupPolicy(status);
      refresh();
      notify("Scheduled backup created, verified, and delivered.");
    } catch (error) {
      notify(errorText(error));
    } finally {
      setBackupPolicyBusy(false);
    }
  }
  async function createMigrationLink(item: Backup) {
    setMigrationBusy(true);
    try {
      const grant = await api<MigrationTransferGrant>(
        `/api/v1/backups/${item.id}/migration-link`,
        { method: "POST", body: "{}" },
      );
      setMigrationGrant(grant);
    } catch (error) {
      notify(errorText(error));
    } finally {
      setMigrationBusy(false);
    }
  }
  async function previewMigration(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setMigrationBusy(true);
    try {
      const preview = await api<BackupPreview>(
        "/api/v1/backups/migration/preview",
        {
          method: "POST",
          body: JSON.stringify({
            sourceAddress: migrationSourceAddress,
            transferToken: migrationToken,
            password: migrationPassword,
          }),
        },
      );
      setRestorePreview(preview);
      setRestoreResult(undefined);
      setRestoreConfirmation("");
      setMigrationToken("");
      setMigrationPassword("");
    } catch (error) {
      notify(errorText(error));
    } finally {
      setMigrationBusy(false);
    }
  }
  async function previewBackupRestore(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setRestoreBusy(true);
    try {
      const preview = await api<BackupPreview>(
        "/api/v1/backups/restore/preview",
        { method: "POST", body: form },
      );
      setRestorePreview(preview);
      setRestoreResult(undefined);
      setRestoreConfirmation("");
      setRestorePassword("");
    } catch (e) {
      notify(errorText(e));
    } finally {
      setRestoreBusy(false);
    }
  }
  async function restoreBackup() {
    if (!restorePreview || restoreConfirmation !== "RESTORE") return;
    setRestoreBusy(true);
    try {
      const result = await api<BackupRestoreResult>("/api/v1/backups/restore", {
        method: "POST",
        body: JSON.stringify({
          restoreId: restorePreview.restoreId,
          confirmation: restoreConfirmation,
        }),
      });
      setRestoreResult(result);
      notify(
        "Backup restored. Review the safety-backup details, then reload LessonCue.",
      );
    } catch (e) {
      notify(errorText(e));
    } finally {
      setRestoreBusy(false);
    }
  }
  async function saveStorage(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      const limitBytes = automaticStorage
        ? 0
        : Math.round(Number(allocationGb) * 1024 ** 3);
      await api<StorageStatus>("/api/v1/storage", {
        method: "PUT",
        body: JSON.stringify({ limitBytes }),
      });
      refresh();
      notify(
        automaticStorage
          ? "Storage allocation will adjust automatically."
          : "Storage allocation saved.",
      );
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function saveUploadPolicy(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      const toBytes = (value: string) =>
        value.trim() === "" || Number(value) === 0
          ? 0
          : Math.round(Number(value) * 1024 ** 3);
      const policy = await api<UploadQuotaPolicy>("/api/v1/upload-policy", {
        method: "PUT",
        body: JSON.stringify({
          maxFileBytes: toBytes(maxUploadFileGb),
          maxDailyBytes: toBytes(maxUploadDailyGb),
          maxActiveSessionsPerUser: Number(maxActiveUploads),
          userDailyBytes: quotaLimitsFromText(uploadUserLimits),
          roleDailyBytes: quotaLimitsFromText(uploadRoleLimits),
          classDailyBytes: quotaLimitsFromText(uploadClassLimits),
          allowedVideoCodecs: allowedVideoCodecs
            .split(",")
            .map((value) => value.trim())
            .filter(Boolean),
          allowedAudioCodecs: allowedAudioCodecs
            .split(",")
            .map((value) => value.trim())
            .filter(Boolean),
        }),
      });
      setMaxUploadFileGb(
        policy.maxFileBytes
          ? (policy.maxFileBytes / 1024 ** 3).toFixed(1)
          : "0",
      );
      setMaxUploadDailyGb(
        policy.maxDailyBytes
          ? (policy.maxDailyBytes / 1024 ** 3).toFixed(1)
          : "0",
      );
      setUploadUserLimits(quotaLimitsToText(policy.userDailyBytes));
      setUploadRoleLimits(quotaLimitsToText(policy.roleDailyBytes));
      setUploadClassLimits(quotaLimitsToText(policy.classDailyBytes));
      notify("Upload limits saved.");
      refresh();
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function saveAdaptiveTranscoding(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      await api("/api/v1/organization", {
        method: "PUT",
        body: JSON.stringify({
          ...o,
          adaptiveTranscodingEnabled: adaptiveTranscoding,
          transcodeLeadDays: Number(transcodeLeadDays),
          hardwareAccelerationEnabled: hardwareAcceleration,
        }),
      });
      refresh();
      notify(
        hardwareAcceleration && bootstrap.hardwareAcceleration.available
          ? `${bootstrap.hardwareAcceleration.engine} will accelerate local video conversion.`
          : "Playback profile settings saved.",
      );
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function checkHardware() {
    setCheckingHardware(true);
    try {
      const status = await api<HardwareAccelerationStatus>(
        "/api/v1/hardware-acceleration/check",
        { method: "POST", body: "{}" },
      );
      refresh();
      notify(status.available ? `${status.engine} is ready.` : status.message);
    } catch (e) {
      notify(errorText(e));
    } finally {
      setCheckingHardware(false);
    }
  }
  async function saveMediaTaxonomy(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      const result = await api<MediaTaxonomy>("/api/v1/media-taxonomy", {
        method: "PUT",
        body: JSON.stringify({
          folders: mediaFolders.split("\n"),
          tags: mediaTags.split("\n"),
        }),
      });
      setMediaFolders(result.folders.join("\n"));
      setMediaTags(result.tags.join("\n"));
      refresh();
      notify("Approved media folders and tags saved.");
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function checkUpdates() {
    setChecking(true);
    try {
      const status = await api<UpdateStatus>("/api/v1/updates/check", {
        method: "POST",
        body: "{}",
      });
      refresh();
      notify(
        status.updateAvailable
          ? `LessonCue ${status.latestVersion} is available.`
          : "LessonCue is up to date.",
      );
    } catch (e) {
      notify(errorText(e));
    } finally {
      setChecking(false);
    }
  }
  async function installUpdate() {
    if (
      !await confirmAction(
        `Install LessonCue ${bootstrap.update.latestVersion}? The local interface will be unavailable briefly while the server restarts.`,
      )
    )
      return;
    setInstalling(true);
    try {
      await api("/api/v1/updates/install", { method: "POST", body: "{}" });
      notify("Installing the update. LessonCue will reconnect automatically.");
      await waitForVersion(bootstrap.update.latestVersion);
      location.reload();
    } catch (e) {
      notify(errorText(e));
      setInstalling(false);
    }
  }
  async function rollbackUpdate() {
    const target = bootstrap.update.rollbackTargetVersion;
    if (
      !await confirmAction(
        `Restore the protected ${target ? `LessonCue ${target} ` : ""}last-known-good snapshot? This replaces the current application, database, and server configuration with their pre-update copies. Media files are not changed.`,
        { destructive: true, confirmLabel: "Restore snapshot" },
      )
    )
      return;
    setInstalling(true);
    try {
      const result = await api<{
        message: string;
        targetVersion?: string;
      }>("/api/v1/updates/rollback", { method: "POST", body: "{}" });
      notify(result.message);
      await waitForVersion(result.targetVersion);
      location.reload();
    } catch (e) {
      notify(errorText(e));
      setInstalling(false);
    }
  }
  async function checkYoutubeRuntime() {
    setCheckingYoutubeRuntime(true);
    try {
      const result = await api<{
        message: string;
        status: YouTubeRuntimeStatus;
      }>("/api/v1/updates/youtube-runtime/check", {
        method: "POST",
        body: "{}",
      });
      setYoutubeRuntime(result.status);
      refresh();
      notify(result.message);
      window.setTimeout(() => {
        void api<YouTubeRuntimeStatus>("/api/v1/updates/youtube-runtime")
          .then(setYoutubeRuntime)
          .catch(() => undefined);
      }, 2_000);
    } catch (error) {
      notify(errorText(error));
    } finally {
      setCheckingYoutubeRuntime(false);
    }
  }
  async function updateYoutubeRuntime() {
    if (!await confirmAction(
      "Update the server's independent YouTube downloader now? LessonCue will keep the previous yt-dlp binary for rollback.",
    )) return;
    setUpdatingYoutubeRuntime(true);
    try {
      const result = await api<{
        message: string;
        status: YouTubeRuntimeStatus;
      }>("/api/v1/updates/youtube-runtime/update", {
        method: "POST",
        body: "{}",
      });
      setYoutubeRuntime(result.status);
      refresh();
      notify(result.message);
      window.setTimeout(() => {
        void api<YouTubeRuntimeStatus>("/api/v1/updates/youtube-runtime")
          .then(setYoutubeRuntime)
          .catch(() => undefined);
      }, 3_000);
    } catch (error) {
      notify(errorText(error));
    } finally {
      setUpdatingYoutubeRuntime(false);
    }
  }
  async function savePairingPin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      await api("/api/v1/pairing/pin", {
        method: "PUT",
        body: JSON.stringify({
          automatic: !fixedPairing,
          pin: fixedPairing ? pairingPin : null,
        }),
      });
      refresh();
      notify(
        fixedPairing
          ? "The fixed pairing PIN is active."
          : "Automatic PIN rotation is active.",
      );
    } catch (e) {
      notify(errorText(e));
    }
  }
  const [shortener, setShortener] = useState<ShortenerSettings | null>(null);
  const [shortenerTunnel, setShortenerTunnel] = useState<ShortenerTunnelPlan | null>(null);
  const [shortenerReport, setShortenerReport] = useState<ShortenerReport | null>(null);
  const [shortenerAdminKey, setShortenerAdminKey] = useState("");
  const [shortenerKeyScope, setShortenerKeyScope] = useState("");
  const [shortenerConsolePassword, setShortenerConsolePassword] = useState("");
  const [shortenerTest, setShortenerTest] = useState<ShortenerTestResult | null>(null);
  const [shortenerBusy, setShortenerBusy] = useState("");

  async function loadShortener() {
    if (!canManageApp) return;
    try {
      setShortener(await api<ShortenerSettings>("/api/v1/shortener"));
    } catch {
      setShortener(null);
    }
  }

  useEffect(() => {
    if (!canManageApp) return;
    // Read directly here rather than through the handler, so this effect
    // depends only on the permission and not on a function identity.
    void api<ShortenerSettings>("/api/v1/shortener")
      .then((loaded) => setShortener({
        ...loaded,
        // Filled in rather than merely suggested. Both of these are derivable,
        // and leaving them blank asks the operator to work out an address they
        // can just as easily get wrong.
        upstream: loaded.upstream || loaded.suggestedUpstream,
        adminHost: loaded.adminHost || loaded.suggestedAdminHost,
      }))
      .catch(() => setShortener(null));
    // The tunnel routes are the thing an operator most often comes here to
    // copy, so fetch them on open rather than only after a save.
    void api<ShortenerTunnelPlan>("/api/v1/shortener/tunnel").then(setShortenerTunnel).catch(() => setShortenerTunnel(null));
  }, [canManageApp]);

  // The install runs in a privileged helper and takes a minute or two, so the
  // panel has to ask again. Without this the outcome only appeared if the
  // operator happened to reload the page.
  const shortenerInstalling = Boolean(shortener?.installRequestedFor) && !shortener?.installResult;
  useEffect(() => {
    if (!canManageApp || !shortenerInstalling) return;
    const poll = window.setInterval(() => {
      void api<ShortenerSettings>("/api/v1/shortener")
        // Keep what the operator has typed. Only the progress of the install
        // comes from the server while this is running.
        .then((loaded) => setShortener((previous) => previous ? {
          ...loaded,
          domain: previous.domain,
          adminHost: previous.adminHost,
          upstream: previous.upstream,
          rootRedirectUrl: previous.rootRedirectUrl,
          rootRedirectMode: previous.rootRedirectMode,
        } : loaded))
        .catch(() => { /* a restarting server is expected mid-install */ });
    }, 5_000);
    return () => window.clearInterval(poll);
  }, [canManageApp, shortenerInstalling]);

  async function saveShortener(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!shortener) return;
    setShortenerBusy("save");
    try {
      await api("/api/v1/shortener", {
        method: "PUT",
        body: JSON.stringify({
          domain: shortener.domain,
          adminHost: shortener.adminHost,
          upstream: shortener.upstream,
          rootRedirectMode: shortener.rootRedirectMode,
          rootRedirectUrl: shortener.rootRedirectUrl,
          enabled: shortener.enabled,
        }),
      });
      await loadShortener();
      setShortenerTunnel(await api<ShortenerTunnelPlan>("/api/v1/shortener/tunnel"));
      notify("URL shortener settings saved.");
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function setShortenerInstall(requested: boolean) {
    setShortenerBusy("install");
    try {
      // Save the destination first: the shortener reads it when it starts, so
      // it has to be recorded before the containers come up.
      if (requested && shortener) {
        await api("/api/v1/shortener", {
          method: "PUT",
          body: JSON.stringify({
            domain: shortener.domain,
            adminHost: shortener.adminHost,
            upstream: shortener.upstream,
            rootRedirectMode: shortener.rootRedirectUrl ? "organization" : "notfound",
            rootRedirectUrl: shortener.rootRedirectUrl,
            enabled: true,
          }),
        });
      }
      await api("/api/v1/shortener/install", {
        method: "PUT",
        body: JSON.stringify({ requested, domain: shortener?.domain ?? "" }),
      });
      await loadShortener();
      notify(requested
        ? "Installing the shortener now. This takes a minute or two the first time."
        : "The shortener will not be installed.");
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function saveShortenerConsolePassword() {
    setShortenerBusy("console-password");
    try {
      await api("/api/v1/shortener/console-password", {
        method: "PUT",
        body: JSON.stringify({ password: shortenerConsolePassword }),
      });
      setShortenerConsolePassword("");
      await loadShortener();
      notify("Companion password set for both accounts.");
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function revealShortenerKey() {
    setShortenerBusy("reveal");
    try {
      const revealed = await api<{ apiKey: string; scope: string }>("/api/v1/shortener/key/reveal", { method: "POST" });
      setShortenerAdminKey(revealed.apiKey);
      setShortenerKeyScope(revealed.scope);
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function saveShortenerKey() {
    setShortenerBusy("keys");
    try {
      // Recorded, not minted. The shortener has no way to register a key we
      // invent, so this is the value it was started with.
      const result = await api<{ state: string; detail: string | null }>("/api/v1/shortener/key", {
        method: "PUT",
        body: JSON.stringify({ apiKey: shortenerAdminKey }),
      });
      setShortenerAdminKey("");
      await loadShortener();
      notify(result.detail || "API key recorded.");
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function testShortener() {
    setShortenerBusy("test");
    setShortenerTest(null);
    try {
      setShortenerTest(await api<ShortenerTestResult>("/api/v1/shortener/test", { method: "POST" }));
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function reconcileShortener() {
    setShortenerBusy("repair");
    setShortenerReport(null);
    try {
      setShortenerReport(await api<ShortenerReport>("/api/v1/shortener/reconcile", { method: "POST" }));
      await loadShortener();
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function shortenerLifecycle(action: "disable" | "uninstall") {
    if (action === "uninstall" && !(await confirmAction(
      "Remove the URL shortener integration? LessonCue stops using short links and forgets its credentials. "
      + "The shortener's own database and every link in it are left untouched.",
      { title: "Remove the URL shortener integration?", destructive: true, confirmLabel: "Remove integration" },
    ))) return;

    setShortenerBusy(action);
    try {
      await api("/api/v1/shortener/lifecycle", { method: "POST", body: JSON.stringify({ action, confirm: true }) });
      await loadShortener();
      notify(action === "disable" ? "Short links switched off." : "URL shortener integration removed.");
    } catch (e) {
      notify(errorText(e));
    } finally {
      setShortenerBusy("");
    }
  }

  async function saveControllerPin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      await api("/api/v1/controller-pin", {
        method: "PUT",
        body: JSON.stringify({ pin: controllerPin }),
      });
      sessionStorage.removeItem("lessoncue.universalGrant");
      refresh();
      notify("Universal controller PIN saved.");
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function saveLocalAddress(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try {
      const status = await api<LocalAddressStatus>("/api/v1/local-address", {
        method: "PUT",
        body: JSON.stringify({ hostname: localHostname, ipv6Enabled: localIpv6Enabled }),
      });
      setLocalHostname(status.hostname);
      setLocalIpv6Enabled(status.ipv6Enabled);
      refresh();
      notify(
        status.pending
          ? `Setting up ${status.address}…`
          : `${status.address} is active.`,
      );
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function saveHttpPort(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const port = Number(httpPort);
    if (
      !await confirmAction(
        `Change LessonCue's browser port to ${port}? The interface will restart. Saved browser links and screens using the old address must be updated.`,
      )
    )
      return;
    try {
      const status = await api<HttpPortStatus>("/api/v1/http-port", {
        method: "PUT",
        body: JSON.stringify({ port }),
      });
      setHttpPort(String(status.port));
      if (status.supported) {
        notify(`Restarting LessonCue at ${status.address}…`);
        const destination = new URL(location.href);
        destination.port = status.port === 80 ? "" : String(status.port);
        for (let attempt = 0; attempt < 45; attempt++) {
          await new Promise((resolve) => window.setTimeout(resolve, 1000));
          try {
            await fetch(`${destination.origin}/health`, {
              mode: "no-cors",
              cache: "no-store",
            });
            location.assign(destination.origin);
            return;
          } catch {
            /* Wait for the protected restart or rollback. */
          }
        }
        notify(
          "The new port did not respond. Returning to the previous address.",
        );
        location.reload();
      } else {
        refresh();
        notify(status.error || "Port saved. Restart the server to apply it.");
      }
    } catch (e) {
      notify(errorText(e));
    }
  }
  async function saveCloudflareTunnel(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (
      !tunnelEnabled &&
      bootstrap.cloudflareTunnel.enabled &&
      !await confirmAction(
        "Disable remote access through this Cloudflare Tunnel? Local LessonCue access will continue to work.",
        { destructive: true, confirmLabel: "Disable remote access" },
      )
    )
      return;
    setTunnelBusy(true);
    try {
      await api<CloudflareTunnelStatus>("/api/v1/cloudflare-tunnel", {
        method: "PUT",
        body: JSON.stringify({
          enabled: tunnelEnabled,
          publicHostname: tunnelHostname,
          token: tunnelToken || null,
          acknowledgedRemoteExposure: tunnelEnabled && tunnelAcknowledged,
        }),
      });
      setTunnelToken("");
      notify(
        tunnelEnabled
          ? "Cloudflare Tunnel setup started. Checking its edge connection…"
          : "Cloudflare Tunnel is being disabled.",
      );
      for (let attempt = 0; attempt < 45; attempt++) {
        await new Promise((resolve) => window.setTimeout(resolve, 1500));
        const status = await api<CloudflareTunnelStatus>(
          "/api/v1/cloudflare-tunnel",
        );
        if (!status.pending) {
          refresh();
          notify(
            status.error ||
              (status.connected
                ? `${status.publicUrl} is connected through Cloudflare.`
                : status.enabled
                  ? "Cloudflare Tunnel is enabled and waiting for an edge connection."
                  : "Cloudflare Tunnel is disabled."),
          );
          break;
        }
      }
    } catch (e) {
      notify(errorText(e));
    } finally {
      setTunnelBusy(false);
    }
  }
  async function restoreRecycleItem(item: RecycleItem) {
    try {
      await api(`/api/v1/recycle-bin/${item.kind}/${item.id}/restore`, {
        method: "POST",
        body: "{}",
      });
      await loadRecycleBin();
      refresh();
      notify(`${item.title} restored.`);
    } catch (error) {
      notify(errorText(error));
    }
  }
  async function purgeRecycleBin() {
    if (
      !await confirmAction(
        "Permanently purge every item in the recycling bin? Files and records cannot be recovered after this.",
        { destructive: true, confirmLabel: "Permanently purge" },
      )
    )
      return;
    try {
      const result = await api<{ purged: number }>("/api/v1/recycle-bin", {
        method: "DELETE",
      });
      await loadRecycleBin();
      refresh();
      notify(
        `${result.purged} recycled item${result.purged === 1 ? "" : "s"} permanently purged.`,
      );
    } catch (error) {
      notify(errorText(error));
    }
  }
  const o = bootstrap.settings;
  const caddyOriginPort =
    bootstrap.httpPort.port === 443 ? 8080 : bootstrap.httpPort.port;
  const caddyConfig = `${bootstrap.localAddress.hostname}.local {\n  tls internal\n  reverse_proxy 127.0.0.1:${caddyOriginPort}\n}`;
  const diagnosticStoragePercent = diagnostics
    ? Math.min(
        100,
        Math.round(
          (diagnostics.storage.usedBytes /
            Math.max(1, diagnostics.storage.allocationBytes)) *
            100,
        ),
      )
    : 0;
  const diagnosticConverterReady = diagnostics
    ? diagnostics.converters.missing.length === 0
    : false;
  return (
    <>
      <PageHead
        eyebrow="SERVER"
        title="Settings"
        detail="Choose an area from the settings menu to manage LessonCue."
      />
      <div className="settings-page" data-section={settingsSection}>
        <section className="settings-overview" aria-labelledby="settings-overview-title">
          <div className="settings-heading">
            <div>
              <span className="settings-kicker">SETTINGS</span>
              <h2 id="settings-overview-title">Settings overview</h2>
              <p className="settings-copy">
                A quick view of the server. Select any status to open the page
                where it can be managed.
              </p>
            </div>
          </div>
          <div className="settings-overview-grid">
            {(canServiceSettings || canUpdates) && (
              <button className="settings-overview-card" onClick={() => setSettingsSection("system")}>
                <span>SYSTEM</span>
                <strong>
                  {diagnostics
                    ? diagnosticConverterReady && diagnostics.screens.playbackErrors === 0 && !diagnostics.backup.overdue
                      ? "Healthy"
                      : "Needs attention"
                    : "Checking"}
                </strong>
                <small>Version {bootstrap.update.currentVersion}</small>
              </button>
            )}
            {canUpdates && youtubeRuntime?.updateAvailable && (
              <button className="settings-overview-card" onClick={() => setSettingsSection("system")}>
                <span>YOUTUBE DOWNLOADS</span>
                <strong>Update available</strong>
                <small>
                  {youtubeRuntime.updateNoticeVisible
                    ? "Downloads are failing; review the independent downloader."
                    : "Review the independent downloader in Settings."}
                </small>
              </button>
            )}
            {canManageApp && (
              <button className="settings-overview-card" onClick={() => setSettingsSection("playback")}>
                <span>DISPLAYS</span>
                <strong>{bootstrap.counts.screens} registered</strong>
                <small>Open Playback &amp; Displays</small>
              </button>
            )}
            {canServiceSettings && (
              <button className="settings-overview-card" onClick={() => setSettingsSection("network")}>
                <span>REMOTE ACCESS</span>
                <strong>{bootstrap.cloudflareTunnel.enabled ? "Enabled" : "Disabled"}</strong>
                <small>{bootstrap.cloudflareTunnel.connected ? `${bootstrap.cloudflareTunnel.activeConnections} edge connections` : "Cloudflare Tunnel"}</small>
              </button>
            )}
            {canManageApp && shortener && (
              <button className="settings-overview-card" onClick={() => setSettingsSection("integrations")}>
                <span>SHORTENER</span>
                <strong>{shortenerStateLabel(shortener.state)}</strong>
                <small>{shortener.publicUrl || "URL shortener"}</small>
              </button>
            )}
            {(canManageApp || canBackups) && (
              <button className="settings-overview-card" onClick={() => setSettingsSection("backup")}>
                <span>BACKUPS</span>
                <strong>{bootstrap.backupPolicy?.enabled ? "Enabled" : "Disabled"}</strong>
                <small>{bootstrap.backupPolicy?.overdue ? "Needs attention" : "Backup & Recovery"}</small>
              </button>
            )}
          </div>
        </section>
        {canManageApp && (
          <div className="settings-grid account-settings-grid">
            <RegistrationSettingsPanel
              bootstrap={bootstrap}
              notify={notify}
              refresh={refresh}
              canServiceSettings={canServiceSettings}
            />
            {canServiceSettings && <AuthenticatorMfaPanel notify={notify} showPolicy />}
            {canServiceSettings && (
              <CollapsibleSettingsSection
                label="Room controller policy"
                className="settings-panel settings-security settings-room-policy"
              >
                <h2>Room controller policy</h2>
                <p className="settings-copy">
                  Restrict room and temporary remotes to the local network while
                  allowing administrators to troubleshoot remotely.
                </p>
                <form className="stack" onSubmit={saveOrganization}>
                  <input type="hidden" name="requireLocalRoomControllers" value="off" />
                  <label className="check-row">
                    <input
                      name="requireLocalRoomControllers"
                      type="checkbox"
                      defaultChecked={o.requireLocalRoomControllers}
                    />{" "}
                    Require non-administrator room remotes to use the local .local address
                  </label>
                  <button className="button primary">Save controller policy</button>
                </form>
              </CollapsibleSettingsSection>
            )}
          </div>
        )}
        {restorePreview && (
          <Modal
            title={restoreResult ? "Restore complete" : "Review backup restore"}
            onClose={() => !restoreBusy && setRestorePreview(undefined)}
          >
            {restoreResult ? (
              <div className="restore-complete">
                <div className="success-mark">✓</div>
                <h3>{restoreResult.organization} was restored</h3>
                <p>
                  A full safety backup was created first and remains available
                  on this server.
                </p>
                <Definition
                  label="Safety backup"
                  value={restoreResult.safetyBackupFileName}
                />
                <Definition
                  label="Media"
                  value={
                    restoreResult.mediaRestored
                      ? "Restored from the archive"
                      : "Existing server media preserved"
                  }
                />
                <p className="settings-copy">
                  This server kept its{" "}
                  {restoreResult.preservedServerSettings.join(", ")}.
                </p>
                <button
                  className="button primary wide"
                  onClick={() => location.reload()}
                >
                  Reload restored LessonCue
                </button>
              </div>
            ) : (
              <div className="restore-review">
                <div className="restore-heading">
                  <div>
                    <span>
                      {restorePreview.encrypted ? "ENCRYPTED " : ""}
                      {restorePreview.kind.toUpperCase()} BACKUP
                    </span>
                    <h3>{restorePreview.organization}</h3>
                    <p>
                      {restorePreview.fileName} · Server secrets{" "}
                      {restorePreview.secretHandling === "include"
                        ? "included"
                        : restorePreview.secretHandling === "exclude"
                          ? "excluded"
                          : "use legacy behavior"}{" "}
                      · Source version {restorePreview.sourceVersion || "unknown"}
                    </p>
                  </div>
                  <strong>{formatBytes(restorePreview.compressedBytes)}</strong>
                </div>
                <div className="restore-counts">
                  <Definition
                    label="Users"
                    value={String(restorePreview.users)}
                  />
                  <Definition
                    label="Classes"
                    value={String(restorePreview.classes)}
                  />
                  <Definition
                    label="Lessons"
                    value={String(restorePreview.lessons)}
                  />
                  <Definition
                    label="Media records"
                    value={String(restorePreview.mediaRecords)}
                  />
                </div>
                {restorePreview.warnings.map((warning) => (
                  <div className="alert" key={warning}>
                    {warning}
                  </div>
                ))}
                <div className="danger-callout">
                  <strong>This replaces current LessonCue data.</strong>
                  <p>
                    LessonCue creates a full safety backup before changing
                    anything. The receiving server's identity, keys, network
                    address, port, and pairing secrets remain unchanged.
                  </p>
                </div>
                <Field label="Type RESTORE to continue">
                  <input
                    value={restoreConfirmation}
                    onChange={(e) => setRestoreConfirmation(e.target.value)}
                    autoComplete="off"
                  />
                </Field>
                <button
                  className="button danger wide"
                  onClick={restoreBackup}
                  disabled={restoreBusy || restoreConfirmation !== "RESTORE"}
                >
                  {restoreBusy
                    ? "Restoring…"
                    : "Create safety backup and restore"}
                </button>
              </div>
            )}
          </Modal>
        )}
        {backupDrill && (
          <Modal
            title="Restore-readiness drill passed"
            onClose={() => setBackupDrill(undefined)}
          >
            <div className="restore-complete">
              <div className="success-mark">✓</div>
              <h3>{backupDrill.fileName} is recoverable</h3>
              <p>
                LessonCue authenticated the complete encrypted envelope,
                checked every manifest digest, opened SQLite read-only, ran its
                integrity check, and confirmed the required tables and media
                inventory without changing this server.
              </p>
              <div className="restore-counts">
                <Definition
                  label="Archive files"
                  value={String(backupDrill.fileCount)}
                />
                <Definition
                  label="Database"
                  value="Healthy"
                />
                <Definition
                  label="Media files"
                  value={String(backupDrill.mediaFiles)}
                />
                <Definition
                  label="Encryption"
                  value={backupDrill.encrypted ? "Authenticated" : "Legacy ZIP"}
                />
              </div>
              <div className="danger-callout">
                <strong>Complete the human part of the drill.</strong>
                <p>
                  Confirm the password is stored separately, download this
                  backup to another device, and record who can reach it. For
                  the strongest test, restore that copy on a spare LessonCue
                  server rather than replacing this production server.
                </p>
              </div>
              <button
                className="button primary wide"
                onClick={() => setBackupDrill(undefined)}
              >
                Finish drill
              </button>
            </div>
          </Modal>
        )}
        {migrationGrant && (
          <Modal
            title="One-time server transfer"
            onClose={() => setMigrationGrant(undefined)}
          >
            <div className="migration-grant">
              <div className="privacy-callout">
                <strong>{migrationGrant.fileName}</strong>
                <p>
                  This encrypted backup can be downloaded once, until{" "}
                  {new Date(migrationGrant.expiresAt).toLocaleString()}. The
                  source server never receives its backup password.
                </p>
              </div>
              <Field label="Source LessonCue address">
                <input
                  readOnly
                  value={
                    bootstrap.cloudflareTunnel.publicUrl ||
                    bootstrap.httpPort.address
                  }
                />
              </Field>
              <Field label="One-time transfer token">
                <textarea readOnly value={migrationGrant.token} rows={2} />
              </Field>
              <button
                className="button primary wide"
                onClick={() => {
                  void navigator.clipboard.writeText(
                    `${bootstrap.cloudflareTunnel.publicUrl || bootstrap.httpPort.address}\n${migrationGrant.token}`,
                  );
                  notify("Source address and one-time token copied.");
                }}
              >
                Copy transfer details
              </button>
              <p className="settings-copy">
                On the destination server, open Privacy & backups and paste
                these values under <strong>Move from another server</strong>.
                Enter the backup password separately.
              </p>
            </div>
          </Modal>
        )}
        <div className="settings-grid">
          {canUpdates && (
            <CollapsibleSettingsSection
              label="Software updates"
              className="wide-settings update-settings settings-panel settings-system"
            >
              <div className="settings-heading">
                <div>
                  <span className="settings-kicker">SYSTEM MAINTENANCE</span>
                  <h2>Software updates</h2>
                  <p className="settings-copy">
                    LessonCue checks once each day and alerts administrators
                    when a newer release is available.
                  </p>
                </div>
                <span
                  className={`update-state ${bootstrap.update.updateAvailable ? "available" : "current"}`}
                >
                  {bootstrap.update.updateAvailable
                    ? "Update available"
                    : "Up to date"}
                </span>
              </div>
              <div className="storage-facts">
                <Definition
                  label="Installed version"
                  value={bootstrap.update.currentVersion}
                />
                <Definition
                  label="Latest version"
                  value={bootstrap.update.latestVersion || "Not checked yet"}
                />
                <Definition
                  label="Last checked"
                  value={
                    bootstrap.update.lastCheckedAt
                      ? timeAgo(bootstrap.update.lastCheckedAt)
                      : "Not checked yet"
                  }
                />
              </div>
              {bootstrap.update.updateAvailable && bootstrap.update.releaseNotes && (
                <div className="update-notes" aria-label="What is new in this update">
                  <strong>What’s new in this update</strong>
                  <p>{cleanReleaseNotes(bootstrap.update.releaseNotes)}</p>
                </div>
              )}
              {bootstrap.update.error && (
                <div className="alert error" role="alert">{bootstrap.update.error}</div>
              )}
              {!bootstrap.update.error &&
                bootstrap.update.lastInstallMessage &&
                bootstrap.update.lastInstallAt && (
                  <div
                    className={`alert ${
                      bootstrap.update.lastInstallSucceeded === false
                        ? "error"
                        : "success"
                    }`}
                  >
                    {bootstrap.update.lastInstallMessage}{" "}
                    <span className="muted">
                      ({timeAgo(bootstrap.update.lastInstallAt)})
                    </span>
                  </div>
                )}
              {bootstrap.update.rollbackSnapshotAvailable && (
                <div className="rollback-available">
                  <p className="settings-copy">
                    A protected last-known-good application, database,
                    configuration, updater, and service snapshot
                    {bootstrap.update.rollbackTargetVersion
                      ? ` for LessonCue ${bootstrap.update.rollbackTargetVersion}`
                      : ""}{" "}
                    is available. Media files are not replaced.
                  </p>
                  {canServiceSettings && (
                    <button
                      className="button danger"
                      onClick={rollbackUpdate}
                      disabled={installing}
                    >
                      {installing
                        ? "Server operation in progress…"
                        : "Restore last-known-good snapshot"}
                    </button>
                  )}
                </div>
              )}
              <div className="head-actions">
                <button
                  className="button"
                  onClick={checkUpdates}
                  disabled={checking}
                >
                  {checking ? "Checking…" : "Check now"}
                </button>
                {bootstrap.update.updateAvailable &&
                  bootstrap.update.automaticInstallSupported && (
                    <button
                      className="button primary"
                      onClick={installUpdate}
                      disabled={installing}
                    >
                      {installing
                        ? "Installing…"
                        : `Install ${bootstrap.update.latestVersion}`}
                    </button>
                  )}
                {bootstrap.update.releaseUrl && (
                  <a
                    className="button"
                    href={bootstrap.update.releaseUrl}
                    target="_blank"
                    rel="noreferrer"
                  >
                    Release notes
                  </a>
                )}
              </div>
              {!bootstrap.update.automaticInstallSupported && (
                <p className="settings-copy">
                  Run the current release installer once from SSH to enable
                  automatic updates on this server.
                </p>
              )}
            </CollapsibleSettingsSection>
          )}
          {canUpdates && (
            <CollapsibleSettingsSection
              label="YouTube downloader"
              className="wide-settings settings-panel settings-system"
            >
              <div className="settings-heading">
                <div>
                  <span className="settings-kicker">MEDIA IMPORTS</span>
                  <h2>Independent YouTube downloader</h2>
                  <p className="settings-copy">
                    yt-dlp is checked and maintained separately from LessonCue
                    releases. Routine checks and verified updates are silent;
                    this panel is the place to manage it manually.
                  </p>
                </div>
                <span
                  className={`update-state ${youtubeRuntime?.updateAvailable ? "available" : "current"}`}
                >
                  {youtubeRuntime?.updateAvailable
                    ? "Update available"
                    : youtubeRuntime?.supported
                      ? "Maintained"
                      : "Unavailable"}
                </span>
              </div>
              <div className="storage-facts">
                <Definition
                  label="Installed version"
                  value={youtubeRuntime?.installedVersion || "Unknown"}
                />
                <Definition
                  label="Last checked"
                  value={youtubeRuntime?.lastCheckedAt ? timeAgo(youtubeRuntime.lastCheckedAt) : "Not checked yet"}
                />
                <Definition
                  label="Automatic checks"
                  value={youtubeRuntime?.automaticUpdatesEnabled ? "Daily" : "Not configured"}
                />
              </div>
              {youtubeRuntime?.updateAvailable && (
                <div className="alert warning" role="status">
                  {youtubeRuntime.updateNoticeVisible
                    ? `YouTube downloads are failing and yt-dlp ${youtubeRuntime.latestVersion || "a newer version"} is available. Update the independent downloader and retry the failed import.`
                    : `A verified yt-dlp ${youtubeRuntime.latestVersion || "newer"} release is available. This update is managed separately from LessonCue.`}
                </div>
              )}
              {!youtubeRuntime?.supported && (
                <p className="settings-copy">
                  Independent updates are available on the native Linux server
                  installation after the current installer has installed the
                  updater services.
                </p>
              )}
              {youtubeRuntime?.lastUpdateMessage && youtubeRuntime.lastUpdatedAt && (
                <div className={`alert ${youtubeRuntime.lastUpdateSucceeded === false ? "error" : "success"}`} role="status">
                  {youtubeRuntime.lastUpdateMessage} <span className="muted">({timeAgo(youtubeRuntime.lastUpdatedAt)})</span>
                </div>
              )}
              {youtubeRuntime?.error && youtubeRuntime.updateNoticeVisible && (
                <div className="alert error" role="alert">{youtubeRuntime.error}</div>
              )}
              <div className="head-actions">
                <button
                  className="button"
                  onClick={checkYoutubeRuntime}
                  disabled={checkingYoutubeRuntime || updatingYoutubeRuntime || !youtubeRuntime?.supported}
                >
                  {checkingYoutubeRuntime ? "Checking…" : "Check now"}
                </button>
                <button
                  className="button primary"
                  onClick={updateYoutubeRuntime}
                  disabled={checkingYoutubeRuntime || updatingYoutubeRuntime || !youtubeRuntime?.supported}
                >
                  {updatingYoutubeRuntime ? "Queuing…" : "Update yt-dlp now"}
                </button>
              </div>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="System diagnostics & support"
              className="wide-settings settings-panel settings-system settings-diagnostics"
            >
              <div className="settings-heading">
                <div>
                  <span className="settings-kicker">SERVICE ADMIN</span>
                  <h2>System diagnostics &amp; support</h2>
                  <p className="settings-copy">
                    A quick, privacy-safe view of capacity, converters, queues,
                    displays, backups, and updates. Export the same redacted
                    snapshot when support needs context.
                  </p>
                </div>
                <span
                  className={`diagnostic-state ${diagnostics && diagnosticConverterReady && diagnostics.screens.playbackErrors === 0 && !diagnostics.backup.overdue ? "healthy" : "attention"}`}
                >
                  {diagnostics
                    ? diagnosticConverterReady && diagnostics.screens.playbackErrors === 0 && !diagnostics.backup.overdue
                      ? "Healthy"
                      : "Needs attention"
                    : "Checking"}
                </span>
              </div>
              {diagnosticsError && (
                <div className="alert error" role="alert">
                  {diagnosticsError}
                </div>
              )}
              {diagnostics?.diagnosticErrors?.length ? (
                <div className="alert warning" role="status">
                  Some diagnostics could not be collected: {diagnostics.diagnosticErrors.join(" · ")}. The remaining checks are still available, and the downloaded bundle includes this partial-failure record.
                </div>
              ) : null}
              {diagnostics ? (
                <>
                  <div className="diagnostic-grid">
                    <div className="diagnostic-card">
                      <span>STORAGE PROJECTION</span>
                      <strong>{diagnosticStoragePercent}% used</strong>
                      <div
                        className="storage-meter"
                        role="progressbar"
                        aria-valuemin={0}
                        aria-valuemax={100}
                        aria-valuenow={diagnosticStoragePercent}
                        aria-label={`${diagnosticStoragePercent}% storage used`}
                      >
                        <span style={{ width: `${diagnosticStoragePercent}%` }} />
                      </div>
                      <small>{formatBytes(diagnostics.storage.remainingBytes)} remaining · {formatBytes(diagnostics.storage.reservedBytes)} reserved</small>
                    </div>
                    <div className="diagnostic-card">
                      <span>CONVERTERS</span>
                      <strong className={diagnosticConverterReady ? "diagnostic-good" : "diagnostic-bad"}>
                        {diagnosticConverterReady ? "Ready" : `${diagnostics.converters.missing.length} missing`}
                      </strong>
                      <small>{diagnostics.converters.missing.length ? diagnostics.converters.missing.join(", ") : "Video, audio, documents, and image derivatives available."}</small>
                    </div>
                    <div className="diagnostic-card">
                      <span>UPLOAD QUEUE</span>
                      <strong>{diagnostics.queue.activeUploads} active</strong>
                      <small>{formatBytes(diagnostics.queue.reservedBytes)} reserved · {Object.entries(diagnostics.queue.states).map(([state, count]) => `${count} ${state}`).join(" · ") || "No recent sessions"}</small>
                    </div>
                    <div className="diagnostic-card">
                      <span>DISPLAYS</span>
                      <strong>{diagnostics.screens.online}/{diagnostics.screens.count} online</strong>
                      <small>{diagnostics.screens.commandsAwaitingReceipt} command{diagnostics.screens.commandsAwaitingReceipt === 1 ? "" : "s"} awaiting receipt · {diagnostics.screens.playbackErrors} playback error{diagnostics.screens.playbackErrors === 1 ? "" : "s"}</small>
                    </div>
                    <div className="diagnostic-card">
                      <span>BACKUPS</span>
                      <strong className={diagnostics.backup.overdue || diagnostics.backup.lastError ? "diagnostic-bad" : "diagnostic-good"}>
                        {diagnostics.backup.overdue ? "Overdue" : diagnostics.backup.lastSucceededAt ? `Last ${timeAgo(diagnostics.backup.lastSucceededAt)}` : "Not run yet"}
                      </strong>
                      <small>{diagnostics.backup.lastError || (diagnostics.backup.enabled ? `Next run ${diagnostics.backup.nextRunAt ? new Date(diagnostics.backup.nextRunAt).toLocaleString() : "scheduled"}` : "Automatic backups are disabled")}</small>
                    </div>
                    <div className="diagnostic-card">
                      <span>UPDATES</span>
                      <strong className={diagnostics.update.error || diagnostics.update.updateAvailable ? "diagnostic-bad" : "diagnostic-good"}>
                        {diagnostics.update.error ? "Error" : diagnostics.update.updateAvailable ? `Available: ${diagnostics.update.latestVersion}` : "Current"}
                      </strong>
                      <small>{diagnostics.update.error || `LessonCue ${diagnostics.update.currentVersion} · checked ${diagnostics.update.lastCheckedAt ? timeAgo(diagnostics.update.lastCheckedAt) : "not yet"}`}</small>
                    </div>
                  </div>
                  <div className="diagnostic-actions">
                    <button className="button" onClick={refreshDiagnostics} disabled={diagnosticsBusy}>
                      {diagnosticsBusy ? "Refreshing…" : "Refresh diagnostics"}
                    </button>
                    <a className="button primary" href="/api/v1/support/bundle" download>
                      Download redacted support bundle
                    </a>
                  </div>
                  <p className="settings-copy diagnostic-footnote">
                    Generated {new Date(diagnostics.generatedAt).toLocaleString()}. The export excludes account names, IP addresses, media paths, URLs, secrets, and response text.
                  </p>
                </>
              ) : (
                <div className="loading">Collecting system diagnostics…</div>
              )}
            </CollapsibleSettingsSection>
          )}
      {canServiceSettings && (
        <>
          <CollapsibleSettingsSection
            label="General"
            className="wide-settings settings-panel settings-general"
          >
            <div className="settings-heading">
              <div>
                <span className="settings-kicker">ORGANIZATION</span>
                <h2>General</h2>
                <p className="settings-copy">
                  Organization identity, lesson defaults, and teacher-facing
                  feature availability.
                </p>
              </div>
            </div>
            <form className="stack" onSubmit={saveOrganization}>
              <div className="settings-subsection">
                <h3>Organization</h3>
                <div className="two-fields">
                  <Field label="Organization">
                    <input name="name" defaultValue={o.name} required />
                  </Field>
                  <Field label="Site">
                    <input name="siteName" defaultValue={o.siteName} required />
                  </Field>
                </div>
                <div className="two-fields">
                  <Field label="Time zone">
                    <input name="timeZone" defaultValue={o.timeZone} required />
                  </Field>
                  <Field label="Week starts">
                    <select name="weekStartsOn" defaultValue={o.weekStartsOn}>
                      <option>Sunday</option>
                      <option>Monday</option>
                    </select>
                  </Field>
                </div>
              </div>
              <div className="settings-subsection">
                <h3>Lesson defaults</h3>
                <Field label="Welcome message">
                  <input name="welcomeMessage" defaultValue={o.welcomeMessage} />
                </Field>
                <Field label="Default lesson minutes">
                  <input
                    name="defaultLessonDurationMinutes"
                    type="number"
                    min="5"
                    max="480"
                    defaultValue={o.defaultLessonDurationMinutes}
                  />
                </Field>
              </div>
              <div className="settings-subsection">
                <h3>Features</h3>
                <ActivityAvailabilityPanel notify={notify} refresh={refresh} />
              </div>
              <button className="button primary">Save general settings</button>
            </form>
          </CollapsibleSettingsSection>

          <CollapsibleSettingsSection
            label="Appearance"
            className="wide-settings settings-panel settings-appearance"
          >
            <div className="settings-heading">
              <div>
                <span className="settings-kicker">BRANDING</span>
                <h2>Appearance</h2>
                <p className="settings-copy">
                  Set the colors used by the LessonCue navigation and selected
                  states. Future branding and logo settings belong here too.
                </p>
              </div>
            </div>
            <form className="stack" onSubmit={saveOrganization}>
              <div className="color-fields">
                <Field label="Navigation background">
                  <input name="primaryColor" type="color" defaultValue={o.primaryColor} />
                </Field>
                <Field label="Accent color">
                  <input name="accentColor" type="color" defaultValue={o.accentColor} />
                </Field>
                <Field label="Navigation text">
                  <input name="navigationTextColor" type="color" defaultValue={o.navigationTextColor} />
                </Field>
                <Field label="Selected navigation tab">
                  <input name="selectedTabColor" type="color" defaultValue={o.selectedTabColor} />
                </Field>
              </div>
              <button className="button primary">Save appearance</button>
            </form>
          </CollapsibleSettingsSection>

          <CollapsibleSettingsSection
            label="Approved signage sources"
            className="wide-settings settings-panel settings-integrations"
          >
            <h2>Approved signage sources</h2>
            <p className="settings-copy">
              Signage editors may only use RSS, calendars, weather, menus, or
              JSON data from origins approved here.
            </p>
            <form className="stack" onSubmit={saveOrganization}>
              <Field label="Approved source origins" hint="Enter one trusted website origin per line.">
                <textarea
                  name="signageSourceAllowlist"
                  rows={5}
                  defaultValue={parseStringArray(o.signageSourceAllowlistJson).join("\n")}
                  placeholder="https://example.org"
                />
              </Field>
              <button className="button primary">Save approved sources</button>
            </form>
          </CollapsibleSettingsSection>

        </>
      )}
          {canManageApp && (
            <CollapsibleSettingsSection
              label="Approved folders & tags"
              className="wide-settings settings-panel settings-media"
            >
              <div className="settings-heading">
                <div>
                  <span className="settings-kicker">MEDIA LIBRARY</span>
                  <h2>Approved folders & tags</h2>
                  <p className="settings-copy">
                    Give uploaders a consistent organization system. Folder
                    paths may use / for hierarchy; enter one folder or tag per
                    line.
                  </p>
                </div>
                <span className="update-state current">
                  {bootstrap.mediaTaxonomy.folders.length} folders ·{" "}
                  {bootstrap.mediaTaxonomy.tags.length} tags
                </span>
              </div>
              <form className="stack" onSubmit={saveMediaTaxonomy}>
                <div className="two-fields taxonomy-settings">
                  <Field
                    label="Approved folder paths"
                    hint="Examples: Elementary/Science or Main Campus/Events"
                  >
                    <textarea
                      rows={7}
                      value={mediaFolders}
                      onChange={(event) => setMediaFolders(event.target.value)}
                      placeholder={"General\nLessons\nSignage"}
                    />
                  </Field>
                  <Field
                    label="Approved tags"
                    hint="Each tag may be up to 40 characters."
                  >
                    <textarea
                      rows={7}
                      value={mediaTags}
                      onChange={(event) => setMediaTags(event.target.value)}
                      placeholder={"Reusable\nIntro\nReference"}
                    />
                  </Field>
                </div>
                <div className="alert">
                  A folder or tag that is already assigned to media cannot be
                  removed until those items are reassigned.
                </div>
                <button className="button primary">
                  Save approved folders & tags
                </button>
              </form>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Storage allocation"
              className="settings-panel settings-media settings-storage"
            >
              <h2>Storage allocation</h2>
              <div className="storage-facts">
                <Definition
                  label="LessonCue is using"
                  value={formatBytes(bootstrap.storage.usedBytes)}
                />
                <Definition
                  label="Available on computer"
                  value={formatBytes(bootstrap.storage.diskAvailableBytes)}
                />
                <Definition
                  label="Available for uploads"
                  value={formatBytes(bootstrap.storage.remainingBytes)}
                />
                <Definition
                  label="Reserved by active uploads"
                  value={formatBytes(bootstrap.storage.reservedBytes)}
                />
              </div>
              <StorageMeter storage={bootstrap.storage} />
              <form className="stack storage-form" onSubmit={saveStorage}>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={automaticStorage}
                    onChange={(e) => setAutomaticStorage(e.target.checked)}
                  />{" "}
                  Adjust allocation automatically
                </label>
                <Field
                  label="Maximum LessonCue storage"
                  hint={`Must be between ${formatBytes(bootstrap.storage.usedBytes)} and ${formatBytes(bootstrap.storage.maximumAllocationBytes)}. LessonCue keeps 512 MB free for the operating system.`}
                >
                  <div className="number-suffix">
                    <input
                      type="number"
                      min={Math.max(
                        0.1,
                        bootstrap.storage.usedBytes / 1024 ** 3,
                      )}
                      max={bootstrap.storage.maximumAllocationBytes / 1024 ** 3}
                      step="0.1"
                      value={allocationGb}
                      onChange={(e) => setAllocationGb(e.target.value)}
                      disabled={automaticStorage}
                      required={!automaticStorage}
                    />
                    <span>GB</span>
                  </div>
                </Field>
                <button className="button primary">Save storage limit</button>
              </form>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Upload limits"
              className="settings-panel settings-media settings-upload-limits"
            >
              <div className="settings-heading">
                <div>
                  <span className="settings-kicker">UPLOAD SAFETY</span>
                  <h2>Upload limits</h2>
                  <p className="settings-copy">
                    Limit file size and daily use without changing the main
                    storage allocation. Enter 0 for no size or daily limit.
                    Active uploads always reserve their full declared size.
                  </p>
                </div>
                <span className="update-state current">
                  {maxActiveUploads} active per account
                </span>
              </div>
              <form className="stack" onSubmit={saveUploadPolicy}>
                <div className="three-fields">
                  <Field label="Maximum file size" hint="0 means unlimited.">
                    <div className="number-suffix">
                      <input
                        type="number"
                        min="0"
                        max="100"
                        step="0.1"
                        value={maxUploadFileGb}
                        onChange={(event) =>
                          setMaxUploadFileGb(event.target.value)
                        }
                      />
                      <span>GB</span>
                    </div>
                  </Field>
                  <Field
                    label="Default daily allowance"
                    hint="Per account, reset at 00:00 UTC; 0 means unlimited."
                  >
                    <div className="number-suffix">
                      <input
                        type="number"
                        min="0"
                        step="0.1"
                        value={maxUploadDailyGb}
                        onChange={(event) =>
                          setMaxUploadDailyGb(event.target.value)
                        }
                      />
                      <span>GB</span>
                    </div>
                  </Field>
                  <Field
                    label="Active uploads per account"
                    hint="Paused and failed resumable uploads count."
                  >
                    <input
                      type="number"
                      min="1"
                      max="10"
                      value={maxActiveUploads}
                      onChange={(event) =>
                        setMaxActiveUploads(event.target.value)
                      }
                    />
                  </Field>
                </div>
                <details>
                  <summary>Advanced per-user, role, class, and codec limits</summary>
                  <div className="stack details-body">
                    <div className="three-fields">
                      <Field
                        label="User daily overrides"
                        hint={"One per line: username = GB or account UUID = GB"}
                      >
                        <textarea
                          rows={5}
                          value={uploadUserLimits}
                          onChange={(event) =>
                            setUploadUserLimits(event.target.value)
                          }
                          placeholder="alex = 5"
                        />
                      </Field>
                      <Field
                        label="Role daily overrides"
                        hint="One per line: role = GB. The stricter applicable limit wins."
                      >
                        <textarea
                          rows={5}
                          value={uploadRoleLimits}
                          onChange={(event) =>
                            setUploadRoleLimits(event.target.value)
                          }
                          placeholder={"Editor = 10\nViewer = 2"}
                        />
                      </Field>
                      <Field
                        label="Class daily limits"
                        hint="One per line: class name or UUID = GB. Shared by everyone uploading to that class."
                      >
                        <textarea
                          rows={5}
                          value={uploadClassLimits}
                          onChange={(event) =>
                            setUploadClassLimits(event.target.value)
                          }
                          placeholder="Learning Lab = 20"
                        />
                      </Field>
                    </div>
                    <div className="two-fields">
                      <Field
                        label="Allowed video codecs"
                        hint="Comma-separated FFmpeg names such as h264, hevc, vp9. Blank allows all."
                      >
                        <input
                          value={allowedVideoCodecs}
                          onChange={(event) =>
                            setAllowedVideoCodecs(event.target.value)
                          }
                          placeholder="All video codecs"
                        />
                      </Field>
                      <Field
                        label="Allowed audio codecs"
                        hint="Comma-separated FFmpeg names such as aac, mp3, opus. Blank allows all."
                      >
                        <input
                          value={allowedAudioCodecs}
                          onChange={(event) =>
                            setAllowedAudioCodecs(event.target.value)
                          }
                          placeholder="All audio codecs"
                        />
                      </Field>
                    </div>
                    <div className="alert">
                      Codec rules are verified from the file itself during
                      processing, not from its name or browser-supplied type.
                      A disallowed codec is marked failed with a specific
                      explanation.
                    </div>
                  </div>
                </details>
                <button className="button primary">Save upload limits</button>
              </form>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Adaptive TV playback"
              className="settings-panel settings-playback settings-adaptive-playback"
            >
              <div className="settings-heading">
                <div>
                  <h2>Adaptive TV playback</h2>
                  <p className="settings-copy">
                    LessonCue prepares reusable, validated 720p and 480p H.264
                    copies and automatically falls back to software if hardware
                    conversion fails.
                  </p>
                </div>
                <span
                  className={`update-state ${bootstrap.hardwareAcceleration.available && hardwareAcceleration ? "current" : ""}`}
                >
                  {bootstrap.hardwareAcceleration.available &&
                  hardwareAcceleration
                    ? "Hardware ready"
                    : "Software ready"}
                </span>
              </div>
              <div className="storage-facts">
                <Definition
                  label="Conversion engine"
                  value={
                    bootstrap.hardwareAcceleration.available &&
                    hardwareAcceleration
                      ? bootstrap.hardwareAcceleration.engine
                      : "Software (libx264)"
                  }
                />
                {bootstrap.hardwareAcceleration.device && (
                  <Definition
                    label="Hardware device"
                    value={bootstrap.hardwareAcceleration.device}
                  />
                )}
                <Definition
                  label="Hardware checked"
                  value={
                    bootstrap.hardwareAcceleration.lastCheckedAt
                      ? timeAgo(bootstrap.hardwareAcceleration.lastCheckedAt)
                      : "Starting check"
                  }
                />
                {bootstrap.hardwareAcceleration.lastHardwareUseAt && (
                  <Definition
                    label="Last hardware use"
                    value={timeAgo(
                      bootstrap.hardwareAcceleration.lastHardwareUseAt,
                    )}
                  />
                )}
              </div>
              <p className="settings-copy">
                {bootstrap.hardwareAcceleration.message}
              </p>
              {bootstrap.hardwareAcceleration.lastError && (
                <div className="alert">
                  Most recent hardware fallback:{" "}
                  {bootstrap.hardwareAcceleration.lastError}
                </div>
              )}
              <form className="stack" onSubmit={saveAdaptiveTranscoding}>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={
                      hardwareAcceleration &&
                      bootstrap.hardwareAcceleration.available
                    }
                    onChange={(e) => setHardwareAcceleration(e.target.checked)}
                    disabled={!bootstrap.hardwareAcceleration.available}
                  />{" "}
                  Use Intel hardware encoding when available
                </label>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={adaptiveTranscoding}
                    onChange={(e) => setAdaptiveTranscoding(e.target.checked)}
                  />{" "}
                  Prepare adaptive copies automatically
                </label>
                <Field
                  label="Guaranteed lead time"
                  hint="Idle capacity starts preparing new uploads immediately. This setting ensures assigned lesson media is prioritized no later than the selected number of days before class."
                >
                  <div className="number-suffix">
                    <input
                      type="number"
                      min="1"
                      max="30"
                      step="1"
                      value={transcodeLeadDays}
                      onChange={(e) => setTranscodeLeadDays(e.target.value)}
                      disabled={!adaptiveTranscoding}
                      required
                    />
                    <span>days</span>
                  </div>
                </Field>
                <div className="head-actions">
                  <button className="button primary">
                    Save playback profiles
                  </button>
                  <button
                    className="button"
                    type="button"
                    onClick={checkHardware}
                    disabled={checkingHardware}
                  >
                    {checkingHardware ? "Checking…" : "Check hardware"}
                  </button>
                </div>
              </form>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Server connection"
              className="settings-panel settings-network settings-server-connection"
            >
              <h2>Server connection</h2>
              <GameJoinAddressPanel notify={notify} />
              <Definition
                label="Browser address"
                value={`${location.protocol}//${location.host}`}
              />
              <Definition
                label="Preferred local address"
                value={bootstrap.httpPort.address}
              />
              <Definition
                label="HTTP port"
                value={String(bootstrap.httpPort.port)}
              />
              <Definition label="Server name" value={bootstrap.serverName} />
              <Definition label="Server ID" value={bootstrap.serverId} mono />
              {(bootstrap.localAddress.pending ||
                bootstrap.httpPort.pending) && (
                <div className="alert">
                  The new connection setting is being applied. The previous
                  address may remain available briefly.
                </div>
              )}
              {bootstrap.localAddress.error && (
                <p className="settings-copy">{bootstrap.localAddress.error}</p>
              )}
              {bootstrap.httpPort.error && (
                <p className="settings-copy">{bootstrap.httpPort.error}</p>
              )}
              {bootstrap.httpPort.configurable && (
                <form className="stack pairing-form" onSubmit={saveHttpPort}>
                  <Field
                    label="Browser port"
                    hint="Port 80 is the default and does not need to be typed in the browser address. If a tunnel is enabled, update its published application route after changing this port."
                  >
                    <input
                      type="number"
                      min="1"
                      max="65535"
                      step="1"
                      value={httpPort}
                      onChange={(e) => setHttpPort(e.target.value)}
                      inputMode="numeric"
                      required
                    />
                  </Field>
                  <button className="button primary">Save browser port</button>
                </form>
              )}
              {bootstrap.localAddress.supported && (
                <form
                  className="stack pairing-form"
                  onSubmit={saveLocalAddress}
                >
                  <Field
                    label="Local browser name"
                    hint="Use letters, numbers, or hyphens. Devices on this network will open this name with .local appended."
                  >
                    <div className="number-suffix domain-suffix">
                      <input
                        value={localHostname}
                        onChange={(e) =>
                          setLocalHostname(
                            e.target.value
                              .toLowerCase()
                              .replace(/[^a-z0-9-]/g, "")
                              .slice(0, 63),
                          )
                        }
                        pattern="[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?"
                        minLength={1}
                        maxLength={63}
                        required
                        autoComplete="off"
                      />
                      <span>.local</span>
                    </div>
                  </Field>
                  <label className="check-row">
                    <input type="checkbox" checked={localIpv6Enabled}
                      onChange={(event) => setLocalIpv6Enabled(event.target.checked)} />
                    <span>Allow IPv6 for the local .local address</span>
                  </label>
                  <p className="field-help">Turn this off only if local devices choose an IPv6 address that they cannot reach. This changes Avahi discovery for this server, including any other Avahi services; it does not disable system-wide IPv6 or Cloudflare remote access. Save below to apply it.</p>
                  <button className="button primary">Save local address</button>
                </form>
              )}
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Optional local HTTPS"
              className="wide-settings settings-panel settings-network settings-local-https"
            >
              <div className="settings-heading">
                <div>
                  <span className="settings-kicker">LOCAL NETWORK SECURITY</span>
                  <h2>Optional local HTTPS</h2>
                  <p className="settings-copy">
                    Browsers on a trusted private network may use local HTTP.
                    Use a local reverse proxy when this network is shared or
                    untrusted.
                  </p>
                </div>
                <span
                  className={`update-state ${location.protocol === "https:" ? "current" : "available"}`}
                >
                  {location.protocol === "https:"
                    ? "HTTPS protected"
                    : "Local HTTP"}
                </span>
              </div>
              {bootstrap.httpPort.port === 80 && (
                <div className="alert">
                  Change LessonCue’s browser port to 8080 before placing Caddy
                  on ports 80 and 443.
                </div>
              )}
              <Field label="Caddy configuration">
                <textarea readOnly rows={4} value={caddyConfig} />
              </Field>
              <div className="head-actions">
                <button
                  className="button"
                  type="button"
                  onClick={() => {
                    void navigator.clipboard.writeText(caddyConfig);
                    notify("Caddy configuration copied.");
                  }}
                >
                  Copy configuration
                </button>
                <a
                  className="button"
                  href="https://github.com/ProbablyModded/lessoncue/blob/main/docs/local-network-security.md"
                  target="_blank"
                  rel="noreferrer"
                >
                  Setup & trust instructions
                </a>
              </div>
              <p className="settings-copy">
                Do not expose LessonCue’s origin port to the internet. Remote
                access must use an HTTPS reverse proxy, VPN, or the protected
                Cloudflare option below.
              </p>
            </CollapsibleSettingsSection>
          )}
          {canManageApp && (
            <CollapsibleSettingsSection
              label="Screen pairing"
              className="settings-panel settings-playback"
            >
              <h2>Screen pairing</h2>
              <Definition
                label="Current pairing PIN"
                value={bootstrap.pairingPin || "Restricted"}
                mono
              />
              <form className="stack pairing-form" onSubmit={savePairingPin}>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={fixedPairing}
                    onChange={(e) => setFixedPairing(e.target.checked)}
                  />{" "}
                  Use a fixed local PIN
                </label>
                <Field
                  label="Six-digit pairing PIN"
                  hint={
                    fixedPairing
                      ? "This PIN remains active until an administrator changes it."
                      : "Automatic mode creates a new PIN every ten minutes."
                  }
                >
                  <input
                    value={pairingPin}
                    onChange={(e) =>
                      setPairingPin(
                        e.target.value.replace(/\D/g, "").slice(0, 6),
                      )
                    }
                    inputMode="numeric"
                    pattern="[0-9]{6}"
                    minLength={6}
                    maxLength={6}
                    disabled={!fixedPairing}
                    required={fixedPairing}
                    autoComplete="off"
                  />
                </Field>
                <button className="button primary">Save pairing mode</button>
              </form>
            </CollapsibleSettingsSection>
          )}
          {canManageApp && shortener && (
            <CollapsibleSettingsSection
              label="Integrations · URL shortener"
              className="wide-settings settings-panel settings-integrations settings-shortener"
            >
              <h2>URL shortener</h2>
              <p className="settings-copy">
                An optional self-hosted shortener on your own domain. It gives the
                organization ordinary short links, and gives LessonCue a hundred
                reserved codes that games can be joined with. Everything below is
                yours to set — LessonCue assumes no particular domain.
              </p>

              <div className="shortener-facts">
                <Definition label="Status" value={shortenerStateLabel(shortener.state)} />
                {shortener.state !== "NotInstalled" && (
                  <>
                    <Definition label="Short domain" value={shortener.publicUrl || "Not set"} />
                    <Definition label="Management" value={shortener.adminUrl || "Not set"} />
                    <Definition
                      label="Reserved LessonCue codes"
                      value={`${shortener.poolPresent} / ${shortener.poolTotal} in the shortener`}
                    />
                    <Definition label="Active game codes" value={String(shortener.activeCodes)} />
                  </>
                )}
              </div>
              {shortener.detail && <p className="settings-copy settings-warning">{shortener.detail}</p>}

              {/* Offered whenever the shortener is not actually answering. Keyed on
                  NotInstalled alone, it vanished the moment a domain was saved --
                  which is the exact point somebody wants to install it. */}
              {shortener.state !== "Running" && shortener.state !== "Degraded" && (
                <div className="settings-instructions">
                  <strong>Install it on this server</strong>
                  <p className="settings-copy">
                    The shortener runs as containers alongside LessonCue, which only the updater
                    has the privileges to set up. Asking for it here records the request; the next
                    update installs it and every update afterwards keeps it running.
                  </p>
                  {shortener.canRequestInstall ? (
                    <>
                      <Field label="Short domain" hint="The domain your short links and game codes live on.">
                        <input
                          value={shortener.domain}
                          onChange={(event) => setShortener((prev) => (prev ? { ...prev, domain: event.target.value } : prev))}
                          placeholder="go.example.org"
                          autoComplete="off"
                          spellCheck={false}
                        />
                      </Field>
                      <Field label="Send the bare domain to" hint="Where someone visiting the domain on its own should end up. Optional.">
                        <input
                          value={shortener.rootRedirectUrl}
                          onChange={(event) => setShortener((prev) => (prev
                            ? { ...prev, rootRedirectUrl: event.target.value, rootRedirectMode: event.target.value ? "organization" : "notfound" }
                            : prev))}
                          placeholder="https://www.example.org"
                          autoComplete="off"
                          spellCheck={false}
                        />
                      </Field>
                      <div className="row gap">
                        <button
                          type="button"
                          className="button primary"
                          disabled={shortenerBusy !== "" || !shortener.domain.trim()}
                          onClick={() => void setShortenerInstall(true)}
                        >{shortenerBusy === "install" ? "Installing…" : "Install the URL shortener"}</button>
                        {shortener.installRequestedFor && (
                          <button
                            type="button"
                            className="button"
                            disabled={shortenerBusy !== ""}
                            onClick={() => void setShortenerInstall(false)}
                          >Cancel</button>
                        )}
                      </div>
                      <p className="settings-copy">
                        Everything else is worked out for you, including Docker if this server does not have it yet.
                        The first install takes a minute or two.
                      </p>
                      {shortener.installRequestedFor && !shortener.installResult && (
                        <p className="settings-copy">
                          Installing for <strong>{shortener.installRequestedFor}</strong>. This takes a minute or two
                          the first time, while the containers are fetched.
                        </p>
                      )}
                      {shortener.installResult && !shortener.installResult.installed && (
                        <p className="settings-copy settings-warning">
                          {shortener.installResult.error
                            || "The shortener did not start. Check the container logs on this server."}
                        </p>
                      )}
                      {shortener.installResult?.installed && (
                        <p className="settings-copy">
                          Installed for <strong>{shortener.installResult.domain}</strong>. Waiting for it to answer
                          and for the reserved game codes to be provisioned.
                        </p>
                      )}
                    </>
                  ) : (
                    <p className="settings-copy settings-warning">
                      LessonCue cannot write to its own configuration directory here, so it cannot record
                      the request. On a container install, run the shortener stack directly instead.
                    </p>
                  )}
                </div>
              )}

              {shortener.missing.length > 0 && (
                <p className="settings-copy settings-warning">
                  Missing reserved game codes: {shortener.missing.slice(0, 8).join(", ")}
                  {shortener.missing.length > 8 ? ", and " + (shortener.missing.length - 8) + " more" : ""}.
                  Use <strong>Repair reserved codes</strong> to recreate them.
                </p>
              )}

              {shortener.conflicts.length > 0 && (
                <p className="settings-copy settings-warning">
                  Owned by someone else in the shortener: {shortener.conflicts.slice(0, 8).join(", ")}
                  {shortener.conflicts.length > 8 ? ", and " + (shortener.conflicts.length - 8) + " more" : ""}.
                  Delete or rename only those confirmed links there, then repair.
                </p>
              )}

              {shortener.failures.length > 0 && (
                <p className="settings-copy settings-warning">
                  Could not verify {shortener.failures.length} reserved code{shortener.failures.length === 1 ? "" : "s"}
                  {" "}because the shortener returned an error. Do not delete links based on this result. First failure: {shortener.failures[0]}
                </p>
              )}

              {/* Only once it is running. Before that the install block above is
                  the whole story, and a second Short domain field on the same
                  panel is just something to get wrong. */}
              {(shortener.state === "Running" || shortener.state === "Degraded") && (
              <form className="stack shortener-config-form" onSubmit={saveShortener}>
                <Field label="Short domain" hint="The domain short links live on, such as go.example.org.">
                  <input
                    value={shortener.domain}
                    onChange={(event) => setShortener((prev) => {
                      if (!prev) return prev;
                      const domain = event.target.value.trim().replace(/^https?:\/\//, "").replace(/\/.*$/, "");
                      // Keep the management address in step while it is still
                      // the derived one; an overridden value is left alone.
                      const derived = domain ? `short.${domain}` : "";
                      const following = !prev.adminHost || prev.adminHost === prev.suggestedAdminHost;
                      return {
                        ...prev,
                        domain: event.target.value,
                        suggestedAdminHost: derived,
                        adminHost: following ? derived : prev.adminHost,
                      };
                    })}
                    placeholder="go.example.org"
                    autoComplete="off"
                    spellCheck={false}
                  />
                </Field>
                <Field
                  label="Management address"
                  hint={shortener.suggestedAdminHost
                    ? `Leave blank to use ${shortener.suggestedAdminHost}. This is the console, never where short links live.`
                    : "The shortener's console. Never where short links live."}
                >
                  <input
                    value={shortener.adminHost}
                    onChange={(event) => setShortener((prev) => (prev ? { ...prev, adminHost: event.target.value } : prev))}
                    placeholder={shortener.suggestedAdminHost || "short.go.example.org"}
                    autoComplete="off"
                    spellCheck={false}
                  />
                </Field>
                <Field
                  label="Where the shortener is reachable"
                  hint={`From this server. Leave blank for ${shortener.suggestedUpstream}, which is where the shortener normally is for this installation.`}
                >
                  <input
                    value={shortener.upstream}
                    onChange={(event) => setShortener((prev) => (prev ? { ...prev, upstream: event.target.value } : prev))}
                    placeholder={shortener.suggestedUpstream}
                    autoComplete="off"
                    spellCheck={false}
                  />
                </Field>

                <Field label="When someone visits the bare short domain">
                  <select
                    value={shortener.rootRedirectMode}
                    onChange={(event) => setShortener((prev) => (prev
                      ? { ...prev, rootRedirectMode: event.target.value as ShortenerSettings["rootRedirectMode"] }
                      : prev))}
                  >
                    <option value="notfound">Show the shortener's own page</option>
                    <option value="organization">Send them to the organization's website</option>
                    <option value="lessoncue">Send them to LessonCue</option>
                    <option value="custom">Send them somewhere else</option>
                  </select>
                </Field>
                {(shortener.rootRedirectMode === "organization" || shortener.rootRedirectMode === "custom") && (
                  <Field label="Destination">
                    <input
                      value={shortener.rootRedirectUrl}
                      onChange={(event) => setShortener((prev) => (prev ? { ...prev, rootRedirectUrl: event.target.value } : prev))}
                      placeholder="https://www.example.org"
                      autoComplete="off"
                      spellCheck={false}
                    />
                  </Field>
                )}

                <div className="check-row">
                  <input
                    id="shortener-enabled"
                    type="checkbox"
                    checked={shortener.enabled}
                    onChange={(event) => setShortener((prev) => (prev ? { ...prev, enabled: event.target.checked } : prev))}
                  />
                  <label htmlFor="shortener-enabled">Use short-domain links for game codes</label>
                </div>

                <div className="row gap">
                  <button className="button primary" disabled={shortenerBusy !== ""}>Save</button>
                  <button
                    type="button"
                    className="button"
                    disabled={shortenerBusy !== "" || !shortener.integrationKeyConfigured}
                    onClick={() => void reconcileShortener()}
                  >{shortenerBusy === "repair" ? "Repairing…" : "Repair reserved codes"}</button>
                  <button
                    type="button"
                    className="button"
                    disabled={shortenerBusy !== "" || !shortener.domain}
                    onClick={() => void testShortener()}
                  >{shortenerBusy === "test" ? "Testing…" : "Test configuration"}</button>
                  {shortener.adminUrl && (
                    <a className="button" href={shortener.adminUrl} target="_blank" rel="noreferrer noopener">Open shortener</a>
                  )}
                </div>
              </form>
              )}

              {shortener.integrationKeyConfigured && shortener.adminUrl && (
                <div className="shortener-companion-grid">
                <div className="settings-instructions">
                  <strong>Password for Link Shortener Companion</strong>
                  <p className="settings-copy">
                    {shortener.consolePasswordSet
                      ? (<>The companion asks for a password before it will open. Sign in as <code>{shortener.consoleUser}</code>. Setting a new one here updates both the Administrator and Link Studio accounts.</>)
                      : (<>The companion is locked until you set its first password here. LessonCue will use it for both the Administrator and Link Studio accounts.</>)}
                  </p>
                  <div className="row gap">
                    <input
                      type="password"
                      className="input"
                      autoComplete="new-password"
                      placeholder="At least 8 characters"
                      value={shortenerConsolePassword}
                      onChange={(event) => setShortenerConsolePassword(event.target.value)}
                    />
                    <button
                      type="button"
                      className="button"
                      disabled={shortenerBusy !== "" || shortenerConsolePassword.trim().length < 8}
                      onClick={() => void saveShortenerConsolePassword()}
                    >{shortenerBusy === "console-password" ? "Setting…" : shortener.consolePasswordSet ? "Change password" : "Set password"}</button>
                  </div>
                  <small>
                    This sets the stored passwords in the companion. After signing in, an administrator
                    can change either account separately under Access &amp; brand. It protects the management
                    workspace only — your short links and game codes keep working for everyone.
                  </small>
                </div>

                <div className="settings-instructions">
                  <strong>Link Shortener Companion</strong>
                  <p className="settings-copy">
                    Open <code>{shortener.adminUrl}</code> and sign in. The companion is already connected to{" "}
                    <code>{shortener.publicUrl}</code>; its Shlink API key stays on the server and is never sent
                    to your browser. Use the API-key button only when configuring another Shlink client.
                  </p>
                  <div className="row gap">
                    <button
                      type="button"
                      className="button"
                      disabled={shortenerBusy !== ""}
                      onClick={() => void revealShortenerKey()}
                    >{shortenerBusy === "reveal" ? "Reading…" : "Show API key"}</button>
                    {shortener.adminUrl && (
                      <a className="button" href={shortener.adminUrl} target="_blank" rel="noreferrer noopener">Open console</a>
                    )}
                  </div>
                  {shortenerAdminKey && (
                    <div className="settings-key-reveal">
                      <code>{shortenerAdminKey}</code>
                      <button
                        type="button"
                        className="button"
                        onClick={() => void navigator.clipboard.writeText(shortenerAdminKey).then(() => notify("API key copied."))}
                      >Copy</button>
                      <small>
                        {shortenerKeyScope === "console"
                          ? "This key is scoped to the links you make in the console. The reserved game codes"
                            + " were made by LessonCue, so they do not appear there and cannot be edited or"
                            + " deleted through it."
                          : "This is the key LessonCue uses for its own reserved codes, and it can reach them."
                            + " For day-to-day work in the console, install the shortener again to have a"
                            + " scoped key generated for you."}
                      </small>
                    </div>
                  )}
                </div>
                </div>
              )}

              {!shortener.integrationKeyConfigured && shortener.domain && (
                <div className="settings-key-reveal">
                  <strong>LessonCue needs the shortener's API key</strong>
                  <small>
                    The installer wrote it where LessonCue can read it. If it cannot — because the
                    shortener runs elsewhere, or you rotated the key — paste it here. LessonCue cannot
                    create one: the shortener has no way to register a key it did not start with.
                    Generate another with <code>docker compose exec shlink shlink api-key:generate</code>.
                  </small>
                  <input
                    type="password"
                    value={shortenerAdminKey}
                    placeholder="Paste the shortener's API key"
                    autoComplete="off"
                    onChange={(event) => setShortenerAdminKey(event.target.value)}
                  />
                  <button
                    type="button"
                    className="button"
                    disabled={shortenerBusy !== "" || shortenerAdminKey.trim().length < 8}
                    onClick={() => void saveShortenerKey()}
                  >Record API key</button>
                </div>
              )}

              {shortenerTest && (
                <ul className="settings-check-list">
                  {shortenerTest.checks.map((check) => (
                    <li key={check.name} className={check.passed ? "passed" : "failed"}>
                      <strong>{check.passed ? "✓" : "✕"} {check.name}</strong>
                      <small>{check.detail}</small>
                    </li>
                  ))}
                </ul>
              )}

              {shortenerReport && (
                <p className="settings-copy">
                  {shortenerReport.created} created, {shortenerReport.repaired} repaired,{" "}
                  {shortenerReport.alreadyCorrect} already correct
                  {shortenerReport.conflicts.length > 0 ? `, ${shortenerReport.conflicts.length} in conflict` : ""}
                  {shortenerReport.failures.length > 0 ? `, ${shortenerReport.failures.length} failed` : ""}.
                </p>
              )}

              {shortenerTunnel && (
                <div className="settings-instructions">
                  <strong>Cloudflare Tunnel</strong>
                  <p className="settings-copy">{shortenerTunnel.explanation}</p>
                  {shortenerTunnel.routes.length > 0 && (
                    <table className="settings-route-table">
                      <thead><tr><th>Public hostname</th><th>Service</th></tr></thead>
                      <tbody>
                        {shortenerTunnel.routes.map((route) => (
                          <tr key={route.hostname}>
                            <td><code>{route.hostname}</code></td>
                            <td><code>{route.service}</code></td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                  <ol>{shortenerTunnel.instructions.map((step) => <li key={step}>{step}</li>)}</ol>
                  {shortenerTunnel.localConfigPath && (
                    <p className="settings-copy">
                      This server also has a cloudflared configuration at <code>{shortenerTunnel.localConfigPath}</code>.
                      {shortenerTunnel.localConfigRefusal
                        ? ` ${shortenerTunnel.localConfigRefusal}`
                        : shortenerTunnel.localConfigMerged
                          ? " The merged version below adds these routes and leaves every existing one untouched."
                          : " It already has these routes."}
                    </p>
                  )}
                  {shortenerTunnel.localConfigMerged && (
                    <pre className="settings-config-preview"><code>{shortenerTunnel.localConfigMerged}</code></pre>
                  )}
                </div>
              )}

              {shortener.state !== "NotInstalled" && (
                <div className="row gap">
                  <button
                    type="button"
                    className="button"
                    disabled={shortenerBusy !== "" || !shortener.enabled}
                    onClick={() => void shortenerLifecycle("disable")}
                  >Stop using short links</button>
                  <button
                    type="button"
                    className="button danger"
                    disabled={shortenerBusy !== ""}
                    onClick={() => void shortenerLifecycle("uninstall")}
                  >Remove integration</button>
                </div>
              )}
            </CollapsibleSettingsSection>
          )}
          {canManageApp && (
            <CollapsibleSettingsSection
              label="Universal controller"
              className="settings-panel settings-playback"
            >
              <h2>Universal controller</h2>
              <Definition
                label="Address"
                value={`${location.origin}/universalremote`}
              />
              <Definition
                label="PIN protection"
                value={
                  bootstrap.controllerPinConfigured
                    ? "Configured"
                    : "PIN not set"
                }
              />
              <p className="settings-copy">
                The universal remote can operate every paired classroom. Its PIN
                is separate from account passwords and TV pairing.
              </p>
              <form className="stack pairing-form" onSubmit={saveControllerPin}>
                <Field
                  label="Six-digit pairing PIN"
                >
                  <input
                    value={controllerPin}
                    onChange={(event) =>
                      setControllerPin(
                        event.target.value.replace(/\D/g, "").slice(0, 6),
                      )
                    }
                    inputMode="numeric"
                    pattern="[0-9]{6}"
                    minLength={6}
                    maxLength={6}
                    required
                    autoComplete="new-password"
                  />
                </Field>
                <button className="button primary">
                  {bootstrap.controllerPinConfigured
                    ? "Change controller PIN"
                    : "Set controller PIN"}
                </button>
              </form>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Optional remote access"
              className="cloudflare-settings settings-panel settings-network settings-cloudflare"
            >
              <div className="settings-heading">
                <div>
                  <h2>Optional remote access</h2>
                  <p className="settings-copy">
                    Connect this self-hosted server to your own domain with a
                    remotely managed Cloudflare Tunnel. Local access keeps
                    working and remains the default.
                  </p>
                </div>
                <span
                  className={`tunnel-state ${bootstrap.cloudflareTunnel.connected ? "connected" : bootstrap.cloudflareTunnel.enabled ? "waiting" : ""}`}
                >
                  {bootstrap.cloudflareTunnel.connected
                    ? `${bootstrap.cloudflareTunnel.activeConnections} edge connections`
                    : bootstrap.cloudflareTunnel.pending
                      ? "Applying…"
                      : bootstrap.cloudflareTunnel.enabled
                        ? "Waiting for connection"
                        : "Off"}
                </span>
              </div>
              <div className="storage-facts">
                <Definition
                  label="Public address"
                  value={
                    bootstrap.cloudflareTunnel.publicUrl || "Not configured"
                  }
                />
                <Definition
                  label="Local origin route"
                  value={bootstrap.cloudflareTunnel.originUrl}
                />
                <Definition
                  label="Connector version"
                  value={
                    bootstrap.cloudflareTunnel.cloudflaredVersion ||
                    "Preparing verified connector"
                  }
                />
                <Definition
                  label="Connector verified"
                  value={
                    bootstrap.cloudflareTunnel.cloudflaredCheckedAt
                      ? timeAgo(bootstrap.cloudflareTunnel.cloudflaredCheckedAt)
                      : "Pending first check"
                  }
                />
              </div>
              {bootstrap.cloudflareTunnel.cloudflaredUpdateError && (
                <div className="alert error">
                  Connector update:{" "}
                  {bootstrap.cloudflareTunnel.cloudflaredUpdateError}
                </div>
              )}
              {bootstrap.cloudflareTunnel.error && (
                <div className="alert error">
                  {bootstrap.cloudflareTunnel.error}
                </div>
              )}
              <ol className="tunnel-steps">
                <li>
                  Create a{" "}
                  <a
                    href="https://one.dash.cloudflare.com/"
                    target="_blank"
                    rel="noreferrer"
                  >
                    remotely managed tunnel in Cloudflare
                  </a>
                  .
                </li>
                <li>
                  Add a published application hostname and point its service to{" "}
                  <code>{bootstrap.cloudflareTunnel.originUrl}</code>.
                </li>
                <li>
                  Protect the hostname with Cloudflare Access, then paste its
                  tunnel token below. LessonCue keeps a checksum-verified
                  connector ready and checks it daily.
                </li>
              </ol>
              <form className="stack" onSubmit={saveCloudflareTunnel}>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={tunnelEnabled}
                    onChange={(e) => setTunnelEnabled(e.target.checked)}
                    disabled={
                      !bootstrap.cloudflareTunnel.supported || tunnelBusy
                    }
                  />{" "}
                  Enable Cloudflare Tunnel remote access
                </label>
                {tunnelEnabled && (
                  <>
                    <Field
                      label="Public hostname"
                      hint="Use the exact published application hostname configured in Cloudflare."
                    >
                      <input
                        value={tunnelHostname}
                        onChange={(e) =>
                          setTunnelHostname(e.target.value.trim().toLowerCase())
                        }
                        type="text"
                        inputMode="url"
                        placeholder="lesson.example.org"
                        required
                        autoComplete="off"
                      />
                    </Field>
                    <Field
                      label="Tunnel Token"
                      hint={
                        bootstrap.cloudflareTunnel.credentialConfigured
                          ? "Leave blank to keep the current token, or paste a replacement. LessonCue never returns this secret to the browser."
                          : "Paste the eyJ… token or Cloudflare's complete cloudflared service install command. LessonCue never returns this secret to the browser."
                      }
                    >
                      <input
                        value={tunnelToken}
                        onChange={(e) => setTunnelToken(e.target.value)}
                        type="password"
                        placeholder={
                          bootstrap.cloudflareTunnel.credentialConfigured
                            ? "••••••••••••••••"
                            : undefined
                        }
                        required={
                          !bootstrap.cloudflareTunnel.credentialConfigured
                        }
                        autoComplete="new-password"
                      />
                    </Field>
                    <label className="check-row security-confirm">
                      <input
                        type="checkbox"
                        checked={tunnelAcknowledged}
                        onChange={(e) =>
                          setTunnelAcknowledged(e.target.checked)
                        }
                        required
                      />{" "}
                      I configured Cloudflare Access, or I understand this
                      exposes the LessonCue sign-in page to the internet.
                    </label>
                  </>
                )}
                <button
                  className={`button ${tunnelEnabled ? "primary" : "danger"}`}
                  disabled={
                    tunnelBusy ||
                    bootstrap.cloudflareTunnel.pending ||
                    !bootstrap.cloudflareTunnel.supported
                  }
                >
                  {tunnelBusy
                    ? "Applying…"
                    : tunnelEnabled
                      ? bootstrap.cloudflareTunnel.enabled
                        ? bootstrap.cloudflareTunnel.connected
                          ? "Update tunnel"
                          : "Retry tunnel connection"
                        : "Install and enable tunnel"
                      : "Disable tunnel"}
                </button>
              </form>
              <p className="settings-copy">
                The connector runs as a restricted local service and uses an
                outbound-only connection. Disable it here to stop the service
                and remove its stored credential; the verified connector remains
                cached for later use and security updates.
              </p>
            </CollapsibleSettingsSection>
          )}
          {canManageApp && (
            <CollapsibleSettingsSection
              label="Recycling bin"
              className="wide-settings settings-panel settings-backup"
            >
              <div className="settings-heading">
                <div>
                  <h2>Recycling bin</h2>
                  <p className="settings-copy">
                    Deleted classes, lessons, and media remain recoverable for
                    {o.defaultRetentionDays} days. Recycled media still uses storage until it is
                    purged.
                  </p>
                </div>
                {recycleItems.length > 0 && (
                  <button className="button danger" onClick={purgeRecycleBin}>
                    Purge all
                  </button>
                )}
              </div>
              {recycleItems.length ? (
                <div className="recycle-list">
                  {recycleItems.map((item) => (
                    <div key={`${item.kind}-${item.id}`}>
                      <span className="recycle-kind">{item.kind}</span>
                      <span>
                        <strong>{item.title}</strong>
                        <small>
                          {item.detail} · deleted {timeAgo(item.deletedAt)}
                          {item.deletedBy ? ` by ${item.deletedBy}` : ""}
                        </small>
                      </span>
                      <span>
                        <small>
                          Purges{" "}
                          {new Date(
                            new Date(item.deletedAt).getTime() + o.defaultRetentionDays * 86400000,
                          ).toLocaleDateString()}
                        </small>
                        <button
                          className="button"
                          onClick={() => restoreRecycleItem(item)}
                        >
                          Restore
                        </button>
                      </span>
                    </div>
                  ))}
                </div>
              ) : (
                <Empty
                  title="Recycling bin is empty"
                  body={`Deleted classes, lessons, and media will appear here for ${o.defaultRetentionDays} days.`}
                />
              )}
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && canBackups && (
            <CollapsibleSettingsSection
              label="Privacy & backups"
              className="wide-settings settings-panel settings-backup settings-privacy-backups"
            >
              <h2>Privacy & backups</h2>
              <form className="stack retention-form" onSubmit={saveOrganization}>
                <Field
                  label="Archive retention days"
                  hint="How long deleted classes, lessons, and media remain recoverable before purge."
                >
                  <input
                    name="defaultRetentionDays"
                    type="number"
                    min="1"
                    max="3650"
                    defaultValue={o.defaultRetentionDays}
                  />
                </Field>
                <button className="button">Save retention</button>
              </form>
              <div className="privacy-callout">
                <span>⌂</span>
                <div>
                  <strong>
                    {bootstrap.cloudflareTunnel.enabled
                      ? "Local-first with optional remote access"
                      : "Fully local"}
                  </strong>
                  <p>
                    The interface, database, accounts, schedules, and media live
                    on this server.{" "}
                    {bootstrap.cloudflareTunnel.enabled
                      ? "Cloudflare carries encrypted requests to this local origin; it does not become LessonCue's data store."
                      : "No hosted service is required."}
                  </p>
                </div>
              </div>
              <div className="backup-actions">
                <label>
                  <span>Encryption password</span>
                  <input
                    type="password"
                    value={backupPassword}
                    minLength={12}
                    maxLength={1024}
                    autoComplete="new-password"
                    placeholder="At least 12 characters"
                    onChange={(event) => setBackupPassword(event.target.value)}
                  />
                </label>
                <label>
                  <span>Confirm password</span>
                  <input
                    type="password"
                    value={backupPasswordConfirmation}
                    minLength={12}
                    maxLength={1024}
                    autoComplete="new-password"
                    onChange={(event) =>
                      setBackupPasswordConfirmation(event.target.value)
                    }
                  />
                </label>
                <label className="check backup-secret-choice">
                  <input
                    type="checkbox"
                    checked={backupIncludeSecrets}
                    onChange={(event) =>
                      setBackupIncludeSecrets(event.target.checked)
                    }
                  />
                  Include this server&apos;s encrypted provider credentials,
                  pairing secrets, and data-protection keys
                </label>
                <p className="settings-copy backup-password-note">
                  LessonCue never stores this password. Keep it somewhere
                  separate from the downloaded backup. Excluding server secrets
                  is safest for ordinary exports and migrations.
                </p>
                <button
                  className="button"
                  onClick={() => void backup(false)}
                  disabled={backupBusy}
                >
                  Back up settings
                </button>
                <button
                  className="button primary"
                  onClick={() => void backup(true)}
                  disabled={backupBusy}
                >
                  Full backup
                </button>
              </div>
              <form
                className="backup-policy-form"
                onSubmit={saveBackupPolicy}
              >
                <div className="settings-section-heading">
                  <div>
                    <span>AUTOMATIC RECOVERY COPIES</span>
                    <h3>Scheduled and off-server backups</h3>
                    <p>
                      Create, authenticate, verify, retain, and optionally send
                      encrypted backups to an HTTPS WebDAV folder.
                    </p>
                  </div>
                  <label className="toggle-line">
                    <input
                      type="checkbox"
                      checked={policyEnabled}
                      onChange={(event) =>
                        setPolicyEnabled(event.target.checked)
                      }
                    />
                    Enabled
                  </label>
                </div>
                {backupPolicy?.lastError && (
                  <div className="alert error" role="alert">{backupPolicy.lastError}</div>
                )}
                {backupPolicy?.overdue && !backupPolicy.lastError && (
                  <div className="alert error">
                    The most recent verified scheduled backup is overdue.
                  </div>
                )}
                {backupPolicy?.lastSucceededAt && (
                  <div className="alert success">
                    Last verified{" "}
                    {timeAgo(
                      backupPolicy.lastVerifiedAt ||
                        backupPolicy.lastSucceededAt,
                    )}
                    {backupPolicy.lastBackupFileName
                      ? ` · ${backupPolicy.lastBackupFileName}`
                      : ""}
                    {backupPolicy.nextRunAt
                      ? ` · Next ${new Date(
                          backupPolicy.nextRunAt,
                        ).toLocaleString()}`
                      : ""}
                  </div>
                )}
                <div className="two-fields">
                  <Field label="Frequency">
                    <select
                      value={policyFrequency}
                      onChange={(event) =>
                        setPolicyFrequency(
                          event.target.value as "daily" | "weekly",
                        )
                      }
                    >
                      <option value="daily">Daily</option>
                      <option value="weekly">Weekly</option>
                    </select>
                  </Field>
                  <Field label={`Run hour in ${bootstrap.timeZone}`}>
                    <select
                      value={policyHour}
                      onChange={(event) => setPolicyHour(event.target.value)}
                    >
                      {Array.from({ length: 24 }, (_, hour) => (
                        <option key={hour} value={hour}>
                          {new Date(2000, 0, 1, hour).toLocaleTimeString([], {
                            hour: "numeric",
                          })}
                        </option>
                      ))}
                    </select>
                  </Field>
                </div>
                <p className="settings-copy">
                  Scheduled backups and media sync run at this local hour on
                  the LessonCue server. Choose 2 AM for an off-hours run.
                </p>
                {policyFrequency === "weekly" && (
                  <Field label="Weekday">
                    <select
                      value={policyWeeklyDay}
                      onChange={(event) =>
                        setPolicyWeeklyDay(event.target.value)
                      }
                    >
                      {[
                        "Sunday",
                        "Monday",
                        "Tuesday",
                        "Wednesday",
                        "Thursday",
                        "Friday",
                        "Saturday",
                      ].map((day, index) => (
                        <option key={day} value={index}>
                          {day}
                        </option>
                      ))}
                    </select>
                  </Field>
                )}
                <div className="two-fields">
                  <Field label="Keep newest copies">
                    <input
                      type="number"
                      min={1}
                      max={365}
                      value={policyRetentionCount}
                      onChange={(event) =>
                        setPolicyRetentionCount(event.target.value)
                      }
                    />
                  </Field>
                  <Field label="Maximum age in days">
                    <input
                      type="number"
                      min={1}
                      max={3650}
                      value={policyRetentionDays}
                      onChange={(event) =>
                        setPolicyRetentionDays(event.target.value)
                      }
                    />
                  </Field>
                </div>
                <Field label="Media handling">
                  <select
                    value={policyMediaMode}
                    onChange={(event) =>
                      setPolicyMediaMode(
                        event.target.value as "backup" | "sync" | "exclude",
                      )
                    }
                  >
                    <option value="backup">
                      Include media in each backup archive
                    </option>
                    <option value="sync">
                      Sync media to each remote destination
                    </option>
                    <option value="exclude">
                      Exclude media from backups and sync
                    </option>
                  </select>
                </Field>
                <p className="settings-copy">
                  {policyMediaMode === "sync"
                    ? "The encrypted archive keeps the database and other non-media resources. Each configured remote destination mirrors the media library: missing or changed files are added, and files previously managed by LessonCue that are no longer present locally are removed."
                    : policyMediaMode === "backup"
                      ? "The encrypted archive contains the database, configuration, and media library."
                      : "The encrypted archive contains the database and configuration only; existing media is preserved during configuration restores."
                  }
                </p>
                <label className="check">
                  <input
                    type="checkbox"
                    checked={policyIncludeSecrets}
                    onChange={(event) =>
                      setPolicyIncludeSecrets(event.target.checked)
                    }
                  />
                  Include local provider credentials, pairing secrets, and
                  data-protection keys
                </label>
                <Field
                  label={
                    backupPolicy?.backupPasswordConfigured
                      ? "Replace scheduled-backup password (optional)"
                      : "Scheduled-backup password"
                  }
                >
                  <input
                    type="password"
                    minLength={12}
                    maxLength={1024}
                    value={policyPassword}
                    autoComplete="new-password"
                    placeholder={
                      backupPolicy?.backupPasswordConfigured
                        ? "Leave blank to keep the saved protected password"
                        : "At least 12 characters"
                    }
                    onChange={(event) => setPolicyPassword(event.target.value)}
                  />
                </Field>
                <div className="backup-remote-settings">
                  <h4>Off-site backup destinations</h4>
                  <p className="settings-copy">
                    LessonCue encrypts and verifies each `.lcbak` locally,
                    uploads it to every configured destination, and applies
                    retention there. Google Drive, Nextcloud, ownCloud, and
                    other HTTPS WebDAV servers are supported.
                  </p>
                  {(["googledrive", "nextcloud", "owncloud", "webdav"] as const).map(
                    (provider) => {
                      const form = policyRemotes[provider];
                      const status = backupPolicy?.destinations?.find(
                        (destination) => destination.provider === provider,
                      );
                      return (
                        <div className="backup-remote-destination" key={provider}>
                          <h5>{remoteProviderLabel(provider)}</h5>
                          {provider === "googledrive" ? (
                            <>
                              <p className="settings-copy">
                                Connect the Google Drive account that has your storage plan. This is Google Drive file storage, not Google Cloud Storage buckets. LessonCue creates and manages a named folder in My Drive.
                              </p>
                              <Field label="Google OAuth client ID">
                                <input
                                  value={form.googleDriveClientId}
                                  autoComplete="off"
                                  onChange={(event) =>
                                    updatePolicyRemote(provider, {
                                      googleDriveClientId: event.target.value,
                                    })
                                  }
                                />
                              </Field>
                              <Field label={status?.googleDriveClientSecretConfigured
                                ? "Replace Google OAuth client secret (optional)"
                                : "Google OAuth client secret"}>
                                <input
                                  type="password"
                                  maxLength={4096}
                                  value={form.googleDriveClientSecret}
                                  autoComplete="new-password"
                                  placeholder={status?.googleDriveClientSecretConfigured
                                    ? "Leave blank to keep the protected secret"
                                    : "From your Google Cloud OAuth web client"}
                                  onChange={(event) =>
                                    updatePolicyRemote(provider, {
                                      googleDriveClientSecret: event.target.value,
                                    })
                                  }
                                />
                              </Field>
                              <Field label="Folder name (created in My Drive)">
                                <input
                                  value={form.folderName}
                                  placeholder="LessonCue"
                                  maxLength={128}
                                  onChange={(event) =>
                                    updatePolicyRemote(provider, {
                                      folderName: event.target.value,
                                    })
                                  }
                                />
                              </Field>
                              <p className="settings-copy">
                                In{" "}
                                <a href="https://console.cloud.google.com/apis/credentials" target="_blank" rel="noreferrer">
                                  Google Cloud Console
                                </a>
                                , enable the Google Drive API, create an OAuth client with type “Web application,” and add this exact authorized redirect URI:
                                <br />
                                <code>{window.location.origin}/api/v1/backups/google-drive/callback</code>
                                <br />
                                Save this backup policy first, then connect the account. LessonCue requests access only to files and folders it creates. Set the Google OAuth consent screen to Production for persistent scheduled access; Google may expire refresh tokens after seven days while the app is in Testing.
                              </p>
                              <div className="two-fields">
                                <Field label="Keep newest remote copies">
                                  <input
                                    type="number"
                                    min={1}
                                    max={365}
                                    value={form.retentionCount}
                                    onChange={(event) =>
                                      updatePolicyRemote(provider, {
                                        retentionCount: event.target.value,
                                      })
                                    }
                                  />
                                </Field>
                                <Field label="Delete remote copies older than (days)">
                                  <input
                                    type="number"
                                    min={1}
                                    max={3650}
                                    value={form.retentionDays}
                                    onChange={(event) =>
                                      updatePolicyRemote(provider, {
                                        retentionDays: event.target.value,
                                      })
                                    }
                                  />
                                </Field>
                              </div>
                              {status?.googleDriveConnected && (
                                <div className="alert success" role="status">
                                  Google Drive is connected and ready for scheduled backups.
                                </div>
                              )}
                              {status?.lastError && (
                                <div className="alert error" role="alert">
                                  {status.lastError}
                                </div>
                              )}
                              <button
                                className="button"
                                type="button"
                                onClick={() => void connectGoogleDrive()}
                                disabled={
                                  googleDriveBusy ||
                                  !form.googleDriveClientId.trim() ||
                                  !status?.googleDriveClientSecretConfigured
                                }
                              >
                                {googleDriveBusy
                                  ? "Waiting for Google…"
                                  : status?.googleDriveConnected
                                    ? "Reconnect Google Drive"
                                    : "Connect Google Drive"}
                              </button>
                              {!status?.googleDriveClientSecretConfigured && (
                                <p className="settings-copy">
                                  Enter the OAuth client ID and secret, then save this policy before connecting.
                                </p>
                              )}
                              {status?.lastUploadedAt && (
                                <p className="settings-copy">
                                  Last uploaded {timeAgo(status.lastUploadedAt)}
                                  {status.remoteBackupCount !== undefined
                                    ? ` · ${status.remoteBackupCount} remote copies retained`
                                    : ""}
                                </p>
                              )}
                              {status?.lastMediaSyncAt && (
                                <p className="settings-copy">
                                  Media synced {timeAgo(status.lastMediaSyncAt)}
                                  {status.lastMediaSyncAdded !== undefined ||
                                  status.lastMediaSyncUpdated !== undefined ||
                                  status.lastMediaSyncDeleted !== undefined
                                    ? ` · ${status.lastMediaSyncAdded || 0} added, ${status.lastMediaSyncUpdated || 0} updated, ${status.lastMediaSyncDeleted || 0} deleted`
                                    : ""}
                                </p>
                              )}
                            </>
                          ) : (
                            <>
                          <Field label="HTTPS WebDAV root URL">
                            <input
                              type="url"
                              value={form.url}
                              placeholder={
                                provider === "nextcloud"
                                  ? "https://cloud.example.org/remote.php/dav/files/admin/LessonCue/"
                                  : provider === "owncloud"
                                    ? "https://cloud.example.org/remote.php/dav/files/admin/LessonCue/"
                                    : "https://backup.example.org/lessoncue/"
                              }
                              onChange={(event) =>
                                updatePolicyRemote(provider, {
                                  url: event.target.value,
                                })
                              }
                            />
                          </Field>
                          <Field label="Folder name (created under this root)">
                            <input
                              value={form.folderName}
                              placeholder="LessonCue"
                              maxLength={128}
                              onChange={(event) =>
                                updatePolicyRemote(provider, {
                                  folderName: event.target.value,
                                })
                              }
                            />
                          </Field>
                          {form.url && (
                            <>
                              <div className="two-fields">
                                <Field label="Authentication">
                                  <select
                                    value={form.authentication}
                                    onChange={(event) =>
                                      updatePolicyRemote(provider, {
                                        authentication: event.target.value as
                                          | "none"
                                          | "basic"
                                          | "bearer",
                                      })
                                    }
                                  >
                                    <option value="basic">
                                      Username and app password
                                    </option>
                                    <option value="bearer">
                                      Bearer token
                                    </option>
                                    <option value="none">None</option>
                                  </select>
                                </Field>
                                {form.authentication === "basic" && (
                                  <Field label="Username">
                                    <input
                                      value={form.username}
                                      onChange={(event) =>
                                        updatePolicyRemote(provider, {
                                          username: event.target.value,
                                        })
                                      }
                                    />
                                  </Field>
                                )}
                              </div>
                              <div className="two-fields">
                                <Field label="Keep newest remote copies">
                                  <input
                                    type="number"
                                    min={1}
                                    max={365}
                                    value={form.retentionCount}
                                    onChange={(event) =>
                                      updatePolicyRemote(provider, {
                                        retentionCount: event.target.value,
                                      })
                                    }
                                  />
                                </Field>
                                <Field label="Delete remote copies older than (days)">
                                  <input
                                    type="number"
                                    min={1}
                                    max={3650}
                                    value={form.retentionDays}
                                    onChange={(event) =>
                                      updatePolicyRemote(provider, {
                                        retentionDays: event.target.value,
                                      })
                                    }
                                  />
                                </Field>
                              </div>
                              {form.authentication !== "none" && (
                                <Field
                                  label={
                                    status?.secretConfigured
                                      ? "Replace remote credential (optional)"
                                      : form.authentication === "basic"
                                        ? "App password"
                                        : "Bearer token"
                                  }
                                >
                                  <input
                                    type="password"
                                    maxLength={4096}
                                    value={form.secret}
                                    autoComplete="new-password"
                                    placeholder={
                                      status?.secretConfigured
                                        ? "Leave blank to keep the saved protected credential"
                                        : ""
                                    }
                                    onChange={(event) =>
                                      updatePolicyRemote(provider, {
                                        secret: event.target.value,
                                      })
                                    }
                                  />
                                </Field>
                              )}
                              {status?.lastError && (
                                <div className="alert error" role="alert">
                                  {status.lastError}
                                </div>
                              )}
                              {status?.lastUploadedAt && (
                                <p className="settings-copy">
                                  Last uploaded {timeAgo(status.lastUploadedAt)}
                                  {status.remoteBackupCount !== undefined
                                    ? ` · ${status.remoteBackupCount} remote copies retained`
                                    : ""}
                                </p>
                              )}
                              {status?.lastMediaSyncAt && (
                                <p className="settings-copy">
                                  Media synced {timeAgo(status.lastMediaSyncAt)}
                                  {status.lastMediaSyncAdded !== undefined ||
                                  status.lastMediaSyncUpdated !== undefined ||
                                  status.lastMediaSyncDeleted !== undefined
                                    ? ` · ${status.lastMediaSyncAdded || 0} added, ${status.lastMediaSyncUpdated || 0} updated, ${status.lastMediaSyncDeleted || 0} deleted`
                                    : ""}
                                </p>
                              )}
                            </>
                          )}
                            </>
                          )}
                        </div>
                      );
                    },
                  )}
                  <p className="settings-copy">
                    Credentials are protected on this server and never
                    included in ordinary backup archives. Use a Nextcloud or
                    ownCloud app password rather than your main account
                    password. Google OAuth credentials and refresh tokens are
                    also encrypted on the server.
                  </p>
                </div>
                <div className="head-actions">
                  <button
                    className="button primary"
                    disabled={backupPolicyBusy}
                  >
                    {backupPolicyBusy ? "Working…" : "Save backup policy"}
                  </button>
                  <button
                    className="button"
                    type="button"
                    onClick={() => void runBackupPolicy()}
                    disabled={
                      backupPolicyBusy ||
                      !backupPolicy?.backupPasswordConfigured
                    }
                  >
                    Create and verify now
                  </button>
                </div>
              </form>
              <form
                className="backup-restore-upload"
                onSubmit={previewBackupRestore}
              >
                <label>
                  <span>Restore a LessonCue backup</span>
                  <input
                    name="file"
                    type="file"
                    accept=".lcbak,.zip,application/vnd.lessoncue.backup,application/zip"
                    required
                    disabled={restoreBusy}
                  />
                </label>
                <label>
                  <span>Backup password</span>
                  <input
                    name="password"
                    type="password"
                    value={restorePassword}
                    maxLength={1024}
                    autoComplete="current-password"
                    placeholder="Required for .lcbak files"
                    onChange={(event) => setRestorePassword(event.target.value)}
                  />
                </label>
                <button className="button" disabled={restoreBusy}>
                  {restoreBusy ? "Validating…" : "Validate and preview"}
                </button>
              </form>
              <form
                className="migration-pull-form"
                onSubmit={previewMigration}
              >
                <div>
                  <span className="settings-kicker">SERVER MIGRATION</span>
                  <h3>Move from another LessonCue server</h3>
                  <p className="settings-copy">
                    Pull a one-time encrypted backup directly over the local
                    network or HTTPS, then review the normal restore preview.
                  </p>
                </div>
                <Field label="Source LessonCue address">
                  <input
                    type="url"
                    required
                    value={migrationSourceAddress}
                    placeholder="http://192.168.1.50 or https://lesson.example.org"
                    onChange={(event) =>
                      setMigrationSourceAddress(event.target.value)
                    }
                  />
                </Field>
                <Field label="One-time transfer token">
                  <input
                    required
                    minLength={64}
                    maxLength={64}
                    value={migrationToken}
                    autoComplete="off"
                    onChange={(event) =>
                      setMigrationToken(event.target.value)
                    }
                  />
                </Field>
                <Field label="Backup password">
                  <input
                    type="password"
                    required
                    value={migrationPassword}
                    maxLength={1024}
                    autoComplete="current-password"
                    onChange={(event) =>
                      setMigrationPassword(event.target.value)
                    }
                  />
                </Field>
                <button
                  className="button"
                  disabled={migrationBusy}
                >
                  {migrationBusy
                    ? "Transferring and validating…"
                    : "Transfer and preview"}
                </button>
              </form>
              {backups.slice(0, 4).map((item) => (
                <div className="backup-row backup-row-actions" key={item.id}>
                  <a href={`/api/v1/backups/${item.id}/file`}>
                    <span>{item.kind} · {formatBytes(item.sizeBytes)}</span>
                    <small>{new Date(item.createdAt).toLocaleString()}</small>
                  </a>
                  <button
                    className="button"
                    onClick={() => void verifyBackup(item)}
                  >
                    Run restore drill
                  </button>
                  <button
                    className="button"
                    onClick={() => void createMigrationLink(item)}
                    disabled={migrationBusy}
                  >
                    Transfer
                  </button>
                </div>
              ))}
            </CollapsibleSettingsSection>
          )}
          {canManageApp && (
            <CollapsibleSettingsSection
              label="Recent activity"
              className="settings-panel settings-security"
            >
              <h2>Recent activity</h2>
              <div className="audit-list">
                {audit.slice(0, 8).map((item) => (
                  <div key={item.id}>
                    <span>{item.action.replaceAll(".", " ")}</span>
                    <small>
                      {item.actor} · {timeAgo(item.timestamp)}
                    </small>
                  </div>
                ))}
              </div>
            </CollapsibleSettingsSection>
          )}
          {canServiceSettings && <TroubleshootingLogPanel notify={notify} />}
          {canServiceSettings && (
            <CollapsibleSettingsSection
              label="Server commands"
              className="settings-panel settings-system"
            >
              <h2>Server commands</h2>
              <pre>
                sudo systemctl status lessoncue{`\n`}sudo journalctl -u
                lessoncue -f{`\n`}sudo systemctl restart lessoncue
                {bootstrap.cloudflareTunnel.enabled
                  ? `\n\nsudo systemctl status lessoncue-cloudflared\nsudo journalctl -u lessoncue-cloudflared -f`
                  : ""}
              </pre>
            </CollapsibleSettingsSection>
          )}
        </div>
      </div>
    </>
  );
}


/**
 * Which address the game lobby advertises and encodes in its QR code.
 *
 * The display's own origin is whatever the TV happened to connect to, which is
 * often a bare LAN IP nobody can type. The teacher picks what the room sees.
 */
function GameJoinAddressPanel({ notify }: { notify: (message: string) => void }) {
  const [status, setStatus] = useState<JoinAddressStatus | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let active = true;
    api<JoinAddressStatus>("/api/v1/activity-join-address")
      .then((value) => { if (active) setStatus(value); })
      .catch(() => { if (active) setStatus(null); });
    return () => { active = false; };
  }, []);

  async function choose(mode: string) {
    setSaving(true);
    try {
      const updated = await api<JoinAddressStatus>("/api/v1/activity-join-address", {
        method: "PUT",
        body: JSON.stringify({ mode }),
      });
      setStatus(updated);
      notify(
        updated.url
          ? `Games will show ${updated.url.replace(/^https?:\/\//, "")}`
          : "No reachable address yet. Games will show the code only.",
      );
    } catch (e) {
      notify(errorText(e));
    } finally {
      setSaving(false);
    }
  }

  if (!status) return null;
  const selected = status.options.find((option) => option.id === status.mode);
  const fellBack = status.mode !== "auto" && status.resolvedFrom !== status.mode;

  return (
    <div className="stack">
      <Field
        label="Game join address"
        hint="Shown on the lobby screen and encoded in the QR code players scan."
      >
        <select
          value={status.mode}
          disabled={saving}
          onChange={(event) => void choose(event.target.value)}
        >
          {status.options.map((option) => (
            <option key={option.id} value={option.id}>
              {option.label}
              {option.url ? ` — ${option.url.replace(/^https?:\/\//, "")}` : ""}
            </option>
          ))}
        </select>
      </Field>
      {selected?.detail && <p className="settings-copy">{selected.detail}</p>}
      <Definition
        label="Players will see"
        value={status.url
          // The short domain carries the code directly; every other address is
          // LessonCue's own, where the game lives under /play.
          ? `${status.url}${status.resolvedFrom === "shortener" ? "" : "/play"}/CODE`
          : "Code only — no reachable address yet"}
      />
      {fellBack && (
        <div className="alert">
          That address is not reachable right now, so games are showing the{" "}
          {status.resolvedFrom === "none"
            ? "code only"
            : (status.options.find((option) => option.id === status.resolvedFrom)?.label
                ?? status.resolvedFrom).toLowerCase()}{" "}
          address instead.
        </div>
      )}
    </div>
  );
}


/**
 * Hide Activities from the people planning lessons.
 *
 * For running an unfinished game system on a real server: nothing is deleted,
 * teacher-facing surfaces disappear, and nothing new can be launched. A session
 * already in progress is deliberately left alone.
 */
function ActivityAvailabilityPanel({
  notify,
  refresh,
}: {
  notify: (message: string) => void;
  refresh: () => void;
}) {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let active = true;
    api<{ enabled: boolean }>("/api/v1/activity-availability")
      .then((value) => { if (active) setEnabled(value.enabled); })
      .catch(() => { if (active) setEnabled(null); });
    return () => { active = false; };
  }, []);

  async function choose(next: boolean) {
    setSaving(true);
    try {
      const updated = await api<{ enabled: boolean }>("/api/v1/activity-availability", {
        method: "PUT",
        body: JSON.stringify({ enabled: next }),
      });
      setEnabled(updated.enabled);
      refresh();
      notify(
        updated.enabled
          ? "Activities are available to teachers."
          : "Activities are hidden. Games already running are unaffected.",
      );
    } catch (e) {
      notify(errorText(e));
    } finally {
      setSaving(false);
    }
  }

  if (enabled === null) return null;

  return (
    <div className="stack">
      <label className="switch-row">
        <input
          type="checkbox"
          checked={enabled}
          disabled={saving}
          onChange={(event) => void choose(event.target.checked)}
        />
        <span />
        <div>
          <strong>Offer Activities to teachers</strong>
        </div>
      </label>
      <p className="settings-copy">
        Turn this off while a game is still being built. The Activities area and
        the activity cue type disappear for everyone, and no new game can be
        started. Nothing is deleted, and a game already running keeps going.
      </p>
    </div>
  );
}

/** The integration's state, in words rather than an enum name. */
function shortenerStateLabel(state: ShortenerSettings["state"]): string {
  switch (state) {
    case "Running": return "Running";
    case "Degraded": return "Degraded — some reserved codes need attention";
    case "Stopped": return "Stopped — the shortener is not answering";
    case "Configured": return "Configured, not yet in use";
    case "Installing": return "Installing";
    case "ConfigurationError": return "Configuration error";
    default: return "Not installed";
  }
}
