using Microsoft.Extensions.Configuration;
using ThDarkOrbitEmulator.AppHost;
using Aspire.Hosting.ApplicationModel;
using ThDarkOrbitEmulator.AppHost.ClientTesting;
using ThDarkOrbitEmulator.AppHost.Dashboard;
using ThDarkOrbitEmulator.AppHost.Operations;

// Docker publishes persistent container ports on IPv4 loopback; localhost's IPv6 address stalls health probes on Windows.
AppContext.SetSwitch("System.Net.DisableIPv6", true);

var builder = DistributedApplication.CreateBuilder(args);
var desiredReplicas = MeshScalingOptions.DesiredReplicas(builder.Configuration);

var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var clientTestingOptions = new ClientTestingOptions();
builder.Configuration.GetSection(ClientTestingOptions.SectionName).Bind(clientTestingOptions);
var clientTestingPaths = new ClientTestingPaths(repoRoot, clientTestingOptions);

var observabilityPath = Path.Combine(builder.AppHostDirectory, "observability");
var aspireOtlpEndpoint = builder.Configuration["DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]
    ?? builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]
    ?? "http://host.docker.internal:4318";

var sessionsRedis = builder.AddRedis(DashboardResourceNames.SessionsRedis)
    .WithDataVolume(name: "thdarkorbit-redis-data", isReadOnly: false)
    .WithPurposeIcon("Database")
    .WithExternalDocLink("https://redis.io/docs/", "Redis docs")
    .WithRedisInsight(
        redisInsight => redisInsight
            .WithHostPort(5540)
            .WithEnvironment("RI_APP_HOST", "0.0.0.0")
            .WithEnvironment("RI_APP_PORT", "5540")
            .WithPurposeIcon("Eye")
            .WithHttpUiLink("RedisInsight UI"),
        containerName: DashboardResourceNames.SessionsRedisInsight)
    .WithOperationsCommands();

var postgresUser = builder.AddParameter("postgres-user", "app");
var postgresPassword = builder.AddParameter("postgres-password", "app", secret: true);
var ownerPassword = builder.AddParameter("owner-password", "owner", secret: true);

var dataPostgres = builder.AddPostgres(DashboardResourceNames.DataPostgres, postgresUser, postgresPassword)
    .WithImageTag("16")
    .WithPurposeIcon("Database")
    .WithDataVolume(name: "thdarkorbit-postgres-data", isReadOnly: false)
    .WithPgAdmin(pgAdmin => pgAdmin
        .WithImageTag("9.15.0")
        .WithHostPort(5050)
        .WithPurposeIcon("Table")
        .WithHttpUiLink("pgAdmin UI"))
    .WithExternalDocLink("https://www.postgresql.org/docs/", "PostgreSQL docs")
    .WithOperationsCommands()
    .WithIdentityBackofficeCommands("thdarkorbit");

var appDatabase = dataPostgres.AddDatabase(DashboardResourceNames.AppDatabase, "thdarkorbit");

var authKeycloak = builder.AddContainer(DashboardResourceNames.AuthKeycloak, "quay.io/keycloak/keycloak", "25.0")
    .WithPurposeIcon("ShieldKeyhole")
    .WithEnvironment("KEYCLOAK_ADMIN", "admin")
    .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", "admin")
    .WithEnvironment("KC_DB", "postgres")
    .WithEnvironment("KC_DB_URL", $"jdbc:postgresql://{DashboardResourceNames.DataPostgres}:5432/thdarkorbit")
    .WithEnvironment("KC_DB_SCHEMA", "keycloak")
    .WithEnvironment("KC_DB_USERNAME", postgresUser)
    .WithEnvironment("KC_DB_PASSWORD", postgresPassword)
    .WithEnvironment("KC_HOSTNAME", "localhost")
    .WithEnvironment("KC_HOSTNAME_PORT", "8081")
    .WithEnvironment("KC_HOSTNAME_STRICT", "false")
    .WithEnvironment("KC_HTTP_ENABLED", "true")
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithHttpHealthCheck("/health/ready", endpointName: "management")
    .WithHttpUiLink("Keycloak admin")
    .WithExternalDocLink("https://www.keycloak.org/documentation", "Keycloak docs")
    .WithArgs("start-dev")
    .WithKeycloakBackofficeCommands()
    .WaitFor(appDatabase);

var keycloakBaseUrl = authKeycloak.GetEndpoint("http");
var keycloakRealmUrl = ReferenceExpression.Create($"{keycloakBaseUrl}/realms/thdarkorbit");

