# SPEC-05: Out-of-Game Menus 16–29 & Authoritative Economy

## Boundary and authority

These are emulator pages under `GET /indexInternal.es?action=…` and form actions under `POST /indexInternal.es?action=…`, dispatched by `Features/IndexInternal/InternalStart/IndexInternalStartEndpoint.cs`. They are not official Unity menu contracts: no Live payment, voucher, battle pass, or dispatch page was captured, so names, amounts and limits are emulator-defined. The `dosid` cookie identifies the pilot. Missing sessions yield 401; pilots without a company are redirected to `internalCompanyChoose`. Successful mutations redirect with HTTP 303 to a notice URL; the page confirms the notice only when saved state agrees. Pages return HTML with `Cache-Control: no-store`.

For `POST action=internalSpaceSearchExplore`, method and action-query validation precede identity resolution. A missing, empty, whitespace-only, or longer-than-128-character `dosid` is rejected with HTTP 401 and the fixed HTML sign-in error before session lookup or form parsing; the cookie is never echoed. The limit is inclusive: a 128-character nonblank cookie may reach the session store and, if it resolves to a company-selected pilot, the form can be applied. This boundary uses `LocalLogoutAction.MaxDosidLength`, matching the local client-facing session routes.

After session and company admission, Space Search explore accepts an ASP.NET Core form body only. Missing or unsupported form media types return HTTP 400 before body reading. The parsed form must contain exactly one `planet` field and no others, with that key spelled in lowercase; decoded duplicate keys, case variants, extra fields, and multipart file parts return 400. Multipart with only the canonical text field remains valid. The decoded planet value must be 1–32 lowercase ASCII letters before the equipment store is called; a syntactically valid but uncataloged planet returns 404 from the store. Unreadable form bodies return the fixed 400 HTML error without exposing parser details. All failures keep the account balance and searched planets unchanged and carry `Cache-Control: no-store`.

The Space Search GET page uses the first owned inventory item's name for a reward when present; otherwise it uses the catalog reward name. Search matches that same displayed name without changing saved state. The owned name is untrusted display data: the result row, selected-planet detail, and saved-search completion notice each HTML-encode it before insertion into markup. A name containing HTML syntax remains searchable as text and must never become an HTML element in any of those three projections. The completion notice still requires a catalog planet already present in the account's searched-planet set; its query parameter alone cannot create the notice.

The Space Search detail panel may open a catalog planet named by `planet` even when the active `q` search excludes it. Search rows and tab counts still reflect only matching planets. Such an out-of-result detail has no Previous or Next link and retains a Close link that preserves the active filter and search. When the selected planet matches `q` but sits outside the active status filter, Previous and Next walk the text-search result rather than the filtered rows. These links never add a planet absent from the selected navigation sequence.

Common page-query rules (as in SPEC-04): exact lowercase keys from an allow-list; each key once; values at most 64 characters without control characters; the `action` value is canonical (wrong casing is 400). `from`, `prc` and `lang` are accepted navigation values and never mutate state. POST action queries contain zero keys or exactly one canonical `action` key.

## Route and input matrix

