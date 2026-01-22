using System.Text.Json;
using Circles.Market.Api;

namespace Circles.Market.Tests;

[TestFixture]
public class JsonLdShapeVerifierCidTests
{
    private static readonly JsonLdShapeVerifier Verifier = new();

    private static (bool ok, string? reason) CanPin(object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        var ok = Verifier.CanPin(bytes, out var reason);
        return (ok, reason);
    }

    [Test]
    public void NameIndexDoc_Allows_CidV0_For_Head_And_Entries()
    {
        var doc = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.NamespaceContext,
            ["@type"] = "NameIndexDoc",
            ["head"] = "QmYwAPJzv5CZsnAztbCQCF4Gx5An1hYz3fy1cS4GgEcg7F",
            ["entries"] = new Dictionary<string, object?>
            {
                ["product/sku-1"] = "QmRJzSVrQjrRqt8aKGsPRMDGwVf1U1WgMh6ZY1FGLwZ5bE"
            }
        };

        var (ok, reason) = CanPin(doc);
        Assert.That(ok, Is.True, () => reason ?? "reason null");
    }

    [Test]
    public void NameIndexDoc_Rejects_CidV1_For_Head_And_Entries()
    {
        // A typical CIDv1 in base32 starts with "bafy"; use dummy strings that are not CIDv0
        var doc = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.NamespaceContext,
            ["@type"] = "NameIndexDoc",
            ["head"] = "bafybeigdyrzt5o3jg4q7wq7q3t7n2g2xg2d3w3q4r5t6y7u8v9w0x1y2z3",
            ["entries"] = new Dictionary<string, object?>
            {
                ["product/sku-1"] = "bafybeihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku"
            }
        };

        var (ok, reason) = CanPin(doc);
        Assert.That(ok, Is.False, "Expected CIDv1 to be rejected");
        Assert.That(reason, Does.Contain("CIDv0").IgnoreCase);
    }

    [Test]
    public void NamespaceChunk_Prev_Must_Be_CidV0_Or_Null()
    {
        // prev = CIDv0 → OK
        var okChunk = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.NamespaceContext,
            ["@type"] = "NamespaceChunk",
            ["prev"] = "QmRJzSVrQjrRqt8aKGsPRMDGwVf1U1WgMh6ZY1FGLwZ5bE",
            ["links"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "hello",
                    ["cid"] = "QmYwAPJzv5CZsnAztbCQCF4Gx5An1hYz3fy1cS4GgEcg7F",
                    ["signerAddress"] = "0x000000000000000000000000000000000000dEaD",
                    ["signature"] = "0x01"
                }
            }
        };
        var (ok1, reason1) = CanPin(okChunk);
        Assert.That(ok1, Is.True, () => reason1 ?? "null");

        // prev = null → OK
        var nullPrev = new Dictionary<string, object?>
        {
            ["@context"] = Circles.Profiles.Models.JsonLdMeta.NamespaceContext,
            ["@type"] = "NamespaceChunk",
            ["prev"] = null,
            ["links"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "hello",
                    ["cid"] = "QmYwAPJzv5CZsnAztbCQCF4Gx5An1hYz3fy1cS4GgEcg7F",
                    ["signerAddress"] = "0x000000000000000000000000000000000000dEaD",
                    ["signature"] = "0x01"
                }
            }
        };
        var (ok2, reason2) = CanPin(nullPrev);
        Assert.That(ok2, Is.True, () => reason2 ?? "null");

        // prev = CIDv1 → REJECT
        var badChunk = new
        {
            @context = Circles.Profiles.Models.JsonLdMeta.NamespaceContext,
            @type = "NamespaceChunk",
            prev = "bafybeihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku",
            links = new object[]
            {
                new {
                    name = "hello",
                    cid = "QmYwAPJzv5CZsnAztbCQCF4Gx5An1hYz3fy1cS4GgEcg7F",
                    signerAddress = "0x000000000000000000000000000000000000dEaD",
                    signature = "0x01"
                }
            }
        };
        var (ok3, reason3) = CanPin(badChunk);
        Assert.That(ok3, Is.False, "Expected CIDv1 prev to be rejected");
        // Exact error message is not critical; just ensure rejection
        Assert.That(ok3, Is.False);
    }
}
