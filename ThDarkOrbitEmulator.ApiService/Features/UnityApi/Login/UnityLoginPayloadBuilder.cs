using System.Text.Json;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

internal static class UnityLoginPayloadBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Build(GameAuthSession session, HttpRequest request)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var host = EmulatorRedirectHelper.IsEmulatorHost(request)
            ? $"{request.Scheme}://{request.Host.Value}"
            : "https://gbl3.darkorbit.com";

        var payload = new Dictionary<string, object?>
        {
            ["lang"] = "es",
            ["userID"] = session.GameUserId.ToString(),
            ["sessionID"] = session.Dosid,
            ["basePath"] = "spacemap",
            ["pid"] = "1398",
            ["opid"] = "1398",
            ["host"] = host,
            ["allowChat"] = "1",
            ["nextAction"] = "internalWebGL",
            ["platform"] = "UNITY_CLIENT",
            ["serverInfo"] = new
            {
                instanceName = "Global 3 - Titan",
                serverTime = DateTime.UtcNow.ToString("HH:mm"),
                serverDate = DateTime.UtcNow.ToString("dd.MM.yyyy"),
                timeStamp = timestamp,
                localizedTs = timestamp
            },
            ["UnknownBoardLink"] = "Unknown",
            ["UnknownHelpLink"] = "Unknown",
            ["UnknownChatHost"] = "Unknown",
            ["UnknownCdn"] = "Unknown",
            ["UnknownHangars"] = "Unknown",
            ["UnknownItemXmlHash"] = "Unknown",
            ["UnknownResourcesXmlHash"] = "Unknown"
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
