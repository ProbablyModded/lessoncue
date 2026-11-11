using LessonCue.Server.Activities;
using LessonCue.Server.Shortener;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

public static class AdminApi
{
    private const string ControllerPinProtectionPurpose = "LessonCue.ControllerPin.v1";
    // Bounded stripes serialize version allocation without retaining a lock
    // forever for every screen a server has ever paired.
    private static readonly SemaphoreSlim[] PlaybackControlGates =
        Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static void MapLessonCueAdmin(this IEndpointRouteBuilder routes, string mediaPath, string dataPath,
        Guid serverId, string serverName)
    {
        routes.MapSignageStudio();
        var api = routes.MapGroup("/api/v1");
        var auth = api.MapGroup("/auth");

        auth.MapGet("/session", async (HttpContext context, LessonCueDb db, AccountEmailService email,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            var hasAccountId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId);
            var mustChangePassword = authenticated && hasAccountId &&
                await db.AdminAccounts.AnyAsync(x => x.Id == accountId && x.MustChangePassword, ct);
            return new
            {
                setupRequired = !await db.AdminAccounts.AnyAsync(ct),
                authenticated,
                username = context.User.Identity?.Name,
                displayName = context.User.FindFirstValue("display_name"),
                role = context.User.FindFirstValue(ClaimTypes.Role),
                permissions = LessonCuePermissions.Effective(context.User),
                mustChangePassword,
                registrationMode = organization.RegistrationMode,
                registrationAvailable = organization.RegistrationMode is "open" or "code" or "approval" &&
                    email.Status(organization.EmailProvider).Configured,
                emailConfigured = email.Status(organization.EmailProvider).Configured
            };
        });

        auth.MapPost("/setup", async (AdminSetupInput input, HttpContext context, LessonCueDb db,
            IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            if (await db.AdminAccounts.AnyAsync(ct)) return Results.Conflict(new { error = "Service Admin setup is already complete." });
            var validation = ValidateCredentials(input.Username, input.Password);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            var setupEmail = NullIfBlank(input.Email)?.ToLowerInvariant();
            if (setupEmail is not null && !IsEmail(setupEmail))
                return Results.BadRequest(new { error = "Enter a valid email address." });

            var account = new AdminAccount { Username = input.Username.Trim().ToLowerInvariant(), PasswordHash = "pending",
                DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? "Administrator" : input.DisplayName.Trim(),
                Email = setupEmail, EmailVerified = true, EmailVerifiedAt = setupEmail is null ? null : DateTimeOffset.UtcNow,
                Role = "Service Admin" };
            account.PasswordHash = hasher.HashPassword(account, input.Password);
            db.AdminAccounts.Add(account);
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (!string.IsNullOrWhiteSpace(input.OrganizationName)) organization.Name = input.OrganizationName.Trim();
            if (!string.IsNullOrWhiteSpace(input.TimeZone)) organization.TimeZone = input.TimeZone.Trim();
            if (!string.IsNullOrWhiteSpace(input.SiteName)) organization.SiteName = input.SiteName.Trim();
            organization.WeekStartsOn = input.WeekStartsOn == "Monday" ? "Monday" : "Sunday";
            db.AuditEvents.Add(new AuditEvent { Actor = account.Username, Action = "admin.setup", Object = account.Id.ToString() });
            await db.SaveChangesAsync(ct);
            await SignInAsync(context, account);
            return Results.Ok(new { account.Username, organization = organization.Name });
        }).RequireRateLimiting("login");

        // The login form uses this anonymous, rate-limited hint to decide whether
        // it should ask for a TOTP code. It never returns account details.
        auth.MapPost("/login/mfa-requirement", async (MfaRequirementInput input, LessonCueDb db,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var username = input.Username?.Trim().ToLowerInvariant() ?? "";
            var accountHasMfa = username.Length > 0 && await db.AdminAccounts.AsNoTracking()
                .Where(account => account.Username == username && !account.Disabled &&
                    !account.PendingApproval && !account.PendingSetup && account.EmailVerified)
                .Select(account => account.TotpEnabled)
                .SingleOrDefaultAsync(ct);
            return Results.Ok(new { required = organization.RequireMfaForAllUsers || accountHasMfa });
        }).RequireRateLimiting("login");

