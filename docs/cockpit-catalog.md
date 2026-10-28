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

## Installed `StreamingAssets/config.txt` route table

`Init.<LoadConfig>d__32.MoveNext` copies these suffixes into `Settings`, and
`Init.UpdateHost` (`0xEFD580`) joins them to the selected HTTPS host. The
installed `localHost:localhost:60952` is an authority, not a route. All twelve
templates below have an ApiService mapping. The seven newly mapped routes use
local response contracts because no retained Live HAR contains those replies.

| Config key | Installed template | Native use / ApiService behavior |
| --- | --- | --- |
| `instanceHost` | `GET /unityApi/instance.php` | `LaunchShow.<Start>b__55_7` passes `Settings.InstanceHost` to `GetRequest`; its iterator parses `ServerData.data` with region `serverList` collections. Each server item now includes the native `region` enum string (`eu_west`); the local response lists enabled catalog servers and requires `dosid`. |
| `loginHost` | `GET /unityApi/login.php` | Existing nested login JSON and authenticated `dosid` handling. |
| `newsHost` | `GET /unityApi/news.php` | Existing news countdown JSON and session handling. |
| `logoutHost` | `GET /indexInternal.es?action=externalLogout` | Existing local session invalidation and WebView redirect. |
| `quitHost` | `GET /unityApi/close.php` | The configured quit suffix is mapped. The cockpit quit callback `<>c.<Start>b__55_2` calls `Application.Quit`; a native HTTP call to this template has not been established. Local close reply has `data: []` and invalidates `dosid`. |
| `hangerChange` | `POST /unityApi/activate.php` | `hangarId` form field; selects an assigned hangar and returns `success`/`message`. |
| `UnityRequest` | `POST /unityApi/agent.php` | `LaunchShow.<GetUnityPost>d__52.MoveNext` posts one `functionId` field to `Settings.UnityRequest`. Local handler validates it and acknowledges; server-side meaning is unverified. |
| `changeInstance` | `POST /unityApi/changeInstance.php` | `GameLoadingManager.<PostChangeInstance>d__64.MoveNext` posts `instanceId` and parses `ChooseServerData` (`result`, `link`, `error`, `status`). Local handler accepts an enabled online catalog ID and supplies a same-origin login link; no mesh migration is implied. |
| `voucher` | `GET /unityApi/evoucher.php` | Local authenticated navigation to `internalPayment`; native request shape is unverified. |
| `package` | `GET /unityApi/package.php` | Local authenticated navigation to `internalShop`; native request shape is unverified. |
| `link` | `GET /unityApi/links.php` | Existing six-link JSON; a valid `dosid` is propagated/refreshed. |
| `language` | `POST /unityApi/setLang.php` | `LaunchShow.<GetUnityPostLanguage>d__53.MoveNext` injects a WebView `fetch` with URL-encoded `lang` and `credentials:'include'`. Local handler saves the enabled language for that account. |

The native `LaunchShow.<Start>b__55_3` and `_4` callbacks set
`Unity_OpenProfile` and `Unity_OpenRank`, then call `StartGame`; neither
callback contains an HTTP URL. `LaunchShow.OnBtnClick` (`0xEE0FD0`) distinguishes
`?action=internalDock`, `internalGalaxyGates`, and `internalPayment`, sets
`Unity_OpenShop`, `Unity_OpenGate`, or `Unity_Vouchers`, or opens an absolute URL.
Its empty-link branch calls `JavascriptHelper.OpenPackageUrl`. Equipment is
reached through the Dock menu at `internalDockEquipment`, and Account Center
through the existing `links.php` `accountCenter` destination. Breaking News
uses the existing `news.php` and Command Center routes. `OpenWebLogin` wires a
`CallUnityOpenWindow` message callback to `JavascriptHelper.OpenWebUrl`; its
`UpdateWebData` path recognizes the `Unity_OpenProfile`, `Unity_OpenRank`, and
`Unity_ChangeLanguage` flags. These local menu destinations are served, but
their exact native navigation after a button click still needs a redirect
capture. The native server picker requests `instance.php`; its selected
instance posts to `changeInstance.php`. The native language choice posts to
`setLang.php` from the embedded WebView.

