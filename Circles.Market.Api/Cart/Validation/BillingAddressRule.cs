namespace Circles.Market.Api.Cart.Validation;

public sealed class BillingAddressRule : ICartRule
{
    public string Id => "rule:invoice";

    public void Evaluate(ValidationContext context, CancellationToken ct = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (!context.Facts.InvoiceLikely)
        {
            return;
        }

        var requirement = new ValidationRequirement
        {
            Id = "req:billing-address",
            RuleId = Id,
            Reason = "Invoice requires billing address",
            Slot = "billingAddress",
            Path = "/billingAddress",
            ExpectedTypes = new[] { "https://schema.org/PostalAddress" },
            Cardinality = new Cardinality { Min = 1, Max = 1 },
            Scope = "basket"
        };

        CartRuleHelpers.EvaluateAddressSlot(requirement, context.Basket.BillingAddress);
        context.AddRequirement(requirement);
    }
}
