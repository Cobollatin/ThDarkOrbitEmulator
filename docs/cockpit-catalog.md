# Unity cockpit catalog and WebView diagnosis

## Current cockpit / in-game WebView support (2026-10-28)

The cockpit opens portal pages in a Vuplex WebView on the advertised host,
`https://localhost:60952` (see SPEC-02 "Dual HTTPS listeners"). "Served" means
the emulator answers the route with a local page backed by Postgres or Redis
state. "Unverified" means no redirected Unity capture yet shows the native
button calling it.

| Cockpit control | Emulator route | Status |
| --- | --- | --- |
| News (green breaking-news button) | `/unityApi/news.php`, `/flashAPI/brNews.php`, then `indexInternal.es?action=internalCommandCenter` | Served; the button now opens the styled Command Center instead of the Dock. The cockpit preview can still show placeholder labels while the client-side localization lookup (`File Category Not Found`) is unresolved. |
| Profile | `indexInternal.es?action=internalPilotSheet` (detail, skill tree, allocate/reset) | Served; the button click has not been re-tested in the client. |
| Rank | `indexInternal.es?action=internalHallofFame` | Served; the button click has not been re-tested in the client. |
| Hangar | `indexInternal.es?action=internalDock` / `internalDockEquipment`; `POST /unityApi/activate.php` | Served. Native activation maps the submitted assigned `hangarId` to the player's PostgreSQL active slot. |
| Shop | `indexInternal.es?action=internalShop`, `internalPayment` | Served. |
| Forum / Help / Account | `boardLink`, `helpLink`, `/unityApi/links.php` (`forum`, `support`, `help`, `accountCenter`) | Served on the advertised `:60952` host. |
| Open World / Spacemap | Portal `LAUNCH` link on `internalWebGL`; HTTP canvas `indexInternal.es?action=internalMapRevolution` (`/spacemap/world/*`); world TCP listener `127.0.0.1:60954` | The HTTP spacemap works. The portal link disappears by design when `OpenWebLogin` closes the WebView at the `internalWebGL` handoff. Native START → world socket entry is **unverified**. |

## Evidence

The supplied Unity console shows `OpenWebLogin` navigating to
`https://localhost:60952/indexInternal.es?action=internalWebGL&from=internalStart`.
The following `/unityApi/login.php` body advertised `host`,
`boardLink`, `helpLink`, and `sharedImages` at `https://localhost` without a
port. Aspire intentionally serves a compatibility gateway on port 443 because
the native binary has a fixed `https://localhost/` URL. The WebView login used
port 60952, so newly opened cockpit pages could reach a different listener.
`ClientFacingGameHost.Resolve` now uses the configured WebView origin for
native requests whose forwarded host omits the port. This is a plausible
black-screen fix; the supplied log does not prove whether port 443 was healthy
at the time. The Link API and breaking
news action use that same host. The synthetic breaking-news button now opens
the styled `internalCommandCenter` page instead of the Dock.

The same console shows `File Category Not Found` for seven localization
categories before cockpit initialization: `resource_breakingNews`,
`resource_externalHome`, `resource_item_old`, `resource_localeBase`,
`resource_premiumExtension`, `resource_upsellPackage`, and `resource_xtm`.
The installed Unity client contains
`localizations_assets_all.bundle`, and its Addressables `catalog.json` names
all seven categories under both `en` and `es`. The saved Live HAR has no
request for these categories. The failure is in the client-side localization
lookup or bundle load; the log does not distinguish those two cases. It is not
a response from `/resources/resource_command_center.xml`. Serving an invented
HTTP `resources.xml` would not repair that lookup. The current breaking-news
XML still has sparse synthetic labels, so its cockpit preview may show
placeholders until the bundle issue is diagnosed in a runtime capture.

