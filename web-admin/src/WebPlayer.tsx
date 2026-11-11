import { CSSProperties, FormEvent, PointerEvent as ReactPointerEvent, ReactNode, useEffect, useMemo, useRef, useState } from "react";
import QRCode from "qrcode";
import { WeatherConditionArtwork, WeatherDropArtwork, WeatherWindArtwork } from "./WeatherArtwork";
import { ActivityDisplay } from "./activities/ActivityDisplay";
import "./signage-studio.css";

const APP_VERSION = "0.46.44";
/** Shared with public/sw.js, which answers media requests from it. */
const MEDIA_CACHE = "lessoncue-media-v1";

const IDENTITY_KEY = "lessoncue.web-player.identity.v1";

type Identity = { screenId: string; token: string; deviceName: string };
type ConnectionState = "connecting" | "online" | "reconnecting" | "offline" | "error";
type CuePoint = { name: string; positionMs: number };
export type CueItem = {
  itemId: string;
  mediaId?: string;
  type: string;
  title: string;
  downloadUrl?: string;
  playbackUrl?: string;
  streamingFallbackUrl?: string;
  contentType?: string;
  fileExtension?: string;
  durationMs?: number;
  startMs: number;
  endMs?: number;
  volumePercent: number;
  imageDurationSeconds?: number;
  estimatedDurationSeconds?: number;
  endBehavior: string;
  allowSkip: boolean;
  sourceKind?: string;
  linkKind?: string;
  notes?: string;
  fadeInMs: number;
  fadeOutMs: number;
  fitMode?: "fit" | "fill" | "letterbox";
  rotationDegrees?: number;
  cropLeftPercent?: number;
  cropTopPercent?: number;
  cropRightPercent?: number;
  cropBottomPercent?: number;
  muted?: boolean;
  playbackRatePercent?: number;
  repeatCount?: number;
  backgroundColor?: string;
  transitionStyle?: string;
  transitionDurationMs?: number;
  cuePoints: CuePoint[];
  sizeBytes?: number;
  sha256?: string;
  offlineEligible?: boolean;
  renderSupport?: "supported" | "fallback";
  fallbackMessage?: string;
  activityDefinitionId?: string;
  activity?: {
    definitionId: string;
    activityType: string;
    definitionVersion: number;
    name: string;
    requiresNetwork: boolean;
  };
};
type Playlist = {
  playlistId: string;
  title: string;
  lessonDate: string;
  designatedStartAt?: string;
  preRollStartsAt?: string;
  countdown?: { enabled: boolean; itemId: string; durationMs: number; startAt?: string; item: CueItem };
  preRoll?: { enabled: boolean; loop: boolean; items: CueItem[] };
  postLesson?: { enabled: boolean; loop: boolean; items: CueItem[] };
  items: CueItem[];
};
export type SignageContentPlaylist = { id: string; name: string; playbackMode: string; synchronization: string; version: number; items: SignagePlaylistEntry[] };
export type Signage = {
  id: string;
  version?: number;
  publishedVersion?: number;
  name: string;
  mode: string;
  priority: number;
  message: string;
  backgroundColor: string;
  textColor: string;
  mediaUrl?: string;
  media?: CueItem;
  layoutPreset?: string;
  zones?: SignageZone[];
  widgetCacheUpdatedAt?: string;
  widgetCacheError?: string;
  contentPlaylist?: SignageContentPlaylist;
  kiosk?: { enabled: boolean; interactionUrl?: string; timeoutSeconds: number; showCloseButton: boolean; showTouchIndicator: boolean; virtualKeyboard: boolean };
  volumePercent?: number;
  displayPower?: string;
  backgroundAudio?: CueItem;
};
export type SignageCalendarEvent = { title: string; description?: string; location?: string; startsAt?: string; endsAt?: string; allDay?: boolean };
export type SignageWeatherSnapshot = {
  temperature?: number; feelsLike?: number; high?: number; low?: number; precipitation?: number;
  humidity?: number; wind?: number; temperatureUnit?: string; windUnit?: string; conditions?: string;
  forecast?: string; sunrise?: string; sunset?: string; windText?: string;
};
export type SignageWidgetCache = { zoneId: string; title: string; text: string; items: string[]; refreshedAt: string; source?: string; icon?: string; events?: SignageCalendarEvent[]; weather?: SignageWeatherSnapshot };
export type SignageZone = {
  id: string; type: string; title?: string; content?: string; sourceUrl?: string; streamUrl?: string; htmlUrl?: string;
  x: number; y: number; width: number; height: number; backgroundColor: string; textColor: string; accentColor: string; refreshMinutes: number;
  rotation?: number; zIndex?: number; opacity?: number; fit?: "cover" | "contain" | "fill"; locked?: boolean; hidden?: boolean; flipX?: boolean; flipY?: boolean;
  media?: CueItem; cached?: SignageWidgetCache; fontFamily?: string; fontSize?: number; fontScalePercent?: number; fontWeight?: number; italic?: boolean; underline?: boolean;
  lineHeightPercent?: number; textAlign?: CSSProperties["textAlign"]; strokeColor?: string; strokeWidth?: number; cornerRadius?: number;
  qrValue?: string; qrLabelTop?: string; qrLabelBottom?: string; qrLabelLeft?: string; qrLabelRight?: string; qrPlacement?: "left" | "center" | "right"; qrSizePercent?: number;
  tickerSpeed?: number; counterTargetAt?: string; counterRepeatWeekly?: boolean; richTextJson?: string;
  clockDisplay?: "time" | "date" | "both"; clockTimeFormat?: "12h" | "12h-seconds" | "24h" | "24h-seconds";
  clockDateFormat?: "short" | "medium" | "long" | "numeric"; clockOrder?: "time-date" | "date-time" | "inline";
  clockTimeFontSize?: number; clockDateFontSize?: number;
  weatherPostalCode?: string; contentPlaylistId?: string; streamOverrideWhenLive?: boolean; streamOverrideStartsAt?: string; streamOverrideEndsAt?: string; contentPlaylist?: SignageContentPlaylist;
  contentPadding?: number; contentScale?: number; verticalAlign?: "top" | "middle" | "bottom";
  mediaScale?: number; mediaOffsetX?: number; mediaOffsetY?: number; mediaAllowOverflow?: boolean;
  wifiNetworkName?: string; wifiSecurity?: string; wifiHidden?: boolean;
  weatherProvider?: "open-meteo" | "nws" | "custom"; weatherLocation?: string; weatherFields?: string;
  weatherIconStyle?: "color" | "white"; weatherLayout?: "icon-top" | "icon-left" | "icon-right" | "compact";
  weatherIconSize?: number; weatherTitleSize?: number; weatherTemperatureSize?: number; weatherDetailsSize?: number;
  clockShowPeriod?: boolean; clockShowWeekday?: boolean; clockShowYear?: boolean;
  calendarMaxItems?: number; calendarFields?: string;
  audienceSessionId?: string; audienceCode?: string; audienceShowResults?: boolean; audienceResultDelaySeconds?: number;
  renderSupport?: "supported" | "fallback"; fallbackMessage?: string;
};
export type SignagePlaylistEntry = { id: string; kind: string; title?: string; durationSeconds: number; transition?: string; hidden?: boolean; transparent?: boolean; sourceUrl?: string; appType?: string; volumePercent?: number; muted?: boolean; fadeInMs?: number; fadeOutMs?: number; fit?: "contain" | "cover" | "fill"; media?: CueItem; layout?: { id: string; name: string; backgroundColor: string; canvasWidth: number; canvasHeight: number; safeAreaPercent: number; zones: SignageZone[]; backgroundAudio?: CueItem } };
type Manifest = {
  manifestVersion: number;
  capabilityContractVersion?: number;
  displayCapabilities?: {
    platform: string;
    displayName: string;
    contractVersion: number;
    minimumClientVersion: string;
    capabilities: { id: string; label: string; supported: boolean; fallback: string; notes?: string }[];
    limitations: string[];
  };
  compatibilityWarnings?: {
    code: string;
    title: string;
    message: string;
    fallback: string;
  }[];
  screen: { id: string; name: string; volunteerMode: boolean; site: string; signageOnly?: boolean; permanentPairing?: boolean };
  signage: Signage[];
  signageSchedule?: Signage[];
  playlists: Playlist[];
};
type Command = {
  changed: boolean;
  version: number;
  action: string;
  lessonId?: string;
  itemId?: string;
  positionMs?: number;
};
type ActivePlayback = {
  playlist: Playlist;
  items: CueItem[];
  index: number;
  seekMs: number;
  mode: "lesson" | "preroll" | "countdown" | "postLesson";
};
type PlaybackStatus = {
  state: string;
  lessonId?: string;
  itemId?: string;
  positionMs: number;
  durationMs?: number;
  volumePercent: number;
  error?: string;
};

const idleStatus: PlaybackStatus = { state: "idle", positionMs: 0, volumePercent: 100 };

