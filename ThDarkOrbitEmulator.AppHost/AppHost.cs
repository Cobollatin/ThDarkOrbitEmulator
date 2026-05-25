using ThDarkOrbitEmulator.AppHost.Operations;

var builder = DistributedApplication.CreateBuilder(args);

var observabilityPath = Path.Combine(builder.AppHostDirectory, "observability");
var aspireOtlpEndpoint = builder.Configuration["DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]
    ?? builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]
    ?? "http://host.docker.internal:4318";

var cache = builder.AddRedis("cache")
    .WithRedisInsight(redisInsight => redisInsight.WithHostPort(5540))
    .WithOperationsCommands();

var postgresUser = builder.AddParameter("postgres-user", "app");
var postgresPassword = builder.AddParameter("postgres-password", "app", secret: true);

var postgres = builder.AddPostgres("thdarkorbit-emulator", postgresUser, postgresPassword)
    .WithImageTag("16")
    .WithDataVolume(name: "thdarkorbit-postgres-data", isReadOnly: false)
    .WithPgAdmin(pgAdmin => pgAdmin
        .WithImageTag("9.15.0")
        .WithHostPort(5050))
    .WithOperationsCommands()
    .WithIdentityBackofficeCommands("thdarkorbit");

var appDatabase = postgres.AddDatabase("thdarkorbit");

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "25.0")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
    .WithEnvironment("KC_DB", "postgres")
    .WithEnvironment("KC_DB_URL", "jdbc:postgresql://thdarkorbit-emulator:5432/thdarkorbit")
    .WithEnvironment("KC_DB_SCHEMA", "keycloak")
    .WithEnvironment("KC_DB_USERNAME", postgresUser)
    .WithEnvironment("KC_DB_PASSWORD", postgresPassword)
    .WithEnvironment("KC_HOSTNAME", "localhost")
    .WithEnvironment("KC_HOSTNAME_PORT", "8081")
    .WithEnvironment("KC_HOSTNAME_STRICT", "false")
    .WithEnvironment("KC_HTTP_ENABLED", "true")
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
    .WithArgs("start-dev")
    .WithKeycloakBackofficeCommands()
    .WaitFor(appDatabase);

var otelCollector = builder.AddContainer("otel-collector", "otel/opentelemetry-collector-contrib", "0.106.1")
    .WithBindMount(Path.Combine(observabilityPath, "otel-collector-config.yaml"), "/etc/otelcol-contrib/config.yaml")
    .WithEnvironment("ASPIRE_OTLP_ENDPOINT", aspireOtlpEndpoint)
    .WithArgs("--config=/etc/otelcol-contrib/config.yaml")
    .WithHttpEndpoint(port: 14317, targetPort: 4317, name: "otlp-grpc")
    .WithHttpEndpoint(port: 14318, targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(port: 18889, targetPort: 8889, name: "metrics");

builder.AddContainer("prometheus", "prom/prometheus", "v2.53.0")
    .WithBindMount(Path.Combine(observabilityPath, "prometheus.yml"), "/etc/prometheus/prometheus.yml")
    .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-lifecycle")
    .WithHttpEndpoint(port: 19090, targetPort: 9090, name: "http")
    .WaitFor(otelCollector);

builder.AddContainer("grafana", "grafana/grafana-oss", "11.1.0")
    .WithEnvironment("GF_SECURITY_ADMIN_USER", "admin")
    .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", "admin")
    .WithBindMount(Path.Combine(observabilityPath, "grafana", "provisioning"), "/etc/grafana/provisioning")
    .WithBindMount(Path.Combine(observabilityPath, "grafana", "dashboards"), "/etc/grafana/provisioning/dashboards/json")
    .WithHttpEndpoint(port: 13000, targetPort: 3000, name: "http")
    .WaitFor(otelCollector);

var apiService = builder.AddProject<Projects.ThDarkOrbitEmulator_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WithReference(appDatabase, "identity-write")
    .WithReference(appDatabase, "identity-read")
    .WithEnvironment("IdentityDatabase__Schema", "identity")
    .WithEnvironment("IdentityDatabase__AutoMigrate", "true")
    .WithEnvironment("IdentityDatabase__DockerPostgresContainerName", "thdarkorbit-emulator")
    .WithEnvironment("Keycloak__Authority", "http://localhost:8081/realms/thdarkorbit")
    .WithEnvironment("KeycloakAdmin__BaseUrl", "http://localhost:8081")
    .WithEnvironment("KeycloakAdmin__Realm", "thdarkorbit")
    .WithEnvironment("KeycloakAdmin__AdminUsername", "admin")
    .WithEnvironment("KeycloakAdmin__AdminPassword", "admin")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4318")
    .WithOtlpExporter()
    .WithEmulatorOperationsCommand()
    .WaitFor(appDatabase)
    .WaitFor(keycloak);

var gateway = builder.AddProject<Projects.ThDarkOrbitEmulator_Gateway>("gateway")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WithEnvironment("Keycloak__Authority", "http://localhost:8081/realms/thdarkorbit")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4318")
    .WithOperationsCommands()
    .WithOtlpExporter()
    .WaitFor(apiService)
    .WaitFor(keycloak);

builder.AddJavaScriptApp("cms", "../ThDarkOrbitEmulator.Cms", "start")
    .WithHttpEndpoint(targetPort: 4300)
    .WithReference(gateway)
    .WaitFor(gateway);

builder.Build().Run();
