using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Audit;
using Resort.Domain.Identity;

namespace Resort.Application.Identity;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
    public int LockoutThreshold { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>Successful sign-in/refresh: the API puts <see cref="RefreshToken"/> in an HttpOnly cookie
/// and returns <see cref="Response"/> as JSON.</summary>
public sealed record AuthResult(AuthResponse Response, string RefreshToken, DateTimeOffset RefreshExpiresAt);

public sealed class AuthService(
    IAppDbContext db,
    IPasswordHasherService hasher,
    ITokenService tokens,
    IUserAccessService access,
    ICurrentUser caller,
    TimeProvider clock,
    IOptions<AuthOptions> options)
{
    private readonly AuthOptions _opt = options.Value;

    public async Task<AuthResult> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var normalized = req.Email.Trim().ToLowerInvariant();

        // Tenant filter can't apply yet (anonymous), so bypass it explicitly for this lookup only.
        var user = await db.Users.IgnoreQueryFilters()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized && u.DeletedAt == null, ct);

        if (user is null)
        {
            // Same cost as a wrong password, so response time doesn't reveal which emails exist.
            hasher.SimulateVerify(req.Password);
            await RecordLoginAsync(null, normalized, false, "UNKNOWN_EMAIL", ct);
            throw InvalidCredentials();
        }

        if (!user.IsActive)
        {
            await RecordLoginAsync(user.Id, normalized, false, "INACTIVE", ct);
            throw InvalidCredentials();
        }

        if (user.IsLockedOut(now))
        {
            await RecordLoginAsync(user.Id, normalized, false, "LOCKED_OUT", ct);
            throw new UnauthorizedAppException("ACCOUNT_LOCKED",
                "Too many failed attempts. Try again later or ask a manager to reset your password.");
        }

        var check = hasher.Verify(user.PasswordHash, req.Password);
        if (check == PasswordCheck.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _opt.LockoutThreshold)
            {
                user.LockoutUntil = now.AddMinutes(_opt.LockoutMinutes);
                user.FailedLoginCount = 0;
            }
            await RecordLoginAsync(user.Id, normalized, false, "BAD_PASSWORD", ct, saveNow: false);
            await db.SaveChangesAsync(ct);
            throw InvalidCredentials();
        }

        if (check == PasswordCheck.SuccessRehashNeeded)
            user.PasswordHash = hasher.Hash(req.Password);

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;

        var (raw, expires) = IssueRefreshToken(user.Id, Guid.NewGuid(), now);
        await RecordLoginAsync(user.Id, normalized, true, null, ct, saveNow: false);
        await db.SaveChangesAsync(ct);

        return new AuthResult(await BuildResponseAsync(user, ct), raw, expires);
    }

    /// <summary>
    /// Rotates a refresh token. The row is locked (SELECT … FOR UPDATE) so two concurrent refreshes with the
    /// same token serialise: the first rotates it, the second then sees it as already rotated.
    /// Presenting an already-rotated token is treated as theft: the whole token family is revoked.
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string rawToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(rawToken);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var token = await db.RefreshTokens
            .FromSql($"SELECT * FROM refresh_tokens WHERE token_hash = {hash} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAppException("INVALID_REFRESH_TOKEN", "Session expired. Please sign in again.");

        if (token.RevokedAt is not null)
        {
            if (token.RevokedReason == RevokeReasons.Rotated)
            {
                await RevokeFamilyAsync(token.FamilyId, RevokeReasons.ReuseDetected, now, ct);
                var owner = await db.Users.IgnoreQueryFilters().Where(u => u.Id == token.UserId)
                    .Select(u => new { u.OrganizationId }).SingleAsync(ct);
                db.AuditLogs.Add(new AuditLog
                {
                    OrganizationId = owner.OrganizationId,
                    UserId = token.UserId,
                    Action = AuditActions.RefreshTokenReuse,
                    EntityType = nameof(RefreshToken),
                    EntityId = token.FamilyId.ToString(),
                    IpAddress = caller.IpAddress,
                    CorrelationId = caller.CorrelationId,
                    RequestId = caller.RequestId,
                    UserAgent = Truncate(caller.UserAgent, 500),
                    OccurredAt = now,
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            throw new UnauthorizedAppException("INVALID_REFRESH_TOKEN", "Session expired. Please sign in again.");
        }

        if (token.ExpiresAt <= now)
            throw new UnauthorizedAppException("INVALID_REFRESH_TOKEN", "Session expired. Please sign in again.");

        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == token.UserId, ct);
        if (!user.IsActive || user.DeletedAt is not null)
        {
            token.Revoke(now, RevokeReasons.UserDeactivated);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new UnauthorizedAppException("INVALID_REFRESH_TOKEN", "Session expired. Please sign in again.");
        }

        var (raw, expires) = IssueRefreshToken(user.Id, token.FamilyId, now, out var newToken);
        token.Revoke(now, RevokeReasons.Rotated);
        token.ReplacedById = newToken.Id;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new AuthResult(await BuildResponseAsync(user, ct), raw, expires);
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken)) return;
        var hash = tokens.HashRefreshToken(rawToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        await RevokeFamilyAsync(token.FamilyId, RevokeReasons.Logout, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MeResponse> MeAsync(CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == caller.RequireUserId(), ct);
        return await BuildMeAsync(user, ct);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.SingleAsync(u => u.Id == caller.RequireUserId(), ct);
        if (hasher.Verify(user.PasswordHash, req.CurrentPassword) == PasswordCheck.Failed)
            throw new BusinessRuleAppException("CURRENT_PASSWORD_INCORRECT", "Current password is incorrect.");

        user.PasswordHash = hasher.Hash(req.NewPassword);
        user.MustChangePassword = false;
        user.RotateSecurityStamp();
        await RevokeAllForUserAsync(user.Id, RevokeReasons.PasswordChanged, now, ct);
        await db.SaveChangesAsync(ct);
        access.Invalidate(user.Id);
    }

    internal async Task RevokeAllForUserAsync(Guid userId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) t.Revoke(now, reason);
    }

    private async Task RevokeFamilyAsync(Guid familyId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var family = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in family) t.Revoke(now, reason);
    }

    private (string Raw, DateTimeOffset Expires) IssueRefreshToken(Guid userId, Guid familyId, DateTimeOffset now) =>
        IssueRefreshToken(userId, familyId, now, out _);

    private (string Raw, DateTimeOffset Expires) IssueRefreshToken(Guid userId, Guid familyId, DateTimeOffset now, out RefreshToken entity)
    {
        var (raw, hash) = tokens.CreateRefreshToken();
        var expires = now.AddDays(_opt.RefreshTokenDays);
        entity = new RefreshToken
        {
            UserId = userId,
            FamilyId = familyId,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = expires,
            CreatedIp = caller.IpAddress,
            UserAgent = Truncate(caller.UserAgent, 500),
        };
        db.RefreshTokens.Add(entity);
        return (raw, expires);
    }

    private async Task<AuthResponse> BuildResponseAsync(User user, CancellationToken ct)
    {
        var (access, expires) = tokens.CreateAccessToken(user);
        return new AuthResponse(access, expires, await BuildMeAsync(user, ct));
    }

    private async Task<MeResponse> BuildMeAsync(User user, CancellationToken ct)
    {
        var org = await db.Organizations.IgnoreQueryFilters().Where(o => o.Id == user.OrganizationId)
            .Select(o => new OrganizationSummary(o.Id, o.Code, o.Name)).SingleAsync(ct);

        access.Invalidate(user.Id);
        var resolved = await access.GetAsync(user.Id, ct)
            ?? throw new UnauthorizedAppException();

        var grants = resolved.Grants
            .GroupBy(g => g.PropertyId)
            .Select(g => new AccessGrantDto(
                g.Key,
                g.Select(x => x.RoleCode).Distinct().Order().ToList(),
                g.SelectMany(x => x.Permissions).Distinct().Order().ToList()))
            .ToList();

        return new MeResponse(user.Id, user.Email, user.FullName, user.MustChangePassword, org, grants);
    }

    private async Task RecordLoginAsync(Guid? userId, string email, bool success, string? reason, CancellationToken ct, bool saveNow = true)
    {
        db.LoginEvents.Add(new LoginEvent
        {
            UserId = userId,
            EmailAttempted = Truncate(email, 320)!,
            Success = success,
            FailureReason = reason,
            IpAddress = caller.IpAddress,
            UserAgent = Truncate(caller.UserAgent, 500),
            OccurredAt = clock.GetUtcNow(),
        });
        if (saveNow) await db.SaveChangesAsync(ct);
    }

    private static UnauthorizedAppException InvalidCredentials() =>
        new("INVALID_CREDENTIALS", "Email or password is incorrect.");

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
