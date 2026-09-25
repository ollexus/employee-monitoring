using EmployeeMonitoring.Protocol;
using EmployeeMonitoring.Server.Monitoring;

// Корень содержимого задаётся явно: сервер одинаково корректно запускается
// из Visual Studio, из планировщика задач и из службы, независимо от текущего каталога.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot"
});

var agentOptions = new AgentOptions();
builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
var dashboardOptions = new DashboardOptions();
builder.Configuration.GetSection(DashboardOptions.SectionName).Bind(dashboardOptions);

builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton(dashboardOptions);
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<AgentGateway>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentGateway>());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<BasicAuthorizationMiddleware>();

app.MapGet("/api/health", (AgentGateway gateway) => Results.Ok(new
{
    status = "ok",
    serverTimeUtc = DateTime.UtcNow,
    serverName = Environment.MachineName,
    agentPort = agentOptions.Port,
    activeConnections = gateway.ActiveConnections
}));

app.MapGet("/api/clients", (ClientRegistry registry, AgentGateway gateway) =>
{
    IReadOnlyList<ClientSnapshot> snapshots = registry.Snapshots();
    return Results.Ok(new
    {
        serverTimeUtc = DateTime.UtcNow,
        serverName = Environment.MachineName,
        agentPort = agentOptions.Port,
        activeConnections = gateway.ActiveConnections,
        total = snapshots.Count,
        online = snapshots.Count(snapshot => snapshot.IsOnline),
        idleThresholdSeconds = agentOptions.OfflineAfterSeconds,
        captureIntervalSeconds = agentOptions.CaptureIntervalSeconds,
        clients = snapshots
    });
});

app.MapGet("/api/clients/{clientId}", (string clientId, ClientRegistry registry) =>
{
    ClientSession? session = registry.Find(clientId);
    return session is null
        ? Results.NotFound(new { message = "Агент не найден" })
        : Results.Ok(session.ToSnapshot());
});

app.MapGet("/api/clients/{clientId}/screenshot", (string clientId, ClientRegistry registry, HttpContext context) =>
{
    ClientSession? session = registry.Find(clientId);
    if (session is null)
    {
        return Results.NotFound(new { message = "Агент не найден" });
    }

    byte[]? image = session.GetScreenshot();
    if (image is null)
    {
        return Results.NotFound(new { message = "Снимок экрана ещё не получен" });
    }

    string etag = $"\"{session.ScreenshotAtUtc?.Ticks ?? 0}\"";
    if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "no-store";
    return Results.File(image, "image/jpeg");
});

app.MapPost("/api/clients/{clientId}/command", async (
    string clientId,
    CommandRequest request,
    ClientRegistry registry,
    CancellationToken cancellationToken) =>
{
    ClientSession? session = registry.Find(clientId);
    if (session is null)
    {
        return Results.NotFound(new { message = "Агент не найден" });
    }

    if (!session.IsOnline)
    {
        return Results.Conflict(new { message = "Агент не в сети" });
    }

    string action = string.IsNullOrWhiteSpace(request.Action) ? CommandActions.CaptureScreenshot : request.Action;
    var command = new AgentCommand
    {
        Id = Guid.NewGuid().ToString("N"),
        Action = action,
        Text = request.Text
    };

    bool queued = await session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
    return queued
        ? Results.Accepted($"/api/clients/{clientId}", new { message = $"Команда «{action}» отправлена агенту" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapDelete("/api/clients/{clientId}", (string clientId, ClientRegistry registry) =>
    registry.Remove(clientId)
        ? Results.Ok(new { message = "Агент удалён из списка" })
        : Results.NotFound(new { message = "Агент не найден" }));

app.MapFallback(async context =>
{
    string? indexPath = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath is { } root
        ? Path.Combine(root, "index.html")
        : null;

    if (indexPath is not null && File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.Logger.LogInformation("Панель мониторинга: http://{Address}:{HttpPort}/ (шлюз агентов: TCP {AgentPort})",
    "localhost",
    GetHttpPort(app.Configuration),
    agentOptions.Port);

app.Run();

static int GetHttpPort(IConfiguration configuration)
{
    string? url = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
    if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url.Split(';')[0], UriKind.Absolute, out Uri? uri))
    {
        return uri.Port;
    }

    return 5000;
}

internal sealed class CommandRequest
{
    public string? Action { get; set; }
    public string? Text { get; set; }
}
