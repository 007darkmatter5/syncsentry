using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Security;

namespace SyncSentry.Alerts;

public sealed record NtfyTarget(string ServerUrl, string Topic, string? Token);

public sealed record NtfySendResult(bool Ok, string Detail);

public interface INtfyClient
{
    Task<NtfySendResult> SendAsync(NtfyTarget target, string title, string body, CancellationToken cancellationToken);
}

public static class NtfyRequest
{
    public static bool TryCreate(NtfyTarget target, string title, string body, out HttpRequestMessage? message, out string? error)
    {
        message = null;
        error = null;
        if (!Rules.NtfyServer(target.ServerUrl))
        {
            error = "Ntfy server URL is not a plain http(s) address.";
            return false;
        }

        if (!Rules.NtfyTopic(target.Topic))
        {
            error = "Ntfy topic must be letters, numbers, _ or -.";
            return false;
        }

        var baseUri = new Uri(target.ServerUrl.Trim().TrimEnd('/') + "/");
        var uri = new Uri(baseUri, target.Topic.Trim());
        var content = body.Length > 3500 ? body[..3500] : body;
        message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain")
        };
        message.Headers.TryAddWithoutValidation("Title", Ascii(title));
        message.Headers.TryAddWithoutValidation("Priority", "4");
        if (!string.IsNullOrEmpty(target.Token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        return true;
    }

    public static string Ascii(string value)
    {
        var chars = value.Select(character => character <= 127 ? character : '?').ToArray();
        var text = new string(chars).Trim();
        if (text.Length > 180)
            text = text[..180];
        return text.Length == 0 ? "SyncSentry" : text;
    }
}

public sealed class NtfyClient(IHttpClientFactory factory) : INtfyClient
{
    public async Task<NtfySendResult> SendAsync(NtfyTarget target, string title, string body, CancellationToken cancellationToken)
    {
        if (!NtfyRequest.TryCreate(target, title, body, out var message, out var error) || message is null)
            return new NtfySendResult(false, error ?? "Could not build the notification.");

        try
        {
            var client = factory.CreateClient("ntfy");
            using (message)
            {
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return new NtfySendResult(true, "Sent");
                return new NtfySendResult(false, "Ntfy returned HTTP " + (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new NtfySendResult(false, "Ntfy could not be reached (" + ex.GetType().Name + ").");
        }
    }
}

public sealed class AlertDispatcher(INtfyClient ntfy, TimeProvider clock)
{
    public const int RetryMinutes = 10;

    public async Task<bool> DispatchAsync(
        ApplicationDbContext db,
        NtfyTarget? target,
        AlertMessage message,
        string? blockedReason,
        CancellationToken cancellationToken)
    {
        var last = await db.AlertEvents
            .Where(item => item.Key == message.Key)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (last is { Delivered: true })
            return false;
        if (last is not null && now - last.CreatedUtc < TimeSpan.FromMinutes(RetryMinutes))
            return false;

        var delivered = false;
        var detail = blockedReason ?? "Ntfy is not configured.";
        if (blockedReason is null && target is not null && !string.IsNullOrWhiteSpace(target.ServerUrl))
        {
            var result = await ntfy.SendAsync(target, message.Title, message.Body, cancellationToken).ConfigureAwait(false);
            delivered = result.Ok;
            detail = SecretRedactor.ForDisplay(result.Detail, 200);
        }

        db.AlertEvents.Add(new AlertEvent
        {
            Key = message.Key,
            Kind = message.Kind,
            Title = message.Title,
            Message = message.Body,
            Delivered = delivered,
            Detail = detail,
            CreatedUtc = now
        });
        return delivered;
    }
}
