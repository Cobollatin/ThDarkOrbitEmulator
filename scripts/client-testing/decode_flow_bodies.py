"""Export redacted, decoded JSON/XML responses from a mitmproxy session flow.

Usage: .venv\\Scripts\\python.exe scripts\\client-testing\\decode_flow_bodies.py [SESSION_DIR]
Without SESSION_DIR, the newest successful session with more than 50 entries is used.
Only game/API-host JSON/XML responses are exported; ads, analytics, binary,
HTML, scripts, and request bodies are not.
"""

import argparse
import json
import re
from pathlib import Path
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit
from xml.etree import ElementTree as ET

from mitmproxy import http, io


REPO = Path(__file__).resolve().parents[2]
SESSIONS = REPO / "_reversing" / "Requests" / "sessions"
SENSITIVE = re.compile(r"password|passwd|secret|token|cookie|session|auth|csrf|xsrf|credential|api.?key|user.?id|(^|_)pid$|^opid$", re.I)
URL = re.compile(r"https?://[^\s\"'<>]+", re.I)


def latest_session():
    for session in sorted(SESSIONS.iterdir(), reverse=True):
        summary = session / "summary.json"
        if session.is_dir() and summary.is_file():
            data = json.loads(summary.read_text(encoding="utf-8-sig"))
            if data.get("ok") is True and data.get("entries", 0) > 50:
                return session
    raise SystemExit("No successful session with more than 50 entries")


def safe_url(value):
    parts = urlsplit(value)
    # Query values can contain credentials even when the key is unfamiliar.
    query = urlencode([(key, "<redacted>") for key, _ in parse_qsl(parts.query, keep_blank_values=True)])
    return urlunsplit((parts.scheme, parts.netloc, parts.path, query, ""))


def safe_text(value):
    return URL.sub(lambda match: safe_url(match.group()), value)


def scrub_json(value):
    if isinstance(value, dict):
        return {key: "<redacted>" if SENSITIVE.search(key) else scrub_json(item)
                for key, item in value.items()}
    if isinstance(value, list):
        return [scrub_json(item) for item in value]
    if isinstance(value, str):
        # login.php puts another JSON document in its data string.
        if value.lstrip().startswith(("{", "[")):
            try:
                return json.dumps(scrub_json(json.loads(value)), ensure_ascii=False, separators=(",", ":"))
            except json.JSONDecodeError:
                pass
        return safe_text(value)
    return value


def scrub_xml(root):
    for element in root.iter():
        for key, value in list(element.attrib.items()):
            element.set(key, "<redacted>" if SENSITIVE.search(key) else safe_text(value))
        if element.text:
            element.text = "<redacted>" if SENSITIVE.search(element.tag) else safe_text(element.text)


def sample_value(value):
    """Keep wire keys and JSON types while replacing every captured value."""
    if isinstance(value, dict):
        return {key: sample_value(item) for key, item in value.items()}
    if isinstance(value, list):
        return [sample_value(value[0])] if value else []
    if isinstance(value, bool):
        return True
    if isinstance(value, int):
        return 1
    if value is None:
        return None
    if isinstance(value, str):
        if value.lstrip().startswith(("{", "[")):
            try:
                return json.dumps(sample_value(json.loads(value)), separators=(",", ":"))
            except json.JSONDecodeError:
                pass
        return "example"
    return value


def export(session, fixture_dir=None):
    session = session.resolve()
    if session.parent != SESSIONS.resolve() or not (session / "flow").is_file():
        raise SystemExit("SESSION_DIR must be a session directory containing flow")
    output = session / "decoded"
    output.mkdir(exist_ok=True)
    entries = []
    fixtures = {"/unityapi/login.php": "unity-login.json",
                "/unityapi/links.php": "unity-links.json",
                "/unityapi/news.php": "unity-news.json"}
    if fixture_dir is not None:
        fixture_dir = fixture_dir.resolve()
        tests_root = (REPO / "ThDarkOrbitEmulator.Tests").resolve()
        if tests_root not in fixture_dir.parents:
            raise SystemExit("Fixture directory must be under ThDarkOrbitEmulator.Tests")
        fixture_dir.mkdir(parents=True, exist_ok=True)
    with (session / "flow").open("rb") as source:
        for number, flow in enumerate(io.FlowReader(source).stream(), start=1):
            if not isinstance(flow, http.HTTPFlow) or flow.response is None:
                continue
            host = flow.request.pretty_host.lower()
            if not (host.endswith(".darkorbit.com") or host == "darkorbit.com" or
                    host.endswith(".bpsecure.com") or host == "bpsecure.com"):
                continue
            response = flow.response
            media_type = response.headers.get("content-type", "").split(";", 1)[0].strip().lower()
            path = urlsplit(flow.request.pretty_url).path.lower()
            if path == "/indexinternal.es":
                # Menu pages are HTML. Record only redacted endpoint metadata, never their bodies.
                entries.append({"flow": number, "method": flow.request.method,
                                "url": safe_url(flow.request.pretty_url), "status": response.status_code,
                                "contentType": media_type,
                                "contentEncoding": response.headers.get("content-encoding", "identity")})
                continue
            is_json = "json" in media_type or path.endswith(".php") and path in (
                "/unityapi/login.php", "/unityapi/links.php", "/unityapi/news.php")
            is_xml = "xml" in media_type or path.endswith(".xml")
            if not (is_json or is_xml):
                continue
            try:
                body = response.get_content().decode("utf-8-sig")
                if is_json:
                    parsed = json.loads(body)
                    decoded = json.dumps(scrub_json(parsed), ensure_ascii=False, indent=2) + "\n"
                    suffix = ".json"
                else:
                    root = ET.fromstring(body)
                    scrub_xml(root)
                    decoded = ET.tostring(root, encoding="unicode") + "\n"
                    suffix = ".xml"
            except (ValueError, UnicodeError, ET.ParseError) as error:
                # Do not dump raw bodies or exception text to logs.
                entries.append({"flow": number, "method": flow.request.method,
                                "url": safe_url(flow.request.pretty_url), "status": response.status_code,
                                "contentType": media_type,
                                "contentEncoding": response.headers.get("content-encoding", "identity"),
                                "error": type(error).__name__})
                continue
            filename = f"{number:04d}{suffix}"
            (output / filename).write_text(decoded, encoding="utf-8")
            if fixture_dir is not None and path in fixtures:
                (fixture_dir / fixtures[path]).write_text(
                    json.dumps(sample_value(parsed), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            entries.append({"flow": number, "file": filename, "method": flow.request.method,
                            "url": safe_url(flow.request.pretty_url), "status": response.status_code,
                            "contentType": media_type,
                            "contentEncoding": response.headers.get("content-encoding", "identity")})
    (output / "index.json").write_text(json.dumps(entries, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"session": session.name, "decoded": len([e for e in entries if "file" in e]),
                      "errors": len([e for e in entries if "error" in e])}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session", nargs="?", type=Path)
    parser.add_argument("--fixture-dir", type=Path, help="Write synthetic Unity API fixtures under the tests project")
    args = parser.parse_args()
    export(args.session or latest_session(), args.fixture_dir)