### Native response boundaries for the twelve templates

The following addresses refer to the installed DLL hash below. The metadata
types give JSON field names and types; the `MoveNext` branches establish which
fields the client actually uses. A mapped suffix alone does not establish a
request or response parser. HTTP success branches below check
`UnityWebRequest.result`, not a particular numeric HTTP status; 401/400/404
behavior on the new local routes is an emulator policy.

| Config key | Response evidence and local contract |
| --- | --- |
| `instanceHost` | `<GetRequest>d__62.MoveNext` `0xEEBE60`: checks a `json` content type and `DarkOrbitUnity` header at `0xEEC20C`–`0xEEC23A`, compares the URL to `Settings.InstanceHost` (`+0x258`) at `0xEEC26B`, and calls `ServerData.FromJson` (`0xF852B0`) at `0xEEC38F`. Metadata `ServerData.Data` contains `faction` string, `hangars` list, and `euWest`/`euEast`/`usWest`/`usEast`/`test` objects; each has integer `serverCount` and `serverList`. `ServerList` has string `continent`, `langCode`, `name`, `location`, `lang`, `title`; integer `active`, `id`, `recommended`; URI `url`; and `Region` enum `region`. `RegionConverter.ReadJson` (`0xF84D30`) recognizes `eu_west`, `eu_east`, `us_west`, `us_east`, `test` at `0xF84D50`–`0xF84D78`. Local items now provide `region: "eu_west"`. |
| `loginHost` | `Init.UpdateHost` writes the concatenated URL to `Settings.LoginHost` (`+0x25C`) at `0xEFD655`. `LoginDataNew.FromJson` (`0x4A41C0`) describes an outer `loginData` string; `LoginNodeData` metadata describes its nested account, hangar, host, and asset fields. The captured Live login body supports the existing local response. This bounded trace does not identify the native call site that consumes `LoginDataNew`, so those metadata fields are not claimed as reads from this endpoint. |
| `newsHost` | `<GetNewsRequest>d__61.MoveNext` `0xEEB540` performs GET at `0xEEB66F`, checks request success at `0xEEB729`–`0xEEB74B`, and deserializes the body with `JsonConvert.DeserializeObject` at `0xEEB849`. It selects news entries at `0xEEB90D` and updates the Command Center list at `0xEEBAD7`. The captured Live JSON `data[]` entries with `newsId` string and `seconds` integer are retained in `CapturedNewsCatalog`; no XML deserializer handles this endpoint. |
| `logoutHost` | `Init.UpdateHost` stores the URL at `Settings.LogoutHost` (`+0x210`) at `0xEFD6CA`. The suffix is an embedded WebView navigation; no native JSON/XML response deserializer for it is established. The local endpoint invalidates the session and redirects. |
| `quitHost` | `<GetRequest>d__62.MoveNext` compares its URL to `Settings.QuitHost` (`+0x214`) at `0xEEC2A6` and calls `Application.Quit` at `0xEEC2F0`. The same coroutine compares an `instanceHost` reply to the exact literal `{"data":[]}` at `0xEEC350` and skips server deserialization on that body. No native field reader for a `close.php` body is established. The local close response is that exact JSON and invalidates the session. |
| `hangerChange` | `<GetHangerPost>d__54.MoveNext` `0xEEAE40` posts `hangarId` at `0xEEAF75`–`0xEEAF8D`; `MessageData.FromJson` at `0xEEB141` reads integer `success` and nullable string `message`. The branch at `0xEEB153` accepts `success == 1` with null `message`; the local 200 reply matches. |
| `UnityRequest` | `<GetUnityPost>d__52.MoveNext` `0xEECB70` posts `functionId` at `0xEECC39`–`0xEECC66`. After `UnityWebRequest.get_result` at `0xEECD20`, it obtains response text at `0xEECD56` but does not call a JSON/XML deserializer or read a field. Local `{"result":true}` is an acknowledgment only. |
| `changeInstance` | `<PostChangeInstance>d__64.MoveNext` `0xF1AAC0` posts integer `instanceId` at `0xF1AC69`–`0xF1ACB0`, checks `json` and `DarkOrbitUnity` at `0xF1AF12`–`0xF1AF40`, and calls `ChooseServerData.FromJson` (`0xE276E0`) at `0xF1AF75`. Metadata fields are string `result`, URI `link`, string `error`, and 64-bit integer `status`. The branch tests `status` at `0xF1AF87`: zero opens `link` (`0xF1B02B`–`0xF1B04D`); nonzero opens the empty WebView URL at `0xF1B000`. The local success reply has zero `status` and an absolute `link`; the other fields are present for the serializer but no native read was found in this branch. |
| `voucher`, `package` | `Init.UpdateHost` writes `Settings.Voucher` (`+0x220`) at `0xEFD7EA` and `Settings.Package` (`+0x224`) at `0xEFD823`. No native request coroutine or response deserializer binding these URLs is established. Local authenticated navigation remains provisional; their 302 responses are not claimed as a Live contract. |
| `link` | `<GetLinkRequest>d__47.MoveNext` `0xF01C40` GETs at `0xF01CF2`, checks for HTML at `0xF01EF9`, and calls `JsonConvert.DeserializeObject` at `0xF01FA8`. `LinkData` metadata exposes `forum`, `credits`, `accountCenter` strings and `help`, `imprint`, `support` URIs; the six local fields match the captured Live JSON. |
| `language` | `<GetUnityPostLanguage>d__53.MoveNext` `0xEEC740` builds a WebView `fetch` at `0xEEC78F`–`0xEEC7C6` with `POST`, URL-encoded `lang`, and `credentials:'include'`. It never receives the fetch result in this coroutine, so no native JSON/XML response parser or field requirement is established. Local `{"result":true,"data":{"language":...}}` is provisional. |

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
The bounded disassembly exposes `LaunchShow.GetHangerPost(url)`,
`GetNewsRequest`, `Start`, and the button callback bodies. Known `/unityApi/login.php`, `/unityApi/news.php`,
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
framed login status followed by the server commands documented below. Native gameplay and end-to-end Unity
START remain unverified. The four-byte local
protocol and its 4,096-byte prelogin cap remain available on the same port.

