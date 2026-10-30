# SPEC-02: Unity client URI patching and local connectivity

## Scope and evidence

`scripts/client-testing/Patch-ClientUris.ps1` invokes `patch_il2cpp_uris.py` to inspect, patch, or reset three client files: `DarkOrbit_Data/il2cpp_data/Metadata/global-metadata.dat`, `DarkOrbit_Data/StreamingAssets/config.txt`, and `update.ini`. The normal target is the Aspire gateway at `https://localhost:60952`. The fixed original metadata SHA-256 is `a9df074bb27ea58dbd19f322a6c6b45438fe198c74cc2f78a1d33f29604e3055`; the examined original file was 18,179,656 bytes. Literal offsets and source values below come from the read-only IL2CPP inventory in `docs/client-hardcoded-hosts.md`. A compiled literal is a routing candidate, not proof that a request occurred.

## IL2CPP v31 binary contract

All fields below are little-endian `uint32` at file offsets in `global-metadata.dat`. The metadata header continues after these fields; they are the six fields used by this patcher.

| Header byte offset | Field | Required meaning |
| ---: | --- | --- |
| `0x00` | magic | `0xFAB11BAF` |
| `0x04` | version | `31` |
| `0x08` | `stringLiteralOffset` | Absolute start of the descriptor table |
| `0x0c` | `stringLiteralSize` | Descriptor table size in bytes; divisible by 8 and covering index 24854 |
| `0x10` | `stringLiteralDataOffset` | Absolute start of UTF-8 literal data |
| `0x14` | `stringLiteralDataSize` | Literal data section size in bytes |

The examined original metadata has `stringLiteralOffset = 256 (0x100)`, `stringLiteralSize = 288288 (0x46620)`, `stringLiteralDataOffset = 288544 (0x46720)`, and `stringLiteralDataSize = 1018116 (0xf8904)`. Both declared sections must fit within the file. A descriptor for index `i` starts at `stringLiteralOffset + 8*i`; its first `uint32` is UTF-8 byte length and its second `uint32` is `dataIndex`, the offset relative to `stringLiteralDataOffset`. The literal's absolute data offset is `stringLiteralDataOffset + dataIndex`. The parser rejects invalid magic, non-v31 metadata, incomplete descriptor tables, and out-of-file sections before Apply creates backups. The patcher does not relocate descriptors, string data, or any other section.

| Index | Name / category | Original UTF-8 value | Capacity | Descriptor length / dataIndex file offsets | Literal data file offset | Default Apply | `-PatchCdn` |
| ---: | --- | --- | ---: | --- | ---: | --- | --- |
| 24838 | AliCdnDevUrl / CDN | `https://alicdn-oss-dev.darkorbit.com/` | 37 | `0x30930` / `0x30934` | `0x1096f3` | Retain | Gateway URL with `/` |
| 24839 | AliCdnProdUrl / CDN | `https://alicdn-oss-prod.darkorbit.com/` | 38 | `0x30938` / `0x3093c` | `0x109718` | Retain | Gateway URL with `/` |
| 24843 | BpSecureCdnOrigin / CDN | `https://darkorbit-22.bpsecure.com` | 33 | `0x30958` / `0x3095c` | `0x1097dc` | Retain | Gateway URL without `/` |
| 24844 | BpSecureClanLogoUrl / CDN | `https://darkorbit-22.bpsecure.com/do_img/global/clanLogo/` | 57 | `0x30960` / `0x30964` | `0x1097fd` | Retain | Gateway URL + `/do_img/global/clanLogo/` |
| 24847 | LocalhostHttpsUrl / loopback | `https://localhost/` | 18 | `0x30978` / `0x3097c` | `0x10989b` | Retain if replacement exceeds 18 bytes | Same rule |
| 24851 | SteamPartnerTokenUrl / CDN | `https://steam.partner.darkorbit.com/?token=` | 43 | `0x30998` / `0x3099c` | `0x109926` | Retain | Gateway URL + `/?token=` |
| 24853 | PortalBaseUrl / core | `https://www.darkorbit.com/` | 26 | `0x309a8` / `0x309ac` | `0x109975` | Gateway URL with `/` | Same |
| 24854 | ExternalDownloadUrl / core | `https://www.darkorbit.com/index.es?action=externalDownload` | 58 | `0x309b0` / `0x309b4` | `0x10998f` | Gateway URL + `/index.es?action=externalDownload` | Same |

