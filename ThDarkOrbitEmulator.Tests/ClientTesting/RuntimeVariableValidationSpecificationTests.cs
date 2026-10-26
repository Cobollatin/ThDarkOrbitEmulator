using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-08: write-path validation gates of RuntimeVariableStore, checked without PostgreSQL.
// Key and attribution checks run before any connection is opened; a store without an
// identity-write connection string therefore fails with InvalidOperationException only after
// both gates accept the request.
public sealed class RuntimeVariableValidationSpecificationTests
{
    private static readonly RuntimeVariableActor ValidActor = new("ServerManagement", "operator", "node-a", "review");

    private static RuntimeVariableStore Store(IDictionary<string, string?>? settings = null, IDistributedCache? cache = null) =>
        new(cache!, new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());

    private static string Normalize(string value, string type)
    {
        var method = typeof(RuntimeVariableStore).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { return (string)method.Invoke(null, [value, type])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    private static void ValidateRange(RuntimeVariableStore store, string key, string value)
    {
        var method = typeof(RuntimeVariableStore).GetMethod("ValidateRange", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try { method.Invoke(store, [key, value]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    [TestCase("world")]
    [TestCase("World.tick_rate_ms")]
    [TestCase("world.Tick")]
    [TestCase("1world.tick")]
    [TestCase("world.")]
    [TestCase(".world")]
    [TestCase("world._tick")]
    [TestCase("world.1tick")]
    [TestCase("world.tick-rate")]
    [TestCase("my_world.tick")]
    [TestCase("world..tick")]
    [TestCase("world.tick ")]
    [TestCase("")]
    public void SetAsync_RejectsKeysOutsideTheDottedLowercaseGrammar(string key)
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await Store().SetAsync(key, "1", ValidActor));
        Assert.That(ex!.ParamName, Is.EqualTo("key"));
    }

    [Test]
    public void SetAsync_RejectsKeysLongerThan128Characters()
    {
        var key = "a." + new string('b', 127);
        Assert.That(key, Has.Length.EqualTo(129));
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await Store().SetAsync(key, "1", ValidActor));
        Assert.That(ex!.ParamName, Is.EqualTo("key"));
        var atLimit = "a." + new string('b', 126);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Store().SetAsync(atLimit, "1", ValidActor));
    }

    [TestCase("a.b")]
    [TestCase("a1.b_2")]
    [TestCase("world.tick_rate_ms")]
    [TestCase("custom.feature.flag_1")]
    public void SetAsync_AcceptedKeysPassValidationAndRequireTheWriteConnection(string key)
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await Store().SetAsync(key, "1", ValidActor));
        Assert.That(ex!.Message, Does.Contain("identity-write"));
    }

    [TestCase("ServerManagement", true)]
    [TestCase("AdminCms", true)]
    [TestCase("InGameAdmin", true)]
    [TestCase("servermanagement", false)]
    [TestCase("Backoffice", false)]
    [TestCase("", false)]
    public void SetAsync_AcceptsOnlyTheThreeDocumentedSurfaces(string surface, bool accepted)
    {
        var actor = ValidActor with { Surface = surface };
        if (accepted)
            Assert.ThrowsAsync<InvalidOperationException>(async () => await Store().SetAsync("world.tick_rate_ms", "100", actor));
        else
            Assert.That(Assert.ThrowsAsync<ArgumentException>(async () =>
                await Store().SetAsync("world.tick_rate_ms", "100", actor))!.ParamName, Is.EqualTo("actor"));
    }

    [TestCase("", "node-a", "review")]
    [TestCase("  ", "node-a", "review")]
    [TestCase("operator", "", "review")]
    [TestCase("operator", "node-a", " ")]
    public void SetAsync_RequiresChangedByNodeAndReason(string changedBy, string node, string reason)
    {
        var actor = ValidActor with { ChangedBy = changedBy, NodeId = node, Reason = reason };
        var ex = Assert.ThrowsAsync<ArgumentException>(async () => await Store().SetAsync("world.tick_rate_ms", "100", actor));
        Assert.That(ex!.ParamName, Is.EqualTo("actor"));
    }

