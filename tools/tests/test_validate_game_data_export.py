"""离线验收工具的独立样本：正常跨文件归属、重复编号、尾记录损坏、计数错误。"""
import importlib.util
import json
import pathlib
import tempfile
import unittest

MODULE = pathlib.Path(__file__).parents[1] / "validate_game_data_export.py"
SPEC = importlib.util.spec_from_file_location("validator", MODULE)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class ValidatorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.manifest = self.root / "captures" / "test" / "manifest.json"
        self.manifest.parent.mkdir(parents=True)
        self.row = dict(captureId="test", schemaVersion="schema", eventIndex=1,
                        receivedAt="2026-11-05T12:00:00+00:00", playerId=None,
                        sessionId="login", direction="server_to_client", decodeStatus="decoded",
                        hasUnknownFields=False, payload={"value": "9007199254740993"})
        self.info = dict(captureId="test", schemaVersion="schema", state="completed", error=None,
                         sessionBindings={"login": 123}, files=["captures/test/unresolved-000001.jsonl"],
                         received=1, written=1, decoded=1, unknown=0, failed=0, unresolved=0, dropped=0,
                         players=[dict(playerId=123, written=1, decoded=1, unknown=0, failed=0)])

    def tearDown(self):
        self.temp.cleanup()

    def write(self, content=None):
        self.manifest.write_text(json.dumps(self.info), encoding="utf-8")
        (self.root / self.info["files"][0]).write_text(
            content if content is not None else json.dumps(self.row) + "\n", encoding="utf-8")

    def test_pending_binding(self):
        self.write()
        self.assertEqual(VALIDATOR.validate(self.manifest)[1], 1)

    def test_duplicate_index(self):
        self.write((json.dumps(self.row) + "\n") * 2)
        with self.assertRaises(ValueError):
            VALIDATOR.validate(self.manifest)

    def test_partial_tail(self):
        self.write(json.dumps(self.row))
        with self.assertRaises(ValueError):
            VALIDATOR.validate(self.manifest)

    def test_bad_counts(self):
        self.info["decoded"] = 2
        self.write()
        with self.assertRaises(ValueError):
            VALIDATOR.validate(self.manifest)

    def test_wrong_owner(self):
        self.row["playerId"] = 456
        self.write()
        with self.assertRaises(ValueError):
            VALIDATOR.validate(self.manifest)

    def test_repair_preserves_tail(self):
        self.info["state"] = "running"
        good = json.dumps(self.row) + "\n"
        self.write(good + '{"partial":')
        self.assertEqual(VALIDATOR.repair_tails(self.manifest), 1)
        file = self.root / self.info["files"][0]
        self.assertEqual(file.read_text(), good)
        self.assertEqual(file.with_name(file.name + ".partial-tail.bak").read_text(), '{"partial":')
        self.assertEqual(json.loads(self.manifest.read_text())["state"], "running")

    def test_unfinished_capture(self):
        self.info["state"] = "running"
        self.write()
        with self.assertRaises(ValueError):
            VALIDATOR.validate(self.manifest)


if __name__ == "__main__":
    unittest.main()
