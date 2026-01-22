namespace Circles.Market.Api.Cart.Validation;

public interface ICartValidator
{
    ValidationResult Validate(Basket basket, CancellationToken ct = default);
}

public class CartValidator : ICartValidator
{
    private readonly IReadOnlyList<ICartRule> _rules;

    public CartValidator(IEnumerable<ICartRule> rules)
    {
        _rules = (rules ?? throw new ArgumentNullException(nameof(rules))).ToArray();
    }

    public ValidationResult Validate(Basket basket, CancellationToken ct = default)
    {
        if (basket is null) throw new ArgumentNullException(nameof(basket));

        var context = new ValidationContext(basket);
        foreach (var rule in _rules)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            rule.Evaluate(context, ct);
        }

        return context.BuildResult();
    }
}
