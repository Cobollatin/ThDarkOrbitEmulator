var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var postgresUser = builder.AddParameter("postgres-user", "app");
var postgresPassword = builder.AddParameter("postgres-password", "app", secret: true);

var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithImageTag("16")
    .WithDataVolume(name: "thdarkorbit-postgres-data", isReadOnly: false);

var appDatabase = postgres.AddDatabase("thdarkorbit");

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "25.0")
    .WithEnvironment("KEYCLOAK_ADMIN", "admin")
    .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", "admin")
    .WithEnvironment("KC_DB", "postgres")
    .WithEnvironment("KC_DB_URL_HOST", "postgres")
    .WithEnvironment("KC_DB_URL_PORT", "5432")
    .WithEnvironment("KC_DB_URL_DATABASE", "thdarkorbit")
    .WithEnvironment("KC_DB_SCHEMA", "keycloak")
    .WithEnvironment("KC_DB_USERNAME", "app")
    .WithEnvironment("KC_DB_PASSWORD", "app")
    .WithHttpEndpoint(port: 8081, targetPort: 8080, name: "http")
    .WithArgs("start-dev")
    .WaitFor(postgres);

builder.AddContainer("otel-collector", "otel/opentelemetry-collector-contrib", "0.102.1")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "grpc")
    .WithEndpoint(port: 4318, targetPort: 4318, name: "http");

builder.AddContainer("prometheus", "prom/prometheus", "v2.53.0")
    .WithHttpEndpoint(port: 19090, targetPort: 9090, name: "http");

builder.AddContainer("grafana", "grafana/grafana-oss", "11.1.0")
    .WithEnvironment("GF_SECURITY_ADMIN_USER", "admin")
    .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", "admin")
    .WithHttpEndpoint(port: 13000, targetPort: 3000, name: "http");

var apiService = builder.AddProject<Projects.ThDarkOrbitEmulator_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WithReference(appDatabase, "identity-write")
    .WithReference(appDatabase, "identity-read")
    .WithEnvironment("IdentityDatabase__Schema", "identity")
    .WithEnvironment("IdentityDatabase__AutoMigrate", "true")
    .WithEnvironment("Keycloak__Authority", "http://localhost:8081/realms/thdarkorbit")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")
    .WaitFor(appDatabase);

builder.AddProject<Projects.ThDarkOrbitEmulator_Gateway>("gateway")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WithEnvironment("Keycloak__Authority", "http://localhost:8081/realms/thdarkorbit")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")
    .WaitFor(apiService)
    .WaitFor(keycloak);

builder.Build().Run();