export function WebPlayerApp() {
  const [identity, setIdentity] = useState<Identity | null>(() => readIdentity());
  const [manifest, setManifest] = useState<Manifest>();
  const [connection, setConnection] = useState<ConnectionState>(identity ? "connecting" : "offline");
  const [connectionMessage, setConnectionMessage] = useState("");
  const [active, setActive] = useState<ActivePlayback>();
  const [paused, setPaused] = useState(false);
  const [autoplayBlocked, setAutoplayBlocked] = useState(false);
  const [interactionUnlocked, setInteractionUnlocked] = useState(false);
  const [unlockNonce, setUnlockNonce] = useState(0);
  const acknowledgedVersionRef = useRef(0);
  const commandErrorRef = useRef<string | null>(null);
  const heartbeatWakeRef = useRef<(() => void) | undefined>(undefined);
  const [status, setStatus] = useState<PlaybackStatus>(idleStatus);
  const [controlsVisible, setControlsVisible] = useState(true);
  const requestedCueRef = useRef<string | null>(new URLSearchParams(location.search).get("cue"));
  const statusRef = useRef(status);
  const activeRef = useRef(active);
  const manifestRef = useRef(manifest);
  const lastInteractionRef = useRef(Date.now());
  const networkLatencyRef = useRef<number | undefined>(undefined);
  const errorsRef = useRef<{ timestamp: string; area: string; message: string; itemId?: string }[]>([]);
  const signageCacheRef = useRef<{ itemId: string; title: string; state: string; sizeBytes: number; expectedBytes?: number; error?: string }[]>([]);
  const interruptedRef = useRef<{ playback: ActivePlayback; paused: boolean } | undefined>(undefined);
  useEffect(() => {
    const query = new URLSearchParams(location.search);
    if (!query.get("screenId") || !query.get("token") || !identity) return;
    localStorage.setItem(IDENTITY_KEY, JSON.stringify(identity));
    query.delete("screenId"); query.delete("token"); query.delete("name"); query.delete("screen"); query.delete("cue");
    history.replaceState(null, "", `${location.pathname}${query.size ? `?${query}` : ""}`);
  }, [identity]);
  const repeatProgressRef = useRef<{ itemId: string; completed: number }>({ itemId: "", completed: 0 });
  useDurableMediaCache(manifest?.signageSchedule, manifest?.playlists, identity, signageCacheRef, errorsRef);

  useEffect(() => { statusRef.current = status; }, [status]);
  useEffect(() => { activeRef.current = active; }, [active]);
  useEffect(() => { manifestRef.current = manifest; }, [manifest]);

  function forgetPairing(message = "") {
    commandErrorRef.current = null;
    localStorage.removeItem(IDENTITY_KEY);
    setIdentity(null);
    setManifest(undefined);
    setActive(undefined);
    setStatus(idleStatus);
    setConnection("offline");
    setConnectionMessage(message);
  }

  async function loadManifest(current: Identity, signal?: AbortSignal) {
    const started = performance.now();
    const response = await fetch(`/api/v1/screens/${current.screenId}/manifest`, {
      headers: { Authorization: `Bearer ${current.token}` },
      cache: "no-store",
      signal,
    });
    networkLatencyRef.current = Math.round(performance.now() - started);
    if (response.status === 401 || response.status === 404) throw new PairingExpiredError();
    if (!response.ok) throw new Error(`Manifest request failed (${response.status}).`);
    const next = await response.json() as Manifest;
    if (signal?.aborted) throw new DOMException("Manifest request cancelled", "AbortError");
    setManifest(next);
    const requestedCue = requestedCueRef.current;
    if (requestedCue) {
      const match = next.playlists.map(playlist => {
        const items = [
          ...(playlist.preRoll?.items || []),
          ...(playlist.countdown ? [playlist.countdown.item] : []),
          ...playlist.items,
          ...(playlist.postLesson?.items || []),
        ];
        return { playlist, items, index: items.findIndex(item => item.itemId === requestedCue) };
      }).find(candidate => candidate.index >= 0);
      if (match) {
        requestedCueRef.current = null;
        startPlayback(match.playlist, match.items, match.index);
      }
    }
    setConnection("online");
    setConnectionMessage("");
    return next;
  }

  function startPlayback(playlist: Playlist, items: CueItem[], index = 0, seekMs = 0, mode: ActivePlayback["mode"] = "lesson") {
    if (!items.length) return;
    const playback = { playlist, items, index: Math.max(0, Math.min(index, items.length - 1)), seekMs: Math.max(0, seekMs), mode };
    if (manifestRef.current?.signage.some(sign => sign.mode === "emergency")) {
      interruptedRef.current = { playback, paused: false };
      setActive(undefined);
      setStatus(idleStatus);
      return;
    }
    setPaused(false);
    setAutoplayBlocked(false);
    setActive(playback);
  }

  function applyCommand(command: Command, sourceManifest = manifestRef.current) {
    const current = activeRef.current;
    switch (command.action) {
      case "play": {
        const playlist = sourceManifest?.playlists.find(item => item.playlistId === command.lessonId);
        if (!playlist) throw new Error("The requested lesson is not available to this screen.");
        const allItems = [...(playlist.preRoll?.items || []), ...(playlist.countdown ? [playlist.countdown.item] : []), ...playlist.items, ...(playlist.postLesson?.items || [])];
        const selected = command.itemId ? allItems.findIndex(item => item.itemId === command.itemId) : -1;
        startPlayback(playlist, selected >= 0 ? allItems : playlist.items, selected >= 0 ? selected : 0, command.positionMs || 0);
        break;
      }
      case "stop":
        interruptedRef.current = undefined;
        setActive(undefined);
        setPaused(false);
        setStatus(idleStatus);
        break;
      case "next":
        if (current) moveTo(current.index + 1);
        break;
      case "previous":
        if (current) moveTo(Math.max(0, current.index - 1));
        break;
      case "seek":
        if (current) setActive({ ...current, seekMs: Math.max(0, command.positionMs || 0) });
        break;
      case "pause":
        setPaused(true);
        break;
      case "resume":
        setPaused(false);
        setUnlockNonce(value => value + 1);
        break;
    }
  }

  function moveTo(index: number) {
    const current = activeRef.current;
    if (!current) return;
    if (index < 0 || index >= current.items.length) {
      setActive(undefined);
      setStatus(idleStatus);
      return;
    }
    setPaused(false);
    setAutoplayBlocked(false);
    setActive({ ...current, index, seekMs: 0 });
  }

  function seekRelative(deltaMs: number) {
    const current = activeRef.current;
    if (!current) return;
    const item = current.items[current.index];
    if (item.type === "activity" || item.activity || item.activityDefinitionId) return;
    const observed = statusRef.current.itemId === item.itemId ? statusRef.current.positionMs : current.seekMs;
    const duration = statusRef.current.durationMs ?? effectiveDuration(item);
    const next = Math.max(0, Math.min(duration ?? Number.MAX_SAFE_INTEGER, observed + deltaMs));
    setActive({ ...current, seekMs: next });
    setUnlockNonce(value => value + 1);
  }

  function finishItem() {
    const current = activeRef.current;
    if (!current) return;
    const item = current.items[current.index];
    const repeats = Math.max(1, item.repeatCount || 1);
    if (repeatProgressRef.current.itemId !== item.itemId) repeatProgressRef.current = { itemId: item.itemId, completed: 0 };
    repeatProgressRef.current.completed += 1;
    if (item.endBehavior !== "loop" && repeatProgressRef.current.completed < repeats) {
      setActive({ ...current, seekMs: 0 });
      setUnlockNonce(value => value + 1);
      return;
    }
    repeatProgressRef.current = { itemId: "", completed: 0 };
    const sectionLoop = current.mode === "preroll" || current.mode === "postLesson";
    if (sectionLoop) {
      setActive({ ...current, index: current.index + 1 < current.items.length ? current.index + 1 : 0, seekMs: 0 });
      setUnlockNonce(value => value + 1);
    } else if (current.mode === "lesson" && current.index === current.items.length - 1 && current.playlist.postLesson?.items.length) {
      startPlayback(current.playlist, current.playlist.postLesson.items, 0, 0, "postLesson");
    } else if (item.endBehavior === "loop") {
      setActive({ ...current, seekMs: 0 });
      setUnlockNonce(value => value + 1);
    } else if (item.endBehavior === "playlistLoop") {
      setActive({ ...current, index: 0, seekMs: 0 });
    } else if (item.endBehavior === "advance" && current.index + 1 < current.items.length) {
      moveTo(current.index + 1);
    } else if (item.endBehavior === "pause") {
      setPaused(true);
      setStatus(previous => ({ ...previous, state: "completed" }));
    } else {
      setActive(undefined);
      setStatus(idleStatus);
    }
  }

  useEffect(() => {
    if (!identity) return;
    const controller = new AbortController();
    let stopped = false;
    let retryMs = 1_000;
    let timer = 0;
    async function refresh() {
      try {
        await loadManifest(identity!, controller.signal);
        if (stopped || controller.signal.aborted) return;
        retryMs = 1_000;
        timer = window.setTimeout(refresh, 30_000);
      } catch (error) {
        if (stopped || controller.signal.aborted) return;
        if (error instanceof PairingExpiredError) return forgetPairing("This browser was unpaired. Pair it again to continue.");
        setConnection(navigator.onLine ? "reconnecting" : "offline");
        setConnectionMessage(errorText(error));
        timer = window.setTimeout(refresh, retryMs);
        retryMs = Math.min(retryMs * 2, 15_000);
      }
    }
    void refresh();
    return () => { stopped = true; controller.abort(); window.clearTimeout(timer); };
  }, [identity]);

  useEffect(() => {
    const emergency = manifest?.signage.some(sign => sign.mode === "emergency") ?? false;
    const current = activeRef.current;
    if (emergency && current && !interruptedRef.current) {
      interruptedRef.current = {
        playback: { ...current, seekMs: Math.max(current.seekMs, statusRef.current.positionMs) },
        paused,
      };
      setActive(undefined);
      setStatus(idleStatus);
      return;
    }
    if (!emergency && interruptedRef.current) {
      const interrupted = interruptedRef.current;
      interruptedRef.current = undefined;
      setPaused(interrupted.paused);
      setActive(interrupted.playback);
    }
  }, [manifest?.manifestVersion, manifest?.signage, paused]);

  useEffect(() => {
    if (!identity) return;
    acknowledgedVersionRef.current = 0;
    commandErrorRef.current = null;
  }, [identity]);

  useEffect(() => {
    if (!identity) return;
    const controller = new AbortController();
    let stopped = false;
    let version: number | undefined;
    let timer = 0;
    async function poll() {
      let delay = 750;
      try {
        const query = version == null ? "" : `?after=${version}`;
        const response = await fetch(`/api/v1/screens/${identity!.screenId}/control${query}`, {
          headers: { Authorization: `Bearer ${identity!.token}` },
          cache: "no-store",
          signal: controller.signal,
        });
        if (stopped || controller.signal.aborted) return;
        if (response.status === 401 || response.status === 404) throw new PairingExpiredError();
        if (!response.ok) throw new Error(`Controller request failed (${response.status}).`);
        const command = await response.json() as Command;
        if (stopped || controller.signal.aborted) return;
        if (version == null) {
          version = command.version;
        } else if (command.changed) {
          let freshManifest = manifestRef.current;
          if (command.action === "play") freshManifest = await loadManifest(identity!, controller.signal);
          if (stopped || controller.signal.aborted) return;
          if (command.action === "play" && !freshManifest?.playlists.some(item => item.playlistId === command.lessonId)) {
            // A successful fresh manifest definitively rejects this command.
            // Report it, but do not poison the queue or interrupt current media.
            commandErrorRef.current = "The requested lesson is not available to this screen.";
          } else {
            applyCommand(command, freshManifest);
            commandErrorRef.current = null;
          }
          version = command.version;
          acknowledgedVersionRef.current = command.version;
          heartbeatWakeRef.current?.();
        } else {
          version = Math.max(version, command.version);
        }
        setConnection("online");
      } catch (error) {
        if (stopped || controller.signal.aborted) return;
        if (error instanceof PairingExpiredError) return forgetPairing("This browser was unpaired. Pair it again to continue.");
        setConnection(navigator.onLine ? "reconnecting" : "offline");
        setConnectionMessage(errorText(error));
        delay = 2_500;
      }
      if (!stopped && !controller.signal.aborted) timer = window.setTimeout(poll, delay);
    }
    void poll();
    return () => { stopped = true; controller.abort(); window.clearTimeout(timer); };
    // Command application intentionally reads the latest manifest/playback refs without restarting the long-poll loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity]);

  useEffect(() => {
    if (!identity) return;
    const controller = new AbortController();
    let stopped = false;
    let timer = 0;
    let inFlight = false;
    let pending = false;
    async function heartbeat() {
      inFlight = true;
      const current = statusRef.current;
      try {
        const storage = await navigator.storage?.estimate?.().catch(() => undefined);
        if (stopped || controller.signal.aborted) return;
        const response = await fetch("/api/v1/tv/status", {
          method: "POST",
          headers: { "Content-Type": "application/json", Authorization: `Bearer ${identity!.token}` },
          signal: controller.signal,
          body: JSON.stringify({
            screenId: identity!.screenId,
            appVersion: APP_VERSION,
            online: navigator.onLine,
            freeBytes: Math.max(0, (storage?.quota || 0) - (storage?.usage || 0)),
            manifestVersion: manifestRef.current?.manifestVersion || 0,
            failedDownloads: errorsRef.current.length,
            acknowledgedControlVersion: acknowledgedVersionRef.current,
            playbackState: commandErrorRef.current ? "error" : current.state,
            lessonId: current.lessonId,
            itemId: current.itemId,
            positionMs: current.positionMs,
            durationMs: current.durationMs,
            volumePercent: current.volumePercent,
            playbackError: commandErrorRef.current || current.error,
            cachedItems: signageCacheRef.current.filter(item => item.state === "ready").length,
            totalItems: manifestItemCount(manifestRef.current),
            deviceModel: browserName(),
            osVersion: navigator.userAgent.slice(0, 80),
            clientTimeUnixMs: Date.now(),
            networkLatencyMs: networkLatencyRef.current,
            networkQuality: networkQuality(networkLatencyRef.current, navigator.onLine),
            codecCapabilities: codecCapabilities(),
            cacheInventory: signageCacheRef.current,
            recentErrors: errorsRef.current,
            signageId: manifestRef.current?.signage?.[0]?.id || null,
            signageVersion: manifestRef.current?.signage?.[0]?.publishedVersion || manifestRef.current?.signage?.[0]?.version || 1,
            signageName: manifestRef.current?.signage?.[0]?.name || null,
            signageError: errorsRef.current.find(error => error.area?.startsWith("signage"))?.message || null,
          }),
        });
        if (stopped || controller.signal.aborted) return;
        if (response.status === 401 || response.status === 404) throw new PairingExpiredError();
        if (!response.ok) throw new Error(`Heartbeat failed (${response.status}).`);
        setConnection("online");
      } catch (error) {
        if (stopped || controller.signal.aborted) return;
        if (error instanceof PairingExpiredError) return forgetPairing("This browser was unpaired. Pair it again to continue.");
        setConnection(navigator.onLine ? "reconnecting" : "offline");
      } finally {
        inFlight = false;
        if (!stopped && !controller.signal.aborted) {
          timer = window.setTimeout(heartbeat, pending ? 0 : activeRef.current ? 2_000 : 10_000);
          pending = false;
        }
      }
    }
    const wake = () => {
      if (stopped || controller.signal.aborted) return;
      if (inFlight) { pending = true; return; }
      window.clearTimeout(timer);
      timer = window.setTimeout(heartbeat, 0);
    };
    heartbeatWakeRef.current = wake;
    void heartbeat();
    return () => {
      stopped = true;
      if (heartbeatWakeRef.current === wake) heartbeatWakeRef.current = undefined;
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [identity]);

  useEffect(() => {
    const update = () => {
      if (!navigator.onLine) {
        setConnection("offline");
        setConnectionMessage("The browser is offline. Playback already loaded by the browser may continue.");
      } else if (identity) {
        setConnection("reconnecting");
        setConnectionMessage("Reconnecting to the local LessonCue server…");
      }
    };
    window.addEventListener("online", update);
    window.addEventListener("offline", update);
    return () => { window.removeEventListener("online", update); window.removeEventListener("offline", update); };
  }, [identity]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      const currentManifest = manifestRef.current;
      const current = activeRef.current;
      if (!currentManifest) return;
      if (!current) {
        const scheduled = currentManifest.playlists.map(playlist => ({ playlist, phase: schedulePhase(playlist) }))
          .find(item => item.phase.kind === "countdown" || item.phase.kind === "preroll");
        if (scheduled?.phase.kind === "countdown" && scheduled.playlist.countdown)
          startPlayback(scheduled.playlist, [scheduled.playlist.countdown.item], 0, scheduled.phase.seekMs, "countdown");
        else if (scheduled?.phase.kind === "preroll" && scheduled.playlist.preRoll)
          startPlayback(scheduled.playlist, scheduled.playlist.preRoll.items, 0, 0, "preroll");
        return;
      }
      if (current.mode === "lesson") return;
      const phase = schedulePhase(current.playlist);
      if (current.mode === "preroll" && phase.kind === "countdown" && current.playlist.countdown)
        startPlayback(current.playlist, [current.playlist.countdown.item], 0, phase.seekMs, "countdown");
      else if ((current.mode === "preroll" || current.mode === "countdown") && phase.kind === "ready")
        startPlayback(current.playlist, current.playlist.items, 0, 0, "lesson");
    }, 500);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    function interact() {
      lastInteractionRef.current = Date.now();
      setControlsVisible(true);
    }
    function keyboard(event: KeyboardEvent) {
      if (!identity || event.target instanceof HTMLInputElement) return;
      interact();
      if ([" ", "ArrowRight", "ArrowLeft", "PageDown", "PageUp", "Home", "Escape", "f", "F", "MediaPlayPause", "MediaTrackNext", "MediaTrackPrevious"].includes(event.key))
        event.preventDefault();
      const current = activeRef.current;
      if (event.key === " " || event.key === "MediaPlayPause") {
        setInteractionUnlocked(true);
        setPaused(value => !value);
        setAutoplayBlocked(false);
        setUnlockNonce(value => value + 1);
      } else if (event.key === "Enter" && activeRef.current) {
        setInteractionUnlocked(true);
        setAutoplayBlocked(false);
        setPaused(false);
        setUnlockNonce(value => value + 1);
      } else if (event.key === "ArrowRight" || event.key === "PageDown" || event.key === "MediaTrackNext") {
        if (current) moveTo(current.index + 1);
      } else if (event.key === "ArrowLeft" || event.key === "PageUp" || event.key === "MediaTrackPrevious") {
        if (current) moveTo(Math.max(0, current.index - 1));
      } else if (event.key === "Home" && current) {
        setActive({ ...current, seekMs: 0 });
      } else if (event.key === "Escape") {
        setActive(undefined);
        setStatus(idleStatus);
      } else if (event.key.toLowerCase() === "f") {
        void document.documentElement.requestFullscreen?.().catch(() => undefined);
      }
    }
    const hideTimer = window.setInterval(() => {
      if (activeRef.current && Date.now() - lastInteractionRef.current > 4_000) setControlsVisible(false);
    }, 1_000);
    window.addEventListener("mousemove", interact);
    window.addEventListener("pointerdown", interact);
    window.addEventListener("keydown", keyboard);
    return () => {
      window.clearInterval(hideTimer);
      window.removeEventListener("mousemove", interact);
      window.removeEventListener("pointerdown", interact);
      window.removeEventListener("keydown", keyboard);
    };
  }, [identity]);

  const currentItem = active?.items[active.index];
  const nextItem = active && active.items[active.index + 1];
  const permanentSign = Boolean(manifest?.screen.signageOnly && manifest.screen.permanentPairing);
  const currentDuration = currentItem && status.itemId === currentItem.itemId
    ? status.durationMs ?? effectiveDuration(currentItem)
    : currentItem ? effectiveDuration(currentItem) : undefined;
  const currentPosition = currentItem && status.itemId === currentItem.itemId ? status.positionMs : active?.seekMs || 0;
  const currentProgress = currentDuration && currentDuration > 0
    ? Math.max(0, Math.min(100, currentPosition / currentDuration * 100))
    : 0;
  usePreload(nextItem);

  function report(next: PlaybackStatus) {
    setStatus(next);
    if (next.error) {
      errorsRef.current = [{ timestamp: new Date().toISOString(), area: "playback", message: next.error, itemId: next.itemId }, ...errorsRef.current].slice(0, 20);
    }
  }

  if (!identity) return <PairingScreen message={connectionMessage} onPaired={next => {
    localStorage.setItem(IDENTITY_KEY, JSON.stringify(next));
    setIdentity(next);
    setConnection("connecting");
    setConnectionMessage("");
  }} />;

  return <main className={`web-player ${active ? "playing" : ""} ${permanentSign ? "permanent-sign" : ""} ${new URLSearchParams(location.search).has("kiosk") ? "kiosk" : ""}`}>
    {active && currentItem ? <PlaybackStage
      key={`${currentItem.itemId}-${active.seekMs}-${unlockNonce}`}
      playlist={active.playlist}
      item={currentItem}
      paused={paused}
      seekMs={active.seekMs}
      unlockNonce={unlockNonce}
      onStatus={report}
      onEnded={finishItem}
      onBlocked={() => setAutoplayBlocked(true)}
    /> : <PlayerLibrary manifest={manifest} connection={connection} permanentSign={permanentSign}
      onPlay={playlist => startPlayback(playlist, playlist.items)} />}

    {active && controlsVisible && <>
      <div className="web-player-metadata">
        <span>{active.mode === "preroll" ? "PRE-ROLL" : active.mode === "countdown" ? "COUNTDOWN" : active.mode === "postLesson" ? "POST-LESSON" : "NOW PLAYING"}</span>
        <strong>{currentItem?.title}</strong>
        <small>{active.playlist.title} · Cue {active.index + 1} of {active.items.length}</small>
      </div>
      <div className="web-player-overlay">
        <div className="web-player-progress" aria-hidden="true"><i style={{ width: `${currentProgress}%` }} /></div>
        <div className="web-player-transport">
          <button aria-label="Exit playback" onClick={() => { setActive(undefined); setStatus(idleStatus); }}><b>‹</b><span>Back</span></button>
          <button aria-label="Previous media" onClick={() => moveTo(Math.max(0, active.index - 1))}><b>↶</b><span>Previous</span></button>
          <button aria-label="Rewind 5 seconds" disabled={currentItem?.type === "activity"} onClick={() => seekRelative(-5_000)}><b>−5</b><span>Rewind</span></button>
          <button className="primary" aria-label={paused ? "Resume" : "Pause"} disabled={currentItem?.type === "activity"} onClick={() => { setPaused(value => !value); setUnlockNonce(value => value + 1); }}><b>{paused ? "▶" : "Ⅱ"}</b><span>{paused ? "Play" : "Pause"}</span></button>
          <button aria-label="Forward 5 seconds" disabled={currentItem?.type === "activity"} onClick={() => seekRelative(5_000)}><b>+5</b><span>Forward</span></button>
          <button aria-label="Next media" onClick={() => moveTo(active.index + 1)}><b>↷</b><span>Next</span></button>
        </div>
        <time>{formatPlayerTime(currentPosition)} / {formatPlayerTime(currentDuration)}</time>
      </div>
    </>}

    {(autoplayBlocked || Boolean(active && currentItem && needsPlaybackGesture(currentItem) && !interactionUnlocked)) && <button className="autoplay-unlock" onClick={() => {
      setInteractionUnlocked(true);
      setAutoplayBlocked(false);
      setPaused(false);
      setUnlockNonce(value => value + 1);
      void document.documentElement.requestFullscreen?.().catch(() => undefined);
    }}><span>▶</span><strong>Start browser playback</strong><small>Your browser requires one click or Enter press before it can play sound. This only needs to be done after opening the player.</small></button>}

    <div className={`web-player-connection ${connection}`}>
      <i /> <span>{connectionLabel(connection)}</span>
      {connection !== "online" && connectionMessage && <small>{connectionMessage}</small>}
    </div>
    {!active && !permanentSign && <div className="web-player-actions">
      <button onClick={() => void document.documentElement.requestFullscreen?.().catch(() => undefined)}>Enter full screen</button>
      <button onClick={() => forgetPairing()}>Unpair this browser</button>
    </div>}
  </main>;
}

function PairingScreen({ message, onPaired }: { message: string; onPaired: (identity: Identity) => void }) {
  const [requestId, setRequestId] = useState("");
  const [deviceName, setDeviceName] = useState(() => `Browser display · ${browserName()}`);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(message);

  async function begin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const response = await fetch("/api/v1/pairing/request", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceName, platform: "web-player", appVersion: APP_VERSION }),
      });
      if (!response.ok) throw new Error(`Pairing request failed (${response.status}).`);
      const result = await response.json() as { requestId: string };
      setRequestId(result.requestId);
    } catch (cause) { setError(errorText(cause)); }
    finally { setBusy(false); }
  }

  async function confirmPairing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = new FormData(event.currentTarget);
    try {
      const response = await fetch("/api/v1/pairing/confirm", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ requestId, pin: values.get("pin") }),
      });
      const result = await response.json() as { screenId?: string; deviceToken?: string; error?: string };
      if (!response.ok || !result.screenId || !result.deviceToken) throw new Error(result.error || `Pairing failed (${response.status}).`);
      onPaired({ screenId: result.screenId, token: result.deviceToken, deviceName });
    } catch (cause) { setError(errorText(cause)); }
    finally { setBusy(false); }
  }

  return <main className="web-player pairing">
    <section className="web-player-card">
      <div className="web-player-brand"><img src="/lessoncue-icon.svg" alt="" aria-hidden="true" /><span><strong>LessonCue</strong><small>Browser playback client</small></span></div>
      {!requestId ? <form key="display-name" onSubmit={begin}>
        <span className="web-player-eyebrow">LOCAL DISPLAY SETUP</span>
        <h1>Pair this computer or projector</h1>
        <p>This browser becomes a secure LessonCue screen. It stays connected to this local server and can be controlled from the existing phone remote.</p>
        <label><span>Display name</span><input aria-label="Display name" value={deviceName} onChange={event => setDeviceName(event.target.value)} required maxLength={120} autoFocus /></label>
        {error && <div className="web-player-error">{error}</div>}
        <button disabled={busy}>{busy ? "Starting…" : "Start pairing"}</button>
      </form> : <form key="pairing-pin" onSubmit={confirmPairing}>
        <span className="web-player-eyebrow">PAIRING REQUESTED</span>
        <h1>Enter the server PIN</h1>
        <p>Find the six-digit pairing PIN on LessonCue’s Screens page, then enter it here.</p>
        <label><span>Six-digit pairing PIN</span><input name="pin" aria-label="Six-digit pairing PIN" inputMode="numeric" pattern="[0-9]{6}" maxLength={6} required autoFocus /></label>
        {error && <div className="web-player-error">{error}</div>}
        <button disabled={busy}>{busy ? "Pairing…" : "Pair this display"}</button>
        <button className="secondary" type="button" onClick={() => { setRequestId(""); setError(""); }}>Start over</button>
      </form>}
      <footer>Self-hosted at {location.host} · No hosted interface required</footer>
    </section>
  </main>;
}

