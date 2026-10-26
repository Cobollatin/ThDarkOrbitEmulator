# SPEC-01: Authentication, Gateway Forwarding & Placeholder Portal

Executable contract: `ThDarkOrbitEmulator.Tests/ClientTesting/AuthenticationAndPortalSpecificationTests.cs`
(supporting: `SasAuthenticationEndpointSpecificationTests`, `ClientStubResponseTests`, `BrandingPlaceholderComplianceTests`, `ApiServiceVerticalSliceTests`).

## 1. Scope

The local login pipeline from the public portal to the in-game start page:

| Step | Request | Slice | Result |
| :--- | :--- | :--- | :--- |
| 1 | `GET /` | `Features/Mocks/WwwDarkorbitCom/HomeEndpoint` | 200 login page (rewritten), `dosid` cookie |
| 1' | `GET /index.es` | `Features/IndexEs/IndexEsEndpoint` | 200 login page (rewritten), `dosid` cookie; launcher-download query variant served by `LocalLauncherDownload` |
| 2 | `POST /Sas/Authentication/Bigpoint` | `Features/Sas/Authentication/SasAuthenticationEndpoint` | 302 to `ProjectApi/Authentication?authUser=291&token=…` or `/?loginError=94` |
| 3 | `GET /ProjectApi/Authentication?authUser=291&token=…` | `Features/ProjectApi/Authentication` | 302 to `…?authUser=22&token=<rotated>`, session cookies |
| 4 | `GET /ProjectApi/Authentication?authUser=22&token=…` | same | 302 to `/indexInternal.es?action=internalStart&prc=100`, `lastLogin` |
| 5 | `GET /indexInternal.es?action=internalStart` | `Features/IndexInternal/InternalStart` | in-game start page bound to the `dosid` session |
| — | `GET /indexInternal.es?action=externalLogin` | `Features/IndexInternal/InternalStart/IndexInternalStartEndpoint` | **not implemented as a distinct action**; the local flow never emits it (logout uses `action=externalLogout` → `LocalLogoutAction`) |
| — | `POST /api/v1/identity/login` | `Features/Identity/Login/LoginEndpoint` | JSON `{ authenticated, token }` (CMS / API clients) |
| — | `POST /api/v1/identity/register` | `Features/Identity/Register/RegisterEndpoint` | Keycloak user + local account |

Shared helpers: `Features/Mocks/Common/EmulatorRedirectHelper`, `Features/Mocks/Common/ClientFacingGameHost`,
`Features/Common/ClientRedirectResponse`, `Features/GameAuth/GameAuthSessionStore`.

## 2. Host model

### 2.1 Definitions

- **Loopback host**: `HostString.Host` equals (case-insensitive) `localhost`, `127.0.0.1`, `::1` or `[::1]`.
  `HostString.Host` always returns IPv6 literals in brackets, so `[::1]` MUST be accepted.
- **Gateway host**: `api-game` (the Aspire service name YARP forwards to).
- **Trusted forwarded host**: the *first* comma-separated value of `X-Forwarded-Host`, only if it is a loopback host.
  The first value is taken from the comma-joined header (multi-valued `StringValues` entries join in order) and trimmed;
  a blank first value (`" , localhost:60952"`, `",localhost"`, whitespace only) is never trusted, even if a later value is loopback.
- **Trusted game original host**: `X-Original-Host` matching `\Agbl[1-9][0-9]*\.darkorbit\.com\z`, honoured only on an emulator host.

### 2.2 `EmulatorRedirectHelper.IsEmulatorHost(request)`

Returns `true` iff the request host is loopback/gateway **or** the trusted forwarded host is loopback.
Public hosts (`www.darkorbit.com`, `gbl3.darkorbit.com`, `sas.bpsecure.com`), look-alikes (`localhost.evil.example`)
and private LAN IPs are not emulator hosts. A non-loopback first `X-Forwarded-Host` value is ignored even if a
later value is loopback.

For classification only, a first `X-Forwarded-Host` value of `api-game` (any case, any port) also counts, so a public
request host with `X-Forwarded-Host: api-game` is an emulator host. `ClientFacingGameHost` does not echo such a gateway
forwarded host (it is not loopback) and falls back to the request scheme and host (§2.4 rule 4).

### 2.3 `EmulatorRedirectHelper.ToRedirectLocation(request, production, relative)`

Returns `relative` iff `IsEmulatorHost` and no `X-Original-Host` header is present; otherwise `production`.
`X-Original-Host` (any value) marks live-capture replay, which must reproduce the captured absolute URL.
Presence is checked, not content: an empty, whitespace-only or untrusted `X-Original-Host` value still selects `production`.

### 2.4 `ClientFacingGameHost`

| Request | `Resolve` | `ResolveHostName` | `ResolveScheme` |
| :--- | :--- | :--- | :--- |
| Emulator host + trusted `X-Original-Host: gblN.darkorbit.com` | `https://gblN.darkorbit.com` | `gblN.darkorbit.com` | `https` |
| Public / unknown host | `https://gbl3.darkorbit.com` | request host | request scheme |
| `api-game` + `X-Forwarded-Host: localhost:60952` + `X-Forwarded-Proto: https` | `https://localhost:60952` | `localhost` | `https` |
| Direct loopback (`localhost:60952`, `127.0.0.1:60952`, `[::1]:60952`) | `{scheme}://{host:port}` | request host | request scheme |
| Direct loopback `localhost:60952` + forwarded `localhost` without port | `{forwarded scheme}://localhost:60952` | `localhost` | forwarded scheme |
| Native API request on the compatibility listener with forwarded `localhost` and configured `ClientGateway:HttpsOrigin=https://localhost:60952` | `https://localhost:60952` | `localhost` | forwarded scheme |

