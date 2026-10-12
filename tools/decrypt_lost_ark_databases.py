#!/usr/bin/env python3
"""Decrypt raw Lost Ark Steam/global database entries dumped from data2.lpk."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path

try:
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
except ImportError as exc:  # pragma: no cover - depends on the invoking runtime
    raise SystemExit(
        "The 'cryptography' Python package is required to decrypt Lost Ark databases."
    ) from exc


BLOCK_SIZE = 0x400
ZERO_IV = bytes(16)
STEAM_BASE = bytes.fromhex("1069d88738c5c75f82b44a1f0a382768")
RAW_NAME = re.compile(r"^(?P<name>.+)\.__arkive_size_(?P<size>\d+)\.encrypted$")


def database_key(filename: str) -> bytes:
    """Reproduce the filename-based v5/v6 key derivation."""
    last_component = filename.rsplit("_", 1)[-1]
    key_name = last_component.split(".", 1)[0]
    digest = hashlib.md5(key_name.encode("utf-16le")).digest()
    mixed = bytes(STEAM_BASE[index] ^ digest[15 - index] for index in range(16))
    return hashlib.sha256(mixed.hex().encode("ascii")).digest()


def decrypt_payload(payload: bytes, key: bytes, plaintext_size: int) -> bytes:
    if len(payload) % 16:
        raise ValueError(f"encrypted payload size {len(payload)} is not AES-aligned")
    # Decrypt as one CBC stream, then correct each reset boundary. At the first
    # block of a 0x400-byte segment, continuous CBC XORed the previous segment's
    # final ciphertext block where Lost Ark uses a fresh zero IV.
    decryptor = Cipher(algorithms.AES(key), modes.CBC(ZERO_IV)).decryptor()
    output = bytearray(decryptor.update(payload) + decryptor.finalize())
    for offset in range(BLOCK_SIZE, len(payload), BLOCK_SIZE):
        previous_ciphertext = payload[offset - 16 : offset]
        for index, value in enumerate(previous_ciphertext):
            output[offset + index] ^= value
    if plaintext_size > len(output):
        raise ValueError(
            f"plaintext size {plaintext_size} exceeds decrypted size {len(output)}"
        )
    return bytes(output[:plaintext_size])


def decode_tree(source_root: Path, output_root: Path) -> tuple[int, int]:
    decoded = 0
    sqlite_headers = 0
    for source in source_root.rglob("*.encrypted"):
        match = RAW_NAME.match(source.name)
        if not match:
            continue
        filename = match.group("name")
        plaintext_size = int(match.group("size"))
        relative_parent = source.relative_to(source_root).parent
        destination = output_root / relative_parent / filename
        destination.parent.mkdir(parents=True, exist_ok=True)
        plaintext = decrypt_payload(
            source.read_bytes(), database_key(filename), plaintext_size
        )
        temporary = destination.with_name(destination.name + ".tmp")
        temporary.write_bytes(plaintext)
        temporary.replace(destination)
        decoded += 1
        if plaintext.startswith(b"SQLite format 3\x00"):
            sqlite_headers += 1
    return decoded, sqlite_headers


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="QuickBMS encrypted dump directory")
    parser.add_argument("output", type=Path, help="directory for decrypted databases")
    args = parser.parse_args()
    if not args.source.is_dir():
        parser.error(f"source directory does not exist: {args.source}")
    decoded, sqlite_headers = decode_tree(args.source, args.output)
    print(f"Decoded {decoded} entries; {sqlite_headers} have SQLite headers.")
    return 0 if decoded else 1


if __name__ == "__main__":
    sys.exit(main())
