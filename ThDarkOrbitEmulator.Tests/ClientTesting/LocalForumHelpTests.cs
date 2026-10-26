using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalForumHelpTests
{
    [Test]
    public void BothPagesListBrowsableGuidanceAndReturnToMenu()
    {
        Assert.That(LocalForumHelp.Topics.Select(topic => topic.Id),
            Is.EquivalentTo(new[]
            {
                "company", "hangar", "shop", "assembly", "skylab", "tech-factory",
                "gates", "events", "quests", "auction", "clan", "account"
            }));
        foreach (var forum in new[] { true, false })
        {
            var page = LocalForumHelp.Page(forum);
            var action = forum ? "internalForum" : "internalHelp";
            Assert.That(page, Does.Contain(forum ? "id=\"localForum\"" : "id=\"localHelp\""));
            foreach (var topic in LocalForumHelp.Topics)
                Assert.That(page, Does.Contain($"action={action}&amp;topic={topic.Id}"));
            Assert.That(page, Does.Contain("action=internalWebGL&amp;from=internalStart"));
            Assert.That(page, Does.Not.Contain("being built"));
        }
    }

    [Test]
    public void DetailShowsOneTopicAndRejectsUnknownIdentifiers()
    {
        var detail = LocalForumHelp.Page(false, "shop");
        Assert.That(detail, Does.Contain("Shop purchases spend local practice credits"));
        Assert.That(detail, Does.Not.Contain("Use local energy to spin"));
        Assert.That(detail, Does.Contain("action=internalHelp\">All topics"));
        Assert.That(LocalForumHelp.IsTopic("shop"), Is.True);
        Assert.That(LocalForumHelp.IsTopic("invalid"), Is.False);
    }

    [Test]
    public async Task CapturedForumActionServesSameLocalBoardWithSessionGuard()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await GameApiForumResponse.WriteAsync(context.Response, true, false, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(body.Length, Is.Zero);

        await GameApiForumResponse.WriteAsync(context.Response, true, true, CancellationToken.None, "hangar");
        body.Position = 0;
        var html = await new StreamReader(body).ReadToEndAsync();
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(html, Does.Contain("Ships and equipment"));
        Assert.That(html, Does.Contain("id=\"localForum\""));
    }
}