Rules:
1. `X-Forwarded-Proto` is first-value, lower-cased, and honoured only for `http`/`https`; anything else falls back to the request scheme.
2. A non-loopback `X-Forwarded-Host` is never echoed.
3. An untrusted `X-Original-Host` (`www.darkorbit.com`, `gbl0…`, `gbl01…`, `gbl.darkorbit.com`, `gbl3.darkorbit.com:443`, values with leading whitespace or a trailing newline, `gbl3xdarkorbit.com`, `gbl3.darkorbit.com.evil.example`, arbitrary hosts) is never echoed; resolution proceeds as if it were absent. The pattern is anchored with `\A…\z`, matched case-insensitively, and a trusted value is echoed with its original casing. On a public request host, even a well-formed `gblN` original host is not trusted.
4. A trusted loopback forwarded host retains its casing and port (`" LOCALHOST:60952 , proxy"` → `https://LOCALHOST:60952`, host name `LOCALHOST`). If it has no port, the configured HTTPS WebView origin is used when its host matches; otherwise a loopback request host's port is added. A request that is an emulator host only through `X-Forwarded-Host: api-game` resolves to `{request scheme}://{request host}` and ignores `X-Forwarded-Proto`.
5. `ClientFacingGameHost` holds no host literals of its own: loopback checks, first-value parsing and emulator classification are delegated to `EmulatorRedirectHelper` (enforced by `ClientFacingGameHost_DelegatesHostTrustToEmulatorRedirectHelperAndAnchorsGameHostPattern`).

## 3. Gateway forwarding

`ThDarkOrbitEmulator.Gateway` (YARP, `https://localhost:60952`) routes every portal/game path to cluster `api-cluster`
(`api-game`). YARP's default transforms add `X-Forwarded-Host: localhost:60952` and `X-Forwarded-Proto: https`;
the API sees `Host: api-game`. All redirects issued through the gateway MUST therefore be relative, and all absolute
client URLs (links, Unity login payload, payment proxy) MUST use `ClientFacingGameHost.Resolve`.

## 4. Login page rewriting (`EmulatorRedirectHelper.RewriteLoginPageHtml`)

Applied by `GET /` and `GET /index.es` to the captured page. Only on emulator hosts without `X-Original-Host`;
otherwise, and for null/blank input, the HTML is returned byte-for-byte unchanged.

| Captured fragment (case-insensitive) | Rewritten to |
| :--- | :--- |
| `https://sas.bpsecure.com/Sas/Authentication/Bigpoint` (form action) | `/Sas/Authentication/Bigpoint` (query preserved) |
| `https://js.hcaptcha.com/1/api.js` | `/1/api.js` (local no-op captcha stub) |
| `https://sharedservices.bpsecure.com/bgc/js/bgc-2.0.0.min.js` | `/bgc/js/bgc-2.0.0.min.js` |
| `//assets.bpsecure.com/bpid/bpid.js` | `/bpid/bpid.js` |
| `<title>DarkOrbit Reloaded \| MMO de acción espacial</title>` | `<title>Space Emulator Portal \| Local Login (Placeholder)</title>` |
| `&copy; Bigpoint` | `Space Emulator Placeholder` |
| `<img src="https://darkorbit-22.bpsecure.com/…/logo_rework_shadow_420x168.png…" class="eh_mc_logo_center">` | `<img src="{PlaceholderLogoDataUri}" alt="Space Emulator Placeholder Logo" class="eh_mc_logo_center">` |

Invariants:
- Captcha and BGC scripts are neutralised by pointing them at local paths, so form submission never blocks on or leaks to an external host.
- `PlaceholderLogoDataUri` is a self-contained inline SVG (`[ SPACE EMULATOR ]` / `LOCAL SERVER PORTAL (PLACEHOLDER)`), contains no `"` (safe inside a double-quoted attribute), no external URL other than the SVG `xmlns`, and no DarkOrbit/Bigpoint text.
- Other `<img class="eh_mc_logo_center">` markup is left untouched; the rewrite is idempotent.
- Username/password inputs are preserved.

## 5. Sessions & cookies

### 5.1 `GET /` and `GET /index.es`
- `Set-Cookie: dosid=<id>; path=/; samesite=none; secure; HttpOnly` where `<id>` is the existing `dosid` cookie or a new 32-hex GUID.
- `aid`, `aip`, `aig` (and `acr` on `/index.es`) are cleared with `Max-Age=0`.
- `Cache-Control: no-store, no-cache, must-revalidate`; `/index.es` sends `Content-Security-Policy: frame-ancestors https://*.darkorbit.com/;`.

### 5.2 `POST /Sas/Authentication/Bigpoint`
- Blank username/password, unknown account, or Keycloak rejection → 302 `/?loginError=94` (production: `https://www.darkorbit.com/?loginError=94`).
- Success → `GameAuthSessionStore.CreateSessionAsync` issues a session (2 h lifetime) and an opaque redirect token; 302 to `ProjectApi/Authentication?authUser=291&token=…` via `ToRedirectLocation`.

Endpoint-level contract (`SasAuthenticationEndpoint`, anonymous, `POST /Sas/Authentication/Bigpoint`):

