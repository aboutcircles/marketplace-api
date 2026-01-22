using System.Threading;

namespace Circles.Market.Api.Cart.Validation;

public sealed class CustomerNameRule : ICartRule
{
    public string Id => "rule:customer-names";

    public void Evaluate(ValidationContext context, CancellationToken ct = default)
    {
        // 100% compatible to all previous offers that don't have these fields:
        // We only require customer names if they are explicitly requested by the offer's requiredSlots.
        
        bool isNameRequired = context.Basket.Items
            .Any(i => i.OfferSnapshot?.RequiredSlots?.Contains("customer", StringComparer.OrdinalIgnoreCase) == true);

        if (!isNameRequired)
        {
            return;
        }

        var customer = context.Basket.Customer;

        // Validate Given Name
        var givenNameReq = new ValidationRequirement
        {
            Id = "req:customer:givenName",
            RuleId = Id,
            Reason = "First name (givenName) is required",
            Slot = "customer.givenName",
            Path = "/customer/givenName",
            ExpectedTypes = new[] { "https://schema.org/Text" },
            Scope = "basket",
            Status = string.IsNullOrWhiteSpace(customer?.GivenName) ? "missing" : "ok"
        };
        context.AddRequirement(givenNameReq);

        // Validate Family Name
        var familyNameReq = new ValidationRequirement
        {
            Id = "req:customer:familyName",
            RuleId = Id,
            Reason = "Last name (familyName) is required",
            Slot = "customer.familyName",
            Path = "/customer/familyName",
            ExpectedTypes = new[] { "https://schema.org/Text" },
            Scope = "basket",
            Status = string.IsNullOrWhiteSpace(customer?.FamilyName) ? "missing" : "ok"
        };
        context.AddRequirement(familyNameReq);
    }
}
