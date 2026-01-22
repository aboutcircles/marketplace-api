namespace Circles.Market.Api.Cart.Validation;

public sealed class ItemsNonEmptyRule : ICartRule
{
    public string Id => "rule:items-nonempty";

    public void Evaluate(ValidationContext context, CancellationToken ct = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (context.Facts.HasItems)
        {
            return;
        }

        var requirement = new ValidationRequirement
        {
            Id = "req:items-nonempty",
            RuleId = Id,
            Reason = "Basket must contain at least one item",
            Slot = "items",
            Path = "/items",
            ExpectedTypes = Array.Empty<string>(),
            Cardinality = new Cardinality { Min = 1, Max = 500 },
            Status = "missing",
            Scope = "basket"
        };

        context.AddRequirement(requirement);
    }
}
