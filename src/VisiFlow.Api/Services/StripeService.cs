using Stripe;
using Stripe.Checkout;

// No namespace - matches VisitPlanGenerator.cs/VisitPlanCityOptimizer.cs/Program.cs's global namespace.

/// <summary>
/// Wraps the Stripe.net SDK for self-serve signup billing. Reads three environment variables:
/// STRIPE_SECRET_KEY (required for anything here to activate), STRIPE_PRICE_ID (the recurring price a
/// new signup subscribes to - created once in the Stripe dashboard), and STRIPE_WEBHOOK_SECRET (to
/// verify POST /api/billing/webhook really came from Stripe). Any of them missing -> IsConfigured false
/// and every method here is a silent no-op/null-returning stub, so POST /api/signup keeps creating
/// companies in "manual" mode (no payment step) exactly as it does today, with zero code change needed
/// once real Stripe keys are supplied.
/// </summary>
public class StripeService
{
    private readonly string? _secretKey;
    private readonly string? _priceId;
    private readonly string? _webhookSecret;

    public StripeService()
    {
        _secretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
        _priceId = Environment.GetEnvironmentVariable("STRIPE_PRICE_ID");
        _webhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");
        if (!string.IsNullOrWhiteSpace(_secretKey)) StripeConfiguration.ApiKey = _secretKey;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secretKey);
    private bool CanCheckout => IsConfigured && !string.IsNullOrWhiteSpace(_priceId);
    public bool CanVerifyWebhooks => IsConfigured && !string.IsNullOrWhiteSpace(_webhookSecret);

    /// <summary>Creates the Stripe Customer for a newly-signed-up company. Returns null (never throws)
    /// if Stripe isn't configured, or if Stripe itself rejects the call - either way, signup still
    /// succeeds without billing wired up.</summary>
    public async Task<string?> CreateCustomerAsync(string companyName, string email)
    {
        if (!IsConfigured) return null;
        try
        {
            var service = new CustomerService();
            var customer = await service.CreateAsync(new CustomerCreateOptions { Name = companyName, Email = email });
            return customer.Id;
        }
        catch (StripeException)
        {
            return null;
        }
    }

    /// <summary>Starting a paid subscription needs a real Price configured in the Stripe dashboard
    /// (STRIPE_PRICE_ID) on top of just having a secret key - without it, signup completes in "manual"
    /// mode (a Stripe Customer may still exist, but no subscription/checkout prompt is shown).</summary>
    public async Task<string?> CreateCheckoutSessionUrlAsync(string stripeCustomerId, string successUrl, string cancelUrl)
    {
        if (!CanCheckout) return null;
        try
        {
            var service = new SessionService();
            var session = await service.CreateAsync(new SessionCreateOptions
            {
                Customer = stripeCustomerId,
                Mode = "subscription",
                LineItems = new List<SessionLineItemOptions> { new() { Price = _priceId, Quantity = 1 } },
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl
            });
            return session.Url;
        }
        catch (StripeException)
        {
            return null;
        }
    }

    /// <summary>Stripe's own hosted billing UI (payment method, invoice history, cancel) - deliberately
    /// not a hand-built billing screen, per the commercialization plan's "less work, less security risk"
    /// reasoning for anything touching card data.</summary>
    public async Task<string?> CreatePortalSessionUrlAsync(string stripeCustomerId, string returnUrl)
    {
        if (!IsConfigured) return null;
        try
        {
            var service = new Stripe.BillingPortal.SessionService();
            var session = await service.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = stripeCustomerId,
                ReturnUrl = returnUrl
            });
            return session.Url;
        }
        catch (StripeException)
        {
            return null;
        }
    }

    /// <summary>Verifies the Stripe-Signature header against the raw request body using
    /// STRIPE_WEBHOOK_SECRET. Throws (caller returns 400) on a bad/missing signature - unlike every
    /// other method here, a webhook call while Stripe ISN'T configured/verifiable should be rejected
    /// outright, not silently accepted, since accepting an unverifiable "cancel this company's
    /// subscription" request would be a real security hole.</summary>
    public Event ConstructEvent(string json, string stripeSignatureHeader)
    {
        if (!CanVerifyWebhooks) throw new InvalidOperationException("Stripe webhook is not configured (STRIPE_SECRET_KEY/STRIPE_WEBHOOK_SECRET missing).");
        return EventUtility.ConstructEvent(json, stripeSignatureHeader, _webhookSecret);
    }
}
