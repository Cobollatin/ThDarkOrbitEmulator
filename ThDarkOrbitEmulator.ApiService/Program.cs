using System.Security.Cryptography.X509Certificates;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.Cluster.Application;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Get the certificate thumbprint from the PowerShell output from ConnectionGuide.txt
var certThumbprint = ""; // Replace with actual thumbprint

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ConfigureHttpsDefaults(httpsOptions =>
    {
        // Load the wild certificate
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates.Find(X509FindType.FindByThumbprint, certThumbprint, false)[0];
        httpsOptions.ServerCertificate = cert;
    });
});

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddFeatureManagement();
builder.Services.AddKeycloakJwtAuthentication(builder.Configuration);

builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument(settings =>
{
    settings.ShortSchemaNames = true;
    settings.EnableJWTBearerAuth = true;
    settings.DocumentSettings = document =>
    {
        document.Title = "ThDarkOrbitEmulator API";
        document.Version = "v1";
    };
});

builder.Services.AddMemoryCache();
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("cache");
});
builder.Services.AddHybridCache();
builder.Services.AddOutputCache();

builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.Configure<ClusterOptions>(builder.Configuration.GetSection(ClusterOptions.SectionName));
builder.Services.AddSingleton<IWorkerSpawner, LocalWorkerSpawner>();
builder.Services.AddSingleton<IWorkerSpawner, SshWorkerSpawner>();
builder.Services.AddHostedService<MasterOrchestratorBackgroundService>();

var app = builder.Build();

// LOGGING MIDDLEWARE
app.Use(async (context, next) =>
{
    // Log to CONSOLE (always visible)
    Console.WriteLine($"🔥🔥🔥 REQUEST: {context.Request.Method} {context.Request.Path} - Host: {context.Request.Host}");

    // Also try to log normally
    try
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("🔥 REQUEST: {Method} {Path} - Host: {Host}",
            context.Request.Method,
            context.Request.Path,
            context.Request.Host);
    }
    catch { }

    await next();
});

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.UseFastEndpoints(config =>
{
    config.Endpoints.RoutePrefix = "api";
});

app.UseSwaggerGen();
app.MapDefaultEndpoints();

app.Run();
