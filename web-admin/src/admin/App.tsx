import { AccessibleDialogHost } from "../AccessibleDialogs";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { FormEvent, lazy, ReactNode, Suspense, useCallback, useEffect, useRef, useState } from "react";
import { api } from "./api";
import { AccountProfile, Audit, Backup, Bootstrap, Lesson, LessonClass, LessonTemplate, Media, Permission, RecurringSchedule, Screen, Session, SettingsSection, Signage, UpdateStatus, User, View } from "./models";
import { BrandMark, Field, Modal } from "./ui";
import { AuthenticatorMfaPanel } from "./views/Mfa";
import { errorText, formatBytes, isAccountLinkPath, isActivityDisplayPath, isActivityParticipantPath, isAudienceDisplayPath, isAudiencePath, isControllerPath, isWebPlayerPath } from "./utils";

const ActivityParticipantApp = lazy(() => import("../activities/ActivityParticipant").then((module) => ({ default: module.ActivityParticipantApp })));
const ActivityTvDisplayApp = lazy(() => import("../activities/ActivityTvDisplay").then((module) => ({ default: module.ActivityTvDisplayApp })));
const AudienceAdmin = lazy(() => import("../AudienceInteraction").then((module) => ({ default: module.AudienceAdmin })));
const AudienceDisplayApp = lazy(() => import("../AudienceInteraction").then((module) => ({ default: module.AudienceDisplayApp })));
const AudienceResponseApp = lazy(() => import("../AudienceInteraction").then((module) => ({ default: module.AudienceResponseApp })));
const Dashboard = lazy(() => import("./views/Dashboard").then((module) => ({ default: module.Dashboard })));
const WebPlayerApp = lazy(() => import("../WebPlayer").then((module) => ({ default: module.WebPlayerApp })));
const ControllerView = lazy(() => import("./views/Controller").then((module) => ({ default: module.ControllerView })));
const PublicControllerApp = lazy(() => import("./views/Controller").then((module) => ({ default: module.PublicControllerApp })));
const ClassesView = lazy(() => import("./views/Lessons").then((module) => ({ default: module.ClassesView })));
const CalendarView = lazy(() => import("./views/Calendar").then((module) => ({ default: module.CalendarView })));
const MediaView = lazy(() => import("./views/Media").then((module) => ({ default: module.MediaView })));
const ScreensView = lazy(() => import("./views/Screens").then((module) => ({ default: module.ScreensView })));
const Settings = lazy(() => import("./views/Settings").then((module) => ({ default: module.Settings })));
const TemplatesView = lazy(() => import("./views/Templates").then((module) => ({ default: module.TemplatesView })));
const UsersView = lazy(() => import("./views/Users").then((module) => ({ default: module.UsersView })));
const SimpleSignage = lazy(() => import("../SimpleSignage").then((module) => ({ default: module.SimpleSignage })));
const ActivityLibrary = lazy(() => import("../activities/ActivityLibrary").then((module) => ({ default: module.ActivityLibrary })));

export function App() {
  let content: ReactNode;
  if (isActivityDisplayPath(location.pathname)) content = <ActivityTvDisplayApp />;
  else if (isWebPlayerPath(location.pathname)) content = <WebPlayerApp />;
  else if (isActivityParticipantPath(location.pathname)) content = <ActivityParticipantApp />;
  else if (isAudienceDisplayPath(location.pathname))
    content = <AudienceDisplayApp />;
  else if (isAudiencePath(location.pathname))
    content = <AudienceResponseApp />;
  else if (isControllerPath(location.pathname))
    content = <PublicControllerApp />;
  else content = <AdminApp />;
  return (
    <>
      <Suspense fallback={<Splash />}>{content}</Suspense>
      <AccessibleDialogHost />
    </>
  );
}

export function AdminApp() {
  const [session, setSession] = useState<Session>();
  const [view, setView] = useState<View>(
    isControllerPath(location.pathname) ? "controller" : "dashboard",
  );
  const [notice, setNotice] = useState("");

  useEffect(() => {
    api<Session>("/api/v1/auth/session")
      .then(setSession)
      .catch(() => setSession({ setupRequired: false, authenticated: false }));
  }, []);
  if (!session) return <Splash />;
  if (!session.authenticated || isAccountLinkPath(location.pathname))
    return (
      <Auth
        session={session}
        onAuthenticated={() =>
          api<Session>("/api/v1/auth/session").then(setSession)
        }
      />
    );
  if (session.mustChangePassword)
    return (
      <RequiredPasswordChange
        onChanged={() => api<Session>("/api/v1/auth/session").then(setSession)}
      />
    );
  return (
    <Shell
      view={view}
      setView={setView}
      username={session.displayName || session.username || "admin"}
      currentUsername={session.username || ""}
      role={session.role || "Viewer"}
      permissions={session.permissions || []}
      notice={notice}
      setNotice={setNotice}
      onLogout={async () => {
        await api<void>("/api/v1/auth/logout", { method: "POST", body: "{}" });
        setSession({ ...session, authenticated: false, setupRequired: false });
      }}
    />
  );
}

export function Splash() {
  return (
    <main className="auth-page">
      <BrandMark large />
      <p className="muted">Opening your local LessonCue server…</p>
    </main>
  );
}

