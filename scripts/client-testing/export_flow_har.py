"""Convert a mitmproxy flow dump into HAR 1.2.

Usage:
  python export_flow_har.py [SRC_FLOW] [DST_HAR] [--summary SUMMARY_JSON]

Without arguments it keeps the historical defaults (_reversing/Requests/flow -> flow.har).
"""
from mitmproxy import io as mio
from mitmproxy import http
from pathlib import Path
from collections import Counter
import argparse, json, base64, time

REPO = Path(__file__).resolve().parents[2]
ap = argparse.ArgumentParser()
ap.add_argument("src", nargs="?", default=str(REPO / "_reversing" / "Requests" / "flow"))
ap.add_argument("dst", nargs="?", default=None)
ap.add_argument("--summary", default=None, help="Write a JSON summary (entry count, hosts) here.")
args = ap.parse_args()
src = Path(args.src)
dst = Path(args.dst) if args.dst else src.with_suffix(".har")

entries = []
hosts = Counter()
flows_total = 0
if src.exists() and src.stat().st_size > 0:
    with src.open("rb") as f:
        reader = mio.FlowReader(f)
        try:
            for flow in reader.stream():
                flows_total += 1
                if not isinstance(flow, http.HTTPFlow):
                    continue
                hosts[flow.request.pretty_host] += 1
                if flow.response is None:
                    continue
                req = flow.request
                res = flow.response

                def headers_list(h):
                    return [{"name": k, "value": v} for k, v in h.items(multi=True)]

                def as_text(content):
                    if not content:
                        return "", ""
                    try:
                        return content.decode("utf-8"), ""
                    except Exception:
                        return base64.b64encode(content).decode("ascii"), "base64"

                req_body = req.raw_content or b""
                res_body = res.raw_content or b""
                req_text, req_enc = as_text(req_body)
                res_text, res_enc = as_text(res_body)
                entries.append({
                    "startedDateTime": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime(req.timestamp_start or time.time())),
                    "time": max(0, int(((res.timestamp_end or req.timestamp_end or req.timestamp_start or 0) - (req.timestamp_start or 0)) * 1000)),
                    "request": {
                        "method": req.method,
                        "url": req.pretty_url,
                        "httpVersion": f"HTTP/{req.http_version}",
                        "cookies": [],
                        "headers": headers_list(req.headers),
                        "queryString": [{"name": k, "value": v} for k, v in req.query.items(multi=True)] if req.query else [],
                        "headersSize": -1,
                        "bodySize": len(req_body),
                        "postData": {"mimeType": req.headers.get("content-type", ""), "text": req_text,
                                     **({"encoding": req_enc} if req_enc else {})},
                    },
                    "response": {
                        "status": res.status_code,
                        "statusText": res.reason or "",
                        "httpVersion": f"HTTP/{res.http_version}",
                        "cookies": [],
                        "headers": headers_list(res.headers),
                        "content": {"size": len(res_body), "mimeType": res.headers.get("content-type", ""), "text": res_text,
                                    **({"encoding": res_enc} if res_enc else {})},
                        "redirectURL": res.headers.get("location", ""),
                        "headersSize": -1,
                        "bodySize": len(res_body),
                    },
                    "cache": {},
                    "timings": {"send": 0, "wait": 0, "receive": 0},
                })
        except Exception as e:  # truncated dump after a crash: keep what we have
            print(f"warning: flow read stopped early: {e}")

har = {"log": {"version": "1.2", "creator": {"name": "ThDarkOrbitEmulator-flow-to-har", "version": "2"}, "entries": entries}}
dst.write_text(json.dumps(har), encoding="utf-8")
if args.summary:
    Path(args.summary).write_text(json.dumps({
        "har": str(dst), "flows": flows_total, "entries": len(entries),
        "hosts": dict(hosts.most_common()),
    }, indent=2), encoding="utf-8")
print(f"wrote {dst} entries={len(entries)}")
