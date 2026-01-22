namespace Circles.Market.Api.Cart.Validation;

public sealed class ShippingAddressRule : ICartRule
{
    public string Id => "rule:shipping-address";

    public void Evaluate(ValidationContext context, CancellationToken ct = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (!context.Facts.AnyPhysicalItems)
        {
            return;
        }

        var requirement = new ValidationRequirement
        {
            Id = "req:shipping-address",
            RuleId = Id,
            Reason = "Physical item present (non-download fulfilment)",
            Slot = "shippingAddress",
            Path = "/shippingAddress",
            ExpectedTypes = new[] { "https://schema.org/PostalAddress" },
            Cardinality = new Cardinality { Min = 1, Max = 1 },
            Scope = "basket"
        };

        CartRuleHelpers.EvaluateAddressSlot(requirement, context.Basket.ShippingAddress);
        context.AddRequirement(requirement);

        AddShippingFieldRequirements(context.Basket.ShippingAddress, context);
    }

    private static void AddShippingFieldRequirements(PostalAddress? address, ValidationContext context)
    {
        AddStringFieldRequirement(
            id: "req:shipping-street",
            ruleId: "rule:shipping-street",
            reason: "Street address is required for physical delivery",
            slot: "shippingAddress.streetAddress",
            path: "/shippingAddress/streetAddress",
            value: address?.StreetAddress,
            context: context
        );

        AddStringFieldRequirement(
            id: "req:shipping-locality",
            ruleId: "rule:shipping-locality",
            reason: "City / locality is required for physical delivery",
            slot: "shippingAddress.addressLocality",
            path: "/shippingAddress/addressLocality",
            value: address?.AddressLocality,
            context: context
        );

        AddStringFieldRequirement(
            id: "req:shipping-postal",
            ruleId: "rule:shipping-postal",
            reason: "Postal code is required for physical delivery",
            slot: "shippingAddress.postalCode",
            path: "/shippingAddress/postalCode",
            value: address?.PostalCode,
            context: context
        );

        AddStringFieldRequirement(
            id: "req:shipping-country",
            ruleId: "rule:shipping-country",
            reason: "Country is required for physical delivery",
            slot: "shippingAddress.addressCountry",
            path: "/shippingAddress/addressCountry",
            value: address?.AddressCountry,
            context: context
        );
    }

    private static void AddStringFieldRequirement(
        string id,
        string ruleId,
        string reason,
        string slot,
        string path,
        string? value,
        ValidationContext context)
    {
        bool isMissing = string.IsNullOrWhiteSpace(value);
        string status = isMissing ? "missing" : "ok";

        var requirement = new ValidationRequirement
        {
            Id = id,
            RuleId = ruleId,
            Reason = reason,
            Slot = slot,
            Path = path,
            ExpectedTypes = new[] { "https://schema.org/Text" },
            Cardinality = new Cardinality { Min = 1, Max = 1 },
            Status = status,
            Scope = "basket"
        };

        context.AddRequirement(requirement);
    }
}