| # | Menu | GET `action` and page query | POST `action` and form fields | State transition and guards |
|---|---|---|---|---|
| 16 | Uridium/Payment & Banking | `internalPayment`: `detail`, `claimed` (package ID: 1–32 ASCII letters/digits/`-`/`_`), `filter` (1–16 lowercase letters), `q` | `internalPaymentClaim`: `package` | Practice grant only: no price, checkout, provider or real currency. Each package is claimed once per account (ledger key `payment:<id>`). Success: 303 to `…internalPayment&claimed=<id>`. Disabled/unknown packages fail. |
| 17 | Bonus Codes/Vouchers | `internalVouchers`: `filter` (`all`, `available`, `redeemed`, `unavailable`), `voucher`, `redeemed`, `q` (1–32, trimmed, letters/digits/space/`-`) | `internalVoucherRedeem`: `code` (1–32 ASCII letters/digits/`-`) | One redemption per code per account (ledger key `voucher:<code>`). Success: 303 to `…&redeemed=<code>`, or the plain list if the admin code is not page-safe. `redeemed` and `voucher` cannot be combined. |
| 18 | Daily Login/Calendar | Not implemented | — | No route; the action falls through to the default `internalWebGL&from=internalStart` redirect. |
| 19 | Battle Pass | `internalEvents`: `detail` (tier ID shape), `filter` (`all`, `ready`, `in-progress`, `claimed`, `unavailable`), `q` (blank, or trimmed 2–40), `claimed` (tier ID) | `internalBattlePassClaim`: exactly `tier` | Tier status: `unavailable` (disabled) → `in-progress` (progress < target) → `ready` → `claimed`. `claimed` is terminal and survives later disabling. Claim pays once (`battlepass:<tier>`); success: 303 to `…&claimed=<tier>`. |
| 20 | Dispatch/Retrievers | Same `internalEvents` page; notice `expedition` = `started` or `claimed` | `internalExpeditionStart`: exactly `start=1`; `internalExpeditionClaim`: exactly `claim=1` | Expedition: `available` (credits ≥ 100) / `insufficient-credits` → `running` (2 minutes) → `ready` → `claimed`; `reward-unavailable` when the reward stack is full. Start debits 100 credits once (`expedition:start`). `claimed` and `expedition` notices are exclusive. The countdown rounds up and never shows 0 minutes. |
| 21 | Eternal Blacklight | Not implemented | — | As menu 18. |
| 22 | Astral Ascension | Not implemented | — | As menu 18. |
| 23 | Chromin Rush | Not implemented | — | As menu 18. |
| 24 | Logfiles & Booty Keys (Logbook) | `internalLogbook`: `category` (1–16), `source`, `asset` (1–64, not with `source=demo`), `q` (2–40, no edge spaces, letters/digits/space/`-`/`_`), `page` (positive), `entry` (canonical entry ID) | None | Read-only view over the newest `economy.ledger` rows (limit 200, 8 per page) plus generated demo entries. Unknown category is 404; another account's entry is 403. No separate booty-key inventory exists. |
| 25 | Ship Warp | Not implemented | — | As menu 18. Hangar slot selection (SPEC-04 menu 1) is the closest implemented feature. |
| 26 | Account Center/Settings | `internalAccount`: `saved` (revision 1–1,000,000), `unchanged` (`1`); the two are exclusive | `internalAccountSave`: `language`, `sound`, `notifications`, `rev` | Preferences (default `es`, sound on, notifications on) are revisioned. Save with current `rev` applies and redirects to `…&saved=<rev>`. An identical save goes to `…&unchanged=1`. A stale `rev` is a conflict. Revision: 1–7 ASCII digits, no sign or space. |
| 27 | Internal Start / Command Center News | `internalCommandCenter`: `category`, `item` (captured catalog entries), `sort` (`priority`, `ending`, `name`), `urgency` (`ending-soon`, `this-week`, `running`, `permanent`), `q`. `internalStart`/`internalMapRevolution` open the local spacemap | None | Read-only, from `CapturedCommandCenterCategories` and `CapturedNewsCatalog`. A shape error is 400 before session checks; signed-out is 401. A mismatched category/item pair is rejected. |
| 28 | External Logout | `externalLogout` (any casing; query ignored, never reflected) | — | Invalidates the session if the cookie is 1–128 characters, always emits the expired `dosid` cookie, and redirects to `internalWebGL&from=externalLogout` with `no-store, no-cache, must-revalidate`. |
| 29 | Internal Logout (signed-out step) | `internalWebGL&from=externalLogout` | — | Does not issue a fresh session cookie. Redirects to `/index.es?action=externalHome&loginError=94`. No separate `internalLogout` action exists. |

## Economy invariants (`Features/Economy/EconomyStore.cs`)

There is no `IEconomyService` interface. Endpoints use the concrete `EconomyStore` over the PostgreSQL `economy` schema.

1. **Atomicity.** Each mutation runs in one transaction: the balance check, debit/credit, inventory change and ledger insert commit together or not at all.
2. **Non-negative balances.** A debit that would make a balance negative returns `InsufficientFunds` / `InsufficientInputs` / `InsufficientCredits`, and no row changes.
3. **Idempotency.** Every grant or purchase has an `idempotency_key` scoped by account (`payment:`, `voucher:`, `quest:`, `battlepass:`, `expedition:start`, shop/assembly request keys). A replay returns `Duplicate` (purchase/craft) or `AlreadyClaimed` (events) and does not mutate state again.
4. **Audit ledger.** `economy.ledger` is append-only: `asset_id`, `delta`, `balance_after`, `reason`, `menu_source`, `occurred_at`. It is read with a stable per-account sequence (`row_number() OVER (ORDER BY id)`). Logbook only reads it.
5. **Grant caps.** A single practice payment or voucher grant is at most 1,000,000 (`LocalPayment.MaxGrant`, `LocalVouchers.MaxGrant`).
6. **Closed outcomes.** `EconomyPurchaseResult` = {Applied, Duplicate, Invalid, Unavailable, InsufficientFunds}; `EconomyCraftResult` = {Applied, Duplicate, Invalid, NotFound, Unavailable, InsufficientInputs}; `LocalEventResult` adds UnknownTier, AlreadyClaimed, NotReady, InventoryFull, Rejected.
7. **Admin catalog.** Admin `RewardOffersAsync(source)` offers (Postgres) replace the built-in payment, voucher and battle pass catalogs when present. Disabled offers render as `unavailable` but keep previously claimed state.