function PlayerLibrary({ manifest, connection, permanentSign, onPlay }: {
  manifest?: Manifest;
  connection: ConnectionState;
  permanentSign: boolean;
  onPlay: (playlist: Playlist) => void;
}) {
  const signage = manifest?.signage[0];
  const emergency = signage?.mode === "emergency";
  usePreload(signage?.media);
  useSignagePreload(manifest?.signageSchedule);
  const signageMedia = signage?.media;
  const signageImage = signageMedia?.type === "image" || signageMedia?.contentType?.startsWith("image/");
  if (signage?.displayPower === "off") return <div className="signage-display-off" aria-label="Display power scheduled off" />;
  const signageContent = signage ? <SignageExperience signage={signage}>{signage.zones?.length ? <SignageLayout signage={signage} /> : <section className="web-player-signage">
    {signageMedia?.downloadUrl ? (signageImage
      ? <img src={signageMedia.downloadUrl} alt="" />
      : signageMedia.type === "video" || signageMedia.contentType?.startsWith("video/")
        ? <video src={signageMedia.downloadUrl} autoPlay muted loop playsInline preload="auto" aria-label={signageMedia.title} />
        : null)
      : signageMedia?.streamingFallbackUrl
        ? <iframe src={signageMedia.streamingFallbackUrl} title={signageMedia.title || "YouTube video"} allow="autoplay; encrypted-media" />
        : null}
    {!signageMedia && signage.mediaUrl && <img src={signage.mediaUrl} alt="" />}
    <div><small>{signage.name}</small><h1>{signage.message}</h1></div>
  </section>}</SignageExperience> : null;
  if (permanentSign) return <div className="web-player-permanent-sign" data-display-mode="permanent-sign"
    style={signage ? { backgroundColor: signage.backgroundColor, color: signage.textColor } : undefined}>
    {signageContent || <section className="web-player-sign-empty" aria-live="polite">
      <strong>{connection === "online" ? "Waiting for published signage" : "Connecting to LessonCue…"}</strong>
      <span>Assign an active layout and playlist to this permanent sign.</span>
    </section>}
  </div>;
  return <div className="web-player-library" style={signage ? { backgroundColor: signage.backgroundColor, color: signage.textColor } : undefined}>
    <header><div className="web-player-brand"><img src="/lessoncue-icon.svg" alt="" aria-hidden="true" /><span><strong>{manifest?.screen.name || "LessonCue"}</strong><small>{manifest?.screen.site || "Browser display"}</small></span></div><span className="web-player-eyebrow">READY FOR PLAYBACK</span></header>
    {signageContent || <section className="web-player-ready">
      <span>✓</span><h1>{connection === "online" ? "Ready for a lesson" : "Connecting to LessonCue…"}</h1>
      <p>Use the phone controller, select a lesson below, or wait for scheduled pre-roll and countdown media.</p>
    </section>}
    <section className="web-player-lessons" aria-label="Available lessons">
      {emergency ? <p className="web-player-empty">Emergency signage is active. Lesson controls return automatically when the override ends.</p> : manifest?.playlists.map(playlist => <button key={playlist.playlistId} onClick={() => onPlay(playlist)} disabled={!playlist.items.length}>
        <time>{formatLessonDate(playlist.lessonDate)}</time>
        <span><strong>{playlist.title}</strong><small>{playlist.items.length} lesson item{playlist.items.length === 1 ? "" : "s"}{playlist.designatedStartAt ? ` · starts ${new Date(playlist.designatedStartAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}` : ""}</small></span>
        <b>{playlist.items.length ? "▶" : "—"}</b>
      </button>)}
      {manifest && !manifest.playlists.length && <p className="web-player-empty">No lessons are currently assigned to this screen.</p>}
    </section>
    <footer>Keyboard: Space play/pause · ←/→ previous/next · Esc stop · F full screen</footer>
  </div>;
}

