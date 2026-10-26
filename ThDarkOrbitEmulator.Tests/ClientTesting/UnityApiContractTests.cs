using System.Text.Json;
using System.Text.Json.Nodes;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News.Contracts;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class UnityApiContractTests
{
    [Test]
    public void Login_DeserializesCapturedWireShape()
    {
        var wire = Fixture("unity-login.json");
        var outer = JsonSerializer.Deserialize<UnityLoginCapturedResponse>(wire)!;
        var login = JsonSerializer.Deserialize<UnityLoginData>(outer.Data)!;

        Assert.Multiple(() =>
        {
            Assert.That(login.UserId, Is.EqualTo("example"));
            Assert.That(login.SessionId, Is.EqualTo("example"));
            Assert.That(login.LanguageXmlHash, Is.Null);
            Assert.That(login.ShowAdvertisingHint, Is.True);
            Assert.That(login.Hangars.Data.Hangars, Has.Count.EqualTo(1));
            Assert.That(login.Hangars.Data.Hangars[0].ModelId, Is.EqualTo(1));
            Assert.That(login.ServerInfo.TimeStamp, Is.EqualTo(1));
        });
        AssertSameJson(outer.Data, JsonSerializer.Serialize(login));
        AssertSameJson(wire, JsonSerializer.Serialize(outer));
    }

    [Test]
    public void Links_DeserializesCapturedWireShape()
    {
        var wire = Fixture("unity-links.json");
        var links = JsonSerializer.Deserialize<UnityLinksResponse>(wire)!;

        Assert.Multiple(() =>
        {
            Assert.That(links.Imprint, Is.EqualTo("example"));
            Assert.That(links.Credits, Is.EqualTo("example"));
            Assert.That(links.Forum, Is.EqualTo("example"));
            Assert.That(links.Support, Is.EqualTo("example"));
            Assert.That(links.Help, Is.EqualTo("example"));
            Assert.That(links.AccountCenter, Is.EqualTo("example"));
        });
        AssertSameJson(wire, JsonSerializer.Serialize(links));
    }

    [Test]
    public void News_DeserializesCapturedWireShape()
    {
        var wire = Fixture("unity-news.json");
        var news = JsonSerializer.Deserialize<UnityNewsResponse>(wire)!;

        Assert.That(news.Data, Has.Count.EqualTo(1));
        Assert.That(news.Data[0].NewsId, Is.EqualTo("example"));
        Assert.That(news.Data[0].Seconds, Is.EqualTo(1));
        AssertSameJson(wire, JsonSerializer.Serialize(news));
    }

    [Test]
    public void Login_AcceptsPresentLanguageHashInSyntheticPayload()
    {
        var outer = JsonSerializer.Deserialize<UnityLoginCapturedResponse>(Fixture("unity-login.json"))!;
        var login = JsonSerializer.Deserialize<UnityLoginData>(outer.Data)!;
        var variant = login with { LanguageXmlHash = "synthetic-hash" };

        var serialized = JsonSerializer.Serialize(variant);
        Assert.That(JsonSerializer.Deserialize<UnityLoginData>(serialized)!.LanguageXmlHash,
            Is.EqualTo("synthetic-hash"));
        Assert.That(JsonNode.Parse(serialized)!["languageXmlHash"]!.GetValue<string>(),
            Is.EqualTo("synthetic-hash"));
    }

    [Test]
    public void News_AcceptsEmptyDataArray()
    {
        const string wire = """{"data":[]}""";
        var news = JsonSerializer.Deserialize<UnityNewsResponse>(wire)!;

        Assert.That(news.Data, Is.Empty);
        AssertSameJson(wire, JsonSerializer.Serialize(news));
    }

    private static void AssertSameJson(string expected, string actual)
    {
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), Is.True,
            "DTO serialization must preserve the captured field names, nesting, and JSON types.");
    }

    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "ThDarkOrbitEmulator.Tests", "ClientTesting", "Fixtures", name);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate fixture {name}.");
    }
}