## Payment and voucher catalog rules (`LocalPayment.cs`, `LocalVouchers.cs`)

**Payment (menu 16).**
- With no offer list, the built-in five-package catalog is used. Every built-in package is enabled and grants `credits`.
- Any offer list, including an empty one, replaces the built-in catalog completely.
- Package IDs are matched ordinally, so wrong casing or added spaces find nothing.
- Status precedence is `claimed` > `available` (enabled) > `unavailable`.
- The `available`, `claimed` and `unavailable` filters split the catalog with no overlap and no gaps.
- `AssetLabel` labels only `credits` and `uridium`. Any other asset ID is shown as is.
- A filter must be 1–16 lowercase letters (`IsWellFormedFilter`) and must also be a listed filter (`IsKnownFilter`). A well-formed but unlisted filter (`pending`) has no detail view and renders as `all`.
- Search is 2–40 characters (letters, digits, space, `-`, `_`) with no leading or trailing space. It matches ID, name or asset ID, ignoring case.
- A detail view exists only when the package is listed under the current filter and search.
- The claimed notice appears only for a known package that this account has claimed. Its amount comes from the catalog. The balance line is shown only for `credits` grants.

**Vouchers (menu 17).**
- Status precedence is `redeemed` > `unavailable` > `balance-limit` > `available`:
  - `redeemed` is terminal. It holds even if the offer is later disabled or goes over the cap.
  - `unavailable` means the offer is disabled or its amount is outside 1–`MaxGrant`.
  - `balance-limit` applies only to `credits`: credits plus the amount would exceed `int.MaxValue`. Exactly `int.MaxValue` is still `available`.
- The `unavailable` filter also lists `balance-limit` codes.
- Code shape is 1–32 ASCII letters, digits or `-`. Search is 1–32 characters (letters, digits, space, `-`) and must already be trimmed.
- `DetailPage` and `RedeemedNotice` match codes ordinally:
  - An unknown or wrongly cased code gives `null`.
  - An unknown filter is treated as `all`.
  - A code that is not redeemed on this account gets a neutral notice (`data-redeemed="false"`) that shows no amount.

## Unity API candidate endpoints (`Features/UnityApi/CandidateEndpoints/`)

The installed client contains these path literals. Methods, query keys and response shapes are provisional and come from IL2CPP field names, not captured responses. Every route sits under the `unityApi` prefix, allows anonymous callers and writes through `CandidateResponse`. That helper always sets `Content-Type: application/json` and `Cache-Control: no-store`, on errors too.

| Route | Verb | Response | Status |
|---|---|---|---|
| `/unityApi/events/spaceSearch.php` | GET | `{planets, events, rewards}` | 200, or 400 `Invalid Space Search request.` |
| `/unityApi/events/ssRewards.php` | GET | `{name, special, time, item, title, package}` | 200, 400 or 404 (see below) |
| `/unityApi/guide/ggRewards.php` | GET | `{data}` = `LocalGalaxyGates.Catalog` IDs in catalog order | 200 |
| `/unityApi/payment.php` | GET | `{url: "/indexInternal.es?action=internalPayment"}` | 200 |
| `/unityApi/upload.php` | POST | `{result: false, type: "", error, status: 501}` | 501, so no upload is ever reported as successful |

The three stateless candidates have these exact local projections. `ggRewards.php` writes only a `data` array of distinct gate IDs in `LocalGalaxyGates.Catalog` order; it does not expose energy, owned parts, activation state or account progress. `payment.php` writes only `url`, exactly `/indexInternal.es?action=internalPayment`, a relative local route. `upload.php` writes HTTP 501 and exactly four JSON fields: `result: false`, `type: ""`, `error: "Upload unavailable in emulator"`, and `status: 501`. Its response is independent of request body and does not save an upload. These are provisional emulator responses, not verified official payloads.

