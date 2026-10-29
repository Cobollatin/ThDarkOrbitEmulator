using System.Text.Json;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.ApiService.Common;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using StackExchange.Redis;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

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
var redisConnectionString = builder.Configuration.GetConnectionString("sessions-redis");
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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IManualActivityTracker, ManualActivityTracker>();

builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IGameAuthSessionStore, GameAuthSessionStore>();
builder.Services.AddSingleton<LocalHangarStore>();
builder.Services.AddSingleton<ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.CockpitCatalogStore>();
builder.Services.AddSingleton<LocalHangarEquipmentStore>();
builder.Services.AddSingleton<EconomyStore>();
builder.Services.AddSingleton<IWorldRepairWallet, WorldRepairWallet>();
builder.Services.AddSingleton<ThDarkOrbitEmulator.ApiService.Features.Backoffice.Progression.ProgressionBackofficeStore>();
builder.Services.Configure<WorldServerOptions>(builder.Configuration.GetSection(WorldServerOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorldServerOptions>>().Value);
builder.Services.AddSingleton<IWorldPilotResolver, WorldPilotResolver>();
builder.Services.AddSingleton<IWorldAccountState, WorldAccountState>();
builder.Services.AddSingleton<WorldSessionStore>();
builder.Services.AddSingleton<IRuntimeVariableRegistry, RuntimeVariableStore>();
builder.Services.AddSingleton<IAdminCommandDispatcher, AdminCommandDispatcher>();
builder.Services.AddSingleton<WorldAdminEvents>();
builder.Services.AddSingleton<IWorldRewardWallet, WorldRewardWallet>();
builder.Services.AddSingleton<WorldCollectibles>();
builder.Services.Configure<WorldNpcOptions>(builder.Configuration.GetSection(WorldNpcOptions.SectionName));
builder.Services.AddSingleton<IWorldNpcRewards, WorldNpcRewards>();
builder.Services.AddSingleton<IWorldPulseInventory, WorldPulseInventory>();
builder.Services.AddSingleton<WorldNpcs>();
builder.Services.AddSingleton<WorldGateCombat>();
builder.Services.AddHostedService<WorldNpcSimulationService>();
builder.Services.AddSingleton<LocalPetStore>();
builder.Services.AddSingleton<WorldServerHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorldServerHostedService>());
builder.Services.AddSingleton<LocalGalaxyGateStore>();
builder.Services.AddSingleton<LocalSkylabStore>();
builder.Services.AddSingleton<LocalTechFactoryStore>();
builder.Services.AddSingleton<LocalClanStore>();
builder.Services.AddSingleton<LocalPilotSkillStore>();
builder.Services.AddSingleton<LocalMessagingStore>();
builder.Services.AddSingleton<LocalAccountPreferencesStore>();
builder.Services.Configure<ClusterOptions>(builder.Configuration.GetSection(ClusterOptions.SectionName));
new ClusterOptions
{
    DesiredWorkers = builder.Configuration.GetValue("Cluster:DesiredWorkers", 1),
    MaxMapsPerNode = builder.Configuration.GetValue("Cluster:MaxMapsPerNode", 4),
    HeartbeatIntervalSeconds = builder.Configuration.GetValue("Cluster:HeartbeatIntervalSeconds", 10),
    LeaseTtlSeconds = builder.Configuration.GetValue("Cluster:LeaseTtlSeconds", 30),
    NodeId = builder.Configuration["Cluster:NodeId"]
}.Validate();
builder.Services.AddSingleton<IServerMeshMutex, RedisServerMeshMutex>();
builder.Services.AddSingleton<IServerMeshRegistry, ServerMeshCoordinator>();
builder.Services.AddSingleton<LocalWorkerSpawner>();
builder.Services.AddHostedService<ServerMeshHostedService>();

var app = builder.Build();

await app.ValidateIdentityDatabaseAsync();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
        && (context.Request.Path.StartsWithSegments("/unityApi")
            || context.Request.Path.StartsWithSegments("/indexInternal.es")))
    {
        // Keep credentials and dosid out of access logs. The action key distinguishes
        // legacy menu routes from an unmatched endpoint behind the fallback policy.
        var action = context.Request.Query["action"].ToString();
        if (action.Length is < 1 or > 40 || !action.All(char.IsLetter)) action = "(none)";
        app.Logger.LogWarning("Unity route returned 401: {Method} {Path}, action={Action}, endpoint={Endpoint}",
            context.Request.Method, context.Request.Path.Value,
            action, context.GetEndpoint()?.DisplayName ?? "unmatched");
    }
});
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
app.MapEconomyAdmin();
ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.CockpitCatalogEndpoints.MapCockpitCatalog(app);
ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.UnityConfigRoutes.MapUnityConfigRoutes(app);
ThDarkOrbitEmulator.ApiService.Features.Backoffice.Progression.ProgressionBackofficeEndpoints.MapProgressionBackoffice(app);
WorldHttpBridge.Map(app);

app.Run();
