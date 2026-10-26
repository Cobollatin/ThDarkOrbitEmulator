using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.ApiService.Features;

// Synthetic bodies shaped by the captured XML and HTML response contracts.
public static class ClientStubResponses
{
    public static UnityLinksResponse Links(string host) => new()
    {
        Imprint = "https://legal.bigpoint.com/PE/imprint/es",
        Credits = string.Empty,
        Forum = "/GameAPI.php?action=portal.redirectToBoard",
        Support = $"{host}/indexInternal.es?action=internalSupport",
        Help = "https://board-es.darkorbit.com/",
        AccountCenter = $"{host}/indexInternal.es?action=internalAccount"
    };

    public static UnityNewsResponse News() => CapturedNewsCatalog.News();

    public static string BreakingNews => CapturedNewsCatalog.BreakingNewsXml;

}
