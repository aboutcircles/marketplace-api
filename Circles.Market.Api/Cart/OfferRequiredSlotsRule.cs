using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Circles.Market.Api.Cart.Validation;

namespace Circles.Market.Api.Cart;

public sealed class OfferRequiredSlotsRule : ICartRule
{
    public string Id => "rule:offer-required-slots";

    // Map opaque slot keys from offers to basket paths + semantics.
    private static readonly IReadOnlyDictionary<string, SlotDescriptor> SlotMap =
        new Dictionary<string, SlotDescriptor>(StringComparer.Ordinal)
        {
            ["contactPoint.email"] = new SlotDescriptor(
                "contactPoint.email",
                "/contactPoint/email",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["contactPoint.telephone"] = new SlotDescriptor(
                "contactPoint.telephone",
                "/contactPoint/telephone",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            // Age proof (coarse and fine-grained)
            ["ageProof"] = new SlotDescriptor(
                "ageProof",
                "/ageProof",
                new[] { "https://schema.org/Person" },
                "basket"
            ),
            ["ageProof.birthDate"] = new SlotDescriptor(
                "ageProof.birthDate",
                "/ageProof/birthDate",
                new[] { "https://schema.org/Date" },
                "basket"
            ),
            // Shipping address (coarse and fine-grained)
            ["shippingAddress"] = new SlotDescriptor(
                "shippingAddress",
                "/shippingAddress",
                new[] { "https://schema.org/PostalAddress" },
                "basket"
            ),
            ["shippingAddress.streetAddress"] = new SlotDescriptor(
                "shippingAddress.streetAddress",
                "/shippingAddress/streetAddress",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["shippingAddress.addressLocality"] = new SlotDescriptor(
                "shippingAddress.addressLocality",
                "/shippingAddress/addressLocality",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["shippingAddress.postalCode"] = new SlotDescriptor(
                "shippingAddress.postalCode",
                "/shippingAddress/postalCode",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["shippingAddress.addressCountry"] = new SlotDescriptor(
                "shippingAddress.addressCountry",
                "/shippingAddress/addressCountry",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            // Billing address (coarse and fine-grained)
            ["billingAddress"] = new SlotDescriptor(
                "billingAddress",
                "/billingAddress",
                new[] { "https://schema.org/PostalAddress" },
                "basket"
            ),
            ["billingAddress.streetAddress"] = new SlotDescriptor(
                "billingAddress.streetAddress",
                "/billingAddress/streetAddress",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["billingAddress.addressLocality"] = new SlotDescriptor(
                "billingAddress.addressLocality",
                "/billingAddress/addressLocality",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["billingAddress.postalCode"] = new SlotDescriptor(
                "billingAddress.postalCode",
                "/billingAddress/postalCode",
                new[] { "https://schema.org/Text" },
                "basket"
            ),
            ["billingAddress.addressCountry"] = new SlotDescriptor(
                "billingAddress.addressCountry",
                "/billingAddress/addressCountry",
                new[] { "https://schema.org/Text" },
                "basket"
            )
        };

    public void Evaluate(ValidationContext context, CancellationToken ct = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        // Collect union of required slots across all offers in the basket.
        var required = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in context.Basket.Items)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

            var snapshot = line.OfferSnapshot;
            if (snapshot?.RequiredSlots is not { Count: > 0 })
            {
                continue;
            }

            foreach (var raw in snapshot.RequiredSlots)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string key = raw.Trim();
                required.Add(key);
            }
        }

        if (required.Count == 0)
        {
            return;
        }

        foreach (var slotKey in required)
        {
            if (!SlotMap.TryGetValue(slotKey, out var descriptor))
            {
                // Unknown keys are ignored for now
                continue;
            }

            var requirement = new ValidationRequirement
            {
                Id = $"req:slot:{slotKey}",
                RuleId = Id,
                Reason = $"Offer requires {slotKey}",
                Slot = descriptor.Slot,
                Path = descriptor.Path,
                ExpectedTypes = descriptor.ExpectedTypes,
                Cardinality = new Cardinality { Min = 1, Max = 1 },
                Status = "missing",
                Scope = descriptor.Scope,
                Blocking = true
            };

            switch (slotKey)
            {
                case "contactPoint.email":
                {
                    string? value = context.Basket.ContactPoint?.Email;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "contactPoint.telephone":
                {
                    string? value = context.Basket.ContactPoint?.Telephone;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "ageProof":
                {
                    var person = context.Basket.AgeProof;
                    requirement.Status = person is null ? "missing" : "ok";
                    break;
                }
                case "ageProof.birthDate":
                {
                    string? value = context.Basket.AgeProof?.BirthDate;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "shippingAddress":
                {
                    var addr = context.Basket.ShippingAddress;
                    requirement.Status = addr is null ? "missing" : "ok";
                    break;
                }
                case "shippingAddress.streetAddress":
                {
                    string? value = context.Basket.ShippingAddress?.StreetAddress;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "shippingAddress.addressLocality":
                {
                    string? value = context.Basket.ShippingAddress?.AddressLocality;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "shippingAddress.postalCode":
                {
                    string? value = context.Basket.ShippingAddress?.PostalCode;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "shippingAddress.addressCountry":
                {
                    string? value = context.Basket.ShippingAddress?.AddressCountry;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "billingAddress":
                {
                    var addr = context.Basket.BillingAddress;
                    requirement.Status = addr is null ? "missing" : "ok";
                    break;
                }
                case "billingAddress.streetAddress":
                {
                    string? value = context.Basket.BillingAddress?.StreetAddress;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "billingAddress.addressLocality":
                {
                    string? value = context.Basket.BillingAddress?.AddressLocality;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "billingAddress.postalCode":
                {
                    string? value = context.Basket.BillingAddress?.PostalCode;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                case "billingAddress.addressCountry":
                {
                    string? value = context.Basket.BillingAddress?.AddressCountry;
                    requirement.Status = string.IsNullOrWhiteSpace(value) ? "missing" : "ok";
                    break;
                }
                default:
                {
                    requirement.Status = "missing";
                    break;
                }
            }

            context.AddRequirement(requirement);
        }
    }

    private readonly record struct SlotDescriptor(
        string Slot,
        string Path,
        string[] ExpectedTypes,
        string Scope
    );
}
