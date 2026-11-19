using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using SyncSentry.Data;

namespace SyncSentry.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public void RedirectTo(string? uri)
    {
        uri ??= "";
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative) || uri.StartsWith("//", StringComparison.Ordinal) || uri.Contains('\\'))
            uri = "";
        navigationManager.NavigateTo(uri, forceLoad: true);
    }

    public void RedirectToInvalidUser(UserManager<ApplicationUser> userManager, HttpContext context)
        => RedirectTo("Account/Login");
}