    [Test]
    public void ResetToDefault_ResolvesTheDefaultThenAppliesTheSameAttributionGate()
    {
        // Reset reads the default through GetAsync (a cache hit here) and then enters the shared write path.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        cache.SetString("runtime-variable:v1:world.tick_rate_ms",
            JsonSerializer.Serialize(new RuntimeVariable("world.tick_rate_ms", "World", "int", "1000", "750")));
        var actor = ValidActor with { Surface = "Unknown" };
        Assert.ThrowsAsync<ArgumentException>(async () => await Store(cache: cache).ResetToDefaultAsync("world.tick_rate_ms", actor));
    }

    [Test]
    public async Task GetAndNumber_ServeCachedValuesWithoutTouchingPostgres()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        cache.SetString("runtime-variable:v1:world.movement.speed_multiplier",
            JsonSerializer.Serialize(new RuntimeVariable("world.movement.speed_multiplier", "Movement", "double", "1", "2.5")));
        var store = Store(cache: cache);
        var value = await store.GetAsync("world.movement.speed_multiplier");
        Assert.That((value.CurrentValue, value.DefaultValue, value.ValueType), Is.EqualTo(("2.5", "1", "double")));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-PE");
            Assert.That(await store.NumberAsync("world.movement.speed_multiplier"), Is.EqualTo(2.5));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestCase("int", "750", "750")]
    [TestCase("int", "007", "7")]
    [TestCase("int", "+5", "5")]
    [TestCase("int", " 42 ", "42")]
    [TestCase("int", "-1000000", "-1000000")]
    [TestCase("int", "1000000", "1000000")]
    [TestCase("double", "2", "2")]
    [TestCase("double", "0.1", "0.1")]
    [TestCase("double", "1e3", "1000")]
    [TestCase("double", "-1000000", "-1000000")]
    [TestCase("double", "1000000.0", "1000000")]
    [TestCase("string", "", "")]
    [TestCase("string", "Mixed Case 1,5", "Mixed Case 1,5")]
    public void Normalize_ProducesCanonicalInvariantText(string type, string input, string expected) =>
        Assert.That(Normalize(input, type), Is.EqualTo(expected));

    [TestCase("int", "1.5")]
    [TestCase("int", "1e3")]
    [TestCase("int", "1000001")]
    [TestCase("int", "-1000001")]
    [TestCase("int", "abc")]
    [TestCase("int", "")]
    [TestCase("double", "1,5")]
    [TestCase("double", "NaN")]
    [TestCase("double", "Infinity")]
    [TestCase("double", "1000000.5")]
    [TestCase("double", "-1000000.5")]
    [TestCase("bool", "true")]
    public void Normalize_RejectsMalformedOrOversizedValues(string type, string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => Normalize(input, type));
        Assert.That(ex!.ParamName, Is.EqualTo("value"));
    }

    [Test]
    public void Normalize_BoundsStringsAt4096Characters()
    {
        Assert.That(Normalize(new string('s', 4096), "string"), Has.Length.EqualTo(4096));
        Assert.Throws<ArgumentException>(() => Normalize(new string('s', 4097), "string"));
    }

    [TestCase("world.tick_rate_ms", "20", true)]
    [TestCase("world.tick_rate_ms", "19", false)]
    [TestCase("world.tick_rate_ms", "60000", true)]
    [TestCase("world.tick_rate_ms", "60001", false)]
    [TestCase("world.collectibles.respawn_seconds", "1", true)]
    [TestCase("world.collectibles.respawn_seconds", "0", false)]
    [TestCase("world.collectibles.respawn_seconds", "86400", true)]
    [TestCase("world.collectibles.respawn_seconds", "86401", false)]
    [TestCase("world.spawn.streuner_count", "0", true)]
    [TestCase("world.spawn.lordakia_count", "30", true)]
    [TestCase("world.spawn.saimon_count", "31", false)]
    [TestCase("world.spawn.bonus_box_count", "1000", true)]
    [TestCase("world.spawn.bonus_box_count", "1001", false)]
    [TestCase("world.combat.shield_absorption_ratio", "0", true)]
    [TestCase("world.combat.shield_absorption_ratio", "1", true)]
    [TestCase("world.combat.shield_absorption_ratio", "1.01", false)]
    [TestCase("economy.shop_discount_percent", "100", true)]
    [TestCase("economy.shop_discount_percent", "100.5", false)]
    [TestCase("world.movement.speed_multiplier", "0", false)]
    [TestCase("world.movement.speed_multiplier", "0.0001", true)]
    [TestCase("world.movement.speed_multiplier", "100", true)]
    [TestCase("world.movement.speed_multiplier", "100.5", false)]
    [TestCase("mesh.desired_replicas", "0", false)]
    [TestCase("mesh.desired_replicas", "1", true)]
    [TestCase("mesh.desired_replicas", "16", true)]
    [TestCase("mesh.desired_replicas", "17", false)]
    [TestCase("mesh.max_maps_per_node", "0", false)]
    [TestCase("mesh.max_maps_per_node", "256", true)]
    [TestCase("mesh.max_maps_per_node", "257", false)]
    [TestCase("mesh.heartbeat_ttl_seconds", "20", false)]
    [TestCase("mesh.heartbeat_ttl_seconds", "21", true)]
    [TestCase("mesh.heartbeat_ttl_seconds", "3600", true)]
    [TestCase("mesh.heartbeat_ttl_seconds", "3601", false)]
    [TestCase("world.combat.fire_range", "1000000", true)]
    [TestCase("economy.xp_reward_multiplier", "0", true)]
    public void ValidateRange_EnforcesDocumentedBoundsForKnownKeys(string key, string value, bool valid)
    {
        if (valid) Assert.DoesNotThrow(() => ValidateRange(Store(), key, value));
        else Assert.Throws<ArgumentOutOfRangeException>(() => ValidateRange(Store(), key, value));
    }

    [Test]
    public void ValidateRange_RejectsNegativesForEveryCatalogKey()
    {
        var store = Store();
        foreach (var key in store.Catalog.Select(v => v.Key))
            Assert.Throws<ArgumentOutOfRangeException>(() => ValidateRange(store, key, "-1"), key);
    }

    [Test]
    public void ValidateRange_AllowsNegativeAndNonNumericDynamicValues()
    {
        var store = Store();
        Assert.DoesNotThrow(() => ValidateRange(store, "custom.offset", "-5"));
        Assert.DoesNotThrow(() => ValidateRange(store, "custom.label", "hello"));
        Assert.DoesNotThrow(() => ValidateRange(store, "world.tick_rate_ms", "not-a-number"));
    }

    [TestCase("5", "10", false)]
    [TestCase("5", "11", true)]
    [TestCase("30", "60", false)]
    [TestCase("30", "61", true)]
    public void HeartbeatTtl_MustExceedTwiceTheConfiguredHeartbeatInterval(string interval, string ttl, bool valid)
    {
        var store = Store(new Dictionary<string, string?> { ["Cluster:HeartbeatIntervalSeconds"] = interval });
        if (valid) Assert.DoesNotThrow(() => ValidateRange(store, "mesh.heartbeat_ttl_seconds", ttl));
        else Assert.Throws<ArgumentOutOfRangeException>(() => ValidateRange(store, "mesh.heartbeat_ttl_seconds", ttl));
    }
}