### Server command factory and inbound readers

The `darkorbit-protocol.dll` IL2CPP types in installed `global-metadata.dat`
name the fields; the matching `GameAssembly.dll` method bodies establish their
read order, primitive widths, and integer bit rotations. `ModuleFactory.createInstance`
starts at `0x154BC40` and registers/constructs protocol modules; `ModuleFactory.get`
is `0x1563D70`. `DarkOrbit.ByteArray.ReadShort` (`0xF414E0`) combines the
first byte shifted by eight with the second byte for a big-endian 16-bit
module number. `PacketParser.Parse` (`0xF45420`) reads the three-byte
big-endian frame length. `ByteArray..ctor` (`0xF415F0`) constructs
`DarkOrbit.EndianBinaryWriter` and `DarkOrbit.EndianBinaryReader` with the
`Big` enum value; the generated commands use their virtual 16-bit, 32-bit,
double, single, and Boolean reads and writes. `LoginResponseHandler.Handle` (`0xF5CA80`)
treats status zero as success at `0xF5CBD9`. All RVAs refer to the two hashes
above.

In the table, `i32(rN)` means a big-endian 32-bit wire value that the native
reader rotates **left** by N to obtain the named field. Therefore the server
encodes with a right rotation by N. `f64` is an IEEE 754 double, converted to
the metadata `Int64` field by the reader; `f32` is an IEEE 754 single. `str`
is a big-endian 16-bit UTF-8 byte count followed by the bytes; `bool` is one
byte. These are reader contracts; no retained Live capture confirms an entry
burst or its sequencing.

