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
    /// marketing pages advertise a product the reader already runs, and support
    /// tickets belong to whoever sold the installation.
    /// </summary>
    private static readonly HashSet<string> RemovedControllers = new(StringComparer.Ordinal)
    {
        "Billing",
        "StripeWebhook",
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
    /// manage API keys.
    /// </summary>
    private static readonly HashSet<string> RemovedActions = new(StringComparer.Ordinal)
    {
        "SignUp",
        "ResendVerification",
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