1. **Parsing** (`SasAuthenticationRequestContext.ParseAsync`): only a form content type is read. `username`, `password`, `g-recaptcha-response` and `h-captcha-response` are named fields (case-insensitive); every other form field goes to `Payload.Additional`. Cookies `dosid` and `aliyungf_tc` are named; other cookies go to `Cookies.Additional`. A non-form body (for example JSON) leaves both credentials `null`. Missing cookies are `null`; missing query values and headers are empty strings. Query `authUser`/`token`, captcha fields, cookies and extra fields are logged or captured only and never change the outcome.
2. **Check order**: the username is trimmed; the password is used verbatim. (a) A null, empty or whitespace username or password fails with **no** dependency call. (b) `GetAccountByUsernameQuery(trimmedUsername)`; `null` fails before Keycloak is called. (c) `RequestAccessTokenAsync(trimmedUsername, password)`; a null, empty or whitespace token fails without a session. (d) `CreateSessionAsync(account, accessToken)`. The request's cancellation token is passed to all three calls. The slice never validates local credentials, creates Keycloak users, rotates tokens, reads existing sessions or chooses a company.
3. **Failure response**: 302, `Content-Type: text/html; charset=UTF-8`, `Location` from `ToRedirectLocation(request, "https://www.darkorbit.com/?loginError=94", "/?loginError=94")`. No `Set-Cookie`, `Content-Security-Policy` or `Strict-Transport-Security`.
4. **Success response**: 302, same content type, `Location` from `ToRedirectLocation` with path `/ProjectApi/Authentication?authUser=291&token=<Uri.EscapeDataString(redirectToken)>` (production prefix `https://www.darkorbit.com`). `authUser` is always `291`, whatever the incoming query says. Exactly one `Set-Cookie: aliyungf_tc=<new 32-hex GUID>; Path=/; HttpOnly` (fresh per request), one `Content-Security-Policy: frame-ancestors 'none';` and one `Strict-Transport-Security: max-age=31536000`.
5. **Host selection** (both responses, section 2): the relative location is used for `localhost`, `127.0.0.1`, `[::1]`, `api-game`, or a non-loopback host whose first `X-Forwarded-Host` is loopback. Public hosts (`www.darkorbit.com`, `sas.bpsecure.com`), look-alikes (`localhost.evil.example`) and any request carrying `X-Original-Host` (replay) get the absolute production location.

### 5.3 `GET /ProjectApi/Authentication`
- The anonymous `ProjectApi` slice reads `authUser` and `token` from the query, then passes the token (or an empty string when absent) and request cancellation token to `RotateRedirectTokenAsync`. It rotates before issuing any session cookie. An absent, unknown, expired or already-consumed token yields 302 `/indexInternal.es?action=externalLogout` with no cookies, CSP or HSTS. The session store owns token validity and rotation; the endpoint does not create or look up sessions separately.
- Only `authUser=22` (ordinal case-insensitive equality) selects the internal gbl3 hop. `291`, absent, blank and whitespace-padded `22` select the www hop. The query value is a routing hint, not an authentication check: a valid token is rotated in either branch.
- The www hop redirects to `…/ProjectApi/Authentication?authUser=22&token=<Uri.EscapeDataString(new token)>` (production host `https://gbl3.darkorbit.com`; relative path on emulator hosts). It sets five cookies, in order: `dosid=<session dosid>` (HttpOnly, Secure, SameSite=None), cleared `aid`, `aip`, `aig`, and `acr=22` (30-day expiry). The rotated token differs from the consumed token.
- The internal hop redirects to `/indexInternal.es?action=internalStart&prc=100`. It sets the same first four cookies, clears `acr`, then sets `lastLogin=<current Unix seconds>` with a 30-day expiry. On emulator hosts `lastLogin` is host-only; on production hosts it carries `domain=.gbl3.darkorbit.com`. Both successful branches append one frame-ancestors CSP and one HSTS header, plus no-cache headers. The old redirect token cannot be replayed.

### 5.4 Identity REST API (`/api/v1/identity/*`)
- Feature-flagged by `Identity.Login` / `Identity.Register`; disabled → 404 `IdentityFeatureDisabled`.
- Blank input → 400 `IdentityInvalidInput`; unknown account → 401 `IdentityAccountNotRegistered`; Keycloak rejection → 401 `IdentityAuthenticationFailed`.
- Register creates the Keycloak user first and aborts without a local account if that fails.

### 5.5 Game authentication session store

- Session creation copies the account ID, username and game user ID. It copies the account's company only when that code exactly matches the local company catalog; missing, blank, unknown and differently cased codes become `null`. The store assigns a new `dosid` and opaque redirect token, records creation time, and sets expiration exactly two hours later.
- The session and redirect-token lookup are held in the distributed cache with the same absolute expiration. Looking up a blank identifier returns no session. Looking up a redirect token resolves its linked live `dosid` session; a token whose session is absent or expired cannot authenticate.
- A redirect-token cache record with malformed JSON, the wrong JSON shape, or an absent/blank `dosid` resolves to no session. A session cache record with malformed JSON or incompatible field types also resolves to no session. Rotation of either damaged record returns no token. Damage to one record does not affect another valid session; cache read failures themselves are not classified as missing records.
- Rotating a valid redirect token consumes its old cache entry, issues a different token for the same `dosid`, and preserves the original creation and expiration times. Reusing the consumed token returns no session. This contract covers sequential rotation; concurrent rotation is not guaranteed atomic by the cache operations.
- Invalidating a `dosid` removes its session. A remaining redirect-token cache entry then resolves to no session and cannot be rotated. Invalidating a blank ID is a no-op, and invalidating one session does not affect another.

### 5.6 Unity login (`GET /unityApi/login.php`)

The route is anonymous; authentication is the `dosid` cookie alone.

