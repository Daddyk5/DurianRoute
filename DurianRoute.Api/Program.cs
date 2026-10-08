using System.Threading.RateLimiting;
using DurianRoute.Api.Auth;
using DurianRoute.Api.Data;
using DurianRoute.Api.Forecasting;
using DurianRoute.Api.Hubs;
using DurianRoute.Api.Scheduling;
using DurianRoute.Api.Simulation;
using DurianRoute.Api.Traffic;
using DurianRoute.Api.Weather;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Database ---------------------------------------------------------------------------------
builder.Services.AddDbContext<DurianDbContext>(options =>
{
    if (string.Equals(config["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase))
        options.UseSqlServer(config.GetConnectionString("SqlServer"));
    else
        options.UseSqlite(config.GetConnectionString("Sqlite"));
});

// ---- Authentication (JWT bearer, also accepted on the SignalR query string) -------------------
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters. Set it in appsettings or user secrets.");

builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.SigningKey(),
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // WebSockets cannot send headers, so the SignalR client passes the token in the query.
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(HubPaths.Telemetry))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// ---- CORS for the Blazor WebAssembly client ---------------------------------------------------
var clientOrigins = config.GetSection("ClientOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(clientOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---- Application services ---------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

builder.Services.Configure<LaneSchedulerOptions>(config.GetSection("LaneScheduler"));
builder.Services.AddSingleton<LiveTrafficState>();
builder.Services.AddSingleton<FleetState>();
builder.Services.AddSingleton<TrafficModelService>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<RouteShapeService>();
builder.Services.AddHttpClient("routing", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("DurianRoute/1.0"));
builder.Services.AddHttpClient("weather", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("DurianRoute/1.0"));
builder.Services.AddScoped<LaneControlService>();

builder.Services.AddHostedService<TrafficSimulator>();
builder.Services.AddHostedService<BusSimulator>();
builder.Services.AddHostedService<ForecastWorker>();
builder.Services.AddHostedService<LaneWorker>();
builder.Services.AddHostedService<WeatherWorker>();

var app = builder.Build();

// ---- Database bootstrap + in-memory simulation state ------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DurianDbContext>();
    Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "App_Data"));
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrader.UpgradeAsync(db, app.Logger);
    await SeedData.EnsureSeededAsync(db, config);

    app.Services.GetRequiredService<LiveTrafficState>().Initialize(await db.ChokePoints.ToListAsync());
    var routes = await db.Routes.AsNoTracking().Include(r => r.Stops).Include(r => r.Buses).AsSplitQuery().ToListAsync();
    var shapes = app.Services.GetRequiredService<RouteShapeService>();
    await shapes.LoadAsync(routes, CancellationToken.None);   // snap routes to real roads (cached after first run)
    app.Services.GetRequiredService<FleetState>().Initialize(routes, shapes.Shapes);
}

// ---- HTTP pipeline ----------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
else
    app.UseHsts();

app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<TelemetryHub>(HubPaths.Telemetry);

app.Run();