function SignageExperience({ signage, children }: { signage: Signage; children: ReactNode }) {
  const playlistItems = signage.contentPlaylist?.items;
  const items = useMemo(() => playlistItems?.filter(item => !item.hidden) || [], [playlistItems]);
  const [index, setIndex] = useState(0);
  const [interacting, setInteracting] = useState(false);
  const timeoutRef = useRef<number>(0);
  useEffect(() => {
    let nextIndex = 0;
    if (signage.contentPlaylist?.synchronization !== "screen" && items.length) {
      const cycle = items.reduce((total, item) => total + Math.max(1, item.durationSeconds || 10), 0);
      let offset = Math.floor(Date.now() / 1000) % Math.max(1, cycle);
      const synchronized = items.findIndex(item => { offset -= Math.max(1, item.durationSeconds || 10); return offset < 0; });
      nextIndex = Math.max(0, synchronized);
    }
    const timer = window.setTimeout(() => setIndex(nextIndex), 0);
    return () => window.clearTimeout(timer);
  }, [signage.contentPlaylist?.id, signage.contentPlaylist?.version, signage.contentPlaylist?.synchronization, items]);
  useEffect(() => {
    if (!items.length || interacting) return;
    const current = items[index % items.length];
    const timer = window.setTimeout(() => setIndex(value => value + 1), Math.max(1, current.durationSeconds || 10) * 1000);
    return () => window.clearTimeout(timer);
  }, [items, index, interacting]);
  const current = items.length ? items[index % items.length] : undefined;
  const backgroundAudio = current?.layout?.backgroundAudio || signage.backgroundAudio;
  function beginInteraction() {
    if (!signage.kiosk?.enabled || !signage.kiosk.interactionUrl) return;
    setInteracting(true); resetTimeout();
  }
  function resetTimeout() {
    window.clearTimeout(timeoutRef.current);
    timeoutRef.current = window.setTimeout(() => setInteracting(false), Math.max(5, signage.kiosk?.timeoutSeconds || 60) * 1000);
  }
  let content = children;
  if (current?.layout) content = <SignageLayout signage={{ ...signage, name: current.layout.name, backgroundColor: current.layout.backgroundColor, zones: current.layout.zones, backgroundAudio: current.layout.backgroundAudio }} />;
  else if (current?.media?.downloadUrl) content = <section className={`web-player-signage playlist-entry ${current.transition || "cut"}`}><SignagePlaylistMedia item={current} /></section>;
  else if (current?.sourceUrl) content = <section className="web-player-signage playlist-entry"><iframe src={current.sourceUrl} title={current.title || current.kind} /></section>;
  return <div className="signage-experience" onPointerDown={beginInteraction}>
    {backgroundAudio?.downloadUrl && <SignageAudio source={backgroundAudio.downloadUrl} volume={signage.volumePercent ?? 100} />}
    {current?.transparent && <div className="signage-transparent-base">{children}</div>}
    {content}
    {signage.kiosk?.enabled && signage.kiosk.showTouchIndicator && !interacting && <button className="kiosk-touch-indicator" onClick={beginInteraction}>Touch to explore</button>}
    {interacting && signage.kiosk?.interactionUrl && <div className="kiosk-overlay" onPointerDown={resetTimeout}>
      {signage.kiosk.showCloseButton && <button onClick={() => setInteracting(false)}>Close</button>}
      <iframe src={signage.kiosk.interactionUrl} title="Interactive kiosk content" sandbox="allow-forms allow-modals allow-popups allow-same-origin allow-scripts" />
      {signage.kiosk.virtualKeyboard && <span className="kiosk-keyboard-hint">Tap a text field to use the on-screen keyboard.</span>}
    </div>}
  </div>;
}

function SignageAudio({ source, volume }: { source: string; volume: number }) {
  const ref = useRef<HTMLAudioElement>(null);
  useEffect(() => { if (ref.current) ref.current.volume = Math.max(0, Math.min(1, volume / 100)); }, [volume]);
  return <audio ref={ref} src={source} autoPlay loop />;
}