- **Request parsing** (`UnityLoginRequestContext.Parse`): named fields are read for the known cookies `dosid`, `lastLogin`, `_ga`, `_gcl_au`, `__qca`, `__bpid`, `_uetsid`, `_uetvid` and `_ga_TYEEV93NN7`. A known cookie that is not sent is `null`. All other cookies go into `Additional`. Metadata takes `User-Agent`, `X-Unity-Version`, `Accept-Encoding` and the request host; a missing header gives an empty string. Only the `dosid` cookie and the logged metadata affect the response.
- **Rejection** (`UnityLoginAction`): `Cache-Control: no-store, no-cache, must-revalidate` is set on every response. If the `dosid` is null, blank, longer than `LocalLogoutAction.MaxDosidLength`, unknown or expired, the response is `401` with no `Set-Cookie` and no `X-Custom-Header`. Length is checked before the session store, so an oversized cookie is never looked up.
- **Success**: the response sets `X-Custom-Header: DarkOrbitUnity` and refreshes `dosid` with `Path=/`, `HttpOnly`, `Secure` and `SameSite=None`. The body is `{ "data": "<inner JSON string>" }`. The inner payload holds that session's `userID`, `sessionID`, active hangar slot and saved language. A language outside the supported set falls back to `es`. `nextAction` is `internalWebGL`.
- **Starter map and faction**: `mapID` is the session company's catalog starter map (`mmo`→1, `eic`→5, `vru`→9, `tst`→13). `hangars.data.faction` is the company code. A null, blank, unknown or differently cased company gives map `1` and faction `0`.
- **Game server** (`gameServerIP`):
  1. If no `WorldServerOptions` is given, the field is left out of the JSON.
  2. Otherwise the builder takes the **first** lease in the mesh snapshot for the starter map whose `ExpiresAtUtc` is after the current time. A lease that is already expired is skipped.
  3. That lease's node must exist, be `Active`, and have a TCP port from 1 to 65535 inclusive. If so, the value is `{WorldTcpHost}:{WorldTcpPort}`.
  4. In every other case the value is `{WorldServerOptions.Host}:{WorldServerOptions.Port}`: no snapshot, no live lease, unknown node, `Draining`/`Offline` node, or port out of range. If the first live lease's owner can't be used, the builder does **not** try later leases.
- Resolving the game server only reads the mesh snapshot (`IServerMeshRegistry.GetSnapshotAsync`). Login never sends heartbeats, scales, rebalances or drains. The builder does not depend on the registry or on `LocalMapsXml`.

### 5.7 Unity links (`GET /unityApi/links.php`)

This anonymous route always returns HTTP 200 with `application/json`, six string properties in order:
`imprint`, `credits`, `forum`, `support`, `help`, `accountCenter`. `imprint` is
`https://legal.bigpoint.com/PE/imprint/es`, `credits` is empty, `forum` is
`/GameAPI.php?action=portal.redirectToBoard`, and `help` is `https://board-es.darkorbit.com/`.
`support` and `accountCenter` append `indexInternal.es?action=internalSupport` and
`indexInternal.es?action=internalAccount`, respectively, to the validated game host from section 2.4.
The response has an exact UTF-8 `Content-Length`, `X-Custom-Header: DarkOrbitUnity`,
`Cache-Control: no-store, no-cache, must-revalidate`, `Pragma: no-cache`, the captured
`Expires`, `Content-Security-Policy: frame-ancestors https://*.darkorbit.com/;` and HSTS headers.

The route's body is public and does not depend on query parameters or cookies. A nonblank `dosid`
of at most `LocalLogoutAction.MaxDosidLength` (128) is looked up. A live session refreshes exactly
one `dosid` cookie (`Path=/`, `HttpOnly`, `Secure`, `SameSite=None`); a missing, blank, unknown,
expired, invalidated or oversized identifier sets none. An oversized ID never reaches the store.
Anonymous Live behavior was not captured; public access and conditional refresh are emulator rules.

### 5.8 Unity news (`GET /unityApi/news.php`)

This anonymous route always returns HTTP 200 `application/json` shaped as
`{"data":[{"newsId":string,"seconds":integer},…]}`. At the capture snapshot
`2026-10-21T22:46:36-05:00`, the 19 entries retain the captured order, IDs and positive
countdowns. IDs are the `BE_` prefix plus each `CapturedNewsCatalog` entry name; the same order
is used for breaking-news XML layers. The response has the same cache, pragma, expires, CSP,
HSTS and custom headers as links, and sets no cookie. Query strings and request cookies do not
affect it; only the injected UTC clock does.

For positive captured duration `d`, let `e = floor((now - snapshot).TotalSeconds)`. If `e <= 0`,
`seconds = d`; otherwise `seconds = d - (e mod d)`. Thus subsecond time before the next whole
second does not decrement, and an exact expiry restarts at `d` instead of returning zero.
Nonpositive duration returns zero. Different offsets denoting the same instant give the same
result. Restarting a notice is an emulator choice; the captured Live behavior does not establish
that notices repeat indefinitely.

### 5.9 Flash breaking news (`GET /flashAPI/brNews.php`, `GET /xml/CommandCenter.xml`)

Both anonymous routes call the single stateless writer `CommandCenterNewsResponse.WriteAsync` and
return identical bytes and headers. The gateway forwards `/flashAPI/{**catch-all}` (route
`flash-api`) and the exact GET-only `/xml/CommandCenter.xml` (route `game-xml-command-center`) to
`api-cluster` with the `anonymous` policy. `/xml/CommandCenter.xml` is an IL2CPP literal that no
saved capture requests; it is an emulator compatibility alias.

The response is HTTP 200, `Content-Type: text/xml; charset=UTF-8`, and carries exactly one each of
`Expires: Thu, 19 Nov 1981 08:52:00 GMT`, `Cache-Control: no-store, no-cache, must-revalidate`,
`Pragma: no-cache`, `Content-Security-Policy: frame-ancestors https://*.darkorbit.com/;` and
`Strict-Transport-Security: max-age=31536000; includeSubDomains`. It sets no cookie and ignores
the query string, cookies, sessions and clock, so repeated calls are byte-identical.

The body is `ClientStubResponses.BreakingNews` (`CapturedNewsCatalog.BreakingNewsXml`): the line
`<?xml version="1.0" encoding="UTF-8"?>`, one `\n`, then an unformatted single-line `<root>`
without `\r`. `root` has one `layer` per `CapturedNewsCatalog` entry (19), in catalog order:

- Attributes, in order: `name` (the entry name, unique, without the `BE_` prefix), `prio`
  (invariant integer, at least 1 and nondecreasing in document order), `countdown="1"` and
  `category` (`deals`, `news` or `events`). `categoryOverrideKey` is never emitted.
- Child elements, in order: an empty `config`; `assignments` containing only
  `background assetID="<bare .jpg filename>"`; and `actions` containing exactly one
  `button style="primary" text="Continue" position="center"` whose `onclick` holds exactly one
  `link href="/indexInternal.es?action=internalDock"`.
