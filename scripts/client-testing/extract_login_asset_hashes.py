"""Generate a source file containing only asset hashes from a decoded login response.

The source must be the known decoded response inside a repository capture session.
No account, session, token, cookie, or request fields are copied or printed.
"""

import argparse
import json
import re
from pathlib import Path
from urllib.parse import parse_qs, urlsplit


ROOT = Path(__file__).resolve().parents[2]
SESSIONS = ROOT / "_reversing" / "Requests" / "sessions"
DESTINATION = ROOT / "ThDarkOrbitEmulator.ApiService" / "Features" / "UnityApi" / "Login" / "Contracts" / "CapturedAssetHashes.cs"
FIELDS = {
    "itemXmlHash": "ItemXmlHash",
    "resourcesXmlHash": "ResourcesXmlHash",
    "resources3dXmlHash": "Resources3dXmlHash",
    "resources3dparticlesXmlHash": "Resources3dParticlesXmlHash",
    "resourceachievementsXmlHash": "ResourceAchievementsXmlHash",
    "resourceEventsXmlHash": "ResourceEventsXmlHash",
    "resourceJumpGateXmlHash": "ResourceJumpGateXmlHash",
    "profileXmlHash": "ProfileXmlHash",
    "loadingscreenHash": "LoadingscreenHash",
    "gameclientHash": "GameclientHash",
    "loadingscreenAssetsXmlHash": "LoadingscreenAssetsXmlHash",
    "crossdomainHash": "CrossdomainHash",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session", type=Path)
    args = parser.parse_args()
    session = args.session.resolve()
    if session.parent != SESSIONS.resolve():
        raise SystemExit("Session must be directly under the repository sessions directory")
    wrapper = json.loads((session / "decoded" / "0082.json").read_text(encoding="utf-8"))
    data = json.loads(wrapper["data"])
    hashes = {wire: data.get(wire) for wire in FIELDS}
    if any(not isinstance(value, str) or re.fullmatch(r"[a-fA-F0-9]{32}", value) is None
           for value in hashes.values()):
        raise SystemExit("One or more allowlisted asset hashes are missing or not 32 hex characters")
    active = [hangar for hangar in data.get("hangars", {}).get("data", {}).get("hangars", [])
              if hangar.get("hangarActive") is True]
    if len(active) != 1:
        raise SystemExit("Expected exactly one active hangar")
    icon = urlsplit(active[0].get("tooltipIcon", ""))
    if icon.scheme or icon.netloc or icon.path != "/do_img/global/items/ship/pusat-plus_100x100.png":
        raise SystemExit("Active hangar icon path differs from the allowlisted asset")
    icon_query = parse_qs(icon.query, strict_parsing=True)
    if set(icon_query) != {"__cv"} or len(icon_query["__cv"]) != 1 or not re.fullmatch(r"[a-fA-F0-9]{32}", icon_query["__cv"][0]):
        raise SystemExit("Active hangar icon cache version is not a 32-character hex value")

    lines = [
        "namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts;",
        "",
        f"// Asset content hashes from {session.name}/decoded/0082.json (login response only).",
        "// Refresh when the upstream game assets change; no account fields are included.",
        "public static class CapturedAssetHashes",
        "{",
    ]
    lines.extend(f'    public const string {property_name} = "{hashes[wire]}";'
                 for wire, property_name in FIELDS.items())
    lines.append(f'    public const string ActiveHangarIconVersion = "{icon_query["__cv"][0]}";')
    lines.append("}")
    DESTINATION.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"wrote {DESTINATION.relative_to(ROOT)} with {len(hashes) + 1} validated asset hashes")


if __name__ == "__main__":
    main()