Query rules for both Space Search routes (`SpaceSearchCandidateAction.ValidQuery`):
- Any key is accepted, because the official query is unknown. An empty query is valid.
- Each key must be 1–64 characters and each value 0–64 characters (both bounds inclusive). Neither may contain control characters.
- Each key may appear only once. A repeated key, even `planet`, makes the whole query invalid.
- Query keys are compared after URL decoding with ASP.NET Core's case-insensitive query collection. Thus `x` plus `X`, or `planet` plus `%70lanet`, count as repeated keys and return the generic invalid-query 400. Neither form may select a planet or reach the session store.
- Lengths are measured on decoded .NET strings in UTF-16 code units, not raw percent-encoded bytes or Unicode scalar values. An unknown key or value containing 32 four-byte UTF-8 emoji decodes to 64 UTF-16 code units and is accepted; 33 decodes to 66 and is rejected with the route's generic 400. This applies to both Space Search routes.
- The reward route looks up `planet` through that same decoded, case-insensitive query collection. A single `PLANET=mars` or `%70lanet=mars` selects Mars even after Mars has been searched; a duplicate in any casing remains invalid at the preceding query-validation step.
- These bounds apply to unknown keys too. A 64-character key and 64-character value are accepted; an empty unknown value is accepted, but an empty key, a 65-character key, or a control character in either is rejected. URL-decoded repeated keys are rejected before any account progress is projected.

Error precedence on `ssRewards.php`:
1. An invalid query (see above) → 400 `Invalid Space Search reward request.`
2. A `planet` value that is not 1–32 lowercase ASCII letters (for example empty, `Mars` or `mars1`) → 400 `The planet value is invalid.`
3. A well-shaped planet that is not in `LocalSpaceSearch.Catalog` → 404 `That planet is not part of Space Search.`

For example, repeated `planet` values including an unknown planet return the generic invalid-query 400 rather than a planet-shape or unknown-planet error. The error reveals neither the candidate planet nor searched account progress. Both routes use the same bounds, JSON error shape and `no-store` policy for malformed unknown keys.

The error body is exactly `{error, status}`, with fixed text. It never echoes the rejected value.

Session and progress:
- A caller counts as signed in only if the `dosid` cookie resolves to a session whose company exactly matches a code in `CompanyCatalog` (ordinal, lowercase). A null or empty company, a wrong-case code such as `MMO`, or an unknown code does not admit the account to the equipment read. Both routes then return HTTP 200 with `no-store` and the anonymous catalog/default Mars reward even if that account has searched Mars. The session lookup itself may occur; neither progress nor inventory from the rejected session is projected.
- Session lookup accepts a nonblank `dosid` of at most 128 decoded characters, sharing `LocalLogoutAction.MaxDosidLength` with other client-facing session routes. A 128-character token may reach the session store; a 129-character token does not. The latter follows the anonymous success projection on both Space Search routes, without a session-store or equipment read, and does not echo the cookie.
- Anonymous Space Search lists every catalog planet in catalog order, with `rewards[i]` as the reward item of `planets[i]` and `events = ["local_space_search"]`.
- Signed-in Space Search lists only planets the account has not searched. Once none are left, `events` is empty.
- With no `planet`, the reward is the account's next unsearched planet. If every planet has been searched, or the caller is anonymous with nothing searched, it is the first catalog planet. `title` uses the owned inventory name first, then the catalog reward name. `special` is `false`, `time` is `0` and `package` is `local_space_search`.
- An explicit, well-shaped catalog `planet` selects that planet even when the account has already searched it; the lookup does not grant or consume a reward. With no `planet`, the next-unsearched choice remains account-specific. Progress and inventory are read by the resolved session's account ID, so two signed-in accounts may receive different lists and default rewards from the same endpoint. If multiple inventory rows have the reward item ID, the first row supplies `title`; if none has it, the catalog reward name supplies `title`.
- Both handlers reject an invalid query before resolving a session or reading equipment. The rewards handler checks query validity, then planet shape, then catalog membership, before reading account state. This order preserves the fixed error response regardless of a cookie's validity.
- Rejected requests, including a 65-character unknown value, a wrongly cased planet value, and a well-shaped unknown planet, do not call the session store even when a `dosid` cookie is present. The 400 or 404 response remains a two-field `{error, status}` JSON object with the matching numeric HTTP status, `application/json` content type and `Cache-Control: no-store`.
- These routes are read-only and never change account state.