The console's `DarkOrbit.<GetHangerPost>d__54` 401 has no request URL. The installed
`DarkOrbit_Data/StreamingAssets/config.txt` contains `hangerChange:/unityApi/activate.php`
and `localHost:localhost:60952`. The native config parser assigns that suffix to
`Settings.HangerChangeHostSuffix`, and `Init.UpdateHost` combines it with its host.
The resulting local URL is `https://localhost:60952/unityApi/activate.php`.
The native request posts one `WWWForm` field, `hangarId`, and reads JSON
`success` (integer, `1` on success) and `message` (null on success). The emulator
returns HTTP 200 `{"success":1,"message":null}` after selecting an assigned ID;
missing sessions and invalid or unassigned IDs receive JSON errors and do not
change the selection. This response is supported by the native branch trace,
but a redirected Unity activation capture is still needed for live confirmation.
A targeted
review of the retained session HARs found no 401 hangar request or native
Profile, Rank, or News button navigation. One redirect capture contains a
`GET /GameAPI.php?action=core.bpid` 401; it has no evidence tying it to
`GetHangerPost`. The saved Live capture ends after initial news images.
Metadata names `LaunchShow.GetHangerPost(url)`,
`GetNewsRequest`, `Start`, and button callbacks but does not expose their native
method bodies. Known `/unityApi/login.php`, `/unityApi/news.php`,
`/unityApi/links.php`, and both `/indexInternal.es` endpoint methods already
allow anonymous HTTP routing and perform `dosid` checks themselves. An
unmapped route can be rejected by the API fallback authorization policy; a
mapped route can return 401 for a missing or expired `dosid`. The API logs the
method, path, safe action, and endpoint name for future Unity 401 responses
without logging the cookie. The mapped activation route now performs its own
session check and logs rejection without the cookie.

The existing portal destinations for the three cockpit controls are Profile:
`https://localhost:60952/indexInternal.es?action=internalPilotSheet`, Rank:
`https://localhost:60952/indexInternal.es?action=internalHallofFame`, and News:
`https://localhost:60952/indexInternal.es?action=internalCommandCenter`.
These are emulator menu destinations, not yet observed native button URLs.
Integration tests exercise their action routes with a valid `dosid` and a
selected company. Each action is mapped on the anonymous `/indexInternal.es`
route and performs its own session check, so no missing route or HTTP fallback
authorization has been demonstrated for these pages.

The "Open world" link is the portal `LAUNCH` link on the
`internalWebGL` menu page. Native `OpenWebLogin` closes its Vuplex WebView at
the `internalWebGL` handoff, so that HTML link disappears when the 3D cockpit
appears. The cockpit's blue START button is native Unity UI, represented in
metadata as `LaunchShow.btnStart` and callbacks. The HTTP
`internalMapRevolution` page is a separate emulator canvas. The supplied log
does not show a START click or world socket handshake; native world entry
needs a redirected cockpit capture to verify.

## Admin and public contracts

Migration `030_cockpit_catalog` adds languages, server descriptors, locale
overrides, ship catalog records, and per-account hangar assignments in
Postgres. The Cockpit Backoffice screen lists and edits them. Admin routes are
under `/api/v1/backoffice/cockpit` and require `emulator-admin`:

| Resource | Read | Write |
| --- | --- | --- |
| Languages | `GET /languages` | `PUT /languages/{code}` |
| Servers | `GET /servers` | `PUT /servers/{code}` |
| Ship catalog | `GET /ships` | `PUT /ships/{shipId}` |
| Player slots | `GET /accounts/{accountId}/hangars` | `PUT /accounts/{accountId}/hangars/{slot}` |
| Locale overrides | `GET /locale-overrides` | `PUT /locale-overrides/{language}/{category}/{key}` |

Public `GET /unityApi/languages.php` and `/unityApi/servers.php` expose enabled
descriptors. The local portal `internalServers` page reads the same server
table. `/unityApi/login.php` uses the enabled language selection, default
instance name, and assigned player ships. First login or the first Backoffice account view creates the two starter
assignments (Pusat Plus and Phoenix) in Postgres, with stable six-digit hangar
IDs; subsequent admin edits survive login. The Dock and world account summary
read those assignments. The world combat stat formulas remain slot based.

The Unity client's dropdown request contract is not present in the saved HAR;
these public list routes are emulator contracts and have not been observed
being called by this client build. Locale overrides are stored for Backoffice
editing but the Unity asset bundle does not read them yet. Server descriptors
do not reconfigure the running mesh or redirect its TCP listener. A server
choice in the native dropdown must be captured before binding these records to
that client interaction.

