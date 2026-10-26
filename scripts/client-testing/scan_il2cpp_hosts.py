"""Read-only, bounded host/string inventory for the installed IL2CPP client.

Prints only network-related strings and their file offsets. No capture files or
credentials are read. Metadata indices are offsets in the metadata string heap
or indices in the string-literal table, as indicated by `source`.
"""

import hashlib
import json
import re
import struct
import sys
from pathlib import Path


ROOT = Path(r"D:\Games\DarkOrbit Unity")
FILES = (
    ROOT / "GameAssembly.dll",
    ROOT / "DarkOrbit_Data/il2cpp_data/Metadata/global-metadata.dat",
)
HOST = re.compile(
    r"(?i)(?:[a-z0-9-]+\.)+(?:darkorbit\.com|bpsecure\.com|unity3d\.com|unity\.com)"
    r"|(?:[a-z0-9-]+\.)?(?:darkorbit\.com|bpsecure\.com)"
    r"|\b(?:gbl\d+|sas|cdn|localhost)(?::\d{2,5})?\b"
)
URL = re.compile(r"(?i)(?:https?|wss?)://[^\s\x00\"'<>]{3,250}")
PRINTABLE = re.compile(rb"[\x20-\x7e]{4,}")
UTF16 = re.compile(rb"(?:[\x20-\x7e]\x00){4,}")


def emit(path, source, offset, value, index=None):
    if not (HOST.search(value) or URL.search(value) and "unity" in value.lower()):
        return
    # Avoid printing arbitrary nearby binary text or a long fused string.
    value = value[:250]
    print(json.dumps({"file": path.name, "source": source,
                      "offset": f"0x{offset:x}", "index": index,
                      "value": value}, ensure_ascii=True))


def scan_spans(path, data):
    for pattern, encoding, source in ((PRINTABLE, "ascii", "ascii"),
                                      (UTF16, "utf-16le", "utf16le")):
        for match in pattern.finditer(data):
            value = match.group().decode(encoding, errors="ignore")
            if HOST.search(value) or URL.search(value) and "unity" in value.lower():
                emit(path, source, match.start(), value)


def scan_metadata(path, data):
    if len(data) < 32 or struct.unpack_from("<I", data)[0] != 0xFAB11BAF:
        print(json.dumps({"metadata_error": "IL2CPP header magic missing"}))
        return
    version, lit_off, lit_size, data_off, data_size, str_off, str_size = struct.unpack_from("<7I", data, 4)
    print(json.dumps({"metadata_version": version, "string_heap": [str_off, str_size],
                      "literal_table": [lit_off, lit_size], "literal_data": [data_off, data_size]}))
    if str_off + str_size <= len(data):
        heap = data[str_off:str_off + str_size]
        for match in re.finditer(rb"[^\x00]+", heap):
            value = match.group().decode("utf-8", errors="replace")
            if "--symbols" in sys.argv:
                if re.search(r"(?i)^(?:.*(?:Cdn|Url|Host|ServerAddress|ServerPort|WebSocket|BaseUri|UriBuilder|ApiEndpoint).*)$", value) and len(value) < 90:
                    print(json.dumps({"source": "metadata-string", "index": match.start(),
                                      "offset": f"0x{str_off + match.start():x}",
                                      "value": value}, ensure_ascii=True))
            else:
                emit(path, "metadata-string", str_off + match.start(), value, match.start())
    if lit_off + lit_size <= len(data) and data_off + data_size <= len(data):
        for i in range(lit_size // 8):
            length, rel_off = struct.unpack_from("<II", data, lit_off + i * 8)
            if length > 1024 or rel_off + length > data_size:
                continue
            value = data[data_off + rel_off:data_off + rel_off + length].decode("utf-8", errors="replace")
            if "--details" in sys.argv:
                if (24820 <= i <= 24865 or re.search(r"(?i)(?:cdn|gbl\d*|bpsecure|darkorbit|sas|unityapi|flashapi|https?://|wss?://|:\d{4,5})", value)):
                    # Literal data may include unrelated text; keep this bounded.
                    print(json.dumps({"source": "metadata-literal", "index": i,
                                      "offset": f"0x{data_off + rel_off:x}",
                                      "value": value[:250]}, ensure_ascii=True))
            else:
                emit(path, "metadata-literal", data_off + rel_off, value, i)


for path in FILES:
    data = path.read_bytes()
    print(json.dumps({"file": path.name, "size": len(data),
                      "sha256": hashlib.sha256(data).hexdigest()}))
    if path.name == "GameAssembly.dll":
        scan_spans(path, data)
    else:
        scan_metadata(path, data)