function SignagePlaylistMedia({ item, zoneFit }: {
  item: SignagePlaylistEntry;
  zoneFit?: "contain" | "cover" | "fill";
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [phase, setPhase] = useState<"in" | "show" | "out">("in");
  const fadeInMs = Math.max(0, item.fadeInMs || 0);
  const fadeOutMs = Math.max(0, item.fadeOutMs || 0);
  useEffect(() => {
    // Reset the visual envelope when the playlist advances to a new item.
    setPhase("in");
    const reveal = window.requestAnimationFrame(() => setPhase("show"));
    const outAt = Math.max(fadeInMs, Math.max(1, item.durationSeconds || 10) * 1000 - fadeOutMs);
    const hide = fadeOutMs > 0 ? window.setTimeout(() => setPhase("out"), outAt) : 0;
    return () => {
      window.cancelAnimationFrame(reveal);
      if (hide) window.clearTimeout(hide);
    };
  }, [item.id, item.durationSeconds, fadeInMs, fadeOutMs]);
  useEffect(() => {
    if (!videoRef.current) return;
    videoRef.current.muted = !!item.muted;
    videoRef.current.volume = Math.max(0, Math.min(1, (item.volumePercent ?? 100) / 100));
  }, [item.muted, item.volumePercent]);
  const style = {
    objectFit: zoneFit || item.fit || "contain",
    opacity: phase === "show" ? 1 : 0,
    transitionProperty: "opacity",
    transitionTimingFunction: "linear",
    transitionDuration: `${phase === "out" ? fadeOutMs : fadeInMs}ms`
  } as CSSProperties;
  if (!item.media) return null;
  if (!item.media.downloadUrl && item.media.streamingFallbackUrl)
    return <iframe src={item.media.streamingFallbackUrl} title={item.title || "YouTube video"} allow="autoplay; encrypted-media" style={style} />;
  return item.media.type === "video" || item.media.contentType?.startsWith("video/")
    ? <video ref={videoRef} src={item.media.downloadUrl} autoPlay muted={!!item.muted} loop playsInline preload="auto" style={style} />
    : <img src={item.media.downloadUrl} alt={item.title || ""} style={style} />;
}

export type SignageEditorOptions = {
  selectedZoneId?: string;
  onSelect?: (id: string) => void;
  onMediaTransform?: (id: string, patch: { mediaOffsetX: number; mediaOffsetY: number }) => void;
};

export function SignageLayout({ signage, editor }: { signage: Signage; editor?: SignageEditorOptions }) {
  const dragRef = useRef<{ id: string; x: number; y: number; offsetX: number; offsetY: number; width: number; height: number } | undefined>(undefined);
  function beginMediaDrag(event: ReactPointerEvent<HTMLElement>, zone: SignageZone) {
    if (!editor?.onMediaTransform || zone.type !== "media") return;
    event.preventDefault();
    event.stopPropagation();
    const bounds = event.currentTarget.getBoundingClientRect();
    dragRef.current = { id: zone.id, x: event.clientX, y: event.clientY, offsetX: zone.mediaOffsetX || 0, offsetY: zone.mediaOffsetY || 0, width: bounds.width, height: bounds.height };
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function moveMedia(event: ReactPointerEvent<HTMLElement>) {
    const drag = dragRef.current;
    if (!drag || !editor?.onMediaTransform) return;
    editor.onMediaTransform(drag.id, {
      mediaOffsetX: Math.max(-150, Math.min(150, drag.offsetX + (event.clientX - drag.x) / Math.max(1, drag.width) * 100)),
      mediaOffsetY: Math.max(-150, Math.min(150, drag.offsetY + (event.clientY - drag.y) / Math.max(1, drag.height) * 100))
    });
  }
  function endMediaDrag() { dragRef.current = undefined; }
  return <section className={`web-player-signage-layout ${signage.layoutPreset || "single"}`} aria-label={`${signage.name} signage layout`}>
    {signage.zones?.filter(zone => !zone.hidden).map(zone => <article
      className={`web-player-signage-zone ${zone.type} align-${zone.verticalAlign || "middle"} ${zone.mediaAllowOverflow ? "allow-media-overflow" : ""} ${editor?.selectedZoneId === zone.id ? "editor-selected" : ""}`}
      key={zone.id}
      role={editor ? "button" : undefined}
      tabIndex={editor ? 0 : undefined}
      onClick={() => editor?.onSelect?.(zone.id)}
      onPointerDown={event => beginMediaDrag(event, zone)}
      onPointerMove={moveMedia}
      onPointerUp={endMediaDrag}
      onPointerCancel={endMediaDrag}
      style={{ left: `${zone.x}%`, top: `${zone.y}%`, width: `${zone.width}%`, height: `${zone.height}%`, backgroundColor: zone.backgroundColor, color: zone.textColor, borderColor: zone.accentColor, borderRadius: `${zone.cornerRadius || 0}%`, zIndex: zone.zIndex ?? 0, opacity: (zone.opacity ?? 100) / 100, transform: `rotate(${zone.rotation ?? 0}deg) scaleX(${zone.flipX ? -1 : 1}) scaleY(${zone.flipY ? -1 : 1})`, fontFamily: zone.fontFamily, fontWeight: zone.fontWeight, fontStyle: zone.italic ? "italic" : undefined, textDecoration: zone.underline ? "underline" : undefined, textAlign: zone.textAlign, ["--signage-accent" as string]: zone.accentColor, ["--signage-line-height" as string]: `${Math.max(80, Math.min(300, zone.lineHeightPercent ?? 120)) / 100}`, ["--signage-zone-padding" as string]: `${Math.max(0, Math.min(30, zone.contentPadding ?? 6))}%`, ["--signage-content-scale" as string]: Math.max(.25, Math.min(1, (zone.contentScale ?? 100) / 100)), ["--signage-qr-size" as string]: `${Math.max(20, Math.min(90, zone.qrSizePercent ?? 42))}%` } as CSSProperties}>
      {zone.media?.downloadUrl ? (zone.media.type === "video" || zone.media.contentType?.startsWith("video/")
        ? <video src={zone.media.downloadUrl} autoPlay muted loop playsInline preload="auto" aria-label={zone.media.title} style={{ objectFit: zone.fit || "cover", transform: mediaTransform(zone) }} />
        : <img src={zone.media.downloadUrl} alt={zone.title || ""} style={{ objectFit: zone.fit || "cover", transform: mediaTransform(zone) }} />)
        : zone.media?.streamingFallbackUrl
          ? <iframe src={zone.media.streamingFallbackUrl} title={zone.title || "YouTube video"} allow="autoplay; encrypted-media" style={{ transform: mediaTransform(zone) }} />
          : null}
      {zone.type === "stream" && zone.streamUrl && <SignageStream source={zone.streamUrl} title={zone.title || "Live stream"} fit={zone.fit || "cover"} />}
      {zone.type === "presentation" && <SignagePresentation key={`${zone.contentPlaylist?.id || "none"}:${zone.contentPlaylist?.version || 0}`} zone={zone} signage={signage} />}
      {zone.type === "webpage" && zone.sourceUrl && <iframe src={zone.sourceUrl} title={zone.title || "Web page"} sandbox="allow-forms allow-modals allow-popups allow-presentation allow-same-origin allow-scripts" />}
      {zone.type === "customHtml" && (zone.htmlUrl
        ? <iframe src={zone.htmlUrl} title={zone.title || "Custom HTML"} sandbox="allow-forms allow-modals allow-popups allow-presentation allow-scripts" />
        : zone.content && <iframe srcDoc={zone.content} title={zone.title || "Custom HTML"} sandbox="allow-forms allow-modals allow-popups allow-presentation allow-scripts" />)}
      {(zone.type === "qr" || zone.type === "wifi") && <SignageQr zone={zone} />}
      {zone.type === "audience" && <SignageAudiencePoll zone={zone} />}
      <div className={`web-player-zone-copy ${zone.type === "ticker" ? "ticker" : ""}`} style={{ fontSize: relativeSignageFontSize(zone), ...(zone.type === "ticker" ? { animationDuration: `${Math.max(5, 300 / Math.max(10, zone.tickerSpeed || 60))}s` } : {}) }}>
        {zone.title && !["qr","wifi","audience","webpage","customHtml","presentation","media","weather","calendar"].includes(zone.type) && <small style={{ color: zone.accentColor }}>{zone.title}</small>}
        {zone.type === "clock" ? <SignageClock zone={zone} />
          : zone.type === "weather" ? <SignageWeather zone={zone} />
          : zone.type === "calendar" ? <SignageCalendar zone={zone} />
          : zone.type === "counter" ? <SignageCounter zone={zone} />
          : zone.richTextJson ? <SignageRichText zone={zone} value={zone.richTextJson} fallback={zone.cached?.text || zone.content || ""} />
          : !["qr","wifi","audience","webpage","customHtml","presentation","stream","media","weather","calendar"].includes(zone.type) ? <><strong>{zone.cached?.text || zone.content}</strong>{zone.cached?.items?.length ? <ul>{zone.cached.items.map((item, index) => <li key={`${zone.id}-${index}`}>{item}</li>)}</ul> : null}</>
          : null}
      </div>
    </article>)}
  </section>;
}

function mediaTransform(zone: SignageZone) {
  return `translate(${zone.mediaOffsetX || 0}%, ${zone.mediaOffsetY || 0}%) scale(${Math.max(25, Math.min(400, zone.mediaScale || 100)) / 100})`;
}

function relativeSignageFontSize(zone: Pick<SignageZone, "fontScalePercent">) {
  const scale = Math.max(1, Math.min(40, zone.fontScalePercent ?? 10));
  return `min(${scale}cqw, ${scale}cqh)`;
}

function SignagePresentation({ zone, signage }: { zone: SignageZone; signage: Signage }) {
  const playlistItems = zone.contentPlaylist?.items;
  const items = useMemo(() => playlistItems?.filter(item => !item.hidden) || [], [playlistItems]);
  const [index, setIndex] = useState(0);
  const [streamLive, setStreamLive] = useState(false);
  const streamOverrideActive = useScheduledStreamOverride(zone);
  useEffect(() => {
    if (!items.length || (streamOverrideActive && streamLive)) return;
    const current = items[index % items.length];
    const timer = window.setTimeout(() => setIndex(value => value + 1), Math.max(1, current.durationSeconds || 10) * 1000);
    return () => window.clearTimeout(timer);
  }, [items, index, streamOverrideActive, streamLive]);
  const current = items.length ? items[index % items.length] : undefined;
  let content: ReactNode = <div className="signage-presentation-empty">Select a published playlist</div>;
  if (current?.layout) content = <SignageLayout signage={{ ...signage, name: current.layout.name, backgroundColor: current.layout.backgroundColor, zones: current.layout.zones, backgroundAudio: current.layout.backgroundAudio }} />;
  else if (current?.media?.downloadUrl || current?.media?.streamingFallbackUrl) content = <SignagePlaylistMedia item={current} zoneFit={zone.fit || "contain"} />;
  else if (current?.sourceUrl) content = <iframe src={current.sourceUrl} title={current.title || current.kind} sandbox="allow-forms allow-modals allow-popups allow-presentation allow-same-origin allow-scripts" />;
  return <div className="signage-presentation">
    <div className="signage-presentation-default" aria-hidden={streamOverrideActive && streamLive}>{content}</div>
    {streamOverrideActive && zone.streamUrl && <div className={`signage-presentation-stream ${streamLive ? "is-live" : ""}`}>
      <SignageStream source={zone.streamUrl} title={zone.title || "Live stream"} fit={zone.fit || "cover"} onAvailabilityChange={setStreamLive} />
    </div>}
  </div>;
}

function useScheduledStreamOverride(zone: SignageZone) {
  const [now, setNow] = useState(() => Date.now());
  const startsAt = zone.streamOverrideStartsAt ? Date.parse(zone.streamOverrideStartsAt) : Number.NEGATIVE_INFINITY;
  const endsAt = zone.streamOverrideEndsAt ? Date.parse(zone.streamOverrideEndsAt) : Number.POSITIVE_INFINITY;
  useEffect(() => {
    const next = [startsAt, endsAt].filter(value => Number.isFinite(value) && value > now).sort((a, b) => a - b)[0];
    if (!next) return;
    const timer = window.setTimeout(() => setNow(Date.now()), Math.max(1, next - now));
    return () => window.clearTimeout(timer);
  }, [startsAt, endsAt, now]);
  return Boolean(zone.streamOverrideWhenLive && zone.streamUrl && now >= startsAt && now < endsAt);
}

function SignageRichText({ zone, value, fallback, className }: { zone: SignageZone; value: string; fallback: string; className?: string }) {
  let runs: { text?: string; bold?: boolean; italic?: boolean; underline?: boolean; color?: string; fontFamily?: string; fontSize?: number }[] = [];
  try {
    const parsed = JSON.parse(value);
    if (Array.isArray(parsed)) runs = parsed;
  } catch { /* The plain text fallback remains visible for malformed manifest data. */ }
  if (!runs.length) return <strong className={className}>{fallback}</strong>;
  const legacyBase = Math.max(12, Math.min(160, zone.fontSize ?? 34)) * 2.1;
  return <strong className={className}>{runs.slice(0,200).map((run,index)=><span key={index} style={{fontWeight:run.bold?800:undefined,fontStyle:run.italic?"italic":undefined,textDecoration:run.underline?"underline":undefined,color:/^#[0-9a-f]{6}$/i.test(run.color||"")?run.color:undefined,fontFamily:String(run.fontFamily||"").slice(0,80)||undefined,fontSize:Number.isFinite(run.fontSize)?`${Math.max(.25,Math.min(4,Number(run.fontSize) / legacyBase))}em`:undefined}}>{String(run.text||"")}</span>)}</strong>;
}

function SignageQr({ zone }: { zone: SignageZone }) {
  const value = zone.qrValue || zone.content || "";
  return value ? <div className={`signage-qr-layout placement-${zone.qrPlacement || "center"}`} style={{ fontSize: relativeSignageFontSize(zone), lineHeight: "var(--signage-line-height, 1.2)" }}>
    <span className="signage-qr-label top">{zone.qrLabelTop}</span>
    <span className="signage-qr-label left">{zone.qrLabelLeft}</span>
    <GeneratedSignageQr value={value} />
    <span className="signage-qr-label right">{zone.qrLabelRight}</span>
    <span className="signage-qr-label bottom">{zone.qrLabelBottom}</span>
  </div> : null;
}

function GeneratedSignageQr({ value }: { value: string }) {
  const [source, setSource] = useState("");
  useEffect(() => { let current = true; void QRCode.toDataURL(value, { width: 480, margin: 1 }).then(url => { if (current) setSource(url); }); return () => { current = false; }; }, [value]);
  return source ? <img className="signage-qr" src={source} alt={`QR code for ${value}`} /> : null;
}

type SignageAudienceSession = {
  code: string;
  title: string;
  status: "draft" | "open" | "closed";
  showLiveResults: boolean;
  questions: { id: string; prompt: string; type: "single" | "multiple" | "text"; options: string[] }[];
  results?: {
    participantCount: number;
    questions: { id: string; prompt: string; type: string; counts: { option: string; count: number }[] }[];
  };
};

function SignageAudiencePoll({ zone }: { zone: SignageZone }) {
  return <AudiencePollDisplay
    code={zone.audienceCode || ""}
    title={zone.title}
    instructions={zone.content}
    showResults={zone.audienceShowResults !== false}
    resultDelaySeconds={zone.audienceResultDelaySeconds || 0}
    placement={zone.qrPlacement as "left" | "center" | "right" | undefined}
  />;
}

export function AudiencePollDisplay({
  code: rawCode,
  title,
  instructions,
  showResults = true,
  resultDelaySeconds = 0,
  placement = "left",
}: {
  code: string;
  title?: string;
  instructions?: string;
  showResults?: boolean;
  resultDelaySeconds?: number;
  placement?: "left" | "center" | "right";
}) {
  const code = String(rawCode || "").trim().toUpperCase();
  const [session, setSession] = useState<SignageAudienceSession>();
  const [displayedResults, setDisplayedResults] =
    useState<SignageAudienceSession["results"]>();
  const [error, setError] = useState("");
  useEffect(() => {
    if (!code) {
      setSession(undefined);
      setDisplayedResults(undefined);
      setError("");
      return;
    }
    let active = true;
    let timer = 0;
    const resultTimers: number[] = [];
    const delayMs = Math.min(3600, Math.max(0, resultDelaySeconds)) * 1000;
    if (!showResults || delayMs > 0) setDisplayedResults(undefined);
    async function refresh() {
      try {
        const response = await fetch(`/api/v1/audience/join/${encodeURIComponent(code)}`, { credentials: "same-origin" });
        const body = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(body.error || `Unable to load poll (${response.status}).`);
        if (active) {
          const next = body as SignageAudienceSession;
          setSession(next);
          if (!showResults) setDisplayedResults(undefined);
          else if (delayMs === 0) setDisplayedResults(next.results);
          else {
            resultTimers.push(window.setTimeout(() => {
              if (active) setDisplayedResults(next.results);
            }, delayMs));
          }
          setError("");
        }
      } catch (problem) {
        if (active) setError(problem instanceof Error ? problem.message : "Unable to load poll.");
      } finally {
        if (active) timer = window.setTimeout(refresh, 10_000);
      }
    }
    void refresh();
    return () => {
      active = false;
      window.clearTimeout(timer);
      resultTimers.forEach((id) => window.clearTimeout(id));
    };
  }, [code, showResults, resultDelaySeconds]);
  if (!code) return <div className="signage-audience-empty">Choose an audience poll</div>;
  const responseUrl = `${location.origin}/respond/${code}`;
  const question = session?.questions[0];
  const result = question
    ? displayedResults?.questions.find(item => item.id === question.id)
    : displayedResults?.questions[0];
  const prompt = question?.prompt || result?.prompt;
  const total = Math.max(1, result?.counts.reduce((sum, item) => sum + item.count, 0) || 0);
  return <div className={`signage-audience layout-${placement}`}>
    <div className="signage-audience-qr">
      <GeneratedSignageQr value={responseUrl} />
      <b>{code}</b>
    </div>
    <div className="signage-audience-copy">
      <strong>{title || session?.title || "Audience poll"}</strong>
      <span className={`signage-audience-status ${session?.status || "loading"}`}>
        {session?.status === "open" ? "Voting open" : session?.status === "closed" ? "Voting closed" : session?.status === "draft" ? "Not open yet" : "Loading poll"}
      </span>
      <p>{instructions || "Scan the QR code to vote."}</p>
      {prompt && <h4>{prompt}</h4>}
      {showResults && result?.counts?.length
        ? <div className="signage-audience-results">{result.counts.slice(0, 8).map(item => <div key={item.option}>
            <span><b>{item.option}</b><em>{item.count}</em></span>
            <i><b style={{ width: `${item.count / total * 100}%` }} /></i>
          </div>)}</div>
        : question?.options?.length ? <ul>{question.options.slice(0, 8).map(option => <li key={option}>{option}</li>)}</ul> : null}
      {showResults && displayedResults && <small>{displayedResults.participantCount} response{displayedResults.participantCount === 1 ? "" : "s"}</small>}
      {error && <small className="signage-audience-error">{error}</small>}
    </div>
  </div>;
}

function SignageCounter({ zone }: { zone: SignageZone }) {
  const [now, setNow] = useState<number | null>(null);
  useEffect(() => {
    const first = window.requestAnimationFrame(() => setNow(Date.now()));
    const timer = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => { window.cancelAnimationFrame(first); window.clearInterval(timer); };
  }, []);
  if (!zone.counterTargetAt || now == null) return <strong>{zone.content || "Countdown"}</strong>;
  const target = nextCounterTarget(zone.counterTargetAt, zone.counterRepeatWeekly, now);
  const seconds = Math.max(0, Math.floor((target - now) / 1000));
  const days = Math.floor(seconds / 86400), hours = Math.floor(seconds % 86400 / 3600), minutes = Math.floor(seconds % 3600 / 60);
  const countdown = `${days > 0 ? `${days}d ` : ""}${String(hours).padStart(2,"0")}:${String(minutes).padStart(2,"0")}:${String(seconds % 60).padStart(2,"0")}`;
  return <strong>{zone.content ? zone.content.replaceAll("[countdown]", countdown) : countdown}</strong>;
}

function nextCounterTarget(target: string, weekly: boolean | undefined, now: number) {
  const parsed = new Date(target).getTime();
  if (!weekly || !Number.isFinite(parsed) || parsed > now) return parsed;
  const week = 7 * 24 * 60 * 60 * 1000;
  return parsed + Math.max(1, Math.ceil((now - parsed) / week)) * week;
}

function SignageStream({ source, title, fit, onAvailabilityChange }: { source: string; title: string; fit: "cover" | "contain" | "fill"; onAvailabilityChange?: (available: boolean) => void }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    onAvailabilityChange?.(false);
    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = source;
      void video.play().catch(() => undefined);
      return () => { onAvailabilityChange?.(false); video.pause(); video.removeAttribute("src"); video.load(); };
    }
    let disposed = false;
    let destroy: (() => void) | undefined;
    void import("hls.js").then(({ default: Hls }) => {
      if (disposed || !Hls.isSupported()) return;
      const hls = new Hls({ manifestLoadingMaxRetry: 20, manifestLoadingRetryDelay: 1_000, levelLoadingMaxRetry: 20, fragLoadingMaxRetry: 20, lowLatencyMode: true });
      destroy = () => hls.destroy();
      hls.loadSource(source);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, () => void video.play().catch(() => undefined));
      hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) onAvailabilityChange?.(false); });
    });
    return () => { disposed = true; onAvailabilityChange?.(false); destroy?.(); };
  }, [source, onAvailabilityChange]);
  return <video ref={videoRef} autoPlay muted playsInline aria-label={title} style={{ objectFit: fit }} onPlaying={() => onAvailabilityChange?.(true)} onCanPlay={() => onAvailabilityChange?.(true)} onError={() => onAvailabilityChange?.(false)} onStalled={() => onAvailabilityChange?.(false)} />;
}

