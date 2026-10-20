import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import distribution_feed as feed


DIGEST = "sha256:" + "a" * 64


def asset(tag, architecture="win-x64", **changes):
    version = tag[1:] if tag.startswith("v") else tag
    name = f"ChatAgentBridge.Portable-{version}-{architecture}.zip"
    result = {
        "name": name,
        "size": 125,
        "digest": DIGEST,
        "browser_download_url": f"https://github.com/mmiyaji/chat-agent-bridge/releases/download/{tag}/{name}",
        "url": "https://api.github.com/opaque-private-api-url",
    }
    result.update(changes)
    return result


def release(tag="v1.2.3", assets=None, **changes):
    result = {
        "tag_name": tag,
        "draft": False,
        "prerelease": "preview" in tag,
        "html_url": f"https://github.com/mmiyaji/chat-agent-bridge/releases/tag/{tag}",
        "assets": assets if assets is not None else [asset(tag)],
        "body": "could contain a token and must never be copied",
    }
    result.update(changes)
    return result


def pricing():
    return {
        "schemaVersion": 1,
        "checkedOn": "2026-10-20",
        "currency": "USD",
        "unitTokens": 1_000_000,
        "sources": {"vendor": "https://example.com/pricing"},
        "models": [{
            "model": "model-a", "input": [1, 2], "cachedInput": [0, 1],
            "cacheWrite": [1, 3], "output": [4, 5], "source": "vendor",
            "extra": "ignore secret field",
        }],
    }


class ReleaseFeedTests(unittest.TestCase):
    def test_stable_and_preview_channels_sorted_numerically_with_arch_filter(self):
        stable, preview = feed.build_releases([
            release("v1.9.0"),
            release("v1.10.0", assets=[asset("v1.10.0", "win-arm64"), asset("v1.10.0", "win-x64")]),
            release("v1.11.0-preview.2", assets=[asset("v1.11.0-preview.2", "win-arm64")]),
        ])
        self.assertEqual(["v1.10.0", "v1.9.0"], [r["tag_name"] for r in stable])
        self.assertEqual(["v1.11.0-preview.2", "v1.10.0", "v1.9.0"], [r["tag_name"] for r in preview])
        self.assertEqual([asset("v1.11.0-preview.2", "win-arm64")["name"]], [preview[0]["assets"][0]["name"]])
        self.assertEqual({"tag_name", "html_url", "draft", "prerelease", "assets"}, set(preview[0]))

    def test_rejects_wrong_urls_traversal_wrong_names_and_bad_digest_or_size(self):
        tag = "v1.2.3"
        candidates = [
            asset(tag, browser_download_url="https://evil.example/file.zip"),
            asset(tag, name="../ChatAgentBridge.Portable-v1.2.3-win-x64.zip"),
            asset(tag, digest="sha256:bad"),
            asset(tag, size=0),
            asset(tag, size=1024 * 1024 * 1024 + 1),
            asset(tag, browser_download_url="https://github.com/mmiyaji/chat-agent-bridge/releases/download/v1.2.3/other.zip"),
        ]
        self.assertEqual(([], []), feed.build_releases([release(tag, assets=candidates)]))

    def test_metadata_mismatch_and_unsupported_semver_are_skipped(self):
        mismatch = release("v1.0.0", prerelease=True)
        draft = release("v2.0.0", draft=True)
        unsupported = release("v3.0.0-rc.1")
        self.assertEqual(([], []), feed.build_releases([mismatch, draft, unsupported]))

    def test_duplicate_versions_are_rejected(self):
        with self.assertRaisesRegex(feed.FeedError, "same version"):
            feed.build_releases([release("v1.0.0"), release("1.0.0")])

    def test_stable_release_precedes_preview_with_same_base_version(self):
        stable, preview = feed.build_releases([
            release("v1.2.3-preview.99"),
            release("v1.2.3"),
        ])
        self.assertEqual(["v1.2.3", "v1.2.3-preview.99"], [r["tag_name"] for r in preview])
        self.assertEqual(["v1.2.3"], [r["tag_name"] for r in stable])

    def test_preview_feed_keeps_newest_stable_after_more_than_limit_previews(self):
        releases = [release("v1.0.0")]
        releases.extend(release(f"v1.{patch}.0-preview.1") for patch in range(1, feed.MAX_RELEASES + 2))
        _, preview = feed.build_releases(releases)
        self.assertEqual(feed.MAX_RELEASES + 1, len(preview))
        self.assertEqual("v1.51.0-preview.1", preview[0]["tag_name"])
        self.assertEqual("v1.0.0", preview[-1]["tag_name"])

    def test_require_tag_requires_exact_valid_publishable_release(self):
        with self.assertRaisesRegex(feed.FeedError, "not found"):
            feed.build_releases([release("v1.0.0")], "v2.0.0")
        bad = release("v2.0.0", assets=[])
        with self.assertRaisesRegex(feed.FeedError, "invalid metadata"):
            feed.build_releases([bad], "v2.0.0")
        stable, preview = feed.build_releases([release("v2.0.0")], "v2.0.0")
        self.assertEqual(["v2.0.0"], [r["tag_name"] for r in stable])
        self.assertEqual(["v2.0.0"], [r["tag_name"] for r in preview])

    def test_empty_array_is_a_valid_empty_feed(self):
        self.assertEqual(([], []), feed.build_releases([]))
        with self.assertRaises(feed.FeedError):
            feed.build_releases({})


