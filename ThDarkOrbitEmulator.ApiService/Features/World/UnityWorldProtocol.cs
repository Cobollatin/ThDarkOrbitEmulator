using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Wire format recovered from the Unity client (GameAssembly.dll, darkorbit-protocol):
// the client's first VersionRequest frame uses a 4-byte big-endian length; every later frame,
// in both directions, uses a 3-byte big-endian length (DarkOrbit.TcpChannel.Send / PacketParser).
// Every module body starts with its 2-byte libcom module id; integer fields are
// bit-rotated per field and 64-bit fields travel as big-endian doubles.
public static class UnityWorldModuleId
{
    public const ushort LoginRequest = 7;
    public const ushort LogoutCancelRequest = 15;
    public const ushort ShipInitializationCommand = 49;
    public const ushort MoveRequest = 82;
    public const ushort VersionRequest = 666;
    public const ushort VersionCommand = 667;
    public const ushort LoginResponse = 19206;
    public const ushort ItemCommand = 2210;
    public const ushort UIFeatureBarCommand = 17638;
    public const ushort UILayoutCommand = 27964;
    public const ushort MessageKeyListWildcardCommand = 30200;
    public const ushort CooldownTypeModule = 92;
    public const ushort MenuItemStatusCommand = 8024;
    public const ushort MenuItemTimerCommand = 8670;
    public const ushort MenuItemInitializationCommand = 9903;
    public const ushort MenuInitializationCommand = 20609;
    public const ushort SlotBarInitializationCommand = 24694;
    public const ushort MenuCategoryInitializationCommand = 37349;
    public const ushort TimerStateModule = 47277;
    public const ushort SlotInfoCommand = 61563;
    public const ushort DisplaySettingsRequest = 266;
    public const ushort WindowSettingsRequest = 268;
    public const ushort UIWindowUpdateRequest = 9246;
    public const ushort SlotBarConfigSetRequest = 31832;
    public const ushort MenuActionRequest = 32474;
    public const ushort WindowItemCommand = 55969;
    public const ushort AssetTypeModule = 146;
    public const ushort BeaconCommand = 4853;
    public const ushort ClanRelationModule = 7010;
    public const ushort AssetCreateCommand = 21957;
    public const ushort JumpRequest = 10;
    public const ushort JumpDeniedCommand = 18;
    public const ushort JumpInitiatedCommand = 19;
    public const ushort JumpgateCreateCommand = 3389;
    public const ushort UserKeyBindingsModule = 800;
    public const ushort UserKeyBindingsUpdate = 801;
    public const ushort AssetInformationModule = 3430;
    public const ushort MapAssetActivationRequest = 4302;
    public const ushort MapAssetActionAvailableCommand = 6530;
    public const ushort HangarShipsCommand = 21507;
    public const ushort HangarShipBriefCommand = 17967;
    public const ushort HangarListRequest = 0xA2C5;
    public const ushort ShopItemListCommand = 0xDF84;
    public const ushort HangarShipDetailRequest = 0xFDAA;
    public const ushort HangarShipDetailCommand = 0xA1C7;
    public const ushort HangarShipEquipmentRequest = 0xDE44;
    public const ushort HangarShipEquipmentCommand = 13231;
    public const ushort HangarShipEquipmentMsg = 20353;
}

// AssetTypeModule.AssetType values used for the home base. AssetCreateHelper.GetBuildTypeId resolves
// them with the faction to lib_assets.xml ids (headquarters_home_mmo, hangar_home_1, questgiver_3, ...).
public enum UnityAssetType : short
{
    QuestGiver = 34,
    CompanyHqHome = 46,
    HangarHome = 48,
    OreTradeHome = 50,
    InstantRepairHome = 53,
}

// MenuItemInitializationCommand.CounterType / ActionStyle (raw shorts).
public enum UnityMenuCounterType : short
{
    None = 0,
    Number = 1,
    Bar = 2,
    Dots = 3,
}

public enum UnityMenuActionStyle : short
{
    OneShot = 0,
    Toggle = 1,
    Selection = 2,
    Timer = 3,
}

public sealed record UnityMenuItem(string Id, long Count, bool Selected = false);

// Items control menu ("itemsControlMenu"): ammo categories plus the slot bars that hold them.
// Category IDs come from ItemsControlMenuConstants; slot bar IDs are matched by
// ItemMenuMenuInitialization.ParseSlotBars, and every slot must reference a category item.
public static class UnityItemsMenu
{
    public static readonly UnityMenuItem[] Lasers =
    [
        new("ammunition_laser_lcb-10", 100000, Selected: true),
        new("ammunition_laser_mcb-25", 50000),
        new("ammunition_laser_mcb-50", 25000),
        new("ammunition_laser_ucb-100", 10000),
        new("ammunition_laser_sab-50", 10000),
    ];

    public static readonly UnityMenuItem[] Rockets =
    [
        new("ammunition_rocket_r-310", 1000, Selected: true),
        new("ammunition_rocket_plt-2026", 500),
        new("ammunition_rocket_plt-2021", 250),
    ];

