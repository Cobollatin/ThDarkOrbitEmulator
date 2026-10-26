using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class PreCompanyMenuRedirectTests
{
    [TestCase("internalDock")]
    [TestCase("internalGalaxyGates")]
    [TestCase("internalPayment")]
    public void CapturedMenuActions_RedirectToCompanyChoice(string action)
    {
        var redirect = PreCompanyMenuRedirect.ForAction(action);
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.RequestedAction, Is.EqualTo(action));

        var response = new DefaultHttpContext().Response;
        ClientRedirectResponse.Apply(response, redirect.Location);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
        });
    }

    [TestCase("internalStart")]
    [TestCase("internalWebGL")]
    [TestCase("internalPaymentProxy")]
    [TestCase(null)]
    public void OtherActions_DoNotUseCapturedMenuContract(string? action)
    {
        Assert.That(PreCompanyMenuRedirect.ForAction(action), Is.Null);
        Assert.That(LocalCompanySelection.MenuPage(action), Is.Null);
    }

    [TestCase("internalPayment", "payment", "Payment")]
    public void SelectedCompanyMenuActions_HaveDistinctLocalPages(string action, string pageId, string title)
    {
        var page = LocalCompanySelection.MenuPage(action);
        Assert.That(page, Is.Not.Null);
        Assert.That(page, Does.Contain($"id=\"{pageId}\""));
        Assert.That(page, Does.Contain($"<h1>{title}</h1>"));
        Assert.That(LocalCompanySelection.GamePage("mmo"),
            Does.Contain($"href=\"/indexInternal.es?action={action}\""));
        Assert.That(page, Does.Contain("/indexInternal.es?action=internalWebGL&amp;from=internalStart"));
        Assert.That(page, Does.Contain("action=internalPaymentClaim"));
        Assert.That(LocalCompanySelection.MenuPage(action.ToUpperInvariant()), Is.EqualTo(page));
    }

    [Test]
    public void SelectedCompanyGalaxyGates_HasLocalBuilderPage()
    {
        var page = LocalGalaxyGates.Page(new GameAuthSession { Username = "pilot", Company = "mmo" },
            new LocalGalaxyGateState(20, [new LocalGalaxyGate("alpha", "Alpha", 34, 2, false)]));
        Assert.That(page, Does.Contain("id=\"galaxy-gates\""));
        Assert.That(page, Does.Contain("Parts: 2 / 34"));
        Assert.That(page, Does.Contain("action=internalGalaxyGateSpin"));
        Assert.That(page, Does.Contain("Energy: 20"));
    }

    [Test]
    public void SelectedCompanyDock_ListsOwnedShipsAndActivationForm()
    {
        var page = LocalHangars.Page(new GameAuthSession { GameUserId = 7, Company = "mmo" }, 1);
        Assert.That(page, Does.Contain("id=\"hangar\""));
        Assert.That(page, Does.Contain("<h1>Hangar</h1>"));
        Assert.That(page, Does.Contain("Pusat Plus"));
        Assert.That(page, Does.Contain("Phoenix"));
        Assert.That(page, Does.Contain("method=\"post\""));
        Assert.That(page, Does.Contain("value=\"2\""));
        Assert.That(page, Does.Contain("/indexInternal.es?action=internalWebGL&from=internalStart"));
        Assert.That(LocalCompanySelection.GamePage("mmo"),
            Does.Contain("href=\"/indexInternal.es?action=internalDock\""));
    }
}
