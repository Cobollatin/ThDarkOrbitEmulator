"""Print only game HTTP endpoint metadata from a captured mitmproxy flow.

Request and response bodies, cookies, and query values are never read or printed.
Usage: .venv\\Scripts\\python.exe scripts\\client-testing\\index_game_http.py SESSION_DIR
"""

import json
import sys
from pathlib import Path
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

from mitmproxy import http, io


ROOT = Path(__file__).resolve().parents[2]
SESSIONS = ROOT / "_reversing" / "Requests" / "sessions"


def redacted_url(value):
    parts = urlsplit(value)
    query = urlencode([(key, "<redacted>") for key, _ in parse_qsl(parts.query, keep_blank_values=True)])
    return urlunsplit((parts.scheme, parts.netloc, parts.path, query, ""))


def main():
    session = Path(sys.argv[1]).resolve()
    if session.parent != SESSIONS.resolve():
        raise SystemExit("Session must be directly under the repository sessions directory")
    rows = []
    with (session / "flow").open("rb") as source:
        for number, flow in enumerate(io.FlowReader(source).stream(), 1):
            if not isinstance(flow, http.HTTPFlow) or flow.response is None:
                continue
            host = flow.request.pretty_host.lower()
            if not (host == "localhost" or host.endswith(".darkorbit.com") or host.endswith(".bpsecure.com")):
                continue
            location = flow.response.headers.get("location")
            rows.append({
                "flow": number,
                "method": flow.request.method,
                "url": redacted_url(flow.request.pretty_url),
                "status": flow.response.status_code,
                "contentType": flow.response.headers.get("content-type", "").split(";", 1)[0],
                "bodyBytes": len(flow.response.raw_content or b""),
                "location": redacted_url(location) if location else None,
            })
    print(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