`Apply` uses the original descriptor length as each slot's capacity. A replacement's UTF-8 bytes must fit; an overlong value raises an error.

The indices above belong to the baseline build. Other client builds put the same literals at other indices; the 2026-03-10 build (`global-metadata.dat` SHA-256 `a0726c248d765b7782ca5045e17521bfa1477428a06ed84a26707cb9569ba1a2`, 18,166,792 bytes) has every index 33 lower, for example `24820` for PortalBaseUrl. The patcher therefore finds each target by its exact original value in the clean metadata (the `.orig.bak` backup when present). Each value must occur exactly once; a missing or duplicated target is rejected before any backup is made. Both SHA-256 values are accepted as originals.

For changed literals it writes replacement bytes, fills the unused original slot with zero bytes, changes only the descriptor length, and retains `dataIndex`. It must preserve total metadata file length. The default gateway URL is 24 bytes including the final `/`, so the 18-byte loopback literal remains untouched. CDN and Steam literals are changed only with `-PatchCdn`; serving their assets and Steam flow locally is a separate connectivity requirement and is not established by the patch itself.

## Backups, companion files, and actions

`Status` is read-only and reports the header, three file hashes/states and backup presence, eight literal values/statuses, `config.txt` `localHost`, and `update.ini` domains. It does not validate the fixed original hash against the current file; `Apply` does. `Apply` requires all three files, creates adjacent `.orig.bak` files if absent, verifies the original metadata backup against the fixed SHA-256, and always starts metadata edits from that backup. Existing backups are reused, so a second Apply and a switch of `-PatchCdn` do not compound prior edits. The target must be an HTTP or HTTPS URL with a host. The target's scheme and authority supply the base URL; its hostname alone supplies `localHost`.

Before rewriting the companion files, Apply restores each from its `.orig.bak`. In `config.txt` it replaces every line beginning exactly `localHost:` with `localHost:<hostname>` and appends one if absent. Other lines remain. In `update.ini` it replaces lines beginning `HashDomain` (allowing an initial BOM) and `DownloadDomain` with `HashDomain = <base>/` and `DownloadDomain = <base>/`; it does not add missing keys. Both files are read as UTF-8 with an optional BOM and written as UTF-8 without a BOM. Python's universal-newline text I/O is used, so on Windows every line of both companion files, replaced or not, is written with CRLF endings; LF-only inputs are therefore not byte-preserved by Apply (Reset still restores them exactly from backup). `Reset` copies each available backup over its corresponding file, then verifies the restored metadata SHA-256. Successful Reset restores all three files byte for byte when all three backups exist. Backups remain after Reset.

### Unity console, `boot.config` and console launcher

`Apply` also requires `DarkOrbit.exe` and `DarkOrbit_Data/boot.config`, backs both up as `.orig.bak`, and always rebuilds them from those backups:

- `DarkOrbit.exe`: the PE optional-header subsystem must be Windows GUI (`2`) in the backup; Apply writes console (`3`). Any other original subsystem is rejected.
- `boot.config`: every line whose trimmed content is exactly `nolog=` is removed; all other bytes and line endings are preserved. `Status` reports `boot_nolog` (`true` while the line is still present).
- `Start-DarkOrbitConsole.cmd` is written to the client root. It runs `scripts/client-testing/Start-ClientConsole.ps1 -ClientRoot "%~dp0"`. A pre-existing launcher with different content is backed up first.

`Reset` restores `DarkOrbit.exe` and `boot.config` from their backups. It restores a backed-up launcher, or deletes the launcher only when its content still matches the generated one. Apply and Reset refuse to run while `DarkOrbit.exe` is running, because Windows keeps the metadata and executable open; `Status` is always allowed.

`Start-ClientConsole.ps1` launches `DarkOrbit.exe -logFile <client>\unity-console.log`, truncates that log, and tails it to the console every 100 ms until `DarkOrbit.exe` exits. It tails the file instead of piping stdout, because Unity child processes inherit stdout handles and would keep a pipeline open after the player exits. On `Ctrl+C` or any error the `finally` block kills the player process tree, flushes the remaining log lines, and disposes the reader, so the console returns to the prompt.

### Dual HTTPS listeners

The native Unity login and cockpit traffic reaches the emulator on two HTTPS origins, both served by the `gateway-players` YARP routes to `api-game`:

| Origin | Why it exists |
| --- | --- |
| `https://localhost:60952` | Patched portal/core URLs (`PortalBaseUrl`, `ExternalDownloadUrl`) and the Vuplex WebView login. `ClientGateway__HttpsOrigin` advertises this origin. |
| `https://localhost:443` | IL2CPP literal 24847 is `https://localhost/` (18-byte slot) and cannot hold `:60952`, so the AppHost adds endpoint `unity-https-default` on port 443 for native requests to that literal. |

`api-game` has an explicit HTTP endpoint so the gateway's 443 route has a reachable destination. Before that change, port 443 answered `502`. The native flow is `OpenWebLogin` (WebView, `/indexInternal.es?action=internalStart` → `internalWebGL` dosid), then `GetLinkRequest` (`/unityApi/links.php`), `GetPost` (`/unityApi/login.php`), `GetNewsRequest` (`/unityApi/news.php`) and `GetHangerPost`. The request URL behind `GetHangerPost` has not been captured yet. A Unity 401 is logged as method, path, safe `action` and endpoint name, without the cookie, so a Redirect capture can identify it. When a native request arrives with a forwarded host that has no port, `ClientFacingGameHost.Resolve` advertises the configured WebView origin (`:60952`) in `host`, `boardLink`, `helpLink` and `sharedImages`. Cockpit WebViews therefore open on the same listener that holds the login cookie. Running the gateway directly (outside Aspire) uses the same two HTTPS URLs from `ThDarkOrbitEmulator.Gateway/Properties/launchSettings.json`. Binding port 443 on Windows can require an elevated shell, or the port must not already be in use.

For repo-local synthetic tests only, the wrapper forwards `-FixtureOriginalSha256` to the Python helper's `--fixture-original-sha256`. The helper accepts a 64-digit hexadecimal digest only when the resolved client root is strictly inside this repository. Aspire does not pass this parameter; the fixed production hash remains mandatory for normal client roots. This permits exercising the full entry point with synthetic v31 metadata without modifying the installed client.

## Validation order and edge-case contract

Apply validates in this order and stops at the first failure. Every rejection below exits nonzero, and none creates an `.orig.bak` file.

| Step | Condition | Diagnostic substring |
| ---: | --- | --- |
| 1 | `-FixtureOriginalSha256` given and client root is the repository root or outside it (checked before any file access, for every action) | `strictly inside the repository` |
| 2 | `-FixtureOriginalSha256` is not exactly 64 hexadecimal characters | `64-character hexadecimal` |
| 3 | Target scheme is not `http`/`https`, or the authority is empty (`ftp://…`, `localhost:60952`, `https://`, `not a url`) | `Invalid --target-base-url` |
| 4 | Any of the three client files is missing | `Required client file not found` |
| 5 | Metadata header: smaller than 32 bytes, bad magic, version other than 31, a section outside the file, or `stringLiteralSize` not divisible by 8 or not covering index 24854 | `Invalid IL2CPP metadata magic`, `expected 31`, `exceeds file bounds`, `incomplete` |
| 6 | No backup yet and the current metadata SHA-256 differs from the expected original | `does not match expected original SHA-256` |
| 7 | A replacement exceeds its slot capacity | `in-place slot capacity` |

Further normative edge cases:

- Only the scheme and authority of the target are used; path, query and fragment are discarded. `https://127.0.0.1:60952/ignored/path?q=1` patches `24853` to `https://127.0.0.1:60952/` and sets `localHost:127.0.0.1`.
- The loopback literal `24847` is part of the default Apply set and is replaced by `<base>/` only when that string is at most 18 UTF-8 bytes (`https://abcdefghi/` is replaced, `https://abcdefghij/` is not). `applied_changes` counts only literals whose value changes.
- In `config.txt`, only lines beginning exactly `localHost:` (case-sensitive, no leading whitespace) are replaced; all such lines are replaced; if none exists one line is appended.
- Status never creates backups, never writes files and does not compare against the expected original hash, so it succeeds with any well-formed digest.
- Reset without any backups succeeds, reports an empty `restored_files` list and leaves the files unchanged, provided the current metadata matches the expected hash.
- Switching from `-PatchCdn` to a default Apply restores CDN literals to their original values because Apply always starts from the metadata backup.

## Traffic-log analysis contract

`ClientTestingTrafficAnalyzer.Analyze(trafficLogPath, options, catalog)` in `ThDarkOrbitEmulator.AppHost/ClientTesting/ClientTestingTrafficAnalyzer.cs` summarizes the mitmproxy JSON Lines log for the Aspire traffic report. It is read-only and has this contract:

