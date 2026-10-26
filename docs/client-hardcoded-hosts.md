# Hardcoded Unity client hosts (2026-10-22)

Scope: read-only inspection of the installed `GameAssembly.dll` and IL2CPP v31
`global-metadata.dat`. Neither installed file was changed. The reproducible scan
is `\.venv\Scripts\python.exe scripts\client-testing\scan_il2cpp_hosts.py`
(`--details` for relevant literals and `--symbols` for candidate metadata names).
The files had SHA-256 `868729de4486860841b40b59d04fd469e510a2854be354b59d945a7eaa472107`
and `a9df074bb27ea58dbd19f322a6c6b45438fe198c74cc2f78a1d33f29604e3055`,
respectively. Offsets below are **file offsets** in `global-metadata.dat`;
literal indices refer to its string-literal table, while name indices are byte
offsets in its metadata string heap. A string's presence does not establish a
request during execution.

| Literal / fragment | Literal index | File offset | Likely role and redirect decision |
| --- | ---: | ---: | --- |
| `https://www.darkorbit.com/` | 24853 | `0x109975` | Public login and web entry; redirect. `index.es?action=externalDownload` is separately embedded at 24854 / `0x10998f`. |
| `https://darkorbit-22.bpsecure.com` | 24843 | `0x1097dc` | Asset origin; leave upstream until local assets exist. Clan logo path has a second literal at 24844 / `0x1097fd`. |
| `https://alicdn-oss-dev.darkorbit.com/` | 24838 | `0x1096f3` | Development asset CDN; conditional redirect. |
| `https://alicdn-oss-prod.darkorbit.com/` | 24839 | `0x109718` | Production asset CDN; redirect when assets are served locally. |
| `https://steam.partner.darkorbit.com/?token=` | 24851 | `0x109926` | Steam authentication entry; conditional Steam redirect. This is only a static literal, not a captured flow. |
| `.darkorbit.com`, `.darkorbit.com/` | 1650, 1651 | `0x50f10`, `0x50f1e` | Domain suffix fragments; consistent with constructing a game host from a server name. Exact caller and assembly order are unverified. |
| `https://acp-` | 24837 | `0x1096e7` | Prefix of a possible dynamic account/payment host. No complete host can be mapped from this fragment alone. |
| `https://`, `http://` | 24836, 24797 | `0x1096df`, `0x1090d1` | Generic scheme fragments; no destination on their own. |
| `https://localhost/` | 24847 | `0x10989b` | Existing local URL literal, purpose not established. |

The metadata also contains relative API paths `flashAPI/brNews.php` (literal
24029, `0x105d12`), `unityApi/events/spaceSearch.php` (34625, `0x138dfe`),
`unityApi/events/ssRewards.php` (34626, `0x138e1d`), and
`unityApi/guide/ggRewards.php` (34627, `0x138e3a`). They require a base URL
at runtime; the events and guide paths have not been captured as requests.
An expanded IL2CPP v31 table parse found leading-slash forms of those three
paths at literal indices 1772–1774 (`0x51516`, `0x51536`, `0x51554`) and
additional `/unityApi/payment.php` (1775, `0x51571`) and
`/unityApi/upload.php` (1776, `0x51586`). All five remain unverified as runtime
requests in the examined login capture. The existing complete
`https://www.darkorbit.com/index.es?action=externalDownload` literal (24854,
`0x10998f`) is also an unverified client URL. The
[network type inventory](../_reversing/Analysis/il2cpp-network-types.md)
records candidate builders and DTO classes; it does not establish exact
literal callers or HTTP methods.
The string heap has `get_Cdn` / `set_Cdn` (indices 197597 / 197605,
offsets `0x16f401` / `0x16f409`), `<Cdn>k__BackingField` (198984,
`0x16f96c`), and `Cdn` (199866, `0x16fcde`). These show a CDN property in
compiled metadata, but the type and methods that consume each URL literal
cannot be linked from the string heap alone. It also has
`CreateServerAddress` (49622, `0x14b1fa`) and `ServerAddress` (46772,
`0x14a6d8`). The table parse places `CreateServerAddress` in
`Infinicast.Client.Utils.NetFactory`, a game networking utility; it does not
establish an HTTP URL builder. `DarkOrbit.Settings` instead contains
`StaticHost`, `DynamicHost`, `NewsHost`, `LoginHost`, `UnityRequest`, and suffix
fields, with URL getters; `DarkOrbit.Init.UpdateHost(prefixDomain)` is another
builder candidate. Neither method body is available in this analysis.