    public const string StandardSlotBar = "standardSlotBar";
    public const string PremiumSlotBar = "premiumSlotBar";
    public const string ProActionBar = "proActionBar";

    public static readonly IReadOnlyList<(string Category, UnityMenuItem[] Items)> Categories =
        [("lasers", Lasers), ("rockets", Rockets)];

    public static string? CategoryOf(string menuItemId) =>
        Categories.FirstOrDefault(category => category.Items.Any(item => item.Id == menuItemId)).Category;

    public static UnityMenuItem? Find(string menuItemId) =>
        Categories.SelectMany(category => category.Items).FirstOrDefault(item => item.Id == menuItemId);
}

// UIFeatureBarCommand.FeatureBarType; the client maps 1/2/3 to the gameFeaturesBar (left),
// genericFeaturesBar (right) and gameNewFeatureBar (second left row) HUD bars.
public enum UnityFeatureBarType : short
{
    GameFeatureBar = 1,
    GenericFeatureBar = 2,
    GameNewFeatureBar = 3,
}

public static class UnityFeatureMenu
{
    // Item IDs must match window IDs in the client's bundled game.xml; KeyBoardExecuteEvent.Logout
    // and the logout window look up the "logout" item, and UIMainCenterNewSystem.ShowMenuList indexes
    // "hangar" and "stellar_pathfinder" directly (a missing key throws and drops the connection).
    public static readonly string[] GameItems =
        ["user", "ship", "minimap", "spacemap", "log", "chat", "group", "quests", "pet", "refinement", "hangar",
            "stellar_pathfinder"];

    public static readonly string[] GenericItems = ["settings", "help", "logout"];
}

public sealed class UnityWorldFrameWriter(Stream stream)
{
    public const int MaximumBodyBytes = 0xFFFFFF;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Serialized so background status pushes cannot interleave with request replies.
    public async Task WriteAsync(byte[] body, CancellationToken ct = default)
    {
        if (body.Length is < 2 or > MaximumBodyBytes)
            throw new InvalidDataException("Unity world frame has an invalid length.");
        var frame = new byte[3 + body.Length];
        frame[0] = (byte)(body.Length >> 16);
        frame[1] = (byte)(body.Length >> 8);
        frame[2] = (byte)body.Length;
        body.CopyTo(frame, 3);
        await _gate.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }
        finally { _gate.Release(); }
    }
}

public sealed class UnityWorldFrameReader(Stream stream, int maximumBodyBytes = 65536)
{
    // After the initial VersionRequest, TcpChannel.Send frames bodies with a 3-byte big-endian length.
    public async Task<WorldPacket?> ReadAsync(CancellationToken ct = default)
    {
        var header = new byte[3];
        var count = await stream.ReadAsync(header, ct);
        if (count == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(count), ct);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        if (length < 2 || length > maximumBodyBytes)
            throw new InvalidDataException("Invalid Unity world frame length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return new WorldPacket((WorldCommandId)BinaryPrimitives.ReadUInt16BigEndian(body), body.AsMemory(2));
    }
}

public sealed class UnityModuleWriter
{
    private readonly MemoryStream _stream = new();

    public UnityModuleWriter(ushort moduleId) => Short((short)moduleId);