function SignageClock({ zone }: { zone: SignageZone }) {
  const [now, setNow] = useState(new Date());
  useEffect(() => { const timer = window.setInterval(() => setNow(new Date()), 1000); return () => window.clearInterval(timer); }, []);
  const display = zone.clockDisplay || "both";
  const timeOptions: Intl.DateTimeFormatOptions = {
    hour: "numeric", minute: "2-digit", second: zone.clockTimeFormat?.endsWith("seconds") ? "2-digit" : undefined,
    hour12: !zone.clockTimeFormat?.startsWith("24h")
  };
  let timeText = now.toLocaleTimeString([], timeOptions);
  if (timeOptions.hour12 && zone.clockShowPeriod === false) {
    timeText = new Intl.DateTimeFormat([], timeOptions).formatToParts(now).filter(part => part.type !== "dayPeriod").map(part => part.value).join("").trim();
  }
  const time = <b className="signage-clock-time">{timeText}</b>;
  const year = zone.clockShowYear === false ? undefined : "numeric";
  const weekday = zone.clockShowWeekday === false ? undefined : zone.clockDateFormat === "medium" ? "short" : "long";
  const dateOptions: Intl.DateTimeFormatOptions = zone.clockDateFormat === "numeric" ? { year, month: "2-digit", day: "2-digit" }
    : zone.clockDateFormat === "short" ? { month: "short", day: "numeric" }
    : zone.clockDateFormat === "medium" ? { weekday, month: "short", day: "numeric", year }
    : { weekday, month: "long", day: "numeric", year };
  const date = <span className="signage-clock-date">{now.toLocaleDateString([], dateOptions)}</span>;
  if (display === "time") return time;
  if (display === "date") return date;
  return <div className={`signage-clock-stack ${zone.clockOrder === "date-time" ? "date-first" : ""} ${zone.clockOrder === "inline" ? "inline" : ""}`}>{time}{date}</div>;
}

function SignageWeather({ zone }: { zone: SignageZone }) {
  const cache = zone.cached;
  const weather = cache?.weather;
  const weatherFieldKeys = ["icon", "temperature", "conditions", "precipitation", "high", "low", "wind"];
  const fields = new Set((zone.weatherFields || weatherFieldKeys.join(",")).split(",")
    .map(value => value.trim()).filter(value => weatherFieldKeys.includes(value)));
  const icon = (cache?.icon || "☀").replace(/\uFE0F/g, "");
  const degreeValue = (value?: number) => value == null ? "—" : `${value.toFixed(0)}°`;
  const highLow = fields.has("high") || fields.has("low")
    ? `H${fields.has("high") ? degreeValue(weather?.high).replace("°", "") : "—"}/L${fields.has("low") ? degreeValue(weather?.low).replace("°", "") : "—"}`
    : "";
  const windText = weather?.windText || "";
  const windDirection = windText.match(/\b(NNW|NNE|ENE|ESE|SSE|SSW|WSW|WNW|NE|NW|SE|SW|N|E|S|W)\b/i)?.[1]?.toUpperCase();
  const windSpeed = weather?.wind != null
    ? `${weather.wind.toFixed(0)} ${(weather.windUnit || "mph").toUpperCase()}`
    : windText.match(/\d+(?:\.\d+)?\s*[a-z]+/i)?.[0]?.toUpperCase() || windText;
  const details = [
    fields.has("precipitation") && weather?.precipitation != null ? { kind: "precipitation", value: `${weather.precipitation.toFixed(0)}%` } : null,
    highLow ? { kind: "high-low", value: highLow } : null,
    fields.has("wind") && windSpeed ? { kind: "wind", value: windSpeed, direction: windDirection } : null,
  ].filter((item): item is { kind: string; value: string; direction?: string } => Boolean(item));
  const fallbackText = (cache?.text || zone.content || "Weather").replace(icon, "").trim();
  const title = zone.title || cache?.title || "";
  const hasDetails = details.length > 0 || Boolean(!weather && cache?.items?.length);
  return <div className={`signage-weather layout-${zone.weatherLayout || "icon-left"} ${title ? "has-title" : "no-title"} ${hasDetails ? "has-details" : "no-details"}`}>
    <div className="signage-weather-heading">
      {title && (zone.richTextJson
        ? <SignageRichText zone={zone} value={zone.richTextJson} fallback={title} className="signage-weather-title" />
        : <b className="signage-weather-title">{title}</b>)}
    </div>
    <div className="signage-weather-main">
      {fields.has("icon") && <WeatherConditionArtwork className="signage-weather-icon" conditions={weather?.conditions} icon={icon} monochrome={zone.weatherIconStyle === "white"} />}
      <div className="signage-weather-reading">
        {fields.has("temperature") && weather?.temperature != null
          ? <strong className="signage-weather-temperature">{degreeValue(weather.temperature)}</strong>
          : !weather && <strong className="signage-weather-temperature">{fallbackText || "Weather"}</strong>}
        {fields.has("conditions") && weather?.conditions && <span className="signage-weather-conditions">{weather.conditions.toUpperCase()}</span>}
      </div>
    </div>
    {details.length > 0
      ? <ul className="signage-weather-details">{details.map((item, index) => <li className={`signage-weather-${item.kind}`} key={`${zone.id}-weather-${index}`}>
        {item.kind === "precipitation" && <WeatherDropArtwork className="signage-weather-drop" />}
        {item.kind === "wind" && <WeatherWindArtwork className="signage-weather-wind-icon" />}
        <span className="signage-weather-detail-value">{item.value}</span>
        {item.kind === "wind" && item.direction && <small className="signage-weather-wind-direction">{item.direction}</small>}
      </li>)}</ul>
      : !weather && cache?.items?.length ? <ul className="signage-weather-details">{cache.items.map((item, index) => <li key={`${zone.id}-weather-${index}`}>{item}</li>)}</ul> : null}
  </div>;
}

