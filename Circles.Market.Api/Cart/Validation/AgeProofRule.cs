namespace Circles.Market.Api.Cart.Validation;

public sealed class AgeProofRule : ICartRule
{
    public string Id => "rule:age-proof";

    public void Evaluate(ValidationContext context, CancellationToken ct = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (!context.Facts.HasAgeRestrictedItems)
        {
            return;
        }

        var requirement = new ValidationRequirement
        {
            Id = "req:age-proof",
            RuleId = Id,
            Reason = "Restricted item present",
            Slot = "ageProof",
            Path = "/ageProof",
            ExpectedTypes = new[] { "https://schema.org/Person" },
            Cardinality = new Cardinality { Min = 1, Max = 1 },
            Scope = "basket"
        };

        CartRuleHelpers.EvaluatePersonSlot(requirement, context.Basket.AgeProof);
        context.AddRequirement(requirement);
    }
}
