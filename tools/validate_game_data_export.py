#!/usr/bin/env python3
"""验证已结束的全量消息导出：JSONL、编号、玩家会话绑定与 manifest 计数。

用法：python tools/validate_game_data_export.py --manifest <manifest.json>
仅使用标准库；流式读文件，事件编号使用临时 SQLite 表去重，不把所有消息载入内存。
"""
import argparse
import base64
import collections
import os
import shutil
import datetime
import json
import pathlib
import sqlite3
import tempfile


def validate(path):
    path = pathlib.Path(path).resolve()
    root = path.parents[2]
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest["state"] != "completed":
        raise ValueError(f"捕获未完整结束：{manifest['state']}；{manifest.get('error')}")
    bindings = manifest["sessionBindings"]
    totals = collections.Counter()
    players = collections.defaultdict(collections.Counter)
    with tempfile.TemporaryDirectory(prefix="haiyu-event-validation-") as temp:
        connection = sqlite3.connect(str(pathlib.Path(temp) / "indices.sqlite"))
        try:
            connection.execute("create table events (id integer primary key)")
            files = manifest["files"]
            actual_bytes = 0
            if len(set(files)) != len(files):
                raise ValueError("分片文件重复")
            for name in files:
                file = (root / name).resolve()
                if not file.is_relative_to(root):
                    raise ValueError("分片路径越界")
                actual_bytes += file.stat().st_size
                with file.open("r", encoding="utf-8") as lines:
                    for line_number, line in enumerate(lines, 1):
                        try:
                            if not line.endswith("\n"):
                                raise ValueError("存在未完整落盘的尾记录")
                            event = json.loads(line)
                            if event["captureId"] != manifest["captureId"] or event["schemaVersion"] != manifest["schemaVersion"]:
                                raise ValueError("捕获或协议基线不一致")
                            index = event["eventIndex"]
                            if not isinstance(index, int) or index <= 0:
                                raise ValueError("事件编号非法")
                            connection.execute("insert into events values (?)", (index,))
                            when = datetime.datetime.fromisoformat(event["receivedAt"].replace("Z", "+00:00"))
                            if when.tzinfo is None or event["direction"] != "server_to_client":
                                raise ValueError("缺少时区或方向错误")
                            player = event["playerId"]
                            if player is None:
                                player = bindings.get(event["sessionId"])
                            elif player <= 0 or (not name.startswith("captures/") and pathlib.PurePosixPath(name).parts[0] != str(player)):
                                raise ValueError("玩家目录归属错误")
                            if player is not None and event["sessionId"] in bindings and bindings[event["sessionId"]] != player:
                                raise ValueError("玩家与登录会话绑定冲突")
                            status = event["decodeStatus"]
                            category = "decoded" if status.startswith("decoded") else "unknown" if status == "unknown_message" else "failed"
                            totals["written"] += 1
                            totals[category] += 1
                            if player is None:
                                totals["unresolved"] += 1
                            else:
                                players[player]["written"] += 1
                                players[player][category] += 1
                            if not status.startswith("decoded") or event["hasUnknownFields"]:
                                base64.b64decode(event["rawPayload"], validate=True)
                            elif event["payload"] is None:
                                raise ValueError("解析成功但缺少消息内容")
                        except (ValueError, KeyError, TypeError, sqlite3.IntegrityError) as error:
                            raise ValueError(f"{name}:{line_number}: {error}") from error
            for key in ("written", "decoded", "unknown", "failed", "unresolved"):
                if manifest[key] != totals[key]:
                    raise ValueError(f"计数不一致 {key}: manifest={manifest[key]}, records={totals[key]}")
            if manifest["received"] != totals["written"] or manifest["dropped"] != 0:
                raise ValueError("已接收事件存在未写入或丢弃")
            if "bytesWritten" in manifest and manifest["bytesWritten"] != actual_bytes:
                raise ValueError("文件字节总量与 manifest 不一致")
            rows = {p["playerId"]: p for p in manifest["players"]}
            if set(rows) != set(players):
                raise ValueError("玩家汇总列表不一致")
            for player, counts in players.items():
                for key in ("written", "decoded", "unknown", "failed"):
                    if rows[player][key] != counts[key]:
                        raise ValueError(f"玩家 {player} 的 {key} 汇总不一致")
            if totals["written"]:
                low, high, count = connection.execute("select min(id), max(id), count(*) from events").fetchone()
                if low != 1 or high != count:
                    raise ValueError("事件编号存在缺口")
            return dict(totals), len(players), len(files)
        finally:
            connection.close()


def repair_tails(path):
    """只修复未结束捕获的尾行；备份被移除的字节，绝不把 manifest 改为 completed。"""
    path = pathlib.Path(path).resolve()
    root = path.parents[2]
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest["state"] == "completed":
        raise ValueError("completed 捕获不自动修复；请保留损坏样本排查")
    repaired = 0
    for name in manifest["files"]:
        file = (root / name).resolve()
        if not file.is_relative_to(root):
            raise ValueError("分片路径越界")
        with file.open("r+b") as stream:
            end = stream.seek(0, 2)
            if end == 0:
                continue
            stream.seek(end - 1)
            if stream.read(1) == b"\n":
                continue
            cursor, cut = end, 0
            while cursor:
                count = min(cursor, 65536)
                cursor -= count
                stream.seek(cursor)
                chunk = stream.read(count)
                found = chunk.rfind(b"\n")
                if found >= 0:
                    cut = cursor + found + 1
                    break
            backup = file.with_name(file.name + ".partial-tail.bak")
            with backup.open("xb") as output:
                stream.seek(cut)
                shutil.copyfileobj(stream, output)
                output.flush()
                os.fsync(output.fileno())
            stream.truncate(cut)
            stream.flush()
            os.fsync(stream.fileno())
            repaired += 1
    return repaired


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--repair-tail", action="store_true", help="备份并移除未结束捕获的半条尾记录，不修改 manifest")
    args = parser.parse_args()
    try:
        if args.repair_tail:
            print(f"Repaired tails: {repair_tails(args.manifest)}; manifest remains incomplete")
        totals, players, files = validate(args.manifest)
        print(f"PASS players={players}, shards={files}, counts={totals}")
        return 0
    except (ValueError, KeyError, OSError) as error:
        print(f"FAIL {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
