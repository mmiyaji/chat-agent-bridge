#!/usr/bin/env python3
"""Build the public portable-release and pricing feeds from local JSON files."""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import tempfile
import uuid
from decimal import Decimal
from pathlib import Path
from urllib.parse import quote, urlsplit

REPOSITORY = "mmiyaji/chat-agent-bridge"
MAX_ASSET_SIZE = 1024 * 1024 * 1024
MAX_PRICING_BYTES = 512 * 1024
MAX_RELEASES = 50
MAX_FEED_BYTES = 2 * 1024 * 1024
VERSION_RE = re.compile(r"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-preview\.(0|[1-9][0-9]*))?$", re.IGNORECASE)
DIGEST_RE = re.compile(r"^sha256:[0-9a-fA-F]{64}$", re.IGNORECASE)


class FeedError(ValueError):
    """An input feed cannot safely be published."""


def parse_version(tag: object) -> tuple[int, int, int, int, int] | None:
    if not isinstance(tag, str):
        return None
    match = VERSION_RE.fullmatch(tag)
    if not match:
        return None
    values = [int(part or "0") for part in match.groups()]
    # ReleaseVersion uses Int32 fields; keep the publisher's ordering identical.
    if any(number > 2_147_483_647 for number in values):
        return None
    major, minor, patch, preview_number = values
    is_preview = 1 if match.group(4) is not None else 0
    return major, minor, patch, is_preview, preview_number


def version_sort_key(version: tuple[int, int, int, int, int]) -> tuple[int, int, int, int, int]:
    major, minor, patch, is_preview, preview_number = version
    return major, minor, patch, 1 - is_preview, preview_number


def _exact_https_url(value: object, expected_path: str) -> bool:
    if not isinstance(value, str):
        return False
    try:
        parsed = urlsplit(value)
        return (parsed.scheme == "https" and parsed.hostname == "github.com" and
                parsed.port in (None, 443) and not parsed.username and not parsed.password and
                not parsed.query and not parsed.fragment and parsed.path == expected_path)
    except ValueError:
        return False


def _release_page(tag: str) -> str:
    return f"https://github.com/{REPOSITORY}/releases/tag/{quote(tag, safe='')}"


def _asset_url(tag: str, name: str) -> str:
    return f"https://github.com/{REPOSITORY}/releases/download/{quote(tag, safe='')}/{quote(name, safe='')}"


def _release_record(item: object) -> dict | None:
    if not isinstance(item, dict) or item.get("draft") is not False:
        return None
    tag = item.get("tag_name")
    parsed_version = parse_version(tag)
    if parsed_version is None:
        return None
    is_preview = parsed_version[3] == 1
    if not isinstance(item.get("prerelease"), bool) or item["prerelease"] != is_preview:
        return None
    page_path = f"/{REPOSITORY}/releases/tag/{quote(tag, safe='')}"
    if not _exact_https_url(item.get("html_url"), page_path):
        return None
    assets = item.get("assets")
    if not isinstance(assets, list):
        return None

    portable_assets = []
    for architecture in ("win-x64", "win-arm64"):
        name = f"ChatAgentBridge.Portable-{tag.removeprefix('v')}-{architecture}.zip"
        expected_url = _asset_url(tag, name)
        asset_path = f"/{REPOSITORY}/releases/download/{quote(tag, safe='')}/{quote(name, safe='')}"
        for asset in assets:
            if not isinstance(asset, dict) or asset.get("name") != name:
                continue
            size = asset.get("size")
            digest = asset.get("digest")
            url = asset.get("browser_download_url")
            if (isinstance(size, bool) or not isinstance(size, int) or not 1 <= size <= MAX_ASSET_SIZE or
                    not isinstance(digest, str) or DIGEST_RE.fullmatch(digest) is None or
                    not _exact_https_url(url, asset_path) or url != expected_url):
                continue
            portable_assets.append({
                "name": name,
                "size": size,
                "digest": digest.lower(),
                "browser_download_url": expected_url,
            })
            break
    if not portable_assets:
        return None
    return {
        "tag_name": tag,
        "html_url": _release_page(tag),
        "draft": False,
        "prerelease": is_preview,
        "assets": portable_assets,
        "_version": parsed_version,
    }


