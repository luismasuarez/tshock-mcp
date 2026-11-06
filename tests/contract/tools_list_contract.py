#!/usr/bin/env python3
"""Capture and verify the MCP ``tools/list`` catalog contract.

The snapshot freezes what a client receives over stdio: every tool's name,
title, description, input schema and annotations, plus the catalog order. It
deliberately excludes the response envelope (``ttlMs``, ``cacheScope``,
``resultType``, ``_meta`` and ``serverInfo``) so unrelated protocol features do
not turn this contract test into a false failure.

The catalog is captured from the real Release binary over JSON-RPC, never from
the documentation or from an in-process SDK object.
"""
from __future__ import annotations

import argparse
import copy
import difflib
import json
import os
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any, Callable

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DLL = (
    REPO_ROOT / "src" / "TShock.MCP.Server" / "bin" / "Release" / "net8.0" / "tshock-mcp.dll"
)
DEFAULT_SNAPSHOT = Path(__file__).resolve().parent / "tools-list.snapshot.json"

FORMAT_VERSION = 1
PROTOCOLS = ("2025-06-18", "2025-11-25", "2026-07-28")
LEGACY_PROTOCOLS = frozenset({"2025-06-18", "2025-11-25"})
HINT_KEYS = ("readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint")
TOOL_KEYS = ("name", "title", "description", "inputSchema", "annotations")
READ_TIMEOUT_SECONDS = 20.0


class ContractError(RuntimeError):
    """The catalog could not be captured, parsed or validated."""


def _isolated_env(allow_write: bool) -> dict[str, str]:
    """Returns an environment that cannot change the catalog by accident."""
    env = dict(os.environ)
    for name in (
        "TSHOCK_BRIDGE_URL",
        "TSHOCK_BRIDGE_TOKEN",
        "TSHOCK_BRIDGE_TOKEN_FILE",
        "TSHOCK_BRIDGE_TIMEOUT_SECONDS",
        "TSHOCK_MCP_AUDIT_LOG",
        "TSHOCK_MCP_SHOW_IPS",
        "TSHOCK_MCP_ALLOW_INSECURE",
    ):
        env.pop(name, None)
    env.update(
        {
            # tools/list never calls TShock, so a closed loopback port is enough.
            "TSHOCK_REST_URL": "http://127.0.0.1:9",
            "TSHOCK_TOKEN": "contract-capture-token",
            "TSHOCK_MCP_ALLOW_WRITE": "true" if allow_write else "false",
            "TSHOCK_MCP_TIMEOUT_SECONDS": "5",
        }
    )
    return env


def _pump(stream: Any, target: "queue.Queue[str | None]") -> None:
    try:
        for line in stream:
            target.put(line)
    finally:
        target.put(None)


def _drain(stream: Any, lines: list[str]) -> None:
    for line in stream:
        lines.append(line)


def _read_message(messages: "queue.Queue[str | None]") -> dict[str, Any]:
    try:
        line = messages.get(timeout=READ_TIMEOUT_SECONDS)
    except queue.Empty as error:
        raise ContractError("timed out waiting for the MCP server") from error
    if line is None:
        raise ContractError("the MCP server closed stdout")
    try:
        message = json.loads(line)
    except json.JSONDecodeError as error:
        raise ContractError(f"invalid JSON-RPC line: {line!r}") from error
    if not isinstance(message, dict):
        raise ContractError(f"JSON-RPC message is not an object: {line!r}")
    return message


