"""Print a narrow, account-free shape of saved game page responses.

This reads mitmproxy flows, never request bodies or headers, and emits only
allowlisted static markers from the HTML response. It does not write captures.
"""

import argparse
import json
import hashlib
import re
from pathlib import Path
from mitmproxy import http, io


MARKERS = (
    "companySelection", "confirm_mmo", "confirm_eic", "confirm_vru",
    "unityApi/login.php", "unityApi/news.php", "internalWebGL",
    "UnityInstance", "unityInstance", "SendMessage", "Vuplex",
    "unity-webview", "gameContainer", "gameCanvas", "createUnityInstance",
)


def inspect(path: Path) -> None:
    webgl_values = []
    login_values = []
    with path.open("rb") as source:
        for number, flow in enumerate(io.FlowReader(source).stream(), 1):
            if not isinstance(flow, http.HTTPFlow) or flow.response is None:
                continue
            if flow.request.path.split("?", 1)[0] == "/unityApi/login.php" and flow.response.status_code == 200:
                try:
                    login = json.loads(json.loads(flow.response.get_text(strict=False))["data"])
                    login_values.append({key: login.get(key) for key in ("sessionId", "userId", "pid", "opid")})
                except (ValueError, TypeError, KeyError):
                    pass
                continue
            if flow.request.path.split("?", 1)[0] != "/indexInternal.es":
                continue
            action = flow.request.query.get("action", "")
            if action not in ("internalWebGL", "internalCompanyChoose"):
                continue
            body = flow.response.get_text(strict=False)
            found = [marker for marker in MARKERS if marker in body]
            tags = re.findall(r"</?([A-Za-z][A-Za-z0-9]*)\b", body)
            safe_tags = [tag.lower() if tag.lower() in
                         {"html", "head", "body", "script", "main", "div", "iframe", "form"}
                         else "other" for tag in tags[:20]]
            try:
                parsed = json.loads(body)
                body_kind = "json-object" if isinstance(parsed, dict) else "json-other"
            except (ValueError, TypeError):
                body_kind = "html" if tags else "plain"
            charset = ("hex" if re.fullmatch(r"[0-9a-fA-F]{32}", body) else
                       "base64-like" if re.fullmatch(r"[A-Za-z0-9_+/-]{28,}={0,2}", body) else
                       "natural-text" if re.fullmatch(r"[A-Za-z ,.:'!?-]+", body) else
                       "mixed")
            if action == "internalWebGL" and charset == "hex":
                dosid = flow.request.cookies.get("dosid")
                webgl_values.append(body)
                print(f"webgl_equals_request_dosid={body == dosid} "
                      f"webgl_equals_md5_dosid={bool(dosid) and body == hashlib.md5(dosid.encode()).hexdigest()}")
            print(f"flow={number} action={action} status={flow.response.status_code} "
                  f"body_bytes={len(body.encode('utf-8'))} markers={','.join(found)} "
                  f"tags={','.join(safe_tags)} kind={body_kind} charset={charset}")
    for value in webgl_values:
        for login in login_values:
            print("webgl_equals_login_fields=" + ",".join(
                key for key, candidate in login.items() if candidate == value))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("flow", type=Path)
    inspect(parser.parse_args().flow)
