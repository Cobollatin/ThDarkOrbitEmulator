using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts;

// Verified in 20261021-224636-login flow 82 (application/json).
// The data field is JSON encoded as a string on the wire.
public sealed record UnityLoginCapturedResponse
{
    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;
}

// Shape obtained by parsing UnityLoginCapturedResponse.Data as JSON.
// Field presence reflects one login capture; only languageXmlHash was observed as null.
public sealed record UnityLoginData
{
    [JsonPropertyName("lang")]
    public string Lang { get; init; } = string.Empty;

    [JsonPropertyName("userID")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("sessionID")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("basePath")]
    public string BasePath { get; init; } = string.Empty;

    [JsonPropertyName("pid")]
    public string Pid { get; init; } = string.Empty;

    [JsonPropertyName("opid")]
    public string Opid { get; init; } = string.Empty;

    [JsonPropertyName("boardLink")]
    public string BoardLink { get; init; } = string.Empty;

    [JsonPropertyName("helpLink")]
    public string HelpLink { get; init; } = string.Empty;

    [JsonPropertyName("loadingClaim")]
    public string LoadingClaim { get; init; } = string.Empty;

    [JsonPropertyName("chatHost")]
    public string ChatHost { get; init; } = string.Empty;

    [JsonPropertyName("cdn")]
    public string Cdn { get; init; } = string.Empty;

    [JsonPropertyName("useHash")]
    public int UseHash { get; init; }

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("browser")]
    public string Browser { get; init; } = string.Empty;

    [JsonPropertyName("fullscreen")]
    public int Fullscreen { get; init; }

    [JsonPropertyName("itemXmlHash")]
    public string ItemXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("resourcesXmlHash")]
    public string ResourcesXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("resources3dXmlHash")]
    public string Resources3dXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("resources3dparticlesXmlHash")]
    public string Resources3dParticlesXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("resourceachievementsXmlHash")]
    public string ResourceAchievementsXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("resourceEventsXmlHash")]
    public string ResourceEventsXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("resourceJumpGateXmlHash")]
    public string ResourceJumpGateXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("profileXmlHash")]
    public string ProfileXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("languageXmlHash")]
    public string? LanguageXmlHash { get; init; }

    [JsonPropertyName("loadingscreenHash")]
    public string LoadingscreenHash { get; init; } = string.Empty;

    [JsonPropertyName("gameclientHash")]
    public string GameclientHash { get; init; } = string.Empty;

    [JsonPropertyName("gameclientPath")]
    public string GameclientPath { get; init; } = string.Empty;

    [JsonPropertyName("loadingscreenAssetsXmlHash")]
    public string LoadingscreenAssetsXmlHash { get; init; } = string.Empty;

    [JsonPropertyName("crossdomainHash")]
    public string CrossdomainHash { get; init; } = string.Empty;

    [JsonPropertyName("showAdvertisingHint")]
    public bool ShowAdvertisingHint { get; init; }

    [JsonPropertyName("gameclientAllowedInitDelay")]
    public string GameclientAllowedInitDelay { get; init; } = string.Empty;

    [JsonPropertyName("eventStreamContext")]
    public string EventStreamContext { get; init; } = string.Empty;

    [JsonPropertyName("requestScheme")]
    public string RequestScheme { get; init; } = string.Empty;

    [JsonPropertyName("sharedImages")]
    public string SharedImages { get; init; } = string.Empty;

    [JsonPropertyName("useDeviceFonts")]
    public int UseDeviceFonts { get; init; }

    [JsonPropertyName("display2d")]
    public int Display2d { get; init; }

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("userProfileImage")]
    public string UserProfileImage { get; init; } = string.Empty;

    [JsonPropertyName("autoStartEnabled")]
    public int AutoStartEnabled { get; init; }

    [JsonPropertyName("mapID")]
    public string MapId { get; init; } = string.Empty;

    // Local mesh extension; the captured host remains the HTTPS web origin.
    [JsonPropertyName("gameServerIP")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameServerIp { get; init; }

    [JsonPropertyName("hangars")]
    public UnityHangarsEnvelope Hangars { get; init; } = new();

    [JsonPropertyName("serverInfo")]
    public UnityServerInfo ServerInfo { get; init; } = new();

    [JsonPropertyName("allowChat")]
    public string AllowChat { get; init; } = string.Empty;

    [JsonPropertyName("nextAction")]
    public string NextAction { get; init; } = string.Empty;
}

public sealed record UnityHangarsEnvelope
{
    [JsonPropertyName("data")]
    public UnityHangarsData Data { get; init; } = new();
}

public sealed record UnityHangarsData
{
    [JsonPropertyName("hangars")]
    public IReadOnlyList<UnityHangar> Hangars { get; init; } = [];

    [JsonPropertyName("faction")]
    public string Faction { get; init; } = string.Empty;
}

public sealed record UnityHangar
{
    [JsonPropertyName("slotNumber")]
    public int SlotNumber { get; init; }

    [JsonPropertyName("shipId")]
    public string ShipId { get; init; } = string.Empty;

    [JsonPropertyName("modelId")]
    public int ModelId { get; init; }

    [JsonPropertyName("hangarId")]
    public string HangarId { get; init; } = string.Empty;

    [JsonPropertyName("shipName")]
    public string ShipName { get; init; } = string.Empty;

    [JsonPropertyName("mapId")]
    public string MapId { get; init; } = string.Empty;

    [JsonPropertyName("shipDestroyed")]
    public bool ShipDestroyed { get; init; }

    [JsonPropertyName("hangarActive")]
    public bool HangarActive { get; init; }

    [JsonPropertyName("tooltipIcon")]
    public string TooltipIcon { get; init; } = string.Empty;
}

public sealed record UnityServerInfo
{
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = string.Empty;

    [JsonPropertyName("serverTime")]
    public string ServerTime { get; init; } = string.Empty;

    [JsonPropertyName("serverDate")]
    public string ServerDate { get; init; } = string.Empty;

    [JsonPropertyName("timeStamp")]
    public long TimeStamp { get; init; }

    [JsonPropertyName("localizedTs")]
    public long LocalizedTs { get; init; }
}