The Postgres catalog integration test persists all seven category names as
locale overrides. That covers Backoffice storage only; it does not establish
that the native Addressables lookup can read those values.

## Bounded IL2CPP trace (installed 2026-10-28 client)

`GameAssembly.dll` SHA-256 is
`868729de4486860841b40b59d04fd469e510a2854be354b59d945a7eaa472107`.
The installed v31 metadata SHA-256 is
`84065edf86c33aa966478481fbcb8dcf9143bfe05db9809dd543e3e98f08a685`.
Method RVAs below are relative to this exact x86 PE. The bounded disassembly
script is `_reversing/Analysis/trace_il2cpp.py`; its IL2CPP method table comes
from the local dump under `_reversing/Analysis/il2cpp-dump/`.

| Native method / RVA | Finding |
| --- | --- |
| `LaunchShow.GetHangerPost` `0xEE0BD0`; iterator `MoveNext` `0xEEAE40` | The wrapper stores the supplied URL. At `0xEEAF75`–`0xEEAF8D` the iterator adds one `WWWForm` field named `hangarId` from `LaunchShow.hangarId` (`+0x94`) and calls `UnityWebRequest.Post(url, form)`. At `0xEEAFD2` it calls `Init.SetRequestHeader`, then sends. At `0xEEB141` it parses response text with `MessageData.FromJson` (Newtonsoft); `MessageData` has `Success` (64-bit integer) and `Message` (string). The branch at `0xEEB153` accepts `Success == 1` and a null `Message`; the other branch displays `Message`. A JSON body such as `{"success":1,"message":null}` is therefore the success candidate. |
| `Init.SetRequestHeader` `0xEFC5B0` | It sets only `User-Agent`: `DarkOrbit Unity Client 1.1.100` in the normal branch, or `Steam-DROThinClient/1.0 (Unity)` in the Steam branch. The form POST itself supplies its content type. |
| `<>c__DisplayClass77_0.<UpdatePageInfoShip>b__0` `0xEEEBF0` | At `0xEEECE4`–`0xEEED4C` an inactive selected hangar copies `HangarWebData.HangarId` to `LaunchShow.hangarId`, takes `Settings.HangerChangeHost` (`+0x218`) as URL, and starts the hangar POST iterator. Selecting an already active hangar calls `GameLoadingManager.StartGame` at `0xEEED7E`. |
| `Init.UpdateHost` `0xEFD580`; `<LoadConfig>d__32.MoveNext` `0xF02FF0` | `UpdateHost` concatenates the host prefix with `Settings.HangerChangeHostSuffix` (`+0x23C`) and stores `HangerChangeHost` at `0xEFD73C`. The suffix is assigned by the `StreamingAssets/config.txt` parser at `0xF03539`; the installed file supplies `/unityApi/activate.php`. |
| `LaunchShow.Start` `0xEE1E70`; `<Start>b__55_0` `0xEE23A0`; `GameLoadingManager.StartGame` `0xF0E420` | `Start` reads `btnStart` at offset `+0x44`, installs an `onClick` listener at `0xEE1F84`, and that callback disables the button then tail-jumps to `StartGame` at `0xEE2404`. `StartGame` calls `InitDynamicXml` at `0xF0E808`, handles display setup and later calls `LaunchShow.UpdateButton`; this method does not itself call the provisional `/spacemap/world/*` HTTP bridge or prove a connection to `127.0.0.1:60954`. |
| `GameLoadingManager.InitDynamicXml` `0xF0CC80` | The START path next obtains `Settings.get_MapXmlUrl` at `0xF0CD21` and queues it with the Command Center XMLs for `DownloadHelper.StartDownload` at `0xF0CE54`. This confirms a native XML loading stage before any established socket handshake. The emulator already serves `/xml/maps.xml`; the callback and subsequent transport still need a native trace or capture. |
| `<LoadLocalizationData>d__8.MoveNext` `0xF03B60` | At `0xF03DB7` it calls `Addressables.LoadAssetsAsync`. At `0xF03F97` it gets each loaded asset's name; at `0xF03FA2`–`0xF03FCA` it logs `File Category Not Found:` when that name is absent from the in-memory category mapping. This is an in-process mapping failure after asset loading, not an HTTP response. |
| `LocalizationManager.GetCategoryMapping` `0xEFE410` | The function's literal keys include `resource_news`, `resource_items`, and `resource_items_old`, but omit all seven reported asset names. In particular, the asset `resource_item_old` differs from mapped `resource_items_old`. The seven missing names are a native mapping/catalog version mismatch, independently of the server. |

