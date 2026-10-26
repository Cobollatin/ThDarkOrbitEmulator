using System.Text.Json;
using System.Text.Json.Nodes;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class CandidateEndpointContractTests
{
    [Test]
    public void SpaceSearchCandidates_EmitOnlyInferredFieldNames()
    {
        var search = JsonNode.Parse(JsonSerializer.Serialize(new SpaceSearchResponse()))!;
        Assert.That(search.AsObject().Select(field => field.Key).Order(),
            Is.EqualTo(new[] { "planets", "events", "rewards" }.Order()));
        Assert.That(search["planets"]!.AsArray(), Is.Empty);

        var reward = JsonNode.Parse(JsonSerializer.Serialize(new SpaceSearchRewardResponse()))!;
        Assert.That(reward.AsObject().Select(field => field.Key).Order(),
            Is.EqualTo(new[] { "name", "special", "time", "item", "title", "package" }.Order()));
        Assert.That(reward["special"]!.GetValue<bool>(), Is.False);
    }

    [Test]
    public void UnverifiedRoutes_DoNotClaimAnUploadSucceeded()
    {
        var upload = JsonNode.Parse(JsonSerializer.Serialize(new UploadResponse()))!;
        Assert.That(upload["result"]!.GetValue<bool>(), Is.False);
        Assert.That(upload["status"]!.GetValue<int>(), Is.EqualTo(501));
        Assert.That(upload["error"]!.GetValue<string>(), Is.Not.Empty);

        Assert.That(new PaymentResponse { Url = "/indexInternal.es?action=internalPayment" }.Url,
            Does.StartWith("/indexInternal.es"));
    }

    [Test]
    public void GalaxyGateCandidate_ListsTheLocalBuilderCatalog()
    {
        var gates = JsonNode.Parse(JsonSerializer.Serialize(
            GalaxyGateRewardsEndpoint.LocalCatalogResponse()))!;
        Assert.That(gates.AsObject().Select(field => field.Key), Is.EqualTo(new[] { "data" }));
        Assert.That(gates["data"]!.AsArray().Select(item => item!.GetValue<string>()),
            Is.EqualTo(LocalGalaxyGates.Catalog.Select(gate => gate.Id)));
    }

    [Test]
    public void GalaxyGateCandidate_ProjectsOnlyStableIdentifiersIndependentOfProgress()
    {
        var response = GalaxyGateRewardsEndpoint.LocalCatalogResponse();
        var first = JsonNode.Parse(JsonSerializer.Serialize(response))!;
        Assert.Multiple(() =>
        {
            Assert.That(first.AsObject().Select(field => field.Key), Is.EqualTo(new[] { "data" }));
            Assert.That(first["data"]!.AsArray().Select(item => item!.GetValue<string>()),
                Is.EqualTo(LocalGalaxyGates.Catalog.Select(gate => gate.Id)));
            Assert.That(first.ToJsonString(), Does.Not.Contain("PartsOwned").And.Not.Contain("Activated")
                .And.Not.Contain("Energy"));
            Assert.That(response.Data.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(response.Data.Count));
        });
    }

    [Test]
    public void PaymentCandidate_UsesOnlyTheRelativeLocalPaymentRoute()
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(new PaymentResponse
        {
            Url = "/indexInternal.es?action=internalPayment"
        }))!;
        Assert.Multiple(() =>
        {
            Assert.That(body.AsObject().Select(field => field.Key), Is.EqualTo(new[] { "url" }));
            Assert.That(body["url"]!.GetValue<string>(), Is.EqualTo("/indexInternal.es?action=internalPayment"));
            Assert.That(Uri.TryCreate(body["url"]!.GetValue<string>(), UriKind.Absolute, out _), Is.False);
        });
    }

    [Test]
    public void UploadCandidate_ProvidesExactUnavailableBody()
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(new UploadResponse()))!;
        Assert.Multiple(() =>
        {
            Assert.That(body.AsObject().Select(field => field.Key),
                Is.EquivalentTo(new[] { "result", "type", "error", "status" }));
            Assert.That(body["result"]!.GetValue<bool>(), Is.False);
            Assert.That(body["type"]!.GetValue<string>(), Is.Empty);
            Assert.That(body["error"]!.GetValue<string>(), Is.EqualTo("Upload unavailable in emulator"));
            Assert.That(body["status"]!.GetValue<int>(), Is.EqualTo(501));
        });
    }
}
