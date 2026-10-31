using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public static class UnityLoginPayloadBuilder
{
    public static string Build(GameAuthSession session, HttpRequest request, int activeHangarSlot = LocalHangars.DefaultSlot,
        string language = "es", ServerMeshSnapshot? mesh = null, WorldServerOptions? world = null,
        IReadOnlyList<UnityHangar>? hangars = null, string instanceName = "Local emulator",
        IReadOnlySet<string>? enabledLanguages = null, int display2d = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var originalHost = request.Headers["X-Original-Host"].ToString();
        var host = ClientFacingGameHost.Resolve(request);
        var redirectedGameHost = ClientFacingGameHost.IsRedirectedGameHost(request);
        var scheme = ClientFacingGameHost.ResolveScheme(request);
        var starterMap = LocalCompanySelection.StarterMap(session.Company) ?? "1";
        string? gameServer = null;
        if (world is not null)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var lease = mesh?.Leases.FirstOrDefault(item => item.MapId.ToString() == starterMap
                && item.ExpiresAtUtc > nowUtc);
            var owner = mesh?.Nodes.FirstOrDefault(node => node.NodeId == lease?.NodeId
                && node.Status == ServerMeshNodeStatus.Active && node.WorldTcpPort is > 0 and <= 65535);
            gameServer = owner is null ? $"{world.Host}:{world.Port}"
                : $"{owner.WorldTcpHost}:{owner.WorldTcpPort}";
        }
        var data = new UnityLoginData
        {
            Lang = (enabledLanguages?.Contains(language) ?? LocalAccountPreferencesStore.Languages.Any(item => item.Code == language))
                ? language : LocalAccountPreferencesStore.Default.Language,
            UserId = session.GameUserId.ToString(), SessionId = session.Dosid,
            BasePath = "spacemap", Pid = "1398", Opid = "1398",
            BoardLink = $"{host}/indexInternal.es?action=internalForum",
            HelpLink = $"{host}/indexInternal.es?action=internalHelp",
            LoadingClaim = "DarkOrbit", ChatHost = ClientFacingGameHost.ResolveHostName(request),
            Cdn = "https://darkorbit-22.bpsecure.com/",
            UseHash = 1, Host = host, Browser = "Unknown", Fullscreen = 1,
            ItemXmlHash = CapturedAssetHashes.ItemXmlHash,
            ResourcesXmlHash = CapturedAssetHashes.ResourcesXmlHash,
            Resources3dXmlHash = CapturedAssetHashes.Resources3dXmlHash,
            Resources3dParticlesXmlHash = CapturedAssetHashes.Resources3dParticlesXmlHash,
            ResourceAchievementsXmlHash = CapturedAssetHashes.ResourceAchievementsXmlHash,
            ResourceEventsXmlHash = CapturedAssetHashes.ResourceEventsXmlHash,
            ResourceJumpGateXmlHash = CapturedAssetHashes.ResourceJumpGateXmlHash,
            ProfileXmlHash = CapturedAssetHashes.ProfileXmlHash,
            LanguageXmlHash = null, LoadingscreenHash = CapturedAssetHashes.LoadingscreenHash,
            GameclientHash = CapturedAssetHashes.GameclientHash, GameclientPath = "spacemap",
            LoadingscreenAssetsXmlHash = CapturedAssetHashes.LoadingscreenAssetsXmlHash,
            CrossdomainHash = CapturedAssetHashes.CrossdomainHash,
            ShowAdvertisingHint = false, GameclientAllowedInitDelay = "10",
            EventStreamContext = "", RequestScheme = scheme, SharedImages = host,
            // display2d feeds Settings.UserType: 1 forces 3D, 2 forces 2D, 0 keeps the client's force2D PlayerPref.
            UseDeviceFonts = 0, Display2d = display2d is 1 or 2 ? display2d : 0, Theme = "default",
            Platform = "UNITY_CLIENT", UserProfileImage = "", AutoStartEnabled = 0,
            MapId = starterMap, GameServerIp = gameServer,
            Hangars = new UnityHangarsEnvelope { Data = new UnityHangarsData
            {
                Faction = LocalCompanySelection.Faction(session.Company) ?? "0",
                Hangars = hangars ?? LocalHangars.For(session, activeHangarSlot)
            }},
            ServerInfo = new UnityServerInfo
            {
                InstanceName = instanceName, ServerTime = now.ToString("HH:mm"),
                ServerDate = now.ToString("dd.MM.yyyy"), TimeStamp = now.ToUnixTimeSeconds(),
                LocalizedTs = now.ToUnixTimeSeconds()
            },
            AllowChat = "1", NextAction = "internalWebGL"
        };
        return JsonSerializer.Serialize(data);
    }
}