    public UnityModuleWriter Short(short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public UnityModuleWriter Int(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    // The client decodes with rotate-right(raw, bits), so the server rotates left.
    public UnityModuleWriter RotatedInt(int value, int bits) =>
        Int((int)BitOperations.RotateLeft((uint)value, bits));

    public UnityModuleWriter Long(long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public UnityModuleWriter Float(float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public UnityModuleWriter Bool(bool value)
    {
        _stream.WriteByte(value ? (byte)1 : (byte)0);
        return this;
    }

    public UnityModuleWriter Byte(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public UnityModuleWriter String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > short.MaxValue) throw new InvalidDataException("Unity world string is too long.");
        Short((short)bytes.Length);
        _stream.Write(bytes);
        return this;
    }

    // Nested modules are written inline as their own module ID followed by their fields.
    public UnityModuleWriter Module(byte[] module)
    {
        _stream.Write(module);
        return this;
    }

    public UnityModuleWriter ModuleList(IReadOnlyCollection<byte[]> modules)
    {
        if (modules.Count > byte.MaxValue) throw new InvalidDataException("Unity world list is too long.");
        Byte((byte)modules.Count);
        foreach (var module in modules) Module(module);
        return this;
    }

    // A few lists (e.g. SlotBarInitializationCommand.slots) carry a 16-bit count instead.
    public UnityModuleWriter ShortModuleList(IReadOnlyCollection<byte[]> modules)
    {
        if (modules.Count > short.MaxValue) throw new InvalidDataException("Unity world list is too long.");
        Short((short)modules.Count);
        foreach (var module in modules) Module(module);
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();
}

public sealed class UnityModuleReader(ReadOnlyMemory<byte> payload)
{
    private int _offset;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _offset + count > payload.Length) throw new EndOfStreamException();
        var span = payload.Span.Slice(_offset, count);
        _offset += count;
        return span;
    }

    public short Short() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public int Int() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public int RotatedInt(int bits) => (int)BitOperations.RotateRight((uint)Int(), bits);

    public short RotatedShort(int bits)
    {
        var raw = (ushort)Short();
        return (short)((raw >> bits) | (raw << (16 - bits)));
    }

    public bool Bool() => Take(1)[0] != 0;
    public string String() => Encoding.UTF8.GetString(Take((ushort)Short()));
}

public sealed record UnityLoginRequest(int UserId, short FactionId, string SessionId, string Version,
    int InstanceId, bool IsMiniClient)
{
    // LoginRequest.write rotates userID left by 3, factionID (16-bit) left by 7 and instanceId left by 12.
    public static UnityLoginRequest Read(ReadOnlyMemory<byte> payload)
    {
        var reader = new UnityModuleReader(payload);
        return new UnityLoginRequest(reader.RotatedInt(3), reader.RotatedShort(7), reader.String(),
            reader.String(), reader.RotatedInt(12), reader.Bool());
    }
}

public sealed record UnityMoveRequest(int PositionX, int TargetY, int TargetX, int PositionY)
{
    public static UnityMoveRequest Read(ReadOnlyMemory<byte> payload)
    {
        var reader = new UnityModuleReader(payload);
        return new UnityMoveRequest(reader.RotatedInt(11), reader.RotatedInt(17), reader.RotatedInt(13),
            reader.RotatedInt(10));
    }
}

// MapAssetActivationRequest.read: mapAssetId (rotr 5). Sent when an enabled click bubble is clicked.
public sealed record UnityMapAssetActivationRequest(int MapAssetId)
{
    public static UnityMapAssetActivationRequest Read(ReadOnlyMemory<byte> payload) =>
        new(new UnityModuleReader(payload).RotatedInt(5));
}

// MenuActionRequest.write: sourceType (ITEM_BAR=0, SLOT_BAR=1), menuItemId, actionType (SELECT=0, ACTIVATE=1).
public sealed record UnityMenuActionRequest(short SourceType, string MenuItemId, short ActionType)
{
    public static UnityMenuActionRequest Read(ReadOnlyMemory<byte> payload)
    {
        var reader = new UnityModuleReader(payload);
        return new UnityMenuActionRequest(reader.Short(), reader.String(), reader.Short());
    }
}

// UIWindowUpdateRequest.write: itemId, height (rotl 2), width (rotl 10), posX (rotl 1), maximized, posY (rotl 9).
public sealed record UnityWindowUpdateRequest(string ItemId, UnityWindowState State)
{
    public static UnityWindowUpdateRequest Read(ReadOnlyMemory<byte> payload)
    {
        var reader = new UnityModuleReader(payload);
        var itemId = reader.String();
        var height = reader.RotatedInt(2);
        var width = reader.RotatedInt(10);
        var posX = reader.RotatedInt(1);
        var maximized = reader.Bool();
        return new UnityWindowUpdateRequest(itemId, new UnityWindowState(height, width, posX, maximized,
            reader.RotatedInt(9)));
    }
}

// SlotBarConfigSetRequest.write: menuItemId, slotBarIdTo, slotBarIdFrom, slotIdTo (rotl 10), slotIdFrom (rotl 11).
public sealed record UnitySlotBarConfigSetRequest(string MenuItemId, string SlotBarIdTo, string SlotBarIdFrom,
    int SlotIdTo, int SlotIdFrom)
{
    public static UnitySlotBarConfigSetRequest Read(ReadOnlyMemory<byte> payload)
    {
        var reader = new UnityModuleReader(payload);
        return new UnitySlotBarConfigSetRequest(reader.String(), reader.String(), reader.String(),
            reader.RotatedInt(10), reader.RotatedInt(11));
    }
}

// WindowSettingsRequest.write: minimapScale (rotl 18), thirteen strings in declaration order, hideAllWindows.
// Sent whenever the HUD bars are dragged or rotated.
public sealed record UnityWindowSettingsRequest(int MinimapScale, string MainMenuPosition, string BarStatus,
    string GenericFeatureMenuPosition, string GenericFeatureMenuOrder, string GameFeatureMenuPosition,
    string GameFeatureMenuOrder, string SlotMenuPosition, string SlotMenuOrder, string SlotMenuPremiumPosition,
    string SlotMenuPremiumOrder, string SlotMenuProActionPosition, string SlotMenuProActionOrder, string StageSize,
    bool HideAllWindows)
{
    public static UnityWindowSettingsRequest Read(ReadOnlyMemory<byte> payload)
    {
        var r = new UnityModuleReader(payload);
        return new UnityWindowSettingsRequest(r.RotatedInt(18), r.String(), r.String(), r.String(), r.String(),
            r.String(), r.String(), r.String(), r.String(), r.String(), r.String(), r.String(), r.String(),
            r.String(), r.Bool());
    }
}

// DisplaySettingsRequest.write: seventeen display booleans, displayUse2d, eight rotated quality ints
// (rotr 11, 7, 26, 15, 30, 28, 1, 8), then five booleans. Sent at login and when display settings are saved.
// One hangar equipment configuration; lists hold inventory item ids.
public sealed record HangarShipConfig(int SetId, bool Active, IReadOnlyList<int> Lasers, IReadOnlyList<int> Generators);

public sealed record UnityDisplaySettingsRequest(bool Use2D)
{
    private static readonly int[] QualityRotations = [11, 7, 26, 15, 30, 28, 1, 8];

    public static UnityDisplaySettingsRequest Read(ReadOnlyMemory<byte> payload)
    {
        var r = new UnityModuleReader(payload);
        for (var i = 0; i < 17; i++) r.Bool();
        var use2D = r.Bool();
        foreach (var bits in QualityRotations) r.RotatedInt(bits);
        for (var i = 0; i < 5; i++) r.Bool();
        return new UnityDisplaySettingsRequest(use2D);
    }
}

public static class UnityWorldModules
{
    public static byte[] Version(string version, bool equal) =>
        new UnityModuleWriter(UnityWorldModuleId.VersionCommand).String(version).Bool(equal).ToArray();

    // Status 0 makes LoginResponseHandler clear NetConnectionHelper.LoginMode, which flushes the
    // client's queued commands (e.g. MoveRequest) and starts its ping; non-zero shows a login error.
    public static byte[] LoginResponse(short status = 0) =>
        new UnityModuleWriter(UnityWorldModuleId.LoginResponse).Short(status).ToArray();

    // UILayoutCommand.read: a byte-counted list of UIFeatureBarCommand modules. The client's
    // UILayoutCommandHandler builds the HUD feature menus (including the logout button) from it.
    public static byte[] UILayout(UnityWindowSettingsRequest? layout = null) =>
        new UnityModuleWriter(UnityWorldModuleId.UILayoutCommand)
            .ModuleList([
                FeatureBar(UnityFeatureBarType.GameFeatureBar, OrDefault(layout?.GameFeatureMenuOrder, "0"),
                    OrDefault(layout?.GameFeatureMenuPosition, "0,0"), UnityFeatureMenu.GameItems),
                FeatureBar(UnityFeatureBarType.GenericFeatureBar, OrDefault(layout?.GenericFeatureMenuOrder, "0"),
                    OrDefault(layout?.GenericFeatureMenuPosition, "0,0"), UnityFeatureMenu.GenericItems),
            ])
            .ToArray();

    private static string OrDefault(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    // The client reports slot bar positions as one "x,y" screen-percentage pair. SlotBarVo treats an
    // optional second "|x,y" pair as a stick offset, so the reported pair is replayed unchanged.
    private static string SlotBarPosition(string? reported, string fallback) =>
        string.IsNullOrEmpty(reported) ? fallback : reported;

    // UIFeatureBarCommand.read: featureBarOrder (int as string), featureBarType (raw short),
    // featureBarPosition ("x,y"), then a byte-counted list of ItemCommand modules.
    public static byte[] FeatureBar(UnityFeatureBarType type, string order, string position,
        IReadOnlyCollection<string> itemIds) =>
        new UnityModuleWriter(UnityWorldModuleId.UIFeatureBarCommand)
            .String(order)
            .Short((short)type)
            .String(position)
            .ModuleList(itemIds.Select(id => FeatureItem(id)).ToArray())
            .ToArray();

    // ItemCommand.read: visible, toolTip (MessageKeyListWildcardCommand), itemId.
    public static byte[] FeatureItem(string itemId, bool visible = true) =>
        new UnityModuleWriter(UnityWorldModuleId.ItemCommand)
            .Bool(visible)
            .Module(new UnityModuleWriter(UnityWorldModuleId.MessageKeyListWildcardCommand).Byte(0).ToArray())
            .String(itemId)
            .ToArray();

    private static byte[] EmptyToolTip() =>
        new UnityModuleWriter(UnityWorldModuleId.MessageKeyListWildcardCommand).Byte(0).ToArray();

    // MenuInitializationCommand.read: categories (byte list), itemBarPosition, slotBars (byte list).
    // Positions are "x,y|x,y" pairs parsed by ItemMenuMenuInitialization.ParseStringCords.
    public static byte[] ItemsMenu(UnityUiSettings? settings = null)
    {
        settings ??= new UnityUiSettings();
        var layout = settings.Layout;
        byte[][] Slots(string slotBarId) =>
            settings.Slots(slotBarId).Select(slot => SlotInfo(slot.MenuItemId, slot.SlotId)).ToArray();
        UnityMenuItem[] Items(UnityMenuItem[] items) =>
            items.Select(item => item with { Selected = settings.IsSelected(item.Id) }).ToArray();
        return new UnityModuleWriter(UnityWorldModuleId.MenuInitializationCommand)
            .ModuleList(UnityItemsMenu.Categories
                .Select(category => MenuCategory(category.Category, Items(category.Items)))
                .ToArray())
            .String(OrDefault(layout?.MainMenuPosition, "0,80"))
            .ModuleList([
                SlotBar(UnityItemsMenu.StandardSlotBar, OrDefault(layout?.SlotMenuOrder, "0"),
                    SlotBarPosition(layout?.SlotMenuPosition, "50,85|0,40"), Slots(UnityItemsMenu.StandardSlotBar)),
                SlotBar(UnityItemsMenu.PremiumSlotBar, OrDefault(layout?.SlotMenuPremiumOrder, "1"),
                    SlotBarPosition(layout?.SlotMenuPremiumPosition, "50,85|0,80"), Slots(UnityItemsMenu.PremiumSlotBar)),
                SlotBar(UnityItemsMenu.ProActionBar, OrDefault(layout?.SlotMenuProActionOrder, "2"),
                    SlotBarPosition(layout?.SlotMenuProActionPosition, "50,85|0,120"),
                    Slots(UnityItemsMenu.ProActionBar), visible: false),
            ])
            .ToArray();
    }

    // WindowItemCommand.read: ItemCommand id + fields (visible, toolTip, itemId), posX (rotr 4), posY (rotr 26),
    // helpText, height (rotr 5), width (rotr 9), maximized, windowTitle. WindowItemCommandHandler updates the
    // feature item and opens (maximized) or closes the window.
    public static byte[] WindowItem(string itemId, UnityWindowState state) =>
        new UnityModuleWriter(UnityWorldModuleId.WindowItemCommand)
            .Short((short)UnityWorldModuleId.ItemCommand)
            .Bool(true)
            .Module(EmptyToolTip())
            .String(itemId)
            .RotatedInt(state.PosX, 4)
            .RotatedInt(state.PosY, 26)
            .Module(EmptyToolTip())
            .RotatedInt(state.Height, 5)
            .RotatedInt(state.Width, 9)
            .Bool(state.Maximized)
            .String(string.Empty)
            .ToArray();

    // AssetCreateCommand.read: showBubble, assetId (rotr 1), clanRelation, modifiers (byte list), designId (rotr 27),
    // invisible, userName, visibleOnWarnRadar, clanTag, factionId (rotr 1), expansionStage (rotr 17),
    // clanId (rotr 2), type, detectedByWarnRadar, posX (rotr 17), posY (rotr 21).
    public static byte[] AssetCreate(int assetId, UnityAssetType type, string name, int factionId, int designId,
        int x, int y) =>
        new UnityModuleWriter(UnityWorldModuleId.AssetCreateCommand)
            .Bool(true)
            .RotatedInt(assetId, 1)
            .Module(new UnityModuleWriter(UnityWorldModuleId.ClanRelationModule).Short(0).ToArray())
            .Byte(0)
            .RotatedInt(designId, 27)
            .Bool(false)
            .String(name)
            .Bool(true)
            .String(string.Empty)
            .RotatedInt(factionId, 1)
            .RotatedInt(0, 17)
            .RotatedInt(0, 2)
            .Module(new UnityModuleWriter(UnityWorldModuleId.AssetTypeModule).Short((short)type).ToArray())
            .Bool(false)
            .RotatedInt(x, 17)
            .RotatedInt(y, 21)
            .ToArray();

    // A home base is five separate buildings around the station point (HQ in the middle).
    public static IReadOnlyList<(int Id, UnityAssetType Type, string Name, int X, int Y)> HomeBaseLayout(
        WorldStation station)
    {
        int id = station.Id * 10, x = station.X, y = station.Y;
        return
        [
            (id + 1, UnityAssetType.CompanyHqHome, "HQ", x, y),
            (id + 2, UnityAssetType.InstantRepairHome, "Repair Station", x, y - 600),
            (id + 3, UnityAssetType.HangarHome, "Hangar", x + 600, y),
            (id + 4, UnityAssetType.QuestGiver, "Quest Giver", x, y + 600),
            (id + 5, UnityAssetType.OreTradeHome, "Ore Trade", x - 600, y),
        ];
    }

    public static IReadOnlyList<byte[]> HomeBase(WorldStation station, int factionId) =>
        HomeBaseLayout(station).Select(asset => AssetCreate(asset.Id, asset.Type, asset.Name, factionId,
            asset.Type == UnityAssetType.QuestGiver ? factionId * 3 : 0, asset.X, asset.Y)).ToArray();

    // MapAssetActionAvailableCommand.read: mapAssetId (rotr 20), state (raw short, 0 = ON, 1 = OFF), activatable,
    // toolTip (MessageKeyListWildcardCommand), additionalInformation (empty AssetInformationModule).
    // Enables or disables the click bubble of a building or jumpgate.
    public static byte[] MapAssetActionAvailable(int assetId, bool available) =>
        new UnityModuleWriter(UnityWorldModuleId.MapAssetActionAvailableCommand)
            .RotatedInt(assetId, 20)
            .Short(available ? (short)0 : (short)1)
            .Bool(available)
            .Module(EmptyToolTip())
            .Module(new UnityModuleWriter(UnityWorldModuleId.AssetInformationModule).ToArray())
            .ToArray();

    // BeaconCommand.read: inPeaceArea, positionAheadY (rotr 20), isAlarmEffectActive, positionY (rotr 21),
    // repairRobotActive, repairRobotLootId, positionX (rotr 31), inStation, nearJumpGate, positionAheadX (rotr 14),
    // inRadiationZone, repairBotSkilled. BeaconCommandHandler shows the demilitarized-zone notice.
    public static byte[] Beacon(int x, int y, bool inPeaceArea, bool nearJumpGate = false) =>
        new UnityModuleWriter(UnityWorldModuleId.BeaconCommand)
            .Bool(inPeaceArea)
            .RotatedInt(y, 20)
            .Bool(false)
            .RotatedInt(y, 21)
            .Bool(false)
            .String(string.Empty)
            .RotatedInt(x, 31)
            .Bool(false)
            .Bool(nearJumpGate)
            .RotatedInt(x, 14)
            .Bool(false)
            .Bool(false)
            .ToArray();

    // JumpgateCreateCommand.read: gateId (rotr 10), factionId (rotr 30), designId (rotr 17), x (rotr 11),
    // y (rotr 9), isVisibleOnMinimap, showBubble, decoratorAssetTypes (byte count of ints rotr 30).
    // designId selects the lib_portals asset; 1 is the standard portal.
    public static byte[] Jumpgate(WorldPortal portal, int factionId = 0, int designId = 1) =>
        new UnityModuleWriter(UnityWorldModuleId.JumpgateCreateCommand)
            .RotatedInt(portal.Id, 10)
            .RotatedInt(factionId, 30)
            .RotatedInt(designId, 17)
            .RotatedInt(portal.X, 11)
            .RotatedInt(portal.Y, 9)
            .Bool(true)
            .Bool(true)
            .Byte(0)
            .ToArray();

    // JumpInitiatedCommand.read: mapId (rotr 25), gateId (rotr 26). Plays the jump effect on that gate;
    // the map switch itself happens when ShipInitializationCommand arrives with a different map id.
    public static byte[] JumpInitiated(int mapId, int gateId) =>
        new UnityModuleWriter(UnityWorldModuleId.JumpInitiatedCommand)
            .RotatedInt(mapId, 25)
            .RotatedInt(gateId, 26)
            .ToArray();

    // JumpDeniedCommand.read: mandatoryLevel (rotr 6).
    public static byte[] JumpDenied(int mandatoryLevel = 0) =>
        new UnityModuleWriter(UnityWorldModuleId.JumpDeniedCommand)
            .RotatedInt(mandatoryLevel, 6)
            .ToArray();

    // UserKeyBindingsModule.ActionType values with DarkOrbit's default (Flash) key codes. The client has no
    // local defaults: KeyboardComponent.KeyboardInit only runs from UserKeyBindingsUpdateHandler.
    public static readonly IReadOnlyList<(short Action, int[] KeyCodes, int Parameter)> DefaultKeyBindings =
    [
        (0, [74], 0),   // JUMP: J
        (1, [67], 0),   // CHANGE_CONFIG: C
        (2, [17], 0),   // ACTIVATE_LASER: Ctrl
        (3, [32], 0),   // LAUNCH_ROCKET: Space
        (4, [69], 0),   // PET_ACTIVATE: E
        (5, [82], 0),   // PET_GUARD_MODE: R
        (6, [76], 0),   // LOGOUT: L
        .. Enumerable.Range(0, 10).Select(i => ((short)7, new[] { i == 9 ? 48 : 49 + i }, i)),       // QUICKSLOT: 1..0
        .. Enumerable.Range(0, 10).Select(i => ((short)8, new[] { 112 + i }, i)),                     // QUICKSLOT_PREMIUM: F1..F10
        (9, [72], 0),   // TOGGLE_WINDOWS: H
        (10, [70], 0),  // PERFORMANCE_MONITORING: F
        (11, [107], 0), // ZOOM_IN: numpad +
        (12, [109], 0), // ZOOM_OUT: numpad -
        (13, [], 0),    // PET_REPAIR_SHIP
        (14, [13], 0),  // FOCUS_CHAT: Enter
        (15, [], 0),    // TOGGLE_CATEGORY_BAR
        (16, [], 0),    // TOGGLE_PRO_ACTION_BAR
        (17, [], 0),    // AUTO_ACTION_BAR
    ];

    // UserKeyBindingsUpdate.read: changedKeyBindings (byte list), remove (bool).
    // UserKeyBindingsModule.read: actionType (raw short), keyCodes (byte list of int rotr 25),
    // parameter (rotr 18), charCode (short; client computes rotl 2).
    public static byte[] KeyBindings(IEnumerable<(short Action, int[] KeyCodes, int Parameter)>? bindings = null) =>
        new UnityModuleWriter(UnityWorldModuleId.UserKeyBindingsUpdate)
            .ModuleList((bindings ?? DefaultKeyBindings).Select(binding =>
            {
                var module = new UnityModuleWriter(UnityWorldModuleId.UserKeyBindingsModule)
                    .Short(binding.Action)
                    .Byte((byte)binding.KeyCodes.Length);
                foreach (var keyCode in binding.KeyCodes) module.RotatedInt(keyCode, 25);
                return module.RotatedInt(binding.Parameter, 18).Short(0).ToArray();
            }).ToArray())
            .Bool(false)
            .ToArray();

    // A standalone MenuItemStatusCommand is applied by MenuItemStatusCommandHandler to the existing item.
    public static byte[] AmmoStatus(string menuItemId, bool selected)
    {
        var item = UnityItemsMenu.Find(menuItemId) ?? new UnityMenuItem(menuItemId, 0);
        return MenuItemStatus(item with { Selected = selected });
    }

    // MenuCategoryInitializationCommand.read: categoryId, menuItems (byte list).
    public static byte[] MenuCategory(string categoryId, IReadOnlyCollection<UnityMenuItem> items) =>
        new UnityModuleWriter(UnityWorldModuleId.MenuCategoryInitializationCommand)
            .String(categoryId)
            .ModuleList(items.Select(item => MenuItem(item)).ToArray())
            .ToArray();

    // MenuItemInitializationCommand.read: counterType, defaultIndex (rotr 12), timer, actionStyle,
    // showTooltipCooldownTimer, status, cooldownGroup. Nested modules are sent non-null.
    public static byte[] MenuItem(UnityMenuItem item,
        UnityMenuCounterType counterType = UnityMenuCounterType.Number,
        UnityMenuActionStyle actionStyle = UnityMenuActionStyle.Selection) =>
        new UnityModuleWriter(UnityWorldModuleId.MenuItemInitializationCommand)
            .Short((short)counterType)
            .RotatedInt(0, 12)
            .Module(new UnityModuleWriter(UnityWorldModuleId.MenuItemTimerCommand)
                .Bool(false)
                .Module(new UnityModuleWriter(UnityWorldModuleId.TimerStateModule).Short(0).ToArray())
                .Long(0)
                .Long(0)
                .String(item.Id)
                .ToArray())
            .Short((short)actionStyle)
            .Bool(false)
            .Module(MenuItemStatus(item))
            .Module(new UnityModuleWriter(UnityWorldModuleId.CooldownTypeModule).Short(0).ToArray())
            .ToArray();

    // MenuItemStatusCommand.read: counterStyle, selected, buyable, counterValue, menuItemId, visible,
    // activatable, available, toolTipItemBar, toolTipSlotBar, blocked, maxCounterValue, iconLootId.
    public static byte[] MenuItemStatus(UnityMenuItem item) =>
        new UnityModuleWriter(UnityWorldModuleId.MenuItemStatusCommand)
            .Short(0)
            .Bool(item.Selected)
            .Bool(false)
            .Long(item.Count)
            .String(item.Id)
            .Bool(true)
            .Bool(true)
            .Bool(true)
            .Module(EmptyToolTip())
            .Module(EmptyToolTip())
            .Bool(false)
            .Long(item.Count)
            .String(item.Id)
            .ToArray();

    // SlotBarInitializationCommand.read: visible, slots (short-counted list), slotBarId, order, position.
    public static byte[] SlotBar(string slotBarId, string order, string position,
        IReadOnlyCollection<byte[]> slots, bool visible = true) =>
        new UnityModuleWriter(UnityWorldModuleId.SlotBarInitializationCommand)
            .Bool(visible)
            .ShortModuleList(slots)
            .String(slotBarId)
            .String(order)
            .String(position)
            .ToArray();

    // SlotInfoCommand.read: menuItemId, slotId (client rotates right by 28).
    public static byte[] SlotInfo(string menuItemId, int slotId) =>
        new UnityModuleWriter(UnityWorldModuleId.SlotInfoCommand)
            .String(menuItemId)
            .RotatedInt(slotId, 28)
            .ToArray();

    // Field order and rotations follow ShipInitializationCommand.read.
    // HangarShipsCommand.read: petView (nullable module, id 0 = none), then byte-counted module lists drones,
    // ships (HangarShipBriefCommand), items and allDesignPack. Answers HangarListRequest from the hangar window.
    // ShopItemListCommand.read: itemList, promptList, currentShipLootId, category. HangarShipsCommandHandlerEvent
    // only resolves each hangar ship's ItemInfo once a "ship" category list exists, so send it (empty) first.
    public static byte[] ShopItemList(string category, string currentShipLootId) =>
        new UnityModuleWriter(UnityWorldModuleId.ShopItemListCommand)
            .ModuleList([])
            .ModuleList([])
            .String(currentShipLootId)
            .String(category)
            .ToArray();

    public static byte[] HangarShips(string shipLootId, long shipId) =>
        new UnityModuleWriter(UnityWorldModuleId.HangarShipsCommand)
            .Short(0)
            .ModuleList([])
            .ModuleList([HangarShipBrief(shipLootId, shipId, isActive: true)])
            .ModuleList([])
            .ModuleList([])
            .ToArray();

    // HangarShipBriefCommand.read: lootId, isFavorite, shipId (double), isActive.
    public static byte[] HangarShipBrief(string lootId, long shipId, bool isActive) =>
        new UnityModuleWriter(UnityWorldModuleId.HangarShipBriefCommand)
            .String(lootId)
            .Bool(false)
            .Long(shipId)
            .Bool(isActive)
            .ToArray();

    // HangarShipDetailCommand.read: hp (rotr 3), laserAmmo (double), nano (29), shieldGenerators (11),
    // curDesignLootId, maxSpeed (2), maxHp (3), designList (byte-counted strings), upgradeModuleCount (20),
    // isFavorite, shield (12), shipId (double), hull (31), lootId, repairVouchers (26), speedGenerators (16),
    // laserCount (25), isActive, rockets (12), maxNano (16). Answers HangarShipDetailRequest for the selected ship.
    public static byte[] HangarShipDetail(string lootId, long shipId, bool isActive, WorldShipStats stats,
        long hitPoints, long shield) =>
        new UnityModuleWriter(UnityWorldModuleId.HangarShipDetailCommand)
            .RotatedInt(ClampInt(hitPoints), 3)
            .Long(0)
            .RotatedInt(0, 29)
            .RotatedInt(0, 11)
            .String(lootId)
            .RotatedInt(stats.Speed, 2)
            .RotatedInt(ClampInt(stats.MaxHitPoints), 3)
            .Byte(0)
            .RotatedInt(0, 20)
            .Bool(false)
            .RotatedInt(ClampInt(shield), 12)
            .Long(shipId)
            .RotatedInt(0, 31)
            .String(lootId)
            .RotatedInt(0, 26)
            .RotatedInt(0, 16)
            .RotatedInt(0, 25)
            .Bool(isActive)
            .RotatedInt(0, 12)
            .RotatedInt(0, 16)
            .ToArray();

    private static int ClampInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    // HangarShipEquipmentCommand.read: shipId (double), extrasSlotsSize2 (rotr 18), hangarId (double), lootId,
    // petConfigs, extrasSlotsSize1 (2), shipConfigs, droneConfigs, currentConfigId (26).
    // Answers HangarShipEquipmentRequest when the hangar Manage page opens.
    public static byte[] HangarShipEquipment(string lootId, long shipId, long hangarId, int currentConfigId,
        IReadOnlyList<HangarShipConfig> shipConfigs) =>
        new UnityModuleWriter(UnityWorldModuleId.HangarShipEquipmentCommand)
            .Long(shipId)
            .RotatedInt(0, 18)
            .Long(hangarId)
            .String(lootId)
            .ModuleList([])
            .RotatedInt(0, 2)
            .ModuleList(shipConfigs.Select(HangarShipEquipmentMsg).ToArray())
            .ModuleList([])
            .RotatedInt(currentConfigId, 26)
            .ToArray();

    // HangarShipEquipmentMsg.read: active, setId (rotr 15), then byte-counted int lists generators (23),
    // extra (21), QuantumFluidCore (10), laser (21), cannonShield (9), rocket (11), upgrades (16), cannon (23).
    public static byte[] HangarShipEquipmentMsg(HangarShipConfig config)
    {
        var writer = new UnityModuleWriter(UnityWorldModuleId.HangarShipEquipmentMsg)
            .Bool(config.Active)
            .RotatedInt(config.SetId, 15);
        IntList(writer, config.Generators, 23);
        IntList(writer, [], 21);
        IntList(writer, [], 10);
        IntList(writer, config.Lasers, 21);
        IntList(writer, [], 9);
        IntList(writer, [], 11);
        IntList(writer, [], 16);
        IntList(writer, [], 23);
        return writer.ToArray();
    }

    private static void IntList(UnityModuleWriter writer, IReadOnlyCollection<int> values, int bits)
    {
        writer.Byte(checked((byte)values.Count));
        foreach (var value in values) writer.RotatedInt(value, bits);
    }

    public static byte[] ShipInitialization(WorldPilot pilot, WorldShipStats stats, long shield, long hitPoints,
        int x, int y) =>
        new UnityModuleWriter(UnityWorldModuleId.ShipInitializationCommand)
            .RotatedInt((int)pilot.UserId, 14)
            .String(pilot.UserName)
            .String(pilot.ShipId)
            .RotatedInt(stats.Speed, 20)
            .RotatedInt((int)Math.Min(shield, int.MaxValue), 18)
            .RotatedInt((int)Math.Min(stats.MaxShield, int.MaxValue), 19)
            .Long(hitPoints)
            .Long(stats.MaxHitPoints)
            .RotatedInt(0, 22)
            .RotatedInt(stats.CargoCapacity, 14)
            .RotatedInt(0, 11)
            .RotatedInt(0, 15)
            .RotatedInt(x, 14)
            .RotatedInt(y, 4)
            .RotatedInt(pilot.MapId, 21)
            .RotatedInt(pilot.FactionId, 27)
            .RotatedInt(0, 3)
            .RotatedInt(3, 5)
            .Bool(true)
            .Long(pilot.Experience)
            .Long(pilot.Honor)
            .RotatedInt(pilot.Level, 15)
            .Long(pilot.Credits)
            .Long(pilot.Uridium)
            .Float(0)
            .RotatedInt(0, 18)
            .String(string.Empty)
            .RotatedInt(0, 27)
            .Bool(false)
            .Bool(false)
            // refreshMap: the handler only creates the hero and loads the scene when true.
            .Bool(true)
            .RotatedInt(0, 23)
            .RotatedInt(0, 31)
            .Byte(0)
            .ToArray();
}
