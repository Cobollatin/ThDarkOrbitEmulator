using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThDarkOrbitEmulator.AppHost.Operations;

namespace ThDarkOrbitEmulator.AppHost.Dashboard;

internal static class DashboardPresentationExtensions
{
    private static int _cmsBrowserLaunched;

    public static IResourceBuilder<T> WithPurposeIcon<T>(
        this IResourceBuilder<T> builder,
        string iconName,
        IconVariant variant = IconVariant.Filled)
        where T : IResource =>
        builder.WithIconName(iconName, variant);

    public static IResourceBuilder<T> WithHttpUiLink<T>(
        this IResourceBuilder<T> builder,
        string displayText,
        int displayOrder = 100)
        where T : IResource =>
        builder.WithUrlForEndpoint("http", url =>
        {
            url.DisplayText = displayText;
#pragma warning disable CS0618
            url.DisplayOrder = displayOrder;
#pragma warning restore CS0618
        });

    public static IResourceBuilder<T> WithExternalDocLink<T>(
        this IResourceBuilder<T> builder,
        string url,
        string displayText,
        int displayOrder = 200)
        where T : IResource =>
        builder.WithUrl(url, displayText);

    /// <summary>
    /// Ensures named Docker volumes exist before DCP starts reconciling volumes.
    /// Docker Engine 29+ outputs "volume &lt;name&gt; not found" instead of "no such volume" on
    /// <c>docker volume inspect</c>, which causes DCP 0.26.x VolumeReconciler to retry indefinitely
    /// instead of creating the missing volume, leaving all containers stuck in Unknown state.
    /// </summary>
    public static IDistributedApplicationBuilder EnsureNamedDockerVolumesExist(
        this IDistributedApplicationBuilder builder)
    {
        if (IsTestHost())
        {
            return builder;
        }

        var volumeNames = builder.Resources
            .OfType<ContainerResource>()
            .SelectMany(container => container.Annotations.OfType<ContainerMountAnnotation>())
            .Where(mount => mount.Type == ContainerMountType.Volume && !string.IsNullOrWhiteSpace(mount.Source))
            .Select(mount => mount.Source!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (volumeNames.Length == 0)
        {
            return builder;
        }

        try
        {
            var existingVolumes = GetExistingDockerVolumes();
            foreach (var volumeName in volumeNames)
            {
                if (!existingVolumes.Contains(volumeName))
                {
                    CreateDockerVolume(volumeName);
                }
            }
        }
        catch
        {
            // Best-effort pre-creation; never block AppHost startup if Docker CLI is unavailable.
        }

        return builder;
    }

    public static IResourceBuilder<T> WithAutoOpenInBrowser<T>(
        this IResourceBuilder<T> builder,
        string defaultUrl = DevOperationsUrls.Cms)
        where T : IResourceWithEndpoints
    {
        if (!ShouldAutoOpenBrowser(builder.ApplicationBuilder.Configuration))
        {
            return builder;
        }

        var configuredUrl = builder.ApplicationBuilder.Configuration["ASPIRE_CMS_BROWSER_URL"]
            ?? Environment.GetEnvironmentVariable("ASPIRE_CMS_BROWSER_URL");
        var preferredUrl = string.IsNullOrWhiteSpace(configuredUrl) ? defaultUrl : configuredUrl.Trim();

        builder.ApplicationBuilder.Eventing.Subscribe<AfterResourcesCreatedEvent>((@event, cancellationToken) =>
        {
            var lifetime = @event.Services.GetService<IHostApplicationLifetime>();
            var logger = @event.Services.GetService<ILoggerFactory>()?.CreateLogger("AspireCmsBrowserLauncher");
            var stoppingToken = lifetime?.ApplicationStopping ?? CancellationToken.None;

            _ = PollAndOpenBrowserAsync(builder.Resource, preferredUrl, logger, stoppingToken);
            return Task.CompletedTask;
        });

        return builder;
    }

    internal static bool ShouldAutoOpenBrowser(IConfiguration configuration)
    {
        var flag = configuration["ASPIRE_OPEN_CMS_ON_START"]
            ?? Environment.GetEnvironmentVariable("ASPIRE_OPEN_CMS_ON_START");
        if (!string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Environment.UserInteractive
            || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsTestHost();
    }

    private static bool IsTestHost() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
        {
            var name = assembly.GetName().Name;
            return name is not null
                && (name.Equals("nunit.framework", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Aspire.Hosting.Testing", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase));
        });

    private static HashSet<string> GetExistingDockerVolumes()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "volume ls --quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void CreateDockerVolume(string volumeName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"volume create {volumeName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        process?.WaitForExit(5000);
    }

    private static async Task PollAndOpenBrowserAsync(
        IResourceWithEndpoints resource,
        string preferredUrl,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };

            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                foreach (var candidateUrl in GetCandidateUrls(resource, preferredUrl))
                {
                    if (await IsEndpointReadyAsync(httpClient, candidateUrl, cancellationToken))
                    {
                        if (Interlocked.CompareExchange(ref _cmsBrowserLaunched, 1, 0) == 0)
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = candidateUrl,
                                UseShellExecute = true
                            });
                            logger?.LogInformation("Opened CMS in browser at {Url}.", candidateUrl);
                        }

                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down.
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not auto-launch CMS browser window.");
        }
    }

    private static IEnumerable<string> GetCandidateUrls(IResourceWithEndpoints resource, string preferredUrl)
    {
        if (!string.IsNullOrWhiteSpace(preferredUrl))
        {
            yield return preferredUrl;
        }

        if (resource.TryGetEndpoints(out var endpoints))
        {
            var allocated = endpoints
                .FirstOrDefault(e => string.Equals(e.Name, "http", StringComparison.OrdinalIgnoreCase))
                ?.AllocatedEndpoint?.UriString;
            if (!string.IsNullOrWhiteSpace(allocated)
                && !string.Equals(allocated, preferredUrl, StringComparison.OrdinalIgnoreCase))
            {
                yield return allocated;
            }
        }
    }

    private static async Task<bool> IsEndpointReadyAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }
}

