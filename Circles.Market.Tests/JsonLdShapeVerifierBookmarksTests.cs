using System.Text.Json;
using Circles.Market.Api;

namespace Circles.Market.Tests;

[TestFixture]
public class JsonLdShapeVerifierBookmarksTests
{
    private static readonly JsonLdShapeVerifier Verifier = new();

    private static (bool ok, string? reason) CanPin(object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        var ok = Verifier.CanPin(bytes, out var reason);
        return (ok, reason);
    }

    [Test]
    public void Bookmarks_Accepts_Valid_Payload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.ProfileContext,
            ["@type"] = "Bookmarks",
            ["bookmarks"] = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["profiles"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = "0x1234567890abcdef1234567890abcdef12345678",
                        ["createdAt"] = 1739123456789,
                        ["note"] = "optional note",
                        ["folder"] = "optional/folder"
                    }
                },
                ["folders"] = new[] { "VIPs", "optional/folder" },
                ["products"] = Array.Empty<string>()
            }
        };

        var (ok, reason) = CanPin(payload);
        Assert.That(ok, Is.True, () => reason ?? "reason null");
    }

    [Test]
    public void Bookmarks_Rejects_Additional_TopLevel_Properties()
    {
        var payload = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.ProfileContext,
            ["@type"] = "Bookmarks",
            ["extra"] = true,
            ["bookmarks"] = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["profiles"] = Array.Empty<object>(),
                ["folders"] = Array.Empty<string>(),
                ["products"] = Array.Empty<string>()
            }
        };

        var (ok, reason) = CanPin(payload);
        Assert.That(ok, Is.False);
        Assert.That(reason, Does.Contain("unsupported top-level properties"));
    }

    [Test]
    public void Bookmarks_Rejects_Invalid_Profile_Address()
    {
        var payload = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.ProfileContext,
            ["@type"] = "Bookmarks",
            ["bookmarks"] = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["profiles"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = "0xnot-an-address",
                        ["createdAt"] = 1739123456789
                    }
                },
                ["folders"] = Array.Empty<string>(),
                ["products"] = Array.Empty<string>()
            }
        };

        var (ok, reason) = CanPin(payload);
        Assert.That(ok, Is.False);
        Assert.That(reason, Does.Contain("profiles[].address"));
    }

    [Test]
    public void CustomDataLink_For_Bookmarks_Name_Is_Still_Valid()
    {
        var payload = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.LinkContext,
            ["@type"] = "CustomDataLink",
            ["name"] = "circles/bookmarks-v1",
            ["cid"] = "QmYwAPJzv5CZsnAztbCQCF4Gx5An1hYz3fy1cS4GgEcg7F",
            ["encrypted"] = false,
            ["encryptionAlgorithm"] = null,
            ["encryptionKeyFingerprint"] = null,
            ["chainId"] = 100,
            ["signerAddress"] = "0x1234567890abcdef1234567890abcdef12345678",
            ["signedAt"] = 1739123456,
            ["nonce"] = "0x0123456789abcdef0123456789abcdef",
            ["signature"] = "0x" + new string('a', 130)
        };

        var (ok, reason) = CanPin(payload);
        Assert.That(ok, Is.True, () => reason ?? "reason null");
    }
}
