r"""Reversible IL2CPP v31 string-literal URI patcher and inspector for the DarkOrbit Unity client.

Supports:
  --action status | apply | reset
  --client-root "D:\Games\DarkOrbit Unity"
  --target-base-url "https://localhost:60952"
  --patch-cdn (optional switch to also patch CDN / partner literals)
  --json (optional switch to emit structured JSON output)

Creates automatic .orig.bak backups before modifying any file and verifies SHA-256 hashes.
Because IL2CPP stores (uint32 length, int32 dataIndex) in the stringLiteral descriptor table
(8 bytes per literal), writing a shorter replacement string into the existing stringLiteralData
slot, zero-padding the remaining slot bytes, and updating the descriptor's length field keeps
the file size and every other offset in global-metadata.dat 100% unchanged.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import struct
import sys
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Iterable
from urllib.parse import urlparse


EXPECTED_ORIG_METADATA_SHA256 = "a9df074bb27ea58dbd19f322a6c6b45438fe198c74cc2f78a1d33f29604e3055"
# Unmodified global-metadata.dat builds known to be safe to patch. Literal indices differ per build,
# so targets are located by their exact original value (see resolve_literal_indices).
KNOWN_ORIG_METADATA_SHA256: dict[str, str] = {
    EXPECTED_ORIG_METADATA_SHA256: "baseline build (portal literal index 24853)",
    "a0726c248d765b7782ca5045e17521bfa1477428a06ed84a26707cb9569ba1a2": "1.1.92, 2026-03-10 build (portal literal index 24820)",
    "796e531afd56fbae3bc8ae39c6813d8ded8935346a1bea55b748801e9f6a605c": "1.1.93, 2026-03-31 build (portal literal index 24820)",
}
IL2CPP_MAGIC = 0xFAB11BAF


@dataclass(frozen=True)
class LiteralTarget:
    index: int
    name: str
    original_value: str
    original_capacity: int
    category: str  # "core" | "loopback" | "cdn"

    def build_replacement(self, base_url: str) -> str:
        base_no_slash = base_url.rstrip("/")
        base_with_slash = base_no_slash + "/"
        if self.name == "PortalBaseUrl":
            return base_with_slash
        if self.name == "ExternalDownloadUrl":
            return f"{base_no_slash}/index.es?action=externalDownload"
        if self.name == "LocalhostHttpsUrl":
            return base_with_slash if len(base_with_slash.encode("utf-8")) <= self.original_capacity else self.original_value
        if self.name in ("AliCdnDevUrl", "AliCdnProdUrl"):
            return base_with_slash
        if self.name == "BpSecureCdnOrigin":
            return base_no_slash
        if self.name == "BpSecureClanLogoUrl":
            return f"{base_no_slash}/do_img/global/clanLogo/"
        if self.name == "SteamPartnerTokenUrl":
            return f"{base_no_slash}/?token="
        return self.original_value


LITERAL_TARGETS: tuple[LiteralTarget, ...] = (
    LiteralTarget(24853, "PortalBaseUrl", "https://www.darkorbit.com/", 26, "core"),
    LiteralTarget(24854, "ExternalDownloadUrl", "https://www.darkorbit.com/index.es?action=externalDownload", 58, "core"),
    LiteralTarget(24847, "LocalhostHttpsUrl", "https://localhost/", 18, "loopback"),
    LiteralTarget(24838, "AliCdnDevUrl", "https://alicdn-oss-dev.darkorbit.com/", 37, "cdn"),
    LiteralTarget(24839, "AliCdnProdUrl", "https://alicdn-oss-prod.darkorbit.com/", 38, "cdn"),
    LiteralTarget(24843, "BpSecureCdnOrigin", "https://darkorbit-22.bpsecure.com", 33, "cdn"),
    LiteralTarget(24844, "BpSecureClanLogoUrl", "https://darkorbit-22.bpsecure.com/do_img/global/clanLogo/", 57, "cdn"),
    LiteralTarget(24851, "SteamPartnerTokenUrl", "https://steam.partner.darkorbit.com/?token=", 43, "cdn"),
)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def backup_path_for(path: Path) -> Path:
    return path.with_name(path.name + ".orig.bak")


def read_il2cpp_header(data: bytes | bytearray) -> tuple[int, int, int, int, int]:
    if len(data) < 32:
        raise ValueError("global-metadata.dat is smaller than 32 bytes.")
    magic, version, lit_off, lit_size, data_off, data_size = struct.unpack_from("<6I", data, 0)
    if magic != IL2CPP_MAGIC:
        raise ValueError(f"Invalid IL2CPP metadata magic: 0x{magic:08x}")
    if version != 31:
        raise ValueError(f"Unsupported IL2CPP metadata version: {version} (expected 31).")
    if lit_off + lit_size > len(data) or data_off + data_size > len(data):
        raise ValueError("IL2CPP string literal table or data section exceeds file bounds.")
    if lit_size % 8 or lit_size == 0:
        raise ValueError("IL2CPP string literal descriptor table is incomplete.")
    return version, lit_off, lit_size, data_off, data_size


def resolve_literal_targets(data: bytes | bytearray) -> tuple[LiteralTarget, ...]:
    """Locate each target by its exact original value; each must occur exactly once."""
    _, lit_off, lit_size, data_off, data_size = read_il2cpp_header(data)
    wanted = {t.original_value.encode("utf-8"): t for t in LITERAL_TARGETS}
    found: dict[str, list[int]] = {t.name: [] for t in LITERAL_TARGETS}
    lengths = {len(k) for k in wanted}
    for index in range(lit_size // 8):
        length, rel_off = struct.unpack_from("<II", data, lit_off + index * 8)
        if length not in lengths or rel_off + length > data_size:
            continue
        abs_off = data_off + rel_off
        target = wanted.get(bytes(data[abs_off : abs_off + length]))
        if target is not None:
            found[target.name].append(index)

    problems = [f"{name} ({'missing' if not idx else 'ambiguous: ' + str(idx)})" for name, idx in found.items() if len(idx) != 1]
    if problems:
        raise ValueError(
            "IL2CPP string literal descriptor table is incomplete or unrecognized; cannot locate "
            + ", ".join(problems)
        )
    return tuple(replace(t, index=found[t.name][0]) for t in LITERAL_TARGETS)


def normalize_expected(expected_sha256: str | Iterable[str]) -> frozenset[str]:
    if isinstance(expected_sha256, str):
        return frozenset({expected_sha256.lower()})
    return frozenset(h.lower() for h in expected_sha256)


def read_literal(data: bytes | bytearray, lit_off: int, data_off: int, index: int) -> tuple[int, int, int, str]:
    desc_off = lit_off + index * 8
    length, rel_off = struct.unpack_from("<II", data, desc_off)
    abs_off = data_off + rel_off
    raw = bytes(data[abs_off : abs_off + length])
    return length, rel_off, abs_off, raw.decode("utf-8", errors="replace")


def write_literal_in_place(
    data: bytearray,
    lit_off: int,
    data_off: int,
    target: LiteralTarget,
    new_value: str,
    orig_data: bytes | None = None,
) -> dict:
    desc_off = lit_off + target.index * 8
    cur_len, rel_off = struct.unpack_from("<II", data, desc_off)
    if orig_data is not None:
        orig_len, orig_rel_off = struct.unpack_from("<II", orig_data, desc_off)
        if orig_rel_off != rel_off:
            raise ValueError(
                f"Literal {target.index} dataIndex mismatch vs backup ({rel_off} != {orig_rel_off})."
            )
        slot_capacity = orig_len
    else:
        slot_capacity = target.original_capacity

    encoded = new_value.encode("utf-8")
    if len(encoded) > slot_capacity:
        raise ValueError(
            f"Replacement for literal {target.index} ({target.name}) is {len(encoded)} bytes, "
            f"which exceeds the {slot_capacity}-byte in-place slot capacity."
        )

    abs_off = data_off + rel_off
    previous_value = bytes(data[abs_off : abs_off + cur_len]).decode("utf-8", errors="replace")

    # Write new UTF-8 bytes and zero-pad remaining bytes in the original slot
    data[abs_off : abs_off + len(encoded)] = encoded
    if len(encoded) < slot_capacity:
        data[abs_off + len(encoded) : abs_off + slot_capacity] = b"\x00" * (slot_capacity - len(encoded))

    # Update uint32 length in descriptor table; keep dataIndex untouched
    struct.pack_into("<II", data, desc_off, len(encoded), rel_off)

    return {
        "index": target.index,
        "name": target.name,
        "offset": f"0x{abs_off:x}",
        "capacity": slot_capacity,
        "old_length": cur_len,
        "new_length": len(encoded),
        "old_value": previous_value,
        "new_value": new_value,
    }


def ensure_backup(path: Path, expected_sha256: str | Iterable[str] | None = None) -> tuple[Path, bool]:
    bak = backup_path_for(path)
    created = False
    if not bak.exists():
        if expected_sha256 is not None:
            allowed = normalize_expected(expected_sha256)
            actual = sha256_file(path)
            if actual.lower() not in allowed:
                raise RuntimeError(
                    f"Refusing to create initial backup for {path.name}: current SHA-256 ({actual}) "
                    f"does not match expected original SHA-256 ({', '.join(sorted(allowed))})."
                )
        shutil.copy2(path, bak)
        created = True
    return bak, created


def inspect_config_txt(path: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    if not path.exists():
        return result
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        if ":" in line:
            k, v = line.split(":", 1)
            result[k.strip()] = v.strip()
    return result


def patch_config_txt(path: Path, local_host_value: str) -> None:
    lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines(keepends=True)
    updated_lines: list[str] = []
    replaced = False
    for line in lines:
        stripped = line.rstrip("\r\n")
        ending = line[len(stripped) :] or "\n"
        if stripped.startswith("localHost:"):
            updated_lines.append(f"localHost:{local_host_value}{ending}")
            replaced = True
        else:
            updated_lines.append(line)
    if not replaced:
        updated_lines.append(f"localHost:{local_host_value}\n")
    path.write_text("".join(updated_lines), encoding="utf-8")


def inspect_update_ini(path: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    if not path.exists():
        return result
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            result[k.strip()] = v.strip()
    return result


def patch_update_ini(path: Path, base_url_with_slash: str) -> None:
    lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines(keepends=True)
    updated_lines: list[str] = []
    for line in lines:
        stripped = line.rstrip("\r\n")
        ending = line[len(stripped) :] or "\n"
        if stripped.lstrip("\ufeff").startswith("HashDomain"):
            updated_lines.append(f"HashDomain = {base_url_with_slash}{ending}")
        elif stripped.startswith("DownloadDomain"):
            updated_lines.append(f"DownloadDomain = {base_url_with_slash}{ending}")
        else:
            updated_lines.append(line)
    path.write_text("".join(updated_lines), encoding="utf-8")


def pe_subsystem(data: bytes | bytearray) -> tuple[int, int]:
    if data[:2] != b"MZ" or len(data) < 0x40:
        raise ValueError("DarkOrbit.exe is not a PE executable.")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError("DarkOrbit.exe has no PE header.")
    optional = pe_offset + 24
    if struct.unpack_from("<H", data, optional)[0] not in (0x10B, 0x20B):
        raise ValueError("Unsupported PE optional header.")
    subsystem_offset = optional + 68
    return subsystem_offset, struct.unpack_from("<H", data, subsystem_offset)[0]


def patch_console(exe_path: Path, boot_path: Path) -> None:
    exe_backup, _ = ensure_backup(exe_path)
    boot_backup, _ = ensure_backup(boot_path)
    exe = bytearray(exe_backup.read_bytes())
    offset, subsystem = pe_subsystem(exe)
    if subsystem != 2:
        raise ValueError(f"Original DarkOrbit.exe subsystem is {subsystem}, expected Windows GUI (2).")
    struct.pack_into("<H", exe, offset, 3)
    exe_path.write_bytes(exe)
    boot = boot_backup.read_bytes()
    lines = boot.splitlines(keepends=True)
    boot_path.write_bytes(b"".join(line for line in lines if line.strip() != b"nolog="))


def console_launcher_content() -> bytes:
    script = Path(__file__).with_name("Start-ClientConsole.ps1")
    return (f'@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File "{script}" -ClientRoot "%~dp0"\r\n').encode("utf-8")


def apply_console_launcher(client_root: Path) -> None:
    launcher = client_root / "Start-DarkOrbitConsole.cmd"
    if launcher.exists() and launcher.read_bytes() != console_launcher_content():
        ensure_backup(launcher)
    launcher.write_bytes(console_launcher_content())


def reset_console_launcher(client_root: Path) -> None:
    launcher = client_root / "Start-DarkOrbitConsole.cmd"
    backup = backup_path_for(launcher)
    if backup.exists():
        shutil.copy2(backup, launcher)
    elif launcher.exists() and launcher.read_bytes() == console_launcher_content():
        launcher.unlink()


def verify_console_reset(exe_path: Path, boot_path: Path) -> None:
    for path in (exe_path, boot_path):
        backup = backup_path_for(path)
        if backup.exists() and sha256_file(path) != sha256_file(backup):
            raise RuntimeError(f"Reset hash mismatch: {path}")


def collect_status(client_root: Path, target_base_url: str, expected_sha256: str | Iterable[str] = KNOWN_ORIG_METADATA_SHA256) -> dict:
    allowed = normalize_expected(expected_sha256)
    metadata_path = client_root / "DarkOrbit_Data" / "il2cpp_data" / "Metadata" / "global-metadata.dat"
    config_path = client_root / "DarkOrbit_Data" / "StreamingAssets" / "config.txt"
    update_path = client_root / "update.ini"
    exe_path = client_root / "DarkOrbit.exe"
    boot_path = client_root / "DarkOrbit_Data" / "boot.config"

    files_report = []
    for label, fpath, expected_orig in (
        ("global-metadata.dat", metadata_path, allowed),
        ("config.txt", config_path, None),
        ("update.ini", update_path, None),
    ):
        exists = fpath.exists()
        bak = backup_path_for(fpath)
        bak_exists = bak.exists()
        cur_sha = sha256_file(fpath) if exists else None
        bak_sha = sha256_file(bak) if bak_exists else None
        is_original = (
            (cur_sha is not None and expected_orig is not None and cur_sha.lower() in expected_orig)
            or (cur_sha is not None and bak_sha is not None and cur_sha.lower() == bak_sha.lower())
            or (cur_sha is not None and expected_orig is None and not bak_exists)
        )
        files_report.append(
            {
                "name": label,
                "path": str(fpath),
                "exists": exists,
                "size": fpath.stat().st_size if exists else 0,
                "sha256": cur_sha,
                "backup_path": str(bak),
                "backup_exists": bak_exists,
                "backup_sha256": bak_sha,
                "expected_original_sha256": sorted(expected_orig) if expected_orig is not None else None,
                "state": "ORIGINAL" if is_original else "PATCHED",
            }
        )

    literals_report = []
    header_info = {}
    resolution_error = None
    if metadata_path.exists():
        raw = metadata_path.read_bytes()
        version, lit_off, lit_size, data_off, data_size = read_il2cpp_header(raw)
        header_info = {
            "version": version,
            "stringLiteralOffset": lit_off,
            "stringLiteralSize": lit_size,
            "stringLiteralDataOffset": data_off,
            "stringLiteralDataSize": data_size,
            "literalCount": lit_size // 8,
        }
        meta_bak = backup_path_for(metadata_path)
        try:
            targets = resolve_literal_targets(meta_bak.read_bytes() if meta_bak.exists() else raw)
        except ValueError as exc:
            targets = ()
            resolution_error = str(exc)
        for target in targets:
            length, rel_off, abs_off, val = read_literal(raw, lit_off, data_off, target.index)
            planned = target.build_replacement(target_base_url)
            literals_report.append(
                {
                    "index": target.index,
                    "name": target.name,
                    "category": target.category,
                    "offset": f"0x{abs_off:x}",
                    "length": length,
                    "capacity": target.original_capacity,
                    "current_value": val,
                    "original_value": target.original_value,
                    "target_value": planned,
                    "status": "ORIGINAL" if val == target.original_value else ("PATCHED" if val == planned else "CUSTOM"),
                }
            )

    cfg_kv = inspect_config_txt(config_path)
    upd_kv = inspect_update_ini(update_path)

    return {
        "client_root": str(client_root),
        "target_base_url": target_base_url,
        "il2cpp_header": header_info,
        "literal_resolution_error": resolution_error,
        "files": files_report,
        "literals": literals_report,
        "config_txt": {"localHost": cfg_kv.get("localHost", "")},
        "update_ini": {
            "HashDomain": upd_kv.get("HashDomain", ""),
            "DownloadDomain": upd_kv.get("DownloadDomain", ""),
        },
        "console": {
            "subsystem": pe_subsystem(exe_path.read_bytes())[1] if exe_path.exists() else None,
            "boot_nolog": b"nolog=" in boot_path.read_bytes().splitlines() if boot_path.exists() else None,
            "exe_original_sha256": sha256_file(backup_path_for(exe_path)) if backup_path_for(exe_path).exists() else None,
            "boot_original_sha256": sha256_file(backup_path_for(boot_path)) if backup_path_for(boot_path).exists() else None,
        },
    }


def apply_patches(client_root: Path, target_base_url: str, patch_cdn: bool, expected_sha256: str | Iterable[str] = KNOWN_ORIG_METADATA_SHA256) -> dict:
    allowed = normalize_expected(expected_sha256)
    parsed = urlparse(target_base_url)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise ValueError(f"Invalid --target-base-url '{target_base_url}'. Expected e.g. 'https://localhost:60952'.")

    base_no_slash = f"{parsed.scheme}://{parsed.netloc}"
    base_with_slash = base_no_slash + "/"
    # localHost is a connection authority; preserve the explicit gateway port.
    local_authority = parsed.netloc

    metadata_path = client_root / "DarkOrbit_Data" / "il2cpp_data" / "Metadata" / "global-metadata.dat"
    config_path = client_root / "DarkOrbit_Data" / "StreamingAssets" / "config.txt"
    update_path = client_root / "update.ini"
    exe_path = client_root / "DarkOrbit.exe"
    boot_path = client_root / "DarkOrbit_Data" / "boot.config"

    for required in (metadata_path, config_path, update_path, exe_path, boot_path):
        if not required.exists():
            raise FileNotFoundError(f"Required client file not found: {required}")

    # Reject damaged or unrecognized metadata before creating any backups.
    meta_bak_path = backup_path_for(metadata_path)
    targets = resolve_literal_targets((meta_bak_path if meta_bak_path.exists() else metadata_path).read_bytes())

    meta_bak, _ = ensure_backup(metadata_path, allowed)
    cfg_bak, _ = ensure_backup(config_path)
    upd_bak, _ = ensure_backup(update_path)

    orig_meta_bytes = meta_bak.read_bytes()
    orig_meta_sha = hashlib.sha256(orig_meta_bytes).hexdigest()
    if orig_meta_sha.lower() not in allowed:
        raise RuntimeError(
            f"Backup {meta_bak} SHA-256 ({orig_meta_sha}) does not match expected original ({', '.join(sorted(allowed))})."
        )

    # Always start from the clean original metadata bytes so repeated Apply runs (with or without -PatchCdn) are idempotent
    working = bytearray(orig_meta_bytes)
    _, lit_off, _, data_off, _ = read_il2cpp_header(working)

    changes = []
    for target in targets:
        if target.category == "cdn" and not patch_cdn:
            continue
        replacement = target.build_replacement(base_no_slash)
        if replacement == target.original_value:
            continue
        change = write_literal_in_place(working, lit_off, data_off, target, replacement, orig_meta_bytes)
        changes.append(change)

    if len(working) != len(orig_meta_bytes):
        raise RuntimeError("Internal error: metadata size changed during in-place patch!")

    metadata_path.write_bytes(working)

    # Restore config.txt and update.ini from backup first, then apply clean settings
    shutil.copy2(cfg_bak, config_path)
    patch_config_txt(config_path, local_authority)

    shutil.copy2(upd_bak, update_path)
    patch_update_ini(update_path, base_with_slash)
    patch_console(exe_path, boot_path)
    apply_console_launcher(client_root)

    status = collect_status(client_root, base_no_slash, allowed)
    status["applied_changes"] = changes
    status["patch_cdn"] = patch_cdn
    return status


def reset_patches(client_root: Path, target_base_url: str, expected_sha256: str | Iterable[str] = KNOWN_ORIG_METADATA_SHA256) -> dict:
    allowed = normalize_expected(expected_sha256)
    metadata_path = client_root / "DarkOrbit_Data" / "il2cpp_data" / "Metadata" / "global-metadata.dat"
    config_path = client_root / "DarkOrbit_Data" / "StreamingAssets" / "config.txt"
    update_path = client_root / "update.ini"
    exe_path = client_root / "DarkOrbit.exe"
    boot_path = client_root / "DarkOrbit_Data" / "boot.config"

    restored = []
    for fpath, expected_sha in (
        (metadata_path, allowed),
        (config_path, None),
        (update_path, None),
        (exe_path, None),
        (boot_path, None),
    ):
        bak = backup_path_for(fpath)
        if bak.exists():
            shutil.copy2(bak, fpath)
            restored.append(fpath.name)
        if expected_sha is not None and fpath.exists():
            actual = sha256_file(fpath)
            if actual.lower() not in expected_sha:
                raise RuntimeError(
                    f"SHA-256 verification failed after restoring {fpath.name}: "
                    f"got {actual}, expected {', '.join(sorted(expected_sha))}."
                )

    reset_console_launcher(client_root)

    status = collect_status(client_root, target_base_url, allowed)
    verify_console_reset(exe_path, boot_path)
    status["restored_files"] = restored
    return status


def print_human_report(report: dict, action: str) -> None:
    print(f"=== DarkOrbit Unity Client URI Patcher ({action.upper()}) ===")
    print(f"Client Root     : {report['client_root']}")
    print(f"Target Base URL : {report['target_base_url']}")
    hdr = report.get("il2cpp_header") or {}
    if hdr:
        print(
            f"IL2CPP Header   : v{hdr.get('version')} | literals={hdr.get('literalCount')} "
            f"(offset={hdr.get('stringLiteralOffset')}, size={hdr.get('stringLiteralSize')}) | "
            f"dataOffset={hdr.get('stringLiteralDataOffset')}, dataSize={hdr.get('stringLiteralDataSize')}"
        )
    print()
    print("Files:")
    print(f"  {'File':<22} {'State':<10} {'Backup':<8} {'Size':>12}  {'SHA-256'}")
    print(f"  {'-'*22} {'-'*10} {'-'*8} {'-'*12}  {'-'*64}")
    for f in report["files"]:
        bak_flag = "YES" if f["backup_exists"] else "NO"
        print(f"  {f['name']:<22} {f['state']:<10} {bak_flag:<8} {f['size']:>12,}  {f['sha256'] or 'MISSING'}")
    print()
    print("IL2CPP String Literals (global-metadata.dat):")
    print(f"  {'Index':<7} {'Cat':<8} {'Offset':<10} {'Len/Cap':<9} {'Status':<9} {'Live Value'}")
    print(f"  {'-'*7} {'-'*8} {'-'*10} {'-'*9} {'-'*9} {'-'*58}")
    for lit in report["literals"]:
        len_cap = f"{lit['length']}/{lit['capacity']}"
        print(
            f"  {lit['index']:<7} {lit['category']:<8} {lit['offset']:<10} "
            f"{len_cap:<9} {lit['status']:<9} {lit['current_value']}"
        )
    print()
    print("Companion Config Files:")
    print(f"  config.txt -> localHost      : {report['config_txt'].get('localHost', '')}")
    print(f"  update.ini -> HashDomain     : {report['update_ini'].get('HashDomain', '')}")
    print(f"  update.ini -> DownloadDomain : {report['update_ini'].get('DownloadDomain', '')}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Patch or reset DarkOrbit Unity client URIs.")
    parser.add_argument(
        "--action",
        choices=("status", "apply", "reset"),
        default="status",
        help="Operation to perform (default: status).",
    )
    parser.add_argument(
        "--client-root",
        default=r"D:\Games\DarkOrbit Unity",
        help="Path to the DarkOrbit Unity client installation directory.",
    )
    parser.add_argument(
        "--target-base-url",
        default="https://localhost:60952",
        help="Target emulator base URL (must fit within original literal byte slots).",
    )
    parser.add_argument(
        "--patch-cdn",
        action="store_true",
        help="Also patch CDN and Steam partner literals (AliCdn*, BpSecure*, SteamPartnerTokenUrl).",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Emit JSON output instead of human-readable table.",
    )
    parser.add_argument(
        "--fixture-original-sha256",
        help="Synthetic repo-local fixture only: expected original metadata SHA-256.",
    )
    args = parser.parse_args()

    client_root = Path(args.client_root).resolve()
    expected_sha256: str | Iterable[str] = KNOWN_ORIG_METADATA_SHA256
    if args.fixture_original_sha256 is not None:
        repo_root = Path(__file__).resolve().parents[2]
        if not client_root.is_relative_to(repo_root) or client_root == repo_root:
            parser.error("--fixture-original-sha256 requires a client root strictly inside the repository.")
        if len(args.fixture_original_sha256) != 64 or any(c not in "0123456789abcdefABCDEF" for c in args.fixture_original_sha256):
            parser.error("--fixture-original-sha256 must be a 64-character hexadecimal SHA-256 digest.")
        expected_sha256 = args.fixture_original_sha256
    action = args.action.lower()

    if action == "apply":
        report = apply_patches(client_root, args.target_base_url, args.patch_cdn, expected_sha256)
    elif action == "reset":
        report = reset_patches(client_root, args.target_base_url, expected_sha256)
    else:
        report = collect_status(client_root, args.target_base_url, expected_sha256)

    if args.json:
        print(json.dumps(report, indent=2))
    else:
        print_human_report(report, action)

    return 0


if __name__ == "__main__":
    sys.exit(main())

