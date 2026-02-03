using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Auth.Siwe;

public sealed class JwtTokenService : ITokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtTokenService(SymmetricSecurityKey key, string issuer, string audience)
    {
        _key = key; _issuer = issuer; _audience = audience;
    }

    public string Issue(TokenSubject subject, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.Sub),
            new("addr", subject.Address.ToLowerInvariant()),
            new("chainId", subject.ChainId.ToString())
        };

        var jwt = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public static SymmetricSecurityKey BuildKey(string secret)
        => new(Encoding.UTF8.GetBytes(secret));
}