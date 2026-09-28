using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace DotNetSigningServer.Conventions;

/// <summary>
/// Removes the parts of this server that only make sense for the hosted service.
///
/// Done by taking the controllers out of the routing table rather than by
/// answering 404 from a filter: a route that does not exist cannot be reached by
/// a request that gets the casing right, cannot appear in generated
/// documentation, and cannot be re-enabled by a misplaced attribute later.
/// </summary>
public class PrivateServerConvention : IApplicationModelConvention
{
    /// <summary>
    /// Exists for the hosted service only. Billing has nothing to charge, the
    /// marketing pages advertise a product the reader already runs, support
    /// tickets belong to whoever sold the installation, and the backoffice
    /// integration is forced Off.
    /// </summary>
    private static readonly HashSet<string> RemovedControllers = new(StringComparer.Ordinal)
    {
        "Billing",
        "StripeWebhook",
        "BackofficeWebhook",
        "Home",
        "Seo",
        "Legal",
        "Support",
        "Requests",
    };

    /// <summary>
    /// Signing up is what makes this a service rather than an appliance. The one
    /// account is created at installation; the rest of AccountController — signing
    /// in, signing out, changing a password — is what the administrator needs to
    /// manage API keys. The re-consent page belongs to the backoffice integration,
    /// which is forced Off here (its gate is not registered either).
    /// </summary>
    private static readonly HashSet<string> RemovedActions = new(StringComparer.Ordinal)
    {
        "SignUp",
        "ResendVerification",
        "Consent",
    };

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers.ToList())
        {
            if (RemovedControllers.Contains(controller.ControllerName))
            {
                application.Controllers.Remove(controller);
                continue;
            }

            if (controller.ControllerName == "Account")
            {
                foreach (var action in controller.Actions.ToList())
                {
                    if (RemovedActions.Contains(action.ActionName))
                    {
                        controller.Actions.Remove(action);
                    }
                }
            }
        }
    }

    /// <summary>Names removed wholesale — exposed so a test can assert on them.</summary>
    internal static IReadOnlySet<string> RemovedControllerNames => RemovedControllers;

    internal static IReadOnlySet<string> RemovedActionNames => RemovedActions;
}
