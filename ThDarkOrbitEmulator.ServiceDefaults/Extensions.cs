using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using System.Threading.RateLimiting;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
public static class Extensions
{
    private static readonly string InstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var instanceId = builder.Configuration["Cluster:NodeId"] is { Length: > 0 } nodeId
            ? nodeId : InstanceId;
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];

        builder.Logging.ClearProviders();

        builder.Services.AddSerilog((services, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("application", builder.Environment.ApplicationName)
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                loggerConfiguration.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpEndpoint;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = builder.Environment.ApplicationName,
                        ["service.instance.id"] = instanceId
                    };
                });
            }
        });

        builder.Logging.AddSerilog(dispose: true);

        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.User.Identity?.IsAuthenticated == true || context.Request.Path.StartsWithSegments("/spacemap/world") || context.Request.Cookies.ContainsKey("dosid"))
                {
                    return RateLimitPartition.GetNoLimiter("authenticated");
                }

                var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"anonymous:{remoteIp}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHttpLogging(logging =>
        {
            logging.LoggingFields = HttpLoggingFields.RequestPropertiesAndHeaders
                | HttpLoggingFields.ResponsePropertiesAndHeaders
                | HttpLoggingFields.Duration;
            logging.CombineLogs = true;
            logging.RequestHeaders.Add("traceparent");
            logging.RequestHeaders.Add("x-correlation-id");
            logging.ResponseHeaders.Add("traceparent");
            logging.MediaTypeOptions.AddText("application/json");
            logging.MediaTypeOptions.AddText("application/problem+json");
        });

        var openTelemetryBuilder = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(new Dictionary<string, object>
            {
                ["service.instance.id"] = builder.Configuration["Cluster:NodeId"] is { Length: > 0 } nodeId
                    ? nodeId : InstanceId
            }))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
            });

        builder.AddOpenTelemetryExporters(openTelemetryBuilder);

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(
        this TBuilder builder,
        OpenTelemetry.OpenTelemetryBuilder openTelemetryBuilder) where TBuilder : IHostApplicationBuilder
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            openTelemetryBuilder.UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms (client {ClientIP})";
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            };
            options.GetLevel = (httpContext, elapsed, ex) =>
            {
                if (ex is not null)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Response.StatusCode >= 400 || elapsed > 1000)
                {
                    return LogEventLevel.Warning;
                }

                return LogEventLevel.Information;
            };
        });

        app.UseHttpLogging();
        app.UseRateLimiter();

        return app;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            }).AllowAnonymous();
        }

        return app;
    }
}
