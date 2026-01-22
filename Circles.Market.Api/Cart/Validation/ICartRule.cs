namespace Circles.Market.Api.Cart.Validation;

public interface ICartRule
{
    string Id { get; }

    void Evaluate(ValidationContext context, CancellationToken ct = default);
}
