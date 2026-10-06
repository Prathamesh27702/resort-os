using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Resort.Application.Common;
using Resort.Application.Identity;
using Resort.Domain.Identity;
using Resort.Infrastructure.Persistence;

namespace Resort.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "resort-os";
    public string Audience { get; set; } = "resort-os";
    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supply via environment/secret store, never commit it.</summary>
    public string SigningKey { get; set; } = "";

    public SymmetricSecurityKey Key()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Set it via the Jwt__SigningKey environment variable.");
        return new SymmetricSecurityKey(bytes);
    }
}

public static class ResortClaims
{
    public const string Organization = "org";
    public const string SecurityStamp = "sstamp";
}

public sealed class TokenService(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth, TimeProvider clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(auth.Value.AccessTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = jwt.Value.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.Value.Key(), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Name, user.FullName),
                new Claim(ResortClaims.Organization, user.OrganizationId.ToString()),
                new Claim(ResortClaims.SecurityStamp, user.SecurityStamp.ToString()),
            ]),
        });
        return (token, expires);
    }

    public (string RawToken, byte[] Hash) CreateRefreshToken()
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (raw, HashRefreshToken(raw));
    }

    public byte[] HashRefreshToken(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}

/// <summary>PBKDF2 (ASP.NET Core Identity v3 format: HMAC-SHA512, 100k iterations, per-hash salt).</summary>
public sealed class PasswordHasherService : IPasswordHasherService
{
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly User Dummy = new();
    private static readonly string DummyHash = Hasher.HashPassword(Dummy, Guid.NewGuid().ToString());

    public string Hash(string password) => Hasher.HashPassword(Dummy, password);

    public PasswordCheck Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(Dummy, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };

    public void SimulateVerify(string password) => Hasher.VerifyHashedPassword(Dummy, DummyHash, password);
}

/// <summary>
/// Loads a user's active flag, security stamp and role grants, cached for 30 seconds.
/// Changes made through this API invalidate the entry immediately; the TTL bounds staleness for
/// anything else (e.g. a direct DB edit, or another API instance — Sprint 26 moves this to a shared cache).
/// </summary>
public sealed class UserAccessService(IServiceScopeFactory scopes, IMemoryCache cache) : IUserAccessService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static string Key(Guid id) => $"user-access:{id}";

    public async Task<UserAccess?> GetAsync(Guid userId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(userId), out UserAccess? hit)) return hit;

        // Own scope: this runs during authentication, before the request's ICurrentUser is populated.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.DeletedAt == null)
            .Select(u => new { u.Id, u.OrganizationId, u.IsActive, u.SecurityStamp, u.MustChangePassword })
            .SingleOrDefaultAsync(ct);
        if (user is null) return null;

        var grants = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => new
            {
                ur.PropertyId,
                ur.Role.Code,
                Perms = db.RolePermissions.Where(rp => rp.RoleId == ur.RoleId).Select(rp => rp.PermissionCode).ToList(),
            })
            .ToListAsync(ct);

        var access = new UserAccess(user.Id, user.OrganizationId, user.IsActive, user.SecurityStamp, user.MustChangePassword,
            grants.Select(g => new ScopedGrant(g.PropertyId, g.Code, g.Perms.ToHashSet(StringComparer.Ordinal))).ToList());

        cache.Set(Key(userId), access, Ttl);
        return access;
    }

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));
}