- No absolute `http://` or `https://` URL and no `payment`, `overlay` or `event` element appears.

The Unity news IDs of section 5.8 are exactly `BE_` plus these layer names, in the same order.
The captured Live document had 694 layers with captured events, overlays, payments and external
links; the emulator keeps only the 19 captured IDs, priorities, categories and backgrounds, and
replaces actions with the local dock link.

### 5.10 Forum board link (`GET /GameAPI.php`)

The Unity links response in section 5.7 points `forum` to
`/GameAPI.php?action=portal.redirectToBoard`. The link target was captured; this route's HTML
response and rejection rules are emulator contracts. The endpoint permits anonymous HTTP access,
but a live `dosid` session is required to see the forum.

- The query must contain exactly one key spelled `action` and its single value must be exactly
  `portal.redirectToBoard` (ordinal case-sensitive comparison). A missing, differently cased,
  duplicate, empty or other action returns 404 before the session store is called. Mixed-case
  duplicate keys also fail; ASP.NET's case-insensitive query lookup combines their values.
- After that action gate, a missing, blank, oversized (more than 128 characters), unknown or
  expired `dosid` gives 401. A missing, blank or oversized cookie is not looked up. A live session
  whose non-null company is not in the local company catalog gives 403. These decisions precede
  validation of forum parameters, so a bad action wins over a bad session, and a bad session or
  company wins over a bad topic or category.
- A valid request uses the same `LocalLinksRequest` forum validation and page renderer as
  `internalForum`. Only `action`, `from`, `prc`, `lang`, `topic`, `category` and `q` are accepted,
  with exactly one value per key, values at most 64 characters and no control characters. Key
  spelling is exact. `from`, `prc` and `lang` are tolerated navigation values. An omitted or empty
  `topic` opens the index; a known topic opens its detail. Topic and category IDs are 1–32
  lowercase letters, digits or hyphens. A syntactically valid unknown topic or category, or a
  topic outside the selected category, gives 404. An invalid key, value, ID or combination gives
  400.
- `q` is trimmed; empty or whitespace-only search opens the unfiltered page. Nonempty search
  must be 2–40 characters after trimming and cannot be combined with a topic. Search text is
  never echoed in errors. A session with no selected company may read the forum.
- The endpoint writes the resulting status and HTML as `text/html; charset=UTF-8` with
  `Cache-Control: no-store`. It sets no cookie. A 401 page links to sign-in; other errors link to
  the local Forum page.

### 5.11 Third-party telemetry and DNS sinks (`Features/Mocks/*`)

The Vuplex portal and Unity runtime call analytics, advertising, Unity CDP and DNS-over-HTTPS hosts.
The emulator answers them locally, and no request reaches a real third party. Each DNS host has its own
folder under `Features/Mocks/` with exactly one source file. That file holds one anonymous
`EndpointWithoutRequest` per captured route, uses `RoutePrefixOverride(string.Empty)` and is tagged
`DNS:<host>`. Handlers read no request data (body, query, headers or cookies). They also use no session
store, clock or outbound HTTP client, and they never set a cookie, so every call gives the same response.

| DNS host | Route | Status | Content type | Body / extra headers |
| :--- | :--- | :--- | :--- | :--- |
| `www.google-analytics.com` | `POST /g/collect` | 204 | none | empty |
| `pagead2.googlesyndication.com` | `POST /ccm/collect` | 204 | none | empty |
| `pixel.quantcount.com` | `POST /pixel`, `GET /pixel` | 204 | none | empty |
| `bat.bing.net` | `POST /actionp/0`, `GET /action/0` | 200 | `text/plain; charset=UTF-8` | empty |
| `bat.bing.com` | `GET /bat.js` | 200 | `application/javascript; charset=UTF-8` | `/* mock bat.js */` |
| `bat.bing.com` | `GET /p/action/97130958.js` | 200 | `application/javascript; charset=UTF-8` | `/* mock action js */` |
| `cdp.cloud.unity3d.com` | `POST /v1/events` | 200 | none | empty; `Vary: Origin`, `Via: 1.1 google`, `Alt-Svc: h3=":443"; ma=2592000,h3-29=":443"; ma=2592000`, each exactly once |
| `chrome.cloudflare-dns.com` | `POST /dns-query` | 200 | `application/dns-message` | empty (no DNS answer) |

Both script bodies are a single-line comment with no URL, so no further script loads or tracking calls follow.
The gateway forwards each path to `api-cluster` with the `anonymous` policy, and exactly one route matches each
path. The Bing beacon routes use `/actionp/{**catch-all}` and `/action/{**catch-all}` at the gateway, but the API
service implements only the captured `/0` suffix.

### 5.12 Browser support resource stand-ins (`Features/Mocks/*`)

The portal loads consent, tag, analytics-support and background challenge resources from separate
DNS hosts. The emulator serves deterministic local stand-ins. These are emulator response contracts
for paths seen in captures or referenced by captured markup; they do not reproduce the third-party
implementations. All endpoints are anonymous GET routes at the root prefix, tagged with their DNS
host. They ignore query strings, cookies and request bodies, set no cookie, and use no session store,
clock or outbound HTTP client. Each listed path is forwarded anonymously to `api-cluster` by the
gateway; `/bgc/{**catch-all}` covers all three BGC resource subpaths.

