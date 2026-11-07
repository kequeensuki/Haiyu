#!/usr/bin/env python3
"""Read-only structural audit of Haiyu export directories/ZIPs (Python 3.10+).

Does not extract archives or replay business mutations. Payloads, mail bodies and
session keys are never emitted. Exit 0 means audit completed, not feature healthy;
--strict exits 2 when findings include an error. Account/session IDs are aliased
unless --include-identifiers is explicitly requested.
"""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import sys
import zipfile


@contextmanager
def input_files(path: Path, maximum: int):
    if path.is_file():
        if not zipfile.is_zipfile(path):
            raise ValueError("Input file must be a ZIP, or provide an export directory.")
        with zipfile.ZipFile(path) as archive:
            files = [item for item in archive.infolist() if not item.is_dir() and PurePosixPath(item.filename).suffix.lower() in {".json", ".jsonl"}]
            if sum(item.file_size for item in files) > maximum:
                raise ValueError("Uncompressed JSON data exceeds --max-bytes; no extraction was attempted.")
            yield [(item.filename, lambda item=item: archive.open(item)) for item in files]
    elif path.is_dir():
        files = sorted(p for p in path.rglob("*") if p.is_file() and p.suffix.lower() in {".json", ".jsonl"})
        if any(not p.resolve().is_relative_to(path) for p in files):
            raise ValueError("Input contains a file resolving outside the supplied directory.")
        if sum(p.stat().st_size for p in files) > maximum:
            raise ValueError("JSON data exceeds --max-bytes.")
        yield [(p.relative_to(path).as_posix(), lambda p=p: p.open("rb")) for p in files]
    else:
        raise ValueError("Input path does not exist.")


