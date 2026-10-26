# Unity cockpit catalog and WebView diagnosis

## Current cockpit / in-game WebView support (2026-10-25)

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
| Hangar | `indexInternal.es?action=internalDock` / `internalDockEquipment`; ships from the cockpit catalog | Served. The native `GetHangerPost` 401 is **open**: its URL has not been captured, and 401s are now logged without cookies. |
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
categories before cockpit initialization. The installed Unity client contains
`localizations_assets_all.bundle`, and its Addressables `catalog.json` names
all seven categories under both `en` and `es`. The saved Live HAR has no
request for these categories. The failure is in the client-side localization
lookup or bundle load; the log does not distinguish those two cases. It is not
a response from `/resources/resource_command_center.xml`. Serving an invented
HTTP `resources.xml` would not repair that lookup. The current breaking-news
XML still has sparse synthetic labels, so its cockpit preview may show
placeholders until the bundle issue is diagnosed in a runtime capture.

The console's `GetHangerPost` 401 has no request URL. The saved Live capture
ends after initial news images. Metadata names `LaunchShow.GetHangerPost(url)`,
`GetNewsRequest`, `Start`, and button callbacks but does not expose their native
method bodies. Known `/unityApi/login.php`, `/unityApi/news.php`,
`/unityApi/links.php`, and both `/indexInternal.es` endpoint methods already
allow anonymous HTTP routing and perform `dosid` checks themselves. An
unmapped route can be rejected by the API fallback authorization policy; a
mapped route can return 401 for a missing or expired `dosid`. The API logs the
method, path, safe action, and endpoint name for future Unity 401 responses
without logging the cookie. A redirected capture of the Hangar click is needed
to choose the correct route and response shape.

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
