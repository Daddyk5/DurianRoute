using System.Security.Claims;
using System.Text.Json;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace DurianRoute.Client.Services;

/// <summary>
/// Holds the signed-in user's JWT (persisted in localStorage) and exposes it as the Blazor
/// authentication state. Singleton, so the HTTP handler and SignalR client share one session.
/// </summary>
public class SessionStore(IJSRuntime js) : AuthenticationStateProvider
{
    private const string StorageKey = "durianroute.session";
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private LoginResponse? _session;
    private bool _loaded;

    public async Task<string?> GetTokenAsync()
    {
        await EnsureLoadedAsync();
        return IsValid(_session) ? _session!.Token : null;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await EnsureLoadedAsync();
        if (!IsValid(_session)) return Anonymous;

        var claims = new List<Claim> { new(ClaimTypes.Name, _session!.UserName) };
        claims.AddRange(_session.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, "jwt", ClaimTypes.Name, ClaimTypes.Role);
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task SignInAsync(LoginResponse session)
    {
        _session = session;
        _loaded = true;
        await js.InvokeVoidAsync("durianStorage.set", StorageKey, JsonSerializer.Serialize(session));
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync()
    {
        _session = null;
        _loaded = true;
        await js.InvokeVoidAsync("durianStorage.remove", StorageKey);
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var json = await js.InvokeAsync<string?>("durianStorage.get", StorageKey);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            _session = JsonSerializer.Deserialize<LoginResponse>(json);
        }
        catch (JsonException)
        {
            _session = null;
        }
    }

    private static bool IsValid(LoginResponse? s) => s is not null && s.ExpiresUtc > DateTime.UtcNow.AddMinutes(1);
}
