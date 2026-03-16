using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.Cluster.Application;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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