def _request(
    send: Callable[[dict[str, Any]], None],
    messages: "queue.Queue[str | None]",
    request_id: int,
    method: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
    while True:
        message = _read_message(messages)
        if message.get("id") != request_id:
            continue
        if "error" in message:
            raise ContractError(f"{method} failed: {message['error']!r}")
        result = message.get("result")
        if not isinstance(result, dict):
            raise ContractError(f"{method} returned no result object")
        return result


def _list_all(
    send: Callable[[dict[str, Any]], None],
    messages: "queue.Queue[str | None]",
    first_request_id: int,
    meta: dict[str, Any] | None,
) -> list[dict[str, Any]]:
    tools: list[dict[str, Any]] = []
    cursor: str | None = None
    request_id = first_request_id

    while True:
        params: dict[str, Any] = {}
        if cursor is not None:
            params["cursor"] = cursor
        if meta is not None:
            params["_meta"] = meta

        result = _request(send, messages, request_id, "tools/list", params)
        page = result.get("tools")
        if not isinstance(page, list):
            raise ContractError("tools/list returned no tools array")
        tools.extend(page)

        cursor = result.get("nextCursor")
        if not cursor:
            return tools
        if not isinstance(cursor, str):
            raise ContractError(f"tools/list returned an invalid nextCursor: {cursor!r}")
        request_id += 1


def capture_catalog(
    protocol: str,
    dll: Path = DEFAULT_DLL,
    allow_write: bool = False,
) -> list[dict[str, Any]]:
    """Launches the real binary and returns the catalog for one protocol era."""
    if protocol not in PROTOCOLS:
        raise ContractError(f"unsupported protocol version: {protocol}")
    if not dll.exists():
        raise ContractError(f"binary not found: {dll}\nRun: dotnet build -c Release")

    proc = subprocess.Popen(
        ["dotnet", str(dll)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=_isolated_env(allow_write),
        text=True,
        bufsize=1,
    )
    messages: "queue.Queue[str | None]" = queue.Queue()
    stderr_lines: list[str] = []
    threading.Thread(target=_pump, args=(proc.stdout, messages), daemon=True).start()
    threading.Thread(target=_drain, args=(proc.stderr, stderr_lines), daemon=True).start()

    def send(message: dict[str, Any]) -> None:
        if proc.stdin is None:
            raise ContractError("the MCP server stdin is closed")
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    try:
        if protocol in LEGACY_PROTOCOLS:
            _request(
                send,
                messages,
                1,
                "initialize",
                {
                    "protocolVersion": protocol,
                    "capabilities": {},
                    "clientInfo": {"name": "contract-capture", "version": "0.1"},
                },
            )
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            return _list_all(send, messages, 2, meta=None)

        meta = {
            "io.modelcontextprotocol/protocolVersion": protocol,
            "io.modelcontextprotocol/clientInfo": {
                "name": "contract-capture",
                "version": "0.1",
            },
            "io.modelcontextprotocol/clientCapabilities": {},
        }
        _request(send, messages, 1, "server/discover", {"_meta": meta})
        return _list_all(send, messages, 2, meta=meta)
    except (ContractError, OSError) as error:
        detail = "".join(stderr_lines[-20:]).strip()
        if detail:
            raise ContractError(f"{error}\nserver stderr:\n{detail}") from error
        raise ContractError(str(error)) from error
    finally:
        if proc.stdin is not None:
            proc.stdin.close()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


def validate_catalog(tools: Any) -> list[str]:
    """Returns structural problems that would make a snapshot meaningless."""
    if not isinstance(tools, list):
        return ["catalog is not a list"]
    if not tools:
        return ["catalog is empty"]

    errors: list[str] = []
    seen: set[str] = set()

    for index, tool in enumerate(tools):
        where = f"tools[{index}]"
        if not isinstance(tool, dict):
            errors.append(f"{where}: not an object")
            continue

        missing = [key for key in TOOL_KEYS if key not in tool]
        if missing:
            errors.append(f"{where}: missing {missing}")

        name = tool.get("name")
        if not isinstance(name, str) or not name.strip():
            errors.append(f"{where}.name: must be a non-empty string")
        elif name in seen:
            errors.append(f"{where}.name: duplicate {name!r}")
        else:
            seen.add(name)

        title = tool.get("title")
        if not isinstance(title, str) or not title.strip():
            errors.append(f"{where}.title: must be a non-empty string")
        elif name == title:
            errors.append(f"{where}.title: must differ from the tool name")

        description = tool.get("description")
        if not isinstance(description, str) or not description.strip():
            errors.append(f"{where}.description: must be a non-empty string")

        schema = tool.get("inputSchema")
        if not isinstance(schema, dict):
            errors.append(f"{where}.inputSchema: must be an object")
        else:
            if schema.get("type") != "object":
                errors.append(f"{where}.inputSchema.type: must be 'object'")
            properties = schema.get("properties")
            if not isinstance(properties, dict):
                errors.append(f"{where}.inputSchema.properties: must be an object")
            else:
                for property_name, property_schema in properties.items():
                    child = f"{where}.inputSchema.properties.{property_name}"
                    if not isinstance(property_schema, dict):
                        errors.append(f"{child}: must be an object")
                    elif not any(
                        key in property_schema
                        for key in ("type", "$ref", "anyOf", "oneOf", "allOf", "enum", "const")
                    ):
                        errors.append(f"{child}: has no type, reference or enum")
            required = schema.get("required")
            if required is not None:
                if not isinstance(required, list) or not all(
                    isinstance(item, str) for item in required
                ):
                    errors.append(f"{where}.inputSchema.required: must be a string array")
                elif isinstance(properties, dict):
                    unknown = [item for item in required if item not in properties]
                    if unknown:
                        errors.append(
                            f"{where}.inputSchema.required: unknown properties {unknown}"
                        )

        annotations = tool.get("annotations")
        if not isinstance(annotations, dict):
            errors.append(f"{where}.annotations: must be an object")
        else:
            for hint in HINT_KEYS:
                if not isinstance(annotations.get(hint), bool):
                    errors.append(f"{where}.annotations.{hint}: must be a boolean")
            if annotations.get("title") != title:
                errors.append(f"{where}.annotations.title: must match the tool title")

    return errors


def normalize(tools: list[dict[str, Any]], *, validate: bool = True) -> dict[str, Any]:
    """Returns the stable, reviewable representation stored in the snapshot."""
    if validate:
        errors = validate_catalog(tools)
        if errors:
            raise ContractError("invalid tools/list catalog:\n" + "\n".join(errors))

    return {
        "formatVersion": FORMAT_VERSION,
        "order": [tool["name"] for tool in tools],
        "tools": {tool["name"]: tool for tool in tools},
    }


def load_snapshot(path: Path = DEFAULT_SNAPSHOT) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise ContractError(f"snapshot not found: {path}") from error
    except json.JSONDecodeError as error:
        raise ContractError(f"snapshot is not valid JSON: {path}: {error}") from error

    if not isinstance(data, dict):
        raise ContractError("snapshot must be a JSON object")
    if data.get("formatVersion") != FORMAT_VERSION:
        raise ContractError(f"unsupported snapshot formatVersion: {data.get('formatVersion')!r}")

    order = data.get("order")
    tools = data.get("tools")
    if not isinstance(order, list) or not all(isinstance(name, str) for name in order):
        raise ContractError("snapshot.order must be a string array")
    if not isinstance(tools, dict):
        raise ContractError("snapshot.tools must be an object")
    if len(order) != len(set(order)):
        raise ContractError("snapshot.order contains duplicate names")
    if set(order) != set(tools):
        raise ContractError("snapshot.order and snapshot.tools disagree")

    for name, tool in tools.items():
        if not isinstance(tool, dict) or tool.get("name") != name:
            raise ContractError(f"snapshot.tools.{name}: name does not match its key")

    errors = validate_catalog([tools[name] for name in order])
    if errors:
        raise ContractError("snapshot is structurally invalid:\n" + "\n".join(errors))

    return data


def _short(value: Any, limit: int = 160) -> str:
    text = json.dumps(value, ensure_ascii=False, sort_keys=True)
    return text if len(text) <= limit else text[: limit - 3] + "..."


def _diff_values(expected: Any, actual: Any, path: str, out: list[str]) -> None:
    if isinstance(expected, dict) and isinstance(actual, dict):
        for key in sorted(set(expected) | set(actual)):
            child = f"{path}.{key}"
            if key not in actual:
                out.append(f"{child}: removed")
            elif key not in expected:
                out.append(f"{child}: added")
            else:
                _diff_values(expected[key], actual[key], child, out)
        return

    if expected != actual:
        out.append(f"{path}: {_short(expected)} -> {_short(actual)}")


def compare_snapshot(snapshot: dict[str, Any], tools: list[dict[str, Any]]) -> list[str]:
    """Returns field-level differences; an empty list means the contract holds."""
    errors = validate_catalog(tools)
    if errors:
        return ["actual catalog is structurally invalid:"] + errors

    expected_order: list[str] = snapshot["order"]
    expected_tools: dict[str, dict[str, Any]] = snapshot["tools"]
    actual_order = [tool["name"] for tool in tools]
    actual_tools = {tool["name"]: tool for tool in tools}

    problems: list[str] = []

    if expected_order != actual_order:
        missing = [name for name in expected_order if name not in actual_tools]
        added = [name for name in actual_order if name not in expected_tools]
        if missing:
            problems.append(f"order: missing {missing}")
        if added:
            problems.append(f"order: added {added}")
        if not missing and not added:
            for index, (expected, actual) in enumerate(zip(expected_order, actual_order)):
                if expected != actual:
                    problems.append(
                        f"order[{index}]: expected {expected!r}, got {actual!r}"
                    )
                    break
            problems.append(f"order: {expected_order} -> {actual_order}")

    for name in expected_tools:
        if name not in actual_tools:
            problems.append(f"tools.{name}: removed")
    for name in actual_tools:
        if name not in expected_tools:
            problems.append(f"tools.{name}: added")

    for name in sorted(set(expected_tools) & set(actual_tools)):
        _diff_values(expected_tools[name], actual_tools[name], f"tools.{name}", problems)

    return problems


def _pretty(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def format_diff(snapshot: dict[str, Any], tools: list[dict[str, Any]]) -> str:
    """Returns a unified diff ready to print in a failure message."""
    try:
        actual = normalize(tools, validate=False)
    except (KeyError, TypeError, AttributeError) as error:
        return f"<cannot render the diff: {error}>"
    return "\n".join(
        difflib.unified_diff(
            _pretty(snapshot).splitlines(),
            _pretty(actual).splitlines(),
            fromfile="tools-list.snapshot.json",
            tofile="actual tools/list",
            lineterm="",
        )
    )


def write_snapshot(path: Path, tools: list[dict[str, Any]]) -> dict[str, Any]:
    data = normalize(tools)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(_pretty(data))
    return data


def _sample_catalog() -> list[dict[str, Any]]:
    return [
        {
            "name": "alpha",
            "title": "Get alpha",
            "description": "Returns alpha.",
            "inputSchema": {
                "type": "object",
                "properties": {"name": {"type": "string", "maxLength": 8}},
                "required": ["name"],
            },
            "annotations": {
                "title": "Get alpha",
                "readOnlyHint": True,
                "destructiveHint": False,
                "idempotentHint": True,
                "openWorldHint": False,
            },
        },
        {
            "name": "beta",
            "title": "Send beta",
            "description": "Sends beta.",
            "inputSchema": {"type": "object", "properties": {}},
            "annotations": {
                "title": "Send beta",
                "readOnlyHint": False,
                "destructiveHint": False,
                "idempotentHint": False,
                "openWorldHint": False,
            },
        },
    ]


def _add_valid_tool(tools: list[dict[str, Any]]) -> None:
    extra = copy.deepcopy(tools[0])
    extra["name"] = "gamma"
    extra["title"] = "Get gamma"
    extra["annotations"]["title"] = "Get gamma"
    tools.append(extra)


def self_test() -> int:
    """Proves the comparator fails on every change class. Returns the case count."""
    sample = _sample_catalog()
    snapshot = normalize(sample)

    if compare_snapshot(snapshot, copy.deepcopy(sample)):
        raise AssertionError("the baseline catalog must match its own snapshot")

    mutations: dict[str, tuple[Callable[[list[dict[str, Any]]], None], str]] = {
        "name": (
            lambda tools: tools[0].__setitem__("name", "gamma"),
            "tools.gamma: added",
        ),
        "title": (lambda tools: tools[0].__setitem__("title", "Get gamma"), ".title"),
        "description": (
            lambda tools: tools[0].__setitem__("description", "Changed."),
            ".description",
        ),
        "schema": (
            lambda tools: tools[0]["inputSchema"]["properties"]["name"].__setitem__(
                "type", "integer"
            ),
            ".inputSchema.properties.name.type",
        ),
        "annotation": (
            lambda tools: tools[0]["annotations"].__setitem__("readOnlyHint", False),
            ".annotations.readOnlyHint",
        ),
        "order": (lambda tools: tools.reverse(), "order["),
        "added": (_add_valid_tool, "tools.gamma: added"),
        "removed": (lambda tools: tools.pop(), "tools.beta: removed"),
    }

    for label, (mutate, expected_path) in mutations.items():
        changed = copy.deepcopy(sample)
        mutate(changed)
        problems = compare_snapshot(snapshot, changed)
        if not problems:
            raise AssertionError(f"the comparator missed the '{label}' mutation")
        if not any(expected_path in problem for problem in problems):
            raise AssertionError(
                f"the '{label}' mutation was not reported at {expected_path}: {problems}"
            )
        if not format_diff(snapshot, changed):
            raise AssertionError(f"the '{label}' mutation produced no diff")

    malformed = copy.deepcopy(sample)
    malformed[1]["name"] = "alpha"
    if not validate_catalog(malformed):
        raise AssertionError("duplicate names must be rejected")

    malformed = copy.deepcopy(sample)
    malformed[0]["inputSchema"]["required"] = ["missing"]
    if not validate_catalog(malformed):
        raise AssertionError("required properties outside properties must be rejected")

    malformed = copy.deepcopy(sample)
    del malformed[0]["annotations"]["readOnlyHint"]
    if not validate_catalog(malformed):
        raise AssertionError("a missing annotation hint must be rejected")

    return len(mutations)


def _capture_all(dll: Path) -> dict[str, list[dict[str, Any]]]:
    return {protocol: capture_catalog(protocol, dll=dll) for protocol in PROTOCOLS}


def _run(args: argparse.Namespace) -> int:
    if args.self_test:
        cases = self_test()
        print(f"contract self-test OK: {cases} mutation classes detected")
        return 0

    if args.write:
        if os.environ.get("CI") or os.environ.get("GITHUB_ACTIONS"):
            print(
                "refusing --write in CI: review and update the snapshot locally",
                file=sys.stderr,
            )
            return 2

        catalogs = _capture_all(args.dll)
        baseline = normalize(catalogs[PROTOCOLS[0]])
        for protocol, tools in catalogs.items():
            problems = compare_snapshot(baseline, tools)
            if problems:
                print(f"{protocol} differs from {PROTOCOLS[0]}:", file=sys.stderr)
                for problem in problems:
                    print(f"  {problem}", file=sys.stderr)
                return 1

        data = write_snapshot(args.snapshot, catalogs[PROTOCOLS[0]])
        print(
            f"wrote {args.snapshot} "
            f"({len(data['order'])} tools, {len(PROTOCOLS)} protocol eras)"
        )
        return 0

    snapshot = load_snapshot(args.snapshot)
    failures: list[tuple[str, list[dict[str, Any]], list[str]]] = []
    for protocol in PROTOCOLS:
        tools = capture_catalog(protocol, dll=args.dll)
        problems = compare_snapshot(snapshot, tools)
        if problems:
            failures.append((protocol, tools, problems))

    if failures:
        for protocol, _, problems in failures:
            print(f"{protocol}: {len(problems)} contract difference(s)")
            for problem in problems[:40]:
                print(f"  {problem}")
        if len(failures) > 1:
            print(f"(the snapshot was compared against {len(failures)} protocol eras)")
        print(format_diff(snapshot, failures[0][1]))
        return 1

    print(
        f"tools/list contract OK: {len(snapshot['order'])} tools "
        f"across {len(PROTOCOLS)} protocol eras"
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--write",
        action="store_true",
        help="regenerate the snapshot from the binary (blocked in CI)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify the snapshot against the binary (the default mode)",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="prove the comparator detects every mutation class",
    )
    parser.add_argument("--dll", type=Path, default=DEFAULT_DLL)
    parser.add_argument("--snapshot", type=Path, default=DEFAULT_SNAPSHOT)
    args = parser.parse_args(argv)

    try:
        return _run(args)
    except ContractError as error:
        print(f"contract error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
