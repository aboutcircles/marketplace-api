using System;
using System.Collections.Generic;
using System.Linq;

namespace Circles.Market.Api.Cart;

public sealed class ValidationContext
{
    public ValidationContext(Basket basket)
    {
        Basket = basket ?? throw new ArgumentNullException(nameof(basket));
        Facts = BasketFacts.From(basket);
    }

    public Basket Basket { get; }

    public BasketFacts Facts { get; }

    public List<ValidationRequirement> Requirements { get; } = new();

    public List<RuleTrace> RuleTrace { get; } = new();

    public void AddRequirement(ValidationRequirement requirement)
    {
        if (requirement is null) throw new ArgumentNullException(nameof(requirement));

        Requirements.Add(requirement);

        RuleTrace.Add(new RuleTrace
        {
            Id = requirement.RuleId,
            Evaluated = true,
            Result = requirement.Status
        });
    }

    public ValidationResult BuildResult()
    {
        var missing = Requirements
            .Where(r => r.Status == "missing")
            .Select(r => new MissingSlot
            {
                Slot = r.Slot,
                Path = r.Path,
                ExpectedTypes = r.ExpectedTypes
            })
            .ToList();

        bool allOk = Requirements.All(r => r.Status == "ok");

        return new ValidationResult
        {
            BasketId = Basket.BasketId,
            Requirements = Requirements,
            RuleTrace = RuleTrace,
            Missing = missing,
            Valid = allOk
        };
    }
}

public sealed class BasketFacts
{
    public bool HasItems { get; private init; }

    public bool AllLinesClearlyDigitalOnly { get; private init; }

    public bool AnyPhysicalItems => HasItems && !AllLinesClearlyDigitalOnly;

    public bool InvoiceLikely { get; private init; }

    public bool HasAgeRestrictedItems { get; private init; }

    public static BasketFacts From(Basket basket)
    {
        bool hasItems = basket.Items.Any();
        bool allDigitalOnly = hasItems && basket.Items.All(CartRuleHelpers.IsDigitalOnlyLine);

        bool hasBillingAddress = basket.BillingAddress is not null;
        bool hasContactPoint = basket.ContactPoint is not null;
        bool invoiceLikely = hasBillingAddress || hasContactPoint;

        bool hasAgeRestrictedItems = basket.Items.Any(i => CartRuleHelpers.IsAgeRestrictedSku(i.OrderedItem.Sku));

        return new BasketFacts
        {
            HasItems = hasItems,
            AllLinesClearlyDigitalOnly = allDigitalOnly,
            InvoiceLikely = invoiceLikely,
            HasAgeRestrictedItems = hasAgeRestrictedItems
        };
    }
}

internal static class CartRuleHelpers
{
    private static readonly HashSet<string> DirectDownloadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://purl.org/goodrelations/v1#DeliveryModeDirectDownload",
        "https://schema.org/DeliveryModeDirectDownload",
        "http://purl.org/goodrelations/v1#DeliveryModePickUp",
        "https://purl.org/goodrelations/v1#DeliveryModePickUp",
        "https://schema.org/OnSitePickup",
        "http://schema.org/OnSitePickup"
    };

    private static readonly string[] AgeRestrictedTokens =
    {
        "alcohol",
        "tobacco"
    };

    public static bool IsDigitalOnlyLine(OrderItemPreview line)
    {
        var offer = line.OfferSnapshot;

        bool hasMethods = offer?.AvailableDeliveryMethod is { Count: > 0 };
        if (!hasMethods)
        {
            return false;
        }

        foreach (var method in offer!.AvailableDeliveryMethod!)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                return false;
            }

            string trimmed = method.Trim();
            bool isDirectDownload = DirectDownloadMethods.Contains(trimmed);
            if (!isDirectDownload)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAgeRestrictedSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return false;

        string value = sku!;
        foreach (var token in AgeRestrictedTokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void EvaluateAddressSlot(ValidationRequirement requirement, PostalAddress? address)
    {
        if (address is null)
        {
            requirement.Status = "missing";
            return;
        }

        bool typeMatches = string.Equals(address.Type, "PostalAddress", StringComparison.Ordinal);
        if (!typeMatches)
        {
            requirement.Status = "typeMismatch";
            requirement.FoundAt = requirement.Path;
            requirement.FoundType = address.Type;
            return;
        }

        bool hasStreet = !string.IsNullOrWhiteSpace(address.StreetAddress);
        bool hasLocality = !string.IsNullOrWhiteSpace(address.AddressLocality);
        bool hasPostalCode = !string.IsNullOrWhiteSpace(address.PostalCode);
        bool hasCountry = !string.IsNullOrWhiteSpace(address.AddressCountry);

        bool allPresent = hasStreet && hasLocality && hasPostalCode && hasCountry;
        if (!allPresent)
        {
            requirement.Status = "invalidShape";
            requirement.FoundAt = requirement.Path;
            requirement.FoundType = "https://schema.org/PostalAddress";
            return;
        }

        requirement.Status = "ok";
        requirement.FoundAt = requirement.Path;
        requirement.FoundType = "https://schema.org/PostalAddress";
    }

    public static void EvaluatePersonSlot(ValidationRequirement requirement, PersonMinimal? person)
    {
        if (person is null)
        {
            requirement.Status = "missing";
            return;
        }

        bool typeMatches = string.Equals(person.Type, "Person", StringComparison.Ordinal);
        if (!typeMatches)
        {
            requirement.Status = "typeMismatch";
            requirement.FoundAt = requirement.Path;
            requirement.FoundType = person.Type;
            return;
        }

        bool hasBirthDate = !string.IsNullOrWhiteSpace(person.BirthDate);
        bool hasGivenName = !string.IsNullOrWhiteSpace(person.GivenName);
        bool hasFamilyName = !string.IsNullOrWhiteSpace(person.FamilyName);

        if (!hasBirthDate && !hasGivenName && !hasFamilyName)
        {
            requirement.Status = "invalidShape";
            requirement.FoundAt = requirement.Path;
            requirement.FoundType = "https://schema.org/Person";
            return;
        }

        requirement.Status = "ok";
        requirement.FoundAt = requirement.Path;
        requirement.FoundType = "https://schema.org/Person";
    }
}
