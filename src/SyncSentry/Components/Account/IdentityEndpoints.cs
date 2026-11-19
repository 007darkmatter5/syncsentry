using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using SyncSentry.Data;
using SyncSentry.Services;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityEndpoints
{
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/Account");
        account.MapPost("/SignIn", async (HttpContext context, SignInManager<ApplicationUser> signInManager) =>
        {
            if (context.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var remember = form["rememberMe"].ToString() is "true" or "on";
            var result = await signInManager.PasswordSignInAsync(email, password, remember, lockoutOnFailure: true);
            if (result.Succeeded)
                return Results.LocalRedirect(SafeReturn(form["returnUrl"].ToString()));
            if (result.IsLockedOut)
                return Results.LocalRedirect("~/Account/Lockout");
            return Results.LocalRedirect("~/Account/Login?error=1");
        }).AllowAnonymous().WithMetadata(new RequireAntiforgeryTokenAttribute());

        account.MapPost("/SignUp", async (HttpContext context, AdminAccountService accounts, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signInManager) =>
        {
            if (context.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
                return Results.BadRequest();
            if (await accounts.HasAccountAsync(context.RequestAborted))
                return Results.LocalRedirect("~/Account/Register");

            var form = await context.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
                return Results.LocalRedirect("~/Account/Register?error=" + Uri.EscapeDataString("The passwords don't match."));

            var created = await accounts.CreateAdminAsync(email, password, context.RequestAborted);
            if (!created.Succeeded)
            {
                var message = string.Join(" ", created.Errors.Select(error => error.Description));
                return Results.LocalRedirect("~/Account/Register?error=" + Uri.EscapeDataString(message));
            }

            var user = await users.FindByEmailAsync(email.Trim());
            if (user is not null)
                await signInManager.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect(SafeReturn(form["returnUrl"].ToString()));
        }).AllowAnonymous().WithMetadata(new RequireAntiforgeryTokenAttribute());

        account.MapPost("/ChangePassword", async (HttpContext context, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signInManager) =>
        {
            if (context.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
                return Results.BadRequest();
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Redirect("/Account/Login");

            var form = await context.Request.ReadFormAsync();
            var user = await users.GetUserAsync(context.User);
            if (user is null)
                return Results.Redirect("/Account/Login");
            if (!string.Equals(form["next"].ToString(), form["confirm"].ToString(), StringComparison.Ordinal))
                return Results.LocalRedirect("~/account/password?error=" + Uri.EscapeDataString("The passwords don't match."));

            var changed = await users.ChangePasswordAsync(user, form["current"].ToString(), form["next"].ToString());
            if (!changed.Succeeded)
            {
                var message = string.Join(" ", changed.Errors.Select(error => error.Description));
                return Results.LocalRedirect("~/account/password?error=" + Uri.EscapeDataString(message));
            }

            await signInManager.RefreshSignInAsync(user);
            return Results.LocalRedirect("~/account/password?ok=1");
        }).RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute());

        account.MapPost("/Logout", async (HttpContext context, SignInManager<ApplicationUser> signInManager) =>
        {
            if (context.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
                return Results.BadRequest();

            await signInManager.SignOutAsync();
            return Results.LocalRedirect("~/");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());
        return account;
    }

    private static string SafeReturn(string? uri)
    {
        if (string.IsNullOrEmpty(uri)
            || !Uri.IsWellFormedUriString(uri, UriKind.Relative)
            || uri.StartsWith("//", StringComparison.Ordinal)
            || uri.Contains('\\')
            || uri.Contains("://", StringComparison.Ordinal))
            return "~/";
        return "~/" + uri.TrimStart('/');
    }
}