| DNS host | Route | Content type | Exact UTF-8 body |
| :--- | :--- | :--- | :--- |
| `cmp.inmobi.com` | `/choice/qB5-sBEDaawtz/darkorbit.com/choice.js` | `application/javascript; charset=UTF-8` | `/* mock choice.js */` |
| `cmp.inmobi.com` | `/tcfv2/cmp2.js` | `application/javascript; charset=UTF-8` | `/* mock cmp2.js */` |
| `cmp.inmobi.com` | `/geoip` | `application/json; charset=UTF-8` | `{}` |
| `cmp.inmobi.com` | `/GVL-v3/vendor-list-trimmed-v1.json` | `application/json; charset=UTF-8` | `{"vendors":[]}` |
| `www.googletagmanager.com` | `/gtm.js` | `application/javascript; charset=UTF-8` | `/* mock gtm.js */` |
| `www.googletagmanager.com` | `/gtag/js` | `application/javascript; charset=UTF-8` | `/* mock gtag.js */` |
| `www.googletagmanager.com` | `/ns.html` | `text/html; charset=UTF-8` | `<!doctype html><html><body></body></html>` |
| `assets.bpsecure.com` | `/bpid/bpid.js` | `application/javascript; charset=UTF-8` | `/* mock bpid.js */` |
| `secure.quantserve.com` | `/quant.js` | `application/javascript; charset=UTF-8` | `/* mock quant.js */` |
| `rules.quantcount.com` | `/rules-p-qB5-sBEDaawtz.js` | `application/javascript; charset=UTF-8` | `/* mock rules */` |
| `sharedservices.bpsecure.com` | `/bgc/js/{**catch-all}` | `application/javascript; charset=UTF-8` | `/* mock sharedservices js */` |
| `sharedservices.bpsecure.com` | `/bgc/css/{**catch-all}` | `text/css; charset=UTF-8` | empty |

Every route above returns 200. Script bodies are single-line comments with no URL. The geo-IP
response is an empty object; the vendor-list response has only an empty `vendors` array. The
`/bgc/img/{**catch-all}` route also returns 200 with `image/png` and a stable 1×1 PNG (valid PNG
signature and width/height fields). BGC's CSS and image stand-ins contain no original challenge
assets. The `ns.html` route is referenced by captured home-page markup, not a directly captured
request.

### 5.13 Challenge widget and Unity remote-config stand-ins (`Features/Mocks/*`)

The portal markup references the hCaptcha loader, the challenge widget fetches a logo from one of
several per-tenant `*.w.hcaptcha.com` hosts, and the Unity runtime posts its analytics remote-config
request to `config.uca.cloud.unity3d.com`. The emulator answers each with a local, anonymous,
root-prefixed endpoint (one public sealed endpoint per host folder). None of them read the request,
cookies, a session store, a clock or an outbound HTTP client, and none set a cookie.

| DNS host(s) | Method and route | Gateway route | Status | Content type | Body |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `js.hcaptcha.com` | `GET /1/api.js` | `hcaptcha-js` (any method) | 200 | `application/javascript; charset=UTF-8` | `/* mock hcaptcha api.js */` |
| `c47f56fecf32.w.hcaptcha.com`, `7dd0d2f1479d.w.hcaptcha.com`, `ac36ddf2442a.w.hcaptcha.com` | `GET /logo.png` | `hcaptcha-logo` (any method) | 200 | `image/png` | the shared 1×1 PNG (byte-identical to the §5.12 BGC image) |
| `config.uca.cloud.unity3d.com` | `POST /` | `unity-config` (`POST` only) | 200 | `application/json;charset=utf-8` | captured response body of flow `65d1d8c1-84c4-4036-893f-ffedc42657fd`, or empty |

Rules:

1. The loader script is a single-line comment with no URL, so the captcha widget never initialises
   and the placeholder login form stays usable without a challenge.
2. All three logo hosts share one endpoint, tagged with all three DNS names; the response bytes do
   not depend on the host.
3. The Unity config response appends, exactly once each, `Access-Control-Allow-Origin: *`,
   `Server: Jetty(9.4.z-SNAPSHOT)`, `Via: 1.1 google` and `Alt-Svc: h3=":443"; ma=2592000`.
4. The Unity config body comes from `FlowCaptureLoader`, which searches `_reversing/Requests/full_flow.jsonl`
   then `extracted_flow.jsonl` upward from the application base directory. The capture files are
   git-ignored; when the flow is absent the body is empty but status and headers are unchanged. When
   present, the body is a JSON object. The body is loaded once per process and is identical for
   every request.
5. `unity-config` is the only gateway route on path `/` that allows `POST`; other `/` routes
   (the portal home page) never list `POST`, so root `GET` requests reach the home page slice.

### 5.14 Portal static assets (`Features/Mocks/Darkorbit22BpsecureCom`)

The `darkorbit-22.bpsecure.com` host serves four anonymous, root-prefixed GET routes. The gateway sends each route to `api-cluster` under its `darkorbit-static-*` route with the `anonymous` policy. The CSS and JavaScript routes accept any suffix after `/css/` and `/js/`; they return status 200 with, respectively, empty `text/css; charset=UTF-8` and the exact inert body `/* mock darkorbit-22 js */` as `application/javascript; charset=UTF-8`. `/favicon.ico` returns status 200, `image/png` and the shared 1×1 PNG, despite its `.ico` suffix. None of these routes sets a cookie or requires a session.

`GET /do_img/{**catch-all}` resolves images in this order:

1. A case-insensitive path in the captured Command Center manifest whose type is `jpg` or `png` receives deterministic, generated PNG artwork. The response is 200 with `image/png`, exact `Content-Length`, `Cache-Control: public, max-age=3600`, `X-Content-Type-Options: nosniff` and a strong ETag made from the manifest hash and version. An exact or weak matching `If-None-Match` yields 304 with the same cache and ETag headers and no body. Even `.jpg` manifest paths advertise PNG because the generated bytes are PNG.
2. The exact, case-sensitive `/do_img/global/items/ship/pusat-plus_100x100.png` and `/do_img/global/items/ship/phoenix_100x100.png` paths receive distinct locally generated 100×100 PNGs, status 200 and `Cache-Control: public, max-age=3600`.
3. All remaining paths, including paths longer than the manifest's 256-character lookup limit, receive the shared 1×1 PNG with status 200 and `image/png`; they carry no ETag or cache directive. This is a placeholder asset, not captured image content.