def build_releases(raw: object, require_tag: str | None = None) -> tuple[list[dict], list[dict]]:
    if not isinstance(raw, list):
        raise FeedError("GitHub releases input must be a JSON array")
    required_version = parse_version(require_tag) if require_tag is not None else None
    if require_tag is not None and required_version is None:
        raise FeedError(f"required tag is not a supported release version: {require_tag}")

    records = []
    seen_versions = set()
    required_seen = False
    for item in raw:
        # Track the requested tag before filtering so invalid metadata fails the build.
        requested = isinstance(item, dict) and item.get("tag_name") == require_tag if require_tag else False
        record = _release_record(item)
        if requested:
            if record is None:
                raise FeedError(f"required tag has invalid metadata or no valid portable assets: {require_tag}")
            required_seen = True
        if record is None:
            continue
        version = record.pop("_version")
        if version in seen_versions:
            raise FeedError(f"multiple valid releases use the same version: {record['tag_name']}")
        seen_versions.add(version)
        record["_version"] = version
        records.append(record)
    if require_tag is not None and not required_seen:
        raise FeedError(f"required tag was not found in the valid releases: {require_tag}")
    records.sort(key=lambda record: version_sort_key(record["_version"]), reverse=True)
    stable = []
    preview = []
    newest_stable = next((record for record in records if not record["prerelease"]), None)
    for record in records:
        public_record = {key: value for key, value in record.items() if key != "_version"}
        preview.append(public_record)
        if not record["prerelease"]:
            stable.append(public_record)
    stable = stable[:MAX_RELEASES]
    preview = preview[:MAX_RELEASES]
    if newest_stable is not None and all(item["tag_name"] != newest_stable["tag_name"] for item in preview):
        preview.append({key: value for key, value in newest_stable.items() if key != "_version"})
        preview.sort(key=lambda item: version_sort_key(parse_version(item["tag_name"])), reverse=True)
    return stable, preview


def _finite_number(value: object) -> bool:
    if isinstance(value, bool) or not isinstance(value, (int, float, Decimal)):
        return False
    if isinstance(value, Decimal):
        return value.is_finite()
    return math.isfinite(value)


MAX_DOTNET_DECIMAL = Decimal("79228162514264337593543950335")


def _dotnet_decimal(value: object) -> bool:
    if not _finite_number(value):
        return False
    number = Decimal(value) if isinstance(value, int) else (value if isinstance(value, Decimal) else Decimal(str(value)))
    return abs(number) <= MAX_DOTNET_DECIMAL and max(0, -number.as_tuple().exponent) <= 28


def _json_bytes(value: object) -> bytes:
    marker = uuid.uuid4().hex
    decimals = {}

    def replace_decimal(item):
        if isinstance(item, Decimal):
            token = f"__CAB_DECIMAL_{marker}_{len(decimals)}__"
            decimals[token] = format(item, "f")
            return token
        if isinstance(item, dict):
            return {key: replace_decimal(child) for key, child in item.items()}
        if isinstance(item, list):
            return [replace_decimal(child) for child in item]
        return item

    serialized = json.dumps(replace_decimal(value), ensure_ascii=False, indent=2, allow_nan=False)
    for token, number in decimals.items():
        serialized = serialized.replace(json.dumps(token), number)
    return (serialized + "\n").encode("utf-8")