| Rule | Behavior |
| --- | --- |
| Missing log | Throws `FileNotFoundException`; the message contains the path and the hint `client-test-start-mitm`. |
| Blank lines | Empty or whitespace-only lines are skipped and are not counted. An empty file yields zero counts and empty lists. |
| Malformed lines | A line that is not JSON propagates `JsonException` (no partial result). A JSON array or `null` root, a non-string non-null `host`/`method`/`path`, or a non-boolean (including `null`) `is_local_gateway`/`redirected` value propagates `InvalidOperationException`. |
| Field defaults | Missing or `null` `host` becomes `""`, `method` becomes `GET` and `path` becomes `/`. Missing flags are `false`; explicit `false` flags have the same effect. |
| Locality | A request is local when `is_local_gateway` is `true`, or `host` equals the hostname of `LocalGatewayHttpsUrl` or `localhost` (ordinal, case-insensitive; port, path and other loopback literals such as `127.0.0.1` are not considered). Every other request is a production hit. `LocalGatewayHits + ProductionHits = TotalRequests`. |
| Redirects | `RedirectedHits` counts `redirected: true` independently of locality. |
| Host summary | `ByHost` contains every host, local or production, merged case-insensitively under the first-seen spelling, ordered by descending count with first-seen order breaking equal counts; counts sum to `TotalRequests`. `IsLocalGateway` uses the hostname rule only, so a host counted as local solely through `is_local_gateway` is reported with `IsLocalGateway = false`. |
| Production paths | `UnmatchedProductionPaths` groups production hits by `host`, `method` and `path` (case-insensitive, first-seen spelling, query string included, pipe characters inside a path preserved), ordered by descending count with first-seen order breaking equal counts, and capped at 40 entries. Despite its name it lists all production paths, including ones present in the endpoint catalog; the report heading is accordingly "Production traffic". Catalog lookup keys are computed but do not filter the list, and internal `__unmapped__` keys never appear in the result. |

AppHost client-testing types are local tooling: every type in the `ThDarkOrbitEmulator.AppHost.ClientTesting` namespace is `internal` (visible to tests through `InternalsVisibleTo`) and lives under `ThDarkOrbitEmulator.AppHost/ClientTesting/`. `ProjectReferenceArchitectureTests` enforces this.

## Client-testing path resolution contract

`ClientTestingPaths(repoRoot, options)` in `ThDarkOrbitEmulator.AppHost/ClientTesting/ClientTestingPaths.cs` computes every path the Aspire client-testing commands use. `AppHost.cs` passes the repository root (`Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."))`), not the AppHost directory. The constructor only computes strings: it creates no directory and checks no file, and `Options` is the same instance that was passed in.

| Property | Resolution |
| --- | --- |
| `RepoRoot` | `repoRoot` verbatim. |
| `OutputDirectory` | `Path.Combine(repoRoot, options.OutputDirectory)`. A rooted value is used verbatim; `..` segments are kept, not normalized. Default `client-testing`. |
| `TrafficLogPath`, `PidLogPath`, `MitmPidFile` | `client-traffic.jsonl`, `darkorbit_pids.log` and `mitmproxy.pid` under `OutputDirectory`. |
| `MappingPath` | A rooted `options.MappingPath` is used verbatim; otherwise it is combined with `repoRoot`. Default `client-testing/url-replacements.json`. |
| `ScriptsDirectory`, `ClientTestingScriptsDirectory` | `<repoRoot>/scripts` and `<repoRoot>/scripts/client-testing`; no option changes them. |
| `FindUrlsScript`, `ReplaceUrlsScript` | `Find-HardcodedUrls.ps1` and `Replace-HardcodedUrls.ps1` under `ScriptsDirectory`. |
| `PatchClientUrisScript`, `MappingTemplatePath` | `Patch-ClientUris.ps1` and `url-replacements.template.json` under `ClientTestingScriptsDirectory`; both exist in the repository. |
| `LaunchRedirectScript` | `<repoRoot>/_reversing/Mitmproxy/launch_redirect.py`. |
| `FeaturesDirectory` | `Path.GetFullPath(Path.Combine(repoRoot, options.ApiServiceFeaturesPath))`; this is the only normalized path. The default `ThDarkOrbitEmulator.ApiService/Features` resolves to the ApiService feature tree that `ClientEndpointCatalog.Discover` scans. |