function SignageCalendar({ zone }: { zone: SignageZone }) {
  const fields = new Set((zone.calendarFields || "date,time,title").split(",").map(value => value.trim().toLowerCase()).filter(Boolean));
  const allEvents = zone.cached?.events || [];
  const limit = zone.calendarMaxItems && zone.calendarMaxItems > 0 ? zone.calendarMaxItems : 4;
  const events = allEvents.slice(0, limit);
  if (!events.length) {
    const items = zone.cached?.items || [];
    const visible = items.slice(0, limit);
    return <div className="signage-calendar">{zone.richTextJson
      ? <SignageRichText zone={zone} value={zone.richTextJson} fallback={zone.title || "Upcoming events"} className="signage-calendar-heading" />
      : <b className="signage-calendar-heading">{zone.title || "Upcoming events"}</b>}
      {visible.length
        ? <ul className="signage-calendar-list">{visible.map((item, index) => <li key={`${zone.id}-calendar-${index}`}><b>{item}</b></li>)}</ul>
        : <strong>{zone.content || "Calendar feed"}</strong>}
    </div>;
  }
  return <div className="signage-calendar">{zone.richTextJson
    ? <SignageRichText zone={zone} value={zone.richTextJson} fallback={zone.title || "Upcoming events"} className="signage-calendar-heading" />
    : <b className="signage-calendar-heading">{zone.title || "Upcoming events"}</b>}<ol className="signage-calendar-list">
    {events.map((event, index) => {
      const starts = event.startsAt ? new Date(event.startsAt) : undefined;
      const ends = event.endsAt ? new Date(event.endsAt) : undefined;
      const dateText = starts && fields.has("date")
        ? starts.toLocaleDateString([], { month: "long", day: "numeric" })
        : "";
      const timeText = starts && fields.has("time") && !event.allDay
        ? `${starts.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}${ends && ends.toDateString() === starts.toDateString() ? ` - ${ends.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}` : ""}`
        : "";
      return <li key={`${zone.id}-event-${index}`}>
        {fields.has("title") && <b style={{ color: zone.accentColor }}>{event.title}</b>}
        {(dateText || timeText) && <time dateTime={event.startsAt}>
          {dateText && <span>{dateText}</span>}
          {timeText && <span>{timeText}</span>}
        </time>}
        {fields.has("description") && event.description && <p className="signage-calendar-description">{event.description}</p>}
        {fields.has("location") && event.location && <small className="signage-calendar-location">{event.location}</small>}
      </li>;
    })}
  </ol></div>;
}