## Expedition box resource (`Features/Resources/Expedition/`)

`GET /xml/events/expedition/expedition-boxes.xml` (tag `DNS:gbl3.darkorbit.com`) is a provisional, anonymous and stateless catalogue. The installed client contains the path literal and the `RateId`/`Rewards` member names, but no official response has been captured, so the XML shape is local.

- **Route.** `ExpeditionBoxesEndpoint` is the only endpoint in the folder. It is an `EndpointWithoutRequest` with a parameterless constructor, `RoutePrefixOverride("xml")` and `AllowAnonymous()`. It reads no session, cookie, query, clock, economy or outbound HTTP state.
- **Catalogue.** `ExpeditionBoxesResponse.Catalog` is read-only and holds exactly one entry: `RateId = 1`, `ItemId = LocalEvents.ExpeditionRewardItemId` (`shield_sg3n_b02`), `Quantity = 1`. This is the single shield that the Events menu claim (menu 20) grants, and the item is a known `LocalShop` item. Rate IDs are unique and positive, and quantities are positive.
- **Body.** The body is built once and is exactly `<ExpeditionBoxes><ExpeditionBox><RateId>1</RateId><Rewards><Reward ItemId="shield_sg3n_b02" Quantity="1" /></Rewards></ExpeditionBox></ExpeditionBoxes>`. It has no XML declaration, no namespace, no whitespace between elements and no UTF-8 BOM. There is one `ExpeditionBox` per catalogue entry, in catalogue order, and each box has exactly one `Reward`.
- **Response.** Status 200, `Content-Type: application/xml` with no charset, and `Cache-Control: no-store`. No cookie is set. Query strings, cookies and repeated calls never change the body. A cancelled token surfaces as `OperationCanceledException` and no body bytes are written.

## Command center resource manifest (`Features/Resources/CommandCenter/`)

`GET /resources/resource_command_center.xml` is the captured `gbl3.darkorbit.com` route. `GET /xml/resource_command_center.xml` is an alias drawn from an IL2CPP path literal; its runtime use is unverified. Both routes are anonymous `EndpointWithoutRequest` classes tagged `DNS:gbl3.darkorbit.com` and call `CapturedCommandCenterResources.WriteResponseAsync` with the same embedded snapshot. The response reads no account, cookie, query, clock or outbound service.

- **Body and contract.** `CapturedCommandCenterResources.Xml` is the embedded `CommandCenterResources.xml` snapshot from `command-center-resources.20261021.xml`. The writer sends its exact UTF-8 bytes, starting with `<filecollection>` and without a byte-order mark. The root has no attributes. It contains three `location` elements and 609 `file` elements. Location IDs are `do_img_new_bg`, `do_img_new_overlay` and `resources_command-center`; each has a relative directory path. Every file refers to one of them and has `debugView` (boolean), `hash` (32 lowercase hexadecimal characters), `id`, `location`, `name`, `type` and `version` (integer) attributes. All `debugView` values are false and all versions are 1. There are 577 JPG, 30 PNG and two XML entries. Each resulting absolute resource path is unique, contains no parent traversal and fits the artwork lookup's 256-character limit. `CommandCenterResourcesXmlResponse`, `CommandCenterLocation` and `CommandCenterFile` deserialize the same XML attribute names and types. The XML asset entries are categories and positions; the manifest itself does not supply their bytes.
- **Response.** Status 200 and `Content-Type: application/xml` with no charset. `Content-Security-Policy` is `frame-ancestors https://*.darkorbit.com/;` and `Strict-Transport-Security` is `max-age=31536000; includeSubDomains`. The writer sets no cookie. It ignores path, query, cookies and `If-None-Match`; even `*` receives the full 200 body. A cancelled write propagates `OperationCanceledException` without body bytes.

## Command center category resource (`Features/Resources/CommandCenter/`)

`GET /resources/command-center/categories.xml` (tag `CDN:darkorbit-22.bpsecure.com`, the path named by the captured resource manifest) and `GET /xml/Categories.xml` (tag `DNS:gbl3.darkorbit.com`, an IL2CPP literal whose runtime request is unverified) are anonymous aliases. Both are sealed `EndpointWithoutRequest` classes with a parameterless constructor, and both delegate to `CapturedCommandCenterCategories.WriteResponseAsync(HttpContext, ct)`.