def validate_pricing(raw_bytes: bytes) -> dict:
    if not raw_bytes or len(raw_bytes) > MAX_PRICING_BYTES:
        raise FeedError("pricing catalog size is invalid")
    try:
        catalog = json.loads(raw_bytes.decode("utf-8"), parse_float=Decimal,
                             parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
        raise FeedError(f"pricing catalog is not valid JSON: {error}") from error
    if not isinstance(catalog, dict) or type(catalog.get("schemaVersion")) is not int or catalog.get("schemaVersion") != 1:
        raise FeedError("pricing catalog must use schemaVersion 1")
    if catalog.get("currency") != "USD" or catalog.get("unitTokens") != 1_000_000 or isinstance(catalog.get("unitTokens"), bool):
        raise FeedError("pricing catalog must use USD and one million token units")
    checked_on = catalog.get("checkedOn")
    if not isinstance(checked_on, str) or re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", checked_on) is None:
        raise FeedError("pricing catalog has an invalid checked-on date")
    from datetime import date
    try:
        date.fromisoformat(checked_on)
    except ValueError as error:
        raise FeedError("pricing catalog has an invalid checked-on date") from error

    raw_sources = catalog.get("sources")
    if not isinstance(raw_sources, dict):
        raise FeedError("pricing catalog has no source map")
    sources = {}
    for name, url in raw_sources.items():
        if (not isinstance(name, str) or not name or any(ord(ch) < 32 for ch in name) or
                not isinstance(url, str)):
            raise FeedError("pricing catalog contains an invalid source")
        try:
            parsed = urlsplit(url)
            valid = parsed.scheme == "https" and bool(parsed.hostname) and not parsed.username and not parsed.password
        except ValueError:
            valid = False
        if not valid:
            raise FeedError("pricing catalog contains a non-HTTPS source")
        sources[name] = url

    raw_models = catalog.get("models")
    if not isinstance(raw_models, list) or not 1 <= len(raw_models) <= 4096:
        raise FeedError("pricing catalog model list is invalid")
    models = []
    seen_models = set()
    for model in raw_models:
        if not isinstance(model, dict):
            raise FeedError("pricing catalog contains an invalid model")
        model_id = model.get("model")
        source = model.get("source")
        if (not isinstance(model_id, str) or not model_id.strip() or len(model_id) > 200 or
                any(ord(ch) < 32 for ch in model_id) or not isinstance(source, str) or source not in sources):
            raise FeedError("pricing catalog contains an invalid model ID or source")
        if model_id in seen_models:
            raise FeedError(f"pricing catalog duplicates model '{model_id}'")
        seen_models.add(model_id)
        clean_model = {"model": model_id}
        for rate_name in ("input", "cachedInput", "cacheWrite", "output"):
            rate = model.get(rate_name)
            if (not isinstance(rate, list) or len(rate) != 2 or
                    not all(_dotnet_decimal(number) for number in rate) or rate[0] < 0 or rate[1] < rate[0]):
                raise FeedError(f"pricing rate '{rate_name}' is invalid")
            clean_model[rate_name] = rate
        clean_model["source"] = source
        models.append(clean_model)
    return {
        "schemaVersion": 1,
        "checkedOn": checked_on,
        "currency": "USD",
        "unitTokens": 1_000_000,
        "sources": sources,
        "models": models,
    }


def atomic_json(path: Path, value: object, max_bytes: int | None = None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    data = _json_bytes(value)
    if max_bytes is not None and len(data) > max_bytes:
        raise FeedError(f"serialized feed exceeds {max_bytes} bytes")
    temp_name = None
    try:
        with tempfile.NamedTemporaryFile(mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False) as stream:
            temp_name = stream.name
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_name, path)
    finally:
        if temp_name is not None and os.path.exists(temp_name):
            os.unlink(temp_name)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--releases", required=True, type=Path, help="local GitHub Releases API JSON array")
    parser.add_argument("--pricing", required=True, type=Path, help="local model-prices.json")
    parser.add_argument("--output", required=True, type=Path, help="distribution site directory")
    parser.add_argument("--require-tag", help="fail unless this exact release tag is valid and publishable")
    args = parser.parse_args(argv)
    try:
        raw_releases = json.loads(args.releases.read_text(encoding="utf-8"))
        stable, preview = build_releases(raw_releases, args.require_tag)
        pricing = validate_pricing(args.pricing.read_bytes())
        output = args.output
        atomic_json(output / "updates" / "stable.json", {"schemaVersion": 1, "channel": "stable", "releases": stable}, MAX_FEED_BYTES)
        atomic_json(output / "updates" / "preview.json", {"schemaVersion": 1, "channel": "preview", "releases": preview}, MAX_FEED_BYTES)
        atomic_json(output / "pricing" / "models.json", pricing, MAX_PRICING_BYTES)
        (output / "index.html").parent.mkdir(parents=True, exist_ok=True)
        write_index(output / "index.html")
    except (OSError, json.JSONDecodeError, FeedError) as error:
        print(f"distribution feed: {error}", file=sys.stderr)
        return 1
    print(f"Wrote {len(stable)} stable and {len(preview)} preview releases to {args.output}")
    return 0


def write_index(path: Path) -> None:
    html = """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Chat Agent Bridge distribution</title></head><body>
<main><h1>Chat Agent Bridge distribution</h1>
<p>This site hosts public portable release metadata and model pricing data for Chat Agent Bridge.</p>
<p><a href="updates/stable.json">Stable updates</a> · <a href="updates/preview.json">Preview updates</a> · <a href="pricing/models.json">Model pricing</a></p>
<p>Download portable releases from the <a href="https://github.com/mmiyaji/chat-agent-bridge/releases">GitHub releases page</a>.</p></main>
</body></html>
"""
    temp_name = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=path.parent,
                                         prefix=f".{path.name}.", suffix=".tmp", delete=False) as stream:
            temp_name = stream.name
            stream.write(html)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_name, path)
    finally:
        if temp_name is not None and os.path.exists(temp_name):
            os.unlink(temp_name)


if __name__ == "__main__":
    raise SystemExit(main())
