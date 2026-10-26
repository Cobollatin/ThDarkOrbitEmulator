"""Print only allowlisted local route names and status codes from a session flow.

Never reads or prints request/response bodies, headers, cookies, or query values.
"""

import argparse
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from mitmproxy import http, io

ROOT = Path(__file__).resolve().parents[2]
SESSIONS = ROOT / "_reversing" / "Requests" / "sessions"
ACTIONS = frozenset({"internalStart", "internalWebGL", "internalCompanyChoose",
                     "internalCompanyConfirm", "internalDock", "internalGalaxyGates",
                     "internalPayment"})
PATHS = frozenset({"/", "/Sas/Authentication/Bigpoint", "/ProjectApi/Authentication",
                   "/indexInternal.es", "/unityApi/login.php", "/unityApi/links.php"})


def main(session: Path) -> None:
    session = session.resolve()
    if session.parent != SESSIONS.resolve():
        raise SystemExit("session must be a direct child of the repo sessions directory")
    with (session / "flow").open("rb") as source:
        for flow in io.FlowReader(source).stream():
            if not isinstance(flow, http.HTTPFlow) or flow.response is None:
                continue
            url = urlsplit(flow.request.pretty_url)
            if url.hostname not in {"localhost", "127.0.0.1"} or url.path not in PATHS:
                continue
            values = parse_qs(url.query).get("action", [])
            action = values[0] if values and values[0] in ACTIONS else ""
            label = f" {action}" if action else ""
            print(f"{flow.request.method} {url.path}{label} {flow.response.status_code}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session", type=Path)
    main(parser.parse_args().session)
