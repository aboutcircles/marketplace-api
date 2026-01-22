namespace Circles.Market.Api;

internal sealed class PayloadTooLargeException(string? message = null)
    : Exception(message ?? "Object exceeds configured maximum size");