using System.Text.Json;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.ApiService.Common;
using ThDarkOrbitEmulator.Cluster.Application;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using StackExchange.Redis;
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
var redisConnectionString = builder.Configuration.GetConnectionString("cache");
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
});
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisConnectionString));
}
builder.Services.AddHybridCache();
builder.Services.AddOutputCache();

builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IGameAuthSessionStore, GameAuthSessionStore>();
builder.Services.Configure<ClusterOptions>(builder.Configuration.GetSection(ClusterOptions.SectionName));
builder.Services.AddSingleton<IWorkerSpawner, LocalWorkerSpawner>();
builder.Services.AddSingleton<IWorkerSpawner, SshWorkerSpawner>();
builder.Services.AddHostedService<MasterOrchestratorBackgroundService>();

var app = builder.Build();

await app.ValidateIdentityDatabaseAsync();

app.UseExceptionHandler();
app.UseServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.UseFastEndpoints(config =>
{
    config.Endpoints.RoutePrefix = "api";
    config.Versioning.Prefix = "v";
    config.Versioning.PrependToRoute = true;
    config.Errors.ResponseBuilder = (failures, _, _) =>
    {
        var details = failures.Count > 0
            ? string.Join(' ', failures.Select(failure => failure.ErrorMessage))
            : null;
        var body = ApiErrorResponse.Create(ApiErrorCode.CommonValidationFailed, details);
        return JsonSerializer.Serialize(body);
    };
});

app.UseSwaggerGen();
app.MapDefaultEndpoints();

app.Run();