## Hosts from captured redirects

The captured command center manifest includes a versioned
`resources/command-center/categories.xml` asset on
`darkorbit-22.bpsecure.com`. Its public XML was fetched on 2026-10-22 and is
served locally. Redirect mode now intercepts that exact CDN path only; the
rest of the CDN remains upstream. The installed client's use of the path is
still unverified.

The [endpoint map](client-endpoint-map.md) records live login traffic to
`sas.bpsecure.com`, `gbl3.darkorbit.com`, and `gbl6.darkorbit.com`.
Those complete hosts were **not** found as standalone literals in the two
inspected IL2CPP files. The `gbl` number may come from web redirects or login
data; this remains an inference. The template includes both observed `gbl`
hosts and SAS so a local redirect configuration covers the known login flow.
The Redirect addons now match exact numbered `gblN.darkorbit.com` hostnames as
well as the captured IP; only `gbl3` and `gbl6` have runtime evidence. The
binary replacement template remains an untested option.
The login payload's CDN URL is `https://darkorbit-22.bpsecure.com/` in the Live
sample and now in the emulator response. The Redirect addon leaves that asset
host upstream. See the endpoint map's `20261022-053738-cdn-verify` run for the
runtime boundary. The twelve asset hashes and four startup flags were aligned
from the captured login response afterward; client behavior with those changes
has not been recaptured.
Other `gblN` values are possible and cannot be enumerated from this build's
static literals. The Redirect host pattern covers them if encountered, while
the `https://acp-` fragment still needs runtime evidence
before an exact host mapping can be added.

The scan also found a Vuplex diagnostic message naming `localhost:8080`
(literal 499, `0x4a5e9`). It describes webview debugging, not a game server.
No hardcoded game TCP port was established. Generic Unity documentation,
sample WebSocket URLs, social links, surveys, and analytics URLs were excluded
from the redirect template because there is no evidence they are emulator
endpoints.

## Expanded URL scan (2026-10-22)

A second bounded scan of the same metadata build selected game URL and XML
path literals with the inventory script's `--literal-match` option. It found
additional **relative** resource candidates:
`resources/command-center/categories.xml`, `xml/Categories.xml`,
`xml/CommandCenter.xml`, `xml/maps.php`, `xml/maps.xml`,
`xml/resource_command_center.xml`, and battle pass and expedition XML prefixes.
The [endpoint map](client-endpoint-map.md) lists their literal indices and
unverified status. The scan also found `//` variants of two event API paths
and the expedition boxes XML path; these are literal spellings, not observed
requests. `DarkOrbit.Settings` has corresponding command center and map URL
getter names, but no native bodies or xrefs were available to resolve their
base host.

The scan found no new complete DarkOrbit or bpsecure host. Therefore
`scripts/client-testing/url-replacements.template.json` needs no new entry.

## Analysis limits and template use

REA `open_binary` identified the x86 PE and confirmed its SHA-256 above.
REA native `search_strings` / xrefs returned `provider_unavailable`: Ghidra is
not configured on this machine, and no other native provider was selected.
A direct ASCII/UTF-16LE scan of `GameAssembly.dll` found no complete game URL;
the one case-insensitive `sas` text hit was unrelated printable data.
Consequently, exact URL construction and caller attribution remain open. The
metadata table parse establishes declaring types, field names, method names,
and parameter names, but not native control flow. REA's generic artifact
inspector was previously found unsuitable for IL2CPP metadata (see
[REA notes](rea-for-client-reversing.md)). No additional complete game host
was found, so the replacement template needs no new host entry.

[`url-replacements.template.json`](../scripts/client-testing/url-replacements.template.json)
lists observed game hosts and the conditional CDN/Steam hosts. It is an
inventory template, **not** a tested binary patch. Its `localhost` targets are
shorter than the source URLs, and `Replace-HardcodedUrls.ps1` skips shorter
binary replacements unless `-AllowShorterBinaryReplacement` is supplied.
That script must not be run against the installed client under the current
read-only task. Runtime generated hosts and redirects may require interception
rather than a literal byte replacement.
