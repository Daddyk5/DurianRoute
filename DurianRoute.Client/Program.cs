using DurianRoute.Client;
using DurianRoute.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;

builder.Services.AddMudServices();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<SessionStore>());
builder.Services.AddSingleton<TelemetryClient>();

builder.Services.AddScoped(sp =>
{
    var handler = new AuthHeaderHandler(sp.GetRequiredService<SessionStore>()) { InnerHandler = new HttpClientHandler() };
    return new HttpClient(handler) { BaseAddress = new Uri(apiBase.TrimEnd('/') + "/") };
});
builder.Services.AddScoped<ApiClient>();

await builder.Build().RunAsync();