The installed Addressables `StreamingAssets/aa/catalog.json` points to a local
`BuildTarget/localizations_assets_all.bundle`; each of the seven reported names
also has an `en` and `es` asset address. The native localization path needs a
category mapping or client asset fix. Adding an emulator XML route cannot make
the native mapping contain those keys. The current server-side locale override
records are therefore not a repair for this log message.

An actual native world socket login capture remains unavailable. The emulator's
existing world protocol remains its documented local diagnostic contract.

## Native world transport after START (installed 2026-10-28 build)

The post-XML path is `NetConnectionHelper.ConnectToMap` (`0x2AB870`), which
selects `MapsPattern.GameServerIP` from `maps.xml` (or the configured default)
and calls `Connect` (`0x2ABBA0`). `Connect` creates a `SocketComponent` session
and sends `VersionRequest`; `HandleVersionResponse` (`0x2AC810`) sends
`LoginRequest` only when the server's `VersionCommand.equal` is true. This is
native code and metadata evidence, not a captured START socket exchange.

| IL2CPP method / RVA | Native module ID and structure |
| --- | --- |
| `VersionRequest.getLibcomModuleId` `0x15A8320`; `write` `0x15A8330` | `0x029A` (666). Writes the 16-bit ID, then a 16-bit UTF-8 byte count and the version bytes. The installed client literal is `05bcf83b06964a516a8282e10dbf3369` (`SendVersionRequest` `0x2AF370`). |
| `VersionCommand.getLibcomModuleId` `0x15A81B0`; `read` `0x15660A0` | `0x029B` (667). Reads a length-prefixed UTF-8 `version` and a Boolean `equal`. |
| `LoginRequest.getLibcomModuleId` `0x10A9A10`; `write` `0x1546DE0` | `0x0007`. Fields are `int32 userID`, `int16 factionID`, UTF-8 `sessionID`, UTF-8 `version`, `int32 instanceId`, Boolean `isMiniClient`, in that order after the ID. The integer writes rotate bits (`userID` by 3, `factionID` by 7, `instanceId` by 12); `SendLoginRequest` (`0x2AEAA0`) populates the fields from the current login data and settings. |
| `LoginResponse.getLibcomModuleId` `0x1546F50`; `read` `0x14AC830` | Signed 16-bit ID `-23284` (`0xA50C`). Reads one 16-bit status enum. |
| `MoveRequest.getLibcomModuleId` `0x1565050` | `0x0052` (82); metadata fields are `int32 positionX,targetY,targetX,positionY`. |
| `JumpRequest.getLibcomModuleId` `0x10CA500`; `write` `0x153EC30` | `0x000A` (10), ID only; no portal ID field. `JumpToServerCommand` is ID `0x0014` (20), also ID only. |

`PacketParser.Parse` (`0xF45420`) reads a **three-byte big-endian length** from
its circular buffer before copying that many body bytes. `TcpChannel.Send`
(`0xF49E90`) serializes `IModule.write` and reserves a three-byte frame header,
with a special extra byte for `VersionRequest`. The precise first outbound
frame bytes and server responses still require a native capture. The local
world listener's four-byte length, two-byte big-endian command ID and
`local-1` version are incompatible with these native module definitions.
The listener now recognizes the native three-byte length and optional version
preamble, answers module `0x029B` for the installed version hash, and decodes
module `0x0007` with the documented bit rotations. It resolves `userID` and
`sessionID` against the current game session, checks faction, then sends native
framed login status and emulator map/hero initialization payloads (`0xF100` and
`0xF101`). Those two module IDs and their payloads are local extensions because
the installed client has not supplied a captured server entry burst. Native
gameplay and end-to-end Unity START remain unverified. The four-byte local
protocol and its 4,096-byte prelogin cap remain available on the same port.
