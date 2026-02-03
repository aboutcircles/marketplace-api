using Circles.Profiles.Models.Core;

namespace Circles.Market.Auth.Siwe;

internal static class SiweMessageBuilder
{
    public static (string message, string nonce, DateTimeOffset issuedAt, DateTimeOffset expiresAt) Build(
        string domain, string uri, string statement, string address, long chainId, TimeSpan ttl)
    {
        string nonce = CustomDataLink.NewNonce();
        var now = DateTimeOffset.UtcNow;
        var exp = now.Add(ttl);
        var msg = $"{domain} wants you to sign in with your Ethereum account:\n" +
                  $"{address}\n\n" +
                  $"{statement}\n\n" +
                  $"URI: {uri}\n" +
                  $"Version: 1\n" +
                  $"Chain ID: {chainId}\n" +
                  $"Nonce: {nonce}\n" +
                  $"Issued At: {now:O}\n" +
                  $"Expiration Time: {exp:O}";
        return (msg, nonce, now, exp);
    }
}