        auth.MapPost("/login", async (AdminLoginInput input, HttpContext context, LessonCueDb db,
            IPasswordHasher<AdminAccount> hasher, AdminMfaService mfa, CancellationToken ct) =>
        {
            var username = input.Username.Trim().ToLowerInvariant();
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Username == username, ct);
            if (account is null || account.Disabled || account.PendingApproval || account.PendingSetup ||
                !account.EmailVerified ||
                hasher.VerifyHashedPassword(account, account.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
            {
                db.AuditEvents.Add(new AuditEvent { Actor = username, Action = "admin.login", Object = "session", Result = "failed" });
                await db.SaveChangesAsync(ct);
                return Results.Unauthorized();
            }
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers || account.TotpEnabled)
            {
                var validation = mfa.Validate(account, input.MfaCode, DateTimeOffset.UtcNow, preventReplay: true);
                if (validation.Success)
                {
                    var claimed = await db.AdminAccounts
                        .Where(value => value.Id == account.Id && value.TotpEnabled &&
                            value.TotpLastCounter < validation.Counter)
                        .ExecuteUpdateAsync(update =>
                            update.SetProperty(value => value.TotpLastCounter, validation.Counter), ct);
                    if (claimed != 1) validation = new TotpValidation(false, 0);
                }
                if (!validation.Success)
                {
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Actor = username, Action = "admin.login.mfa", Object = "session", Result = "failed"
                    });
                    await db.SaveChangesAsync(ct);
                    return Results.Json(new
                    {
                        error = string.IsNullOrWhiteSpace(input.MfaCode)
                            ? "Enter the six-digit authenticator code."
                            : "The authenticator code was not accepted."
                    }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }
            account.LastLoginAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent { Actor = username, Action = "admin.login", Object = "session" });
            await db.SaveChangesAsync(ct);
            await SignInAsync(context, account);
            return Results.Ok(new { account.Username });
        }).RequireRateLimiting("login");

        auth.MapPost("/register", async (RegistrationInput input, HttpRequest request, LessonCueDb db,
            AccountEmailService email, IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "Account registration is unavailable while Authenticator MFA is required for every user." });
            if (organization.RegistrationMode is not ("open" or "code" or "approval"))
                return Results.Json(new { error = "Registration is closed. Ask an administrator to create your account." }, statusCode: 403);
            if (!email.Status(organization.EmailProvider).Configured)
                return Results.Conflict(new { error = "Registration requires administrator-configured email delivery." });
            var validation = ValidateCredentials(input.Username, input.Password);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Trim().Length > 120 || !IsEmail(input.Email))
                return Results.BadRequest(new { error = "A name and valid email address are required." });
            var username = input.Username.Trim().ToLowerInvariant();
            var address = input.Email.Trim().ToLowerInvariant();
            if (await db.AdminAccounts.AnyAsync(x => x.Username == username || x.Email == address, ct))
                return Results.Conflict(new { error = "That username or email address is already registered." });
            RegistrationCode? registrationCode = null;
            if (organization.RegistrationMode == "code")
            {
                var hash = AccountEmailService.Hash(input.Code ?? "");
                var now = DateTimeOffset.UtcNow;
                registrationCode = await db.RegistrationCodes.SingleOrDefaultAsync(x => x.CodeHash == hash && x.RevokedAt == null, ct);
                if (registrationCode is null || registrationCode.ExpiresAt <= now ||
                    registrationCode.MaxUses is int maximum && registrationCode.Uses >= maximum)
                    return Results.BadRequest(new { error = "The registration code is invalid or expired." });
            }
            var account = new AdminAccount
            {
                Username = username, DisplayName = input.DisplayName.Trim(), Email = address, Role = "Viewer",
                PasswordHash = "pending", EmailVerified = false,
                PendingApproval = organization.RegistrationMode == "approval"
            };
            account.PasswordHash = hasher.HashPassword(account, input.Password);
            db.AdminAccounts.Add(account);
            var raw = AccountEmailService.NewToken();
            db.AccountTokens.Add(NewAccountToken(account.Id, "verify", raw, TimeSpan.FromHours(24)));
            db.AuditEvents.Add(new AuditEvent { Actor = username, Action = "account.register", Object = account.Id.ToString() });
            await using var registrationTransaction = registrationCode is null
                ? null
                : await db.Database.BeginTransactionAsync(ct);
            if (registrationCode is not null)
            {
                var consumed = await db.RegistrationCodes.Where(x => x.Id == registrationCode.Id && x.RevokedAt == null &&
                    (x.MaxUses == null || x.Uses < x.MaxUses)).ExecuteUpdateAsync(update =>
                    update.SetProperty(x => x.Uses, x => x.Uses + 1), ct);
                if (consumed != 1) return Results.BadRequest(new { error = "The registration code is invalid or expired." });
            }
            await db.SaveChangesAsync(ct);
            if (registrationTransaction is not null) await registrationTransaction.CommitAsync(ct);
            try
            {
                await SendAccountLinkAsync(email, organization, address, "Verify your LessonCue account",
                    "Verify account", AccountUrl(organization, request, "/verify", raw), ct);
            }
            catch (Exception error)
            {
                return Results.Problem($"The account was created, but verification email could not be sent: {error.Message}",
                    statusCode: 503);
            }
            return Results.Accepted(value: new
            {
                message = account.PendingApproval
                    ? "Check your email to verify the request. An administrator must approve it before you can sign in."
                    : "Check your email to verify the account."
            });
        }).RequireRateLimiting("account");

        auth.MapPost("/verify", async (VerifyAccountInput input, LessonCueDb db, CancellationToken ct) =>
        {
            var token = await FindTokenAsync(db, input.Token, "verify", ct);
            if (token?.Account is null) return Results.BadRequest(new { error = "This verification link is invalid or expired." });
            token.UsedAt = DateTimeOffset.UtcNow;
            token.Account.EmailVerified = true;
            token.Account.EmailVerifiedAt = DateTimeOffset.UtcNow;
            token.Account.SessionVersion++;
            db.AuditEvents.Add(new AuditEvent { Actor = token.Account.Username, Action = "account.verify", Object = token.AccountId.ToString() });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                message = token.Account.PendingApproval
                    ? "Email verified. Your request is waiting for administrator approval."
                    : "Email verified. You can now sign in."
            });
        }).RequireRateLimiting("account");

        auth.MapPost("/verification/resend", async (PasswordRecoveryInput input, HttpRequest request, LessonCueDb db,
            AccountEmailService email, CancellationToken ct) =>
        {
            var address = input.Email.Trim().ToLowerInvariant();
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Email == address && !x.EmailVerified && !x.Disabled, ct);
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (account is not null && email.Status(organization.EmailProvider).Configured)
            {
                await InvalidateTokensAsync(db, account.Id, "verify", ct);
                var raw = AccountEmailService.NewToken();
                db.AccountTokens.Add(NewAccountToken(account.Id, "verify", raw, TimeSpan.FromHours(24)));
                await db.SaveChangesAsync(ct);
                try
                {
                    await SendAccountLinkAsync(email, organization, address, "Verify your LessonCue account",
                        "Verify account", AccountUrl(organization, request, "/verify", raw), ct);
                }
                catch
                {
                    db.AuditEvents.Add(new AuditEvent { Actor = "system", Action = "account.verification.delivery", Object = account.Id.ToString(), Result = "failed" });
                    await db.SaveChangesAsync(ct);
                }
            }
            return Results.Accepted(value: new { message = "If that unverified account exists, a new link has been sent." });
        }).RequireRateLimiting("account");

        auth.MapPost("/password/forgot", async (PasswordRecoveryInput input, HttpRequest request, LessonCueDb db,
            AccountEmailService email, CancellationToken ct) =>
        {
            var address = input.Email.Trim().ToLowerInvariant();
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Email == address && x.EmailVerified && !x.Disabled, ct);
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (account is not null && email.Status(organization.EmailProvider).Configured)
            {
                await InvalidateTokensAsync(db, account.Id, "reset", ct);
                var raw = AccountEmailService.NewToken();
                db.AccountTokens.Add(NewAccountToken(account.Id, "reset", raw, TimeSpan.FromHours(1)));
                await db.SaveChangesAsync(ct);
                try
                {
                    await SendAccountLinkAsync(email, organization, address, "Reset your LessonCue password",
                        "Reset password", AccountUrl(organization, request, "/reset-password", raw), ct);
                }
                catch
                {
                    db.AuditEvents.Add(new AuditEvent { Actor = "system", Action = "account.recovery.delivery", Object = account.Id.ToString(), Result = "failed" });
                    await db.SaveChangesAsync(ct);
                }
            }
            return Results.Accepted(value: new { message = "If that verified account exists, a reset link has been sent." });
        }).RequireRateLimiting("account");

        auth.MapPost("/password/reset", async (PasswordResetInput input, LessonCueDb db,
            IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var token = await FindTokenAsync(db, input.Token, "reset", ct);
            if (token?.Account is null) return Results.BadRequest(new { error = "This reset link is invalid or expired." });
            var validation = AdminCredentialPolicy.Validate(token.Account.Username, input.Password);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            token.Account.PasswordHash = hasher.HashPassword(token.Account, input.Password);
            token.Account.MustChangePassword = false;
            token.Account.SessionVersion++;
            token.UsedAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent { Actor = token.Account.Username, Action = "account.password.reset", Object = token.AccountId.ToString() });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Password changed. Sign in with the new password." });
        }).RequireRateLimiting("account");

        auth.MapPost("/password/change-required", async (RequiredPasswordChangeInput input, HttpContext context,
            LessonCueDb db, IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            if (account is null) return Results.Unauthorized();
            if (!account.MustChangePassword) return Results.Conflict(new { error = "This account does not require a password change." });
            if (hasher.VerifyHashedPassword(account, account.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "The temporary password was not accepted." });
            var validation = AdminCredentialPolicy.Validate(account.Username, input.NewPassword);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            account.PasswordHash = hasher.HashPassword(account, input.NewPassword);
            account.MustChangePassword = false;
            account.SessionVersion++;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = account.Username, Action = "account.password.required-change", Object = account.Id.ToString()
            });
            await db.SaveChangesAsync(ct);
            await SignInAsync(context, account);
            return Results.Ok(new { message = "Password changed." });
        }).RequireAuthorization().RequireRateLimiting("account");

        auth.MapPost("/setup-account", async (AccountSetupInput input, LessonCueDb db,
            IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var token = await FindTokenAsync(db, input.Token, "setup", ct);
            if (token?.Account is null || !token.Account.PendingSetup)
                return Results.BadRequest(new { error = "This account setup link is invalid or expired." });
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "Account setup is unavailable while Authenticator MFA is required for every user." });
            var validation = ValidateCredentials(input.Username, input.Password);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Trim().Length > 120)
                return Results.BadRequest(new { error = "Name is required and cannot exceed 120 characters." });
            var username = input.Username.Trim().ToLowerInvariant();
            if (await db.AdminAccounts.AnyAsync(x => x.Id != token.AccountId && x.Username == username, ct))
                return Results.Conflict(new { error = "That username already exists." });
            token.Account.Username = username;
            token.Account.DisplayName = input.DisplayName.Trim();
            token.Account.PasswordHash = hasher.HashPassword(token.Account, input.Password);
            token.Account.EmailVerified = true;
            token.Account.EmailVerifiedAt = DateTimeOffset.UtcNow;
            token.Account.PendingSetup = false;
            token.Account.MustChangePassword = false;
            token.Account.SessionVersion++;
            token.UsedAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = token.Account.Username, Action = "account.invitation.accept", Object = token.AccountId.ToString()
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Your account is ready. Sign in with the username and password you chose." });
        }).RequireRateLimiting("account");

        auth.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapGet("/profile", async (HttpContext context, LessonCueDb db, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            return account is null ? Results.Unauthorized() : Results.Ok(new
            {
                account.Username, account.DisplayName, account.Email, account.EmailVerified, account.Role
            });
        }).RequireAuthorization();

        auth.MapPut("/profile", async (ProfileUpdateInput input, HttpContext context, HttpRequest request,
            LessonCueDb db, AccountEmailService email, IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            if (account is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Trim().Length > 120)
                return Results.BadRequest(new { error = "Name is required and cannot exceed 120 characters." });
            var usernameValidation = AdminCredentialPolicy.ValidateUsername(input.Username);
            if (usernameValidation is not null) return Results.BadRequest(new { error = usernameValidation });
            var username = input.Username.Trim().ToLowerInvariant();
            var address = NullIfBlank(input.Email)?.ToLowerInvariant();
            var sensitive = username != account.Username || address != account.Email || !string.IsNullOrWhiteSpace(input.NewPassword);
            if (sensitive && (string.IsNullOrWhiteSpace(input.CurrentPassword) ||
                hasher.VerifyHashedPassword(account, account.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed))
                return Results.BadRequest(new { error = "Enter your current password to change sign-in details." });
            if (await db.AdminAccounts.AnyAsync(x => x.Id != account.Id && x.Username == username, ct))
                return Results.Conflict(new { error = "That username already exists." });
            account.DisplayName = input.DisplayName.Trim();
            account.Username = username;
            if (!string.IsNullOrWhiteSpace(input.NewPassword))
            {
                var passwordValidation = AdminCredentialPolicy.Validate(username, input.NewPassword);
                if (passwordValidation is not null) return Results.BadRequest(new { error = passwordValidation });
                account.PasswordHash = hasher.HashPassword(account, input.NewPassword);
                account.MustChangePassword = false;
            }
            var message = "Profile saved.";
            Organization? pendingEmailOrganization = null;
            string? pendingEmailAddress = null;
            string? pendingEmailToken = null;
            if (address != account.Email && address is null)
            {
                account.Email = null;
                account.EmailVerified = true;
                account.EmailVerifiedAt = null;
                await InvalidateTokensAsync(db, account.Id, "email", ct);
            }
            else if (address is not null && address != account.Email)
            {
                if (!IsEmail(address)) return Results.BadRequest(new { error = "Enter a valid email address." });
                if (await db.AdminAccounts.AnyAsync(x => x.Id != account.Id && x.Email == address, ct))
                    return Results.Conflict(new { error = "That email address is already registered." });
                var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
                if (!email.Status(organization.EmailProvider).Configured)
                    return Results.Conflict(new { error = "An administrator must configure email before changing your address." });
                await InvalidateTokensAsync(db, account.Id, "email", ct);
                var raw = AccountEmailService.NewToken();
                db.AccountTokens.Add(NewAccountToken(account.Id, "email", raw, TimeSpan.FromHours(2), address));
                pendingEmailOrganization = organization;
                pendingEmailAddress = address;
                pendingEmailToken = raw;
                message = "Profile saved. Confirm the link sent to your new email address.";
            }
            account.SessionVersion++;
            db.AuditEvents.Add(new AuditEvent { Actor = account.Username, Action = "account.profile.update", Object = account.Id.ToString() });
            await db.SaveChangesAsync(ct);
            await SignInAsync(context, account);
            if (pendingEmailOrganization is not null && pendingEmailAddress is not null && pendingEmailToken is not null)
            {
                try
                {
                    await SendAccountLinkAsync(email, pendingEmailOrganization, pendingEmailAddress,
                        "Confirm your new LessonCue email", "Confirm email",
                        AccountUrl(pendingEmailOrganization, request, "/verify-email", pendingEmailToken), ct);
                }
                catch
                {
                    return Results.Problem("The profile was saved, but the confirmation email could not be sent. Retry the email change after the provider is fixed.",
                        statusCode: 503);
                }
            }
            return Results.Ok(new { message });
        }).RequireAuthorization().RequireRateLimiting("account");

        var mfaRoutes = auth.MapGroup("/mfa").RequireAuthorization();
        mfaRoutes.MapGet("", async (HttpContext context, LessonCueDb db, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            if (account is null) return Results.Unauthorized();
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Ok(new
            {
                enabled = account.TotpEnabled,
                configured = !string.IsNullOrWhiteSpace(account.TotpSecretProtected),
                requiredForAllUsers = organization.RequireMfaForAllUsers,
                requiredForThisAccount = organization.RequireMfaForAllUsers || account.TotpEnabled,
                account.TotpEnabledAt
            });
        });
        mfaRoutes.MapGet("/policy", async (LessonCueDb db, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var activeUsers = EligibleMfaAccounts(db);
            return Results.Ok(new
            {
                requiredForAllUsers = organization.RequireMfaForAllUsers,
                activeUsers = await activeUsers.CountAsync(ct),
                enrolledUsers = await activeUsers.CountAsync(account => account.TotpEnabled &&
                    account.TotpSecretProtected != null && account.TotpSecretProtected != "", ct)
            });
        }).RequireAuthorization(LessonCuePermissions.Settings);
        mfaRoutes.MapPut("/policy", async (MfaPolicyInput input, LessonCueDb db, CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            var activeUsers = EligibleMfaAccounts(db);
            if (input.RequireForAllUsers && !organization.RequireMfaForAllUsers)
            {
                var unconfiguredUsers = await activeUsers.CountAsync(account => !account.TotpEnabled ||
                    account.TotpSecretProtected == null || account.TotpSecretProtected == "", ct);
                if (unconfiguredUsers > 0)
                    return Results.BadRequest(new
                    {
                        error = $"Enroll Authenticator MFA for {unconfiguredUsers} active user{(unconfiguredUsers == 1 ? "" : "s")} before requiring it for everyone.",
                        unconfiguredUsers
                    });
            }
            organization.RequireMfaForAllUsers = input.RequireForAllUsers;
            Audit(db, "organization.mfa.policy", organization.Id,
                input.RequireForAllUsers ? "required-for-all-users" : "per-user-only");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                requiredForAllUsers = organization.RequireMfaForAllUsers,
                activeUsers = await activeUsers.CountAsync(ct),
                enrolledUsers = await activeUsers.CountAsync(account => account.TotpEnabled &&
                    account.TotpSecretProtected != null && account.TotpSecretProtected != "", ct)
            });
        }).RequireAuthorization(LessonCuePermissions.Settings);
        mfaRoutes.MapPost("/setup", async (MfaSetupInput input, HttpContext context, LessonCueDb db,
            IPasswordHasher<AdminAccount> hasher, AdminMfaService mfa, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            if (account is null) return Results.Unauthorized();
            if (hasher.VerifyHashedPassword(account, account.PasswordHash, input.CurrentPassword) ==
                PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "The current password was not accepted." });
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var secret = mfa.CreateSecret();
            account.TotpSecretProtected = secret.ProtectedSecret;
            account.TotpEnabled = false;
            account.TotpLastCounter = 0;
            account.TotpEnabledAt = null;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = account.Username, Action = "account.mfa.setup", Object = account.Id.ToString()
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                secret = secret.Secret,
                provisioningUri = mfa.BuildProvisioningUri(organization.Name, account.Username, secret.Secret)
            });
        }).RequireRateLimiting("account");
        mfaRoutes.MapPost("/enable", async (MfaCodeInput input, HttpContext context, LessonCueDb db,
            AdminMfaService mfa, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            if (account is null) return Results.Unauthorized();
            var validation = mfa.Validate(account, input.Code, DateTimeOffset.UtcNow, preventReplay: false);
            if (!validation.Success)
                return Results.BadRequest(new { error = "The authenticator code was not accepted." });
            account.TotpEnabled = true;
            account.TotpLastCounter = validation.Counter;
            account.TotpEnabledAt = DateTimeOffset.UtcNow;
            account.SessionVersion++;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = account.Username, Action = "account.mfa.enable", Object = account.Id.ToString()
            });
            await db.SaveChangesAsync(ct);
            await SignInAsync(context, account);
            return Results.Ok(new { enabled = true, account.TotpEnabledAt });
        }).RequireRateLimiting("account");
        mfaRoutes.MapDelete("", async ([FromBody] MfaDisableInput input, HttpContext context, LessonCueDb db,
            IPasswordHasher<AdminAccount> hasher, AdminMfaService mfa, CancellationToken ct) =>
        {
            var account = await CurrentAccountAsync(context, db, ct);
            if (account is null) return Results.Unauthorized();
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "An administrator requires Authenticator MFA for every user. Ask them to turn that requirement off first." });
            if (hasher.VerifyHashedPassword(account, account.PasswordHash, input.CurrentPassword) ==
                PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "The current password was not accepted." });
            if (!account.TotpEnabled)
                return Results.BadRequest(new { error = "Authenticator MFA is not enabled." });
            var validation = mfa.Validate(account, input.Code, DateTimeOffset.UtcNow, preventReplay: true);
            if (!validation.Success)
                return Results.BadRequest(new { error = "The authenticator code was not accepted." });
            account.TotpSecretProtected = null;
            account.TotpEnabled = false;
            account.TotpLastCounter = 0;
            account.TotpEnabledAt = null;
            account.SessionVersion++;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = account.Username, Action = "account.mfa.disable", Object = account.Id.ToString()
            });
            await db.SaveChangesAsync(ct);
            await SignInAsync(context, account);
            return Results.Ok(new { enabled = false });
        }).RequireRateLimiting("account");

        auth.MapPost("/email/verify", async (VerifyAccountInput input, HttpContext context, LessonCueDb db,
            CancellationToken ct) =>
        {
            var token = await FindTokenAsync(db, input.Token, "email", ct);
            if (token?.Account is null || string.IsNullOrWhiteSpace(token.PendingEmail))
                return Results.BadRequest(new { error = "This email confirmation link is invalid or expired." });
            if (await db.AdminAccounts.AnyAsync(x => x.Id != token.AccountId && x.Email == token.PendingEmail, ct))
                return Results.Conflict(new { error = "That email address is no longer available." });
            token.Account.Email = token.PendingEmail;
            token.Account.EmailVerified = true;
            token.Account.EmailVerifiedAt = DateTimeOffset.UtcNow;
            token.Account.SessionVersion++;
            token.UsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            if (context.User.Identity?.IsAuthenticated == true) await SignInAsync(context, token.Account);
            return Results.Ok(new { message = "Email address confirmed." });
        }).RequireRateLimiting("account");

        // Controller pages are intentionally public entry points. The room and
        // temporary-session paths scope the returned data; the universal path
        // returns only the PIN gate until the short-lived grant is presented.
        var publicController = api.MapGroup("/controller");
        publicController.MapGet("/bootstrap", async (string? path, bool? liveOnly, HttpContext context,
            LessonCueDb db, ControllerSessionService controllerSessions, HttpPortService httpPort,
            CancellationToken ct) =>
        {
            var requestedPath = path?.Trim() ?? "";
            Guid? classId = null;
            Guid? lessonId = null;
            string? sessionToken = null;

            if (requestedPath.StartsWith("/session/", StringComparison.OrdinalIgnoreCase))
            {
                sessionToken = requestedPath[9..].Split('/')[0];
                var session = await ResolveControllerSessionAsync(sessionToken, controllerSessions, db, ct);
                if (session is null)
                    return Results.NotFound(new { error = "This controller link is invalid, expired, or revoked." });
                classId = session.ClassId;
                lessonId = session.LessonId;
            }
            else if (requestedPath.StartsWith("/room/", StringComparison.OrdinalIgnoreCase))
            {
                var slug = Uri.UnescapeDataString(requestedPath[6..].Split('/')[0]).Trim().ToLowerInvariant();
                var lessonClass = await db.Classes.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ControllerSlug == slug, ct);
                if (lessonClass is null)
                    return Results.NotFound(new { error = "This classroom controller link is invalid." });
                classId = lessonClass.Id;
            }
            else if (!requestedPath.Equals("/controller", StringComparison.OrdinalIgnoreCase) &&
                     !requestedPath.Equals("/universalremote", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Unsupported controller path." });
            }

            // A configured per-class hostname is another room controller entry
            // point and must behave like /room/{slug}, not like the universal
            // remote merely because its visible path is /controller.
            if (classId is null && sessionToken is null)
            {
                var hostname = context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
                if (hostname.Length > 0)
                    classId = await db.Classes.AsNoTracking()
                        .Where(x => x.ControllerHostname == hostname)
                        .Select(x => (Guid?)x.Id)
                        .FirstOrDefaultAsync(ct);
            }

            var universal = classId is null && sessionToken is null;
            var grant = context.Request.Headers["X-LessonCue-Controller-Grant"].ToString();
            var grantValid = !universal || controllerSessions.IsUniversalGrantValid(grant);
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            // Playback acknowledgments need frequent refreshes, but the entire
            // lesson library does not. Invalid grants still clear cached data.
            var libraryIncluded = liveOnly != true || !grantValid;

            var classesQuery = db.Classes.AsNoTracking().AsQueryable();
            if (classId is Guid selectedClassId) classesQuery = classesQuery.Where(x => x.Id == selectedClassId);
            var classes = grantValid && libraryIncluded
                ? (await classesQuery.OrderBy(x => x.Name).Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.Description,
                    x.ControllerSlug,
                    x.ControllerColor,
                    x.ControllerHostname,
                    lessonCount = db.Lessons.Count(lesson => lesson.ClassId == x.Id && !lesson.Archived),
                    screenCount = db.Screens.Count(screen => screen.AssignedClassId == x.Id && !screen.Revoked)
                }).ToListAsync(ct)).Cast<object>().ToArray()
                : Array.Empty<object>();

            var lessonsQuery = db.Lessons.AsNoTracking().Where(x => !x.Archived).AsQueryable();
            if (classId is Guid lessonClassId) lessonsQuery = lessonsQuery.Where(x => x.ClassId == lessonClassId);
            if (lessonId is Guid selectedLessonId) lessonsQuery = lessonsQuery.Where(x => x.Id == selectedLessonId);
            var lessons = grantValid && libraryIncluded
                ? (await lessonsQuery.OrderBy(x => x.Date).Select(x => new
                {
                    x.Id,
                    x.ClassId,
                    className = x.Class!.Name,
                    x.Date,
                    x.Title,
                    x.AvailableFrom,
                    x.ExpiresAt,
                    x.DesignatedStartAt,
                    x.PreRollStartsAt,
                    x.PreRollEnabled,
                    x.CountdownItemId,
                    x.Version,
                    x.Archived,
                    x.KeepOffline,
                    x.DownloadDaysBefore,
                    x.VolumePercent,
                    x.Muted,
                    x.SubstituteNotes,
                    x.PreRollMonitorUrl,
                    x.GeneratedByScheduleId,
                    items = x.Items.OrderBy(item => item.Position).Select(item => new
                    {
                        item.Id,
                        item.Title,
                        item.Type,
                        item.Role,
                        item.Position,
                        item.ActivityDefinitionId,
                        item.MediaAssetId,
                        mediaFileName = item.MediaAsset != null ? item.MediaAsset.FileName : null,
                        item.DurationMs,
                        mediaDurationMs = item.MediaAsset != null ? item.MediaAsset.DurationMs : null,
                        item.StartMs,
                        item.EndMs,
                        item.VolumePercent,
                        item.ImageDurationSeconds,
                        item.EstimatedDurationSeconds,
                        item.EndBehavior,
                        item.AllowSkip,
                        item.Notes,
                        item.FadeInMs,
                        item.FadeOutMs,
                        item.NormalizeAudio,
                        item.CuePointsJson,
                        item.FitMode,
                        item.RotationDegrees,
                        item.CropLeftPercent,
                        item.CropTopPercent,
                        item.CropRightPercent,
                        item.CropBottomPercent,
                        item.Muted,
                        item.PlaybackRatePercent,
                        item.RepeatCount,
                        item.BackgroundColor,
                        item.TransitionStyle,
                        item.TransitionDurationMs,
                        item.FlexibleTime,
                        offlineEligible = item.MediaAsset != null && item.MediaAsset.OfflineEligible
                    }).ToList()
                }).ToListAsync(ct)).Cast<object>().ToArray()
                : Array.Empty<object>();

            var onlineCutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
            var screensQuery = db.Screens.AsNoTracking().Where(x => !x.Revoked).AsQueryable();
            if (classId is Guid screenClassId) screensQuery = screensQuery.Where(x => x.AssignedClassId == screenClassId);
            var screens = grantValid
                ? (await screensQuery.OrderBy(x => x.Name).Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.Platform,
                    x.AssignedClassId,
                    assignedClassName = db.Classes.Where(c => c.Id == x.AssignedClassId).OrderBy(c => c.Name).Select(c => c.Name).FirstOrDefault(),
                    assignedSignageName = db.SignagePlaylists.Where(sign => sign.Id == x.AssignedSignageId)
                        .OrderBy(sign => sign.Name).Select(sign => sign.Name).FirstOrDefault(),
                    x.VolunteerMode,
                    x.SignageOnly,
                    x.PermanentPairing,
                    x.AssignedSignageId,
                    x.LastSeenAt,
                    online = x.LastSeenAt != null && x.LastSeenAt >= onlineCutoff,
                    x.FreeBytes,
                    x.FailedDownloads,
                    x.Revoked,
                    x.AppVersion,
                    x.ManifestVersion,
                    x.TagsCsv,
                    x.Site,
                    x.SignageOrientation,
                    x.SignageWidth,
                    x.SignageHeight,
                    x.ControlVersion,
                    x.ControlAction,
                    x.ControlLessonId,
                    x.ControlItemId,
                    x.ControlPositionMs,
                    x.ControlIssuedAt,
                    x.AcknowledgedControlVersion,
                    x.PlaybackState,
                    x.PlaybackLessonId,
                    x.PlaybackItemId,
                    x.PlaybackPositionMs,
                    x.PlaybackDurationMs,
                    x.PlaybackVolumePercent,
                    x.PlaybackUpdatedAt,
                    x.PlaybackError,
                    x.CachedItems,
                    x.TotalItems,
                    x.DeviceModel,
                    x.OsVersion,
                    x.CacheInventoryJson,
                    x.DownloadQueueJson,
                    x.CodecCapabilitiesJson,
                    x.RecentErrorsJson,
                    x.ConnectionDiagnosticsJson,
                    x.ClockOffsetMs,
                    x.NetworkLatencyMs,
                    x.NetworkQuality,
                    x.DiagnosticsUpdatedAt,
                    x.AllowDiagnosticScreenshots,
                    x.ScreenshotRequestId,
                    x.ScreenshotRequestedAt,
                    x.ScreenshotExpiresAt,
                    x.ScreenshotStatus,
                    x.ScreenshotCapturedAt,
                    screenshotAvailable = x.ScreenshotStatus == "ready" && x.ScreenshotCapturedAt != null &&
                        x.ScreenshotCapturedAt >= DateTimeOffset.UtcNow.AddHours(-24)
                }).ToListAsync(ct)).Cast<object>().ToArray()
                : Array.Empty<object>();

            return Results.Ok(new
            {
                libraryIncluded,
                classes,
                lessons,
                screens,
                controllerPinConfigured = organization.ControllerPinHash is not null,
                requireLocalRoomControllers = organization.RequireLocalRoomControllers,
                localAddress = httpPort.Status.Address
            });
        });

        var admin = api.MapGroup("").RequireAuthorization();
        var planning = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Planning);
        var uploads = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Uploads);
        var playback = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Playback);
        var screens = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Screens);
        var appSettings = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.AppSettings);

        // A redacted, operator-downloadable snapshot for support tickets. It intentionally
        // omits account names, IP addresses, media paths, URLs, secrets, and response text.
        admin.MapGet("/support/bundle", async (LessonCueDb db, SupportBundleBuilder support,
            CancellationToken ct) =>
        {
            var bundle = await support.BuildAsync(db, serverId, serverName, ct);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return Results.File(bytes, "application/json", $"lessoncue-support-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        }).RequireAuthorization(LessonCuePermissions.Settings);

        admin.MapGet("/admin/bootstrap", async (LessonCueDb db, PairingCodeService pairing, StorageService storage,
            UpdateService updates, LocalAddressService localAddress, HttpPortService httpPort,
            CloudflareTunnelService cloudflareTunnel, HardwareAccelerationService hardwareAcceleration,
            ActivityAvailabilityService activityAvailability, YouTubeRuntimeUpdateService youtubeRuntime,
            BackupPolicyService backupPolicy,
            IDataProtectionProvider protection,
            HttpContext context,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var storageStatus = await storage.GetSnapshotAsync(db, ct);
            var canPair = LessonCuePermissions.Has(context.User, LessonCuePermissions.Screens) ||
                LessonCuePermissions.Has(context.User, LessonCuePermissions.AppSettings) ||
                LessonCuePermissions.Has(context.User, LessonCuePermissions.Settings);
            var canManageService = LessonCuePermissions.Has(context.User, LessonCuePermissions.Settings);
            var canManageApp = LessonCuePermissions.Has(context.User, LessonCuePermissions.AppSettings) || canManageService;
            return Results.Ok(new
            {
                serverId,
                serverName,
                organization = organization.Name,
                settings = new
                {
                    organization.Id, organization.Name, organization.TimeZone, organization.SiteName,
                    organization.WeekStartsOn, organization.DefaultLessonDurationMinutes,
                    organization.DefaultRetentionDays,
                    StorageLimitBytes = canManageService ? organization.StorageLimitBytes : 0,
                    organization.PrimaryColor, organization.AccentColor, organization.NavigationTextColor,
                    organization.SelectedTabColor, organization.WelcomeMessage,
                    AdaptiveTranscodingEnabled = canManageService && organization.AdaptiveTranscodingEnabled,
                    TranscodeLeadDays = canManageService ? organization.TranscodeLeadDays : 0,
                    HardwareAccelerationEnabled = canManageService && organization.HardwareAccelerationEnabled,
                    organization.MediaFoldersJson,
                    organization.MediaTagsJson, organization.SignageSourceAllowlistJson, signageEnabled = true,
                    organization.RequireLocalRoomControllers, organization.RequireMfaForAllUsers, organization.RegistrationMode,
                    PublicBaseUrl = canManageService ? organization.PublicBaseUrl : "",
                    EmailFromAddress = canManageService ? organization.EmailFromAddress : "",
                    EmailFromName = canManageService ? organization.EmailFromName : "",
                    EmailProvider = canManageService ? organization.EmailProvider : "none",
                    DailyTroubleshootingEmailEnabled = canManageService && organization.DailyTroubleshootingEmailEnabled,
                    DailyTroubleshootingEmailRecipient = canManageService ? organization.DailyTroubleshootingEmailRecipient : "",
                    DailyTroubleshootingEmailTime = canManageService ? organization.DailyTroubleshootingEmailTime : "07:00",
                    DailyTroubleshootingEmailLastSentAt = canManageService ? organization.DailyTroubleshootingEmailLastSentAt : null,
                    DailyTroubleshootingEmailLastError = canManageService ? organization.DailyTroubleshootingEmailLastError : null,
                    troubleshootingReview = canManageService
                        ? TroubleshootingReviewService.Status(organization, DateTimeOffset.UtcNow)
                        : null
                },
                organization.TimeZone,
                pairingPin = canPair ? pairing.Current : null,
                pairingExpiresAt = canPair ? (DateTimeOffset?)pairing.ExpiresAt : null,
                pairingFixed = canPair && pairing.FixedPin is not null,
                controllerPinConfigured = organization.ControllerPinHash is not null,
                controllerPin = canManageApp ? UnprotectControllerPin(protection, organization.ControllerPinProtected) : null,
                storage = canManageService ? storageStatus : storageStatus with
                {
                    DiskAvailableBytes = 0,
                    MaximumAllocationBytes = 0
                },
                uploadQuotaPolicy = canManageService ? UploadQuotaPolicy.Read(organization) : null,
                mediaTaxonomy = MediaTaxonomy.Read(organization),
                mediaFormats = new
                {
                    accept = MediaFormatCatalog.BrowserAccept,
                    extensions = MediaFormatCatalog.SupportedExtensions,
                    formats = MediaFormatCatalog.All
                },
                mediaConverters = MediaConverterCapabilities.Snapshot(),
                update = updates.Status,
                youtubeRuntime = canManageService || LessonCuePermissions.Has(context.User, LessonCuePermissions.Updates)
                    ? youtubeRuntime.Status
                    : null,
                backupPolicy = canManageService
                    ? backupPolicy.GetStatus(organization.TimeZone)
                    : null,
                localAddress = localAddress.Status,
                httpPort = httpPort.Status,
                cloudflareTunnel = canManageService ? cloudflareTunnel.Status : new CloudflareTunnelStatus(
                    false, null, null, "", false, false, false, false, false, 0,
                    null, null, null, null, null),
                hardwareAcceleration = canManageService ? hardwareAcceleration.Status : new HardwareAccelerationStatus(
                    false, false, "", "", null, null, null, null, null),
                accountEmail = emailStatus(organization),
                // Every role needs this: it decides whether the Activities
                // surfaces render at all, not just who may change the switch.
                activitiesEnabled = activityAvailability.Enabled,
                permissionDefinitions = LessonCuePermissions.All,
                permissionPresets = new
                {
                    serviceAdmin = LessonCuePermissions.Defaults("Service Admin"),
                    appAdmin = LessonCuePermissions.Defaults("App Admin"),
                    editor = LessonCuePermissions.Defaults("Editor"),
                    viewer = LessonCuePermissions.Defaults("Viewer")
                },
                counts = new
                {
                    classes = await db.Classes.CountAsync(ct),
                    lessons = await db.Lessons.CountAsync(ct),
                    media = await db.MediaAssets.CountAsync(ct),
                    screens = await db.Screens.CountAsync(x => !x.Revoked, ct)
                }
            });
            object emailStatus(Organization value)
            {
                var accountEmail = context.RequestServices.GetRequiredService<AccountEmailService>();
                var status = accountEmail.Status(value.EmailProvider);
                return new
                {
                    status.Configured,
                    Provider = canManageService ? status.Provider : "none"
                };
            }
        });

        admin.MapGet("/classes", async (LessonCueDb db, CancellationToken ct) =>
            await db.Classes.AsNoTracking().OrderBy(x => x.Name).Select(x => new
            {
                x.Id,
                x.Name,
                x.Description,
                x.ControllerSlug,
                x.ControllerColor,
                x.ControllerHostname,
                lessonCount = db.Lessons.Count(lesson => lesson.ClassId == x.Id),
                screenCount = db.Screens.Count(screen => screen.AssignedClassId == x.Id && !screen.Revoked)
            }).ToListAsync(ct));

        planning.MapPost("/classes", async (ClassInput input, LessonCueDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Class name is required." });
            var slug = NormalizeControllerSlug(input.ControllerSlug ?? input.Name);
            var error = ValidateControllerAddress(slug, input.ControllerColor, input.ControllerHostname);
            if (error is not null) return Results.BadRequest(new { error });
            if (await db.Classes.AnyAsync(x => x.ControllerSlug == slug, ct))
                return Results.Conflict(new { error = "That controller path is already assigned to another class." });
            var hostname = NormalizeHostname(input.ControllerHostname);
            if (hostname is not null && await db.Classes.AnyAsync(x => x.ControllerHostname == hostname, ct))
                return Results.Conflict(new { error = "That controller hostname is already assigned to another class." });
            var item = new LessonClass { Name = input.Name.Trim(), Description = input.Description?.Trim() ?? "",
                ControllerSlug = slug, ControllerColor = input.ControllerColor?.Trim() ?? "#2d6a4f",
                ControllerHostname = hostname };
            db.Classes.Add(item);
            Audit(db, "class.create", item.Id, item.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/classes/{item.Id}", item);
        });

        planning.MapPut("/classes/{id:guid}", async (Guid id, ClassInput input, LessonCueDb db, CancellationToken ct) =>
        {
            var item = await db.Classes.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Class name is required." });
            var slug = NormalizeControllerSlug(input.ControllerSlug ??
                (string.IsNullOrWhiteSpace(item.ControllerSlug) ? input.Name : item.ControllerSlug));
            var error = ValidateControllerAddress(slug, input.ControllerColor ?? item.ControllerColor, input.ControllerHostname);
            if (error is not null) return Results.BadRequest(new { error });
            if (await db.Classes.AnyAsync(x => x.Id != id && x.ControllerSlug == slug, ct))
                return Results.Conflict(new { error = "That controller path is already assigned to another class." });
            var hostname = NormalizeHostname(input.ControllerHostname);
            if (hostname is not null && await db.Classes.AnyAsync(x => x.Id != id && x.ControllerHostname == hostname, ct))
                return Results.Conflict(new { error = "That controller hostname is already assigned to another class." });
            item.Name = input.Name.Trim();
            item.Description = input.Description?.Trim() ?? "";
            item.ControllerSlug = slug;
            item.ControllerColor = input.ControllerColor?.Trim() ?? item.ControllerColor;
            item.ControllerHostname = hostname;
            Audit(db, "class.update", id, item.Name);
            await db.SaveChangesAsync(ct);
            return Results.Ok(item);
        });

        planning.MapDelete("/classes/{id:guid}", async (Guid id, LessonCueDb db, HttpContext context,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var item = await db.Classes.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;
            var actor = context.User.Identity?.Name ?? "admin";
            item.DeletedAt = now; item.DeletedBy = actor;
            var lessons = await db.Lessons.Where(x => x.ClassId == id).ToListAsync(ct);
            foreach (var lesson in lessons) { lesson.DeletedAt = now; lesson.DeletedBy = actor; }
            Audit(db, "class.recycle", id, $"{item.Name}; {lessons.Count} lessons");
            await db.SaveChangesAsync(ct); await InvalidateAsync(hub, 0, ct);
            return Results.NoContent();
        });

        planning.MapPost("/controller/sessions", async (TemporaryControllerSessionInput input,
            ControllerSessionService controllerSessions, LessonCueDb db, CancellationToken ct) =>
        {
            if (input.ExpiresInMinutes is < 5 or > 10_080)
                return Results.BadRequest(new { error = "Temporary controller duration must be from 5 minutes to 7 days." });
            var lessonClass = await db.Classes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ClassId, ct);
            if (lessonClass is null) return Results.BadRequest(new { error = "Choose an existing class." });
            if (input.LessonId is Guid lessonId && !await db.Lessons.AnyAsync(x => x.Id == lessonId && x.ClassId == input.ClassId && !x.Archived, ct))
                return Results.BadRequest(new { error = "The selected lesson is unavailable in that class." });
            var session = controllerSessions.Create(input.ClassId, input.LessonId, input.ExpiresInMinutes);
            Audit(db, "controller.session.create", input.ClassId, $"expires:{session.ExpiresAt:O};lesson:{input.LessonId}");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/controller/sessions/{session.Token}", new
            {
                session.Token, session.ClassId, session.LessonId, session.ExpiresAt,
                path = $"/session/{session.Token}"
            });
        });

        planning.MapGet("/controller/permanent/{classId:guid}", async (Guid classId, LessonCueDb db,
            CancellationToken ct) =>
        {
            var lessonClass = await db.Classes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == classId, ct);
            if (lessonClass is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(lessonClass.PermanentControllerToken))
                return Results.Ok(new { active = false });
            return Results.Ok(new
            {
                active = true,
                token = lessonClass.PermanentControllerToken,
                classId = lessonClass.Id,
                lessonId = lessonClass.PermanentControllerLessonId,
                createdAt = lessonClass.PermanentControllerCreatedAt,
                path = $"/session/{lessonClass.PermanentControllerToken}",
                permanent = true
            });
        });

        planning.MapPost("/controller/permanent", async (PermanentControllerSessionInput input, LessonCueDb db,
            CancellationToken ct) =>
        {
            var lessonClass = await db.Classes.SingleOrDefaultAsync(x => x.Id == input.ClassId, ct);
            if (lessonClass is null) return Results.BadRequest(new { error = "Choose an existing class." });
            if (input.LessonId is Guid lessonId && !await db.Lessons.AnyAsync(
                    x => x.Id == lessonId && x.ClassId == input.ClassId && !x.Archived, ct))
                return Results.BadRequest(new { error = "The selected lesson is unavailable in that class." });
            lessonClass.PermanentControllerToken =
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            lessonClass.PermanentControllerLessonId = input.LessonId;
            lessonClass.PermanentControllerCreatedAt = DateTimeOffset.UtcNow;
            Audit(db, "controller.permanent.rotate", input.ClassId,
                $"lesson:{input.LessonId};created:{lessonClass.PermanentControllerCreatedAt:O}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                active = true,
                token = lessonClass.PermanentControllerToken,
                classId = lessonClass.Id,
                lessonId = lessonClass.PermanentControllerLessonId,
                createdAt = lessonClass.PermanentControllerCreatedAt,
                path = $"/session/{lessonClass.PermanentControllerToken}",
                permanent = true
            });
        });

        planning.MapDelete("/controller/permanent/{classId:guid}", async (Guid classId, LessonCueDb db,
            CancellationToken ct) =>
        {
            var lessonClass = await db.Classes.SingleOrDefaultAsync(x => x.Id == classId, ct);
            if (lessonClass is null) return Results.NotFound();
            lessonClass.PermanentControllerToken = null;
            lessonClass.PermanentControllerLessonId = null;
            lessonClass.PermanentControllerCreatedAt = null;
            Audit(db, "controller.permanent.revoke", classId, lessonClass.Name);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        playback.MapGet("/controller/sessions/{token}", async (string token,
            ControllerSessionService controllerSessions, LessonCueDb db, CancellationToken ct) =>
        {
            var session = await ResolveControllerSessionAsync(token, controllerSessions, db, ct);
            return session is null ? Results.NotFound(new { error = "This controller link is invalid, expired, or revoked." }) : Results.Ok(new
            {
                session.ClassId,
                session.LessonId,
                expiresAt = session.Permanent ? (DateTimeOffset?)null : session.ExpiresAt,
                session.Permanent
            });
        }).AllowAnonymous();

        admin.MapGet("/lessons", async (Guid? classId, LessonCueDb db, CancellationToken ct) =>
        {
            var query = db.Lessons.AsNoTracking().AsQueryable();
            if (classId is not null) query = query.Where(x => x.ClassId == classId);
            return Results.Ok(await query.OrderBy(x => x.Date).Select(x => new
            {
                x.Id,
                x.ClassId,
                className = x.Class!.Name,
                x.Date,
                x.Title,
                x.AvailableFrom,
                x.ExpiresAt,
                x.DesignatedStartAt,
                x.PreRollStartsAt,
                x.PreRollEnabled,
                x.CountdownItemId,
                x.Version,
                x.Archived,
                x.KeepOffline,
                x.DownloadDaysBefore,
                x.VolumePercent,
                x.Muted,
                x.SubstituteNotes,
                x.PreRollMonitorUrl,
                x.GeneratedByScheduleId,
                items = x.Items.OrderBy(item => item.Position).Select(item => new
                {
                    item.Id,
                    item.Title,
                    item.Type,
                    item.Role,
                    item.Position,
                    item.ActivityDefinitionId,
                    item.MediaAssetId,
                    mediaFileName = item.MediaAsset != null ? item.MediaAsset.FileName : null,
                    item.DurationMs,
                    mediaDurationMs = item.MediaAsset != null ? item.MediaAsset.DurationMs : null,
                    item.StartMs,
                    item.EndMs,
                    item.VolumePercent,
                    item.ImageDurationSeconds,
                    item.EstimatedDurationSeconds,
                    item.EndBehavior,
                    item.AllowSkip,
                    item.Notes,
                    item.FadeInMs,
                    item.FadeOutMs,
                    item.NormalizeAudio,
                    item.CuePointsJson,
                    item.FitMode,
                    item.RotationDegrees,
                    item.CropLeftPercent,
                    item.CropTopPercent,
                    item.CropRightPercent,
                    item.CropBottomPercent,
                    item.Muted,
                    item.PlaybackRatePercent,
                    item.RepeatCount,
                    item.BackgroundColor,
                    item.TransitionStyle,
                    item.TransitionDurationMs,
                    item.FlexibleTime,
                    offlineEligible = item.MediaAsset != null && item.MediaAsset.OfflineEligible
                }).ToList()
            }).ToListAsync(ct));
        });

        planning.MapPost("/lessons", async (LessonInput input, LessonCueDb db, CancellationToken ct) =>
        {
            if (!await db.Classes.AnyAsync(x => x.Id == input.ClassId, ct)) return Results.BadRequest(new { error = "Class does not exist." });
            if (string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest(new { error = "Lesson title is required." });
            if (input.VolumePercent is < 0 or > 150) return Results.BadRequest(new { error = "Lesson volume must be from 0 to 150." });
            if ((input.SubstituteNotes?.Length ?? 0) > 8000) return Results.BadRequest(new { error = "Substitute notes may contain at most 8,000 characters." });
            if (!TryMonitorUrl(input.PreRollMonitorUrl, out var monitorUrl)) return Results.BadRequest(new { error = "The pre-roll monitor must be a complete HTTP or HTTPS URL." });
            var lesson = new Lesson
            {
                ClassId = input.ClassId,
                Date = input.Date,
                Title = input.Title.Trim(),
                AvailableFrom = input.AvailableFrom,
                ExpiresAt = input.ExpiresAt,
                DesignatedStartAt = input.DesignatedStartAt,
                PreRollStartsAt = input.PreRollStartsAt,
                PreRollEnabled = input.PreRollEnabled,
                CountdownItemId = input.CountdownItemId,
                VolumePercent = input.VolumePercent,
                Muted = input.Muted,
                SubstituteNotes = input.SubstituteNotes?.Trim() ?? "",
                PreRollMonitorUrl = monitorUrl
            };
            db.Lessons.Add(lesson);
            Audit(db, "lesson.create", lesson.Id, lesson.Title);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/lessons/{lesson.Id}", lesson);
        });

        planning.MapPut("/lessons/{id:guid}", async (Guid id, LessonUpdateInput input, LessonCueDb db,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var lesson = await db.Lessons.FindAsync([id], ct);
            if (lesson is null) return Results.NotFound();
            if (input.Title is not null) lesson.Title = input.Title.Trim();
            if (input.Date is not null) lesson.Date = input.Date.Value;
            if (input.ClearAvailableFrom) lesson.AvailableFrom = null;
            else if (input.AvailableFrom is not null) lesson.AvailableFrom = input.AvailableFrom;
            if (input.ClearExpiresAt) lesson.ExpiresAt = null;
            else if (input.ExpiresAt is not null) lesson.ExpiresAt = input.ExpiresAt;
            if (input.ClearDesignatedStartAt) lesson.DesignatedStartAt = null;
            else if (input.DesignatedStartAt is not null) lesson.DesignatedStartAt = input.DesignatedStartAt;
            if (input.ClearPreRollStartsAt) lesson.PreRollStartsAt = null;
            else if (input.PreRollStartsAt is not null) lesson.PreRollStartsAt = input.PreRollStartsAt;
            if (input.PreRollEnabled is not null) lesson.PreRollEnabled = input.PreRollEnabled.Value;
            if (input.VolumePercent is not null)
            {
                if (input.VolumePercent is < 0 or > 150) return Results.BadRequest(new { error = "Lesson volume must be from 0 to 150." });
                lesson.VolumePercent = input.VolumePercent.Value;
            }
            if (input.Muted is not null) lesson.Muted = input.Muted.Value;
            if (input.SubstituteNotes is not null)
            {
                if (input.SubstituteNotes.Length > 8000) return Results.BadRequest(new { error = "Substitute notes may contain at most 8,000 characters." });
                lesson.SubstituteNotes = input.SubstituteNotes.Trim();
            }
            if (input.ClearPreRollMonitorUrl) lesson.PreRollMonitorUrl = null;
            else if (input.PreRollMonitorUrl is not null)
            {
                if (!TryMonitorUrl(input.PreRollMonitorUrl, out var monitorUrl)) return Results.BadRequest(new { error = "The pre-roll monitor must be a complete HTTP or HTTPS URL." });
                lesson.PreRollMonitorUrl = monitorUrl;
            }
            if (input.ClearCountdown) lesson.CountdownItemId = null;
            else if (input.CountdownItemId is not null) lesson.CountdownItemId = input.CountdownItemId;
            lesson.Version++;
            Audit(db, "lesson.update", lesson.Id, lesson.Title);
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, lesson.Version, ct);
            return Results.Ok(lesson);
        });

        planning.MapDelete("/lessons/{id:guid}", async (Guid id, LessonCueDb db, HttpContext context, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var lesson = await db.Lessons.FindAsync([id], ct);
            if (lesson is null) return Results.NotFound();
            lesson.DeletedAt = DateTimeOffset.UtcNow; lesson.DeletedBy = context.User.Identity?.Name ?? "admin";
            Audit(db, "lesson.recycle", id, lesson.Title);
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, 0, ct);
            return Results.NoContent();
        });

        planning.MapPost("/lessons/{id:guid}/duplicate", async (Guid id, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var source = await db.Lessons.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (source is null) return Results.NotFound();
            var timeZone = await db.Organizations.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => item.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
            var copy = new Lesson
            {
                ClassId = source.ClassId, Date = source.Date.AddDays(7), Title = source.Title + " copy",
                AvailableFrom = LessonScheduleService.ShiftWallClock(source.AvailableFrom, 7, timeZone),
                ExpiresAt = LessonScheduleService.ShiftWallClock(source.ExpiresAt, 7, timeZone),
                DesignatedStartAt = LessonScheduleService.ShiftWallClock(source.DesignatedStartAt, 7, timeZone),
                PreRollEnabled = source.PreRollEnabled,
                PreRollStartsAt = LessonScheduleService.ShiftWallClock(source.PreRollStartsAt, 7, timeZone),
                KeepOffline = source.KeepOffline, DownloadDaysBefore = source.DownloadDaysBefore,
                VolumePercent = source.VolumePercent, Muted = source.Muted,
                SubstituteNotes = source.SubstituteNotes, PreRollMonitorUrl = source.PreRollMonitorUrl
            };
            foreach (var sourceItem in source.Items.OrderBy(x => x.Position))
            {
                var clone = new PlaylistItem
                {
                    LessonId = copy.Id, Title = sourceItem.Title, Type = sourceItem.Type, Role = sourceItem.Role,
                    Position = sourceItem.Position, MediaAssetId = sourceItem.MediaAssetId, DurationMs = sourceItem.DurationMs,
                    StartMs = sourceItem.StartMs, EndMs = sourceItem.EndMs, VolumePercent = sourceItem.VolumePercent,
                    ImageDurationSeconds = sourceItem.ImageDurationSeconds, EstimatedDurationSeconds = sourceItem.EstimatedDurationSeconds,
                    EndBehavior = sourceItem.EndBehavior,
                    AllowSkip = sourceItem.AllowSkip, Notes = sourceItem.Notes, FadeInMs = sourceItem.FadeInMs,
                    FadeOutMs = sourceItem.FadeOutMs, NormalizeAudio = sourceItem.NormalizeAudio,
                    CuePointsJson = sourceItem.CuePointsJson, FitMode = sourceItem.FitMode,
                    RotationDegrees = sourceItem.RotationDegrees, CropLeftPercent = sourceItem.CropLeftPercent,
                    CropTopPercent = sourceItem.CropTopPercent, CropRightPercent = sourceItem.CropRightPercent,
                    CropBottomPercent = sourceItem.CropBottomPercent, Muted = sourceItem.Muted,
                    PlaybackRatePercent = sourceItem.PlaybackRatePercent, RepeatCount = sourceItem.RepeatCount,
                    BackgroundColor = sourceItem.BackgroundColor, TransitionStyle = sourceItem.TransitionStyle,
                    TransitionDurationMs = sourceItem.TransitionDurationMs, FlexibleTime = sourceItem.FlexibleTime,
                    ActivityDefinitionId = sourceItem.ActivityDefinitionId
                };
                copy.Items.Add(clone);
                if (sourceItem.Id == source.CountdownItemId || sourceItem.Role == "countdown") copy.CountdownItemId = clone.Id;
            }
            var copiedMediaIds = copy.Items.Where(x => x.MediaAssetId != null).Select(x => x.MediaAssetId!.Value).Distinct().ToList();
            var copiedMedia = await db.MediaAssets.Where(x => copiedMediaIds.Contains(x.Id) && x.StoragePolicy == MediaRetention.LessonScoped).ToListAsync(ct);
            foreach (var media in copiedMedia) MediaRetention.KeepForLesson(media, copy);
            db.Lessons.Add(copy); Audit(db, "lesson.duplicate", copy.Id, copy.Title);
            await db.SaveChangesAsync(ct); await InvalidateAsync(hub, copy.Version, ct);
            return Results.Created($"/api/v1/lessons/{copy.Id}", new { copy.Id });
        });

        planning.MapPost("/lessons/{id:guid}/relocate", async (Guid id, LessonRelocateInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var action = input.Action.Trim().ToLowerInvariant();
            if (action is not ("copy" or "move")) return Results.BadRequest(new { error = "Choose copy or move." });
            if (!await db.Classes.AnyAsync(x => x.Id == input.ClassId, ct)) return Results.BadRequest(new { error = "Choose an existing destination class." });
            var source = await db.Lessons.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (source is null) return Results.NotFound();
            var timeZone = await db.Organizations.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => item.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
            var title = string.IsNullOrWhiteSpace(input.Title) ? source.Title : input.Title.Trim();
            if (title.Length > 160) return Results.BadRequest(new { error = "Lesson title may contain at most 160 characters." });
            var shiftDays = input.Date.DayNumber - source.Date.DayNumber;
            if (action == "move")
            {
                source.ClassId = input.ClassId; source.Date = input.Date; source.Title = title;
                source.AvailableFrom = LessonScheduleService.ShiftWallClock(source.AvailableFrom, shiftDays, timeZone);
                source.ExpiresAt = LessonScheduleService.ShiftWallClock(source.ExpiresAt, shiftDays, timeZone);
                source.DesignatedStartAt = LessonScheduleService.ShiftWallClock(source.DesignatedStartAt, shiftDays, timeZone);
                source.PreRollStartsAt = LessonScheduleService.ShiftWallClock(source.PreRollStartsAt, shiftDays, timeZone);
                source.GeneratedByScheduleId = null; source.Version++;
                var mediaIds = source.Items.Where(x => x.MediaAssetId != null).Select(x => x.MediaAssetId!.Value).Distinct().ToList();
                var media = await db.MediaAssets.Where(x => mediaIds.Contains(x.Id) && x.StoragePolicy == MediaRetention.LessonScoped).ToListAsync(ct);
                foreach (var asset in media) MediaRetention.KeepForLesson(asset, source);
                Audit(db, "lesson.move", source.Id, $"{source.Title}:{source.Date:yyyy-MM-dd}");
                await db.SaveChangesAsync(ct); await InvalidateAsync(hub, source.Version, ct);
                return Results.Ok(new { source.Id, action });
            }

            var copy = new Lesson
            {
                ClassId = input.ClassId, Date = input.Date, Title = title,
                AvailableFrom = LessonScheduleService.ShiftWallClock(source.AvailableFrom, shiftDays, timeZone),
                ExpiresAt = LessonScheduleService.ShiftWallClock(source.ExpiresAt, shiftDays, timeZone),
                DesignatedStartAt = LessonScheduleService.ShiftWallClock(source.DesignatedStartAt, shiftDays, timeZone),
                PreRollStartsAt = LessonScheduleService.ShiftWallClock(source.PreRollStartsAt, shiftDays, timeZone),
                PreRollEnabled = source.PreRollEnabled, KeepOffline = source.KeepOffline, DownloadDaysBefore = source.DownloadDaysBefore,
                VolumePercent = source.VolumePercent, Muted = source.Muted, SubstituteNotes = source.SubstituteNotes,
                PreRollMonitorUrl = source.PreRollMonitorUrl
            };
            foreach (var sourceItem in source.Items.OrderBy(x => x.Position))
            {
                var clone = ClonePlaylistItem(sourceItem, copy.Id);
                copy.Items.Add(clone);
                if (sourceItem.Id == source.CountdownItemId || sourceItem.Role == "countdown") copy.CountdownItemId = clone.Id;
            }
            var copyMediaIds = copy.Items.Where(x => x.MediaAssetId != null).Select(x => x.MediaAssetId!.Value).Distinct().ToList();
            var copyMedia = await db.MediaAssets.Where(x => copyMediaIds.Contains(x.Id) && x.StoragePolicy == MediaRetention.LessonScoped).ToListAsync(ct);
            foreach (var asset in copyMedia) MediaRetention.KeepForLesson(asset, copy);
            db.Lessons.Add(copy); Audit(db, "lesson.copy", copy.Id, $"{copy.Title}:{copy.Date:yyyy-MM-dd}");
            await db.SaveChangesAsync(ct); await InvalidateAsync(hub, copy.Version, ct);
            return Results.Created($"/api/v1/lessons/{copy.Id}", new { copy.Id, action });
        });

        planning.MapPost("/lessons/{id:guid}/archive", async (Guid id, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var lesson = await db.Lessons.FindAsync([id], ct);
            if (lesson is null) return Results.NotFound();
            lesson.Archived = !lesson.Archived; lesson.Version++;
            Audit(db, lesson.Archived ? "lesson.archive" : "lesson.restore", id, lesson.Title);
            await db.SaveChangesAsync(ct); await InvalidateAsync(hub, lesson.Version, ct);
            return Results.Ok(new { lesson.Archived });
        });

        planning.MapPost("/lessons/bulk", async (LessonBulkInput input, LessonCueDb db,
            HttpContext context, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            if (input.LessonIds is null || string.IsNullOrWhiteSpace(input.Action))
                return Results.BadRequest(new { error = "Select lessons and choose an action." });
            var ids = input.LessonIds.Distinct().ToList();
            if (input.LessonIds.Count > 500 || ids.Count is 0 or > 500) return Results.BadRequest(new { error = "Select between 1 and 500 lessons." });
            var lessons = await db.Lessons.Include(x => x.Items).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            if (lessons.Count != ids.Count) return Results.BadRequest(new { error = "One or more selected lessons no longer exist." });
            var action = input.Action.Trim().ToLowerInvariant();
            switch (action)
            {
                case "archive":
                case "restore":
                    foreach (var lesson in lessons) { lesson.Archived = action == "archive"; lesson.Version++; }
                    break;
                case "delete":
                    foreach (var lesson in lessons) { lesson.DeletedAt = DateTimeOffset.UtcNow; lesson.DeletedBy = context.User.Identity?.Name ?? "admin"; }
                    break;
                case "move":
                    if (input.ClassId is not Guid classId || !await db.Classes.AnyAsync(x => x.Id == classId, ct))
                        return Results.BadRequest(new { error = "Choose an existing destination class." });
                    foreach (var lesson in lessons) { lesson.ClassId = classId; lesson.GeneratedByScheduleId = null; lesson.Version++; }
                    break;
                case "shift":
                    if (input.ShiftDays is not int shiftDays || shiftDays is < -3650 or > 3650 || shiftDays == 0)
                        return Results.BadRequest(new { error = "Enter a non-zero date shift between -3650 and 3650 days." });
                    var timeZone = await db.Organizations.AsNoTracking().OrderBy(item => item.Id)
                        .Select(item => item.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
                    foreach (var lesson in lessons)
                    {
                        lesson.Date = lesson.Date.AddDays(shiftDays);
                        lesson.AvailableFrom = LessonScheduleService.ShiftWallClock(lesson.AvailableFrom, shiftDays, timeZone);
                        lesson.ExpiresAt = LessonScheduleService.ShiftWallClock(lesson.ExpiresAt, shiftDays, timeZone);
                        lesson.DesignatedStartAt = LessonScheduleService.ShiftWallClock(lesson.DesignatedStartAt, shiftDays, timeZone);
                        lesson.PreRollStartsAt = LessonScheduleService.ShiftWallClock(lesson.PreRollStartsAt, shiftDays, timeZone);
                        lesson.GeneratedByScheduleId = null;
                        lesson.Version++;
                    }
                    var mediaIds = lessons.SelectMany(x => x.Items).Where(x => x.MediaAssetId != null)
                        .Select(x => x.MediaAssetId!.Value).Distinct().ToList();
                    var media = await db.MediaAssets.Where(x => mediaIds.Contains(x.Id) && x.StoragePolicy == MediaRetention.LessonScoped).ToListAsync(ct);
                    foreach (var asset in media)
                        foreach (var lesson in lessons.Where(x => x.Items.Any(item => item.MediaAssetId == asset.Id)))
                            MediaRetention.KeepForLesson(asset, lesson);
                    break;
                case "prefix-title":
                    var prefix = input.TitlePrefix?.Trim();
                    if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 80)
                        return Results.BadRequest(new { error = "Enter a title prefix of 1 to 80 characters." });
                    foreach (var lesson in lessons)
                    {
                        lesson.Title = (prefix + " " + lesson.Title).Trim();
                        if (lesson.Title.Length > 160) lesson.Title = lesson.Title[..160].TrimEnd();
                        lesson.Version++;
                    }
                    break;
                default:
                    return Results.BadRequest(new { error = "Unsupported bulk lesson action." });
            }
            Audit(db, $"lesson.bulk.{action}", lessons[0].Id, $"{lessons.Count} lessons");
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, lessons.Max(x => x.Version), ct);
            return Results.Ok(new { updated = lessons.Count, action });
        });

        admin.MapGet("/lesson-templates", async (LessonCueDb db, CancellationToken ct) =>
            Results.Ok(await db.LessonTemplates.AsNoTracking().OrderBy(x => x.Name).Select(x => new
            {
                x.Id, x.Name, x.Description, x.DefaultTitle, x.DefaultStartMinutes, x.PreRollLeadMinutes,
                x.AvailableLeadMinutes, x.ExpiresAfterMinutes, x.PreRollEnabled, x.KeepOffline,
                x.DownloadDaysBefore, x.VolumePercent, x.Muted, x.SubstituteNotes, x.CreatedAt, x.UpdatedAt,
                scheduleCount = x.Schedules.Count,
                items = x.Items.OrderBy(item => item.Position).Select(item => new
                {
                    item.Id, item.Title, item.Type, item.Role, item.Position, item.ActivityDefinitionId, item.MediaAssetId,
                    mediaFileName = item.MediaAsset != null ? item.MediaAsset.FileName : null,
                    item.DurationMs, item.StartMs, item.EndMs, item.VolumePercent, item.ImageDurationSeconds, item.EstimatedDurationSeconds,
                    item.EndBehavior, item.AllowSkip, item.Notes, item.FadeInMs, item.FadeOutMs,
                    item.NormalizeAudio, item.CuePointsJson, item.FitMode, item.RotationDegrees,
                    item.CropLeftPercent, item.CropTopPercent, item.CropRightPercent, item.CropBottomPercent,
                    item.Muted, item.PlaybackRatePercent, item.RepeatCount, item.BackgroundColor,
                    item.TransitionStyle, item.TransitionDurationMs, item.FlexibleTime
                })
            }).ToListAsync(ct)));

        planning.MapPost("/lesson-templates/from-lesson", async (LessonTemplateFromLessonInput input,
            LessonCueDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Template name is required." });
            var template = await LessonScheduleService.CreateTemplateFromLessonAsync(db, input.LessonId,
                input.Name, input.Description ?? "", ct);
            return template is null ? Results.NotFound(new { error = "The source lesson was not found." }) :
                Results.Created($"/api/v1/lesson-templates/{template.Id}", new { template.Id });
        });

        planning.MapPut("/lesson-templates/{id:guid}", async (Guid id, LessonTemplateUpdateInput input,
            LessonCueDb db, CancellationToken ct) =>
        {
            var template = await db.LessonTemplates.FindAsync([id], ct);
            if (template is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Template name is required." });
            if (input.DefaultStartMinutes is < 0 or > 1439 || input.PreRollLeadMinutes is < 0 or > 1440 ||
                input.DownloadDaysBefore is < 0 or > 365)
                return Results.BadRequest(new { error = "Template timing is outside the supported range." });
            template.Name = input.Name.Trim(); template.Description = input.Description?.Trim() ?? "";
            template.DefaultTitle = string.IsNullOrWhiteSpace(input.DefaultTitle) ? template.Name : input.DefaultTitle.Trim();
            template.DefaultStartMinutes = input.DefaultStartMinutes; template.PreRollLeadMinutes = input.PreRollLeadMinutes;
            template.PreRollEnabled = input.PreRollEnabled; template.KeepOffline = input.KeepOffline;
            template.DownloadDaysBefore = input.DownloadDaysBefore;
            if (input.VolumePercent is not null)
            {
                if (input.VolumePercent is < 0 or > 150) return Results.BadRequest(new { error = "Template volume must be from 0 to 150." });
                template.VolumePercent = input.VolumePercent.Value;
            }
            if (input.Muted is not null) template.Muted = input.Muted.Value;
            template.UpdatedAt = DateTimeOffset.UtcNow;
            Audit(db, "template.update", id, template.Name); await db.SaveChangesAsync(ct);
            return Results.Ok(new { template.Id });
        });

        planning.MapPost("/lesson-templates/{id:guid}/replace-from-lesson", async (Guid id,
            LessonTemplateReplaceInput input, LessonCueDb db, CancellationToken ct) =>
        {
            var replaced = await LessonScheduleService.ReplaceTemplateFromLessonAsync(db, id, input.LessonId, "admin", ct);
            return replaced ? Results.Ok(new { id }) : Results.NotFound(new { error = "The template or source lesson was not found." });
        });

        planning.MapDelete("/lesson-templates/{id:guid}", async (Guid id, LessonCueDb db, CancellationToken ct) =>
        {
            var template = await db.LessonTemplates.FindAsync([id], ct);
            if (template is null) return Results.NotFound();
            db.LessonTemplates.Remove(template); Audit(db, "template.delete", id, template.Name);
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        planning.MapPost("/lesson-templates/{id:guid}/instantiate", async (Guid id, LessonTemplateInstantiateInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            if (input.StartMinutes is < 0 or > 1439) return Results.BadRequest(new { error = "Start time is invalid." });
            var lesson = await LessonScheduleService.InstantiateAsync(db, id, input.ClassId, input.Date,
                input.Title, input.StartMinutes, null, "admin", ct);
            if (lesson is null) return Results.NotFound(new { error = "The template or class was not found." });
            await InvalidateAsync(hub, lesson.Version, ct);
            return Results.Created($"/api/v1/lessons/{lesson.Id}", new { lesson.Id });
        });

        admin.MapGet("/recurring-schedules", async (LessonCueDb db, CancellationToken ct) =>
            Results.Ok(await db.RecurringLessonSchedules.AsNoTracking().OrderBy(x => x.Name).Select(x => new
            {
                x.Id, x.TemplateId, templateName = x.Template!.Name, x.ClassId, className = x.Class!.Name,
                x.Name, x.Frequency, x.Interval, x.DayOfWeek, x.DayOfMonth, x.StartDate, x.EndDate,
                x.StartMinutes, x.TitlePattern, x.CustomDatesJson, x.ExcludedDatesJson, x.Enabled,
                x.GenerateDaysAhead, x.LastGeneratedAt, x.CreatedAt, x.UpdatedAt,
                generatedCount = db.Lessons.Count(lesson => lesson.GeneratedByScheduleId == x.Id)
            }).ToListAsync(ct)));

        planning.MapPost("/recurring-schedules", async (RecurringScheduleInput input, LessonCueDb db,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var error = ValidateSchedule(input);
            if (error is not null) return Results.BadRequest(new { error });
            if (!await db.LessonTemplates.AnyAsync(x => x.Id == input.TemplateId, ct) ||
                !await db.Classes.AnyAsync(x => x.Id == input.ClassId, ct))
                return Results.BadRequest(new { error = "The selected template or class does not exist." });
            var schedule = new RecurringLessonSchedule { Name = input.Name.Trim() }; ApplySchedule(schedule, input);
            db.RecurringLessonSchedules.Add(schedule); Audit(db, "schedule.create", schedule.Id, schedule.Name);
            await db.SaveChangesAsync(ct);
            var created = schedule.Enabled ? await LessonScheduleService.GenerateAsync(db, schedule.Id, null, "admin", ct) : 0;
            if (created > 0) await InvalidateAsync(hub, created, ct);
            return Results.Created($"/api/v1/recurring-schedules/{schedule.Id}", new { schedule.Id, generated = created });
        });

        planning.MapPut("/recurring-schedules/{id:guid}", async (Guid id, RecurringScheduleInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var error = ValidateSchedule(input);
            if (error is not null) return Results.BadRequest(new { error });
            var schedule = await db.RecurringLessonSchedules.FindAsync([id], ct);
            if (schedule is null) return Results.NotFound();
            if (!await db.LessonTemplates.AnyAsync(x => x.Id == input.TemplateId, ct) ||
                !await db.Classes.AnyAsync(x => x.Id == input.ClassId, ct))
                return Results.BadRequest(new { error = "The selected template or class does not exist." });
            ApplySchedule(schedule, input); Audit(db, "schedule.update", id, schedule.Name); await db.SaveChangesAsync(ct);
            var created = schedule.Enabled ? await LessonScheduleService.GenerateAsync(db, schedule.Id, null, "admin", ct) : 0;
            if (created > 0) await InvalidateAsync(hub, created, ct);
            return Results.Ok(new { schedule.Id, generated = created });
        });

        planning.MapDelete("/recurring-schedules/{id:guid}", async (Guid id, LessonCueDb db, CancellationToken ct) =>
        {
            var schedule = await db.RecurringLessonSchedules.FindAsync([id], ct);
            if (schedule is null) return Results.NotFound();
            db.RecurringLessonSchedules.Remove(schedule); Audit(db, "schedule.delete", id, schedule.Name);
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        planning.MapPost("/recurring-schedules/{id:guid}/generate", async (Guid id,
            RecurringScheduleGenerateInput input, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var created = await LessonScheduleService.GenerateAsync(db, id, input.ThroughDate, "admin", ct);
            if (created < 0) return Results.NotFound();
            if (created > 0) await InvalidateAsync(hub, created, ct);
            return Results.Ok(new { generated = created });
        });

        planning.MapPost("/recurring-schedules/{id:guid}/exception", async (Guid id,
            RecurringScheduleExceptionInput input, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var schedule = await db.RecurringLessonSchedules.FindAsync([id], ct);
            if (schedule is null) return Results.NotFound();
            var dates = LessonScheduleService.ParseDates(schedule.ExcludedDatesJson);
            if (input.Excluded) dates.Add(input.Date); else dates.Remove(input.Date);
            schedule.ExcludedDatesJson = LessonScheduleService.SerializeDates(dates);
            schedule.UpdatedAt = DateTimeOffset.UtcNow;
            var removed = 0;
            if (input.Excluded)
            {
                var generated = await db.Lessons.SingleOrDefaultAsync(x => x.GeneratedByScheduleId == id && x.Date == input.Date, ct);
                if (generated is not null) { db.Lessons.Remove(generated); removed = 1; }
            }
            Audit(db, input.Excluded ? "schedule.exception.add" : "schedule.exception.remove", id, input.Date.ToString("yyyy-MM-dd"));
            await db.SaveChangesAsync(ct);
            var created = !input.Excluded && schedule.Enabled
                ? await LessonScheduleService.GenerateAsync(db, id, input.Date, "admin", ct) : 0;
            if (removed > 0 || created > 0) await InvalidateAsync(hub, created, ct);
            return Results.Ok(new { schedule.ExcludedDatesJson, removed, generated = created });
        });

        planning.MapPost("/lessons/{lessonId:guid}/items", async (Guid lessonId, PlaylistItemInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var lesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == lessonId, ct);
            if (lesson is null) return Results.NotFound();
            if (input.VolumePercent is < 0 or > 150) return Results.BadRequest(new { error = "Volume must be from 0 to 150." });
            if (string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest(new { error = "A display title is required." });
            var playbackError = ValidatePlaybackSettings(input.FitMode ?? "fit", input.RotationDegrees,
                input.CropLeftPercent, input.CropTopPercent, input.CropRightPercent, input.CropBottomPercent,
                input.PlaybackRatePercent, input.RepeatCount, input.BackgroundColor ?? "#000000",
                input.TransitionStyle ?? "cut", input.TransitionDurationMs);
            if (playbackError is not null) return Results.BadRequest(new { error = playbackError });
            var role = NormalizeRole(input.Role);
            var endBehavior = input.EndBehavior ?? DefaultEndBehavior(role);
            var timingError = ValidateCueTiming(input.StartMs, input.EndMs, input.ImageDurationSeconds,
                input.EstimatedDurationSeconds, endBehavior);
            if (timingError is not null) return Results.BadRequest(new { error = timingError });
            MediaAsset? itemMedia = null;
            if (input.MediaId is Guid mediaId)
            {
                itemMedia = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == mediaId, ct);
                if (itemMedia is null) return Results.BadRequest(new { error = "The selected media file does not exist." });
            }
            if (input.ActivityDefinitionId is Guid actDefId)
            {
                var exists = await db.ActivityDefinitions.AnyAsync(x => x.Id == actDefId, ct);
                if (!exists) return Results.BadRequest(new { error = "The selected activity does not exist." });
            }
            var item = new PlaylistItem
            {
                LessonId = lessonId,
                Title = input.Title.Trim(),
                Type = input.Type,
                Role = role,
                Position = input.Position,
                MediaAssetId = input.MediaId,
                ActivityDefinitionId = input.ActivityDefinitionId,
                DurationMs = input.DurationMs,
                StartMs = input.StartMs,
                EndMs = input.EndMs,
                VolumePercent = input.VolumePercent,
                ImageDurationSeconds = input.ImageDurationSeconds,
                EstimatedDurationSeconds = input.EstimatedDurationSeconds,
                EndBehavior = endBehavior,
                AllowSkip = input.AllowSkip,
                FitMode = input.FitMode ?? "fit",
                RotationDegrees = input.RotationDegrees,
                CropLeftPercent = input.CropLeftPercent,
                CropTopPercent = input.CropTopPercent,
                CropRightPercent = input.CropRightPercent,
                CropBottomPercent = input.CropBottomPercent,
                Muted = input.Muted,
                PlaybackRatePercent = input.PlaybackRatePercent,
                RepeatCount = input.RepeatCount,
                BackgroundColor = input.BackgroundColor ?? "#000000",
                TransitionStyle = input.TransitionStyle ?? "cut",
                TransitionDurationMs = input.TransitionDurationMs,
                FlexibleTime = false
            };
            db.PlaylistItems.Add(item);
            lesson.Version++;
            if (role == "countdown")
            {
                var otherCountdowns = await db.PlaylistItems.Where(x => x.LessonId == lessonId && x.Role == "countdown").ToListAsync(ct);
                foreach (var other in otherCountdowns) other.Role = "lesson";
                lesson.CountdownItemId = item.Id;
            }
            if (role == "preRoll") lesson.PreRollEnabled = true;
            if (itemMedia is not null && itemMedia.StoragePolicy == MediaRetention.LessonScoped)
                MediaRetention.KeepForLesson(itemMedia, lesson);
            Audit(db, "playlist.item.add", item.Id, item.Title);
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, lesson.Version, ct);
            return Results.Created($"/api/v1/lessons/{lessonId}/items/{item.Id}", new
            {
                item.Id, item.Title, item.Type, item.Role, item.Position, item.MediaAssetId,
                item.ActivityDefinitionId,
                item.DurationMs, item.StartMs, item.EndMs, item.VolumePercent,
                item.ImageDurationSeconds, item.EstimatedDurationSeconds, item.EndBehavior, item.AllowSkip, item.FitMode,
                item.RotationDegrees, item.CropLeftPercent, item.CropTopPercent, item.CropRightPercent,
                item.CropBottomPercent, item.Muted, item.PlaybackRatePercent, item.RepeatCount,
                item.BackgroundColor, item.TransitionStyle, item.TransitionDurationMs, item.FlexibleTime
            });
        });

        planning.MapPatch("/playlist-items/{id:guid}", async (Guid id, PlaylistItemUpdateInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var item = await db.PlaylistItems.Include(x => x.Lesson).Include(x => x.MediaAsset).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item?.Lesson is null) return Results.NotFound();
            var playbackError = ValidatePlaybackSettings(input.FitMode ?? item.FitMode,
                input.RotationDegrees ?? item.RotationDegrees, input.CropLeftPercent ?? item.CropLeftPercent,
                input.CropTopPercent ?? item.CropTopPercent, input.CropRightPercent ?? item.CropRightPercent,
                input.CropBottomPercent ?? item.CropBottomPercent, input.PlaybackRatePercent ?? item.PlaybackRatePercent,
                input.RepeatCount ?? item.RepeatCount, input.BackgroundColor ?? item.BackgroundColor,
                input.TransitionStyle ?? item.TransitionStyle, input.TransitionDurationMs ?? item.TransitionDurationMs);
            if (playbackError is not null) return Results.BadRequest(new { error = playbackError });
            var nextImageDuration = input.ClearImageDuration ? null : input.ImageDurationSeconds ?? item.ImageDurationSeconds;
            var nextEstimatedDuration = input.ClearEstimatedDuration ? null : input.EstimatedDurationSeconds ?? item.EstimatedDurationSeconds;
            var nextRole = input.Role is null ? item.Role : NormalizeRole(input.Role);
            var nextEndBehavior = input.EndBehavior ?? (input.Role is null ? item.EndBehavior : DefaultEndBehavior(nextRole));
            var timingError = ValidateCueTiming(input.StartMs ?? item.StartMs,
                input.ClearEndMs ? null : input.EndMs ?? item.EndMs,
                nextImageDuration, nextEstimatedDuration, nextEndBehavior);
            if (timingError is not null) return Results.BadRequest(new { error = timingError });
            if (input.Title is not null) item.Title = input.Title.Trim();
            if (input.Type is not null) item.Type = input.Type;
            var wasCountdown = item.Lesson.CountdownItemId == item.Id || item.Role == "countdown";
            if (input.Role is not null) item.Role = nextRole;
            if (input.MediaId is not null) item.MediaAssetId = input.MediaId;
            if (input.DurationMs is not null) item.DurationMs = input.DurationMs;
            if (input.StartMs is not null) item.StartMs = input.StartMs.Value;
            if (input.ClearEndMs) item.EndMs = null;
            else if (input.EndMs is not null) item.EndMs = input.EndMs;
            if (input.VolumePercent is not null) item.VolumePercent = Math.Clamp(input.VolumePercent.Value, 0, 150);
            if (input.ClearImageDuration) item.ImageDurationSeconds = null;
            else if (input.ImageDurationSeconds is not null) item.ImageDurationSeconds = input.ImageDurationSeconds;
            if (input.ClearEstimatedDuration) item.EstimatedDurationSeconds = null;
            else if (input.EstimatedDurationSeconds is not null) item.EstimatedDurationSeconds = input.EstimatedDurationSeconds;
            if (input.EndBehavior is not null || input.Role is not null) item.EndBehavior = nextEndBehavior;
            if (input.AllowSkip is not null) item.AllowSkip = input.AllowSkip.Value;
            if (input.Notes is not null) item.Notes = input.Notes.Trim();
            if (input.FadeInMs is not null) item.FadeInMs = Math.Clamp(input.FadeInMs.Value, 0, 30_000);
            if (input.FadeOutMs is not null) item.FadeOutMs = Math.Clamp(input.FadeOutMs.Value, 0, 30_000);
            if (input.NormalizeAudio is not null) item.NormalizeAudio = input.NormalizeAudio.Value;
            if (input.FitMode is not null) item.FitMode = input.FitMode;
            if (input.RotationDegrees is not null) item.RotationDegrees = input.RotationDegrees.Value;
            if (input.CropLeftPercent is not null) item.CropLeftPercent = input.CropLeftPercent.Value;
            if (input.CropTopPercent is not null) item.CropTopPercent = input.CropTopPercent.Value;
            if (input.CropRightPercent is not null) item.CropRightPercent = input.CropRightPercent.Value;
            if (input.CropBottomPercent is not null) item.CropBottomPercent = input.CropBottomPercent.Value;
            if (input.Muted is not null) item.Muted = input.Muted.Value;
            if (input.PlaybackRatePercent is not null) item.PlaybackRatePercent = input.PlaybackRatePercent.Value;
            if (input.RepeatCount is not null) item.RepeatCount = input.RepeatCount.Value;
            if (input.BackgroundColor is not null) item.BackgroundColor = input.BackgroundColor;
            if (input.TransitionStyle is not null) item.TransitionStyle = input.TransitionStyle;
            if (input.TransitionDurationMs is not null) item.TransitionDurationMs = input.TransitionDurationMs.Value;
            if (input.FlexibleTime is not null) item.FlexibleTime = input.FlexibleTime.Value;
            if (input.ActivityDefinitionId is not null) item.ActivityDefinitionId = input.ActivityDefinitionId;
            if (input.CuePoints is not null)
            {
                if (input.CuePoints.Count > 50) return Results.BadRequest(new { error = "A media item can have at most 50 named markers." });
                var minimum = item.StartMs;
                var maximum = item.EndMs ?? item.MediaAsset?.DurationMs ?? item.DurationMs ?? long.MaxValue;
                if (input.CuePoints.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 80 || x.PositionMs < minimum || x.PositionMs > maximum))
                    return Results.BadRequest(new { error = "Every marker needs a name and a position within the playable selection." });
                var normalized = input.CuePoints.Select(x => new CuePointInput(x.Name.Trim(), x.PositionMs))
                    .OrderBy(x => x.PositionMs).ThenBy(x => x.Name).ToList();
                item.CuePointsJson = JsonSerializer.Serialize(normalized);
            }
            if (item.Role == "countdown")
            {
                var otherCountdowns = await db.PlaylistItems.Where(x => x.LessonId == item.LessonId && x.Id != item.Id && x.Role == "countdown").ToListAsync(ct);
                foreach (var other in otherCountdowns) other.Role = "lesson";
                item.Lesson.CountdownItemId = item.Id;
            }
            else if (wasCountdown) item.Lesson.CountdownItemId = null;
            if (item.Role == "preRoll") item.Lesson.PreRollEnabled = true;
            item.Lesson.Version++;
            Audit(db, "playlist.item.update", item.Id, item.Title);
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, item.Lesson.Version, ct);
            return Results.NoContent();
        });

        planning.MapDelete("/playlist-items/{id:guid}", async (Guid id, LessonCueDb db,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var item = await db.PlaylistItems.Include(x => x.Lesson).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item?.Lesson is null) return Results.NotFound();
            if (item.Lesson.CountdownItemId == id) item.Lesson.CountdownItemId = null;
            item.Lesson.Version++;
            var version = item.Lesson.Version;
            db.PlaylistItems.Remove(item);
            Audit(db, "playlist.item.delete", id, item.Title);
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, version, ct);
            return Results.NoContent();
        });

        planning.MapPost("/lessons/{lessonId:guid}/reorder", async (Guid lessonId, PlaylistReorderInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var lesson = await db.Lessons.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == lessonId, ct);
            if (lesson is null) return Results.NotFound();
            var byId = lesson.Items.ToDictionary(x => x.Id);
            if (input.ItemIds is null || input.ItemIds.Count != byId.Count ||
                input.ItemIds.Distinct().Count() != byId.Count || input.ItemIds.Any(id => !byId.ContainsKey(id)))
                return Results.BadRequest(new { error = "Reorder list must contain every playlist item exactly once." });
            for (var index = 0; index < input.ItemIds.Count; index++) byId[input.ItemIds[index]].Position = (index + 1) * 1000;
            lesson.Version++;
            Audit(db, "playlist.reorder", lesson.Id, lesson.Title);
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, lesson.Version, ct);
            return Results.NoContent();
        });

        planning.MapPost("/playlist-items/bulk", async (PlaylistItemBulkInput input, LessonCueDb db,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            if (input.ItemIds is null || string.IsNullOrWhiteSpace(input.Action))
                return Results.BadRequest(new { error = "Select playlist items and choose an action." });
            var ids = input.ItemIds.Distinct().ToList();
            if (input.ItemIds.Count > 500 || ids.Count is 0 or > 500) return Results.BadRequest(new { error = "Select between 1 and 500 playlist items." });
            var items = await db.PlaylistItems.Include(x => x.Lesson).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            if (items.Count != ids.Count || items.Any(x => x.Lesson is null))
                return Results.BadRequest(new { error = "One or more selected playlist items no longer exist." });
            var action = input.Action.Trim().ToLowerInvariant();
            switch (action)
            {
                case "delete":
                    foreach (var item in items.Where(x => x.Lesson!.CountdownItemId == x.Id)) item.Lesson!.CountdownItemId = null;
                    db.PlaylistItems.RemoveRange(items);
                    break;
                case "role":
                    if (input.Role is not ("lesson" or "preRoll" or "countdown" or "postLesson"))
                        return Results.BadRequest(new { error = "Choose main lesson, pre-roll, countdown, or post-lesson." });
                    var role = NormalizeRole(input.Role);
                    if (role == "countdown" && items.Count != 1)
                        return Results.BadRequest(new { error = "A lesson can have only one countdown. Select exactly one item." });
                    foreach (var item in items)
                    {
                        item.Role = role;
                        if (role is "preRoll" or "postLesson") item.EndBehavior = "loop";
                        else if (item.EndBehavior == "loop") item.EndBehavior = "pause";
                    }
                    if (role == "countdown")
                    {
                        var item = items[0];
                        var otherCountdowns = await db.PlaylistItems.Where(x => x.LessonId == item.LessonId && x.Id != item.Id && x.Role == "countdown").ToListAsync(ct);
                        foreach (var other in otherCountdowns) other.Role = "lesson";
                        item.Lesson!.CountdownItemId = item.Id;
                    }
                    foreach (var lesson in items.Select(x => x.Lesson!).Distinct())
                    {
                        if (role != "countdown" && items.Any(x => x.LessonId == lesson.Id && lesson.CountdownItemId == x.Id)) lesson.CountdownItemId = null;
                        if (role == "preRoll") lesson.PreRollEnabled = true;
                    }
                    break;
                case "volume":
                    if (input.VolumePercent is not int volume || volume is < 0 or > 150)
                        return Results.BadRequest(new { error = "Volume must be from 0 to 150." });
                    foreach (var item in items) item.VolumePercent = volume;
                    break;
                case "end-behavior":
                    if (input.EndBehavior is not ("advance" or "loop" or "pause" or "menu" or "stop"))
                        return Results.BadRequest(new { error = "Choose a supported end behavior." });
                    foreach (var item in items) item.EndBehavior = input.EndBehavior;
                    break;
                case "allow-skip":
                    if (input.AllowSkip is not bool allowSkip) return Results.BadRequest(new { error = "Choose whether skipping is allowed." });
                    foreach (var item in items) item.AllowSkip = allowSkip;
                    break;
                case "prefix-title":
                    var prefix = input.TitlePrefix?.Trim();
                    if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 80)
                        return Results.BadRequest(new { error = "Enter a title prefix of 1 to 80 characters." });
                    foreach (var item in items)
                    {
                        item.Title = (prefix + " " + item.Title).Trim();
                        if (item.Title.Length > 160) item.Title = item.Title[..160].TrimEnd();
                    }
                    break;
                default:
                    return Results.BadRequest(new { error = "Unsupported bulk playlist action." });
            }
            var lessons = items.Select(x => x.Lesson!).Distinct().ToList();
            foreach (var lesson in lessons) lesson.Version++;
            Audit(db, $"playlist.bulk.{action}", lessons[0].Id, $"{items.Count} items across {lessons.Count} lessons");
            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, lessons.Max(x => x.Version), ct);
            return Results.Ok(new { updated = items.Count, lessons = lessons.Count, action });
        });

        admin.MapGet("/media", async (LessonCueDb db, CancellationToken ct) =>
            await db.MediaAssets.AsNoTracking().OrderByDescending(x => x.Id).Select(x => new
            {
                x.Id,
                x.FileName,
                x.RelativePath,
                x.ContentType,
                x.SizeBytes,
                x.DurationMs,
                x.Sha256,
                x.OfflineEligible,
                x.CreatedAt,
                x.ProcessingStatus,
                x.ProcessingError,
                x.VideoCodec,
                x.AudioCodec,
                x.Width,
                x.Height,
                x.LoudnessLufs,
                x.ThumbnailPath,
                x.FilmstripPath,
                x.WaveformPath,
                x.CompatibilityStatus,
                x.CompatibilityError,
                x.CompatibilityPath,
                x.CompatibilitySha256,
                x.CompatibilityTranscodedAt,
                x.CompatibilitySizeBytes,
                transcodes = x.TranscodeVariants.OrderBy(v => v.Profile).Select(v => new
                {
                    v.Id, v.Profile, v.Status, v.RelativePath, v.Sha256, v.SizeBytes, v.Width, v.Height, v.VideoBitrateKbps,
                    v.SourceVersion, v.Error, v.QueuedAt, v.StartedAt, v.CompletedAt
                }).ToArray(),
                x.SourceKind,
                x.SourceUrl,
                x.LinkKind,
                x.StoragePolicy,
                x.OriginLessonId,
                x.DeleteAfter,
                x.RetentionDateIsManual,
                x.Folder,
                x.TagsCsv,
                x.Version,
                x.ReplacedAt,
                x.ConversionStatus,
                x.ConversionError,
                x.ConvertedSlidesJson,
                x.ConvertedAt,
                thumbnailUrl = x.ThumbnailPath == null ? null : $"/api/v1/media/{x.Id}/thumbnail",
                filmstripUrl = x.FilmstripPath == null ? null : $"/api/v1/media/{x.Id}/filmstrip",
                waveformUrl = x.WaveformPath == null ? null : $"/api/v1/media/{x.Id}/waveform",
                downloadUrl = $"/api/v1/media/{x.Id}/file",
                playbackUrl = x.SourceKind == "link" ? x.SourceUrl : $"/api/v1/media/{x.Id}/playback"
            }).ToListAsync(ct));

        uploads.MapPost("/media/{id:guid}/transcodes/{profile}", async (Guid id, string profile,
            LessonCueDb db, HttpContext context, CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (media is null) return Results.NotFound();
            if (!AdaptiveTranscodeEligibility.IsVideo(media))
                return Results.BadRequest(new { error = "Adaptive profiles require a processed local video." });
            var profiles = profile.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? AdaptiveTranscodeProfiles.All.Keys.ToArray() : [profile];
            if (profiles.Any(value => !AdaptiveTranscodeProfiles.All.ContainsKey(value)))
                return Results.BadRequest(new { error = "Choose h264-720, h264-480, or all." });
            foreach (var value in profiles) await AdaptiveTranscodeService.QueueAsync(db, media, value, ct);
            db.AuditEvents.Add(new AuditEvent { Actor = context.User.Identity?.Name ?? "admin", Action = "media.transcode.queue",
                Object = media.Id.ToString(), Summary = string.Join(',', profiles) });
            await AdaptiveTranscodeService.SaveQueuedAsync(db, ct);
            return Results.Accepted(value: new { queued = profiles });
        }).RequireRateLimiting("media-processing");

        admin.MapGet("/media/{id:guid}/impact", async (Guid id, LessonCueDb db, CancellationToken ct) =>
        {
            var media = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (media is null) return Results.NotFound();
            var lessonItems = await db.PlaylistItems.AsNoTracking().Where(x => x.MediaAssetId == id)
                .Select(x => new { x.Id, itemTitle = x.Title, x.LessonId, lessonTitle = x.Lesson!.Title, x.Lesson.Date })
                .OrderBy(x => x.Date).ToListAsync(ct);
            var signage = (await db.SignagePlaylists.AsNoTracking().ToListAsync(ct))
                .Where(x => x.MediaAssetId == id || SignageLayout.ParseZones(x.ZonesJson).Any(zone => zone.MediaAssetId == id))
                .Select(x => new { x.Id, x.Name, x.Mode, x.Enabled }).OrderBy(x => x.Name).ToList();
            var templateItems = await db.LessonTemplateItems.AsNoTracking().Where(x => x.MediaAssetId == id)
                .Select(x => new { x.Id, itemTitle = x.Title, x.TemplateId, templateName = x.Template!.Name })
                .OrderBy(x => x.templateName).ToListAsync(ct);
            var versions = await db.MediaAssetVersions.AsNoTracking().Where(x => x.MediaAssetId == id)
                .OrderByDescending(x => x.VersionNumber).Select(x => new
                {
                    x.Id, x.VersionNumber, x.FileName, x.ContentType, x.SizeBytes, x.DurationMs, x.Sha256,
                    x.ArchivedAt, x.ArchivedBy,
                    downloadUrl = $"/api/v1/media/{id}/versions/{x.Id}/file"
                }).ToListAsync(ct);
            return Results.Ok(new
            {
                media.Id, media.FileName, media.Folder, media.TagsCsv, media.Version, media.ReplacedAt,
                lessons = lessonItems.GroupBy(x => new { x.LessonId, x.lessonTitle, x.Date })
                    .Select(group => new { id = group.Key.LessonId, title = group.Key.lessonTitle, date = group.Key.Date, itemCount = group.Count() }),
                templates = templateItems.GroupBy(x => new { x.TemplateId, x.templateName })
                    .Select(group => new { id = group.Key.TemplateId, name = group.Key.templateName, itemCount = group.Count() }),
                signage,
                versions
            });
        });

        uploads.MapPatch("/media/{id:guid}/organize", async (Guid id, MediaOrganizeInput input, LessonCueDb db,
            HttpContext context, CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (media is null) return Results.NotFound();
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var selection = MediaTaxonomy.Validate(organization, input.Folder ?? media.Folder, input.TagsCsv ?? media.TagsCsv);
            if (selection.Error is not null) return Results.BadRequest(new { error = selection.Error });
            if (input.FileName is not null)
            {
                var name = NormalizeMediaName(input.FileName, media.FileName);
                if (name is null)
                    return Results.BadRequest(new { error = "The media name must be between 1 and 255 characters and keep its existing file extension." });
                var conflict = await FindMediaNameConflictAsync(db, [media.Id], [name], ct);
                if (conflict is not null)
                    return Results.Conflict(new { error = $"A media item named \u201c{conflict}\u201d already exists. Choose a different name." });
                media.FileName = name;
            }
            if (input.Folder is not null) media.Folder = selection.Folder;
            if (input.TagsCsv is not null) media.TagsCsv = selection.TagsCsv;
            db.AuditEvents.Add(new AuditEvent { Actor = context.User.Identity?.Name ?? "admin", Action = "media.organize",
                Object = media.Id.ToString(), Summary = $"{media.FileName}: {media.Folder}; {media.TagsCsv}" });
            await db.SaveChangesAsync(ct);
            return Results.Ok(media);
        });

        uploads.MapPost("/media/{id:guid}/reprocess", async (Guid id, LessonCueDb db, MediaStoragePaths paths,
            HttpContext context, CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (media is null) return Results.NotFound();
            if (media.SourceKind == "youtube-download" && string.IsNullOrWhiteSpace(media.RelativePath))
            {
                if (!Uri.TryCreate(media.SourceUrl, UriKind.Absolute, out var youtubeSource) || !YouTubeMedia.IsYouTubeUrl(youtubeSource))
                    return Results.BadRequest(new { error = "This YouTube import has no valid source URL to retry." });
                if (media.ProcessingStatus is "processing" or "downloading")
                    return Results.Conflict(new { error = "This YouTube download is already in progress." });
                media.ProcessingStatus = "downloading";
                media.ProcessingError = null;
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = context.User.Identity?.Name ?? "admin",
                    Action = "media.youtube.retry",
                    Object = media.Id.ToString(),
                    Summary = youtubeSource.Host
                });
                await db.SaveChangesAsync(ct);
                return Results.Accepted($"/api/v1/media/{id}", media);
            }
            if (media.SourceKind == "link" || string.IsNullOrWhiteSpace(media.RelativePath))
                return Results.BadRequest(new { error = "Online-only media does not have a local file to reprocess." });
            if (media.ProcessingStatus is "processing" or "downloading")
                return Results.Conflict(new { error = "This media is already being processed." });
            var source = await MediaRecovery.ValidateOriginalAsync(media, paths, ct);
            if (!source.Valid)
                return Results.UnprocessableEntity(new
                {
                    error = source.Error ?? "The original file cannot be safely reprocessed.",
                    mediaId = media.Id,
                    recoverable = false,
                    originalFileExists = source.Exists,
                    originalDiskSizeBytes = source.DiskSizeBytes,
                    originalSha256Matches = source.Sha256Matches,
                    action = "Restore the intact original file or remove the damaged asset; do not re-upload over this asset."
                });
            var derivatives = ResetMediaProcessing(media);
            db.AuditEvents.Add(new AuditEvent { Actor = context.User.Identity?.Name ?? "admin", Action = "media.reprocess",
                Object = media.Id.ToString(), Summary = media.FileName });
            await db.SaveChangesAsync(ct);
            DeleteDerivatives(paths, derivatives);
            await DeleteAdaptiveTranscodesAsync(db, paths, media.Id, ct);
            return Results.Accepted($"/api/v1/media/{id}", media);
        }).RequireRateLimiting("media-processing");

        uploads.MapPost("/media/{id:guid}/convert", async (Guid id, LessonCueDb db, HttpContext context,
            CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (media is null) return Results.NotFound();
            if (!PresentationConversion.IsConvertible(media.RelativePath) || media.SourceKind == "link" || string.IsNullOrWhiteSpace(media.RelativePath))
                    return Results.BadRequest(new { error = "Local conversion supports the document formats accepted by LessonCue." });
            if (media.ConversionStatus is "pending" or "converting")
                return Results.Conflict(new { error = "This presentation is already being converted." });
            media.ConversionStatus = "pending";
            media.ConversionError = null;
            db.AuditEvents.Add(new AuditEvent { Actor = context.User.Identity?.Name ?? "admin", Action = "presentation.convert.queue",
                Object = media.Id.ToString(), Summary = media.FileName });
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/v1/media/{id}/impact", new { media.Id, media.ConversionStatus });
        }).RequireRateLimiting("media-processing");

        uploads.MapPost("/media/{id:guid}/convert-and-add-to-lesson", async (Guid id,
            PresentationLessonInput input, LessonCueDb db, HttpContext context, CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            var lesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == input.LessonId && !x.Archived, ct);
            if (media is null || lesson is null) return Results.NotFound();
            if (!PresentationConversion.IsConvertible(media.RelativePath) || media.SourceKind == "link" ||
                string.IsNullOrWhiteSpace(media.RelativePath))
                return Results.BadRequest(new { error = "Choose a locally stored document format accepted by LessonCue." });
            media.ConversionLessonId = lesson.Id;
            // A missing slide duration means the generated stills remain on the
            // last frame until the remote queues the next cue. Keep 0 as the
            // persisted sentinel so existing installations do not need a risky
            // SQLite column rebuild just to opt into untimed slides.
            media.ConversionSlideDurationSeconds = Math.Clamp(input.ImageDurationSeconds ?? 0, 0, 3600);
            if (media.ConversionStatus == "ready" && PresentationConversion.SlideIds(media).Count > 0)
            {
                var count = await PresentationConversion.AddToLessonAsync(db, media, lesson,
                    media.ConversionSlideDurationSeconds, context.User.Identity?.Name ?? "admin", ct);
                media.ConversionLessonId = null;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { added = count, lesson.Id, lesson.Version });
            }
            media.ConversionStatus = "pending";
            media.ConversionError = null;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = context.User.Identity?.Name ?? "admin", Action = "presentation.convert-and-add.queue",
                Object = media.Id.ToString(), Summary = $"{media.FileName} -> {lesson.Title}"
            });
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/v1/media/{id}/impact", new
            {
                mediaId = media.Id,
                media.ConversionStatus,
                lessonId = lesson.Id
            });
        }).RequireAuthorization(LessonCuePermissions.Planning)
            .RequireRateLimiting("media-processing");

        uploads.MapPost("/media/{id:guid}/conversion/add-to-lesson", async (Guid id, PresentationLessonInput input,
            LessonCueDb db, IHubContext<SyncHub> hub, HttpContext context, CancellationToken ct) =>
        {
            var source = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            var lesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == input.LessonId && !x.Archived, ct);
            if (source is null || lesson is null) return Results.NotFound();
            try
            {
                var count = await PresentationConversion.AddToLessonAsync(db, source, lesson,
                    input.ImageDurationSeconds, context.User.Identity?.Name ?? "admin", ct);
                await InvalidateAsync(hub, lesson.Version, ct);
                return Results.Ok(new { added = count, lesson.Id, lesson.Version });
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireAuthorization(LessonCuePermissions.Planning);

        admin.MapGet("/media/{id:guid}/versions/{versionId:guid}/file", async (Guid id, Guid versionId,
            LessonCueDb db, MediaStoragePaths paths, CancellationToken ct) =>
        {
            var version = await db.MediaAssetVersions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == versionId && x.MediaAssetId == id, ct);
            if (version is null) return Results.NotFound();
            var path = ResolveStoredFile(paths.Versions, version.RelativePath);
            return path is null ? Results.NotFound() : Results.File(path, version.ContentType, version.FileName, enableRangeProcessing: true);
        });

        uploads.MapPost("/media/{id:guid}/replace", async (Guid id, HttpRequest request, LessonCueDb db,
            StorageService storage, MediaStoragePaths paths, IHubContext<SyncHub> hub, HttpContext context,
            CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (media is null) return Results.NotFound();
            if (media.SourceKind == "link" || string.IsNullOrWhiteSpace(media.RelativePath))
                return Results.BadRequest(new { error = "Online-only media cannot be replaced with a local version." });
            if (media.ProcessingStatus is "processing" or "downloading")
                return Results.Conflict(new { error = "Wait for current processing to finish before replacing this media." });
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Choose a replacement media file." });
            var form = await request.ReadFormAsync(ct);
            var upload = form.Files.GetFile("file");
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { error = "Choose a non-empty replacement file." });
            var extension = Path.GetExtension(upload.FileName);
            if (!IsSupportedMediaExtension(extension)) return Results.BadRequest(new { error = "Unsupported media type." });
            if (await storage.EnsureAvailableAsync(db, SaturatingAdd(upload.Length, media.SizeBytes), ct) is null)
                return StorageExceeded(upload.Length);
            var currentPath = ResolveStoredFile(paths.Originals, media.RelativePath);
            if (currentPath is null) return Results.Conflict(new { error = "The current local media file is missing. Re-upload it as a new library item." });

            var actor = context.User.Identity?.Name ?? "admin";
            var temporaryRoot = Path.Combine(dataPath, "media", "temporary");
            Directory.CreateDirectory(temporaryRoot);
            var temporary = Path.Combine(temporaryRoot, $"replace-{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
            var newRelative = $"{media.Id:N}-v{media.Version + 1}-{Guid.NewGuid().ToString("N")[..8]}{extension.ToLowerInvariant()}";
            var newPath = Path.Combine(paths.Originals, newRelative);
            var archiveRelative = $"{media.Id:N}/v{media.Version:D4}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(media.RelativePath).ToLowerInvariant()}";
            var archivePath = Path.Combine(paths.Versions, archiveRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            try
            {
                await using (var output = File.Create(temporary)) await upload.CopyToAsync(output, ct);
                var inspection = MediaContentInspector.Inspect(temporary, upload.FileName);
                if (!inspection.Valid) return Results.BadRequest(new { error = inspection.Error });
                string sha;
                await using (var source = File.OpenRead(temporary))
                    sha = Convert.ToHexString(await SHA256.HashDataAsync(source, ct)).ToLowerInvariant();
                if (string.Equals(sha, media.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "This file is identical to the current version." });
                File.Copy(currentPath, archivePath, false);
                File.Move(temporary, newPath);
                var archived = CreateArchivedVersion(media, archiveRelative, actor);
                var derivatives = ResetMediaProcessing(media, clearConversion: true);
                media.FileName = Path.GetFileName(upload.FileName);
                media.ContentType = inspection.ContentType;
                media.RelativePath = newRelative;
                media.Sha256 = sha;
                media.SizeBytes = upload.Length;
                media.SourceKind = "upload";
                media.SourceUrl = null;
                media.LinkKind = null;
                media.Version++;
                media.ReplacedAt = DateTimeOffset.UtcNow;
                db.MediaAssetVersions.Add(archived);
                await IncrementMediaLessonVersionsAsync(db, id, ct);
                db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.replace", Object = id.ToString(),
                    Summary = $"Version {archived.VersionNumber} archived; version {media.Version} is {media.FileName}." });
                try { await db.SaveChangesAsync(ct); }
                catch { TryDeleteFile(newPath); TryDeleteFile(archivePath); throw; }
                TryDeleteFile(currentPath);
                DeleteDerivatives(paths, derivatives);
                await DeleteAdaptiveTranscodesAsync(db, paths, media.Id, ct);
                await InvalidateAsync(hub, media.Version, ct);
                return Results.Ok(new { media.Id, media.FileName, media.Version, archivedVersionId = archived.Id });
            }
            catch
            {
                TryDeleteFile(newPath);
                TryDeleteFile(archivePath);
                throw;
            }
            finally { TryDeleteFile(temporary); }
        }).DisableAntiforgery();

        uploads.MapPost("/media/{id:guid}/versions/{versionId:guid}/restore", async (Guid id, Guid versionId,
            LessonCueDb db, StorageService storage, MediaStoragePaths paths, IHubContext<SyncHub> hub,
            HttpContext context, CancellationToken ct) =>
        {
            var media = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, ct);
            var selected = await db.MediaAssetVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId && x.MediaAssetId == id, ct);
            if (media is null || selected is null) return Results.NotFound();
            if (media.ProcessingStatus is "pending" or "processing" or "downloading")
                return Results.Conflict(new { error = "Wait for current processing to finish before restoring a previous version." });
            var currentPath = ResolveStoredFile(paths.Originals, media.RelativePath);
            var selectedPath = ResolveStoredFile(paths.Versions, selected.RelativePath);
            if (currentPath is null || selectedPath is null) return Results.Conflict(new { error = "A required version file is missing from local storage." });
            if (await storage.EnsureAvailableAsync(db, SaturatingAdd(selected.SizeBytes, media.SizeBytes), ct) is null)
                return StorageExceeded(selected.SizeBytes);
            var actor = context.User.Identity?.Name ?? "admin";
            var extension = Path.GetExtension(selected.RelativePath);
            var newRelative = $"{media.Id:N}-v{media.Version + 1}-{Guid.NewGuid().ToString("N")[..8]}{extension}";
            var newPath = Path.Combine(paths.Originals, newRelative);
            var archiveRelative = $"{media.Id:N}/v{media.Version:D4}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(media.RelativePath)}";
            var archivePath = Path.Combine(paths.Versions, archiveRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            try
            {
                File.Copy(currentPath, archivePath, false);
                File.Copy(selectedPath, newPath, false);
                var archived = CreateArchivedVersion(media, archiveRelative, actor);
                var derivatives = ResetMediaProcessing(media, clearConversion: true);
                media.FileName = selected.FileName;
                media.ContentType = selected.ContentType;
                media.RelativePath = newRelative;
                media.Sha256 = selected.Sha256;
                media.SizeBytes = selected.SizeBytes;
                media.DurationMs = selected.DurationMs;
                media.SourceKind = selected.SourceKind;
                media.SourceUrl = selected.SourceUrl;
                media.LinkKind = selected.LinkKind;
                media.Version++;
                media.ReplacedAt = DateTimeOffset.UtcNow;
                db.MediaAssetVersions.Add(archived);
                await IncrementMediaLessonVersionsAsync(db, id, ct);
                db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.version.restore", Object = id.ToString(),
                    Summary = $"Archived version {selected.VersionNumber} restored as version {media.Version}." });
                try { await db.SaveChangesAsync(ct); }
                catch { TryDeleteFile(newPath); TryDeleteFile(archivePath); throw; }
                TryDeleteFile(currentPath);
                DeleteDerivatives(paths, derivatives);
                await DeleteAdaptiveTranscodesAsync(db, paths, media.Id, ct);
                await InvalidateAsync(hub, media.Version, ct);
                return Results.Ok(new { media.Id, media.FileName, media.Version, restoredFrom = selected.VersionNumber });
            }
            catch
            {
                TryDeleteFile(newPath);
                TryDeleteFile(archivePath);
                throw;
            }
        });

        uploads.MapPost("/media/bulk", async (MediaBulkInput input, LessonCueDb db, MediaStoragePaths paths,
            IHubContext<SyncHub> hub, HttpContext context, CancellationToken ct) =>
        {
            if (input.MediaIds is null || input.MediaIds.Count == 0)
                return Results.BadRequest(new { error = "Select at least one media item." });
            var ids = input.MediaIds.Distinct().ToList();
            if (ids.Count > 500) return Results.BadRequest(new { error = "Bulk actions are limited to 500 media items at a time." });
            var media = await db.MediaAssets.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            if (media.Count != ids.Count) return Results.NotFound(new { error = "One or more selected media items no longer exist. Refresh the library and try again." });
            var actor = context.User.Identity?.Name ?? "admin";
            var action = input.Action?.Trim().ToLowerInvariant();

            switch (action)
            {
                case "keep":
                    foreach (var item in media)
                    {
                        MediaRetention.KeepPermanently(item);
                        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.retention.keep", Object = item.Id.ToString(), Summary = item.FileName });
                    }
                    break;
                case "expire":
                    if (input.DeleteOn is not DateOnly deleteOn)
                        return Results.BadRequest(new { error = "Choose an expiration date." });
                    if (deleteOn < DateOnly.FromDateTime(DateTime.Today))
                        return Results.BadRequest(new { error = "The expiration date cannot be in the past." });
                    foreach (var item in media)
                    {
                        MediaRetention.ExpireOn(item, deleteOn);
                        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.retention.expire", Object = item.Id.ToString(), Summary = $"{item.FileName} deletes {deleteOn:yyyy-MM-dd}" });
                    }
                    break;
                case "delete":
                    foreach (var item in media)
                    {
                        item.DeletedAt = DateTimeOffset.UtcNow; item.DeletedBy = actor;
                        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.recycle", Object = item.Id.ToString(), Summary = item.FileName });
                    }
                    break;
                case "organize":
                    var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
                    var selection = MediaTaxonomy.Validate(organization, input.Folder, input.TagsCsv);
                    if (selection.Error is not null) return Results.BadRequest(new { error = selection.Error });
                    var folder = selection.Folder;
                    var tags = selection.TagsCsv;
                    foreach (var item in media)
                    {
                        item.Folder = folder;
                        item.TagsCsv = tags;
                        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.organize", Object = item.Id.ToString(), Summary = $"{item.FileName}: {folder}; {tags}" });
                    }
                    break;
                case "rename":
                {
                    var requested = input.Renames ?? [];
                    if (requested.Count != media.Count || requested.Select(x => x.MediaId).Distinct().Count() != media.Count ||
                        requested.Any(x => !ids.Contains(x.MediaId)))
                        return Results.BadRequest(new { error = "Provide one new name for every selected media item." });

                    var requestedById = requested.ToDictionary(x => x.MediaId);
                    var names = new Dictionary<Guid, string>();
                    foreach (var item in media)
                    {
                        var name = NormalizeMediaName(requestedById[item.Id].FileName, item.FileName);
                        if (name is null)
                            return Results.BadRequest(new { error = "Each media name must be between 1 and 255 characters and keep its existing file extension." });
                        names[item.Id] = name;
                    }

                    var duplicate = names.Values
                        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault(group => group.Count() > 1)?.First();
                    if (duplicate is not null)
                        return Results.Conflict(new { error = $"More than one selected item is assigned the name \u201c{duplicate}\u201d. Each media name must be unique." });

                    var conflict = await FindMediaNameConflictAsync(db, ids, names.Values, ct);
                    if (conflict is not null)
                        return Results.Conflict(new { error = $"A media item named \u201c{conflict}\u201d already exists. Choose a different name." });

                    foreach (var item in media)
                    {
                        item.FileName = names[item.Id];
                        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "media.rename", Object = item.Id.ToString(), Summary = item.FileName });
                    }
                    break;
                }
                default:
                    return Results.BadRequest(new { error = "Choose delete, expire, keep permanently, organize, or rename." });
            }

            await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, 0, ct);
            return Results.Ok(new { updated = media.Count, action });
        });

        uploads.MapPost("/media", async (HttpRequest request, HttpContext context, LessonCueDb db,
            UploadSessionService sessions, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data is required." });
            var form = await request.ReadFormAsync(ct);
            var upload = form.Files.GetFile("file");
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { error = "A non-empty file field is required." });
            var persistent = bool.TryParse(form["persistent"], out var keep) && keep;
            long? duration = long.TryParse(form["durationMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            var ownerId = CurrentAccountId(context);
            var create = await sessions.CreateAsync(db, ownerId, context.User.Identity?.Name ?? "admin",
                context.User.FindFirstValue(ClaimTypes.Role) ?? "Viewer",
                new UploadCreateInput(upload.FileName, upload.Length, upload.ContentType, null, persistent,
                    Guid.TryParse(form["lessonId"], out var lessonId) ? lessonId : null,
                    form["folder"], form["tagsCsv"], duration),
                ct, singleChunk: true);
            if (!create.Success) return UploadError(create.StatusCode, create.Error!, create.Storage);

            await using var input = upload.OpenReadStream();
            var write = await sessions.WriteChunkAsync(db, create.Session!.Id, ownerId, 0, input, upload.Length, ct);
            if (!write.Success) return UploadError(write.StatusCode, write.Error!);
            var completed = await sessions.CompleteAsync(db, create.Session.Id, ownerId,
                new UploadCompleteInput(DurationMs: duration), ct);
            if (!completed.Success) return UploadError(completed.StatusCode, completed.Error!);
            return completed.Duplicate
                ? Results.Ok(new { duplicate = true, media = completed.Media })
                : Results.Created($"/api/v1/media/{completed.Media!.Id}", completed.Media);
        }).DisableAntiforgery().RequireRateLimiting("upload-session");

        uploads.MapPost("/media/link", async (LinkInput input, LessonCueDb db, StorageService storage,
            IHttpClientFactory clients, HttpContext context, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Results.BadRequest(new { error = "Enter a complete http or https URL." });
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var selection = MediaTaxonomy.Validate(organization, input.Folder, input.TagsCsv);
            if (selection.Error is not null) return Results.BadRequest(new { error = selection.Error });
            Lesson? retentionLesson = null;
            if (input.ImportPresentation)
            {
                if (!PresentationConversion.TryGoogleSlidesExport(uri, out var exportUri))
                    return Results.BadRequest(new { error = "Google Slides import requires a docs.google.com/presentation link shared so this server can view it." });
                if (!input.Persistent)
                {
                    if (input.LessonId is not Guid lessonId)
                        return Results.BadRequest(new { error = "Choose the lesson this presentation belongs to, or choose Keep permanently." });
                    retentionLesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == lessonId, ct);
                    if (retentionLesson is null) return Results.BadRequest(new { error = "The selected lesson does not exist." });
                }
                const long maximumImportBytes = 100L * 1024 * 1024;
                var temporaryRoot = Path.Combine(dataPath, "media", "temporary");
                Directory.CreateDirectory(temporaryRoot);
                var temporary = Path.Combine(temporaryRoot, $"google-slides-{Guid.NewGuid():N}.pdf");
                try
                {
                    using var response = await clients.CreateClient("presentation-import")
                        .GetAsync(exportUri, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                        return Results.BadRequest(new { error = $"Google Slides export was not available ({(int)response.StatusCode}). Confirm that anyone with the link can view the presentation." });
                    if (response.Content.Headers.ContentLength is > maximumImportBytes)
                        return Results.BadRequest(new { error = "Google Slides imports are limited to 100 MB." });
                    if (response.Content.Headers.ContentLength is long expected &&
                        await storage.EnsureAvailableAsync(db, expected, ct) is null)
                        return StorageExceeded(expected);
                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    await using (var destination = File.Create(temporary))
                    {
                        var buffer = new byte[64 * 1024];
                        long total = 0;
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, ct);
                            if (read == 0) break;
                            total += read;
                            if (total > maximumImportBytes)
                                return Results.BadRequest(new { error = "Google Slides imports are limited to 100 MB." });
                            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                    }
                    var signature = new byte[5];
                    await using (var check = File.OpenRead(temporary))
                        if (await check.ReadAsync(signature, ct) != signature.Length ||
                            !signature.SequenceEqual("%PDF-"u8.ToArray()))
                            return Results.BadRequest(new { error = "Google returned a webpage instead of a presentation export. Share the deck so anyone with the link can view it, then try again." });
                    var size = new FileInfo(temporary).Length;
                    if (await storage.EnsureAvailableAsync(db, size, ct) is null) return StorageExceeded(size);
                    string sha;
                    await using (var source = File.OpenRead(temporary))
                        sha = Convert.ToHexString(await SHA256.HashDataAsync(source, ct)).ToLowerInvariant();
                    var existing = await db.MediaAssets.FirstOrDefaultAsync(x => x.Sha256 == sha, ct);
                    if (existing is not null)
                    {
                        if (input.Persistent) MediaRetention.KeepPermanently(existing);
                        else if (existing.StoragePolicy == MediaRetention.LessonScoped)
                            MediaRetention.KeepForLesson(existing, retentionLesson!);
                        await db.SaveChangesAsync(ct);
                        return Results.Ok(new { duplicate = true, media = existing });
                    }
                    var id = Guid.NewGuid();
                    var storedName = $"{id:N}.pdf";
                    File.Move(temporary, Path.Combine(mediaPath, storedName));
                    var importedTitle = string.IsNullOrWhiteSpace(input.Title) ? "Google Slides presentation" : input.Title.Trim();
                    importedTitle = Path.GetFileNameWithoutExtension(importedTitle);
                    if (importedTitle.Length > 235) importedTitle = importedTitle[..235];
                    importedTitle += ".pdf";
                    var importedMedia = new MediaAsset
                    {
                        Id = id, FileName = Path.GetFileName(importedTitle), ContentType = "application/pdf",
                        RelativePath = storedName, Sha256 = sha, SizeBytes = size, OfflineEligible = true,
                        ProcessingStatus = "pending", SourceKind = "google-slides", SourceUrl = uri.ToString(),
                        LinkKind = "google-slides-import", Folder = selection.Folder, TagsCsv = selection.TagsCsv,
                        ConversionStatus = "pending"
                    };
                    if (input.Persistent) MediaRetention.KeepPermanently(importedMedia);
                    else MediaRetention.SetNewUploadPolicy(importedMedia, retentionLesson!);
                    db.MediaAssets.Add(importedMedia);
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Actor = context.User.Identity?.Name ?? "admin", Action = "presentation.google-slides.import",
                        Object = importedMedia.Id.ToString(), Summary = uri.Host
                    });
                    await db.SaveChangesAsync(ct);
                    return Results.Accepted($"/api/v1/media/{importedMedia.Id}", importedMedia);
                }
                finally { TryDeleteFile(temporary); }
            }
            var youtube = YouTubeMedia.IsYouTubeUrl(uri);
            // YouTube is always imported locally now. The original URL remains
            // on the MediaAsset so every client can fall back to embedded
            // streaming when the download or its derivatives are unusable.
            if (input.Download || youtube)
            {
                if (!youtube)
                    return Results.BadRequest(new { error = "Local download is available only for YouTube URLs." });
                if (!input.Persistent)
                {
                    if (input.LessonId is not Guid lessonId)
                        return Results.BadRequest(new { error = "Choose the lesson this download belongs to, or choose Keep permanently." });
                    retentionLesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == lessonId, ct);
                    if (retentionLesson is null) return Results.BadRequest(new { error = "The selected lesson does not exist." });
                }
                var pendingTitle = string.IsNullOrWhiteSpace(input.Title) ? "YouTube video" : input.Title.Trim();
                var pending = new MediaAsset { FileName = pendingTitle, ContentType = "video/mp4", RelativePath = "",
                    SizeBytes = 0, OfflineEligible = false, ProcessingStatus = "downloading", SourceKind = "youtube-download",
                    SourceUrl = uri.ToString(), LinkKind = "youtube-local", Folder = selection.Folder,
                    TagsCsv = selection.TagsCsv };
                if (input.Persistent) MediaRetention.KeepPermanently(pending);
                else MediaRetention.SetNewUploadPolicy(pending, retentionLesson!);
                db.MediaAssets.Add(pending); Audit(db, "media.youtube.queue", pending.Id, uri.Host); await db.SaveChangesAsync(ct);
                return Results.Accepted($"/api/v1/media/{pending.Id}", pending);
            }
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            var direct = MediaFormatCatalog.IsDirectLinkable(extension);
            var embedded = youtube || uri.Host.Equals("vimeo.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".vimeo.com", StringComparison.OrdinalIgnoreCase);
            var kind = direct ? "direct" : youtube ? "youtube" : embedded ? "embedded" : "webpage";
            var contentType = direct ? MediaFormatCatalog.ContentType(extension) : "text/uri-list";
            var title = string.IsNullOrWhiteSpace(input.Title) ? uri.Host : input.Title.Trim();
            var media = new MediaAsset { FileName = title, ContentType = contentType, RelativePath = "",
                SizeBytes = 0, OfflineEligible = direct, ProcessingStatus = "ready", SourceKind = "link", SourceUrl = uri.ToString(), LinkKind = kind,
                Folder = selection.Folder, TagsCsv = selection.TagsCsv };
            db.MediaAssets.Add(media); Audit(db, "media.link", media.Id, $"{kind}: {uri.Host}"); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/media/{media.Id}", media);
        }).RequireRateLimiting("media-processing");

        uploads.MapGet("/uploads", async (LessonCueDb db, UploadSessionService sessions,
            HttpContext context, CancellationToken ct) =>
        {
            var ownerId = CurrentAccountId(context);
            var recentCutoff = DateTimeOffset.UtcNow.AddDays(-1);
            var all = await db.UploadSessions.AsNoTracking()
                .Where(value => value.OwnerAccountId == ownerId)
                .ToListAsync(ct);
            var items = all.OrderByDescending(value => value.UpdatedAt).Where(value =>
                    value.State == UploadSessionStates.Active ||
                    value.State == UploadSessionStates.Paused ||
                    value.State == UploadSessionStates.Failed ||
                    value.State == UploadSessionStates.Completing ||
                    value.UpdatedAt >= recentCutoff)
                .Take(100);
            return Results.Ok(items.Select(sessions.Status));
        });

        uploads.MapPost("/media/preflight", async (MediaPreflightInput input, LessonCueDb db,
            StorageService storage, HttpContext context, CancellationToken ct) =>
        {
            var fileName = Path.GetFileName(input.FileName?.Trim() ?? "");
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var format = MediaFormatCatalog.All.FirstOrDefault(x =>
                string.Equals(x.Extension, extension, StringComparison.OrdinalIgnoreCase));
            var converter = MediaConverterCapabilities.Snapshot();
            var family = format?.Family ?? MediaFormatCatalog.Family(extension, input.ContentType);
            var converterName = format?.Converter ?? "none";
            var converterReady = converterName switch
            {
                "FFmpeg" => converter.Ffmpeg && converter.Ffprobe,
                "LibreOffice" => converter.LibreOffice,
                "Poppler" => converter.Poppler,
                _ => true
            };
            var storageStatus = await storage.GetSnapshotAsync(db, ct);
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var policy = UploadQuotaPolicy.Read(organization);
            var activeUploadStates = new[]
            {
                UploadSessionStates.Active,
                UploadSessionStates.Paused,
                UploadSessionStates.Failed,
                UploadSessionStates.Completing
            };
            var currentOwnerId = CurrentAccountId(context);
            var preflightSnapshotTime = DateTimeOffset.UtcNow;
            var active = (await db.UploadSessions.AsNoTracking()
                .Where(x => x.OwnerAccountId == currentOwnerId)
                .Select(x => new { x.State, x.ExpiresAt })
                .ToListAsync(ct))
                .Count(x => x.ExpiresAt > preflightSnapshotTime && activeUploadStates.Contains(x.State));
            var warnings = new List<string>();
            if (format is null) warnings.Add("This extension is not supported by the installed LessonCue catalog.");
            if (input.TotalBytes <= 0) warnings.Add("Choose a file larger than zero bytes.");
            if (input.TotalBytes > UploadQuotaPolicy.HardMaximumFileBytes)
                warnings.Add("The hard server limit is 100 GB per file.");
            if (policy.MaxFileBytes > 0 && input.TotalBytes > policy.MaxFileBytes)
                warnings.Add($"This organization limits files to {FormatBytes(policy.MaxFileBytes)}.");
            if (input.TotalBytes > storageStatus.RemainingBytes)
                warnings.Add($"Only {FormatBytes(storageStatus.RemainingBytes)} remains available after current reservations.");
            if (!input.Persistent && input.LessonId is null)
                warnings.Add("Choose a lesson or switch to Keep permanently before uploading.");
            if (!converterReady)
                warnings.Add($"{converterName} is unavailable on this server. Install the documented converter or upload a different format.");
            var expectedOutput = family switch
            {
                "video" => "Original plus an inspected TV-ready H.264/AAC copy when needed",
                "audio" => "Original plus normalized playback metadata",
                "image" => "Original plus a browser/TV thumbnail derivative",
                "document" => "Validated local PDF/slide PNGs for lesson playback",
                _ => "Validated local media derivative"
            };
            return Results.Ok(new
            {
                fileName, extension, family, label = format?.Label ?? "Unsupported file",
                contentType = format?.ContentType ?? input.ContentType ?? "application/octet-stream",
                converter = converterName,
                converterReady,
                supported = format is not null,
                sizeBytes = input.TotalBytes,
                sizeAllowed = input.TotalBytes > 0 && input.TotalBytes <= UploadQuotaPolicy.HardMaximumFileBytes &&
                    (policy.MaxFileBytes <= 0 || input.TotalBytes <= policy.MaxFileBytes) &&
                    input.TotalBytes <= storageStatus.RemainingBytes,
                codec = family is "video" or "audio" ? "Detected during server inspection after upload" : null,
                expectedOutput, queuePosition = active + 1, activeUploads = active,
                warnings, ready = format is not null && converterReady && warnings.Count == 0
            });
        }).RequireRateLimiting("media-processing");

        uploads.MapGet("/uploads/{uploadId:guid}", async (Guid uploadId, LessonCueDb db,
            UploadSessionService sessions, HttpContext context, CancellationToken ct) =>
        {
            var session = await sessions.OwnedSessionAsync(db, uploadId, CurrentAccountId(context), ct);
            return session is null ? Results.NotFound() : Results.Ok(sessions.Status(session));
        });

        uploads.MapPost("/uploads", async (UploadCreateInput input, LessonCueDb db,
            UploadSessionService sessions, HttpContext context, CancellationToken ct) =>
        {
            var result = await sessions.CreateAsync(db, CurrentAccountId(context),
                context.User.Identity?.Name ?? "admin",
                context.User.FindFirstValue(ClaimTypes.Role) ?? "Viewer", input, ct);
            if (!result.Success) return UploadError(result.StatusCode, result.Error!, result.Storage);
            var session = result.Session!;
            return Results.Created($"/api/v1/uploads/{session.Id}", new
            {
                uploadId = session.Id,
                session.FileName,
                session.ChunkSize,
                session.ChunkCount,
                session.ExpectedLength,
                session.ExpiresAt,
                storageRemainingBytes = result.Storage?.RemainingBytes
            });
        }).RequireRateLimiting("upload-session");

        uploads.MapPut("/uploads/{uploadId:guid}/chunks/{index:int}", async (
            Guid uploadId, int index, HttpRequest request, HttpContext context,
            LessonCueDb db, UploadSessionService sessions, CancellationToken ct) =>
        {
            if (request.ContentLength is null)
                return Results.BadRequest(new { error = "Content-Length is required for every upload chunk." });
            var result = await sessions.WriteChunkAsync(db, uploadId, CurrentAccountId(context),
                index, request.Body, request.ContentLength.Value, ct);
            return result.Success ? Results.NoContent() : UploadError(result.StatusCode, result.Error!);
        }).DisableAntiforgery().RequireRateLimiting("upload-chunk");

        uploads.MapPost("/uploads/{uploadId:guid}/pause", async (
            Guid uploadId, LessonCueDb db, UploadSessionService sessions,
            HttpContext context, CancellationToken ct) =>
        {
            var result = await sessions.ChangeStateAsync(db, uploadId, CurrentAccountId(context), true, ct);
            return result.Success ? Results.Ok(sessions.Status(result.Session!)) : UploadError(result.StatusCode, result.Error!);
        });

        uploads.MapPost("/uploads/{uploadId:guid}/resume", async (
            Guid uploadId, LessonCueDb db, UploadSessionService sessions,
            HttpContext context, CancellationToken ct) =>
        {
            var result = await sessions.ChangeStateAsync(db, uploadId, CurrentAccountId(context), false, ct);
            return result.Success ? Results.Ok(sessions.Status(result.Session!)) : UploadError(result.StatusCode, result.Error!);
        });

        uploads.MapDelete("/uploads/{uploadId:guid}", async (
            Guid uploadId, LessonCueDb db, UploadSessionService sessions,
            HttpContext context, CancellationToken ct) =>
        {
            var result = await sessions.CancelAsync(db, uploadId, CurrentAccountId(context), ct);
            return result.Success ? Results.NoContent() : UploadError(result.StatusCode, result.Error!);
        });

        uploads.MapPost("/uploads/{uploadId:guid}/complete", async (
            Guid uploadId, UploadCompleteInput input, LessonCueDb db,
            UploadSessionService sessions, HttpContext context, CancellationToken ct) =>
        {
            var result = await sessions.CompleteAsync(db, uploadId, CurrentAccountId(context), input, ct);
            if (!result.Success) return UploadError(result.StatusCode, result.Error!);
            return result.Duplicate
                ? Results.Ok(new { duplicate = true, media = result.Media })
                : Results.Created($"/api/v1/media/{result.Media!.Id}", result.Media);
        }).RequireRateLimiting("upload-session");

        admin.MapGet("/screens", async (LessonCueDb db, CancellationToken ct) =>
        {
            var onlineCutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
            return await db.Screens.AsNoTracking().OrderBy(x => x.Name).Select(x => new
            {
                x.Id,
                x.Name,
                x.Platform,
                x.AssignedClassId,
                assignedClassName = db.Classes.Where(c => c.Id == x.AssignedClassId).OrderBy(c => c.Name).Select(c => c.Name).FirstOrDefault(),
                assignedSignageName = db.SignagePlaylists.Where(sign => sign.Id == x.AssignedSignageId)
                    .OrderBy(sign => sign.Name).Select(sign => sign.Name).FirstOrDefault(),
                x.VolunteerMode,
                x.SignageOnly,
                x.PermanentPairing,
                x.AssignedSignageId,
                x.LastSeenAt,
                online = x.LastSeenAt != null && x.LastSeenAt >= onlineCutoff,
                x.FreeBytes,
                x.FailedDownloads,
                x.Revoked,
                x.AppVersion,
                x.ManifestVersion,
                x.TagsCsv,
                x.Site,
                x.SignageOrientation,
                x.SignageWidth,
                x.SignageHeight,
                x.LastIpAddress,
                x.ControlVersion,
                x.ControlAction,
                x.ControlLessonId,
                x.ControlItemId,
                x.ControlPositionMs,
                x.ControlIssuedAt,
                x.AcknowledgedControlVersion,
                x.PlaybackState,
                x.PlaybackLessonId,
                x.PlaybackItemId,
                x.PlaybackPositionMs,
                x.PlaybackDurationMs,
                x.PlaybackVolumePercent,
                x.PlaybackUpdatedAt,
                x.PlaybackError,
                x.CachedItems,
                x.TotalItems,
                x.DeviceModel,
                x.OsVersion,
                x.CacheInventoryJson,
                x.DownloadQueueJson,
                x.CodecCapabilitiesJson,
                x.RecentErrorsJson,
                x.ConnectionDiagnosticsJson,
                x.ClockOffsetMs,
                x.NetworkLatencyMs,
                x.NetworkQuality,
                x.DiagnosticsUpdatedAt,
                x.AllowDiagnosticScreenshots,
                x.ScreenshotRequestId,
                x.ScreenshotRequestedAt,
                x.ScreenshotExpiresAt,
                x.ScreenshotStatus,
                x.ScreenshotCapturedAt,
                screenshotAvailable = x.ScreenshotStatus == "ready" && x.ScreenshotCapturedAt != null &&
                    x.ScreenshotCapturedAt >= DateTimeOffset.UtcNow.AddHours(-24)
            }).ToListAsync(ct);
        });

        screens.MapPost("/screens/{id:guid}/diagnostics/screenshot-request", async (Guid id, LessonCueDb db,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var screen = await db.Screens.SingleOrDefaultAsync(x => x.Id == id && !x.Revoked, ct);
            if (screen is null) return Results.NotFound();
            if (!screen.AllowDiagnosticScreenshots)
                return Results.Conflict(new { error = "Diagnostic screenshots are disabled for this screen. Enable the privacy control first." });
            if (screen.LastSeenAt is null || screen.LastSeenAt < DateTimeOffset.UtcNow.AddMinutes(-2))
                return Results.Conflict(new { error = "This screen is offline. Bring it online before requesting a screenshot." });
            screen.ScreenshotRequestId = Guid.NewGuid();
            screen.ScreenshotRequestedAt = DateTimeOffset.UtcNow;
            screen.ScreenshotExpiresAt = screen.ScreenshotRequestedAt.Value.AddMinutes(1);
            screen.ScreenshotStatus = "pending";
            Audit(db, "screen.screenshot.request", screen.Id, "One-time request; expires in 60 seconds");
            await db.SaveChangesAsync(ct);
            await hub.Clients.Group($"screen:{id}").SendAsync("DiagnosticScreenshotRequested",
                new { screen.ScreenshotRequestId, screen.ScreenshotExpiresAt }, ct);
            return Results.Accepted(value: new { requestId = screen.ScreenshotRequestId, expiresAt = screen.ScreenshotExpiresAt });
        });

        screens.MapGet("/screens/{id:guid}/diagnostics/screenshot", async (Guid id, LessonCueDb db, CancellationToken ct) =>
        {
            var screen = await db.Screens.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.Revoked, ct);
            if (screen?.ScreenshotStatus != "ready" || screen.ScreenshotCapturedAt is null ||
                screen.ScreenshotCapturedAt < DateTimeOffset.UtcNow.AddHours(-24) || string.IsNullOrWhiteSpace(screen.ScreenshotRelativePath))
                return Results.NotFound();
            var path = ContainedPath.ResolveExistingFile(dataPath, screen.ScreenshotRelativePath);
            if (path is null) return Results.NotFound();
            var contentType = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            return Results.File(path, contentType, enableRangeProcessing: false);
        });

        screens.MapDelete("/screens/{id:guid}/diagnostics/screenshot", async (Guid id, LessonCueDb db, CancellationToken ct) =>
        {
            var screen = await db.Screens.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null) return Results.NotFound();
            DeleteDiagnosticScreenshot(dataPath, screen.ScreenshotRelativePath);
            ClearDiagnosticScreenshot(screen);
            Audit(db, "screen.screenshot.delete", screen.Id, "Diagnostic screenshot deleted");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        playback.MapPost("/screens/{id:guid}/control", async (Guid id, ScreenControlInput input, HttpContext context, LessonCueDb db,
            IHubContext<SyncHub> hub, ControllerSessionService controllerSessions, CancellationToken ct) =>
        {
            var action = input.Action.Trim().ToLowerInvariant();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "play", "pause", "resume", "stop", "next", "previous", "seek" };
            if (!allowed.Contains(action)) return Results.BadRequest(new { error = "Unsupported playback command." });
            var gate = PlaybackControlGates[(uint)id.GetHashCode() % PlaybackControlGates.Length];
            await gate.WaitAsync(ct);
            try
            {
                var screen = await db.Screens.SingleOrDefaultAsync(x => x.Id == id && !x.Revoked, ct);
                if (screen is null) return Results.NotFound();
                if (screen.SignageOnly)
                    return Results.Conflict(new { error = "This screen is assigned to signage only and cannot receive lesson playback commands." });
                var controllerContext = context.Request.Headers["X-LessonCue-Controller"].ToString();
                if (!controllerContext.StartsWith("room:", StringComparison.OrdinalIgnoreCase) &&
                    !controllerContext.StartsWith("session:", StringComparison.OrdinalIgnoreCase) &&
                    !controllerContext.Equals("universal", StringComparison.OrdinalIgnoreCase))
                    return Results.Forbid();
                if (controllerContext.StartsWith("room:", StringComparison.OrdinalIgnoreCase) ||
                    controllerContext.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
                {
                    var requireLocal = await db.Organizations.AsNoTracking()
                        .OrderBy(item => item.Id).Select(x => x.RequireLocalRoomControllers).FirstAsync(ct);
                    if (!ControllerAccessPolicy.CanUseRoomController(requireLocal, context.User, context.Request.Host.Host,
                        context.Connection.RemoteIpAddress))
                        return Results.Json(new
                        {
                            error = "This room controller is restricted to the campus network. Open it from the server's .local address."
                        }, statusCode: StatusCodes.Status403Forbidden);
                }
                if (controllerContext.Equals("universal", StringComparison.OrdinalIgnoreCase))
                {
                    var grant = context.Request.Headers["X-LessonCue-Controller-Grant"].ToString();
                    if (!controllerSessions.IsUniversalGrantValid(grant))
                        return Results.Json(new { error = "Enter the current universal controller PIN." }, statusCode: StatusCodes.Status403Forbidden);
                }
                if (controllerContext.StartsWith("room:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Guid.TryParse(controllerContext[5..], out var roomId) || screen.AssignedClassId != roomId)
                        return Results.Forbid();
                    if (input.LessonId is Guid scopedLessonId && !await db.Lessons.AnyAsync(x => x.Id == scopedLessonId && x.ClassId == roomId, ct))
                        return Results.Forbid();
                }
                if (controllerContext.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
                {
                    var session = await ResolveControllerSessionAsync(controllerContext[8..], controllerSessions, db, ct);
                    if (session is null || screen.AssignedClassId != session.ClassId ||
                        session.LessonId is Guid restrictedCurrentLessonId && action != "play" && screen.PlaybackLessonId != restrictedCurrentLessonId ||
                        input.LessonId is Guid sessionLessonId &&
                        (session.LessonId is Guid restrictedLessonId && sessionLessonId != restrictedLessonId ||
                         !await db.Lessons.AnyAsync(x => x.Id == sessionLessonId && x.ClassId == session.ClassId, ct)))
                        return Results.Forbid();
                }
                if (action == "play")
                {
                    if (input.LessonId is not Guid lessonId)
                        return Results.BadRequest(new { error = "Choose a lesson to play." });
                    var lesson = await db.Lessons.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == lessonId && !x.Archived, ct);
                    if (lesson is null) return Results.BadRequest(new { error = "The selected lesson is unavailable." });
                    if (input.ItemId is Guid itemId && lesson.Items.All(x => x.Id != itemId))
                        return Results.BadRequest(new { error = "The selected item is not in that lesson." });
                }
                screen.ControlVersion = screen.ControlVersion == int.MaxValue ? 1 : screen.ControlVersion + 1;
                screen.ControlAction = action;
                screen.ControlLessonId = input.LessonId;
                screen.ControlItemId = input.ItemId;
                screen.ControlPositionMs = input.PositionMs is null ? null : Math.Max(0, input.PositionMs.Value);
                screen.ControlIssuedAt = DateTimeOffset.UtcNow;
                db.PlaybackCommands.Add(new PlaybackCommandRecord
                {
                    ScreenId = screen.Id,
                    Version = screen.ControlVersion,
                    Action = action,
                    LessonId = input.LessonId,
                    ItemId = input.ItemId,
                    PositionMs = screen.ControlPositionMs,
                    IssuedAt = screen.ControlIssuedAt.Value
                });
                var oldestRetainedVersion = Math.Max(0, screen.ControlVersion - 1000);
                var staleCommands = await db.PlaybackCommands.Where(x => x.ScreenId == id && x.Version < oldestRetainedVersion).ToListAsync(ct);
                db.PlaybackCommands.RemoveRange(staleCommands);
                Audit(db, "screen.control", screen.Id, $"{action}:{screen.ControlVersion}");
                await db.SaveChangesAsync(ct);
                await hub.Clients.Group($"screen:{id}").SendAsync("PlaybackCommand", new { screen.ControlVersion }, ct);
                return Results.Accepted(value: new { version = screen.ControlVersion, action, lessonId = input.LessonId,
                    itemId = input.ItemId, positionMs = screen.ControlPositionMs, issuedAt = screen.ControlIssuedAt, state = screen.PlaybackState });
            }
            finally { gate.Release(); }
        }).AllowAnonymous();

        screens.MapPost("/screens/{id:guid}/assignment-check", async (Guid id, ScreenAssignmentCheckInput input,
            LessonCueDb db, CancellationToken ct) =>
        {
            var screen = await db.Screens.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id && !value.Revoked, ct);
            if (screen is null) return Results.NotFound();
            if (input.AssignedClassId is null)
                return Results.Ok(new { platform = DisplayCapabilities.Normalize(screen.Platform),
                    contract = DisplayCapabilities.For(screen.Platform), issues = Array.Empty<DisplayCompatibilityIssue>() });
            if (!await db.Classes.AsNoTracking().AnyAsync(value => value.Id == input.AssignedClassId, ct))
                return Results.BadRequest(new { error = "The selected class no longer exists." });
            var lessons = await db.Lessons.AsNoTracking().Include(value => value.Items)
                .ThenInclude(value => value.MediaAsset).Where(value =>
                    value.ClassId == input.AssignedClassId && !value.Archived).AsSplitQuery().ToListAsync(ct);
            var issues = DisplayCapabilities.AssessLessons(screen.Platform, lessons);
            return Results.Ok(new { platform = DisplayCapabilities.Normalize(screen.Platform),
                contract = DisplayCapabilities.For(screen.Platform), issues });
        });

        screens.MapPatch("/screens/{id:guid}", async (Guid id, ScreenUpdateInput input, LessonCueDb db,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var screen = await db.Screens.FindAsync([id], ct);
            if (screen is null) return Results.NotFound();
            var effectiveSignageOnly = input.SignageOnly ?? screen.SignageOnly;
            if (input.Name is not null) screen.Name = input.Name.Trim();
            if (input.ClearAssignment)
            {
                screen.AssignedClassId = null;
                screen.AssignedSignageId = null;
            }
            else if (input.AssignedClassId is not null)
            {
                if (!await db.Classes.AsNoTracking().AnyAsync(value => value.Id == input.AssignedClassId, ct))
                    return Results.BadRequest(new { error = "The selected class no longer exists." });
                var lessons = await db.Lessons.AsNoTracking().Include(value => value.Items)
                    .ThenInclude(value => value.MediaAsset).Where(value =>
                        value.ClassId == input.AssignedClassId && !value.Archived).AsSplitQuery().ToListAsync(ct);
                var issues = DisplayCapabilities.AssessLessons(screen.Platform, lessons);
                if (issues.Count > 0 && !input.AllowUnsupportedContent)
                    return Results.Conflict(new
                    {
                        code = "display_capability_warning",
                        error = $"{issues.Count} lesson cue{(issues.Count == 1 ? "" : "s")} will use a safe fallback on {DisplayCapabilities.For(screen.Platform).DisplayName}.",
                        platform = DisplayCapabilities.Normalize(screen.Platform),
                        issues
                    });
                screen.AssignedClassId = input.AssignedClassId;
                screen.AssignedSignageId = null;
                if (issues.Count > 0)
                    Audit(db, "screen.assignment.compatibility-override", screen.Id,
                        $"{input.AssignedClassId}:{issues.Count}");
            }
            if (input.ClearSignageAssignment)
                screen.AssignedSignageId = null;
            else if (input.AssignedSignageId is not null)
            {
                if (!effectiveSignageOnly)
                    return Results.BadRequest(new { error = "Only signage-only screens can be assigned a Sign." });
                if (!await db.SignagePlaylists.AsNoTracking().AnyAsync(value => value.Id == input.AssignedSignageId &&
                        value.Mode == "sign" && value.Enabled, ct))
                    return Results.BadRequest(new { error = "The selected Sign no longer exists or is disabled." });
                screen.AssignedSignageId = input.AssignedSignageId;
                screen.AssignedClassId = null;
            }
            if (input.SignageOnly is bool signageOnly)
            {
                screen.SignageOnly = signageOnly;
                if (signageOnly) screen.AssignedClassId = null;
                else screen.AssignedSignageId = null;
            }
            if (input.PermanentPairing is bool permanentPairing) screen.PermanentPairing = permanentPairing;
            if (input.VolunteerMode is not null) screen.VolunteerMode = input.VolunteerMode.Value;
            if (input.TagsCsv is not null) screen.TagsCsv = input.TagsCsv.Trim();
            if (input.Site is not null) screen.Site = input.Site.Trim();
            if (input.SignageOrientation is not null)
                screen.SignageOrientation = input.SignageOrientation is "landscape" or "portrait" ? input.SignageOrientation : "auto";
            if (input.SignageWidth is not null || input.SignageHeight is not null)
            {
                if (input.SignageWidth is < 240 or > 7680 || input.SignageHeight is < 240 or > 7680)
                    return Results.BadRequest(new { error = "Custom signage dimensions must be from 240 to 7,680 pixels." });
                screen.SignageWidth = input.SignageWidth;
                screen.SignageHeight = input.SignageHeight;
            }
            if (input.AllowDiagnosticScreenshots is bool allowScreenshots)
            {
                screen.AllowDiagnosticScreenshots = allowScreenshots;
                if (!allowScreenshots)
                {
                    DeleteDiagnosticScreenshot(dataPath, screen.ScreenshotRelativePath);
                    ClearDiagnosticScreenshot(screen);
                }
            }
            Audit(db, "screen.update", screen.Id, screen.Name);
            await db.SaveChangesAsync(ct);
            await hub.Clients.Group($"screen:{id}").SendAsync("ManifestInvalidated", new { type = "MANIFEST_INVALIDATED" }, ct);
            return Results.Ok(screen);
        });

        screens.MapPost("/screens/{id:guid}/browser-link", async (Guid id, HttpRequest request, LessonCueDb db,
            CancellationToken ct) =>
        {
            var screen = await db.Screens.SingleOrDefaultAsync(x => x.Id == id && !x.Revoked, ct);
            if (screen is null) return Results.NotFound();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            db.DeviceCredentials.Add(new DeviceCredential { ScreenId = screen.Id, TokenHash = HashDeviceToken(token) });
            Audit(db, "screen.browser-link", screen.Id, screen.Name);
            await db.SaveChangesAsync(ct);
            var origin = $"{request.Scheme}://{request.Host}";
            return Results.Ok(new { url = $"{origin}/display?screenId={screen.Id}&token={token}&name={Uri.EscapeDataString(screen.Name)}" });
        });

        screens.MapDelete("/screens/{id:guid}", async (Guid id, LessonCueDb db, CancellationToken ct) =>
        {
            var screen = await db.Screens.FindAsync([id], ct);
            if (screen is null) return Results.NotFound();
            screen.Revoked = true;
            var credentials = await db.DeviceCredentials.Where(x => x.ScreenId == id && x.RevokedAt == null).ToListAsync(ct);
            foreach (var credential in credentials) credential.RevokedAt = DateTimeOffset.UtcNow;
            Audit(db, "screen.revoke", screen.Id, screen.Name);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        appSettings.MapGet("/audit", async (LessonCueDb db, CancellationToken ct) =>
            (await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.Id).Take(250).ToListAsync(ct)).OrderByDescending(x => x.Timestamp));

        MapOperations(admin, dataPath);
    }

    private static void MapOperations(RouteGroupBuilder admin, string dataPath)
    {
        var planning = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Planning);
        var playback = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Playback);
        var userAdmin = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Users);
        var settings = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Settings);
        var appSettings = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.AppSettings);
        var backupsAdmin = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Backups);
        var updatesAdmin = admin.MapGroup("").RequireAuthorization(LessonCuePermissions.Updates);

        settings.MapGet("/organization", async (LessonCueDb db, CancellationToken ct) =>
            await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct));

        settings.MapGet("/troubleshooting-log", async (int? limit, bool? failuresOnly, LessonCueDb db,
            TroubleshootingReportBuilder reports, CancellationToken ct) =>
        {
            var failureFilter = failuresOnly == true;
            var safeLimit = Math.Clamp(limit ?? (failureFilter ? 10_000 : 500), 1, failureFilter ? 10_000 : 2_000);
            return Results.Ok(await reports.BuildAsync(db, safeLimit, failureFilter, ct));
        });

        settings.MapGet("/registration/settings", async (LessonCueDb db, AccountEmailService email,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Ok(new
            {
                mode = organization.RegistrationMode,
                organization.PublicBaseUrl,
                organization.EmailProvider,
                organization.EmailFromAddress,
                organization.EmailFromName,
                emailConfigured = email.Status(organization.EmailProvider).Configured
            });
        });

        appSettings.MapGet("/registration/mode", async (LessonCueDb db, AccountEmailService email,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Ok(new
            {
                mode = organization.RegistrationMode,
                emailConfigured = email.Status(organization.EmailProvider).Configured
            });
        });

        appSettings.MapPut("/registration/mode", async (RegistrationModeInput input, LessonCueDb db,
            AccountEmailService email, CancellationToken ct) =>
        {
            var mode = input.Mode.Trim().ToLowerInvariant();
            if (mode is not ("closed" or "open" or "code" or "approval"))
                return Results.BadRequest(new { error = "Registration mode must be closed, approval, open, or code." });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (mode != "closed" && !email.Status(organization.EmailProvider).Configured)
                return Results.BadRequest(new { error = "A Service Admin must configure account email before self-service registration can be enabled." });
            organization.RegistrationMode = mode;
            Audit(db, "registration.mode.update", organization.Id, mode);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mode, emailConfigured = email.Status(organization.EmailProvider).Configured });
        });

        appSettings.MapPut("/registration/registration", async (RegistrationSectionInput input, LessonCueDb db,
            AccountEmailService email, CancellationToken ct) =>
        {
            var mode = input.Mode.Trim().ToLowerInvariant();
            var publicBaseUrl = input.PublicBaseUrl.Trim().TrimEnd('/');
            if (mode is not ("closed" or "open" or "code" or "approval"))
                return Results.BadRequest(new { error = "Registration mode must be closed, approval, open, or code." });
            if (publicBaseUrl.Length > 253)
                return Results.BadRequest(new { error = "The public address is too long." });
            if (!string.IsNullOrWhiteSpace(publicBaseUrl) &&
                (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUrl) ||
                 publicUrl.Scheme != Uri.UriSchemeHttps && !publicUrl.IsLoopback))
                return Results.BadRequest(new { error = "Public account URL must use HTTPS, except for a loopback development address." });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (mode != "closed" && !email.Status(organization.EmailProvider).Configured)
                return Results.BadRequest(new { error = "Configure account email before enabling self-service registration." });
            organization.RegistrationMode = mode;
            organization.PublicBaseUrl = publicBaseUrl;
            Audit(db, "registration.mode.update", organization.Id, mode);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mode, emailConfigured = email.Status(organization.EmailProvider).Configured });
        });

        settings.MapPut("/registration/email-settings", async (EmailSettingsInput input, LessonCueDb db,
            AccountEmailService email, CancellationToken ct) =>
        {
            var provider = input.EmailProvider.Trim().ToLowerInvariant();
            if (provider is not ("none" or "resend" or "brevo"))
                return Results.BadRequest(new { error = "Email provider must be none, Resend, or Brevo." });
            if (provider != "none" && (!IsEmail(input.EmailFromAddress) ||
                string.IsNullOrWhiteSpace(input.EmailFromName) || input.EmailFromName.Trim().Length > 120))
                return Results.BadRequest(new { error = "A valid sender address and name are required." });
            if (input.ApiKey?.Length > 2048)
                return Results.BadRequest(new { error = "The provider key is too long." });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RegistrationMode != "closed" && provider == "none")
                return Results.BadRequest(new { error = "Self-service registration requires Resend or Brevo email delivery." });
            try { await email.ConfigureAsync(provider, input.ApiKey, ct); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            if (organization.RegistrationMode != "closed" && !email.Status(provider).Configured)
                return Results.BadRequest(new { error = "Self-service registration requires configured email delivery." });
            organization.EmailProvider = provider;
            organization.EmailFromAddress = input.EmailFromAddress.Trim().ToLowerInvariant();
            organization.EmailFromName = input.EmailFromName.Trim();
            Audit(db, "registration.email-settings.update", organization.Id, provider);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { emailConfigured = email.Status(provider).Configured });
        });

        settings.MapPut("/registration/settings", async (RegistrationSettingsInput input, LessonCueDb db,
            AccountEmailService email, CancellationToken ct) =>
        {
            var mode = input.Mode.Trim().ToLowerInvariant();
            var provider = input.EmailProvider.Trim().ToLowerInvariant();
            if (mode is not ("closed" or "open" or "code" or "approval"))
                return Results.BadRequest(new { error = "Registration mode must be closed, approval, open, or code." });
            if (provider is not ("none" or "resend" or "brevo"))
                return Results.BadRequest(new { error = "Email provider must be none, Resend, or Brevo." });
            if (mode != "closed" && provider == "none")
                return Results.BadRequest(new { error = "Approval, open, or code registration requires Resend or Brevo email delivery." });
            if (provider != "none" && (!IsEmail(input.EmailFromAddress) ||
                string.IsNullOrWhiteSpace(input.EmailFromName) || input.EmailFromName.Trim().Length > 120))
                return Results.BadRequest(new { error = "A valid sender address and name are required." });
            if (input.PublicBaseUrl.Trim().Length > 253 || input.ApiKey?.Length > 2048)
                return Results.BadRequest(new { error = "The public address or provider key is too long." });
            if (!string.IsNullOrWhiteSpace(input.PublicBaseUrl) &&
                (!Uri.TryCreate(input.PublicBaseUrl.Trim(), UriKind.Absolute, out var publicUrl) ||
                 publicUrl.Scheme != Uri.UriSchemeHttps && !publicUrl.IsLoopback))
                return Results.BadRequest(new { error = "Public account URL must use HTTPS, except for a loopback development address." });
            try { await email.ConfigureAsync(provider, input.ApiKey, ct); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            if (mode != "closed" && !email.Status(provider).Configured)
                return Results.BadRequest(new { error = "Configure the email provider API key before enabling registration." });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            organization.RegistrationMode = mode;
            organization.PublicBaseUrl = input.PublicBaseUrl.Trim().TrimEnd('/');
            organization.EmailProvider = provider;
            organization.EmailFromAddress = input.EmailFromAddress.Trim().ToLowerInvariant();
            organization.EmailFromName = input.EmailFromName.Trim();
            Audit(db, "registration.settings.update", organization.Id, $"{mode}:{provider}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { emailConfigured = email.Status(provider).Configured });
        });

        settings.MapPost("/registration/email/test", async (TestAccountEmailInput input, LessonCueDb db,
            AccountEmailService email, HttpContext context, CancellationToken ct) =>
        {
            var recipient = input.Recipient?.Trim().ToLowerInvariant() ?? "";
            if (recipient.Length > 200 || !IsEmail(recipient))
                return Results.BadRequest(new { error = "Enter a valid recipient email address." });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (!email.Status(organization.EmailProvider).Configured)
                return Results.Conflict(new { error = "Save a configured Resend or Brevo provider before sending a test." });
            try
            {
                await email.SendAsync(organization, recipient, "LessonCue email test",
                    $"<p>Email delivery for <strong>{System.Net.WebUtility.HtmlEncode(organization.Name)}</strong> is working.</p>" +
                    "<p>This test was sent from the local LessonCue server settings.</p>", ct);
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = context.User.Identity?.Name ?? "admin", Action = "registration.email.test",
                    Object = organization.Id.ToString(), Summary = $"Delivered through {organization.EmailProvider}."
                });
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { message = $"Test email sent to {recipient}." });
            }
            catch (Exception error)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = context.User.Identity?.Name ?? "admin", Action = "registration.email.test",
                    Object = organization.Id.ToString(), Result = "failed", Summary = organization.EmailProvider
                });
                await db.SaveChangesAsync(ct);
                return Results.Json(new { error = $"The provider could not deliver the test: {error.Message}" }, statusCode: 502);
            }
        });

        settings.MapGet("/troubleshooting-email", async (LessonCueDb db, AccountEmailService email,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Ok(TroubleshootingEmailService.Status(
                organization, email.Status(organization.EmailProvider).Configured, DateTimeOffset.UtcNow));
        });

        settings.MapPut("/troubleshooting-email", async (TroubleshootingEmailSettingsInput input,
            LessonCueDb db, AccountEmailService email, CancellationToken ct) =>
        {
            var recipient = input.Recipient?.Trim().ToLowerInvariant() ?? "";
            if (recipient.Length > 200 || !string.IsNullOrWhiteSpace(recipient) && !TroubleshootingEmailSchedule.IsEmail(recipient))
                return Results.BadRequest(new { error = "Enter a valid daily troubleshooting recipient email address." });
            if (!TroubleshootingEmailSchedule.TryNormalizeTime(input.TimeLocal, out var time))
                return Results.BadRequest(new { error = "Choose a daily delivery time in HH:mm format." });

            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (input.Enabled)
            {
                if (!TroubleshootingEmailSchedule.IsEmail(recipient))
                    return Results.BadRequest(new { error = "A recipient is required when daily troubleshooting email is enabled." });
                if (!email.Status(organization.EmailProvider).Configured)
                    return Results.Conflict(new { error = "Configure Resend or Brevo account email before enabling daily troubleshooting delivery." });
            }

            organization.DailyTroubleshootingEmailEnabled = input.Enabled;
            organization.DailyTroubleshootingEmailRecipient = recipient;
            organization.DailyTroubleshootingEmailTime = time;
            organization.DailyTroubleshootingEmailLastError = null;
            Audit(db, "troubleshooting.email-settings.update", organization.Id,
                input.Enabled ? $"enabled:{time}" : "disabled");
            await db.SaveChangesAsync(ct);
            return Results.Ok(TroubleshootingEmailService.Status(
                organization, email.Status(organization.EmailProvider).Configured, DateTimeOffset.UtcNow));
        });

        settings.MapPost("/troubleshooting-email/send-now", async (TroubleshootingEmailService dailyEmail,
            CancellationToken ct) =>
        {
            try { return Results.Accepted("/api/v1/troubleshooting-email", await dailyEmail.QueueSendNowAsync(ct)); }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException)
            {
                return Results.Json(new { error = error.Message }, statusCode: 502);
            }
        });

        settings.MapGet("/troubleshooting-review", async (LessonCueDb db, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Ok(TroubleshootingReviewService.Status(organization, DateTimeOffset.UtcNow));
        });

        settings.MapPut("/troubleshooting-review", async (TroubleshootingReviewSettingsInput input,
            LessonCueDb db, CancellationToken ct) =>
        {
            var candidate = new TroubleshootingReviewConfig(
                input.Enabled, input.Provider, input.Frequency, input.TimeLocal,
                input.WeeklyDay, input.MonthlyDay, input.CustomInterval, input.CustomUnit);
            if (!TroubleshootingReviewSchedule.TryNormalize(candidate, out var normalized, out var error))
                return Results.BadRequest(new { error });

            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            organization.TroubleshootingReviewSettingsJson =
                TroubleshootingReviewConfiguration.Serialize(normalized);
            organization.TroubleshootingReviewLastError = null;
            Audit(db, "troubleshooting.review-settings.update", organization.Id,
                normalized.Enabled ? $"{normalized.Provider}:{normalized.Frequency}" : "disabled");
            await db.SaveChangesAsync(ct);
            return Results.Ok(TroubleshootingReviewService.Status(organization, DateTimeOffset.UtcNow));
        });

        settings.MapPost("/troubleshooting-review/run-now", async (TroubleshootingReviewService review,
            CancellationToken ct) => Results.Accepted(
                "/api/v1/troubleshooting-review", await review.QueueRunNowAsync(ct)));

        settings.MapGet("/troubleshooting-review/artifact", async (string? kind, LessonCueDb db,
            TroubleshootingReviewService review, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var artifact = await review.ReadArtifactAsync(organization, kind ?? "review", ct);
            return artifact is null
                ? Results.NotFound(new { error = "No troubleshooting review artifact is available." })
                : Results.File(artifact.Value.Content, artifact.Value.ContentType, artifact.Value.FileName);
        });

        settings.MapGet("/troubleshooting-review/codex-pull-prompt", async (LessonCueDb db,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Text(
                TroubleshootingReviewService.CodexPullPrompt(organization),
                "text/markdown",
                Encoding.UTF8);
        });

        appSettings.MapGet("/registration/codes", async (LessonCueDb db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var codes = await db.RegistrationCodes.AsNoTracking().ToListAsync(ct);
            return Results.Ok(codes.OrderByDescending(x => x.CreatedAt).Select(x => new
            {
                x.Id, x.Label, x.Hint, x.CreatedAt, x.ExpiresAt, x.RevokedAt, x.Uses, x.MaxUses,
                active = x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now) &&
                    (x.MaxUses == null || x.Uses < x.MaxUses)
            }));
        });

        appSettings.MapPost("/registration/codes", async (RegistrationCodeInput input, LessonCueDb db,
            CancellationToken ct) =>
        {
            if (input.Label.Trim().Length > 120) return Results.BadRequest(new { error = "Label cannot exceed 120 characters." });
            if (input.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "Expiration must be in the future." });
            if (input.MaxUses is <= 0 or > 100000) return Results.BadRequest(new { error = "Maximum uses must be between 1 and 100,000." });
            var raw = AccountEmailService.NewRegistrationCode();
            var code = new RegistrationCode
            {
                CodeHash = AccountEmailService.Hash(raw), Hint = raw[^4..],
                Label = string.IsNullOrWhiteSpace(input.Label) ? "Registration code" : input.Label.Trim(),
                ExpiresAt = input.ExpiresAt, MaxUses = input.MaxUses
            };
            db.RegistrationCodes.Add(code);
            Audit(db, "registration.code.create", code.Id, code.Label);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/registration/codes/{code.Id}", new { code.Id, code = raw, code.Hint });
        });

        appSettings.MapPut("/registration/codes/{id:guid}", async (Guid id, RegistrationCodeInput input,
            LessonCueDb db, CancellationToken ct) =>
        {
            var code = await db.RegistrationCodes.FindAsync([id], ct);
            if (code is null) return Results.NotFound();
            if (input.Label.Trim().Length > 120) return Results.BadRequest(new { error = "Label cannot exceed 120 characters." });
            if (input.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "Expiration must be in the future." });
            if (input.MaxUses is <= 0 or > 100000) return Results.BadRequest(new { error = "Maximum uses must be between 1 and 100,000." });
            code.Label = string.IsNullOrWhiteSpace(input.Label) ? code.Label : input.Label.Trim();
            code.ExpiresAt = input.ExpiresAt;
            code.MaxUses = input.MaxUses;
            Audit(db, "registration.code.update", code.Id, code.Label);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        appSettings.MapPost("/registration/codes/{id:guid}/rotate", async (Guid id, LessonCueDb db,
            CancellationToken ct) =>
        {
            var existing = await db.RegistrationCodes.FindAsync([id], ct);
            if (existing is null) return Results.NotFound();
            existing.RevokedAt = DateTimeOffset.UtcNow;
            var raw = AccountEmailService.NewRegistrationCode();
            var replacement = new RegistrationCode
            {
                CodeHash = AccountEmailService.Hash(raw), Hint = raw[^4..], Label = existing.Label,
                ExpiresAt = existing.ExpiresAt, MaxUses = existing.MaxUses
            };
            db.RegistrationCodes.Add(replacement);
            Audit(db, "registration.code.rotate", existing.Id, replacement.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { replacement.Id, code = raw, replacement.Hint });
        });

        appSettings.MapDelete("/registration/codes/{id:guid}", async (Guid id, LessonCueDb db,
            CancellationToken ct) =>
        {
            var code = await db.RegistrationCodes.FindAsync([id], ct);
            if (code is null) return Results.NotFound();
            code.RevokedAt = DateTimeOffset.UtcNow;
            Audit(db, "registration.code.revoke", code.Id, code.Label);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        settings.MapPut("/organization", async (OrganizationInput input, LessonCueDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.TimeZone))
                return Results.BadRequest(new { error = "Organization name and time zone are required." });
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone); }
            catch { return Results.BadRequest(new { error = "The server does not recognize that IANA time zone." }); }
            if (!IsColor(input.PrimaryColor) || !IsColor(input.AccentColor) ||
                input.NavigationTextColor is not null && !IsColor(input.NavigationTextColor) ||
                input.SelectedTabColor is not null && !IsColor(input.SelectedTabColor))
                return Results.BadRequest(new { error = "Brand colors must use six-digit hex notation." });
            if (input.SignageSourceAllowlist is not null &&
                !SignageLayout.TryNormalizeAllowlist(input.SignageSourceAllowlist, out _, out var allowlistError))
                return Results.BadRequest(new { error = allowlistError });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            organization.Name = input.Name.Trim(); organization.SiteName = input.SiteName.Trim();
            organization.TimeZone = input.TimeZone.Trim(); organization.WeekStartsOn = input.WeekStartsOn == "Monday" ? "Monday" : "Sunday";
            organization.DefaultLessonDurationMinutes = Math.Clamp(input.DefaultLessonDurationMinutes, 5, 480);
            organization.DefaultRetentionDays = Math.Clamp(input.DefaultRetentionDays, 1, 3650);
            organization.PrimaryColor = input.PrimaryColor; organization.AccentColor = input.AccentColor;
            if (input.NavigationTextColor is not null) organization.NavigationTextColor = input.NavigationTextColor;
            if (input.SelectedTabColor is not null) organization.SelectedTabColor = input.SelectedTabColor;
            organization.WelcomeMessage = input.WelcomeMessage.Trim();
            if (input.AdaptiveTranscodingEnabled is not null) organization.AdaptiveTranscodingEnabled = input.AdaptiveTranscodingEnabled.Value;
            if (input.TranscodeLeadDays is not null) organization.TranscodeLeadDays = Math.Clamp(input.TranscodeLeadDays.Value, 1, 30);
            if (input.HardwareAccelerationEnabled is not null) organization.HardwareAccelerationEnabled = input.HardwareAccelerationEnabled.Value;
            if (input.RequireLocalRoomControllers is not null) organization.RequireLocalRoomControllers = input.RequireLocalRoomControllers.Value;
            if (input.SignageSourceAllowlist is not null)
            {
                SignageLayout.TryNormalizeAllowlist(input.SignageSourceAllowlist, out var origins, out _);
                organization.SignageSourceAllowlistJson = JsonSerializer.Serialize(origins);
            }
            // Kept in the input contract for older clients, but signage is now
            // always available and cannot be hidden by an organization toggle.
            organization.SignageEnabled = true;
            Audit(db, "organization.update", organization.Id, organization.Name); await db.SaveChangesAsync(ct);
            return Results.Ok(organization);
        });

        settings.MapPut("/organization/signage-availability", async (SignageAvailabilityInput input,
            LessonCueDb db, CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            organization.SignageEnabled = true;
            Audit(db, "organization.signage-availability", organization.Id, "always-live");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { signageEnabled = true });
        });

        settings.MapPost("/hardware-acceleration/check", async (HardwareAccelerationService hardware,
            CancellationToken ct) => Results.Ok(await hardware.RefreshAsync(ct)));

        appSettings.MapPut("/media-taxonomy", async (MediaTaxonomyInput input, LessonCueDb db,
            HttpContext context, CancellationToken ct) =>
        {
            if (!MediaTaxonomy.TryCreate(input.Folders, input.Tags, out var taxonomy, out var error))
                return Results.BadRequest(new { error });

            var media = await db.MediaAssets.IgnoreQueryFilters().AsNoTracking()
                .Select(item => new { item.Folder, item.TagsCsv }).ToListAsync(ct);
            var missingFolders = media.Select(item => MediaTaxonomy.NormalizeFolder(item.Folder))
                .Where(folder => folder.Length > 0 && !taxonomy.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(folder => folder).ToArray();
            var missingTags = media.SelectMany(item => MediaTaxonomy.SplitTags(item.TagsCsv))
                .Where(tag => !taxonomy.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
            if (missingFolders.Length > 0 || missingTags.Length > 0)
            {
                var details = string.Join("; ", new[]
                {
                    missingFolders.Length > 0 ? $"folders in use: {string.Join(", ", missingFolders)}" : null,
                    missingTags.Length > 0 ? $"tags in use: {string.Join(", ", missingTags)}" : null
                }.Where(value => value is not null));
                return Results.Conflict(new { error = $"Reassign existing media before removing approved {details}." });
            }

            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            MediaTaxonomy.Store(organization, taxonomy);
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = context.User.Identity?.Name ?? "admin", Action = "media.taxonomy.update",
                Object = organization.Id.ToString(), Summary = $"{taxonomy.Folders.Count} folders; {taxonomy.Tags.Count} tags"
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(taxonomy);
        });

        appSettings.MapPut("/controller-pin", async (ControllerPinInput input, LessonCueDb db,
            IPasswordHasher<Organization> hasher, ControllerSessionService controllerSessions,
            IDataProtectionProvider protection, CancellationToken ct) =>
        {
            if (input.Pin.Length != 6 || input.Pin.Any(character => !char.IsAsciiDigit(character)))
                return Results.BadRequest(new { error = "Universal controller PIN must be exactly six digits." });
            var organization = await db.Organizations.SingleAsync(ct);
            organization.ControllerPinHash = hasher.HashPassword(organization, input.Pin);
            organization.ControllerPinProtected = protection.CreateProtector(ControllerPinProtectionPurpose).Protect(input.Pin);
            controllerSessions.RevokeUniversalGrants();
            Audit(db, "controller.pin.update", organization.Id, "Universal controller PIN changed");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ------------------------------------------------- URL shortener ---
        // Optional. Every one of these answers sensibly when the integration
        // has never been installed, so the console can render its card without
        // knowing in advance.

        appSettings.MapGet("/shortener", async (LessonCueDb db, ShortenerService shortener, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var settings = ShortenerConfiguration.Read(organization);
            var status = await shortener.StatusAsync(ct);
            return Results.Ok(new
            {
                state = status.State.ToString(),
                enabled = settings.Enabled,
                domain = settings.Domain,
                adminHost = settings.AdminHost,
                suggestedAdminHost = ShortenerConfiguration.DefaultAdminHost(settings.Domain),
                upstream = settings.Upstream,
                suggestedUpstream = ShortenerConfiguration.SuggestUpstream(
                    string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
                    PortFromEnvironment("SHORTENER_HTTP_PORT", 8081)),
                rootRedirectMode = ShortenerConfiguration.RootRedirectName(settings.RootRedirect),
                rootRedirectUrl = settings.RootRedirectUrl,
                // What SHORT_DOMAIN_ROOT_REDIRECT has to be set to for this
                // choice to take effect, and whether the container needs
                // restarting for it.
                rootRedirectValue = ShortenerConfiguration.ResolveRootRedirect(settings, organization.PublicBaseUrl.Trim().TrimEnd('/')),
                publicUrl = status.PublicUrl,
                adminUrl = status.AdminUrl,
                lessonCuePublicUrl = organization.PublicBaseUrl,
                poolVersion = ReservedGameCodes.Version,
                poolTotal = status.PoolTotal,
                poolPresent = status.PoolPresent,
                activeCodes = status.PoolActive,
                detail = status.Detail,
                missing = status.Missing,
                conflicts = status.Conflicts,
                failures = status.Failures,
                // Whether a key exists, never the key itself.
                integrationKeyConfigured = shortener.IntegrationKey is not null,
                // Likewise for the console's password: whether one was chosen.
                consolePasswordSet = shortener.ConsolePasswordSet,
                consoleUser = ShortenerService.ConsoleUser,
                installRequestedFor = shortener.InstallRequestedFor,
                canRequestInstall = shortener.CanRequestInstall,
                installResult = shortener.InstallResult is { } outcome
                    ? new { outcome.Installed, outcome.Domain, outcome.AppliedAt, outcome.Error }
                    : null,
            });
        });

        appSettings.MapPut("/shortener", async (ShortenerConfigureInput input, LessonCueDb db,
            ShortenerService shortener, CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            string domain, adminHost, upstream, redirect;
            ShortenerRootRedirect mode;
            try
            {
                domain = ShortenerConfiguration.NormalizeHost(input.Domain);
                adminHost = ShortenerConfiguration.NormalizeAdminHost(input.AdminHost, domain);
                upstream = ShortenerConfiguration.NormalizeUpstream(input.Upstream);
                // Left blank, take the address the console offered rather than
                // saving a configuration that cannot reach anything.
                if (upstream.Length == 0 && domain.Length > 0)
                    upstream = ShortenerConfiguration.SuggestUpstream(
                        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
                        PortFromEnvironment("SHORTENER_HTTP_PORT", 8081));
                mode = ShortenerConfiguration.ParseRootRedirect(input.RootRedirectMode);
                // Checked against the domain being saved, so a loop cannot be
                // introduced by changing both at once.
                redirect = ShortenerConfiguration.NormalizeRedirect(input.RootRedirectUrl, domain);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }

            if (mode is ShortenerRootRedirect.Organization or ShortenerRootRedirect.Custom && redirect.Length == 0)
                return Results.BadRequest(new { error = "Enter where the bare short domain should send people, or choose another behaviour." });
            if (mode is ShortenerRootRedirect.LessonCue && organization.PublicBaseUrl.Trim().Length == 0)
                return Results.BadRequest(new { error = "Set LessonCue's public address before sending the short domain to it." });

            organization.ShortDomain = domain;
            organization.ShortenerAdminHost = adminHost;
            organization.ShortDomainUpstream = upstream;
            organization.ShortenerRootRedirectMode = ShortenerConfiguration.RootRedirectName(mode);
            organization.ShortDomainRootRedirectUrl = redirect;
            // Stored, but the shortener is what actually serves the bare root,
            // and it reads that destination from its environment at start-up.
            // The console reports the value to set rather than pretending this
            // took effect on its own.
            organization.ShortenerEnabled = input.Enabled && domain.Length > 0;
            Audit(db, "shortener.configure", organization.Id,
                domain.Length == 0 ? "URL shortener cleared" : $"URL shortener set to {domain}");
            await db.SaveChangesAsync(ct);
            // Games read these settings from a short-lived cache, so an
            // administrator switching the shortener off has to take effect on
            // the next game rather than the next five seconds' worth.
            shortener.Invalidate();
            // The shortener reads its root destination when it starts, so a
            // change here has to be carried to it rather than only stored.
            await shortener.ReapplyAsync(
                ShortenerConfiguration.ResolveRootRedirect(
                    ShortenerConfiguration.Read(organization), organization.PublicBaseUrl.Trim().TrimEnd('/')),
                ct);
            return Results.Ok(new { state = (await shortener.StatusAsync(ct)).State.ToString() });
        });

        appSettings.MapPut("/shortener/install", async (ShortenerInstallInput input, LessonCueDb db,
            ShortenerService shortener, CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (!input.Requested)
            {
                shortener.CancelInstallRequest();
                Audit(db, "shortener.install.cancel", organization.Id, "Withdrew the URL shortener install request");
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { installRequestedFor = (string?)null });
            }

            // Falls back to the configured short domain, which is what an
            // administrator means when they tick the box having already set one.
            var domain = string.IsNullOrWhiteSpace(input.Domain) ? organization.ShortDomain : input.Domain;
            string normalizedDomain;
            try { normalizedDomain = ShortenerConfiguration.NormalizeHost(domain); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            if (normalizedDomain.Length == 0)
                return Results.BadRequest(new { error = "Enter the short domain to install the shortener for." });

            // Turning it on settles everything that has one right answer, so an
            // administrator is not left to fill in fields whose values follow
            // from the domain they just gave.
            organization.ShortDomain = normalizedDomain;
            if (organization.ShortenerAdminHost.Trim().Length == 0)
                organization.ShortenerAdminHost = ShortenerConfiguration.DefaultAdminHost(normalizedDomain);
            if (organization.ShortDomainUpstream.Trim().Length == 0)
                organization.ShortDomainUpstream = ShortenerConfiguration.SuggestUpstream(
                    string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
                    PortFromEnvironment("SHORTENER_HTTP_PORT", 8081));
            organization.ShortenerEnabled = true;

            var rootRedirect = ShortenerConfiguration.ResolveRootRedirect(
                ShortenerConfiguration.Read(organization), organization.PublicBaseUrl.Trim().TrimEnd('/'));

            string? deferred = null;
            try
            {
                await shortener.RequestInstallAsync(normalizedDomain, rootRedirect, ct);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (ShortenerService.InstallHelperUnavailableException error)
            {
                // The request is recorded either way; only its timing changes.
                deferred = error.Message;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new
                {
                    error = "LessonCue could not write to its own configuration directory, so it cannot record this. "
                        + "Check the permissions on the data directory.",
                });
            }

            Audit(db, "shortener.install.request", organization.Id,
                $"Asked for the URL shortener to be installed for {shortener.InstallRequestedFor}");
            await db.SaveChangesAsync(ct);
            shortener.Invalidate();
            return Results.Ok(new { installRequestedFor = shortener.InstallRequestedFor, deferred });
        });

        appSettings.MapPost("/shortener/key/reveal", async (LessonCueDb db, ShortenerService shortener, CancellationToken ct) =>
        {
            // Deliberately a POST an administrator has to ask for, and audited.
            // The key is never included in the status the console polls, so it
            // does not sit in memory or a log waiting to be found.
            // The console's own key first. It is scoped to what it creates, so
            // pasting it into the web interface cannot reach the reserved game
            // codes -- handing over the administrator key would.
            var key = shortener.ConsoleKey ?? shortener.IntegrationKey;
            var scope = shortener.ConsoleKey is not null ? "console" : "administrator";
            if (key is null) return Results.NotFound(new { error = "LessonCue has no API key for the shortener yet." });

            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            Audit(db, "shortener.key.reveal", organization.Id, "Revealed the URL shortener API key");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { apiKey = key, scope });
        });

        appSettings.MapPut("/shortener/console-password", async (ShortenerConsolePasswordInput input,
            LessonCueDb db, ShortenerService shortener, CancellationToken ct) =>
        {
            // LessonCue writes the chosen password into the companion's private
            // one-shot control file. The companion hashes both account values
            // and continues to own its own login and later password changes.
            try
            {
                await shortener.SetConsolePasswordAsync(input.Password, ct);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (IOException) { return Results.BadRequest(new { error = "LessonCue could not write the console password on this server." }); }
            catch (UnauthorizedAccessException) { return Results.BadRequest(new { error = "LessonCue is not allowed to write the console password on this server." }); }

            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            // The password itself is never recorded, here or anywhere else.
            Audit(db, "shortener.console.password", organization.Id, "Set the URL shortener console password");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user = ShortenerService.ConsoleUser });
        });

        appSettings.MapPut("/shortener/key", async (ShortenerKeyInput input, LessonCueDb db,
            ShortenerService shortener, CancellationToken ct) =>
        {
            // Recorded, not minted. The shortener has no API for creating keys,
            // so anything generated here would simply be rejected by it. This
            // is the value the shortener was started with.
            try
            {
                await shortener.SetIntegrationKeyAsync(input.ApiKey ?? "", ct);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }

            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            Audit(db, "shortener.key.set", organization.Id, "Recorded the URL shortener API key");
            await db.SaveChangesAsync(ct);
            // Prove it works before saying so.
            var status = await shortener.StatusAsync(ct);
            return Results.Ok(new { state = status.State.ToString(), status.Detail });
        });

        appSettings.MapPost("/shortener/test", async (ShortenerService shortener, CancellationToken ct) =>
        {
            var checks = await shortener.ProbeAsync(ct);
            return Results.Ok(new { passed = checks.All(check => check.Passed), checks });
        });

        appSettings.MapGet("/shortener/tunnel", async (LessonCueDb db, LocalAddressService localAddress,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var settings = ShortenerConfiguration.Read(organization);
            var shortenerPort = PortFromEnvironment("SHORTENER_HTTP_PORT", 8081);
            var consolePort = PortFromEnvironment("SHORTENER_UI_PORT", 8082);
            // This server's own address, so the routes name something the
            // operator can check by hand rather than an abstract "localhost".
            var serverHost = HostOf(localAddress.Status.Address);
            var plan = ShortenerTunnelPlanner.ForManagedTunnel(settings, shortenerPort, consolePort, serverHost);

            // Where cloudflared is run from a local file instead, offer the
            // merged configuration rather than only describing it. Read-only:
            // this file is what keeps LessonCue itself reachable, so the
            // operator applies it, not us.
            string? localPath = null, localMerged = null, localRefusal = null;
            foreach (var candidate in new[] { "/etc/cloudflared/config.yml", "/etc/cloudflared/config.yaml" })
            {
                if (!System.IO.File.Exists(candidate)) continue;
                localPath = candidate;
                try
                {
                    var current = await System.IO.File.ReadAllTextAsync(candidate, ct);
                    var (changed, merged, refusal) = ShortenerTunnelPlanner.MergeIngress(current, plan.Routes);
                    localRefusal = refusal;
                    localMerged = changed ? merged : null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    localRefusal = "LessonCue cannot read that cloudflared configuration.";
                }
                break;
            }

            return Results.Ok(new
            {
                canApplyAutomatically = plan.CanApplyAutomatically,
                explanation = plan.Explanation,
                instructions = plan.Instructions,
                routes = plan.Routes.Select(route => new { route.Hostname, route.Service, route.Purpose }),
                serverHost,
                shortenerPort,
                consolePort,
                localConfigPath = localPath,
                localConfigMerged = localMerged,
                localConfigRefusal = localRefusal,
            });
        });

        appSettings.MapPost("/shortener/reconcile", async (LessonCueDb db, ShortenerService shortener, CancellationToken ct) =>
        {
            try
            {
                var report = await shortener.ReconcileAsync(ct);
                var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
                organization.ShortenerPoolVersion = ReservedGameCodes.Version;
                Audit(db, "shortener.reconcile", organization.Id,
                    $"Reserved codes: {report.Created} created, {report.Repaired} repaired, {report.AlreadyCorrect} already correct, "
                    + $"{report.Conflicts.Count} conflicting, {report.Failures.Count} failed",
                    report.Degraded ? "degraded" : "success");
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    report.Total, report.AlreadyCorrect, report.Created, report.Repaired,
                    report.Present, report.Degraded, report.Conflicts, report.Failures,
                });
            }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        appSettings.MapPost("/shortener/lifecycle", async (ShortenerLifecycleInput input, LessonCueDb db,
            ShortenerService shortener, CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            switch ((input.Action ?? "").Trim().ToLowerInvariant())
            {
                case "disable":
                    // LessonCue stops using short links. The shortener keeps running.
                    organization.ShortenerEnabled = false;
                    Audit(db, "shortener.disable", organization.Id, "URL shortener integration disabled");
                    break;

                case "uninstall":
                    if (!input.Confirm)
                        return Results.BadRequest(new { error = "Confirm before removing the URL shortener integration." });
                    organization.ShortenerEnabled = false;
                    organization.ShortDomain = "";
                    organization.ShortenerAdminHost = "";
                    organization.ShortDomainUpstream = "";
                    organization.ShortenerRootRedirectMode = "notfound";
                    organization.ShortDomainRootRedirectUrl = "";
                    organization.ShortenerPoolVersion = 0;
                    shortener.ForgetSecrets();
                    // Deliberately never touches the shortener's own database.
                    // Removing links people rely on is a separate, explicit act.
                    Audit(db, "shortener.uninstall", organization.Id,
                        input.DeleteData
                            ? "URL shortener integration removed; operator asked to delete shortener data separately"
                            : "URL shortener integration removed; shortener data retained");
                    break;

                default:
                    return Results.BadRequest(new { error = $"Unrecognized shortener action '{input.Action}'." });
            }

            await db.SaveChangesAsync(ct);
            shortener.Invalidate();
            return Results.Ok(new { state = (await shortener.StatusAsync(ct)).State.ToString() });
        });

        appSettings.MapGet("/recycle-bin", async (LessonCueDb db, CancellationToken ct) =>
        {
            var classes = await db.Classes.IgnoreQueryFilters().AsNoTracking().Where(x => x.DeletedAt != null)
                .Select(x => new { kind = "class", x.Id, title = x.Name, detail = x.Description, x.DeletedAt, x.DeletedBy }).ToListAsync(ct);
            var classNames = await db.Classes.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var lessons = await db.Lessons.IgnoreQueryFilters().AsNoTracking().Where(x => x.DeletedAt != null)
                .Select(x => new { x.Id, x.Title, x.ClassId, x.Date, x.DeletedAt, x.DeletedBy }).ToListAsync(ct);
            var media = await db.MediaAssets.IgnoreQueryFilters().AsNoTracking().Where(x => x.DeletedAt != null)
                .Select(x => new { x.Id, x.FileName, x.SizeBytes, x.DeletedAt, x.DeletedBy }).ToListAsync(ct);
            var results = classes.Select(x => new RecycleBinItem(x.kind, x.Id, x.title, x.detail, x.DeletedAt!.Value, x.DeletedBy))
                .Concat(lessons.Select(x => new RecycleBinItem("lesson", x.Id, x.Title,
                    $"{classNames.GetValueOrDefault(x.ClassId, "Deleted class")} · {x.Date:yyyy-MM-dd}", x.DeletedAt!.Value, x.DeletedBy)))
                .Concat(media.Select(x => new RecycleBinItem("media", x.Id, x.FileName, $"{x.SizeBytes} bytes", x.DeletedAt!.Value, x.DeletedBy)))
                .OrderByDescending(x => x.DeletedAt);
            return Results.Ok(results);
        });

        appSettings.MapPost("/recycle-bin/{kind}/{id:guid}/restore", async (string kind, Guid id, LessonCueDb db,
            HttpContext context, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var actor = context.User.Identity?.Name ?? "admin";
            switch (kind.ToLowerInvariant())
            {
                case "class":
                    var lessonClass = await db.Classes.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct);
                    if (lessonClass is null) return Results.NotFound();
                    var deletedAt = lessonClass.DeletedAt;
                    lessonClass.DeletedAt = null; lessonClass.DeletedBy = null;
                    var childLessons = await db.Lessons.IgnoreQueryFilters().Where(x => x.ClassId == id && x.DeletedAt == deletedAt).ToListAsync(ct);
                    foreach (var lesson in childLessons) { lesson.DeletedAt = null; lesson.DeletedBy = null; }
                    break;
                case "lesson":
                    var deletedLesson = await db.Lessons.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct);
                    if (deletedLesson is null) return Results.NotFound();
                    if (!await db.Classes.AnyAsync(x => x.Id == deletedLesson.ClassId, ct))
                        return Results.Conflict(new { error = "Restore the lesson's class first." });
                    deletedLesson.DeletedAt = null; deletedLesson.DeletedBy = null;
                    break;
                case "media":
                    var deletedMedia = await db.MediaAssets.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct);
                    if (deletedMedia is null) return Results.NotFound();
                    deletedMedia.DeletedAt = null; deletedMedia.DeletedBy = null;
                    break;
                default: return Results.BadRequest(new { error = "Unknown recycling-bin item type." });
            }
            db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = $"recycle.{kind}.restore", Object = id.ToString() });
            await db.SaveChangesAsync(ct); await InvalidateAsync(hub, 0, ct);
            return Results.NoContent();
        });

        appSettings.MapDelete("/recycle-bin", async (LessonCueDb db, MediaStoragePaths paths, HttpContext context,
            IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var purged = await RecycleBinService.PurgeAsync(db, paths, DateTimeOffset.MaxValue, context.User.Identity?.Name ?? "admin", ct);
            await InvalidateAsync(hub, 0, ct);
            return Results.Ok(new { purged });
        });

        playback.MapPost("/controller/unlock", async (ControllerPinInput input, LessonCueDb db,
            IPasswordHasher<Organization> hasher, ControllerSessionService controllerSessions, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().SingleAsync(ct);
            if (organization.ControllerPinHash is null)
                return Results.Conflict(new { error = "An administrator must configure the universal controller PIN in Settings." });
            return hasher.VerifyHashedPassword(organization, organization.ControllerPinHash, input.Pin) == PasswordVerificationResult.Failed
                ? Results.Json(new { error = "That controller PIN was not accepted." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new { grant = controllerSessions.CreateUniversalGrant(), expiresAt = DateTimeOffset.UtcNow.AddHours(12) });
        }).AllowAnonymous().RequireRateLimiting("login");

        settings.MapGet("/storage", async (LessonCueDb db, StorageService storage, CancellationToken ct) =>
            Results.Ok(await storage.GetSnapshotAsync(db, ct)));

        settings.MapPut("/storage", async (StorageLimitInput input, LessonCueDb db, StorageService storage,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            var snapshot = await storage.GetSnapshotAsync(db, ct);
            if (input.LimitBytes < 0) return Results.BadRequest(new { error = "Storage allocation cannot be negative." });
            var committed = snapshot.UsedBytes + snapshot.ReservedBytes;
            if (input.LimitBytes > 0 && input.LimitBytes < committed)
                return Results.BadRequest(new
                {
                    error = $"The app uses or has reserved {committed} bytes. The allocation cannot be lower while uploads are active."
                });
            if (input.LimitBytes > snapshot.MaximumAllocationBytes)
                return Results.BadRequest(new { error = "That allocation is larger than the safely available space on this computer." });
            organization.StorageLimitBytes = input.LimitBytes;
            Audit(db, "storage.limit.update", organization.Id, input.LimitBytes == 0 ? "automatic" : input.LimitBytes.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Ok(await storage.GetSnapshotAsync(db, ct));
        });

        settings.MapGet("/upload-policy", async (LessonCueDb db, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            return Results.Ok(UploadQuotaPolicy.Read(organization));
        });

        settings.MapPut("/upload-policy", async (UploadQuotaPolicyInput input, LessonCueDb db,
            CancellationToken ct) =>
        {
            if (input.MaxFileBytes < 0 || input.MaxDailyBytes < 0)
                return Results.BadRequest(new { error = "Upload limits cannot be negative." });
            if (input.MaxFileBytes > UploadQuotaPolicy.HardMaximumFileBytes)
                return Results.BadRequest(new { error = "The maximum file-size policy cannot exceed 100 GB." });
            if (input.MaxActiveSessionsPerUser is < 1 or > UploadQuotaPolicy.HardMaximumActiveSessions)
                return Results.BadRequest(new { error = "Active uploads per account must be from 1 to 10." });
            if (input.UserDailyBytes?.Values.Any(value => value <= 0) == true ||
                input.RoleDailyBytes?.Values.Any(value => value <= 0) == true ||
                input.ClassDailyBytes?.Values.Any(value => value <= 0) == true)
                return Results.BadRequest(new { error = "Per-user, role, and class limits must be greater than zero." });
            try
            {
                var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
                UploadQuotaPolicy.Store(organization, input);
                Audit(db, "storage.upload-policy.update", organization.Id,
                    $"file:{input.MaxFileBytes};day:{input.MaxDailyBytes};active:{input.MaxActiveSessionsPerUser}");
                await db.SaveChangesAsync(ct);
                return Results.Ok(UploadQuotaPolicy.Read(organization));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        settings.MapGet("/local-address", (LocalAddressService localAddress) => Results.Ok(localAddress.Status));

        settings.MapPut("/local-address", async (LocalHostnameInput input, LocalAddressService localAddress,
            LessonCueDb db, CancellationToken ct) =>
        {
            try
            {
                var status = await localAddress.SetAsync(input.Hostname, input.Ipv6Enabled, ct);
                Audit(db, "server.local-address.update", Guid.Empty,
                    $"{status.Address};local-ipv6:{status.Ipv6Enabled}");
                await db.SaveChangesAsync(ct);
                return Results.Ok(status);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        settings.MapGet("/http-port", (HttpPortService httpPort) => Results.Ok(httpPort.Status));

        settings.MapPut("/http-port", async (HttpPortInput input, HttpPortService httpPort,
            LessonCueDb db, CancellationToken ct) =>
        {
            try
            {
                var status = await httpPort.SetAsync(input.Port, ct);
                Audit(db, "server.http-port.update", Guid.Empty, input.Port.ToString(CultureInfo.InvariantCulture));
                await db.SaveChangesAsync(ct);
                return Results.Ok(status);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        settings.MapGet("/activity-availability",
            (ActivityAvailabilityService availability) => Results.Ok(availability.Status))
            .RequireAuthorization(LessonCuePermissions.Settings);

        settings.MapPut("/activity-availability", async (
            ActivityAvailabilityInput input,
            ActivityAvailabilityService availability,
            CancellationToken ct) => Results.Ok(await availability.SetAsync(input.Enabled, ct)))
            .RequireAuthorization(LessonCuePermissions.Settings);

        settings.MapGet("/activity-join-address", async (
            ActivityJoinAddressService joinAddress,
            ShortenerService shortener,
            CancellationToken ct) =>
        {
            // ActivityJoinAddressService intentionally has a synchronous hot
            // path, while the shortener's usability is established by a live
            // health/audit check. Refresh here so opening Settings immediately
            // after installation (or after a restart) can select Short domain
            // without waiting for the background health service.
            await shortener.StatusAsync(ct);
            return Results.Ok(joinAddress.Status);
        });

        settings.MapPut("/activity-join-address", async (
            JoinAddressInput input,
            ActivityJoinAddressService joinAddress,
            ShortenerService shortener,
            CancellationToken ct) =>
        {
            await shortener.StatusAsync(ct);
            return Results.Ok(await joinAddress.SetAsync(input.Mode, ct));
        });

        settings.MapGet("/cloudflare-tunnel", (CloudflareTunnelService tunnel) => Results.Ok(tunnel.Status));

        settings.MapPut("/cloudflare-tunnel", async (CloudflareTunnelInput input, CloudflareTunnelService tunnel,
            LessonCueDb db, CancellationToken ct) =>
        {
            try
            {
                var status = await tunnel.SetAsync(input.Enabled, input.PublicHostname, input.Token,
                    input.AcknowledgedRemoteExposure, ct);
                Audit(db, input.Enabled ? "server.cloudflare-tunnel.enable" : "server.cloudflare-tunnel.disable",
                    Guid.Empty, input.Enabled ? status.PublicHostname ?? "configured" : "disabled");
                await db.SaveChangesAsync(ct);
                return Results.Accepted(value: status);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        updatesAdmin.MapGet("/updates", (UpdateService updates) => Results.Ok(updates.Status));

        updatesAdmin.MapGet("/updates/youtube-runtime", (YouTubeRuntimeUpdateService youtubeRuntime) =>
            Results.Ok(youtubeRuntime.Status));

        updatesAdmin.MapPost("/updates/youtube-runtime/check", async (
            YouTubeRuntimeUpdateService youtubeRuntime, LessonCueDb db, CancellationToken ct) =>
        {
            var operation = await youtubeRuntime.QueueCheckAsync(ct);
            if (operation.Success)
            {
                Audit(db, "server.youtube-runtime.check", Guid.Empty, "Queued independent yt-dlp update check");
                await db.SaveChangesAsync(ct);
                return Results.Accepted(value: operation);
            }
            return Results.Conflict(new { error = operation.Message, failureCode = operation.FailureCode });
        });

        updatesAdmin.MapPost("/updates/youtube-runtime/update", async (
            YouTubeRuntimeUpdateService youtubeRuntime, LessonCueDb db, CancellationToken ct) =>
        {
            var operation = await youtubeRuntime.QueueUpdateAsync(ct);
            if (operation.Success)
            {
                Audit(db, "server.youtube-runtime.update", Guid.Empty, "Queued independent yt-dlp update");
                await db.SaveChangesAsync(ct);
                return Results.Accepted(value: operation);
            }
            return Results.Conflict(new { error = operation.Message, failureCode = operation.FailureCode });
        });

        updatesAdmin.MapPost("/updates/check", async (UpdateService updates, CancellationToken ct) =>
            Results.Ok(await updates.CheckAsync(true, ct)));

        updatesAdmin.MapPost("/updates/install", async (UpdateService updates, LessonCueDb db, HttpContext context,
            ILogger<UpdateService> logger, CancellationToken ct) =>
        {
            var status = await updates.CheckAsync(true, ct);
            logger.LogInformation(
                "Update install request received. Trace {TraceId}; current version {CurrentVersion}; latest version {LatestVersion}; available {UpdateAvailable}",
                context.TraceIdentifier, status.CurrentVersion, status.LatestVersion ?? "unknown", status.UpdateAvailable);
            if (!status.UpdateAvailable)
            {
                logger.LogWarning("Update install request rejected because no newer release is available. Trace {TraceId}",
                    context.TraceIdentifier);
                return Results.Conflict(new { error = "LessonCue is already up to date.", failureCode = "no-update-available" });
            }
            if (!status.AutomaticInstallSupported)
            {
                const string message = "Automatic installation is not configured on this server. Run the latest Linux installer once to enable it.";
                logger.LogWarning("Update install request rejected because automatic installation is unavailable. Trace {TraceId}",
                    context.TraceIdentifier);
                return Results.Conflict(new { error = message, failureCode = "protected-operation-unavailable" });
            }
            Audit(db, "server.update.start", Guid.Empty, $"{status.CurrentVersion} to {status.LatestVersion}");
            await db.SaveChangesAsync(ct);
            var operation = await updates.StartInstallAsync(ct);
            if (operation.Success)
                return Results.Accepted(value: new { message = "The update has started. LessonCue will restart automatically." });
            if (operation.FailureCode == "operation-in-progress")
            {
                logger.LogWarning("Update install request found another protected operation already in progress. Trace {TraceId}",
                    context.TraceIdentifier);
                return Results.Conflict(new { error = operation.Message, failureCode = operation.FailureCode });
            }

            db.AuditEvents.Add(new AuditEvent
            {
                Actor = "admin",
                Action = "server.update.failed",
                Object = Guid.Empty.ToString(),
                Result = "failed",
                Summary = operation.FailureCode ?? operation.Message
            });
            await db.SaveChangesAsync(ct);
            logger.LogError("Update install request could not be queued. Trace {TraceId}; failure code {FailureCode}; message {Message}",
                context.TraceIdentifier, operation.FailureCode ?? "unknown", operation.Message);
            return Results.Problem(
                title: "LessonCue update could not be started",
                detail: $"{operation.Message} Failure reference: {context.TraceIdentifier}",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>
                {
                    ["failureId"] = context.TraceIdentifier,
                    ["failureCode"] = operation.FailureCode
                });
        });

        settings.MapPost("/updates/rollback", async (UpdateService updates, LessonCueDb db,
            HttpContext context, ILogger<UpdateService> logger, CancellationToken ct) =>
        {
            var status = updates.Status;
            if (!status.AutomaticInstallSupported)
                return Results.Conflict(new { error = "Protected server operations are not configured. Run the latest Linux installer once to enable them." });
            if (!status.RollbackSnapshotAvailable)
                return Results.Conflict(new { error = "No verified last-known-good update snapshot is available." });
            Audit(db, "server.update.rollback", Guid.Empty,
                status.RollbackTargetVersion is null
                    ? "operator requested last-known-good snapshot"
                    : $"operator requested {status.RollbackTargetVersion}");
            await db.SaveChangesAsync(ct);
            var operation = await updates.StartRollbackAsync(ct);
            if (operation.Success)
                return Results.Accepted(value: new
                {
                    message = "Rollback has started. LessonCue will restart using the protected snapshot.",
                    targetVersion = status.RollbackTargetVersion
                });
            if (operation.FailureCode == "operation-in-progress")
            {
                logger.LogWarning("Rollback request found another protected operation already in progress. Trace {TraceId}",
                    context.TraceIdentifier);
                return Results.Conflict(new { error = operation.Message, failureCode = operation.FailureCode });
            }

            db.AuditEvents.Add(new AuditEvent
            {
                Actor = "admin",
                Action = "server.update.rollback-failed",
                Object = Guid.Empty.ToString(),
                Result = "failed",
                Summary = operation.FailureCode ?? operation.Message
            });
            await db.SaveChangesAsync(ct);
            logger.LogError("Rollback request could not be queued. Trace {TraceId}; failure code {FailureCode}; message {Message}",
                context.TraceIdentifier, operation.FailureCode ?? "unknown", operation.Message);
            return Results.Problem(
                title: "LessonCue rollback could not be started",
                detail: $"{operation.Message} Failure reference: {context.TraceIdentifier}",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>
                {
                    ["failureId"] = context.TraceIdentifier,
                    ["failureCode"] = operation.FailureCode
                });
        });

        userAdmin.MapGet("/users", async (LessonCueDb db, CancellationToken ct) =>
        {
            var accounts = await db.AdminAccounts.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct);
            return Results.Ok(accounts.Select(x => new
            {
                x.Id, x.Username, x.DisplayName, x.Email, x.EmailVerified, x.Role, x.Disabled, x.CreatedAt, x.LastLoginAt,
                x.PendingApproval, x.PendingSetup, x.MustChangePassword, mfaEnabled = x.TotpEnabled,
                permissions = LessonCuePermissions.Effective(x),
                customPermissions = x.PermissionsCsv is null ? null : LessonCuePermissions.Effective(x)
            }));
        });

        userAdmin.MapPost("/users", async (UserInput input, LessonCueDb db, HttpContext context,
            IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "Disable the all-user MFA requirement before adding a user, or enroll the new user first." });
            var validation = ValidateCredentials(input.Username, input.Password ?? "");
            if (validation is not null) return Results.BadRequest(new { error = validation });
            if (string.IsNullOrWhiteSpace(input.DisplayName)) return Results.BadRequest(new { error = "Name is required." });
            var username = input.Username.Trim().ToLowerInvariant();
            var address = NullIfBlank(input.Email)?.ToLowerInvariant();
            if (address is not null && !IsEmail(address)) return Results.BadRequest(new { error = "Enter a valid email address." });
            if (await db.AdminAccounts.AnyAsync(x => x.Username == username, ct)) return Results.Conflict(new { error = "That username already exists." });
            if (address is not null && await db.AdminAccounts.AnyAsync(x => x.Email == address, ct))
                return Results.Conflict(new { error = "That email address is already registered." });
            var role = NormalizeAdminRole(input.Role);
            if (LessonCuePermissions.IsServiceAdmin(role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            var account = new AdminAccount { Username = username, DisplayName = input.DisplayName.Trim(), Email = address,
                EmailVerified = true, EmailVerifiedAt = address is null ? null : DateTimeOffset.UtcNow,
                Role = role, PermissionsCsv = LessonCuePermissions.NormalizeCustom(input.Permissions, role),
                Disabled = input.Disabled, PasswordHash = "pending", MustChangePassword = true };
            if (!IsServiceAdmin(context.User) && LessonCuePermissions.Effective(account)
                .Except(LessonCuePermissions.Effective(context.User)).Any()) return Results.Forbid();
            account.PasswordHash = hasher.HashPassword(account, input.Password!);
            db.AdminAccounts.Add(account); Audit(db, "user.create", account.Id, account.Username); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/users/{account.Id}", new { account.Id });
        });

        userAdmin.MapPost("/users/invitations", async (UserInvitationInput input, LessonCueDb db,
            HttpContext context, HttpRequest request, AccountEmailService email,
            IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var address = input.Email.Trim().ToLowerInvariant();
            if (!IsEmail(address)) return Results.BadRequest(new { error = "Enter a valid email address." });
            if (await db.AdminAccounts.AnyAsync(x => x.Email == address, ct))
                return Results.Conflict(new { error = "That email address already belongs to an account." });
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "Disable the all-user MFA requirement before adding a user, or enroll the new user first." });
            if (!email.Status(organization.EmailProvider).Configured)
                return Results.Conflict(new { error = "Configure account email before sending invitations." });
            var role = NormalizeAdminRole(input.Role);
            if (LessonCuePermissions.IsServiceAdmin(role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            var account = new AdminAccount
            {
                Username = $"invite-{Guid.NewGuid():N}", DisplayName = string.IsNullOrWhiteSpace(input.DisplayName)
                    ? "Invited user" : input.DisplayName.Trim(), Email = address, EmailVerified = false,
                Role = role, PermissionsCsv = LessonCuePermissions.NormalizeCustom(input.Permissions, role),
                PasswordHash = "pending", PendingSetup = true
            };
            if (account.DisplayName.Length > 120)
                return Results.BadRequest(new { error = "Name cannot exceed 120 characters." });
            if (!IsServiceAdmin(context.User) && LessonCuePermissions.Effective(account)
                .Except(LessonCuePermissions.Effective(context.User)).Any()) return Results.Forbid();
            account.PasswordHash = hasher.HashPassword(account,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(48)));
            var raw = AccountEmailService.NewToken();
            db.AdminAccounts.Add(account);
            db.AccountTokens.Add(NewAccountToken(account.Id, "setup", raw, TimeSpan.FromDays(3)));
            Audit(db, "user.invite", account.Id, address);
            await db.SaveChangesAsync(ct);
            try
            {
                await SendAccountLinkAsync(email, organization, address, "Set up your LessonCue account",
                    "Set up account", AccountUrl(organization, request, "/setup-account", raw), ct);
            }
            catch (Exception error)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = context.User.Identity?.Name ?? "admin", Action = "user.invite.delivery",
                    Object = account.Id.ToString(), Result = "failed", Summary = error.Message
                });
                await db.SaveChangesAsync(ct);
                return Results.Json(new
                {
                    error = "The account was reserved, but the setup email could not be delivered. Fix email and use Resend setup."
                }, statusCode: 502);
            }
            return Results.Created($"/api/v1/users/{account.Id}",
                new { account.Id, message = $"Setup email sent to {address}." });
        });

        userAdmin.MapPost("/users/{id:guid}/invitation", async (Guid id, LessonCueDb db,
            HttpContext context, HttpRequest request, AccountEmailService email, CancellationToken ct) =>
        {
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (account is null) return Results.NotFound();
            if (!account.PendingSetup || string.IsNullOrWhiteSpace(account.Email))
                return Results.Conflict(new { error = "This account is not waiting for setup." });
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "Disable the all-user MFA requirement before sending a setup link." });
            if (!email.Status(organization.EmailProvider).Configured)
                return Results.Conflict(new { error = "Configure account email before resending invitations." });
            await InvalidateTokensAsync(db, account.Id, "setup", ct);
            var raw = AccountEmailService.NewToken();
            db.AccountTokens.Add(NewAccountToken(account.Id, "setup", raw, TimeSpan.FromDays(3)));
            Audit(db, "user.invite.resend", account.Id, account.Email);
            await db.SaveChangesAsync(ct);
            try
            {
                await SendAccountLinkAsync(email, organization, account.Email, "Set up your LessonCue account",
                    "Set up account", AccountUrl(organization, request, "/setup-account", raw), ct);
            }
            catch (Exception error)
            {
                return Results.Json(new { error = $"The setup email could not be delivered: {error.Message}" }, statusCode: 502);
            }
            return Results.Ok(new { message = $"A new setup link was sent to {account.Email}." });
        });

        userAdmin.MapPost("/users/{id:guid}/approve", async (Guid id, LessonCueDb db,
            HttpContext context, HttpRequest request, AccountEmailService email, CancellationToken ct) =>
        {
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (account is null) return Results.NotFound();
            if (!account.PendingApproval)
                return Results.Conflict(new { error = "This account is not waiting for approval." });
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            var organization = await db.Organizations.OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "Disable the all-user MFA requirement before approving a user, or enroll them first." });
            account.PendingApproval = false;
            account.SessionVersion++;
            Audit(db, "user.approve", account.Id, account.Username);
            await db.SaveChangesAsync(ct);
            var delivered = false;
            if (!string.IsNullOrWhiteSpace(account.Email) && email.Status(organization.EmailProvider).Configured)
            {
                try
                {
                    var signInUrl = AccountUrl(organization, request, "/", "");
                    await email.SendAsync(organization, account.Email, "Your LessonCue account was approved",
                        $"<p>Your account for <strong>{System.Net.WebUtility.HtmlEncode(organization.Name)}</strong> was approved.</p>" +
                        $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(signInUrl)}\">Sign in to LessonCue</a></p>", ct);
                    delivered = true;
                }
                catch
                {
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Actor = context.User.Identity?.Name ?? "admin", Action = "user.approval.delivery",
                        Object = account.Id.ToString(), Result = "failed"
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
            return Results.Ok(new
            {
                message = delivered ? $"{account.DisplayName} was approved and notified."
                    : $"{account.DisplayName} was approved."
            });
        });

        userAdmin.MapPost("/users/{id:guid}/temporary-password", async (Guid id, TemporaryPasswordInput input,
            LessonCueDb db, HttpContext context, IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (account is null) return Results.NotFound();
            var currentAccountId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentId)
                ? currentId : Guid.Empty;
            if (account.Id == currentAccountId)
                return Results.BadRequest(new { error = "Change your own password from your profile." });
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            if (account.PendingSetup)
                return Results.Conflict(new { error = "This invited user must complete account setup first." });
            var validation = ValidateCredentials(account.Username, input.Password);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            account.PasswordHash = hasher.HashPassword(account, input.Password);
            account.MustChangePassword = true;
            account.SessionVersion++;
            await InvalidateTokensAsync(db, account.Id, "reset", ct);
            Audit(db, "user.password.temporary", account.Id, account.Username);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = $"Temporary password set for {account.DisplayName}. Existing sessions were signed out." });
        });

        userAdmin.MapDelete("/users/{id:guid}/mfa", async (Guid id, LessonCueDb db, HttpContext context,
            CancellationToken ct) =>
        {
            var account = await db.AdminAccounts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (account is null) return Results.NotFound();
            var currentAccountId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentId)
                ? currentId : Guid.Empty;
            if (account.Id == currentAccountId)
                return Results.BadRequest(new { error = "Manage your own authenticator from your account menu." });
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            if (organization.RequireMfaForAllUsers)
                return Results.Conflict(new { error = "An administrator requires Authenticator MFA for every user. Turn that requirement off before resetting a user's MFA." });
            if (!account.TotpEnabled && string.IsNullOrWhiteSpace(account.TotpSecretProtected))
                return Results.BadRequest(new { error = "Authenticator MFA is not configured for this user." });
            account.TotpSecretProtected = null;
            account.TotpEnabled = false;
            account.TotpLastCounter = 0;
            account.TotpEnabledAt = null;
            account.SessionVersion++;
            Audit(db, "user.mfa.reset", account.Id, account.Username);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        userAdmin.MapPut("/users/{id:guid}", async (Guid id, UserInput input, LessonCueDb db, HttpContext context,
            IPasswordHasher<AdminAccount> hasher, CancellationToken ct) =>
        {
            var account = await db.AdminAccounts.FindAsync([id], ct); if (account is null) return Results.NotFound();
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            var currentAccountId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentId) ? currentId : Guid.Empty;
            if (account.Id == currentAccountId && input.Disabled)
                return Results.BadRequest(new { error = "You cannot pause your own account." });
            var usernameValidation = AdminCredentialPolicy.ValidateUsername(input.Username);
            if (usernameValidation is not null) return Results.BadRequest(new { error = usernameValidation });
            if (string.IsNullOrWhiteSpace(input.DisplayName)) return Results.BadRequest(new { error = "Name is required." });
            var username = input.Username.Trim().ToLowerInvariant();
            var address = NullIfBlank(input.Email)?.ToLowerInvariant();
            if (address is not null && !IsEmail(address)) return Results.BadRequest(new { error = "Enter a valid email address." });
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var emailChanged = account.Email != address;
            var willBeEligibleForMfa = !input.Disabled && !account.PendingApproval && !account.PendingSetup &&
                (account.EmailVerified || emailChanged);
            if (organization.RequireMfaForAllUsers && willBeEligibleForMfa && !HasEnrolledMfa(account))
                return Results.Conflict(new { error = "Enroll Authenticator MFA for this user before making the account active while the all-user requirement is enabled." });
            if (await db.AdminAccounts.AnyAsync(x => x.Username == username && x.Id != id, ct))
                return Results.Conflict(new { error = "That username already exists." });
            if (address is not null && await db.AdminAccounts.AnyAsync(x => x.Email == address && x.Id != id, ct))
                return Results.Conflict(new { error = "That email address is already registered." });
            var role = NormalizeAdminRole(input.Role);
            if (LessonCuePermissions.IsServiceAdmin(role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && (!LessonCuePermissions.IsServiceAdmin(role) || input.Disabled) &&
                await db.AdminAccounts.CountAsync(x => (x.Role == "Service Admin" || x.Role == "Owner") && !x.Disabled, ct) <= 1)
                return Results.BadRequest(new { error = "At least one active Service Admin is required." });
            var permissionsCsv = LessonCuePermissions.NormalizeCustom(input.Permissions, role);
            if (account.Id == currentAccountId && (account.Role != role || account.PermissionsCsv != permissionsCsv))
                return Results.BadRequest(new { error = "Another user administrator must change your role or permissions." });
            var requestedAccess = new AdminAccount
            {
                Username = account.Username, PasswordHash = account.PasswordHash, Role = role, PermissionsCsv = permissionsCsv
            };
            if (!IsServiceAdmin(context.User) && LessonCuePermissions.Effective(requestedAccess)
                .Except(LessonCuePermissions.Effective(context.User)).Any()) return Results.Forbid();
            var identityChanged = account.Username != username || account.DisplayName != input.DisplayName.Trim() ||
                account.Email != address || account.Role != role || account.PermissionsCsv != permissionsCsv ||
                account.Disabled != input.Disabled;
            account.Username = username; account.DisplayName = input.DisplayName.Trim(); account.Email = address;
            if (emailChanged)
            {
                account.EmailVerified = !account.PendingSetup;
                account.EmailVerifiedAt = address is null || account.PendingSetup ? null : DateTimeOffset.UtcNow;
                if (account.PendingSetup) await InvalidateTokensAsync(db, account.Id, "setup", ct);
            }
            account.Role = role; account.PermissionsCsv = permissionsCsv; account.Disabled = input.Disabled;
            if (!string.IsNullOrWhiteSpace(input.Password))
            {
                var validation = ValidateCredentials(account.Username, input.Password); if (validation is not null) return Results.BadRequest(new { error = validation });
                account.PasswordHash = hasher.HashPassword(account, input.Password);
                account.MustChangePassword = account.Id != currentAccountId;
                identityChanged = true;
            }
            if (identityChanged) account.SessionVersion++;
            Audit(db, "user.update", account.Id, account.Username);
            await db.SaveChangesAsync(ct);
            if (account.Id == currentAccountId) await SignInAsync(context, account);
            return Results.NoContent();
        });

        userAdmin.MapDelete("/users/{id:guid}", async (Guid id, LessonCueDb db, HttpContext context, CancellationToken ct) =>
        {
            var currentAccountId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentId) ? currentId : Guid.Empty;
            if (id == currentAccountId) return Results.BadRequest(new { error = "You cannot delete your own account." });
            var account = await db.AdminAccounts.FindAsync([id], ct); if (account is null) return Results.NotFound();
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !IsServiceAdmin(context.User)) return Results.Forbid();
            if (LessonCuePermissions.IsServiceAdmin(account.Role) && !account.Disabled &&
                await db.AdminAccounts.CountAsync(x => (x.Role == "Service Admin" || x.Role == "Owner") && !x.Disabled, ct) <= 1)
                return Results.BadRequest(new { error = "At least one active Service Admin is required." });
            db.AdminAccounts.Remove(account);
            Audit(db, "user.delete", account.Id, account.Username);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        var signage = admin.MapGroup("/signage");
        var planningSignage = signage.MapGroup("").RequireAuthorization(LessonCuePermissions.Planning);

        signage.MapGet("", async (LessonCueDb db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var timeZone = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).Select(x => x.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
            var screens = await db.Screens.AsNoTracking().Where(x => !x.Revoked).ToListAsync(ct);
            var screenNames = screens.ToDictionary(x => x.Id, x => x.Name);
            var items = await db.SignagePlaylists.AsNoTracking().Include(x => x.MediaAsset)
                .OrderBy(x => x.Mode == "emergency" ? 0 : x.Mode == "scheduled" ? 1 : 2)
                .ThenByDescending(x => x.Priority).ThenBy(x => x.Name).ToListAsync(ct);
            var zoneMediaIds = items.SelectMany(item => SignageLayout.ParseZones(item.ZonesJson))
                .Where(zone => zone.MediaAssetId is not null).Select(zone => zone.MediaAssetId!.Value).Distinct().ToArray();
            var zoneMediaAssets = await db.MediaAssets.AsNoTracking().Where(media => zoneMediaIds.Contains(media.Id))
                .ToDictionaryAsync(media => media.Id, ct);
            return items.Select(item =>
            {
                var state = SignageSchedule.Evaluate(item, now, timeZone);
                var zones = SignageLayout.ParseZones(item.ZonesJson);
                var targetScreenIds = SignageSchedule.ParseScreenIds(item.TargetScreenIdsJson);
                var targetedScreens = screens.Where(screen => SignageSchedule.TargetsScreen(item, screen)).ToArray();
                var mediaCacheItemIds = zones.Where(zone => zone.MediaAssetId is not null).Select(zone => $"signage-{item.Id}-zone-{zone.Id}")
                    .Concat(item.MediaAssetId is null ? [] : [$"signage-{item.Id}"]).ToArray();
                var cacheStates = mediaCacheItemIds.Length == 0
                    ? targetedScreens.Select(_ => "cached").ToArray()
                    : targetedScreens.Select(screen => SignageCacheState(screen, mediaCacheItemIds)).ToArray();
                var referencedMedia = new[] { item.MediaAsset }.Concat(zones.Select(zone => zone.MediaAssetId is { } mediaId && zoneMediaAssets.TryGetValue(mediaId, out var asset) ? asset : null)).ToArray();
                var readiness = referencedMedia.Select(SignageReadiness).OrderBy(value => value switch { "failed" => 0, "missing" => 1, "preparing" => 2, _ => 3 }).FirstOrDefault() ?? "ready";
                return new
                {
                    item.Id, item.Name, item.Mode, item.Enabled, item.Priority, item.StartsAt, item.EndsAt,
                    item.Message, item.BackgroundColor, item.TextColor, item.MediaAssetId,
                    item.LayoutId, item.ContentPlaylistId, item.VolumePercent, item.DisplayPower,
                    item.Version, item.PublishedVersion, item.PublishState, item.PublishedAt, item.LastPushedAt,
                    item.KioskEnabled, item.KioskInteractionUrl, item.KioskTimeoutSeconds,
                    item.KioskShowCloseButton, item.KioskShowTouchIndicator, item.KioskVirtualKeyboard,
                    mediaFileName = item.MediaAsset?.FileName, item.TargetTagsCsv,
                    recurrence = SignageSchedule.NormalizeRecurrence(item.Recurrence),
                    item.ScheduleStartDate, item.ScheduleEndDate, item.StartMinutes, item.EndMinutes,
                    item.LayoutPreset,
                    zones = zones.Select(zone => new
                    {
                        zone.Id, zone.Type, zone.Title, zone.Content, zone.MediaAssetId, zone.SourceUrl,
                        zone.X, zone.Y, zone.Width, zone.Height, zone.BackgroundColor, zone.TextColor, zone.AccentColor,
                        zone.RefreshMinutes, zone.Rotation, zone.ZIndex, zone.Opacity, zone.Fit,
                        zone.Locked, zone.Hidden, zone.FlipX, zone.FlipY,
                        zone.GroupId, zone.LockMode, zone.RichTextJson, zone.FontFamily, zone.FontSize, zone.FontScalePercent, zone.FontWeight,
                        zone.Italic, zone.Underline, zone.LineHeightPercent, zone.TextAlign, zone.Shape,
                        zone.StrokeColor, zone.StrokeWidth, zone.CornerRadius, zone.IconName, zone.QrValue,
                        zone.QrLabelTop, zone.QrLabelBottom, zone.QrLabelLeft, zone.QrLabelRight, zone.QrPlacement,
                        zone.TickerSpeed, zone.CounterTargetAt, zone.CounterRepeatWeekly,
                        zone.ClockDisplay, zone.ClockTimeFormat, zone.ClockDateFormat, zone.ClockOrder,
                        zone.ClockTimeFontSize, zone.ClockDateFontSize,
                        zone.WeatherProvider, zone.WeatherLocation, zone.WeatherLatitude, zone.WeatherLongitude,
                        zone.WeatherPostalCode, zone.WeatherUnits, zone.WeatherFields,
                        zone.ContentPlaylistId, zone.StreamOverrideWhenLive,
                        zone.StreamOverrideStartsAt, zone.StreamOverrideEndsAt,
                        zone.MediaScale, zone.MediaOffsetX, zone.MediaOffsetY, zone.MediaAllowOverflow,
                        zone.WifiNetworkName, zone.WifiSecurity, zone.WifiHidden, zone.WeatherIconStyle,
                        zone.ClockShowPeriod, zone.ClockShowWeekday, zone.ClockShowYear,
                        zone.CalendarMaxItems, zone.CalendarFields,
                        zone.AudienceSessionId, zone.AudienceCode, zone.AudienceShowResults,
                        zone.AudienceResultDelaySeconds,
                        mediaFileName = zone.MediaAssetId is { } mediaId && zoneMediaAssets.TryGetValue(mediaId, out var mediaAsset) ? mediaAsset.FileName : null
                    }).ToArray(),
                    widgetCache = SignageLayout.ParseCache(item.WidgetCacheJson),
                    item.WidgetCacheUpdatedAt, item.WidgetCacheError,
                    daysOfWeek = SignageSchedule.ParseDays(item.DaysOfWeekCsv),
                    excludedDates = SignageSchedule.ParseDates(item.ExcludedDatesJson),
                    targetScreenIds,
                    targetScreenNames = targetScreenIds.Where(screenNames.ContainsKey).Select(id => screenNames[id]).ToArray(),
                    activeNow = state.Active,
                    state.NextChangeAt,
                    readiness,
                    ready = readiness == "ready",
                    targetScreenCount = targetedScreens.Length,
                    cachedScreenCount = cacheStates.Count(value => value == "cached"),
                    failedScreenCount = cacheStates.Count(value => value == "failed"),
                    item.CreatedAt, item.UpdatedAt
                };
            }).ToArray();
        });

        planningSignage.MapPost("", async (SignageInput input, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var validation = await ValidateSignageAsync(input, db, ct);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            var item = new SignagePlaylist { Name = input.Name.Trim() };
            ApplySignage(item, input);
            db.SignagePlaylists.Add(item); Audit(db, "signage.create", item.Id, item.Name); await db.SaveChangesAsync(ct);
            await InvalidateAsync(hub, 0, ct); return Results.Created($"/api/v1/signage/{item.Id}", item);
        });

        planningSignage.MapPut("/{id:guid}", async (Guid id, SignageInput input, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var item = await db.SignagePlaylists.FindAsync([id], ct); if (item is null) return Results.NotFound();
            var validation = await ValidateSignageAsync(input, db, ct);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            ApplySignage(item, input);
            Audit(db, "signage.update", item.Id, item.Name); await db.SaveChangesAsync(ct); await InvalidateAsync(hub, 0, ct); return Results.Ok(item);
        });

        planningSignage.MapDelete("/{id:guid}", async (Guid id, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
        {
            var item = await db.SignagePlaylists.FindAsync([id], ct); if (item is null) return Results.NotFound();
            db.SignagePlaylists.Remove(item); Audit(db, "signage.delete", id, item.Name); await db.SaveChangesAsync(ct); await InvalidateAsync(hub, 0, ct);
            return Results.NoContent();
        });

        planningSignage.MapPost("/{id:guid}/widgets/refresh", async (Guid id, LessonCueDb db,
            SignageWidgetService widgets, CancellationToken ct) =>
        {
            if (!await db.SignagePlaylists.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var refreshed = await widgets.RefreshAsync(id, true, ct);
            return Results.Ok(new { refreshed });
        });

        backupsAdmin.MapGet("/backups/policy", async (
            LessonCueDb db,
            BackupPolicyService policy,
            CancellationToken ct) =>
        {
            var timeZone = await db.Organizations.AsNoTracking()
                .OrderBy(item => item.Id).Select(x => x.TimeZone)
                .FirstOrDefaultAsync(ct) ?? "UTC";
            return Results.Ok(policy.GetStatus(timeZone));
        });
        backupsAdmin.MapPost("/backups/google-drive/connect", async (
            HttpContext context,
            LessonCueDb db,
            BackupPolicyService policy,
            CancellationToken ct) =>
        {
            var configuredBaseUrl = await db.Organizations.AsNoTracking()
                .OrderBy(item => item.Id).Select(x => x.PublicBaseUrl).FirstOrDefaultAsync(ct);
            var origin = GoogleDriveCallbackOrigin(configuredBaseUrl, context);
            if (origin is null)
                return Results.BadRequest(new
                {
                    error = "Connect Google Drive through the HTTPS LessonCue address used in your browser."
                });
            var callbackUri = origin + "/api/v1/backups/google-drive/callback";
            try
            {
                return Results.Ok(new
                {
                    authorizationUrl = await policy.BeginGoogleDriveAuthorizationAsync(callbackUri, ct),
                    redirectUri = callbackUri
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        backupsAdmin.MapGet("/backups/google-drive/callback", async (
            string? state,
            string? code,
            string? error,
            HttpContext context,
            BackupPolicyService policy,
            ILogger<BackupPolicyService> logger,
            CancellationToken ct) =>
        {
            var connected = false;
            if (string.IsNullOrEmpty(error) && !string.IsNullOrWhiteSpace(state) &&
                !string.IsNullOrWhiteSpace(code))
            {
                try
                {
                    await policy.CompleteGoogleDriveAuthorizationAsync(state, code, ct);
                    connected = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Expired OAuth callback state is an expected user-action
                    // outcome, not a server failure. Do not attach the
                    // exception here: TroubleshootingLog treats exceptions as
                    // failures and would otherwise report an old browser tab
                    // as a live service outage.
                    logger.LogWarning("Google Drive backup authorization did not complete: {Message}", ex.Message);
                }
            }

            // This tightly scoped page is the only place LessonCue permits an
            // inline script. It posts a boolean result to the opener and never
            // includes the OAuth code, token, or server-side error text.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
            var connectedText = connected ? "true" : "false";
            var message = connected
                ? "Google Drive is connected. You can close this window."
                : "Google Drive was not connected. Return to LessonCue and try again.";
            var html = $"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Google Drive</title>" +
                       $"<body><p>{message}</p><script>if(window.opener){{window.opener.postMessage(" +
                       $"{{type:'lessoncue-google-drive-oauth',connected:{connectedText}}},window.location.origin);" +
                       "window.close();}</script></body></html>";
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();
        backupsAdmin.MapPut("/backups/policy", async (
            BackupPolicyInput input,
            LessonCueDb db,
            BackupPolicyService policy,
            CancellationToken ct) =>
        {
            var timeZone = await db.Organizations.AsNoTracking()
                .OrderBy(item => item.Id).Select(x => x.TimeZone)
                .FirstOrDefaultAsync(ct) ?? "UTC";
            try
            {
                var status = await policy.UpdateAsync(input, timeZone, ct);
                Audit(db, "backup.policy.update", Guid.Empty,
                    $"{status.Enabled}:{status.Frequency}:{status.RetentionCount}");
                await db.SaveChangesAsync(ct);
                return Results.Ok(status);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        backupsAdmin.MapPost("/backups/policy/run", async (
            LessonCueDb db,
            BackupPolicyService policy,
            CancellationToken ct) =>
        {
            var timeZone = await db.Organizations.AsNoTracking()
                .OrderBy(item => item.Id).Select(x => x.TimeZone)
                .FirstOrDefaultAsync(ct) ?? "UTC";
            try
            {
                var status = await policy.RunNowAsync(timeZone, ct);
                Audit(db, "backup.policy.run", Guid.Empty, status.LastBackupFileName);
                await db.SaveChangesAsync(ct);
                return Results.Ok(status);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (IOException ex)
            {
                return Results.Problem(
                    ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        backupsAdmin.MapGet("/backups", async (LessonCueDb db, CancellationToken ct) =>
            (await db.BackupRecords.AsNoTracking().ToListAsync(ct)).OrderByDescending(x => x.CreatedAt));
        backupsAdmin.MapPost("/backups/{id:guid}/migration-link", async (
            Guid id,
            LessonCueDb db,
            BackupService backups,
            MigrationTransferService transfers,
            CancellationToken ct) =>
        {
            var record = await db.BackupRecords.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (record is null) return Results.NotFound();
            var path = backups.Resolve(record.FileName);
            if (path is null) return Results.NotFound();
            try
            {
                var grant = transfers.Create(path, record.FileName);
                Audit(db, "backup.migration-link.create", record.Id,
                    grant.ExpiresAt.ToString("O"));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    grant.Token,
                    grant.FileName,
                    grant.ExpiresAt,
                    endpoint = "/api/v1/migration/export"
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        backupsAdmin.MapPost("/backups/migration/preview", async (
            MigrationPullInput input,
            MigrationTransferService transfers,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await transfers.PullAsync(input, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(
                    $"The destination server could not reach the source LessonCue server: {ex.Message}",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (IOException ex)
            {
                return Results.Problem(
                    ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        backupsAdmin.MapPost("/backups", async (bool? full, BackupCreateInput input, LessonCueDb db,
            BackupService backups, HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(input.Password))
                return Results.BadRequest(new
                {
                    error = "Enter a password with at least 12 characters. Exported LessonCue backups are always encrypted."
                });
            try
            {
                var record = await backups.CreateAsync(
                    db,
                    input.Full || full == true,
                    context.User.Identity?.Name ?? "admin",
                    ct,
                    input.Password,
                    input.SecretHandling);
                Audit(db, "backup.create", record.Id,
                    $"{record.Kind}:encrypted:{input.SecretHandling}");
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/v1/backups/{record.Id}/file", record);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        backupsAdmin.MapGet("/backups/{id:guid}/file", async (Guid id, LessonCueDb db, BackupService backups, CancellationToken ct) =>
        {
            var record = await db.BackupRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (record is null) return Results.NotFound();
            var path = backups.Resolve(record.FileName);
            if (path is null) return Results.NotFound();
            var contentType = Path.GetExtension(record.FileName).Equals(".lcbak", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.lessoncue.backup"
                : "application/zip";
            return Results.File(path, contentType, record.FileName);
        });
        backupsAdmin.MapPost("/backups/{id:guid}/verify", async (Guid id, LessonCueDb db, BackupService backups,
            BackupPasswordInput input, HttpContext context, CancellationToken ct) =>
        {
            var record = await db.BackupRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (record is null) return Results.NotFound();
            try
            {
                var preview = await backups.VerifyStoredAsync(record, ct, input.Password);
                Audit(db, "backup.restore-drill", record.Id, $"ok:{preview.FileCount} files");
                await db.SaveChangesAsync(ct);
                return Results.Ok(preview);
            }
            catch (InvalidDataException ex)
            {
                Audit(db, "backup.restore-drill", record.Id, "failed");
                await db.SaveChangesAsync(ct);
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (IOException ex)
            {
                Audit(db, "backup.restore-drill", record.Id, "failed");
                await db.SaveChangesAsync(ct);
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        backupsAdmin.MapPost("/backups/restore/preview", async (HttpRequest request, BackupService backups,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Choose a LessonCue .lcbak or ZIP backup." });
            var form = await request.ReadFormAsync(ct);
            var upload = form.Files.GetFile("file");
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { error = "Choose a non-empty LessonCue backup." });
            try
            {
                await using var stream = upload.OpenReadStream();
                return Results.Ok(await backups.StageAsync(
                    stream,
                    upload.FileName,
                    upload.Length,
                    ct,
                    form["password"].ToString()));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (IOException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        backupsAdmin.MapPost("/backups/restore", async (BackupRestoreInput input, LessonCueDb db, BackupService backups,
            IHubContext<SyncHub> hub, HttpContext context, CancellationToken ct) =>
        {
            if (!string.Equals(input.Confirmation?.Trim(), "RESTORE", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Type RESTORE to confirm this replacement." });
            try
            {
                var result = await backups.RestoreAsync(db, input.RestoreId, context.User.Identity?.Name ?? "admin", ct);
                await InvalidateAsync(hub, 0, ct);
                return Results.Ok(result);
            }
            catch (FileNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        admin.MapGet("/pairing/status", (PairingCodeService pairing, HttpContext context) =>
        {
            var canPair = LessonCuePermissions.Has(context.User, LessonCuePermissions.Screens) ||
                LessonCuePermissions.Has(context.User, LessonCuePermissions.AppSettings) ||
                LessonCuePermissions.Has(context.User, LessonCuePermissions.Settings);
            return canPair
                ? Results.Ok(new
                {
                    pin = pairing.Current,
                    expiresAt = pairing.ExpiresAt,
                    fixedPin = pairing.FixedPin is not null
                })
                : Results.Forbid();
        });
        appSettings.MapPut("/pairing/pin", async (PairingPinInput input, PairingCodeService pairing, LessonCueDb db,
            CancellationToken ct) =>
        {
            try { pairing.SetFixedPin(input.Automatic ? null : input.Pin); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            Audit(db, "pairing.pin.update", Guid.Empty, input.Automatic ? "automatic" : "fixed");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { pin = pairing.Current, expiresAt = pairing.ExpiresAt, fixedPin = pairing.FixedPin is not null });
        });
    }

    private static string? UnprotectControllerPin(IDataProtectionProvider protection, string? protectedPin)
    {
        if (string.IsNullOrWhiteSpace(protectedPin)) return null;
        try
        {
            var pin = protection.CreateProtector(ControllerPinProtectionPurpose).Unprotect(protectedPin);
            return pin.Length == 6 && pin.All(char.IsAsciiDigit) ? pin : null;
        }
        catch (CryptographicException)
        {
            // A restored database can retain the authentication hash while
            // its protected value was created with another server's key ring.
            return null;
        }
    }

    private static string NormalizeAdminRole(string role) => role switch
    {
        "Service Admin" or "Owner" => "Service Admin",
        "App Admin" or "Administrator" => "App Admin",
        "Editor" => "Editor",
        _ => "Viewer"
    };
    private static bool IsServiceAdmin(ClaimsPrincipal user) =>
        LessonCuePermissions.IsServiceAdmin(user.FindFirstValue(ClaimTypes.Role));
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

    internal static async Task<string?> ValidateSignageAsync(SignageInput input, LessonCueDb db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Signage name is required.";
        if (input.Name.Trim().Length > 160) return "Signage name must be 160 characters or fewer.";
        if ((input.Message?.Trim().Length ?? 0) > 2000) return "Signage message must be 2,000 characters or fewer.";
        if ((input.TargetTagsCsv?.Trim().Length ?? 0) > 2000) return "Screen tags must be 2,000 characters or fewer.";
        if (input.Priority is < 0 or > 100) return "Priority must be from 0 to 100.";
        if (input.BackgroundColor is not null && !IsColor(input.BackgroundColor) ||
            input.TextColor is not null && !IsColor(input.TextColor))
            return "Signage colors must use six-digit hex values.";
        var recurrence = SignageSchedule.NormalizeRecurrence(input.Recurrence);
        if (recurrence == "once" && input.StartsAt is { } starts && input.EndsAt is { } ends && ends <= starts)
            return "The ending time must be after the starting time.";
        if (recurrence != "once")
        {
            if (input.ScheduleEndDate is { } endDate && input.ScheduleStartDate is { } startDate && endDate < startDate)
                return "The ending date must be on or after the starting date.";
            if (input.StartMinutes is < 0 or > 1439 || input.EndMinutes is < 1 or > 1440)
                return "Recurring start and end times are invalid.";
            if (input.StartMinutes is int startMinutes && input.EndMinutes is int endMinutes && startMinutes == endMinutes)
                return "Recurring signage start and end times must be different.";
            if (recurrence == "weekly" && !(input.DaysOfWeek?.Any(day => day is >= 0 and <= 6) ?? false))
                return "Choose at least one weekday for weekly signage.";
            if ((input.ExcludedDates?.Count ?? 0) > 366) return "Signage supports at most 366 excluded dates.";
        }
        if ((input.TargetScreenIds?.Count ?? 0) > 500) return "Signage supports at most 500 explicitly selected screens.";
        if (input.MediaAssetId is { } mediaId && !await db.MediaAssets.AnyAsync(x => x.Id == mediaId, ct))
            return "The selected media no longer exists.";
        if (input.LayoutId is { } layoutId && !await db.SignageLayouts.AnyAsync(x => x.Id == layoutId && x.PublishedVersion > 0, ct))
            return "The selected layout is not published or no longer exists.";
        if (input.ContentPlaylistId is { } playlistId && !await db.SignageContentPlaylists.AnyAsync(x => x.Id == playlistId && x.PublishedVersion > 0, ct))
            return "The selected signage playlist is not published or no longer exists.";
        if (input.VolumePercent is < 0 or > 150) return "Signage volume must be from 0 to 150 percent.";
        if (input.DisplayPower is not (null or "unchanged" or "on" or "off")) return "Display power must be unchanged, on, or off.";
        if (input.KioskTimeoutSeconds is < 5 or > 86400) return "Kiosk timeout must be from 5 seconds to 24 hours.";
        if (!string.IsNullOrWhiteSpace(input.KioskInteractionUrl) &&
            (!Uri.TryCreate(input.KioskInteractionUrl.Trim(), UriKind.Absolute, out var kioskUri) ||
             kioskUri.Scheme is not ("http" or "https") || !string.IsNullOrWhiteSpace(kioskUri.UserInfo)))
            return "Kiosk interaction content must use an absolute HTTP or HTTPS address without embedded credentials.";
        var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
        var layoutError = SignageLayout.Validate(input.Zones, SignageLayout.ParseAllowlist(organization.SignageSourceAllowlistJson));
        if (layoutError is not null) return layoutError;
        var zoneMediaIds = (input.Zones ?? []).Where(zone => zone.MediaAssetId is not null)
            .Select(zone => zone.MediaAssetId!.Value).Distinct().ToArray();
        if (zoneMediaIds.Length > 0 && await db.MediaAssets.CountAsync(x => zoneMediaIds.Contains(x.Id), ct) != zoneMediaIds.Length)
            return "One or more media items selected for this layout no longer exist.";
        var targetIds = (input.TargetScreenIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (targetIds.Length > 0 && await db.Screens.CountAsync(x => targetIds.Contains(x.Id), ct) != targetIds.Length)
            return "One or more selected screens no longer exist.";
        return null;
    }

    internal static void ApplySignage(SignagePlaylist item, SignageInput input)
    {
        var recurrence = SignageSchedule.NormalizeRecurrence(input.Recurrence);
        item.Name = input.Name.Trim();
        item.Mode = input.Mode is "emergency" or "idle" ? input.Mode : "scheduled";
        item.Enabled = input.Enabled;
        item.Priority = input.Priority;
        item.Message = input.Message?.Trim() ?? "";
        item.BackgroundColor = IsColor(input.BackgroundColor) ? input.BackgroundColor! : "#25302d";
        item.TextColor = IsColor(input.TextColor) ? input.TextColor! : "#ffffff";
        item.MediaAssetId = input.MediaAssetId;
        item.TargetTagsCsv = input.TargetTagsCsv?.Trim() ?? "";
        item.Recurrence = recurrence;
        item.StartsAt = recurrence == "once" ? input.StartsAt : null;
        item.EndsAt = recurrence == "once" ? input.EndsAt : null;
        item.ScheduleStartDate = recurrence == "once" ? null : input.ScheduleStartDate;
        item.ScheduleEndDate = recurrence == "once" ? null : input.ScheduleEndDate;
        item.StartMinutes = recurrence == "once" ? null : input.StartMinutes ?? 0;
        item.EndMinutes = recurrence == "once" ? null : input.EndMinutes ?? 1440;
        item.DaysOfWeekCsv = recurrence == "weekly" ? SignageSchedule.NormalizeDays(input.DaysOfWeek) : "";
        item.ExcludedDatesJson = recurrence == "once" ? "[]" : SignageSchedule.StoreDates(input.ExcludedDates);
        item.TargetScreenIdsJson = SignageSchedule.StoreScreenIds(input.TargetScreenIds);
        item.LayoutPreset = SignageLayout.NormalizePreset(input.LayoutPreset);
        item.ZonesJson = SignageLayout.StoreZones(input.Zones);
        item.LayoutId = input.LayoutId;
        item.ContentPlaylistId = input.ContentPlaylistId;
        item.VolumePercent = Math.Clamp(input.VolumePercent, 0, 150);
        item.DisplayPower = input.DisplayPower is "on" or "off" ? input.DisplayPower : "unchanged";
        item.KioskEnabled = input.KioskEnabled;
        item.KioskInteractionUrl = NullIfBlank(input.KioskInteractionUrl);
        item.KioskTimeoutSeconds = Math.Clamp(input.KioskTimeoutSeconds, 5, 86400);
        item.KioskShowCloseButton = input.KioskShowCloseButton;
        item.KioskShowTouchIndicator = input.KioskShowTouchIndicator;
        item.KioskVirtualKeyboard = input.KioskVirtualKeyboard;
        item.Version++;
        item.PublishedVersion = item.Version;
        item.PublishState = "published";
        item.PublishedAt = DateTimeOffset.UtcNow;
        item.LastPushedAt = DateTimeOffset.UtcNow;
        var zones = SignageLayout.ParseZones(item.ZonesJson).ToDictionary(zone => zone.Id, StringComparer.OrdinalIgnoreCase);
        item.WidgetCacheJson = SignageLayout.StoreCache(SignageLayout.ParseCache(item.WidgetCacheJson).Where(entry =>
            zones.TryGetValue(entry.ZoneId, out var zone) && string.Equals(entry.Source, zone.SourceUrl, StringComparison.OrdinalIgnoreCase)));
        item.WidgetCacheError = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string SignageReadiness(MediaAsset? media)
    {
        if (media is null) return "ready";
        if (media.SourceKind == "youtube-download" &&
            YouTubeMedia.EmbedUrl(media.SourceUrl) is not null && string.IsNullOrWhiteSpace(media.RelativePath))
            return "ready";
        if (media.ProcessingStatus is "pending" or "processing" || media.CompatibilityStatus is "pending" or "processing")
            return "preparing";
        if (media.ProcessingStatus == "failed")
            return YouTubeMedia.EmbedUrl(media.SourceUrl) is not null ? "ready" : "failed";
        if (media.CompatibilityStatus == "failed") return "failed";
        if (media.SourceKind != "link" && string.IsNullOrWhiteSpace(media.RelativePath)) return "missing";
        return "ready";
    }

    private static string SignageCacheState(Screen screen, IReadOnlyCollection<string> itemIds)
    {
        try
        {
            using var document = JsonDocument.Parse(screen.CacheInventoryJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return "unknown";
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("itemId", out var id) || id.GetString() is not { } itemId || !itemIds.Contains(itemId)) continue;
                var state = entry.TryGetProperty("state", out var stateValue) ? stateValue.GetString() ?? "unknown" : "unknown";
                if (state == "failed") return "failed";
                if (state == "ready" || state == "cached") matched.Add(itemId);
            }
            return matched.Count == itemIds.Count ? "cached" : "unknown";
        }
        catch (JsonException) { }
        return "unknown";
    }

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
        try { return new System.Net.Mail.MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static AccountToken NewAccountToken(Guid accountId, string purpose, string raw, TimeSpan lifetime,
        string? pendingEmail = null) => new()
    {
        AccountId = accountId, Purpose = purpose, TokenHash = AccountEmailService.Hash(raw),
        PendingEmail = pendingEmail, ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime)
    };

    private static Task<AdminAccount?> CurrentAccountAsync(HttpContext context, LessonCueDb db, CancellationToken ct) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? db.AdminAccounts.SingleOrDefaultAsync(x => x.Id == id, ct)
            : Task.FromResult<AdminAccount?>(null);

    private static IQueryable<AdminAccount> EligibleMfaAccounts(LessonCueDb db) =>
        db.AdminAccounts.Where(account => !account.Disabled && !account.PendingApproval &&
            !account.PendingSetup && account.EmailVerified);

    private static bool HasEnrolledMfa(AdminAccount account) =>
        account.TotpEnabled && !string.IsNullOrWhiteSpace(account.TotpSecretProtected);

    private static Guid CurrentAccountId(HttpContext context) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : Guid.Empty;

    private static Task<AccountToken?> FindTokenAsync(LessonCueDb db, string raw, string purpose, CancellationToken ct) =>
        AccountTokenLookup.FindAsync(db, raw, purpose, ct);

    private static async Task InvalidateTokensAsync(LessonCueDb db, Guid accountId, string purpose, CancellationToken ct)
    {
        var tokens = await db.AccountTokens.Where(x => x.AccountId == accountId && x.Purpose == purpose && x.UsedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens) token.UsedAt = DateTimeOffset.UtcNow;
    }

    private static string AccountUrl(Organization organization, HttpRequest request, string path, string token)
    {
        var origin = string.IsNullOrWhiteSpace(organization.PublicBaseUrl)
            ? $"{request.Scheme}://{request.Host}"
            : organization.PublicBaseUrl;
        return string.IsNullOrEmpty(token)
            ? $"{origin.TrimEnd('/')}{path}"
            : $"{origin.TrimEnd('/')}{path}?token={Uri.EscapeDataString(token)}";
    }

    private static Task SendAccountLinkAsync(AccountEmailService email, Organization organization,
        string recipient, string subject, string action, string url, CancellationToken ct)
    {
        var safeUrl = System.Net.WebUtility.HtmlEncode(url);
        var safeAction = System.Net.WebUtility.HtmlEncode(action);
        return email.SendAsync(organization, recipient, subject,
            $"<p>{System.Net.WebUtility.HtmlEncode(organization.Name)} sent you a one-time LessonCue account link.</p>" +
            $"<p><a href=\"{safeUrl}\">{safeAction}</a></p>" +
            "<p>If you did not request this, ignore this message.</p>", ct);
    }

    private static bool IsSupportedMediaExtension(string extension) =>
        MediaFormatCatalog.IsSupported(extension);

    private static string NormalizeContentType(string fileName, string? _) =>
        MediaFormatCatalog.ContentType(Path.GetExtension(fileName));

    private static string? NormalizeMediaName(string? value, string originalName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = Path.GetFileName(value.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return null;
        var originalExtension = Path.GetExtension(originalName);
        var requestedExtension = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(originalExtension))
        {
            if (!string.IsNullOrEmpty(requestedExtension) &&
                !string.Equals(requestedExtension, originalExtension, StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrEmpty(requestedExtension)) name += originalExtension;
        }
        else if (!string.IsNullOrEmpty(requestedExtension)) return null;
        return name.Length > 255 ? null : name;
    }

    private static async Task<string?> FindMediaNameConflictAsync(LessonCueDb db, IReadOnlyCollection<Guid> excludedIds,
        IEnumerable<string> names, CancellationToken ct)
    {
        var proposed = names.ToArray();
        if (proposed.Length == 0) return null;
        var excluded = excludedIds.ToArray();
        var existing = await db.MediaAssets.Where(item => !excluded.Contains(item.Id)).Select(item => item.FileName).ToListAsync(ct);
        return proposed.Select(name => existing.FirstOrDefault(value =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(value => value is not null);
    }

    private static MediaAssetVersion CreateArchivedVersion(MediaAsset media, string relativePath, string actor) => new()
    {
        MediaAssetId = media.Id,
        VersionNumber = media.Version,
        FileName = media.FileName,
        ContentType = media.ContentType,
        RelativePath = relativePath,
        Sha256 = media.Sha256,
        SizeBytes = media.SizeBytes,
        DurationMs = media.DurationMs,
        SourceKind = media.SourceKind,
        SourceUrl = media.SourceUrl,
        LinkKind = media.LinkKind,
        ArchivedBy = actor
    };

    private static (string? Thumbnail, string? Filmstrip, string? Waveform, string? Compatibility) ResetMediaProcessing(MediaAsset media,
        bool clearConversion = false)
    {
        var derivatives = (media.ThumbnailPath, media.FilmstripPath, media.WaveformPath, media.CompatibilityPath);
        media.DurationMs = null;
        media.OfflineEligible = false;
        media.ProcessingStatus = "pending";
        media.ProcessingError = null;
        media.VideoCodec = null;
        media.AudioCodec = null;
        media.Width = null;
        media.Height = null;
        media.LoudnessLufs = null;
        media.ThumbnailPath = null;
        media.FilmstripPath = null;
        media.WaveformPath = null;
        media.CompatibilityPath = null;
        media.CompatibilitySha256 = null;
        media.CompatibilitySizeBytes = null;
        media.CompatibilityStatus = "pending";
        media.CompatibilityError = null;
        media.CompatibilityTranscodedAt = null;
        media.CompatibilityTranscodeEngine = null;
        if (clearConversion)
        {
            media.ConversionStatus = "none";
            media.ConversionError = null;
            media.ConvertedSlidesJson = "[]";
            media.ConvertedAt = null;
            media.ConversionLessonId = null;
        }
        return derivatives;
    }

    private static void DeleteDerivatives(MediaStoragePaths paths,
        (string? Thumbnail, string? Filmstrip, string? Waveform, string? Compatibility) derivatives)
    {
        foreach (var relative in new[] { derivatives.Thumbnail, derivatives.Filmstrip, derivatives.Waveform })
        {
            var path = relative is null ? null : ResolveStoredFile(paths.Thumbnails, relative);
            if (path is not null) TryDeleteFile(path);
        }
        var compatibility = derivatives.Compatibility is null ? null : ResolveStoredFile(paths.Compatibility, derivatives.Compatibility);
        if (compatibility is not null) TryDeleteFile(compatibility);
    }

    private static async Task DeleteAdaptiveTranscodesAsync(LessonCueDb db, MediaStoragePaths paths, Guid mediaId, CancellationToken ct)
    {
        var variants = await db.MediaTranscodeVariants.Where(x => x.MediaAssetId == mediaId).ToListAsync(ct);
        var storedFiles = variants.Where(x => x.RelativePath is not null).Select(x => x.RelativePath!).ToArray();
        db.MediaTranscodeVariants.RemoveRange(variants);
        await db.SaveChangesAsync(ct);
        foreach (var relativePath in storedFiles)
        {
            var path = ResolveStoredFile(paths.Transcodes, relativePath);
            if (path is not null) TryDeleteFile(path);
        }
    }

    /// <summary>
    /// The origin a Google Drive OAuth callback URI is built on.
    /// </summary>
    /// <remarks>
    /// This URI is handed to Google and is where an authorization code comes
    /// back, so it decides where that code can be delivered. Built from
    /// Request.Host it was built from a header the caller sets, and the only
    /// thing standing between a forged host and a redirected code was Google
    /// refusing a redirect URI that is not registered — Google's allowlist
    /// doing the job of ours.
    ///
    /// The configured public address is preferred instead: an administrator
    /// sets it and it is already validated as an absolute URL where it is
    /// saved. Installations that have not set one keep the previous behaviour
    /// rather than losing a working connection, and still have to be on HTTPS.
    /// </remarks>
    private static string? GoogleDriveCallbackOrigin(string? publicBaseUrl, HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl)
            && Uri.TryCreate(publicBaseUrl.Trim(), UriKind.Absolute, out var configured)
            && configured.Scheme == Uri.UriSchemeHttps)
            return configured.GetLeftPart(UriPartial.Authority)
                + configured.AbsolutePath.TrimEnd('/');

        if (!context.Request.IsHttps || !context.Request.Host.HasValue) return null;
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
    }

    private static string? ResolveStoredFile(string root, string relativePath) =>
        ContainedPath.ResolveExistingFile(root, relativePath);

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static async Task IncrementMediaLessonVersionsAsync(LessonCueDb db, Guid mediaId, CancellationToken ct)
    {
        var lessonIds = await db.PlaylistItems.Where(x => x.MediaAssetId == mediaId).Select(x => x.LessonId).Distinct().ToListAsync(ct);
        var lessons = await db.Lessons.Where(x => lessonIds.Contains(x.Id)).ToListAsync(ct);
        foreach (var lesson in lessons) lesson.Version++;
    }

    private static string NormalizeRole(string? role) => role is "preRoll" or "countdown" or "postLesson" ? role : "lesson";

    private static string DefaultEndBehavior(string role) => role is "preRoll" or "postLesson" ? "loop" : "pause";

    private static bool TryMonitorUrl(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var clean = value.Trim();
        if (clean.Length > 2000 || !Uri.TryCreate(clean, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo)) return false;
        normalized = uri.ToString();
        return true;
    }

    private static PlaylistItem ClonePlaylistItem(PlaylistItem source, Guid lessonId) => new()
    {
        LessonId = lessonId, Title = source.Title, Type = source.Type, Role = source.Role,
        Position = source.Position, MediaAssetId = source.MediaAssetId, DurationMs = source.DurationMs,
        StartMs = source.StartMs, EndMs = source.EndMs, VolumePercent = source.VolumePercent,
        ImageDurationSeconds = source.ImageDurationSeconds, EstimatedDurationSeconds = source.EstimatedDurationSeconds,
        EndBehavior = source.EndBehavior,
        AllowSkip = source.AllowSkip, Notes = source.Notes, FadeInMs = source.FadeInMs,
        FadeOutMs = source.FadeOutMs, NormalizeAudio = source.NormalizeAudio,
        CuePointsJson = source.CuePointsJson, FitMode = source.FitMode,
        RotationDegrees = source.RotationDegrees, CropLeftPercent = source.CropLeftPercent,
        CropTopPercent = source.CropTopPercent, CropRightPercent = source.CropRightPercent,
        CropBottomPercent = source.CropBottomPercent, Muted = source.Muted,
        PlaybackRatePercent = source.PlaybackRatePercent, RepeatCount = source.RepeatCount,
        BackgroundColor = source.BackgroundColor, TransitionStyle = source.TransitionStyle,
        TransitionDurationMs = source.TransitionDurationMs, FlexibleTime = source.FlexibleTime,
        ActivityDefinitionId = source.ActivityDefinitionId
    };

    private static string? ValidatePlaybackSettings(string fitMode, int rotationDegrees,
        int cropLeft, int cropTop, int cropRight, int cropBottom, int playbackRatePercent,
        int repeatCount, string backgroundColor, string transitionStyle, int transitionDurationMs)
    {
        if (fitMode is not ("fit" or "fill" or "letterbox")) return "Choose fit, fill, or letterbox display behavior.";
        if (rotationDegrees is not (0 or 90 or 180 or 270)) return "Rotation must be 0, 90, 180, or 270 degrees.";
        if (new[] { cropLeft, cropTop, cropRight, cropBottom }.Any(value => value is < 0 or > 45) ||
            cropLeft + cropRight >= 90 || cropTop + cropBottom >= 90)
            return "Crop edges must leave at least ten percent of the picture visible in each direction.";
        if (playbackRatePercent is < 25 or > 400) return "Playback speed must be from 25% to 400%.";
        if (repeatCount is < 1 or > 99) return "Repeat count must be from 1 to 99.";
        if (!IsColor(backgroundColor)) return "Background color must be a six-digit hex color.";
        if (transitionStyle is not ("cut" or "fade-black")) return "Choose a cut or fade-through-black transition.";
        if (transitionDurationMs is < 0 or > 5_000) return "Transition duration must be from 0 to 5 seconds.";
        return null;
    }

    private static string? ValidateCueTiming(long startMs, long? endMs, int? imageDurationSeconds,
        int? estimatedDurationSeconds, string endBehavior)
    {
        if (startMs < 0) return "Trim start cannot be negative.";
        if (endMs is not null && endMs <= startMs) return "Trim end must be after trim start.";
        if (imageDurationSeconds is not null && imageDurationSeconds is < 1 or > 3_600)
            return "Still and slide duration must be from 1 second to 1 hour.";
        if (estimatedDurationSeconds is not null && estimatedDurationSeconds is < 1 or > 3_600)
            return "Estimated still duration must be from 1 second to 1 hour.";
        if (endBehavior is not ("advance" or "loop" or "pause" or "stop" or "menu" or "playlistLoop"))
            return "Choose a supported end behavior.";
        return null;
    }

    private static string? ValidateSchedule(RecurringScheduleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Schedule name is required.";
        if (input.Frequency is not ("weekly" or "monthly" or "custom")) return "Choose a weekly, monthly, or custom schedule.";
        if (input.Interval is < 1 or > 52) return "Recurrence interval must be from 1 to 52.";
        if (input.DayOfWeek is < 0 or > 6) return "Day of week is invalid.";
        if (input.DayOfMonth is < 1 or > 31) return "Day of month is invalid.";
        if (input.StartMinutes is < 0 or > 1439) return "Start time is invalid.";
        if (input.EndDate is DateOnly end && end < input.StartDate) return "End date must be on or after the start date.";
        if (input.GenerateDaysAhead is < 1 or > 730) return "Generation window must be from 1 to 730 days.";
        if (input.Frequency == "weekly" && input.DayOfWeek is null) return "Choose a weekday for a weekly schedule.";
        if (input.Frequency == "monthly" && input.DayOfMonth is null) return "Choose a day of month for a monthly schedule.";
        if (input.Frequency == "custom" && (input.CustomDates is null || input.CustomDates.Count == 0)) return "Add at least one custom date.";
        if ((input.CustomDates?.Count ?? 0) > 500 || (input.ExcludedDates?.Count ?? 0) > 500) return "A schedule supports at most 500 custom or excluded dates.";
        return null;
    }

    private static void ApplySchedule(RecurringLessonSchedule schedule, RecurringScheduleInput input)
    {
        schedule.TemplateId = input.TemplateId; schedule.ClassId = input.ClassId; schedule.Name = input.Name.Trim();
        schedule.Frequency = input.Frequency; schedule.Interval = input.Interval; schedule.DayOfWeek = input.DayOfWeek;
        schedule.DayOfMonth = input.DayOfMonth; schedule.StartDate = input.StartDate; schedule.EndDate = input.EndDate;
        schedule.StartMinutes = input.StartMinutes;
        schedule.TitlePattern = string.IsNullOrWhiteSpace(input.TitlePattern) ? "{template} — {date}" : input.TitlePattern.Trim();
        schedule.CustomDatesJson = LessonScheduleService.SerializeDates(input.CustomDates ?? []);
        schedule.ExcludedDatesJson = LessonScheduleService.SerializeDates(input.ExcludedDates ?? []);
        schedule.Enabled = input.Enabled; schedule.GenerateDaysAhead = input.GenerateDaysAhead;
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? ValidateCredentials(string username, string password)
    {
        return AdminCredentialPolicy.Validate(username, password);
    }

    private static string NormalizeControllerSlug(string? value)
    {
        var source = (value ?? "").Trim().ToLowerInvariant();
        var joined = string.Join('-', source.Split([' ', '_', '/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        var result = new string(joined.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-').ToArray());
        while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-", StringComparison.Ordinal);
        return result.Trim('-');
    }

    private static string? NormalizeHostname(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('.').ToLowerInvariant();

    private static string? ValidateControllerAddress(string slug, string? color, string? hostname)
    {
        if (slug.Length is < 1 or > 63 || slug.StartsWith('-') || slug.EndsWith('-'))
            return "Controller path must be 1–63 lowercase letters, numbers, or hyphens.";
        var selectedColor = color?.Trim() ?? "#2d6a4f";
        if (selectedColor.Length != 7 || selectedColor[0] != '#' || selectedColor[1..].Any(character => !Uri.IsHexDigit(character)))
            return "Controller color must be a six-digit hex color.";
        var normalizedHost = NormalizeHostname(hostname);
        if (normalizedHost is not null && (normalizedHost.Length > 253 || normalizedHost.Contains('/') || normalizedHost.Contains(':') ||
            normalizedHost.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-') || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))))
            return "Controller hostname must be a hostname only, without https://, a port, or a path.";
        return null;
    }

    internal static async Task<TemporaryControllerSession?> ResolveControllerSessionAsync(string token,
        ControllerSessionService controllerSessions, LessonCueDb db, CancellationToken ct)
    {
        var normalized = token.Trim().ToLowerInvariant();
        var temporary = controllerSessions.Get(normalized);
        if (temporary is not null) return temporary;
        if (normalized.Length != 48 || normalized.Any(character => !Uri.IsHexDigit(character))) return null;
        var persistent = await db.Classes.AsNoTracking()
            .Where(x => x.PermanentControllerToken == normalized)
            .Select(x => new
            {
                x.Id,
                x.PermanentControllerLessonId,
                x.PermanentControllerCreatedAt
            })
            .SingleOrDefaultAsync(ct);
        return persistent is null ? null : new TemporaryControllerSession(normalized, persistent.Id,
            persistent.PermanentControllerLessonId, persistent.PermanentControllerCreatedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.MaxValue, true);
    }

    private static Task SignInAsync(HttpContext context, AdminAccount account)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.Role, account.Role),
            new Claim("display_name", account.DisplayName),
            new Claim("session_version", account.SessionVersion.ToString(CultureInfo.InvariantCulture)),
            new Claim("lessoncue_permissions_version", "1")
        };
        if (account.MustChangePassword) claims.Add(new Claim("must_change_password", "true"));
        claims.AddRange(LessonCuePermissions.Effective(account).Select(permission =>
            new Claim(LessonCuePermissions.ClaimType, permission)));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
    }

    /// <summary>Just the host from an absolute URL, for building a service address.</summary>
    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : null;

    /// <summary>A published port from the environment, or the compose default.</summary>
    private static int PortFromEnvironment(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value is > 0 and < 65536 ? value : fallback;

    private static void Audit(LessonCueDb db, string action, Guid id, string? summary, string result = "success") =>
        db.AuditEvents.Add(new AuditEvent { Actor = "admin", Action = action, Object = id.ToString(), Result = result, Summary = summary });

    private static void DeleteDiagnosticScreenshot(string dataPath, string? relativePath) =>
        ContainedPath.DeleteIfContained(dataPath, relativePath);

    private static void ClearDiagnosticScreenshot(Screen screen)
    {
        screen.ScreenshotRequestId = null;
        screen.ScreenshotRequestedAt = null;
        screen.ScreenshotExpiresAt = null;
        screen.ScreenshotStatus = "none";
        screen.ScreenshotCapturedAt = null;
        screen.ScreenshotRelativePath = null;
    }

    private static string HashDeviceToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static Task InvalidateAsync(IHubContext<SyncHub> hub, int version, CancellationToken ct) =>
        hub.Clients.All.SendAsync("ManifestInvalidated", new { type = "MANIFEST_INVALIDATED", manifestVersion = version }, ct);

    private static IResult StorageExceeded(long requestedBytes) => Results.Json(new
    {
        error = $"Not enough LessonCue storage is available for this upload ({requestedBytes} bytes requested). Ask an administrator to increase the allocation or remove media."
    }, statusCode: StatusCodes.Status507InsufficientStorage);

    private static string FormatBytes(long value) =>
        value >= 1024L * 1024 * 1024
            ? $"{value / (1024d * 1024 * 1024):0.##} GB"
            : $"{value / (1024d * 1024):0.##} MB";

    private static IResult UploadError(int statusCode, string error, StorageSnapshot? storage = null) =>
        Results.Json(new
        {
            error,
            storage = storage is null ? null : new
            {
                storage.AllocationBytes,
                storage.UsedBytes,
                storage.RemainingBytes,
                storage.DiskAvailableBytes
            }
        }, statusCode: statusCode);
}