var telemetryOtel = builder.AddContainer(DashboardResourceNames.TelemetryOtel, "otel/opentelemetry-collector-contrib", "0.106.1")
    .WithPurposeIcon("Flow")
    .WithBindMount(Path.Combine(observabilityPath, "otel-collector-config.yaml"), "/etc/otelcol-contrib/config.yaml")
    .WithEnvironment("ASPIRE_OTLP_ENDPOINT", aspireOtlpEndpoint)
    .WithArgs("--config=/etc/otelcol-contrib/config.yaml")
    .WithHttpEndpoint(port: 14317, targetPort: 4317, name: "otlp-grpc")
    .WithHttpEndpoint(port: 14318, targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(port: 18889, targetPort: 8889, name: "metrics")
    .WithExternalDocLink("https://opentelemetry.io/docs/", "OpenTelemetry docs");

builder.AddContainer(DashboardResourceNames.MetricsPrometheus, "prom/prometheus", "v2.53.0")
    .WithPurposeIcon("Pulse")
    .WithBindMount(Path.Combine(observabilityPath, "prometheus.yml"), "/etc/prometheus/prometheus.yml")
    .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-lifecycle")
    .WithHttpEndpoint(port: 19090, targetPort: 9090, name: "http")
    .WithHttpUiLink("Prometheus UI")
    .WithExternalDocLink("https://prometheus.io/docs/", "Prometheus docs")
    .WaitFor(telemetryOtel);

builder.AddContainer(DashboardResourceNames.DashboardsGrafana, "grafana/grafana-oss", "11.1.0")
    .WithPurposeIcon("ChartMultiple")
    .WithEnvironment("GF_SECURITY_ADMIN_USER", "admin")
    .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", "admin")
    .WithBindMount(Path.Combine(observabilityPath, "grafana", "provisioning"), "/etc/grafana/provisioning")
    .WithBindMount(Path.Combine(observabilityPath, "grafana", "dashboards"), "/etc/grafana/provisioning/dashboards/json")
    .WithHttpEndpoint(port: 13000, targetPort: 3000, name: "http")
    .WithHttpUiLink("Grafana UI (admin/admin)")
    .WithExternalDocLink("https://grafana.com/docs/", "Grafana docs")
    .WaitFor(telemetryOtel);

// Launch profile "https" binds 60952/443, which are the gateway's Unity listeners under Aspire.
// Importing them into api-game steals the ports and breaks the /health check (it probes https first).
var apiGame = builder.AddProject<Projects.ThDarkOrbitEmulator_ApiService>(DashboardResourceNames.ApiGame, launchProfileName: "http")
    .WithReplicas(desiredReplicas)
    .WithHttpEndpoint(name: "http")
    .WithPurposeIcon("Cube")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("DOTNET_SYSTEM_NET_DISABLEIPV6", "1")
    .WithReference(sessionsRedis)
    .WithReference(appDatabase, "identity-write")
    .WithReference(appDatabase, "identity-read")
    .WithEnvironment("IdentityDatabase__Schema", "identity")
    .WithEnvironment("WorldServer__Enabled", "true")
    .WithEnvironment("WorldServer__Host", "127.0.0.1")
    .WithEnvironment("ClientGateway__HttpsOrigin", "https://localhost:60952")
    .WithEnvironment("WorldServer__Port", desiredReplicas == 1 ? "60954" : "0")
    .WithEnvironment("WorldServer__DynamicTcpPort", desiredReplicas > 1 ? "true" : "false")
    .WithEnvironment("Cluster__DesiredWorkers", desiredReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture))
    .WithEnvironment("IdentityDatabase__AutoMigrate", "true")
    .WithEnvironment("IdentityDatabase__DockerPostgresContainerName", DashboardResourceNames.DataPostgres)
    .WithEnvironment("Keycloak__Authority", keycloakRealmUrl)
    .WithEnvironment("KeycloakAdmin__BaseUrl", ReferenceExpression.Create($"{keycloakBaseUrl}"))
    .WithEnvironment("KeycloakAdmin__Realm", "thdarkorbit")
    .WithEnvironment("KeycloakAdmin__AdminUsername", "admin")
    .WithEnvironment("KeycloakAdmin__AdminPassword", "admin")
    .WithEnvironment("PlatformOwnerSeed__Enabled", "true")
    .WithEnvironment("PlatformOwnerSeed__Username", "owner")
    .WithEnvironment("PlatformOwnerSeed__Password", ownerPassword)
    .WithEnvironment("PlatformOwnerSeed__Email", "owner@thdarkorbit.local")
    .WithEnvironment("PlatformOwnerSeed__RealmRole", "emulator-admin")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", $"http://{DashboardResourceNames.TelemetryOtel}:4318")
    .WithOtlpExporter()
    .WithEmulatorOperationsCommand(desiredReplicas, dataPostgres)
    .WaitFor(sessionsRedis)
    .WaitFor(appDatabase)
    .WaitFor(authKeycloak);

var gatewayPlayers = builder.AddProject<Projects.ThDarkOrbitEmulator_Gateway>(DashboardResourceNames.GatewayPlayers)
    .WithPurposeIcon("ArrowRouting")
    // IL2CPP literal 24847 is https://localhost/ and cannot fit :60952 in its string slot.
    // Serve the same YARP routes on the default HTTPS port for native Unity requests.
    .WithHttpsEndpoint(port: 443, name: "unity-https-default")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("DOTNET_SYSTEM_NET_DISABLEIPV6", "1")
    .WithReference(apiGame)
    .WithEnvironment("Keycloak__Authority", keycloakRealmUrl)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", $"http://{DashboardResourceNames.TelemetryOtel}:4318")
    .WithOperationsCommands()
    .WithClientTestingCommands(clientTestingPaths)
    .WithOtlpExporter()
    .WaitFor(apiGame)
    .WaitFor(authKeycloak);

builder.AddJavaScriptApp(DashboardResourceNames.CmsPlayers, "../ThDarkOrbitEmulator.Cms", "start")
    .WithPurposeIcon("Window")
    .WithHttpEndpoint(targetPort: 4300)
    .WithHttpUiLink("Player CMS")
    .WithAutoOpenInBrowser()
    .WithReference(gatewayPlayers)
    .WaitFor(gatewayPlayers);

// Persistent containers survive AppHost restarts, so Keycloak/Postgres/Redis are not re-created on every run.
// Stop them with `docker stop` (or from the dashboard) when you are done.
foreach (var container in builder.Resources.OfType<ContainerResource>())
{
    builder.CreateResourceBuilder(container)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerRuntimeArgs(
        "--label", "com.docker.compose.project=thdarkorbit",
        "--label", $"com.docker.compose.service={container.Name}");
}

builder.EnsureNamedDockerVolumesExist();

builder.Build().Run();