Query strings and cookies do not change the returned bytes. The generated ship artwork and Command Center artwork are emulator placeholders; their original image bytes were not retained.

### 5.15 Public home page (`GET /`, `Features/Mocks/WwwDarkorbitCom/HomeEndpoint`)

`HomeEndpoint` is the only source file in `Features/Mocks/WwwDarkorbitCom`. It is anonymous, root-prefixed and tagged `DNS:www.darkorbit.com`. The gateway's `home-page` route forwards `/` with an empty method list (any method) under the `anonymous` policy to `api-cluster`. The `unity-config` route claims only `POST /` (section 5.13). No other gateway route matches `/`.

Dispatch order:

1. **Payment proxy first.** If `LocalPaymentProxyResponse.IsRequest` holds, the request goes to the payment proxy before any home-page header or cookie is written. The test requires path `/`, `action=internalPaymentProxy` (case-insensitive), and either a request host matching `gblN.darkorbit.com` or a trusted redirected game host (section 2.4). A missing, blank or unknown `dosid` returns 401 with no body, no `Set-Cookie` and no home-page headers. A session without a valid company returns 302 to `/indexInternal.es?action=internalCompanyChoose`. On any other host, including `www.darkorbit.com` and plain loopback, `action=internalPaymentProxy` is ignored and the home page is served.
2. **Home page.** Status 200, `text/html; charset=UTF-8`, and exactly five `Set-Cookie` values, in this order:
   1. `dosid=<id>; path=/; samesite=none; secure; HttpOnly`. `<id>` is the request's non-blank `dosid` cookie, unchanged. Otherwise it is a fresh 32-character lowercase hex GUID, different on each request.
   2. `aid`, `aip` and `aig`, each cleared with `=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure`.
   3. `acr=22; expires=Wed, 15-Apr-2026 00:12:29 GMT; Max-Age=2592000; path=/; samesite=none; secure`. The value and dates are fixed, copied from the capture. This cookie is set for 30 days, not cleared.
3. Headers, each sent exactly once: `Expires: Thu, 19 Nov 1981 08:52:00 GMT`, `Cache-Control: no-store, no-cache, must-revalidate`, `Pragma: no-cache`, `Content-Language: es`, `Strict-Transport-Security: max-age=31536000; includeSubDomains`, and one `Content-Security-Policy` with a single `frame-ancestors` directive. It starts with `https://*.darkorbit.com/` and ends with `;`.

Body: `GetBody(request, capturedHtml)` selects a fixed, self-contained placeholder page when the git-ignored `_reversing/Requests/home_html_raw.log` capture is null, empty or whitespace. Its title is exactly `Space Emulator Portal | Local Login (Placeholder)`; it contains no official brand text or remote assets and is identical for local, public and replay hosts. With nonblank captured HTML, the body passes through `RewriteLoginPageHtml` (section 4). The endpoint does no rewriting of its own. Gateway-forwarded loopback requests get the same body as direct loopback requests. A request with `X-Original-Host` (replay) gets the same body as a public-host request, which is the unrewritten capture. Query strings and cookies other than `dosid` do not change the body. The rewritten body no longer contains the legacy SAS form action, and rewriting it again changes nothing.

## 6. Branding

The portal and CMS use only generic placeholder branding (`Space Emulator Portal | Local Login (Placeholder)`,
`Space Emulator CMS`). No rewrite may introduce official DarkOrbit or Bigpoint names or logos
(`BrandingPlaceholderComplianceTests`).

## 7. Test traceability

