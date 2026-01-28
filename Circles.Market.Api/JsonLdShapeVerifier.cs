using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Profiles.Models;

namespace Circles.Market.Api;

public interface IJsonLdShapeVerifier
{
    bool CanPin(ReadOnlyMemory<byte> jsonUtf8, out string? reason);
}

public sealed class JsonLdShapeVerifier : IJsonLdShapeVerifier
{
    // CIDv0 (sha2-256, base58btc, "Qm" + 44 chars)
    private static readonly Regex CidV0 =
        new("^Qm[1-9A-HJ-NP-Za-km-z]{44}$", RegexOptions.Compiled);

    private static readonly Regex HexLower = new("^0x[0-9a-f]+$", RegexOptions.Compiled);
    private static readonly Regex HexAny = new("^0x[0-9a-fA-F]+$", RegexOptions.Compiled);
    private static readonly Regex Eip55AddrShape = new("^0x[0-9a-fA-F]{40}$", RegexOptions.Compiled);
    private static readonly Regex Iso4217Upper = new("^[A-Z]{3}$", RegexOptions.Compiled);

    public bool CanPin(ReadOnlyMemory<byte> jsonUtf8, out string? reason)
    {
        reason = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonUtf8);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                reason = "Root must be a JSON object";
                return false;
            }

            // --- @type
            if (!root.TryGetProperty("@type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            {
                reason = "Missing or invalid @type";
                return false;
            }

            string type = typeProp.GetString() ?? string.Empty;

            // --- @context (collect string entries only; arrays may include a mapping object which we ignore)
            if (!root.TryGetProperty("@context", out var ctxProp))
            {
                reason = "Missing @context";
                return false;
            }

            var ctx = new HashSet<string>(StringComparer.Ordinal);
            switch (ctxProp.ValueKind)
            {
                case JsonValueKind.String:
                    AddIfNonEmpty(ctxProp.GetString());
                    break;

                case JsonValueKind.Array:
                    foreach (var el in ctxProp.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            AddIfNonEmpty(el.GetString());
                        }
                        else if (el.ValueKind == JsonValueKind.Object)
                        {
                            // Allowed: context array often contains a mapping object; ignore for allow-listing.
                        }
                        else
                        {
                            reason = "Invalid @context";
                            return false;
                        }
                    }

                    break;

                case JsonValueKind.Object:
                    // We don’t reject object context per se, but without a schema URL we can’t whitelist safely.
                    reason = "Top-level @context object is not supported; include the schema URL string(s) as well.";
                    return false;

                default:
                    reason = "Invalid @context";
                    return false;
            }

            // Hard exclusion: market aggregate documents must never be pinned here.
            if (ctx.Contains(JsonLdMeta.MarketAggregateContext))
            {
                reason = "Market aggregate documents are generated and cannot be pinned via this endpoint.";
                return false;
            }

            // Accept any valid model from our contexts (except aggregate), with per-type validation.
            // Keep the checks deliberately strict but cheap: we validate shape, not semantics that require RPC.
            if (type.Equals("Profile", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.ProfileContext))
            {
                // Minimal shape is fine; fields inside are validated elsewhere when used.
                return true;
            }

            if (type.Equals("SigningKey", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.ProfileContext))
            {
                return ValidateSigningKey(root, out reason);
            }

            if (type.Equals("CustomDataLink", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.LinkContext))
            {
                return ValidateCustomDataLink(root, out reason);
            }

            if (type.Equals("BasicMessage", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.ChatContext))
            {
                return ValidateBasicMessage(root, out reason);
            }

            // Market models
            if (type.Equals("Product", StringComparison.Ordinal) &&
                ctx.Contains(JsonLdMeta.SchemaOrg) &&
                ctx.Contains(JsonLdMeta.MarketContext))
            {
                return ValidateProduct(root, out reason);
            }

            if (type.Equals("Tombstone", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.MarketContext))
            {
                return ValidateTombstone(root, out reason);
            }

            // Allow top-level ImageObject & Offer if someone wants to pin those directly (they're valid models too).
            if (type.Equals("ImageObject", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.SchemaOrg))
            {
                return ValidateImageObject(root, out reason);
            }

            if (type.Equals("Offer", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.SchemaOrg))
            {
                return ValidateOffer(root, out reason);
            }

            // Namespace primitives (yes, allow them; the IPFS node already enforces 8 MiB cap).
            if (type.Equals("NameIndexDoc", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.NamespaceContext))
            {
                return ValidateNameIndexDoc(root, out reason);
            }

            if (type.Equals("NamespaceChunk", StringComparison.Ordinal) && ctx.Contains(JsonLdMeta.NamespaceContext))
            {
                return ValidateNamespaceChunk(root, out reason);
            }

            if (type.Equals("Snippet", StringComparison.Ordinal) && ctx.Contains("https://aboutcircles.com/contexts/circles-gist/"))
            {
                return ValidateSnippet(root, out reason);
            }

            reason = $"Unsupported JSON-LD shape: @type='{type}', @context='[" + string.Join(",", ctx) + "]'";
            return false;

            void AddIfNonEmpty(string? s)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    ctx.Add(s!);
                }
            }
        }
        catch (JsonException ex)
        {
            reason = $"Malformed JSON: {ex.Message}";
            return false;
        }
    }

    /* ────────────────────────── validators ────────────────────────── */

    private static bool ValidateSigningKey(JsonElement root, out string? reason)
    {
        reason = null;

        if (!TryGetString(root, "publicKey", out var pk) || string.IsNullOrWhiteSpace(pk))
        {
            reason = "SigningKey.publicKey is required";
            return false;
        }

        if (!TryGetLong(root, "validFrom", out _))
        {
            reason = "SigningKey.validFrom is required and must be a unix timestamp";
            return false;
        }

        if (root.TryGetProperty("validTo", out var _))
        {
            if (!TryGetNullableLong(root, "validTo", out _))
            {
                reason = "SigningKey.validTo must be a unix timestamp when present";
                return false;
            }
        }

        if (root.TryGetProperty("revokedAt", out var _))
        {
            if (!TryGetNullableLong(root, "revokedAt", out _))
            {
                reason = "SigningKey.revokedAt must be a unix timestamp when present";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateCustomDataLink(JsonElement root, out string? reason)
    {
        reason = null;

        // @context must equal the link context URI string (spec §4.4)
        if (!root.TryGetProperty("@context", out var ctxProp) ||
            ctxProp.ValueKind != JsonValueKind.String ||
            !string.Equals(ctxProp.GetString(), JsonLdMeta.LinkContext, StringComparison.Ordinal))
        {
            reason = "CustomDataLink.@context must equal the link context URI string";
            return false;
        }

        // @type must be "CustomDataLink"
        if (!root.TryGetProperty("@type", out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String ||
            !string.Equals(typeProp.GetString(), "CustomDataLink", StringComparison.Ordinal))
        {
            reason = "CustomDataLink.@type must equal 'CustomDataLink'";
            return false;
        }

        // Required core envelope fields (spec §4.4)
        if (!TryGetString(root, "name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            reason = "CustomDataLink.name is required";
            return false;
        }

        if (!TryGetString(root, "cid", out var cid) || !CidV0.IsMatch(cid))
        {
            // Implementation restriction: this endpoint only supports CIDv0 payloads.
            reason = "CustomDataLink.cid must be a CIDv0 (base58btc \"Qm…\")";
            return false;
        }

        if (!TryGetLong(root, "chainId", out _))
        {
            reason = "CustomDataLink.chainId is required and must be an integer";
            return false;
        }

        if (!TryGetString(root, "signerAddress", out var addr) ||
            !HexLower.IsMatch(addr) ||
            addr.Length != 42)
        {
            reason = "CustomDataLink.signerAddress must be a 0x-prefixed 20-byte lowercase hex address";
            return false;
        }

        if (!TryGetLong(root, "signedAt", out _))
        {
            reason = "CustomDataLink.signedAt is required and must be a unix timestamp";
            return false;
        }

        if (!TryGetString(root, "nonce", out var nonce) ||
            !HexLower.IsMatch(nonce) ||
            nonce.Length != 34)
        {
            reason = "CustomDataLink.nonce must be 0x followed by 32 lowercase hex characters";
            return false;
        }

        if (!TryGetString(root, "signature", out var sig) || !HexAny.IsMatch(sig) || sig.Length != 132)
        {
            reason = "CustomDataLink.signature must be 0x followed by 130 hex characters (65 bytes)";
            return false;
        }

        // encrypted is required (bool)
        if (!root.TryGetProperty("encrypted", out var encProp) ||
            encProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            reason = "CustomDataLink.encrypted must be present (boolean)";
            return false;
        }

        bool encrypted = encProp.GetBoolean();

        // encryptionAlgorithm and encryptionKeyFingerprint are always present
        if (!root.TryGetProperty("encryptionAlgorithm", out var algProp))
        {
            reason = "CustomDataLink.encryptionAlgorithm is required";
            return false;
        }

        if (!root.TryGetProperty("encryptionKeyFingerprint", out var fpProp))
        {
            reason = "CustomDataLink.encryptionKeyFingerprint is required";
            return false;
        }

        if (!encrypted)
        {
            // spec: when encrypted = false → both MUST be null
            if (algProp.ValueKind != JsonValueKind.Null || fpProp.ValueKind != JsonValueKind.Null)
            {
                reason = "When encrypted=false, encryptionAlgorithm and encryptionKeyFingerprint MUST be null";
                return false;
            }
        }
        else
        {
            // spec: when encrypted = true → non-empty strings
            if (algProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(algProp.GetString()))
            {
                reason = "When encrypted=true, encryptionAlgorithm MUST be a non-empty string";
                return false;
            }

            if (fpProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fpProp.GetString()))
            {
                reason = "When encrypted=true, encryptionKeyFingerprint MUST be a non-empty string";
                return false;
            }

            var fp = fpProp.GetString()!;
            if (!HexAny.IsMatch(fp))
            {
                reason = "encryptionKeyFingerprint must be a 0x-hex string when present";
                return false;
            }
        }

        // Optional extensions object (spec §4.4)
        bool hasExtensions = root.TryGetProperty("extensions", out var extensionsProp);
        if (hasExtensions)
        {
            if (extensionsProp.ValueKind != JsonValueKind.Object)
            {
                reason = "CustomDataLink.extensions must be a JSON object when present";
                return false;
            }

            foreach (var kv in extensionsProp.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(kv.Name))
                {
                    reason = "CustomDataLink.extensions keys must be non-empty strings";
                    return false;
                }
            }
        }

        // Optional criticalExtensions (spec §4.4)
        if (root.TryGetProperty("criticalExtensions", out var critProp) &&
            critProp.ValueKind != JsonValueKind.Null)
        {
            if (critProp.ValueKind != JsonValueKind.Array)
            {
                reason = "CustomDataLink.criticalExtensions must be an array of strings when present";
                return false;
            }

            bool anyCritical = false;
            foreach (var el in critProp.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    reason = "CustomDataLink.criticalExtensions entries must be strings";
                    return false;
                }

                var key = el.GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    reason = "CustomDataLink.criticalExtensions entries must be non-empty strings";
                    return false;
                }

                anyCritical = true;

                // If extensions is present, each critical entry must correspond to a key in extensions
                if (hasExtensions)
                {
                    bool inExtensions = extensionsProp
                        .EnumerateObject()
                        .Any(p => string.Equals(p.Name, key, StringComparison.Ordinal));

                    if (!inExtensions)
                    {
                        reason = "CustomDataLink.criticalExtensions entries must correspond to keys in extensions";
                        return false;
                    }
                }
            }

            // If there are critical entries but no extensions object, that's also a shape error
            if (anyCritical && !hasExtensions)
            {
                reason = "CustomDataLink.criticalExtensions requires an extensions object when non-empty";
                return false;
            }
        }

        // NOTE: we intentionally allow additional top-level properties here,
        // as required by the spec. Unknown fields are ignored for protocol semantics
        // but preserved by the model via JsonExtensionData.
        return true;
    }

    private static bool ValidateBasicMessage(JsonElement root, out string? reason)
    {
        reason = null;

        if (!TryGetString(root, "from", out var from) ||
            !HexLower.IsMatch(from) ||
            from.Length != 42)
        {
            reason = "BasicMessage.from must be a 0x-prefixed 20-byte lowercase hex address";
            return false;
        }

        if (!TryGetString(root, "to", out var to) ||
            !HexLower.IsMatch(to) ||
            to.Length != 42)
        {
            reason = "BasicMessage.to must be a 0x-prefixed 20-byte lowercase hex address";
            return false;
        }

        if (!TryGetString(root, "text", out var txt) || string.IsNullOrWhiteSpace(txt))
        {
            reason = "BasicMessage.text is required";
            return false;
        }

        if (!TryGetLong(root, "ts", out _))
        {
            reason = "BasicMessage.ts is required and must be a unix timestamp";
            return false;
        }

        return true;
    }

    private static bool ValidateProduct(JsonElement root, out string? reason)
    {
        reason = null;

        if (!TryGetString(root, "name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            reason = "Product.name is required";
            return false;
        }

        if (!root.TryGetProperty("offers", out var offers) || offers.ValueKind != JsonValueKind.Array ||
            offers.GetArrayLength() == 0)
        {
            reason = "Product.offers must be a non-empty array";
            return false;
        }

        // Validate at least the first offer strictly; others must not be obviously invalid.
        int i = 0;
        foreach (var offer in offers.EnumerateArray())
        {
            if (!ValidateOffer(offer, out var offerErr))
            {
                reason = $"Offer[{i}]: {offerErr}";
                return false;
            }

            i++;
        }

        // Optional images
        if (root.TryGetProperty("image", out var imgProp))
        {
            if (imgProp.ValueKind != JsonValueKind.Array)
            {
                reason = "Product.image must be an array when present";
                return false;
            }

            foreach (var img in imgProp.EnumerateArray())
            {
                if (img.ValueKind == JsonValueKind.String)
                {
                    var s = img.GetString();
                    if (!IsAbsoluteUri(s))
                    {
                        reason = "Product.image[] string must be an absolute URI";
                        return false;
                    }
                }
                else if (img.ValueKind == JsonValueKind.Object)
                {
                    if (!ValidateImageObject(img, out var imgErr))
                    {
                        reason = $"Product.image[]: {imgErr}";
                        return false;
                    }
                }
                else
                {
                    reason = "Product.image[] must be string or ImageObject";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateTombstone(JsonElement root, out string? reason)
    {
        reason = null;

        if (!TryGetString(root, "sku", out var sku) || string.IsNullOrWhiteSpace(sku))
        {
            reason = "Tombstone.sku is required";
            return false;
        }

        return true;
    }

    private static bool ValidateImageObject(JsonElement obj, out string? reason)
    {
        reason = null;

        // contentUrl (ipfs://, ar://, data:, http(s)://) OR url (http(s)://)
        bool hasContent = obj.TryGetProperty("contentUrl", out var cu) && cu.ValueKind == JsonValueKind.String;
        bool hasUrl = obj.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String;

        if (!hasContent && !hasUrl)
        {
            reason = "ImageObject needs contentUrl or url";
            return false;
        }

        if (hasContent && !IsImageTransport(cu.GetString()))
        {
            reason = "ImageObject.contentUrl must be ipfs://, cid://, ar://, data: or absolute http(s)://";
            return false;
        }

        if (hasUrl && !IsHttpUrl(u.GetString()))
        {
            reason = "ImageObject.url must be absolute http(s)://";
            return false;
        }

        return true;
    }

    private static bool ValidateOffer(JsonElement offer, out string? reason)
    {
        reason = null;

        if (!TryGetDecimal(offer, "price", out _))
        {
            reason = "Offer.price is required";
            return false;
        }

        if (!TryGetString(offer, "priceCurrency", out var cur) || !Iso4217Upper.IsMatch(cur))
        {
            reason = "Offer.priceCurrency must be an ISO-4217 three-letter uppercase code";
            return false;
        }

        // checkout was removed; do not require or validate it.

        if (offer.TryGetProperty("seller", out var seller) && seller.ValueKind == JsonValueKind.Object)
        {
            if (!seller.TryGetProperty("@id", out var idProp) || idProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idProp.GetString()))
            {
                reason = "Offer.seller.@id must be present when seller is provided";
                return false;
            }
        }

        // requiredSlots, when present, must be an array of non-empty strings
        if (offer.TryGetProperty("requiredSlots", out var rsProp) &&
            rsProp.ValueKind != JsonValueKind.Null)
        {
            if (rsProp.ValueKind != JsonValueKind.Array)
            {
                reason = "Offer.requiredSlots must be an array of strings when present";
                return false;
            }

            foreach (var el in rsProp.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    reason = "Offer.requiredSlots entries must be strings";
                    return false;
                }

                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    reason = "Offer.requiredSlots entries must be non-empty strings";
                    return false;
                }
            }
        }

        // Optional: validate potentialAction when present (schema.org PayAction)
        if (offer.TryGetProperty("potentialAction", out var pa) && pa.ValueKind != JsonValueKind.Null)
        {
            JsonElement action = pa;
            if (pa.ValueKind == JsonValueKind.Array)
            {
                using var e = pa.EnumerateArray();
                if (!e.MoveNext())
                {
                    reason = "Offer.potentialAction must not be an empty array";
                    return false;
                }
                action = e.Current;
            }

            if (action.ValueKind != JsonValueKind.Object)
            {
                reason = "Offer.potentialAction must be an object or array of objects";
                return false;
            }

            if (!ValidatePayAction(action, out var actErr))
            {
                reason = actErr;
                return false;
            }
        }

        return true;
    }

    private static bool ValidatePayAction(JsonElement action, out string? reason)
    {
        reason = null;

        // Type is optional for permissive parsing; if present and a string, accept either "PayAction" or anything (schema.org tolerant)
        if (action.TryGetProperty("recipient", out var recip) && recip.ValueKind == JsonValueKind.Object)
        {
            if (recip.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var id = idProp.GetString();
                if (!IsEip155Iri(id))
                {
                    reason = "Offer.potentialAction.recipient.@id must be eip155:{chainId}:{0xaddress}";
                    return false;
                }
            }
        }

        if (action.TryGetProperty("instrument", out var inst) && inst.ValueKind == JsonValueKind.Object)
        {
            // Require propertyID == "eip155" and value like "{chain}:{0xaddr}" when instrument is present
            if (inst.TryGetProperty("propertyID", out var pid) && pid.ValueKind == JsonValueKind.String &&
                inst.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
            {
                var pidStr = pid.GetString();
                var valStr = val.GetString();
                if (!string.Equals(pidStr, "eip155", StringComparison.Ordinal) || !IsChainAddr(valStr))
                {
                    reason = "Offer.potentialAction.instrument must be PropertyValue with propertyID='eip155' and value='{chainId}:{0xaddress}'";
                    return false;
                }
            }
        }

        // If neither recipient nor instrument present, still tolerate (future methods), but if any is present and invalid, we've already failed.
        return true;
    }

    private static bool IsEip155Iri(string? s)
    {
        // eip155:{chain}:{0x40-hex}
        if (string.IsNullOrWhiteSpace(s)) return false;
        var span = s.AsSpan();
        if (!span.StartsWith("eip155:")) return false;
        var rest = span.Slice("eip155:".Length);
        int colon = rest.IndexOf(':');
        if (colon <= 0) return false;
        var chainPart = rest.Slice(0, colon).ToString();
        var addrPart = rest.Slice(colon + 1).ToString();
        return long.TryParse(chainPart, NumberStyles.None, CultureInfo.InvariantCulture, out _) && IsHexAddress(addrPart);
    }

    private static bool IsChainAddr(string? s)
    {
        // {chain}:{0xaddress}
        if (string.IsNullOrWhiteSpace(s)) return false;
        int colon = s.IndexOf(':');
        if (colon <= 0) return false;
        var chain = s.Substring(0, colon);
        var addr = s.Substring(colon + 1);
        return long.TryParse(chain, NumberStyles.None, CultureInfo.InvariantCulture, out _) && IsHexAddress(addr);
    }

    private static bool IsHexAddress(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Length != 42) return false;
        for (int i = 2; i < 42; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    private static bool ValidateNameIndexDoc(JsonElement root, out string? reason)
    {
        reason = null;

        if (!TryGetString(root, "head", out var head) || string.IsNullOrWhiteSpace(head))
        {
            reason = "NameIndexDoc.head is required";
            return false;
        }

        // Enforce CIDv0 strictly for control‑plane documents
        if (!CidV0.IsMatch(head))
        {
            reason = "NameIndexDoc.head must be a CIDv0 (base58btc starting with Qm)";
            return false;
        }

        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
        {
            reason = "NameIndexDoc.entries must be an object";
            return false;
        }

        foreach (var kv in entries.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(kv.Name))
            {
                reason = "NameIndexDoc.entries has an empty key";
                return false;
            }

            if (kv.Value.ValueKind != JsonValueKind.String)
            {
                reason = "NameIndexDoc.entries values must be strings (chunk CIDs)";
                return false;
            }

            var v = kv.Value.GetString();
            if (string.IsNullOrWhiteSpace(v) || !CidV0.IsMatch(v!))
            {
                reason = $"NameIndexDoc.entries['{kv.Name}'] is not a valid CIDv0";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateNamespaceChunk(JsonElement root, out string? reason)
    {
        reason = null;

        // prev: nullable string; if present and string, must be CIDv0
        if (root.TryGetProperty("prev", out var prev))
        {
            if (prev.ValueKind is JsonValueKind.Null)
            {
                // ok
            }
            else if (prev.ValueKind is JsonValueKind.String)
            {
                var pv = prev.GetString();
                if (string.IsNullOrWhiteSpace(pv) || !CidV0.IsMatch(pv!))
                {
                    reason = "NamespaceChunk.prev must be a valid CIDv0 or null";
                    return false;
                }
            }
            else
            {
                reason = "NamespaceChunk.prev must be a string or null";
                return false;
            }
        }

        if (!root.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            reason = "NamespaceChunk.links must be an array";
            return false;
        }

        // Enforce spec cap to prevent abuse
        if (links.GetArrayLength() > Circles.Profiles.Sdk.Helpers.ChunkMaxLinks)
        {
            reason = $"NamespaceChunk.links length must be <= {Circles.Profiles.Sdk.Helpers.ChunkMaxLinks}";
            return false;
        }

        // Quick sanity on each link (don’t fully re-validate here)
        foreach (var el in links.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                reason = "NamespaceChunk.links[] must be objects";
                return false;
            }

            if (!el.TryGetProperty("name", out var _) ||
                !el.TryGetProperty("cid", out var _) ||
                !el.TryGetProperty("signerAddress", out var _) ||
                !el.TryGetProperty("signature", out var _))
            {
                reason = "NamespaceChunk.links[] must at least contain name, cid, signerAddress, signature";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateSnippet(JsonElement root, out string? reason)
    {
        reason = null;

        if (!root.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
        {
            reason = "Snippet.content is required";
            return false;
        }

        if (root.TryGetProperty("createdAt", out var _) && !TryGetLong(root, "createdAt", out _))
        {
            reason = "Snippet.createdAt must be a unix timestamp when present";
            return false;
        }

        if (root.TryGetProperty("source", out var sourceProp))
        {
            if (sourceProp.ValueKind != JsonValueKind.Object)
            {
                reason = "Snippet.source must be an object when present";
                return false;
            }
        }

        return true;
    }

    /* ────────────────────────── utils ────────────────────────── */

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind is JsonValueKind.Number && p.TryGetInt64(out var i))
        {
            value = i;
            return true;
        }

        if (p.ValueKind is JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var s))
        {
            value = s;
            return true;
        }

        return false;
    }

    private static bool TryGetNullableLong(JsonElement obj, string name, out long? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var p)) return true; // absent is ok
        if (p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind is JsonValueKind.Number && p.TryGetInt64(out var i))
        {
            value = i;
            return true;
        }

        if (p.ValueKind is JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var s))
        {
            value = s;
            return true;
        }

        return false;
    }

    private static bool TryGetDecimal(JsonElement obj, string name, out decimal value)
    {
        value = 0m;
        if (!obj.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d))
        {
            value = d;
            return true;
        }

        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var s))
        {
            value = s;
            return true;
        }

        return false;
    }

    private static bool IsAbsoluteUri(string? s)
        => s is not null && Uri.TryCreate(s, UriKind.Absolute, out _);

    private static bool IsHttpUrl(string? s)
        => Uri.TryCreate(s, UriKind.Absolute, out var u) &&
           (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static bool IsImageTransport(string? s)
    {
        if (s is null) return false;
        if (IsHttpUrl(s)) return true;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
               (u.Scheme.Equals("ipfs", StringComparison.OrdinalIgnoreCase) ||
                u.Scheme.Equals("cid", StringComparison.OrdinalIgnoreCase) ||
                u.Scheme.Equals("ar", StringComparison.OrdinalIgnoreCase) ||
                u.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase));
    }
}