def audit(files, include_identifiers=False, max_line_bytes=16 * 1024 * 1024, max_records=1_000_000):
    report = {"auditCompleted": False, "findings": [], "indexes": [], "manifests": [],
              "snapshots": [], "files": [], "captures": [], "repeatedFields": [], "initializationSequences": []}
    players, sessions = {}, {}

    def alias(value, mapping, prefix):
        if value is None:
            return None
        key = str(value)
        if key not in mapping:
            mapping[key] = key if include_identifiers else f"{prefix}-{len(mapping) + 1}"
        return mapping[key]

    def player(value):
        return alias(value, players, "player")

    def session(value):
        return alias(value, sessions, "session")

    def finding(severity, kind, **details):
        report["findings"].append({"severity": severity, "kind": kind, **details})

    raw, filtered, stats, bindings, owned, pending = {}, [], {}, {}, {}, {}
    total_records = 0

    def capture_stats(capture):
        if capture not in stats:
            stats[capture] = {"captureId": capture, "rawRecords": 0, "filteredRecords": 0,
                              "decodeStatuses": Counter(), "playerRecords": Counter(), "messageTypes": Counter(),
                              "indexes": set(), "indexedRecords": 0, "duplicateIndexes": 0}
        return stats[capture]

    def repeated(node, context, path="payload"):
        if isinstance(node, dict):
            for key, value in node.items():
                if key in {"baseProp", "addProp", "skills"} and isinstance(value, list):
                    groups = {}
                    for item in value:
                        if isinstance(item, dict):
                            groups.setdefault(item.get("key", 0), []).append(item.get("value", 0))
                    duplicates = [key for key, values in groups.items() if len(values) > 1]
                    if duplicates and len(report["repeatedFields"]) < 1000:
                        report["repeatedFields"].append({**context, "path": f"{path}.{key}", "duplicateKeys": duplicates,
                            "conflictingKeyCount": sum(len(set(map(str, groups[key]))) > 1 for key in duplicates)})
                repeated(value, context, f"{path}.{key}")
        elif isinstance(node, list):
            for index, value in enumerate(node):
                repeated(value, context, f"{path}[{index}]")

    for name, opener in sorted(files, key=lambda entry: entry[0]):
        parts = PurePosixPath(name.replace("\\", "/")).parts
        base = parts[-1]
        is_module = "modules" in parts
        if base.endswith(".json"):
            with opener() as stream:
                data = json.load(stream)
            if not isinstance(data, dict):
                continue
            if base == "capture-index.json":
                modules = []
                for module in data.get("modules", []):
                    sources = [{"sourceId": s.get("sourceId"), "state": s.get("state"), "required": s.get("required"),
                                "player": player(s.get("playerId")), "session": session(s.get("sessionId")),
                                "localVersion": s.get("localVersion"), "error": s.get("error")} for s in module.get("sources", [])]
                    modules.append({"moduleId": module.get("moduleId"), "state": module.get("state"), "error": module.get("error"),
                                    "counters": [{"name": c.get("name"), "value": player(c.get("value")) if c.get("name") == "currentPlayer" and c.get("value") else c.get("value"), "player": player(c.get("playerId"))}
                                                 for c in module.get("counters", [])], "sources": sources,
                                    "artifactCount": len(module.get("artifacts", []))})
                    if module.get("state") in {"Failed", "Incomplete"}:
                        finding("error" if module.get("state") == "Failed" else "warning", "module-not-completed",
                                captureId=data.get("captureId"), moduleId=module.get("moduleId"), state=module.get("state"))
                report["indexes"].append({"captureId": data.get("captureId"), "state": data.get("state"),
                                           "startedAt": data.get("startedAt"), "endedAt": data.get("endedAt"), "modules": modules})
            elif base == "manifest.json":
                capture = data.get("captureId", "unknown")
                for key, value in data.get("sessionBindings", {}).items():
                    bindings[(capture, key)] = value
                    player(value)
                    session(key)
                counters = {key: data.get(key) for key in ["received", "written", "decoded", "unknown", "failed", "dropped", "unresolved"]}
                report["manifests"].append({"captureId": capture, "moduleId": data.get("moduleId"), "state": data.get("state"),
                    "protocolSourceCommit": data.get("sourceCommit"), "counters": counters,
                    "sessionBindings": {session(k): player(v) for k, v in data.get("sessionBindings", {}).items()}})
                for key in ["failed", "dropped", "unknown"]:
                    if isinstance(counters[key], (int, float)) and counters[key] > 0:
                        finding("warning" if key == "unknown" else "error", "manifest-counter", captureId=capture, moduleId=data.get("moduleId"), counter=key, value=counters[key])
            elif base.startswith("game-data"):
                account = data.get("playerId") or next((p for p in parts[:-1] if p.isdecimal()), None)
                sections = {}
                for key in ["mails", "roles", "achievements"]:
                    value = data.get(key)
                    if isinstance(value, dict):
                        sections[key] = len(value["items"]) if isinstance(value.get("items"), list) else value.get("count")
                report["snapshots"].append({"file": base, "player": player(account), "exportedAt": data.get("exportedAt"),
                                              "latestName": base == "game-data.json", "sectionCounts": sections})
            continue
        if not base.endswith(".jsonl"):
            continue
        rows = 0
        with opener() as stream:
            while True:
                line = stream.readline(max_line_bytes + 1)
                if not line:
                    break
                if len(line) > max_line_bytes:
                    raise ValueError("A JSONL record exceeds --max-line-bytes; increase the explicit bound if appropriate.")
                if not line.strip():
                    continue
                total_records += 1
                if total_records > max_records:
                    raise ValueError("Too many records; review --max-records before increasing it.")
                row = json.loads(line)
                if not isinstance(row, dict) or "payload" not in row or (row["payload"] is not None and not isinstance(row["payload"], dict)):
                    raise ValueError("Expected Haiyu event objects with an object/null payload.")
                rows += 1
                capture = row.get("captureId", "unknown")
                state = capture_stats(capture)
                index = row.get("captureEventIndex")
                account, sid = row.get("playerId"), row.get("sessionId")
                player(account)
                session(sid)
                message = str(row.get("messageType", "unknown")).split(".")[-1]
                digest = hashlib.sha256(json.dumps({key: row.get(key) for key in
                    ["payload", "rawPayload", "decodeStatus", "frameType", "errorCode"]},
                    sort_keys=True, separators=(",", ":")).encode()).hexdigest()
                signature = (row.get("msgId"), sid, account, digest)
                key = (capture, index)
                if is_module:
                    state["filteredRecords"] += 1
                    filtered.append((key, signature))
                    continue
                state["rawRecords"] += 1
                state["decodeStatuses"][str(row.get("decodeStatus"))] += 1
                state["playerRecords"][(sid, account)] += 1
                state["messageTypes"][message] += 1
                if isinstance(index, int) and index > 0:
                    state["indexedRecords"] += 1
                    if key in raw:
                        state["duplicateIndexes"] += 1
                    state["indexes"].add(index)
                    raw[key] = signature
                else:
                    finding("warning", "missing-capture-event-index", captureId=capture)
                context = {"captureId": capture, "index": index, "messageType": message}
                repeated(row["payload"], context)
                # Store compact observations; actual order is handled below, independently of ZIP ordering.
                payload = row["payload"] or {}
                if message in {"PbGetRoleListNotify", "PbGetRoleListResponse", "PbRoleActiveNotify", "RoleChangeNotify",
                               "PbRolePropsNotify", "PbRoleSkillLevelNotify", "RoleSkillNodeNotify"}:
                    state.setdefault("roleEvents", []).append((index, sid, message, {
                        "ids": [r.get("roleId") for r in payload.get("roleList", [])],
                        "roleId": payload.get("roleId"), "activeId": payload.get("role", {}).get("roleId"),
                        "sourceId": payload.get("sourceRoleId"), "changedId": payload.get("roleInfo", {}).get("roleId"),
                        "code": payload.get("code", 0)}))
        report["files"].append({"file": base, "kind": "filtered" if is_module else "unresolved" if base.startswith("unresolved-") else "player-events", "records": rows})

    for capture, state in stats.items():
        resolved = Counter()
        for (sid, account), count in state.pop("playerRecords").items():
            resolved[player(account if account is not None else bindings.get((capture, sid))) or "unresolved"] += count
        state["playerRecordsAfterBindings"] = dict(resolved)
        indexes = state.pop("indexes")
        state["indexRange"] = [min(indexes), max(indexes)] if indexes else None
        state["missingWithinObservedRange"] = max(indexes) - min(indexes) + 1 - len(indexes) if indexes else None
        if state["duplicateIndexes"] or state["missingWithinObservedRange"]:
            finding("warning", "event-index-discontinuity", captureId=capture)
        for index, sid, message, detail in sorted(state.pop("roleEvents", []), key=lambda e: e[0] if isinstance(e[0], int) else -1):
            session_key = (capture, sid)
            if message in {"PbGetRoleListNotify", "PbGetRoleListResponse"}:
                if detail["code"] not in {0, "Success"}:
                    continue
                ids = {value for value in detail["ids"] if value is not None}
                if message == "PbGetRoleListResponse" or session_key not in owned:
                    owned[session_key] = ids
                else:
                    owned[session_key].update(ids)
                for entity in ids:
                    pending.pop((capture, sid, entity), None)
            elif message == "PbRoleActiveNotify":
                entity = detail["activeId"]
                prior = pending.pop((capture, sid, entity), None)
                if prior:
                    report["initializationSequences"].append({**prior, "activeIndex": index})
                owned.setdefault(session_key, set()).add(entity)
            elif message == "RoleChangeNotify":
                owned.setdefault(session_key, set()).discard(detail["sourceId"])
                owned[session_key].add(detail["changedId"])
                pending.pop((capture, sid, detail["sourceId"]), None)
                pending.pop((capture, sid, detail["changedId"]), None)
            elif session_key in owned and detail["roleId"] not in owned[session_key]:
                key = (capture, sid, detail["roleId"])
                item = pending.setdefault(key, {"captureId": capture, "session": session(sid), "roleId": detail["roleId"], "firstIndex": index, "messageCounts": {}})
                item["messageCounts"][message] = item["messageCounts"].get(message, 0) + 1
        state["decodeStatuses"] = dict(state["decodeStatuses"])
        for status, count in state["decodeStatuses"].items():
            if not status.startswith("decoded"):
                finding("error", "raw-decode-status", captureId=capture, status=status, count=count)
            elif status != "decoded":
                finding("warning", "raw-decode-status", captureId=capture, status=status, count=count)
        state["messageTypes"] = dict(state["messageTypes"])
        report["captures"].append(state)
    for manifest in report["manifests"]:
        if manifest["moduleId"] != "server-messages":
            continue
        expected = manifest["counters"].get("written")
        actual = stats.get(manifest["captureId"], {}).get("rawRecords", 0)
        if isinstance(expected, int) and actual != expected:
            finding("warning", "manifest-written-count-mismatch", captureId=manifest["captureId"], expected=expected, provided=actual,
                    action="Check whether all declared files were supplied; this alone does not prove network loss.")
    report["initializationSequences"].extend({**item, "activeIndex": None} for item in pending.values())
    unmatched = sum(raw.get(key) != signature for key, signature in filtered)
    report["filteredRecordsChecked"] = len(filtered)
    report["filteredRecordsUnmatched"] = unmatched
    if unmatched:
        finding("error", "filtered-events-do-not-match-provided-raw-events", count=unmatched)
    if report["repeatedFields"]:
        finding("info", "repeated-key-value-fields-observed", samples=len(report["repeatedFields"]), action="Check client last-write/uniqueness semantics; repetition is not automatically invalid.")
    if report["initializationSequences"]:
        finding("info", "role-deltas-before-complete-entity-observed", count=len(report["initializationSequences"]), action="Check soft initialization and missing-activation protection.")
    report["auditCompleted"] = True
    # Replace identifiers embedded in diagnostic error strings as well as structured fields.
    if not include_identifiers:
        replacements = {**players, **sessions}
        pattern = re.compile(r"(?<![A-Za-z0-9])(?:" + "|".join(re.escape(key) for key in sorted(replacements, key=len, reverse=True)) + r")(?![A-Za-z0-9])") if replacements else None
        def redact(value):
            if isinstance(value, list):
                return [redact(item) for item in value]
            if isinstance(value, dict):
                return {key: pattern.sub(lambda match: replacements[match.group()], item)
                        if key in {"error", "file"} and isinstance(item, str) and pattern else redact(item)
                        for key, item in value.items()}
            return value
        report = redact(report)
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input")
    parser.add_argument("--output", help="New JSON report outside the input directory; existing files are not overwritten")
    parser.add_argument("--include-identifiers", action="store_true")
    parser.add_argument("--strict", action="store_true")
    parser.add_argument("--max-bytes", type=int, default=256 * 1024 * 1024)
    parser.add_argument("--max-line-bytes", type=int, default=16 * 1024 * 1024)
    parser.add_argument("--max-records", type=int, default=1_000_000)
    args = parser.parse_args(argv)
    try:
        path = Path(args.input).resolve()
        output = Path(args.output).resolve() if args.output else None
        if min(args.max_bytes, args.max_line_bytes, args.max_records) <= 0:
            raise ValueError("Input bounds must be positive.")
        if output and (output == path or (path.is_dir() and output.is_relative_to(path))):
            raise ValueError("Do not write reports over/inside the original capture input.")
        if output and output.exists():
            raise ValueError("Output already exists; choose a new report path.")
        with input_files(path, args.max_bytes) as files:
            report = audit(files, args.include_identifiers, args.max_line_bytes, args.max_records)
        text = json.dumps(report, ensure_ascii=False, indent=2)
        if output:
            output.parent.mkdir(parents=True, exist_ok=True)
            with output.open("x", encoding="utf-8") as stream:
                stream.write(text + "\n")
            print(f"AUDIT COMPLETED: {output}; findings={len(report['findings'])}")
        else:
            print(text)
        return 2 if args.strict and any(f["severity"] == "error" for f in report["findings"]) else 0
    except (OSError, ValueError, TypeError, KeyError, RuntimeError, zipfile.BadZipFile) as error:
        print(f"AUDIT FAILED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