| Server command (ID getter / reader RVA) | Reader order after 16-bit module ID |
| --- | --- |
| `MapChangedCommand` `0x15492A0` / `0x14C2690`, `0x008A` | `newMapId:i32(r28)` |
| `ShipInitializationCommand` `0x158B630` / `0x158B640`, `0x0031` | `userId:i32(r18), userName:str, typeId:str, speed:i32(r12), shield:i32(r14), shieldMax:i32(r13), hitPoints:f64, hitMax:f64, cargoSpace:i32(r10), cargoSpaceMax:i32(r18), nanoHull:i32(r21), maxNanoHull:i32(r17), x:i32(r18), y:i32(r28), mapId:i32(r11), factionId:i32(r5), clanId:i32(r3), expansionStage:i32(r27), premium:bool, ep:f64, honourPoints:f64, level:i32(r17), credits:f64, uridium:f64, jackpot:f32, dailyRank:i32(r14), clanTag:str, galaxyGatesDone:i32(r5), useSystemFont:bool, cloaked:bool, refreshMap:bool, captainEnergy:i32(r9), maxCaptainEnergy:i32(r1), modifierCount:u8, then that many `VisualModifierCommand` modules. The local entry sends zero modifiers. |
| `ShipCreateCommand` `0x158A1F0` / `0x158A200`, `0x0053` | Metadata names `userId,typeId,expansionStage,clanTag,userName,x,y,factionId,clanId,dailyRank,warnBox,galaxyGatesDone,useSystemFont,npc,cloaked,motherShipId,positionIndex,designSet,specialNpcType`; the full nested read path has not yet been mapped, so the listener does not emit it. |
| `MoveCommand` `0x1564EF0` / `0x1564F00`, `0x005A` | `userId:i32(r16), x:i32(r19), y:i32(r16), timeToTarget:i32(r14)` |
| `HeroMoveCommand` `0x14E7780` / `0x1538BB0`, `0x001D` | `x:i32(r23), y:i32(r7)` |
| `AttackLaserRunCommand` `0x10A4110` / `0x14B5B00`, `0x0003` | `attackerId:i32(r30), targetId:i32(r1), laserColor:i32(r16), isDiminishedBySkillShield:bool, skilledLaser:bool, visualType:i32(r9)` |
| `AttackRocketCommand` `0x10A9A00` / `0x14B6130`, `0x0006` | `attackerId:i32(r16), targetId:i32(r21), rocketType:i32(r29), smokeId:i32(r29), hit:bool, isHeatSeeking:bool` |
| `AttackHitCommand` `0x11D3F30` / `0x14B5000`, `0x001C` | Nested `AttackTypeModule` ID `0x001B` (`get` `0x14B63F0`, `read` `0x14AC830`) with `attackTypeValue:i16`, then `attackerId:i32(r7), victimId:i32(r29), victimHitpoints:f64, victimShield:i32(r13), victimNanoHull:i32(r24), damage:i32(r24), skilled:bool, strike:bool, cannonType:i16`. The local native path has no hit event routing yet. |
| `JumpgateCreateCommand` `0x153EDA0` / `0x153EDB0`, `0x0D3D` | `gateId:i32(r22), factionId:i32(r2), designId:i32(r15), x:i32(r21), y:i32(r23), isVisibleOnMinimap:bool, showBubble:bool` |
| `JumpInitiatedCommand` `0x153EB50` / `0x153EB60`, `0x0013` | `mapId:i32(r7), gateId:i32(r6)` |
| `JumpDeniedCommand` `0x14AF990` / `0x1529C40`, `0x0012` | `mandatoryLevel:i32`; exact rotation is not yet mapped. |

`NativeWorldHandshake` now sends `MapChanged`, `ShipInitialization`, and one
`JumpgateCreate` per local starter-map portal after successful native login.
`NativeWorldPackets` also serializes the proven move, hero move, laser, rocket,
hit, and jump-initiation shapes for later event routing. Current `WorldPilot` data
does not supply clan, rank, premium, nano hull, visual modifier, jackpot, or
captain energy values; those entry fields are zero/false, and `typeId` uses
the local ship ID. The native session still ends after entry, so these
gameplay serializers are not yet dispatched to a live native client. Portal
design ID zero and the entry command order remain emulator choices pending a
captured native server burst.