class PricingTests(unittest.TestCase):
    def test_validates_and_strips_unknown_fields(self):
        parsed = feed.validate_pricing(json.dumps(pricing()).encode())
        self.assertEqual(["model", "input", "cachedInput", "cacheWrite", "output", "source"], list(parsed["models"][0]))
        self.assertNotIn("extra", parsed["models"][0])

    def test_rejects_bad_schema_and_duplicate_model(self):
        bad = pricing()
        bad["schemaVersion"] = 2
        with self.assertRaises(feed.FeedError):
            feed.validate_pricing(json.dumps(bad).encode())
        bad = pricing()
        bad["models"].append(copy.deepcopy(bad["models"][0]))
        with self.assertRaisesRegex(feed.FeedError, "duplicates"):
            feed.validate_pricing(json.dumps(bad).encode())

    def test_rejects_invalid_rates_non_finite_values_and_missing_sources(self):
        for rate in ([-1, 2], [3, 2], [0, float("nan")], [0, float("inf")]):
            bad = pricing()
            bad["models"][0]["input"] = rate
            with self.subTest(rate=rate), self.assertRaises(feed.FeedError):
                feed.validate_pricing(json.dumps(bad).encode())
        bad = pricing()
        bad["models"][0]["source"] = "unknown"
        with self.assertRaises(feed.FeedError):
            feed.validate_pricing(json.dumps(bad).encode())
        with self.assertRaises(feed.FeedError):
            feed.validate_pricing(b'{"schemaVersion":1,"x":NaN}')

    def test_rejects_rates_outside_dotnet_decimal_range(self):
        for raw_rate in ("[79228162514264337593543950336, 79228162514264337593543950336]",
                         "[1e-29, 1e-29]"):
            raw = json.dumps(pricing()).replace("[1, 2]", raw_rate, 1).encode()
            with self.subTest(rate=raw_rate), self.assertRaises(feed.FeedError):
                feed.validate_pricing(raw)

    def test_rejects_non_https_source_and_invalid_date(self):
        bad = pricing()
        bad["sources"]["vendor"] = "http://example.com/pricing"
        with self.assertRaises(feed.FeedError):
            feed.validate_pricing(json.dumps(bad).encode())
        bad = pricing()
        bad["checkedOn"] = "2026-02-30"
        with self.assertRaises(feed.FeedError):
            feed.validate_pricing(json.dumps(bad).encode())


class OutputTests(unittest.TestCase):
    def test_cli_writes_public_contracts_atomically_and_preserves_unrelated_files(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            input_releases = base / "releases.json"
            input_pricing = base / "prices.json"
            output = base / "site"
            input_releases.write_text(json.dumps([release("v1.0.0")]), encoding="utf-8")
            catalog = pricing()
            catalog["models"][0]["input"] = [0.1, 0.2]
            input_pricing.write_text(json.dumps(catalog), encoding="utf-8")
            output.mkdir()
            (output / "user-file.txt").write_text("keep", encoding="utf-8")
            status = feed.main(["--releases", str(input_releases), "--pricing", str(input_pricing), "--output", str(output)])
            self.assertEqual(0, status)
            stable = json.loads((output / "updates" / "stable.json").read_text(encoding="utf-8"))
            preview = json.loads((output / "updates" / "preview.json").read_text(encoding="utf-8"))
            models = json.loads((output / "pricing" / "models.json").read_text(encoding="utf-8"))
            self.assertEqual("stable", stable["channel"])
            self.assertEqual("preview", preview["channel"])
            self.assertEqual(stable["releases"], preview["releases"])
            self.assertEqual("USD", models["currency"])
            self.assertEqual([0.1, 0.2], models["models"][0]["input"])
            self.assertTrue((output / "index.html").is_file())
            self.assertEqual("keep", (output / "user-file.txt").read_text(encoding="utf-8"))
            published = json.dumps([stable, preview, models, (output / "index.html").read_text(encoding="utf-8")])
            self.assertNotIn("private-api-url", published)
            self.assertNotIn("token", published)


if __name__ == "__main__":
    unittest.main()