- **Body.** `CapturedCommandCenterCategories.Xml` is the embedded resource `CommandCenterCategories.xml` (`command-center-categories.20261022.xml`), returned verbatim: it starts with `<root>`, has no XML declaration, no namespace and no UTF-8 BOM. It lists exactly five `category` elements in captured order: `sales` (prio 5, `isSpecial="true"`), `events` (1), `news` (3), `deals` (2), `upselling` (5). Each `textKey` is `bn_category_` plus the ID. Only `sales` carries `isSpecial`, and no element has other attributes. Every category used by `CapturedNewsCatalog.MenuEntries` is one of these IDs. Menu 27 (`internalCommandCenter`) reads the same captured data.
- **Validators.** The ETag comes from the manifest's `categories` entry (`location="resources_command-center"`, type `xml`): `"cda88151fed5847635e9b06abc645100-1"`, which is `"<hash>-<version>"`. Every response sets `X-Content-Type-Options: nosniff`, that `ETag` and `Cache-Control: public, max-age=3600`. The manifest XML entry is never served as generated artwork.
- **Revalidation.** `If-None-Match` is split on commas and each value is trimmed. The exact strong tag, the `W/` weak form (uppercase only) and `*` give 304 with no body, no `Content-Type` and no `Content-Length`. Other values give a full 200, including the bare hash, an unquoted tag, a differently cased hash, a lowercase `w/` prefix, an empty header or another manifest asset's tag.
- **Full response.** Status 200, `Content-Type: application/xml` with no charset, and `Content-Length` equal to the UTF-8 byte count. No cookie is set. The request path (either alias), query and cookies never change the body or validators. A cancelled token surfaces as `OperationCanceledException` and no body bytes are written.

## Branding

These pages and the CMS keep generic placeholder branding (`Space Emulator CMS`, `Space Emulator Portal | Local Login (Placeholder)`), as `BrandingPlaceholderComplianceTests` enforces.

## Verification and limits

`ClientTesting/Menus16To29AndEconomySpecificationTests.cs` covers page-query allow-lists, canonical action casing, duplicate, control-character and overlong values, package and voucher shapes, notice exclusivity, battle pass status transitions, the dispatch countdown, logbook validation, account revision bounds, command center sort/urgency, both logout steps, the closed economy result sets, payment catalog replacement, status precedence and filter partitioning, the voucher grant-cap and `int.MaxValue` credit-ceiling boundaries, and claim/redeem notice gating, plus the Unity API candidate query bounds, `ssRewards.php` error precedence, anonymous fallback and `no-store` JSON headers. The `LocalPayment*`, `LocalVoucher*`, `LocalEvents*`, `LocalLogbook*`, `LocalAccountSettings*`, `LocalCommandCenter*`, `EconomyCatalogTests`, `EconomyAdminEndpointTests`, `SpaceSearchCandidateActionTests` and `CandidateEndpointContractTests` suites cover deeper cases. `ClientTesting/ExpeditionBoxesResourceSpecificationTests.cs` and `ExpeditionBoxesResponseTests` cover the expedition box body, catalogue and headers, and `ApiServiceVerticalSliceTests.ExpeditionBoxes_IsSingleFileStatelessAnonymousXmlResourceReusingEventsReward` pins the slice shape. `ClientTesting/CommandCenterResourceManifestSpecificationTests.cs` covers the captured manifest, DTO deserialization, aliases, headers and cancellation, with `ApiServiceVerticalSliceTests.CommandCenterManifestAliases_ShareCapturedWriterWithoutSessionState` pinning its route boundary. `ClientTesting/CommandCenterCategoriesResourceSpecificationTests.cs` covers the category body, manifest ETag, `If-None-Match` matching, headers and cancellation, and `ApiServiceVerticalSliceTests.CommandCenterCategoryAliases_ShareOneManifestValidatedWriterWithoutSessionState` pins both aliases to the shared writer. Whether the client actually requests the `/xml/` command center aliases remains unverified. The official expedition-box XML remains unverified. `ApiServiceVerticalSliceTests.UnityCandidateEndpoints_AreAnonymousUnityApiRoutesWithFixedVerbs` pins the candidate routes, verbs, prefix and shared response writer. Transactional idempotency against PostgreSQL is covered only by the Docker-backed integration suites. Menus 18, 21, 22, 23 and 25 have no implementation. Official payloads and a signed-in Unity WebView journey remain unverified under the no-CA rule.