export function RequiredPasswordChange({ onChanged }: { onChanged: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = Object.fromEntries(new FormData(event.currentTarget));
    if (values.newPassword !== values.confirmPassword) {
      setError("Passwords do not match.");
      setBusy(false);
      return;
    }
    try {
      await api("/api/v1/auth/password/change-required", {
        method: "POST",
        body: JSON.stringify({
          currentPassword: values.currentPassword,
          newPassword: values.newPassword,
        }),
      });
      onChanged();
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="auth-page">
      <section className="auth-card">
        <div className="brand-lockup">
          <BrandMark />
          <div>
            <strong>LessonCue</strong>
            <span>Secure first sign-in</span>
          </div>
        </div>
        <div className="auth-copy">
          <span className="eyebrow">PASSWORD UPDATE REQUIRED</span>
          <h1>Choose your password</h1>
          <p>
            The administrator-issued password was temporary. Replace it before
            opening LessonCue.
          </p>
        </div>
        <form className="stack" onSubmit={submit}>
          <Field label="Temporary password">
            <input
              name="currentPassword"
              type="password"
              required
              autoComplete="current-password"
              autoFocus
            />
          </Field>
          <Field
            label="New password"
            hint="10+ characters with uppercase, lowercase, and a number"
          >
            <input
              name="newPassword"
              type="password"
              required
              minLength={10}
              autoComplete="new-password"
            />
          </Field>
          <Field label="Confirm new password">
            <input
              name="confirmPassword"
              type="password"
              required
              minLength={10}
              autoComplete="new-password"
            />
          </Field>
          {error && <div className="alert error" role="alert">{error}</div>}
          <button className="button primary wide" disabled={busy}>
            {busy ? "Changing…" : "Change password and continue"}
          </button>
        </form>
      </section>
    </main>
  );
}

export function Auth({
  session,
  onAuthenticated,
}: {
  session: Session;
  onAuthenticated: () => void;
}) {
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [username, setUsername] = useState("");
  const [mfaRequired, setMfaRequired] = useState(false);
  const [mode, setMode] = useState<"login" | "register" | "forgot" | "resend">(
    location.pathname === "/register" && session.registrationAvailable
      ? "register"
      : location.pathname === "/forgot-password" && session.emailConfigured
        ? "forgot"
        : "login",
  );
  const token = new URLSearchParams(location.search).get("token") || "";
  const verificationPath =
    location.pathname === "/verify" || location.pathname === "/verify-email";
  const resetPath = location.pathname === "/reset-password";
  const setupAccountPath = location.pathname === "/setup-account";
  const [linkResult, setLinkResult] = useState("");
  useEffect(() => {
    if (
      session.setupRequired ||
      mode !== "login" ||
      setupAccountPath ||
      resetPath ||
      verificationPath ||
      username.trim().length < 3
    ) {
      setMfaRequired(false);
      return;
    }
    let active = true;
    const timer = window.setTimeout(() => {
      void api<{ required: boolean }>("/api/v1/auth/login/mfa-requirement", {
        method: "POST",
        body: JSON.stringify({ username: username.trim() }),
      })
        .then((result) => {
          if (active) setMfaRequired(result.required);
        })
        .catch(() => {
          if (active) setMfaRequired(false);
        });
    }, 220);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [mode, resetPath, session.setupRequired, setupAccountPath, username, verificationPath]);
  useEffect(() => {
    if (!verificationPath || !token) return;
    const endpoint =
      location.pathname === "/verify-email"
        ? "/api/v1/auth/email/verify"
        : "/api/v1/auth/verify";
    api<{ message: string }>(endpoint, {
      method: "POST",
      body: JSON.stringify({ token }),
    })
      .then((result) => setLinkResult(result.message))
      .catch((cause) => setError(errorText(cause)));
  }, [token, verificationPath]);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = Object.fromEntries(new FormData(event.currentTarget));
    try {
      await api(
        session.setupRequired ? "/api/v1/auth/setup" : "/api/v1/auth/login",
        { method: "POST", body: JSON.stringify(values) },
      );
      history.replaceState(null, "", "/");
      onAuthenticated();
    } catch (e) {
      setError(
        e instanceof Error && e.message !== "SESSION_EXPIRED"
          ? e.message
          : "The username or password was not accepted.",
      );
    } finally {
      setBusy(false);
    }
  }
  async function register(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = Object.fromEntries(new FormData(event.currentTarget));
    try {
      const result = await api<{ message: string }>("/api/v1/auth/register", {
        method: "POST",
        body: JSON.stringify(values),
      });
      setLinkResult(result.message);
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  async function forgot(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const result = await api<{ message: string }>(
        "/api/v1/auth/password/forgot",
        {
          method: "POST",
          body: JSON.stringify(
            Object.fromEntries(new FormData(event.currentTarget)),
          ),
        },
      );
      setLinkResult(result.message);
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  async function resend(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const result = await api<{ message: string }>(
        "/api/v1/auth/verification/resend",
        {
          method: "POST",
          body: JSON.stringify(
            Object.fromEntries(new FormData(event.currentTarget)),
          ),
        },
      );
      setLinkResult(result.message);
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  async function resetPassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = Object.fromEntries(new FormData(event.currentTarget));
    if (values.password !== values.confirmPassword) {
      setError("Passwords do not match.");
      setBusy(false);
      return;
    }
    try {
      const result = await api<{ message: string }>(
        "/api/v1/auth/password/reset",
        {
          method: "POST",
          body: JSON.stringify({ token, password: values.password }),
        },
      );
      setLinkResult(result.message);
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  async function setupAccount(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = Object.fromEntries(new FormData(event.currentTarget));
    if (values.password !== values.confirmPassword) {
      setError("Passwords do not match.");
      setBusy(false);
      return;
    }
    try {
      const result = await api<{ message: string }>(
        "/api/v1/auth/setup-account",
        {
          method: "POST",
          body: JSON.stringify({
            token,
            username: values.username,
            displayName: values.displayName,
            password: values.password,
          }),
        },
      );
      setLinkResult(result.message);
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="auth-page">
      <section className="auth-card">
        <div className="brand-lockup">
          <BrandMark />
          <div>
            <strong>LessonCue</strong>
            <span>Local classroom media control</span>
          </div>
        </div>
        <div className="auth-copy">
          <span className="eyebrow">
            {session.setupRequired ? "FIRST-RUN SETUP" : "WELCOME BACK"}
          </span>
          <h1>
            {session.setupRequired
              ? "Create your Service Admin"
              : setupAccountPath
                ? "Set up your account"
                : resetPath
                  ? "Choose a new password"
                  : verificationPath
                    ? "Verify your account"
                    : mode === "register"
                      ? session.registrationMode === "approval"
                        ? "Request access"
                        : "Create your account"
                      : mode === "forgot"
                        ? "Reset your password"
                        : mode === "resend"
                          ? "Resend verification"
                          : "Sign in to LessonCue"}
          </h1>
          <p>
            {session.setupRequired
              ? "This account stays on this server. Nothing is sent to a hosted service."
              : setupAccountPath
                ? "Choose your own name, username, and password. This invitation can be used only once."
                : resetPath || verificationPath
                  ? "This secure link can be used only once and expires automatically."
                  : mode === "register"
                    ? session.registrationMode === "approval"
                      ? "Verify your email, then an administrator will review your request before you can sign in."
                      : "Your administrator controls who may register on this server."
                    : mode === "forgot"
                      ? "We will send a one-time link if the address belongs to a verified account."
                      : mode === "resend"
                        ? "We will replace the previous link if the address belongs to an unverified account."
                        : "Manage classes, playlists, and screens on your local network."}
          </p>
        </div>
        {linkResult ? (
          <div className="account-result">
            <strong>{linkResult}</strong>
            <button
              className="button primary wide"
              onClick={() => location.assign("/")}
            >
              Continue to LessonCue
            </button>
          </div>
        ) : setupAccountPath ? (
          <form onSubmit={setupAccount} className="stack">
            <Field label="Your name">
              <input
                name="displayName"
                required
                maxLength={120}
                autoComplete="name"
                autoFocus
              />
            </Field>
            <Field label="Username">
              <input
                name="username"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                required
                minLength={3}
                maxLength={80}
                autoComplete="username"
              />
            </Field>
            <Field
              label="Password"
              hint="10+ characters with uppercase, lowercase, and a number"
            >
              <input
                name="password"
                type="password"
                required
                minLength={10}
                autoComplete="new-password"
              />
            </Field>
            <Field label="Confirm password">
              <input
                name="confirmPassword"
                type="password"
                required
                minLength={10}
                autoComplete="new-password"
              />
            </Field>
            {error && <div className="alert error" role="alert">{error}</div>}
            <button className="button primary wide" disabled={busy}>
              {busy ? "Saving…" : "Finish account setup"}
            </button>
          </form>
        ) : resetPath ? (
          <form onSubmit={resetPassword} className="stack">
            <Field
              label="New password"
              hint="10+ characters with uppercase, lowercase, and a number"
            >
              <input
                name="password"
                type="password"
                required
                minLength={10}
                autoComplete="new-password"
              />
            </Field>
            <Field label="Confirm new password">
              <input
                name="confirmPassword"
                type="password"
                required
                minLength={10}
                autoComplete="new-password"
              />
            </Field>
            {error && <div className="alert error" role="alert">{error}</div>}
            <button className="button primary wide" disabled={busy}>
              {busy ? "Saving…" : "Change password"}
            </button>
          </form>
        ) : verificationPath ? (
          <div className="account-result">
            {error ? (
              <div className="alert error" role="alert">{error}</div>
            ) : (
              <p>Checking this one-time link…</p>
            )}
          </div>
        ) : mode === "register" ? (
          <form onSubmit={register} className="stack">
            <Field label="Your name">
              <input
                name="displayName"
                required
                autoComplete="name"
                autoFocus
              />
            </Field>
            <div className="two-fields">
              <Field label="Username">
                <input
                  name="username"
                  required
                  minLength={3}
                  autoComplete="username"
                />
              </Field>
              <Field label="Email">
                <input
                  name="email"
                  type="email"
                  required
                  autoComplete="email"
                />
              </Field>
            </div>
            {session.registrationMode === "code" && (
              <Field label="Registration code">
                <input name="code" required autoComplete="off" />
              </Field>
            )}
            <Field
              label="Password"
              hint="10+ characters with uppercase, lowercase, and a number"
            >
              <input
                name="password"
                type="password"
                required
                minLength={10}
                autoComplete="new-password"
              />
            </Field>
            {error && <div className="alert error" role="alert">{error}</div>}
            <button className="button primary wide" disabled={busy}>
              {busy
                ? "Creating…"
                : session.registrationMode === "approval"
                  ? "Submit access request"
                  : "Create account"}
            </button>
            <button
              className="text-button"
              type="button"
              onClick={() => {
                setMode("login");
                setError("");
              }}
            >
              Back to sign in
            </button>
          </form>
        ) : mode === "forgot" ? (
          <form onSubmit={forgot} className="stack">
            <Field label="Email">
              <input
                name="email"
                type="email"
                required
                autoComplete="email"
                autoFocus
              />
            </Field>
            {error && <div className="alert error" role="alert">{error}</div>}
            <button className="button primary wide" disabled={busy}>
              {busy ? "Sending…" : "Send reset link"}
            </button>
            <button
              className="text-button"
              type="button"
              onClick={() => {
                setMode("login");
                setError("");
              }}
            >
              Back to sign in
            </button>
          </form>
        ) : mode === "resend" ? (
          <form onSubmit={resend} className="stack">
            <Field label="Email">
              <input
                name="email"
                type="email"
                required
                autoComplete="email"
                autoFocus
              />
            </Field>
            {error && <div className="alert error" role="alert">{error}</div>}
            <button className="button primary wide" disabled={busy}>
              {busy ? "Sending…" : "Resend verification link"}
            </button>
            <button
              className="text-button"
              type="button"
              onClick={() => {
                setMode("login");
                setError("");
              }}
            >
              Back to sign in
            </button>
          </form>
        ) : (
          <form onSubmit={submit} className="stack">
            {session.setupRequired && (
              <>
                <div className="two-fields">
                  <Field label="Organization name">
                    <input
                      name="organizationName"
                      required
                      defaultValue="My Organization"
                    />
                  </Field>
                  <Field label="Site or campus">
                    <input
                      name="siteName"
                      required
                      defaultValue="Main Campus"
                    />
                  </Field>
                </div>
                <div className="two-fields">
                  <Field label="Your name">
                    <input name="displayName" required autoComplete="name" />
                  </Field>
                  <Field label="Email (optional)">
                    <input name="email" type="email" autoComplete="email" />
                  </Field>
                </div>
                <div className="two-fields">
                  <Field label="Time zone">
                    <select
                      name="timeZone"
                      defaultValue={
                        Intl.DateTimeFormat().resolvedOptions().timeZone
                      }
                    >
                      <option>
                        {Intl.DateTimeFormat().resolvedOptions().timeZone}
                      </option>
                      <option>America/New_York</option>
                      <option>America/Chicago</option>
                      <option>America/Denver</option>
                      <option>America/Los_Angeles</option>
                      <option>UTC</option>
                    </select>
                  </Field>
                  <Field label="Week starts">
                    <select name="weekStartsOn">
                      <option>Sunday</option>
                      <option>Monday</option>
                    </select>
                  </Field>
                </div>
              </>
            )}
            <Field label="Username">
              <input
                name="username"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                required
                minLength={3}
                autoComplete="username"
                autoFocus={!session.setupRequired}
              />
            </Field>
            <Field
              label="Password"
              hint={
                session.setupRequired
                  ? "10+ characters with uppercase, lowercase, and a number"
                  : undefined
              }
            >
              <div className="password-field">
                <input
                  name="password"
                  type={showPassword ? "text" : "password"}
                  required
                  minLength={session.setupRequired ? 10 : undefined}
                  autoComplete={
                    session.setupRequired ? "new-password" : "current-password"
                  }
                />
                <button
                  type="button"
                  className="text-button"
                  onClick={() => setShowPassword(!showPassword)}
                >
                  {showPassword ? "Hide" : "Show"}
                </button>
              </div>
            </Field>
            {!session.setupRequired && mode === "login" && mfaRequired && (
              <Field
                label="Authenticator code"
                hint="Enter the current six-digit code from your authenticator app."
              >
                <input
                  name="mfaCode"
                  inputMode="numeric"
                  pattern="[0-9 ]{6,8}"
                  maxLength={8}
                  required
                  autoComplete="one-time-code"
                />
              </Field>
            )}
            {error && <div className="alert error" role="alert">{error}</div>}
            <button className="button primary wide" disabled={busy}>
              {busy
                ? "Please wait…"
                : session.setupRequired
                  ? "Finish setup"
                  : "Sign in"}
            </button>
          </form>
        )}
        {!session.setupRequired &&
          !setupAccountPath &&
          !resetPath &&
          !verificationPath && (
            <div className="auth-links">
              {session.registrationAvailable && (
                <a className="registration-link" href="/register">
                  {session.registrationMode === "code"
                    ? "Register with a code"
                    : session.registrationMode === "approval"
                      ? "Request access"
                      : "Create an account"}
                </a>
              )}
              {session.emailConfigured && (
                <button
                  className="text-button"
                  onClick={() => setMode("forgot")}
                >
                  Forgot password?
                </button>
              )}
              {session.emailConfigured && (
                <button
                  className="text-button"
                  onClick={() => setMode("resend")}
                >
                  Resend verification
                </button>
              )}
              <a
                className="recovery-link"
                href="https://github.com/ProbablyModded/lessoncue/blob/main/docs/installation.md#reset-a-forgotten-administrator-password"
                target="_blank"
                rel="noreferrer"
              >
                SSH recovery ↗
              </a>
            </div>
          )}
        <div className="local-note">
          <span className="status-dot" /> Local server · {location.host}
        </div>
      </section>
    </main>
  );
}

export function Shell({
  view,
  setView,
  username,
  currentUsername,
  role,
  permissions,
  onLogout,
  notice,
  setNotice,
}: {
  view: View;
  setView: (view: View) => void;
  username: string;
  currentUsername: string;
  role: string;
  permissions: Permission[];
  onLogout: () => void;
  notice: string;
  setNotice: (v: string) => void;
}) {
  const has = (permission: Permission) => permissions.includes(permission);
  const canPlan = has("planning.manage");
  const canUpload = has("uploads.manage");
  const canControl = has("playback.control");
  const canManageScreens = has("screens.manage");
  const canManageUsers = has("users.manage");
  const canManageAppSettings = has("app-settings.manage");
  const canManageServiceSettings = has("settings.manage");
  const canManageBackups = has("backups.manage");
  const canManageUpdates = has("updates.manage");
  const defaultSettingsSection: SettingsSection = canManageUpdates
    ? "system"
    : canManageServiceSettings
      ? "overview"
      : canManageBackups
        ? "backup"
        : "overview";
  const [settingsSection, setSettingsSection] = useState<SettingsSection>(defaultSettingsSection);
  const [settingsExpanded, setSettingsExpanded] = useState(view === "settings");
  const [dataVersion, setDataVersion] = useState(0);
  const [bootstrap, setBootstrap] = useState<Bootstrap>();
  // Absent on an older server payload, so default to available.
  const activitiesEnabled = bootstrap?.activitiesEnabled !== false;
  const [classes, setClasses] = useState<LessonClass[]>([]);
  const [lessons, setLessons] = useState<Lesson[]>([]);
  const [templates, setTemplates] = useState<LessonTemplate[]>([]);
  const [schedules, setSchedules] = useState<RecurringSchedule[]>([]);
  const [media, setMedia] = useState<Media[]>([]);
  const [screens, setScreens] = useState<Screen[]>([]);
  const [signage, setSignage] = useState<Signage[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [backups, setBackups] = useState<Backup[]>([]);
  const [audit, setAudit] = useState<Audit[]>([]);
  const [showProfile, setShowProfile] = useState(false);
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const mobileMenuButton = useRef<HTMLButtonElement>(null);
  const sidebar = useRef<HTMLElement>(null);
  const mainContent = useRef<HTMLElement>(null);
  const mobileNavWasOpen = useRef(false);
  const [loading, setLoading] = useState(true);
  const pairingExpiresAt = bootstrap?.pairingExpiresAt;
  const pairingFixed = bootstrap?.pairingFixed;
  const refresh = useCallback(() => setDataVersion((v) => v + 1), []);
  useEffect(() => {
    Promise.all([
      api<Bootstrap>("/api/v1/admin/bootstrap"),
      api<LessonClass[]>("/api/v1/classes"),
      api<Lesson[]>("/api/v1/lessons"),
      api<Media[]>("/api/v1/media"),
      api<Screen[]>("/api/v1/screens"),
      api<LessonTemplate[]>("/api/v1/lesson-templates"),
      api<RecurringSchedule[]>("/api/v1/recurring-schedules"),
      api<Signage[]>("/api/v1/signage").catch(() => []),
      canManageUsers ? api<User[]>("/api/v1/users") : Promise.resolve([]),
      canManageBackups ? api<Backup[]>("/api/v1/backups") : Promise.resolve([]),
      canManageAppSettings
        ? api<Audit[]>("/api/v1/audit")
        : Promise.resolve([]),
    ])
      .then(
        ([
          b,
          c,
          l,
          m,
          s,
          templateData,
          scheduleData,
          g,
          u,
          backupData,
          auditData,
        ]) => {
          setBootstrap(b);
          setClasses(c);
          setLessons(l);
          setMedia(m);
          setScreens(s);
          setTemplates(templateData);
          setSchedules(scheduleData);
          setSignage(g);
          setUsers(u);
          setBackups(backupData);
          setAudit(auditData);
        },
      )
      .catch((e) =>
        setNotice(
          e.message === "SESSION_EXPIRED"
            ? "Your session expired. Refresh the page to sign in again."
            : e.message,
        ),
      )
      .finally(() => setLoading(false));
  }, [
    dataVersion,
    setNotice,
    canManageUsers,
    canManageBackups,
    canManageAppSettings,
  ]);
  useEffect(() => {
    if (!loading && bootstrap) {
      performance.mark("lessoncue-first-meaningful-interaction");
      document.documentElement.dataset.lessoncueReady = "true";
    }
  }, [loading, bootstrap]);
  useEffect(() => {
    if (!bootstrap) return;
    document.documentElement.style.setProperty(
      "--deep",
      bootstrap.settings.primaryColor,
    );
    document.documentElement.style.setProperty(
      "--gold",
      bootstrap.settings.accentColor,
    );
    document.documentElement.style.setProperty(
      "--nav-text",
      bootstrap.settings.navigationTextColor,
    );
    document.documentElement.style.setProperty(
      "--nav-selected",
      bootstrap.settings.selectedTabColor,
    );
  }, [bootstrap]);
  useEffect(() => {
    if (!canManageUpdates) return;
    const poll = () =>
      api<UpdateStatus>("/api/v1/updates")
        .then((update) =>
          setBootstrap((current) =>
            current ? { ...current, update } : current,
          ),
        )
        .catch(() => undefined);
    const initial = window.setTimeout(poll, 20_000);
    const interval = window.setInterval(poll, 60 * 60 * 1000);
    return () => {
      window.clearTimeout(initial);
      window.clearInterval(interval);
    };
  }, [canManageUpdates]);
  useEffect(() => {
    if (!pairingExpiresAt || pairingFixed)
      return;
    let cancelled = false;
    const updatePin = () =>
      api<{ pin: string; expiresAt: string; fixedPin: boolean }>(
        "/api/v1/pairing/status",
      )
        .then((result) => {
          if (cancelled) return;
          setBootstrap((current) =>
            current
              ? {
                  ...current,
                  pairingPin: result.pin,
                  pairingExpiresAt: result.expiresAt,
                  pairingFixed: result.fixedPin,
                }
              : current,
          );
        })
        .catch(() => undefined);
    const delay = Math.max(
      100,
      new Date(pairingExpiresAt).getTime() - Date.now() + 25,
    );
    const timer = window.setTimeout(updatePin, delay);
    const onVisible = () => {
      if (document.visibilityState === "visible") void updatePin();
    };
    window.addEventListener("focus", updatePin);
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
      window.removeEventListener("focus", updatePin);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [pairingExpiresAt, pairingFixed]);
  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(""), 3_000);
    return () => window.clearTimeout(timer);
  }, [notice, setNotice]);
  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/sync")
      .withAutomaticReconnect([0, 1_000, 3_000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build();
    const refreshScreens = () =>
      api<Screen[]>("/api/v1/screens")
        .then(setScreens)
        .catch(() => undefined);
    const refreshManifest = () => refresh();
    connection.on("ScreenStatusChanged", refreshScreens);
    connection.on("ManifestInvalidated", refreshManifest);
    connection
      .start()
      .then(() => connection.invoke("JoinAdmins"))
      .catch(() => undefined);
    return () => {
      connection.off("ScreenStatusChanged", refreshScreens);
      connection.off("ManifestInvalidated", refreshManifest);
      void connection.stop();
    };
  }, [refresh]);
  useEffect(() => {
    const timer = window.setInterval(
      () =>
        api<Screen[]>("/api/v1/screens")
          .then(setScreens)
          .catch(() => undefined),
      2_500,
    );
    return () => window.clearInterval(timer);
  }, []);
  useEffect(() => {
    const controllerPath = isControllerPath(location.pathname)
      ? location.pathname
      : "/universalremote";
    const path = view === "controller" ? controllerPath : "/";
    if (location.pathname !== path) history.replaceState(null, "", path);
  }, [view]);
  useEffect(() => {
    if (
      (view === "controller" && !canControl) ||
      (view === "classes" && !canPlan) ||
      (view === "templates" && !canPlan) ||
      (view === "audience" && !canPlan) ||
      (view === "signage" && !canPlan) ||
      (view === "users" && !canManageUsers) ||
      (view === "settings" &&
        !canManageAppSettings &&
        !canManageServiceSettings &&
        !canManageBackups &&
        !canManageUpdates)
    )
      setView("dashboard");
  }, [
    view,
    canControl,
    canPlan,
    canManageUsers,
    canManageAppSettings,
    canManageServiceSettings,
    canManageBackups,
    canManageUpdates,
    bootstrap,
    setView,
  ]);

  const navItems: { key: View; icon: string; label: string }[] = [
    { key: "dashboard", icon: "⌂", label: "Dashboard" },
    ...(canControl
      ? [{ key: "controller" as View, icon: "⌁", label: "Controller" }]
      : []),
    ...(canPlan
      ? [
          { key: "classes" as View, icon: "▤", label: "Lessons" },
          { key: "templates" as View, icon: "↻", label: "Templates" },
          ...(activitiesEnabled
            ? [{ key: "activities" as View, icon: "✦", label: "Activities" }]
            : []),
          { key: "audience" as View, icon: "◉", label: "Audience" },
        ]
      : []),
    { key: "calendar" as View, icon: "□", label: "Calendar" },
    { key: "media" as View, icon: "▶", label: "Media Library" },
    { key: "screens" as View, icon: "▣", label: "Screens" },
    ...(canPlan
      ? [{ key: "signage" as View, icon: "◇", label: "Signage" }]
      : []),
    ...(canManageUsers
      ? [{ key: "users" as View, icon: "♙", label: "Users" }]
      : []),
    ...(canManageAppSettings ||
    canManageServiceSettings ||
    canManageBackups ||
    canManageUpdates
      ? [{ key: "settings" as View, icon: "⚙", label: "Settings" }]
      : []),
  ];
  const nav = navItems.map((item) => [item.key, item.icon, item.label] as [View, string, string]);
  const navSections: { label: string; keys: View[] }[] = [
    { label: "Teaching", keys: ["dashboard", "controller", "classes", "templates", "activities", "audience", "calendar"] },
    { label: "Media & Devices", keys: ["media", "screens", "signage"] },
    { label: "Administration", keys: ["users", "settings"] },
  ];
  const settingsGroups: { label: string; items: { id: SettingsSection; label: string; visible: boolean }[] }[] = [
    {
      label: "Organization",
      items: [
        { id: "general", label: "General", visible: canManageServiceSettings },
        { id: "accounts", label: "Accounts", visible: canManageAppSettings || canManageServiceSettings },
        { id: "appearance", label: "Appearance", visible: canManageServiceSettings },
      ],
    },
    {
      label: "Content & Devices",
      items: [
        { id: "media", label: "Media & Storage", visible: canManageAppSettings || canManageServiceSettings },
        { id: "playback", label: "Playback & Displays", visible: canManageAppSettings || canManageServiceSettings },
      ],
    },
    {
      label: "Infrastructure",
      items: [
        { id: "network", label: "Network & Remote Access", visible: canManageServiceSettings },
        { id: "integrations", label: "Integrations", visible: canManageAppSettings || canManageServiceSettings },
      ],
    },
    {
      label: "Administration",
      items: [
        { id: "security", label: "Security & Audit", visible: canManageAppSettings || canManageServiceSettings },
        { id: "backup", label: "Backup & Recovery", visible: canManageAppSettings || canManageBackups },
        { id: "system", label: "System & Support", visible: canManageServiceSettings || canManageUpdates },
      ],
    },
  ];
  const openSettings = (section: SettingsSection = "overview") => {
    setSettingsSection(section);
    setSettingsExpanded(true);
    setMobileNavOpen(false);
    setView("settings");
  };
  const renderNavItem = ({ key, icon, label }: { key: View; icon: string; label: string }) => {
    if (key !== "settings") {
      return (
        <button
          key={key}
          type="button"
          className={view === key ? "active" : ""}
          onClick={() => {
            setMobileNavOpen(false);
            setView(key);
          }}
          aria-current={view === key ? "page" : undefined}
        >
          <span className="nav-icon">{icon}</span>
          <span>{label}</span>
        </button>
      );
    }
    return (
      <div key={key} className="settings-nav-item">
        <button
          type="button"
          className={view === key ? "active" : ""}
          onClick={() => {
            if (view !== "settings") openSettings();
            else setSettingsExpanded((current) => !current);
          }}
          aria-current={view === key ? "page" : undefined}
          aria-expanded={view === "settings" ? settingsExpanded : false}
          aria-controls="settings-subnavigation"
        >
          <span className="nav-icon">{icon}</span>
          <span>{label}</span>
          <span className="nav-disclosure" aria-hidden="true">{view === "settings" && settingsExpanded ? "⌃" : "⌄"}</span>
        </button>
        {view === "settings" && settingsExpanded && (
          <nav id="settings-subnavigation" className="settings-subnavigation" aria-label="Settings sections">
            <button
              type="button"
              className={settingsSection === "overview" ? "active" : ""}
              onClick={() => openSettings("overview")}
              aria-current={settingsSection === "overview" ? "page" : undefined}
            >Overview</button>
            {settingsGroups.map((group) => {
              const items = group.items.filter((item) => item.visible);
              if (!items.length) return null;
              return (
                <div key={group.label} className="settings-subgroup">
                  <div className="settings-subgroup-label">{group.label}</div>
                  {items.map((item) => (
                    <button
                      type="button"
                      key={item.id}
                      className={settingsSection === item.id ? "active" : ""}
                      onClick={() => openSettings(item.id)}
                      aria-current={settingsSection === item.id ? "page" : undefined}
                    >{item.label}</button>
                  ))}
                </div>
              );
            })}
          </nav>
        )}
      </div>
    );
  };
  useEffect(() => {
    document.getElementById("main-content")?.focus();
    setMobileNavOpen(false);
  }, [view]);
  useEffect(() => {
    if (view === "settings") setSettingsExpanded(true);
  }, [view]);
  useEffect(() => {
    if (mainContent.current)
      (mainContent.current as HTMLElement & { inert: boolean }).inert = mobileNavOpen;
    if (!mobileNavOpen) {
      if (mobileNavWasOpen.current) mobileMenuButton.current?.focus();
      mobileNavWasOpen.current = false;
      return;
    }
    mobileNavWasOpen.current = true;
    const focusable = () =>
      Array.from(
        sidebar.current?.querySelectorAll<HTMLElement>(
          'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
        ) || [],
      );
    focusable()[0]?.focus();
    const keydown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        setMobileNavOpen(false);
        return;
      }
      if (event.key !== "Tab") return;
      const items = focusable();
      if (!items.length) return;
      const first = items[0];
      const last = items.at(-1)!;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", keydown);
    return () => document.removeEventListener("keydown", keydown);
  }, [mobileNavOpen]);
  return (
    <>
      <a
        className="skip-link"
        href="#main-content"
        onClick={(event) => {
          event.preventDefault();
          setMobileNavOpen(false);
          mainContent.current?.focus();
          mainContent.current?.scrollIntoView();
        }}
      >
        Skip to main content
      </a>
      <div
        className={`app-shell ${view === "controller" ? "controller-mode" : ""} ${view === "signage" ? "signage-mode" : ""}`}
      >
        {view !== "signage" && (
          <header className="mobile-shell-header">
            <BrandMark />
            <button
              ref={mobileMenuButton}
              type="button"
              aria-controls="primary-navigation"
              aria-expanded={mobileNavOpen}
              onClick={() => setMobileNavOpen((current) => !current)}
            >
              <span aria-hidden="true">☰</span>
              Menu
            </button>
          </header>
        )}
        {mobileNavOpen && (
          <button
            type="button"
            className="mobile-nav-backdrop"
            aria-label="Close navigation"
            onClick={() => setMobileNavOpen(false)}
          />
        )}
        <aside
          className={`sidebar ${mobileNavOpen ? "mobile-open" : ""}`}
          id="primary-navigation"
          ref={sidebar}
          aria-label="Primary navigation"
        >
          <div className="brand-lockup inverse">
            <BrandMark />
            <div>
              <strong>LessonCue</strong>
              <span>{bootstrap?.organization || "Local server"}</span>
            </div>
          </div>
          {canUpload && bootstrap && (
            <div className="sidebar-storage-badge">
              <span className="storage-badge-icon">↥</span>
              <div>
                <strong>{formatBytes(bootstrap.storage.remainingBytes)}</strong>
                <small>upload space free</small>
              </div>
            </div>
          )}
          <nav aria-label="LessonCue sections">
            {navSections.map((section) => {
              const sectionItems = navItems.filter((item) => section.keys.includes(item.key));
              if (!sectionItems.length) return null;
              return (
                <div key={section.label} className="nav-section">
                  <div className="nav-section-label">{section.label}</div>
                  {sectionItems.map(renderNavItem)}
                </div>
              );
            })}
          </nav>
          <div className="sidebar-foot">
            <div className="server-online">
              <span className="status-dot" />
              <div>
                <strong>Server online</strong>
                <small>{location.host}</small>
              </div>
            </div>
            <button
              className="account-button"
              onClick={() => setShowProfile(true)}
            >
              {username}
              <span>{role} · Manage account</span>
            </button>
          </div>
        </aside>
        <main
          className="content"
          id="main-content"
          tabIndex={-1}
          ref={mainContent}
        >
          {notice && (
            <div
              className="toast"
              key={notice}
              role="status"
              aria-live="polite"
            >
              <span>{notice}</span>
              <button
                type="button"
                onClick={() => setNotice("")}
                aria-label="Dismiss notification"
              >
                ×
              </button>
            </div>
          )}
          {loading && !bootstrap ? (
            <div className="loading">Loading local data…</div>
          ) : (
            <>
              {bootstrap?.update.updateAvailable && (
                <div className="update-banner" role="status">
                  <div>
                    <strong>
                      LessonCue {bootstrap.update.latestVersion} is available
                    </strong>
                    <span>
                      {canManageUpdates
                        ? "Your server can be updated from Settings."
                        : "An update administrator can install it from Settings."}
                    </span>
                  </div>
                  {canManageUpdates && (
                    <button
                      className="button"
                      onClick={() => openSettings("system")}
                    >
                      Review update
                    </button>
                  )}
                </div>
              )}
              {bootstrap?.backupPolicy &&
                (bootstrap.backupPolicy.overdue ||
                  bootstrap.backupPolicy.lastError) && (
                  <div className="update-banner backup-alert" role="alert">
                    <div>
                      <strong>Scheduled backup needs attention</strong>
                      <span>
                        {bootstrap.backupPolicy.lastError ||
                          "The latest verified recovery copy is overdue."}
                      </span>
                    </div>
                    {canManageBackups && (
                      <button
                        className="button"
                        onClick={() => openSettings("backup")}
                      >
                        Review backups
                      </button>
                    )}
                  </div>
                )}
              <Suspense fallback={<div className="loading workspace-loading">Opening workspace…</div>}>
              {view === "dashboard" && bootstrap && (
                <Dashboard
                  bootstrap={bootstrap}
                  lessons={lessons}
                  screens={screens}
                  onNavigate={setView}
                  canManageUpdates={canManageUpdates}
                />
              )}
              {view === "controller" && bootstrap && (
                <ControllerView
                  screens={screens}
                  lessons={lessons}
                  classes={classes}
                  controllerPinConfigured={bootstrap.controllerPinConfigured}
                  requireLocalRoomControllers={
                    bootstrap.settings.requireLocalRoomControllers
                  }
                  localAddress={bootstrap.httpPort.address}
                  userRole={role}
                  refresh={refresh}
                  notify={setNotice}
                />
              )}
              {view === "classes" && (
                <ClassesView
                  classes={classes}
                  lessons={lessons}
                  media={media}
                  taxonomy={
                    bootstrap?.mediaTaxonomy || { folders: [], tags: [] }
                  }
                  refresh={refresh}
                  notify={setNotice}
                  canUpload={canUpload}
                  storage={bootstrap?.storage}
                  mediaFormats={bootstrap?.mediaFormats}
                  screens={screens}
                  onNavigateScreens={() => setView("screens")}
                  onPresentNow={(lesson) => {
                    history.pushState(null, "", `/controller?lesson=${encodeURIComponent(lesson.id)}`);
                    setView("controller");
                  }}
                  localControllerOrigin={
                    bootstrap?.settings.requireLocalRoomControllers
                      ? bootstrap.httpPort.address
                      : undefined
                  }
                />
              )}
              {view === "templates" && (
                <TemplatesView
                  templates={templates}
                  schedules={schedules}
                  lessons={lessons}
                  classes={classes}
                  refresh={refresh}
                  notify={setNotice}
                />
              )}
              {view === "activities" && <ActivityLibrary />}
              {view === "calendar" && <CalendarView lessons={lessons} />}
              {view === "media" && (
                <MediaView
                  media={media}
                  lessons={lessons}
                  taxonomy={
                    bootstrap?.mediaTaxonomy || { folders: [], tags: [] }
                  }
                  refresh={refresh}
                  notify={setNotice}
                  canUpload={canUpload}
                  storage={bootstrap?.storage}
                  mediaFormats={bootstrap?.mediaFormats}
                  mediaConverters={bootstrap?.mediaConverters}
                />
              )}
              {view === "screens" && bootstrap && (
                <ScreensView
                  screens={screens}
                  classes={classes}
                  signs={signage.filter((item) => item.mode === "sign")}
                  pin={bootstrap.pairingPin}
                  pinExpiresAt={bootstrap.pairingExpiresAt}
                  pinFixed={bootstrap.pairingFixed}
                  refresh={refresh}
                  notify={setNotice}
                  canManage={canManageScreens}
                />
              )}
              {view === "signage" && (
                <SimpleSignage
                  media={media}
                  screens={screens}
                  navigation={nav.map(([key, icon, label]) => ({
                    key,
                    icon,
                    label,
                  }))}
                  onNavigate={(key) => setView(key as View)}
                  refresh={refresh}
                  notify={setNotice}
                />
              )}
              {view === "audience" && canPlan && (
                <AudienceAdmin notify={setNotice} />
              )}
              {view === "users" && (
                <UsersView
                  users={users}
                  currentUsername={currentUsername}
                  currentRole={role}
                  refresh={refresh}
                  notify={setNotice}
                  canManage={canManageUsers}
                  emailConfigured={bootstrap?.accountEmail.configured ?? false}
                />
              )}
              {view === "settings" && bootstrap && (
                <Settings
                  bootstrap={bootstrap}
                  backups={backups}
                  audit={audit}
                  refresh={refresh}
                  notify={setNotice}
                  canAppSettings={canManageAppSettings}
                  canServiceSettings={canManageServiceSettings}
                  canBackups={canManageBackups}
                  canUpdates={canManageUpdates}
                  settingsSection={settingsSection}
                  setSettingsSection={setSettingsSection}
                />
              )}
              </Suspense>
            </>
          )}
        </main>
      </div>
      {showProfile && (
        <ProfileModal
          onClose={() => setShowProfile(false)}
          onLogout={onLogout}
          notify={setNotice}
        />
      )}
    </>
  );
}

export function ProfileModal({
  onClose,
  onLogout,
  notify,
}: {
  onClose: () => void;
  onLogout: () => void;
  notify: (message: string) => void;
}) {
  const [profile, setProfile] = useState<AccountProfile>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    api<AccountProfile>("/api/v1/auth/profile")
      .then(setProfile)
      .catch((cause) => setError(errorText(cause)));
  }, []);
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const values = Object.fromEntries(new FormData(event.currentTarget));
    if (values.newPassword && values.newPassword !== values.confirmPassword) {
      setError("New passwords do not match.");
      setBusy(false);
      return;
    }
    try {
      const result = await api<{ message: string }>("/api/v1/auth/profile", {
        method: "PUT",
        body: JSON.stringify({
          displayName: values.displayName,
          username: values.username,
          email: values.email,
          currentPassword: values.currentPassword || "",
          newPassword: values.newPassword || "",
        }),
      });
      notify(result.message);
      onClose();
      window.setTimeout(() => location.reload(), 350);
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal title="Your account" onClose={onClose}>
      {!profile ? (
        error ? (
          <div className="alert error" role="alert">{error}</div>
        ) : (
          <p className="muted">Loading your account…</p>
        )
      ) : (
        <>
          <form className="stack" onSubmit={save}>
            <div className="account-profile-summary">
              <span>{profile.role}</span>
              <strong>
                {profile.emailVerified
                  ? "Email verified"
                  : "Email verification pending"}
              </strong>
            </div>
            <Field label="Your name">
              <input
                name="displayName"
                defaultValue={profile.displayName}
                required
                autoComplete="name"
              />
            </Field>
            <div className="two-fields">
              <Field label="Username">
                <input
                  name="username"
                  defaultValue={profile.username}
                  required
                  minLength={3}
                  autoComplete="username"
                />
              </Field>
              <Field label="Email (optional for local accounts)">
                <input
                  name="email"
                  type="email"
                  defaultValue={profile.email || ""}
                  autoComplete="email"
                />
              </Field>
            </div>
            <Field
              label="Current password"
              hint="Required only when changing username, email, or password."
            >
              <input
                name="currentPassword"
                type="password"
                autoComplete="current-password"
              />
            </Field>
            <div className="two-fields">
              <Field
                label="New password"
                hint="Leave blank to keep it unchanged."
              >
                <input
                  name="newPassword"
                  type="password"
                  minLength={10}
                  autoComplete="new-password"
                />
              </Field>
              <Field label="Confirm new password">
                <input
                  name="confirmPassword"
                  type="password"
                  minLength={10}
                  autoComplete="new-password"
                />
              </Field>
            </div>
            {error && <div className="alert error" role="alert">{error}</div>}
            <div className="modal-actions split-actions">
              <button className="button danger" type="button" onClick={onLogout}>
                Sign out
              </button>
              <span />
              <button className="button" type="button" onClick={onClose}>
                Cancel
              </button>
              <button className="button primary" disabled={busy}>
                {busy ? "Saving…" : "Save account"}
              </button>
            </div>
          </form>
          <AuthenticatorMfaPanel notify={notify} profile />
        </>
      )}
    </Modal>
  );
}