function PlaybackStage({ playlist, item, paused, seekMs, unlockNonce, onStatus, onEnded, onBlocked }: {
  playlist: Playlist;
  item: CueItem;
  paused: boolean;
  seekMs: number;
  unlockNonce: number;
  onStatus: (status: PlaybackStatus) => void;
  onEnded: () => void;
  onBlocked: () => void;
}) {
  const mediaRef = useRef<HTMLMediaElement>(null);
  const frameRef = useRef<HTMLIFrameElement>(null);
  const [opacity, setOpacity] = useState(item.fadeInMs > 0 ? 0 : 1);
  const [imagePosition, setImagePosition] = useState(seekMs);
  const [useStreamingFallback, setUseStreamingFallback] = useState(false);
  const fallback = item.renderSupport === "fallback";
  const localYouTubeFallback = Boolean(item.streamingFallbackUrl && item.downloadUrl);
  const noLocalYouTubeFallback = Boolean(item.streamingFallbackUrl && !item.downloadUrl);
  const onlineUrl = useStreamingFallback || noLocalYouTubeFallback
    ? item.streamingFallbackUrl
    : item.playbackUrl;
  const online = !fallback && Boolean(onlineUrl);
  const image = !fallback && (item.type === "image" || item.contentType?.startsWith("image/"));
  const audio = !fallback && (item.type === "audio" || item.contentType?.startsWith("audio/"));
  const duration = effectiveDuration(item);

  useEffect(() => { setUseStreamingFallback(false); }, [item.itemId]);

  useEffect(() => {
    if (!fallback) return;
    onStatus({
      state: "unavailable",
      lessonId: playlist.playlistId,
      itemId: item.itemId,
      positionMs: 0,
      durationMs: item.durationMs,
      volumePercent: 0,
      error: item.fallbackMessage || "This item is not supported by this display.",
    });
    // The fallback is tied to the manifest decision for this item.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fallback, item.itemId]);

  useEffect(() => {
    if (!image) return;
    let position = Math.max(0, seekMs);
    let previous = performance.now();
    const timer = window.setInterval(() => {
      const now = performance.now();
      if (!paused) position += now - previous;
      previous = now;
      setImagePosition(position);
      setOpacity(visualOpacity(item, position, duration ?? Number.MAX_SAFE_INTEGER));
      onStatus({ state: paused ? "paused" : "playing", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: Math.round(position), durationMs: duration, volumePercent: item.volumePercent });
      if (duration != null && position >= duration) {
        window.clearInterval(timer);
        onEnded();
      }
    }, 50);
    return () => window.clearInterval(timer);
    // This timing loop is recreated only for media identity, pause, or seek changes; live callbacks must not reset it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [item.itemId, image, paused, seekMs]);

  useEffect(() => {
    if (!online) return;
    configureOnlineFrame(frameRef.current, item, paused);
    onStatus({ state: paused ? "paused" : "playing", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: 0, durationMs: item.durationMs, volumePercent: item.volumePercent });
    // Online telemetry changes only with the selected item or requested playback state.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [item.itemId, item.muted, item.playbackRatePercent, item.volumePercent, online, paused]);

  useEffect(() => {
    if (!online) return;
    const receive = (event: MessageEvent) => {
      if (event.source !== frameRef.current?.contentWindow) return;
      try {
        const message = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
        if (message?.event === "onStateChange" && message.info === 0) onEnded();
      } catch { /* Embedded pages may send unrelated non-JSON messages. */ }
    };
    window.addEventListener("message", receive);
    return () => window.removeEventListener("message", receive);
    // Completion belongs to this online cue even when the parent callback identity changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [item.itemId, online]);

  useEffect(() => {
    const media = mediaRef.current;
    if (!media) return;
    const play = () => media.play().catch(error => {
      if (error?.name === "NotAllowedError") onBlocked();
      onStatus({ state: "paused", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: Math.max(0, (media.currentTime * 1_000) - item.startMs), durationMs: duration, volumePercent: item.volumePercent });
    });
    if (paused) media.pause(); else void play();
    // The media element owns progress telemetry; this effect only responds to transport intent.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paused, unlockNonce, item.itemId]);

  function ready() {
    const media = mediaRef.current;
    if (!media) return;
    media.playbackRate = Math.max(.25, Math.min(4, (item.playbackRatePercent || 100) / 100));
    media.currentTime = Math.max(0, item.startMs + seekMs) / 1_000;
    if (!paused) void media.play().catch(error => {
      if (error?.name === "NotAllowedError") onBlocked();
    });
  }

  function progress() {
    const media = mediaRef.current;
    if (!media) return;
    const position = Math.max(0, (media.currentTime * 1_000) - item.startMs);
    const resolvedDuration = duration ?? (Number.isFinite(media.duration) ? Math.max(0, media.duration * 1_000 - item.startMs) : undefined);
    const visualDuration = resolvedDuration ?? Number.MAX_SAFE_INTEGER;
    const fade = fadeOpacity(item, position, visualDuration);
    setOpacity(visualOpacity(item, position, visualDuration));
    media.volume = item.muted ? 0 : Math.min(1, Math.max(0, item.volumePercent / 100) * fade);
    onStatus({ state: media.paused ? "paused" : "playing", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: Math.round(position), durationMs: resolvedDuration == null ? undefined : Math.round(resolvedDuration), volumePercent: item.volumePercent });
    if (item.endMs && media.currentTime * 1_000 >= item.endMs) {
      media.pause();
      onEnded();
    }
  }

  function failed() {
    const message = mediaRef.current?.error ? `Browser media error ${mediaRef.current.error.code}: ${mediaRef.current.error.message || "Unable to play this format."}` : "The browser could not play this media.";
    if (localYouTubeFallback && !useStreamingFallback) {
      setUseStreamingFallback(true);
      onStatus({ state: "buffering", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: 0, durationMs: duration, volumePercent: item.volumePercent, error: "Local YouTube media failed; using the original streaming source." });
      return;
    }
    onStatus({ state: "error", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: 0, durationMs: duration, volumePercent: item.volumePercent, error: message });
  }

  const stageStyle = { backgroundColor: item.fitMode === "letterbox" ? "#000000" : item.backgroundColor || "#000000" };
  const mediaStyle = cueVisualStyle(item, opacity);

  if (fallback) return <div className="web-player-stage web-player-fallback" style={stageStyle} role="status">
    <div>
      <span>CONTENT UNAVAILABLE</span>
      <h1>{item.title}</h1>
      <p>{item.fallbackMessage || "This item is not supported by this display."}</p>
      <small>Use Previous or Next to continue through the lesson.</small>
    </div>
  </div>;

  if (item.type === "activity" || item.activity || item.activityDefinitionId) {
    const defId = item.activity?.definitionId || item.activityDefinitionId;
    return (
      <div className="web-player-stage activity" style={stageStyle}>
        <ActivityDisplay
          definitionId={defId}
          lessonId={playlist.playlistId}
          lessonItemId={item.itemId}
          interactive={false}
        />
      </div>
    );
  }

  if (online) return <div className="web-player-stage online" style={stageStyle}>
    <iframe ref={frameRef} title={item.title} src={youtubeApiUrl(onlineUrl!)} allow="autoplay; fullscreen; encrypted-media; picture-in-picture" referrerPolicy="strict-origin-when-cross-origin" style={mediaStyle} onLoad={() => { frameRef.current?.contentWindow?.postMessage(JSON.stringify({ event: "listening", id: item.itemId }), "*"); configureOnlineFrame(frameRef.current, item, paused); }} />
    <div className="web-player-black" style={{ opacity: paused ? .2 : 0 }} />
  </div>;

  if (image) return <div className="web-player-stage" style={stageStyle}>
    <img src={item.downloadUrl} alt={item.title} style={mediaStyle} onError={() => onStatus({ state: "error", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: imagePosition, durationMs: duration, volumePercent: item.volumePercent, error: "The browser could not load this image." })} />
  </div>;

  const common = {
    ref: (node: HTMLMediaElement | null) => { mediaRef.current = node; },
    src: item.downloadUrl,
    preload: "auto",
    onLoadedMetadata: ready,
    onTimeUpdate: progress,
    onWaiting: () => onStatus({ state: "buffering", lessonId: playlist.playlistId, itemId: item.itemId, positionMs: statusPosition(mediaRef.current, item), durationMs: duration, volumePercent: item.volumePercent }),
    onPlaying: progress,
    onEnded,
    onError: failed,
    style: mediaStyle,
  };
  return <div className={`web-player-stage ${audio ? "audio" : ""}`} style={stageStyle}>
    {audio ? <><div className="audio-art">♫</div><audio {...common} /></> : <video {...common} playsInline />}
  </div>;
}

function usePreload(item?: CueItem) {
  useEffect(() => {
    if (!item?.downloadUrl) return;
    if (item.type === "image" || item.contentType?.startsWith("image/")) {
      const image = new Image();
      image.src = item.downloadUrl;
      return () => { image.src = ""; };
    }
    const link = document.createElement("link");
    link.rel = "prefetch";
    link.href = item.downloadUrl;
    link.as = item.type === "audio" ? "audio" : "video";
    document.head.appendChild(link);
    return () => link.remove();
    // Preloading is keyed to the resolved next item URL.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [item?.itemId, item?.downloadUrl]);
}

function useSignagePreload(signage?: Signage[]) {
  const signature = signage?.flatMap(item => signageMediaItems(item)).map(item => `${item.itemId}:${item.downloadUrl || ""}`).join("|") || "";
  useEffect(() => {
    const elements: Array<HTMLImageElement | HTMLLinkElement> = [];
    for (const media of (signage || []).flatMap(signageMediaItems)) {
      if (!media.downloadUrl) continue;
      if (media.type === "image" || media.contentType?.startsWith("image/")) {
        const image = new Image();
        image.src = media.downloadUrl;
        elements.push(image);
      } else {
        const link = document.createElement("link");
        link.rel = "prefetch";
        link.href = media.downloadUrl;
        link.as = media.type === "audio" ? "audio" : "video";
        document.head.appendChild(link);
        elements.push(link);
      }
    }
    return () => elements.forEach(element => element instanceof HTMLLinkElement ? element.remove() : element.src = "");
    // The signature restarts prefetch only when schedule media URLs change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);
}

/**
 * Keep the media this screen will need on the device.
 *
 * Signage was cached from the start; a lesson was not, so a room that lost its
 * network mid-service lost the lesson with it while the rota on the wall kept
 * playing. Lessons are cached the same way now.
 *
 * Nothing else had to change for playback to use them. The service worker
 * already answers any /api/v1/media request from this cache before going to the
 * network, so a cached lesson simply plays.
 */
function useDurableMediaCache(
  signage: Signage[] | undefined,
  playlists: Playlist[] | undefined,
  identity: Identity | null,
  inventoryRef: { current: { itemId: string; title: string; state: string; sizeBytes: number; expectedBytes?: number; error?: string }[] },
  errorsRef: { current: { timestamp: string; area: string; message: string; itemId?: string }[] },
) {
  const wanted = [
    ...(signage || []).flatMap(signageMediaItems),
    ...(playlists || []).flatMap(lessonMediaItems),
  ];
  const signature = wanted.map(item => `${item.itemId}:${item.downloadUrl || ""}:${item.sha256 || ""}`).join("|");
  useEffect(() => {
    // `undefined` means the manifest has not arrived yet. An empty, loaded
    // array is meaningful and should prune the cache; an unloaded manifest is
    // not permission to delete media that may be needed offline.
    if (signage === undefined || playlists === undefined) return;
    if (!identity || !("caches" in window)) {
      inventoryRef.current = [];
      return;
    }
    let cancelled = false;
    void (async () => {
      const cache = await caches.open(MEDIA_CACHE);
      if (cancelled) return;
      const media = [...new Map(
        wanted
          .filter((item): item is CueItem => Boolean(item?.downloadUrl))
          .map(item => [item.itemId, item]),
      ).values()];
      const desired = new Set(media.map(item => new URL(item.downloadUrl!, location.origin).toString()));
      for (const request of await cache.keys()) {
        if (cancelled) return;
        if (!desired.has(request.url)) await cache.delete(request);
      }
      const inventory: typeof inventoryRef.current = [];
      for (const item of media) {
        if (cancelled) return;
        const url = new URL(item.downloadUrl!, location.origin).toString();
        try {
          let response = await cache.match(url);
          if (cancelled) return;
          if (!response) {
            inventory.push({ itemId: item.itemId, title: item.title, state: "downloading", sizeBytes: 0, expectedBytes: item.sizeBytes });
            if (!cancelled) inventoryRef.current = [...inventory];
            const downloaded = await fetch(url, { headers: { Authorization: `Bearer ${identity.token}` }, cache: "no-store" });
            if (cancelled) return;
            if (!downloaded.ok) throw new Error(`Media cache request failed (${downloaded.status}).`);
            await cache.put(url, downloaded.clone());
            response = downloaded;
          }
          if (cancelled) return;
          const bytes = Number(response.headers.get("content-length")) || item.sizeBytes || 0;
          inventory.push({ itemId: item.itemId, title: item.title, state: "ready", sizeBytes: bytes, expectedBytes: item.sizeBytes });
        } catch (cause) {
          if (cancelled) return;
          const message = errorText(cause);
          inventory.push({ itemId: item.itemId, title: item.title, state: "failed", sizeBytes: 0, expectedBytes: item.sizeBytes, error: message });
          errorsRef.current = [{ timestamp: new Date().toISOString(), area: "media-cache", message, itemId: item.itemId }, ...errorsRef.current].slice(0, 20);
        }
        if (!cancelled) inventoryRef.current = [...inventory];
      }
    })();
    return () => { cancelled = true; };
    // Cache population is keyed to the media signature and paired screen.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity?.screenId, signature]);
}

/**
 * The media a lesson will ask for, in the order it will ask.
 *
 * Only what the screen is allowed to keep: a cue marked offline-eligible with
 * something to download. A linked video stays a link.
 */
function lessonMediaItems(playlist: Playlist): CueItem[] {
  const items: Array<CueItem | null | undefined> = [
    ...(playlist.preRoll?.items || []),
    playlist.countdown?.item,
    ...playlist.items,
    ...(playlist.postLesson?.items || []),
  ];
  return items.filter((item): item is CueItem =>
    Boolean(item && item.offlineEligible && item.downloadUrl));
}

function signageMediaItems(signage: Signage): CueItem[] {
  const items: Array<CueItem | null | undefined> = [
    signage.media,
    signage.backgroundAudio,
    ...(signage.zones || []).map(zone => zone.media),
    ...(signage.contentPlaylist?.items || []).flatMap(entry => [
      entry.media,
      entry.layout?.backgroundAudio,
      ...(entry.layout?.zones || []).map(zone => zone.media),
    ]),
  ];
  return items.filter((item): item is CueItem => Boolean(item));
}

function readIdentity(): Identity | null {
  try {
    const query = new URLSearchParams(location.search);
    const screenId = query.get("screenId"), token = query.get("token");
    if (screenId && token) return { screenId, token, deviceName: query.get("name") || "Browser display" };
    const value = JSON.parse(localStorage.getItem(IDENTITY_KEY) || "null") as Partial<Identity> | null;
    return value?.screenId && value.token ? { screenId: value.screenId, token: value.token, deviceName: value.deviceName || "Browser display" } : null;
  } catch { return null; }
}

function schedulePhase(playlist: Playlist): { kind: "idle" | "preroll" | "countdown" | "ready"; seekMs: number } {
  if (!playlist.designatedStartAt) return { kind: "ready", seekMs: 0 };
  const now = Date.now();
  const designated = Date.parse(playlist.designatedStartAt);
  if (now >= designated) return { kind: "ready", seekMs: 0 };
  if (playlist.countdown) {
    const start = playlist.countdown.startAt ? Date.parse(playlist.countdown.startAt) : designated - playlist.countdown.durationMs;
    if (now >= start) return { kind: "countdown", seekMs: Math.max(0, now - start) };
  }
  const preRollStart = playlist.preRollStartsAt ? Date.parse(playlist.preRollStartsAt) : designated - 30 * 60_000;
  return playlist.preRoll?.items.length && now >= preRollStart ? { kind: "preroll", seekMs: 0 } : { kind: "idle", seekMs: 0 };
}

function effectiveDuration(item: CueItem): number | undefined {
  if (item.endMs != null) return Math.max(0, item.endMs - item.startMs);
  if (item.durationMs != null) return Math.max(0, item.durationMs - item.startMs);
  if (item.imageDurationSeconds != null) return Math.max(1, item.imageDurationSeconds) * 1_000;
  return undefined;
}

function formatPlayerTime(value?: number) {
  if (value == null || !Number.isFinite(value)) return '--:--';
  const totalSeconds = Math.max(0, Math.floor(value / 1_000));
  const hours = Math.floor(totalSeconds / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
    : `${minutes}:${String(seconds).padStart(2, '0')}`;
}

function fadeOpacity(item: CueItem, position: number, duration: number) {
  const fadeIn = item.fadeInMs > 0 ? Math.min(1, Math.max(0, position / item.fadeInMs)) : 1;
  const fadeOut = item.fadeOutMs > 0 ? Math.min(1, Math.max(0, (duration - position) / item.fadeOutMs)) : 1;
  return Math.min(fadeIn, fadeOut);
}

function visualOpacity(item: CueItem, position: number, duration: number) {
  const cueFade = fadeOpacity(item, position, duration);
  if (item.transitionStyle !== "fade-black" || !item.transitionDurationMs) return cueFade;
  const transition = Math.max(1, item.transitionDurationMs);
  return Math.min(cueFade, Math.min(1, position / transition), Math.min(1, (duration - position) / transition));
}

function cueVisualStyle(item: CueItem, opacity: number): CSSProperties {
  const cropTop = Math.max(0, item.cropTopPercent || 0);
  const cropRight = Math.max(0, item.cropRightPercent || 0);
  const cropBottom = Math.max(0, item.cropBottomPercent || 0);
  const cropLeft = Math.max(0, item.cropLeftPercent || 0);
  const scaleX = 100 / Math.max(10, 100 - cropLeft - cropRight);
  const scaleY = 100 / Math.max(10, 100 - cropTop - cropBottom);
  return {
    opacity,
    objectFit: item.fitMode === "fill" ? "cover" : "contain",
    transform: `translate(${(cropRight - cropLeft) / 2}%, ${(cropBottom - cropTop) / 2}%) scale(${scaleX}, ${scaleY}) rotate(${item.rotationDegrees || 0}deg)`,
    clipPath: `inset(${cropTop}% ${cropRight}% ${cropBottom}% ${cropLeft}%)`,
    transition: "opacity 50ms linear",
  };
}

function statusPosition(media: HTMLMediaElement | null, item: CueItem) {
  return media ? Math.max(0, Math.round(media.currentTime * 1_000 - item.startMs)) : 0;
}

function manifestItemCount(manifest?: Manifest) {
  const lessonItems = manifest?.playlists.flatMap(playlist => [
    ...playlist.items,
    ...(playlist.preRoll?.items || []),
    ...(playlist.countdown ? [playlist.countdown.item] : []),
    ...(playlist.postLesson?.items || []),
  ]) || [];
  const signageItems = manifest?.signageSchedule?.flatMap(sign => sign.media ? [sign.media] : []) || [];
  return new Set([...lessonItems, ...signageItems].map(item => item.itemId)).size;
}

function browserName() {
  const agent = navigator.userAgent;
  if (agent.includes("Edg/")) return "Microsoft Edge";
  if (agent.includes("Firefox/")) return "Mozilla Firefox";
  if (agent.includes("Chrome/")) return "Google Chrome";
  if (agent.includes("Safari/")) return "Apple Safari";
  return "Web browser";
}

function codecCapabilities() {
  const video = document.createElement("video");
  const audio = document.createElement("audio");
  return [
    { kind: "video", codec: "H.264 / AVC", supported: Boolean(video.canPlayType('video/mp4; codecs="avc1.42E01E"')), detail: "video/mp4" },
    { kind: "video", codec: "WebM / VP9", supported: Boolean(video.canPlayType('video/webm; codecs="vp9"')), detail: "video/webm" },
    { kind: "audio", codec: "AAC", supported: Boolean(audio.canPlayType('audio/mp4; codecs="mp4a.40.2"')), detail: "audio/mp4" },
    { kind: "audio", codec: "MP3", supported: Boolean(audio.canPlayType("audio/mpeg")), detail: "audio/mpeg" },
  ];
}

function networkQuality(latency?: number, online = true) {
  if (!online) return "offline";
  if (latency == null) return "unknown";
  if (latency < 100) return "excellent";
  if (latency < 250) return "good";
  if (latency < 500) return "fair";
  return "poor";
}

function needsPlaybackGesture(item: CueItem) {
  // Activities render their own interactive display and do not use the
  // browser media element. Do not cover the game with a first-play prompt.
  if (item.type === "activity" || item.activity || item.activityDefinitionId) return false;
  if (item.playbackUrl) return true;
  if (item.volumePercent <= 0) return false;
  return item.type === "video" || item.type === "audio" ||
    Boolean(item.contentType?.startsWith("video/") || item.contentType?.startsWith("audio/"));
}

function youtubeApiUrl(value: string) {
  try {
    const url = new URL(value, location.origin);
    if (url.hostname.includes("youtube") || url.hostname.includes("youtu.be")) {
      url.searchParams.set("autoplay", "1");
      url.searchParams.set("enablejsapi", "1");
      url.searchParams.set("playsinline", "1");
    }
    return url.toString();
  } catch { return value; }
}

function configureOnlineFrame(frame: HTMLIFrameElement | null, item: CueItem, paused: boolean) {
  const target = frame?.contentWindow;
  const command = (func: string, args: unknown[] = []) => target?.postMessage(JSON.stringify({ event: "command", func, args }), "*");
  command(paused ? "pauseVideo" : "playVideo");
  command("setPlaybackRate", [Math.max(.25, Math.min(2, (item.playbackRatePercent || 100) / 100))]);
  command("setVolume", [item.muted ? 0 : Math.max(0, Math.min(100, item.volumePercent))]);
  command(item.muted ? "mute" : "unMute");
}

function connectionLabel(connection: ConnectionState) {
  if (connection === "online") return "Connected";
  if (connection === "connecting") return "Connecting";
  if (connection === "reconnecting") return "Reconnecting";
  if (connection === "offline") return "Offline";
  return "Connection error";
}

function formatLessonDate(value: string) {
  const date = new Date(`${value}T12:00:00`);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleDateString([], { month: "short", day: "numeric" });
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : "The request could not be completed.";
}

class PairingExpiredError extends Error {}
