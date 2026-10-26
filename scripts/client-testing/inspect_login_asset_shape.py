"""Report only allowlisted asset field shapes from a decoded login response."""

import argparse
import json
import re
from pathlib import Path
from urllib.parse import parse_qsl, urlsplit

from mitmproxy import http, io


ROOT = Path(__file__).resolve().parents[2] / "_reversing" / "Requests" / "sessions"
FIELDS = ("basePath", "cdn", "host", "gameclientPath", "itemXmlHash",
          "resourcesXmlHash", "gameclientHash", "loadingscreenHash")
PUBLIC_FLAGS = ("useHash", "autoStartEnabled", "display2d", "fullscreen")
CLIENT_MODES = ("browser", "platform", "requestScheme", "nextAction",
                "gameclientAllowedInitDelay", "showAdvertisingHint", "useDeviceFonts",
                "allowChat")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("session")
    parser.add_argument("--capture-flow", action="store_true",
                        help="Inspect the first successful captured login response instead of decoded/0082.json")
    parser.add_argument("--public-flags", action="store_true",
                        help="Include non-secret client startup flags")
    parser.add_argument("--client-modes", action="store_true",
                        help="Include only allowlisted non-secret client mode values")
    parser.add_argument("--hangar-shape", action="store_true",
                        help="Include hangar field types and safe value shapes, never identifiers")
    args = parser.parse_args()
    session = Path(args.session).resolve()
    if session.parent != ROOT.resolve():
        raise SystemExit("Session must be under the project capture directory")
    if args.capture_flow:
        wrapper = None
        with (session / "flow").open("rb") as stream:
            for flow in io.FlowReader(stream).stream():
                if not isinstance(flow, http.HTTPFlow) or not flow.response:
                    continue
                if (urlsplit(flow.request.pretty_url).path.lower() == "/unityapi/login.php"
                        and flow.response.status_code == 200):
                    wrapper = json.loads(flow.response.get_text(strict=False))
                    break
        if wrapper is None:
            raise SystemExit("No successful login response in flow")
    else:
        wrapper = json.loads((session / "decoded" / "0082.json").read_text(encoding="utf-8"))
    data = json.loads(wrapper["data"])
    if args.public_flags:
        for field in PUBLIC_FLAGS:
            value = data.get(field)
            if type(value) is int:
                print({"field": field, "value": value})
    if args.client_modes:
        for field in CLIENT_MODES:
            value = data.get(field)
            if type(value) in (bool, int):
                print({"field": field, "value": value})
            elif isinstance(value, str):
                # Do not emit unexpected values from a captured account response.
                safe = {"unity", "UNITY_CLIENT", "https", "http", "internalWebGL",
                        "internalStart", "0", "1", "2", "true", "false", ""}
                if field == "browser" and re.fullmatch(r"[a-zA-Z]{1,16}", value):
                    safe.add(value)
                if field == "gameclientAllowedInitDelay" and re.fullmatch(r"[0-9]{1,3}", value):
                    safe.add(value)
                print({"field": field, "value": value if value in safe else "<other>",
                       "length": len(value)})
    if args.hangar_shape:
        hangar_data = data.get("hangars", {}).get("data", {})
        hangars = hangar_data.get("hangars", [])
        relative_tooltips = sum(
            isinstance(h.get("tooltipIcon"), str)
            and h["tooltipIcon"].startswith("/do_img/")
            and not urlsplit(h["tooltipIcon"]).scheme
            and not urlsplit(h["tooltipIcon"]).netloc
            for h in hangars)
        print({"hangars": len(hangars), "active": sum(h.get("hangarActive") is True for h in hangars),
               "relativeTooltipPaths": relative_tooltips})
        faction = hangar_data.get("faction")
        print({"faction": faction if faction in {"MMO", "EIC", "VRU", "mmo", "eic", "vru"} else "<other>",
               "factionType": type(faction).__name__,
               "factionLength": len(faction) if isinstance(faction, str) else None})
        top_map = data.get("mapID")
        print({"topMapId": top_map if isinstance(top_map, str) and re.fullmatch(r"[1-9][0-9]?(?:-[1-9][0-9]?)?", top_map) else "<other>"})
        for hangar in (h for h in hangars if h.get("hangarActive") is True):
            map_id = hangar.get("mapId")
            ship_id = hangar.get("shipId")
            tooltip = urlsplit(hangar.get("tooltipIcon", ""))
            tooltip_query = parse_qsl(tooltip.query, keep_blank_values=True)
            safe_tooltip_host = (tooltip.hostname if tooltip.hostname and
                                 (tooltip.hostname.endswith(".bpsecure.com") or
                                  tooltip.hostname.endswith(".darkorbit.com"))
                                 else "other")
            print({"activeMapId": map_id if isinstance(map_id, str) and re.fullmatch(r"[0-9]{1,2}", map_id) else "<other>",
                   "activeShipIdShape": "ship_underscore" if isinstance(ship_id, str) and re.fullmatch(r"ship_[a-z0-9_]+", ship_id) else
                                        "ship_hyphen" if isinstance(ship_id, str) and re.fullmatch(r"ship_[a-z0-9-]+", ship_id) else
                                        "other",
                   "activeShipIdLength": len(ship_id) if isinstance(ship_id, str) else None,
                   "activeModelId": hangar.get("modelId") if type(hangar.get("modelId")) is int else "<other>",
                   "tooltipHost": safe_tooltip_host,
                   "tooltipScheme": tooltip.scheme if tooltip.scheme in {"http", "https"} else "other",
                   "tooltipHostLength": len(tooltip.hostname) if tooltip.hostname else 0,
                   "tooltipPath": tooltip.path if tooltip.path.startswith("/do_img/") and not re.search(r"[0-9]{6,}", tooltip.path) else "<other>"})
            print({"activeTooltipQuery": [
                {"key": key if key in {"__cv"} else "<other>", "valueLength": len(value),
                 "hexValue": bool(re.fullmatch(r"[a-fA-F0-9]+", value))}
                for key, value in tooltip_query]})
        for field in ("slotNumber", "shipId", "modelId", "hangarId", "shipName",
                      "mapId", "shipDestroyed", "hangarActive", "tooltipIcon"):
            values = [h.get(field) for h in hangars]
            kinds = sorted({type(v).__name__ for v in values})
            lengths = sorted({len(v) for v in values if isinstance(v, str)})
            numeric = sum(isinstance(v, str) and v.isdecimal() for v in values)
            print({"hangarField": field, "types": kinds, "stringLengths": lengths,
                   "numericStrings": numeric, "uniqueValues": len({str(v) for v in values})})
    for field in FIELDS:
        value = data.get(field)
        if not isinstance(value, str):
            print({"field": field, "type": type(value).__name__})
            continue
        parsed = urlsplit(value)
        print({"field": field, "length": len(value), "isUrl": bool(parsed.scheme),
               "trailingSlash": value.endswith("/"),
               "host": parsed.hostname if parsed.hostname in {
                   "gbl3.darkorbit.com", "gbl6.darkorbit.com", "www.darkorbit.com",
                   "darkorbit-22.bpsecure.com", "alicdn-oss-prod.darkorbit.com",
                   "localhost"} else "other"})


if __name__ == "__main__":
    main()