| Requirement | Test |
| :--- | :--- |
| 2.2 host classification (localhost, 127.0.0.1, [::1], api-game, forwarded, blank and multi-valued forwarded hosts, gateway forwarded host) | `IsEmulatorHost_*`, `GatewayForwardedHost_IsEmulatorButClientFacingHostEchoesRequestHost` |
| 2.3 relative vs production redirects, replay by header presence, login failure | `ToRedirectLocation_*` |
| 2.4 client-facing host, proto normalisation, spoofing, anchored game-host pattern, verbatim forwarded host and delegated host trust | `Resolve_*`, `ApiServiceVerticalSliceTests.ClientFacingGameHost_DelegatesHostTrustToEmulatorRedirectHelperAndAnchorsGameHostPattern` |
| 4 form action, captcha/BGC/bpid scripts, title, footer, SVG logo | `RewriteLoginPageHtml_*`, `PlaceholderLogo_IsSelfContainedGenericSvg` |
| 5.1 `dosid` issuance and reuse | `IndexEs_*` |
| 5.2 SAS form parsing, blank/account/Keycloak/session check order, cancellation forwarding, failure and success headers, token escaping and host-dependent locations | `SasAuthenticationEndpointSpecificationTests`, `ApiServiceVerticalSliceTests.SasAuthentication_ChecksBlankThenAccountThenKeycloakBeforeSessionAndUsesSharedRedirects` |
| 5.3 two-hop handshake, missing/unknown tokens, exact `authUser=22` branch, cookie count and headers, token rotation/replay, cookie domains and vertical-slice boundary | `ProjectApi_*`, `ApiServiceVerticalSliceTests.ProjectApiAuthentication_RotatesBeforeCookiesAndKeepsSessionWorkInGameAuth` |
| 5.5 cache lifetime, token consumption, invalidation, damaged-record rejection and company normalization | `SessionStore_*`, `ApiServiceVerticalSliceTests.GameAuthSessionStore_UsesDistributedCacheBehindSharedFeatureBoundary`, `ApiServiceVerticalSliceTests.GameAuthSessionStore_FailsClosedWhenEitherCachedAuthenticationRecordIsMalformed` |
| 5.6 Unity login request parsing, starter map/faction fallback, live-lease game-server selection and fallbacks | `UnityLogin_*`, `UnityLoginRequestContext_*`, `UnityLoginEndpointHardeningTests`, `ApiServiceVerticalSliceTests.UnityLogin_RejectsBeforeCookieRefreshAndResolvesGameServerOnlyFromMeshSnapshot` |
| 5.7 public links shape, validated host, forwarded host and optional bounded session refresh | `UnityLinksEndpointTests`, `ApiServiceVerticalSliceTests.UnityPublicResources_KeepLinksSessionOptionalAndNewsClockOnly` |
| 5.8 news shape, captured order, clock countdown and exact expiry | `UnityNewsEndpointHardeningTests`, `ApiServiceVerticalSliceTests.UnityPublicResources_KeepLinksSessionOptionalAndNewsClockOnly` |
| 5.9 brNews and CommandCenter.xml alias headers, shared stateless writer, layer order, attribute and element shape, local actions and gateway route | `FlashBreakingNewsEndpointTests`, `ClientStubResponseTests.CommandCenterNewsWriter_EmitsExistingXmlAndHeaders`, `ClientEndpointCatalogTests.LegacyCommandCenterRoute_ReachesApiServiceOnlyForGet`, `ApiServiceVerticalSliceTests.FlashBreakingNews_SharesOneStatelessWriterBetweenBrNewsAndCommandCenterAlias` |
| 5.10 captured forum link target, canonical action, rejection precedence, session bounds, forum validation and HTML response | `GameApiForumActionTests`, `ApiServiceVerticalSliceTests.GameApiForum_UsesCanonicalActionGateBeforeBoundedSessionLookupAndSharedForumValidation` |
| 5.11 telemetry/DNS sink status, content type, body, edge headers, request independence, gateway routes and per-host anonymous stateless slices | `TelemetrySinkMockEndpointTests`, `ApiServiceVerticalSliceTests.TelemetrySinkMocks_AreAnonymousRootRoutedAndStatelessPerDnsFolder` |
| 5.12 browser support resource status, exact response bytes, valid minimal JSON and PNG shape, request independence, gateway forwarding and anonymous host slices | `BrowserSupportMockEndpointTests`, `ApiServiceVerticalSliceTests.BrowserSupportMocks_KeepHostSlicesAnonymousAndRequestIndependent` |
| 5.13 hCaptcha loader and logo bytes, Unity config headers, request-independent capture-backed body, POST-only root gateway route and single-endpoint anonymous host slices | `ChallengeAndUnityConfigMockEndpointTests`, `ApiServiceVerticalSliceTests.ChallengeAndUnityConfigMocks_KeepHostSlicesAnonymousAndRequestIndependent` |
| 5.14 CSS, JS, favicon and image response bytes, manifest revalidation, ship icon precedence, fallback boundaries, gateway routes and anonymous host placement | `StaticAssetMockEndpointTests`, `ApiServiceVerticalSliceTests.PortalStaticAssets_KeepAnonymousHostRoutesAndImageFallbackOrder` |
| 5.15 home-page cookie order and `dosid` reuse, single no-store/CSP/HSTS headers, capture-absent generic fallback, emulator-only rewrite, replay parity, payment-proxy precedence before cookies, any-method anonymous gateway route | `HomePageMockEndpointTests`, `ApiServiceVerticalSliceTests.HomePage_DispatchesPaymentProxyBeforeCookiesAndRewritesOnlyThroughSharedHelper`, `BrandingPlaceholderComplianceTests.HomePageFallback_UsesGenericPlaceholderWithoutExternalAssets` |
| Gateway-forwarded rewrite end-to-end | `ClientStubResponseTests.GatewayForwardedRequest_ResolvesEmulatorHostAndRewritesLoginFormToLocalPath` |

## 8. Known gaps

- Section 2: a request whose host is public but whose first `X-Forwarded-Host` is `api-game` is classified as an emulator host (relative redirects), yet `ClientFacingGameHost.Resolve` echoes the public request host. YARP never sends `api-game` as the forwarded host, so this combination is only reachable by a hand-crafted request; it is pinned by tests rather than changed.

- Section 5.2 is verified by direct endpoint invocation with fakes, not through the full HTTP pipeline or real Keycloak. An existing account whose Keycloak user is missing produces the same `loginError=94` as a wrong password; the two cases cannot be told apart from the response. The `aliyungf_tc` cookie has no `Secure` or `SameSite` attribute.
- `action=externalLogin` has no dedicated handler; if the client is observed requesting it, add a slice that applies section 2.3 host rules.
- Native Unity/Vuplex login is not exercised (no mitmproxy CA in the Windows store). Section 5.6 is verified at action/builder level only. A lease that expires exactly at the current time counts as expired (strict `>`), but this is not tested because the builder reads the clock itself.
- Section 5.9 is verified by direct endpoint invocation only. The native client has not been observed requesting `/xml/CommandCenter.xml`, and parity with the 694-layer Live document (events, overlays, payments, localized text) is not implemented.
- Section 5.11 is verified by direct endpoint invocation only. The DNS-over-HTTPS sink returns an empty `application/dns-message`, which is not a valid DNS reply. Clients must fall back to system DNS, and this fallback has not been verified with the native client. Bing beacon suffixes other than `/0` reach the API service and get 404.
- Section 5.12 is verified by direct endpoint invocation only. The consent, tag and challenge resources are intentionally inert; native-client behavior with these stand-ins has not been verified.
- Section 5.13 is verified by direct endpoint invocation only. The Unity config body depends on a git-ignored capture file, so a clean checkout serves an empty body; whether the Unity runtime tolerates an empty remote-config reply is unverified.
- Section 5.14 is verified by direct endpoint invocation only. The ship, Command Center and fallback PNGs are local placeholders, not retained official asset bytes.
- Section 5.15 is verified by direct endpoint invocation only. `_reversing/Requests/home_html_raw.log` is git-ignored; a clean checkout serves the generic fallback with no login form, so login requires the retained capture or another local form. The fixed `acr` expiry date (2026-04-15) is already in the past.