Earlier builds set the default `ApiServiceFeaturesPath` to `../ThDarkOrbitEmulator.ApiService/Features`. Combined with the repository root, that path pointed above the repository, so endpoint-catalog commands threw `DirectoryNotFoundException`. The default is now repo-relative. `ProjectReferenceArchitectureTests` checks that `AppHost.cs` passes the repository root and that neither the option defaults nor AppHost `appsettings*.json` set `ApiServiceFeaturesPath`, `OutputDirectory` or `MappingPath` to a value containing `..`. The executable contract is `ThDarkOrbitEmulator.Tests/ClientTesting/ClientTestingPathsSpecificationTests.cs`.

## Endpoint catalog discovery contract

`ClientEndpointCatalog.Discover(featuresDirectory)` reads `*.cs` files recursively beneath the supplied feature directory. A missing directory throws `DirectoryNotFoundException` containing its path. Each `// DNS: <host> | <METHOD> <path>` comment contributes an endpoint for each comma-separated `DNS:` host token; method matching ignores case and is emitted in uppercase, while the path is trimmed and preserved. If a file has no explicit DNS endpoint comment, its first `Get/Post/Put/Delete/Patch/Head/Options("/path")` route and its distinct `DNS:<host>` tags contribute inferred entries. An explicit DNS endpoint comment suppresses all tag-based inference from that file. Host tokens accept ASCII letters, digits, dots and hyphens, excluding surrounding source-code quotes and punctuation.

Entries are deduplicated by host, method and path without regard to case, retaining the first source spelling, then sorted by host, path and method without regard to case. Each entry uses its path as `ApiRoute`, is `ClientReplaceable`, and is classified as `game` for `/unityApi`, `/flashAPI`, `/resources/`, `/index`, `/ProjectApi` or `/Sas/`; as `api` for `/api/`; and otherwise as `mock`. Four manual `gbl3.darkorbit.com` entries remain present even when the feature directory has no source files. Discovery is source metadata only; it does not establish that a native client called an endpoint. `ClientEndpointCatalogSpecificationTests` covers these rules, and `ProjectReferenceArchitectureTests` checks that real discovered hostnames contain no source-code punctuation.

Further boundaries: a file with route calls but no `DNS:` token contributes no inferred entry. Explicit DNS comments produce entries even without route calls, and each comment retains its own method, trimmed path, and query text. A matching source entry wins over a later manual entry because deduplication is case-insensitive and manual entries are appended last; the other manual entries remain. Different methods for the same host and path remain distinct. Classification uses literal case-insensitive prefixes: `/unityApiX` is currently `game`, while `/resources` and `/api` without a trailing slash are `mock`. The catalog scans only `*.cs` files under the supplied features directory and reads their text; discovery does not write artifacts or start a process. The architecture suite guards this read-only boundary.

## Aspire resource command contract

`ThDarkOrbitEmulator.AppHost/ClientTesting/ClientTestingExtensions.cs` attaches these commands to the `gateway-players` resource. Each passes `ClientInstallPath` and `LocalGatewayHttpsUrl` from `ClientTestingOptions` to the PowerShell wrapper, enforces `ClientTestingLocalGuard`, and publishes a Markdown result on exit code 0 or a failure result on nonzero exit. No command passes `-PatchCdn`.

| Command ID | Wrapper action | Dashboard behavior |
| --- | --- | --- |
| `client-test-patch-uris-status` | `Status` | Read-only inspection; no confirmation |
| `client-test-patch-uris-apply` | `Apply` | Patches core URLs and companion files; confirmation required |
| `client-test-patch-uris-reset` | `Reset` | Restores backups; confirmation required |

`ProjectReferenceArchitectureTests` guards the dashboard registration: Apply and Reset retain `confirm: true`, while the read-only Status command does not request confirmation.

The gateway target is a local route; URI replacement alone does not prove authentication, TLS trust, asset coverage, or binary game connectivity. This specification does not claim that any candidate literal was executed by the client. The executable contract is `ThDarkOrbitEmulator.Tests/ClientTesting/ClientUriPatcherSpecificationTests.cs`, using synthetic files under the repository; the traffic-log analysis contract is `ThDarkOrbitEmulator.Tests/ClientTesting/ClientTrafficAnalysisSpecificationTests.cs`, using synthetic JSONL logs in the test work directory; the Aspire command catalog is checked by `AspireOperationsCatalogTests`.
