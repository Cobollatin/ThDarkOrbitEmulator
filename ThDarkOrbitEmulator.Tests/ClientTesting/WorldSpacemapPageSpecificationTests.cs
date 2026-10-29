using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class WorldSpacemapPageSpecificationTests
{
    private static readonly string Html = LocalSpacemapPage.Html;

    [Test]
    public void Page_TitleIsGenericPlaceholderAndAppearsExactlyOnce()
    {
        var titles = Regex.Matches(Html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.Multiple(() =>
        {
            Assert.That(titles, Has.Count.EqualTo(1));
            Assert.That(titles[0].Groups[1].Value, Is.EqualTo("Spacemap - Space Emulator (Placeholder)"));
            Assert.That(Html, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(Html, Does.Not.Contain("Bigpoint").IgnoreCase);
            Assert.That(Html, Does.Not.Contain("bpsecure").IgnoreCase);
        });
    }

    [Test]
    public void Page_IsOneSelfContainedHtmlDocument()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.StartWith("<!doctype html><html lang=\"en\">"));
            Assert.That(Html.TrimEnd(), Does.EndWith("</html>"));
            Assert.That(Regex.Matches(Html, "<html\\b").Count, Is.EqualTo(1));
            Assert.That(Regex.Matches(Html, "<script\\b").Count, Is.EqualTo(1));
            Assert.That(Html, Does.Contain("<meta charset=\"utf-8\">"));
        });
    }

    [Test]
    public void Page_LoadsNoExternalResources()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Not.Contain("http://"));
            Assert.That(Html, Does.Not.Contain("https://"));
            Assert.That(Html, Does.Not.Contain(" src="));
            Assert.That(Html, Does.Not.Contain("<link"));
            Assert.That(Html, Does.Not.Contain("@import"));
        });
    }

    [Test]
    public void Page_ElementIdsAreUnique()
    {
        var ids = Regex.Matches(Html, "\\sid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.That(ids, Is.Not.Empty);
        Assert.That(ids, Is.Unique);
    }

    [TestCase("spaceCanvas")]
    [TestCase("minimap")]
    [TestCase("mapName")]
    [TestCase("pilotName")]
    [TestCase("shipName")]
    [TestCase("factionBadge")]
    [TestCase("position")]
    [TestCase("destination")]
    [TestCase("safeZone")]
    [TestCase("pickupRange")]
    [TestCase("pilotCount")]
    [TestCase("npcCount")]
    [TestCase("hpFill")]
    [TestCase("shieldFill")]
    [TestCase("shipSpeed")]
    [TestCase("shipShield")]
    [TestCase("shipLaser")]
    [TestCase("config1")]
    [TestCase("config2")]
    [TestCase("jump")]
    [TestCase("target")]
    [TestCase("fire")]
    [TestCase("pulse")]
    [TestCase("announcement")]
    [TestCase("status")]
    [TestCase("warning")]
    [TestCase("adminPanel")]
    [TestCase("adminCommand")]
    [TestCase("adminResult")]
    [TestCase("adminVariable")]
    [TestCase("adminValue")]
    [TestCase("adminReason")]
    [TestCase("adminHistory")]
    [TestCase("adminRefresh")]
    [TestCase("adminMore")]
    public void Page_DeclaresEveryScriptBoundElement(string id)
    {
        Assert.That(Html, Does.Contain($"id=\"{id}\""));
        Assert.That(Html, Does.Contain($"'{id}'").Or.Contain($"'{id[..^1]}'+n"),
            "Every declared control must be bound by the page script.");
    }

    [Test]
    public void Page_MenuLinkReturnsToTheInternalWebGlStartPage()
    {
        Assert.That(Html, Does.Contain(
            "<a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Menu</a>"));
    }

    [Test]
    public void Page_OffersEverySupportedRepairChoiceOnlyWhenEligible()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("id=\"respawnPortal\" type=\"button\" hidden"));
            Assert.That(Html, Does.Contain("['respawnPortal','portal','nearest gate']"));
            Assert.That(Html, Does.Contain("document.getElementById('respawnPortal').hidden=p.hp>0||state.map.id>=101||!state.map.portals.length"));
            Assert.That(Html, Does.Contain("request('respawn','POST',{location})"));
        });
    }

    [Test]
    public void Page_WorldRequestsAreSameOriginNoStoreAndRootedAtWorldBridge()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("fetch('/spacemap/world/'+path,{method,credentials:'same-origin'"));
            Assert.That(Html, Does.Contain("fetch('/spacemap/world/admin/'+path,{method,credentials:'same-origin'"));
            Assert.That(Regex.Matches(Html, "fetch\\(").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(Html, "cache:'no-store'").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(Html, "headers:body\\?\\{'Content-Type':'application/json'\\}:\\{\\}").Count,
                Is.EqualTo(2));
        });
    }

    [TestCase("request('enter','POST')")]
    [TestCase("request('state')")]
    [TestCase("request('move','POST',{...command.body,expectedMoveId:state.pilot.moveId})")]
    [TestCase("request('jump','POST',{portalId:gate.id,mapId:state.map.id})")]
    [TestCase("request('collect','POST',{boxId:box.id,mapId:state.map.id})")]
    [TestCase("request('target','POST',{npcId:command.id,mapId:command.mapId})")]
    [TestCase("request('fire','POST',{npcId,mapId})")]
    [TestCase("request('pulse','POST',{mapId,actionId})")]
    [TestCase("request('config','POST',{configuration:command.configuration,mapId:command.mapId})")]
    public void Page_UsesDocumentedWorldBridgeCommandShapes(string call)
    {
        Assert.That(Html, Does.Contain(call));
    }

    [Test]
    public void Page_PulseCanBeUsedWithoutASelectedTarget_AndShowsServerResult()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("document.getElementById('pulse').disabled=p.hp<=0||state.map.id>=101||inside"));
            Assert.That(Html, Does.Contain("if(!state||pulsePending||document.getElementById('pulse').disabled)return"));
            Assert.That(Html, Does.Contain("result.hits.length?'Pulse hit '"));
            Assert.That(Html, Does.Contain("e.status==='cooldown'?'Pulse is recharging.'"));
            Assert.That(Html, Does.Contain("e.status==='out_of_ammo'?'Buy pulse charges in the shop.'"));
            Assert.That(Html, Does.Contain("sessionStorage.getItem('pulseActionId')||crypto.randomUUID()"));
        });
    }

    [TestCase("adminRequest('vars')")]
    [TestCase("adminRequest('vars/history?limit=20'+(historyBefore?'&beforeChangeId='+historyBefore:''))")]
    [TestCase("adminRequest('vars/rollback','POST',{changeId:row.changeId,reason})")]
    [TestCase("adminRequest('command','POST',{text:command})")]
    public void Page_UsesDocumentedAdminBridgeCommandShapes(string call)
    {
        Assert.That(Html, Does.Contain(call));
    }

    [TestCase("map_lease_changed", "Map ownership changed. Try again.")]
    [TestCase("map_changed", "Map changed. Choose a new destination.")]
    [TestCase("move_changed", "Destination changed in another client. Choose it again.")]
    [TestCase("distant", "Target out of laser range. Move closer.")]
    public void Page_MapsKnownConflictStatusesToFixedMessages(string status, string message)
    {
        Assert.That(Html, Does.Contain($"{status}:'{message}'"));
    }

    [Test]
    public void Page_ConflictFallbackUsesRawStatusThenCooldownSeconds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain(
                "messages[conflict.status]||conflict.status||('Configuration ready in '+Math.ceil(conflict.cooldownRemainingMs/1000)+'s.')"));
            Assert.That(Html, Does.Contain("error.status=conflict.status;throw error;"));
        });
    }

    [Test]
    public void Page_NonConflictFailuresDistinguishExpiredSessionsFromOtherStatuses()
    {
        Assert.That(Html, Does.Contain(
            "response.status===401?'Session expired. Return to the menu and sign in.':'World request failed ('+response.status+').'"));
    }

    [Test]
    public void Page_AdminFailuresPreferServerErrorTextAndKeepHttpStatus()
    {
        Assert.That(Html, Does.Contain(
            "if(!response.ok){const error=new Error(result.error||'Admin request failed ('+response.status+').');error.status=response.status;throw error;}"));
    }

    [Test]
    public void Page_AdminPanelStartsHiddenAndOpensOnlyForAdminPilotsAfterEnter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("<section id=\"adminPanel\" hidden>"));
            Assert.That(Regex.Matches(Html, "getElementById\\('adminPanel'\\)\\.hidden=").Count, Is.EqualTo(1));
            Assert.That(Html, Does.Contain(
                "request('enter','POST').then(s=>{state=s;render();if(s.pilot.isAdmin){document.getElementById('adminPanel').hidden=false;"));
        });
    }

    [Test]
    public void Page_AdminInputsDeclareClientSideLengthAndRequiredBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("<input id=\"adminCommand\" maxlength=\"1024\" placeholder=\"/help\">"));
            Assert.That(Html, Does.Contain("<input id=\"adminReason\" maxlength=\"256\" required aria-label=\"Reason\">"));
        });
    }

    [Test]
    public void Page_VariableChangesAndRollbacksRequireTrimmedReasonBeforeAnyRequest()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain(
                "if(!reason){text('adminResult','Enter a reason before changing a variable.');return;}"));
            Assert.That(Html, Does.Contain(
                "if(!reason){text('adminResult','Enter a reason before rolling back.');return;}"));
            Assert.That(Regex.Matches(Html, "getElementById\\('adminReason'\\)\\.value\\.trim\\(\\)").Count,
                Is.EqualTo(2));
        });
    }

    [Test]
    public void Page_VariableSetIsSentAsQuotedSlashCommand()
    {
        Assert.That(Html, Does.Contain(
            "runAdmin('/var set '+document.getElementById('adminVariable').value+' '+JSON.stringify(document.getElementById('adminValue').value)+' '+JSON.stringify(reason))"));
    }

    [Test]
    public void Page_HistoryPagesByTwentyUsingLastChangeIdCursor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("if(rows.length)historyBefore=rows[rows.length-1].changeId;"));
            Assert.That(Html, Does.Contain("document.getElementById('adminMore').hidden=rows.length<20;"));
            Assert.That(Html, Does.Contain("if(!more)historyBefore=null;"));
        });
    }

    [Test]
    public void Page_PollsStateEvery150MillisecondsAndSkipsOverlappingPolls()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("setInterval(refresh,150);"));
            Assert.That(Regex.Matches(Html, "setInterval\\(").Count, Is.EqualTo(1));
            Assert.That(Html, Does.Contain("async function refresh(){if(busy||pendingMoves)return;busy=true;"));
        });
    }

    [Test]
    public void Page_PickupRadiusFallsBackTo180WhenStateOmitsCollectionRadius()
    {
        Assert.That(Regex.Matches(Html, "state\\.collectionRadius\\?\\?180").Count, Is.EqualTo(2));
    }

    [Test]
    public void Page_ConfigurationButtonsStartWithConfigurationOneSelected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain("<button id=\"config1\" type=\"button\" aria-pressed=\"true\">1</button>"));
            Assert.That(Html, Does.Contain("<button id=\"config2\" type=\"button\" aria-pressed=\"false\">2</button>"));
            Assert.That(Html, Does.Contain("setAttribute('aria-pressed',String(p.configuration===n))"));
        });
    }

    [Test]
    public void Page_ClickCoordinatesAreClampedToMapBoundsAndRounded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain(
                "x=Math.round(Math.max(0,Math.min(1,(event.clientX-r.left)/r.width))*state.map.width)"));
            Assert.That(Html, Does.Contain(
                "y=Math.round(Math.max(0,Math.min(1,(event.clientY-r.top)/r.height))*state.map.height)"));
        });
    }

    [Test]
    public void Page_ClickPrefersNpcTargetingOverBoxPickupWithinFixedWorldRadii()
    {
        var npcIndex = Html.IndexOf("Math.hypot(n.x-x,n.y-y)<=Math.max(260,15*state.map.width/c.width)", StringComparison.Ordinal);
        var boxIndex = Html.IndexOf("Math.hypot(b.x-x,b.y-y)<=Math.max(250,13*state.map.width/c.width)", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(npcIndex, Is.GreaterThan(0));
            Assert.That(boxIndex, Is.GreaterThan(npcIndex));
            Assert.That(Html, Does.Contain("if(npc){queuedMove=null;pendingBox=null;queuedTarget="));
        });
    }

    [Test]
    public void Page_JumpTargetsNearestPortalAndBlocksConcurrentJumps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain(
                "gate=state.map.portals.reduce((best,item)=>!best||Math.hypot(item.x-p.x,item.y-p.y)<Math.hypot(best.x-p.x,best.y-p.y)?item:best,null);if(!gate)return;"));
            Assert.That(Html, Does.Contain("if(!state||jumpPending)return;"));
            Assert.That(Html, Does.Contain("finally{jumpPending=false;button.disabled=false;}"));
        });
    }

    [Test]
    public void Page_MapChangeResetsQueuedCommandsSelectionAndPendingPickup()
    {
        Assert.That(Html, Does.Contain(
            "if(state&&next.map.id!==state.map.id){mapEpoch++;queuedMove=null;queuedTarget=null;queuedConfig=null;pendingBox=null;targetId=null;"));
    }

    [Test]
    public void Page_RewardSummaryListsOnlyPositiveCurrenciesInFixedOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Contain(
                "[['credits',reward.credits],['uridium',reward.uridium],['XP',reward.experience],['honor',reward.honor]].filter(([,amount])=>amount>0)"));
            Assert.That(Html, Does.Contain("'Destroyed '+hit.target.kind+'; earned '+(earned||'no currency')+'.'"));
        });
    }

    [Test]
    public void Page_RendersWorldTextOnlyThroughTextContent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Html, Does.Not.Contain("innerHTML"));
            Assert.That(Html, Does.Not.Contain("outerHTML"));
            Assert.That(Html, Does.Not.Contain("insertAdjacentHTML"));
            Assert.That(Html, Does.Not.Contain("document.write"));
            Assert.That(Html, Does.Not.Contain("eval("));
            Assert.That(Html, Does.Contain("const text=(id,value)=>document.getElementById(id).textContent=value;"));
        });
    }
}
