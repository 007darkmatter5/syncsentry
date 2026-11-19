using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;

namespace SyncSentry.Services;

public sealed class ThemePreferenceService(IDbContextFactory<ApplicationDbContext> factory, AuthenticationStateProvider auth)
{
    public async Task<bool?> ReadAsync()
    {
        var id = await UserIdAsync();
        if (id is null)
            return null;
        await using var db = await factory.CreateDbContextAsync();
        return await db.UserPreferences.AsNoTracking()
            .Where(item => item.UserId == id)
            .Select(item => (bool?)item.DarkMode)
            .FirstOrDefaultAsync();
    }

    public async Task SetAsync(bool dark)
    {
        var id = await UserIdAsync();
        if (id is null)
            return;
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.UserPreferences.FirstOrDefaultAsync(item => item.UserId == id);
        if (row is null)
        {
            row = new UserPreference { UserId = id, DarkMode = dark };
            db.UserPreferences.Add(row);
        }
        else
        {
            row.DarkMode = dark;
        }

        await db.SaveChangesAsync();
    }

    private async Task<string?> UserIdAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        return state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
