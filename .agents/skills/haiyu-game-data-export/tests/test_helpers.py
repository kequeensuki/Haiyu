"""Anonymous fixtures for the skill's diagnostic tools; no real capture is stored."""
from __future__ import annotations

from contextlib import redirect_stderr, redirect_stdout
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


audit = load("capture_audit")
checks = load("run_export_checks")


class HelperTests(unittest.TestCase):
    def fixtures(self):
        uid, sid, capture = 900000001, "c" * 32, "anonymous-capture"
        def event(index, kind, payload, player=uid):
            return {"captureId": capture, "captureEventIndex": index, "eventIndex": index,
                    "playerId": player, "sessionId": sid, "msgId": 100 + index,
                    "messageType": "Aki.Protocol." + kind, "decodeStatus": "decoded", "payload": payload}
        events = [event(1, "ProtoKeyResponse", {"key": "NEVER_OUTPUT_KEY"}, None),
                  event(2, "PbGetRoleListNotify", {"roleList": [{"roleId": 11, "baseProp": [{"key": 1, "value": 10}, {"key": 1, "value": 20}]}]}),
                  event(3, "PbRolePropsNotify", {"roleId": 22, "baseProp": [{"key": 1, "value": 30}]}),
                  event(4, "PbRoleActiveNotify", {"role": {"roleId": 22}}),
                  event(5, "MailAddNotify", {"newMail": {"id": "private-mail-id", "content": "NEVER_OUTPUT_BODY"}})]
        manifest = {"captureId": capture, "moduleId": "server-messages", "state": "completed",
                    "written": 5, "failed": 0, "dropped": 0, "sessionBindings": {sid: uid}}
        index = {"captureId": capture, "state": "incomplete", "modules": [{"moduleId": "inventory", "state": "Failed",
                 "error": f"玩家 {uid}: session={sid}", "counters": [{"name": "currentPlayer", "value": uid}],
                 "sources": [{"sourceId": "roles", "playerId": uid, "sessionId": sid, "state": "AwaitingBaseline"}]}]}
        data = {"GameDataExport/captures/c/manifest.json": json.dumps(manifest).encode(),
                "GameDataExport/captures/c/capture-index.json": json.dumps(index).encode(),
                f"GameDataExport/{uid}/game-data-20200101-000000.json": json.dumps({"sessionKeyHex": "NEVER_OUTPUT_KEY", "inventory": {}}).encode(),
                f"GameDataExport/{uid}/captures/c/events-000001.jsonl": b"\n".join(json.dumps(e).encode() for e in events),
                f"GameDataExport/{uid}/captures/c/modules/item-changes/events-000001.jsonl": b"\n".join(json.dumps(e).encode() for e in events[1:])}
        return data

    def report(self, data, **kwargs):
        return audit.audit([(name, lambda body=body: io.BytesIO(body)) for name, body in data.items()], **kwargs)

    def test_audit_exposes_failure_and_legitimate_initialization(self):
        result = self.report(self.fixtures())
        self.assertTrue(result["auditCompleted"])
        self.assertTrue(any(f["severity"] == "error" for f in result["findings"]))
        self.assertEqual(result["filteredRecordsChecked"], 4)
        self.assertEqual(result["filteredRecordsUnmatched"], 0)
        self.assertEqual(result["captures"][0]["playerRecordsAfterBindings"], {"player-1": 5})
        self.assertEqual(result["repeatedFields"][0]["duplicateKeys"], [1])
        self.assertEqual(result["repeatedFields"][0]["conflictingKeyCount"], 1)
        self.assertEqual(result["initializationSequences"][0]["firstIndex"], 3)
        self.assertEqual(result["initializationSequences"][0]["activeIndex"], 4)

    def test_private_payloads_and_identifiers_are_not_emitted(self):
        result = json.dumps(self.report(self.fixtures()))
        for value in ["900000001", "c" * 32, "NEVER_OUTPUT_KEY", "NEVER_OUTPUT_BODY", "private-mail-id"]:
            self.assertNotIn(value, result)
        explicit = json.dumps(self.report(self.fixtures(), include_identifiers=True))
        self.assertIn("900000001", explicit)
        self.assertNotIn("NEVER_OUTPUT_BODY", explicit)

    def test_filtered_payload_mismatch_is_detected(self):
        data = self.fixtures()
        name = next(name for name in data if "modules/" in name)
        data[name] = data[name].replace(b"NEVER_OUTPUT_BODY", b"DIFFERENT")
        self.assertEqual(self.report(data)["filteredRecordsUnmatched"], 1)

    def test_missing_activation_is_observable(self):
        data = self.fixtures()
        for name in list(data):
            if name.endswith(".jsonl"):
                rows = [json.loads(line) for line in data[name].splitlines()]
                data[name] = b"\n".join(json.dumps(row).encode() for row in rows if row["captureEventIndex"] != 4)
        result = self.report(data)
        self.assertIsNone(result["initializationSequences"][0]["activeIndex"])
        self.assertEqual(result["captures"][0]["missingWithinObservedRange"], 1)

    def test_strict_mode_and_read_only_zip(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "capture.zip"
            with zipfile.ZipFile(source, "w") as archive:
                for name, body in self.fixtures().items():
                    archive.writestr(name, body)
            before = hashlib.sha256(source.read_bytes()).hexdigest()
            with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                code = audit.main([str(source), "--strict", "--output", str(root / "report.json")])
                rejected = audit.main([str(source), "--output", str(source)])
            self.assertEqual(code, 2)
            self.assertEqual(rejected, 1)
            self.assertEqual(hashlib.sha256(source.read_bytes()).hexdigest(), before)
            with self.assertRaises(ValueError):
                with audit.input_files(source, 1):
                    self.fail("Size bound must reject this input")

    def test_oversized_line_rejected(self):
        with self.assertRaises(ValueError):
            self.report(self.fixtures(), max_line_bytes=5)

    def test_new_test_must_actually_pass(self):
        text = "PASS  新测试名称  2 ms\nALL TESTS PASSED\n"
        self.assertEqual(checks.verify_managed_log(text, ["新测试名称"], 1), 1)
        for bad, expected in [("ALL TESTS PASSED", []), (text, ["未执行的测试"]), ("PASS  新测试名称  2 ms", [])]:
            with self.assertRaises(ValueError):
                checks.verify_managed_log(bad, expected, 1)

    def test_failed_decode_with_null_payload_is_reported_without_raw_bytes(self):
        data = self.fixtures()
        name = next(name for name in data if name.endswith(".jsonl") and "modules/" not in name)
        rows = [json.loads(line) for line in data[name].splitlines()]
        rows[0].update(payload=None, rawPayload="PRIVATE_RAW_CIPHERTEXT", decodeStatus="parse_failed")
        data[name] = b"\n".join(json.dumps(row).encode() for row in rows)
        result = self.report(data)
        self.assertTrue(result["auditCompleted"])
        self.assertTrue(any(f["kind"] == "raw-decode-status" and f["severity"] == "error" for f in result["findings"]))
        self.assertNotIn("PRIVATE_RAW_CIPHERTEXT", json.dumps(result))

    def test_source_fingerprint_covers_root_config_and_new_sources_but_not_generated_files(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            (repo / "src").mkdir()
            (repo / ".gitignore").write_text("obj/\n", encoding="utf-8")
            (repo / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
            (repo / "src/Existing.cs").write_text("class Existing {}", encoding="utf-8")
            subprocess.run(["git", "init", "--quiet"], cwd=repo, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            subprocess.run(["git", "add", ".gitignore", "Directory.Build.props", "src/Existing.cs"], cwd=repo, check=True)
            first = checks.fingerprint(repo)
            (repo / "Directory.Build.props").write_text("<Project><PropertyGroup /></Project>", encoding="utf-8")
            changed = checks.fingerprint(repo)
            self.assertNotEqual(first, changed)
            (repo / "src/obj").mkdir()
            (repo / "src/obj/Generated.cs").write_text("generated", encoding="utf-8")
            self.assertEqual(changed, checks.fingerprint(repo))
            (repo / "src/New.cs").write_text("class New {}", encoding="utf-8")
            self.assertNotEqual(changed, checks.fingerprint(repo))

    def test_skill_frontmatter_and_links(self):
        main = (ROOT / "SKILL.md").read_text(encoding="utf-8")
        self.assertTrue(main.startswith("---\n"))
        self.assertIn("\nname: haiyu-game-data-export\n", main)
        self.assertRegex(main, r"(?m)^description: .+")
        for doc in ROOT.rglob("*.md"):
            for link in re.findall(r"\]\(([^)]+)\)", doc.read_text(encoding="utf-8")):
                if "://" not in link and not link.startswith("#"):
                    self.assertTrue((doc.parent / link.split("#")[0]).is_file(), (doc, link))


if __name__ == "__main__":
    unittest.main()
