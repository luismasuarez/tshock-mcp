#!/usr/bin/env python3
"""End-to-end smoke test: MCP stdio server -> fake TShock REST API and bridge.

Build first, then run from anywhere:

    dotnet build -c Release
    python3 tests/e2e/mcp_stdio_smoke.py

The script starts a local fake TShock server and, later, a fake AgentBridge,
launches the real MCP binary over stdio, and asserts the full path: JSON-RPC
tool call -> HTTP request -> result envelope. It also proves that bridge tools
degrade to bridge_unavailable without affecting the REST tools. It uses only
the Python standard library.
"""
from __future__ import annotations

import json
import os
import queue
import re
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlparse

REPO_ROOT = Path(__file__).resolve().parents[2]
CONTRACT_DIR = Path(__file__).resolve().parent.parent / "contract"
if str(CONTRACT_DIR) not in sys.path:
    sys.path.insert(0, str(CONTRACT_DIR))

import tools_list_contract as contract  # noqa: E402  (path is set above)

DEFAULT_DLL = REPO_ROOT / "src" / "TShock.MCP.Server" / "bin" / "Release" / "net8.0" / "tshock-mcp.dll"
TOKEN = "secret-token"
BRIDGE_TOKEN = "bridge-secret-token"
# Never let the caller's environment change the assertions in this script.
ISOLATED_ENV = (
    "TSHOCK_MCP_SHOW_IPS", "TSHOCK_MCP_AUDIT_LOG", "TSHOCK_MCP_ALLOW_INSECURE",
    "TSHOCK_BRIDGE_URL", "TSHOCK_BRIDGE_TOKEN", "TSHOCK_BRIDGE_TOKEN_FILE",
    "TSHOCK_BRIDGE_TIMEOUT_SECONDS",
    "TSHOCK_MCP_RATE_LIMIT_PER_MINUTE", "TSHOCK_MCP_WRITE_RATE_LIMIT_PER_MINUTE",
)
seen: list[tuple[str, dict[str, list[str]]]] = []
bridge_seen: list[str] = []

REGIONS = [
    {
        "id": 1, "name": "spawn", "x": 100, "y": 100, "width": 40, "height": 20,
        "owner": "mcp-gateway", "disableBuild": False, "z": 0,
        "allowedIds": [], "allowedGroups": [],
    },
    {
        "id": 2, "name": "mi arena", "x": 400, "y": 200, "width": 20, "height": 20,
        "owner": "alice", "disableBuild": True, "z": 0,
        "allowedIds": [], "allowedGroups": ["guest"],
    },
]

# Mirrors the server-side policy: these owners are allowed, nobody else.
ALLOWED_OWNERS = {"mcp-gateway", "alice"}
DEFAULT_OWNER = "mcp-gateway"


class Handler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        seen.append((parsed.path, query))
        if query.get("token") != [TOKEN]:
            self._json(403, {"status": "403", "error": "invalid token"})
            return
        if parsed.path == "/v2/server/status":
            self._json(200, {
                "name": "E2E", "serverversion": "1.4.4.9", "tshockversion": "6.2.1",
                "port": 7777, "playercount": 1, "maxplayers": 8,
                "world": "World", "uptime": "0.00:00:05",
            })
        elif parsed.path == "/v4/players/read":
            self._json(200, {
                "nickname": "Alice", "username": "alice", "ip": "10.0.0.5",
                "group": "default", "muted": False, "token": "leak-me",
            })
        elif parsed.path == "/v3/bans/list":
            self._json(200, {"bans": [{
                "ticket_number": 1, "identifier": "203.0.113.9",
                "reason": "grief", "banning_user": "admin",
            }]})
        elif parsed.path in (
            "/v2/players/kick", "/v2/server/broadcast", "/v2/players/kill",
            "/v2/players/mute", "/v2/world/save", "/v3/server/reload",
        ):
            self._json(200, {"response": "ok"})
        else:
            self._json(404, {"status": "404", "error": "not found"})

    def _json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args: object) -> None:
        pass


class BridgeHandler(BaseHTTPRequestHandler):
    """Fake AgentBridge: enough of /v1/events to exercise the tool contract."""

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        bridge_seen.append(parsed.path)

        if self.headers.get("Authorization") != f"Bearer {BRIDGE_TOKEN}":
            self._json(401, {"error": "unauthorized"})
            return

        if parsed.path == "/v1/regions":
            self._json(200, {"regions": REGIONS})
            return

        if parsed.path.startswith("/v1/regions/"):
            name = unquote(parsed.path[len("/v1/regions/"):])
            region = next((item for item in REGIONS if item["name"] == name), None)
            if region is None:
                self._json(404, {"error": "region_not_found"})
            else:
                self._json(200, region)
            return

        if parsed.path != "/v1/events":
            self._json(404, {"error": "not_found"})
            return

        cursor = query.get("cursor")
        if cursor == ["1"]:
            self._json(200, {
                "events": [_event(2, "chat", {"player": "Bob", "text": "solo este"})],
                "cursor": 2, "head": 2, "oldest": 1, "limit": 100,
                "hasMore": False, "session": "bridge-session",
            })
            return

        if cursor == ["99"]:
            self._json(410, {
                "error": "cursor_expired", "reason": "unknown", "oldest": 1,
                "head": 2, "resumeCursor": 0, "session": "bridge-session",
            })
            return

        self._json(200, {
            "events": [
                _event(1, "player.join", {"player": "Alice"}),
                _event(2, "chat", {"player": "Bob", "text": "solo este"}),
            ],
            "cursor": 2, "head": 2, "oldest": 1, "limit": 100,
            "hasMore": False, "session": "bridge-session",
        })

    def do_POST(self) -> None:
        parsed = urlparse(self.path)
        bridge_seen.append(parsed.path)

        if self.headers.get("Authorization") != f"Bearer {BRIDGE_TOKEN}":
            self._json(401, {"error": "unauthorized"})
            return

        if parsed.path != "/v1/regions":
            self._json(404, {"error": "not_found"})
            return

        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        owner = (body.get("owner") or DEFAULT_OWNER).strip()
        name = (body.get("name") or "").strip()

        if any(region["name"] == name for region in REGIONS):
            self._json(409, {"error": "region_exists"})
            return

        if owner not in ALLOWED_OWNERS:
            self._json(403, {"error": "owner_not_allowed"})
            return

        region = {
            "id": len(REGIONS) + 1, "name": name, "x": body["x"], "y": body["y"],
            "width": body["width"], "height": body["height"], "owner": owner,
            "disableBuild": True, "z": 0, "allowedIds": [], "allowedGroups": [],
        }
        REGIONS.append(region)
        self._json(201, region)

    def do_DELETE(self) -> None:
        parsed = urlparse(self.path)
        bridge_seen.append(parsed.path)

        if self.headers.get("Authorization") != f"Bearer {BRIDGE_TOKEN}":
            self._json(401, {"error": "unauthorized"})
            return

        if not parsed.path.startswith("/v1/regions/"):
            self._json(404, {"error": "not_found"})
            return

        name = unquote(parsed.path[len("/v1/regions/"):])
        region = next((item for item in REGIONS if item["name"] == name), None)
        if region is None:
            self._json(404, {"error": "region_not_found"})
            return

        REGIONS.remove(region)
        self._json(204, {})

    def _json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def log_message(self, *args: object) -> None:
        pass


def _event(cursor: int, event_type: str, data: dict) -> dict:
    return {
        "cursor": cursor,
        "at": "2026-11-01T17:01:59.554Z",
        "type": event_type,
        "untrusted": True,
        "data": data,
    }


def _pump(stream, messages: "queue.Queue[str | None]") -> None:
    for line in stream:
        messages.put(line)
    messages.put(None)


def read_message(messages: "queue.Queue[str | None]", timeout: float = 15.0) -> dict:
    try:
        line = messages.get(timeout=timeout)
    except queue.Empty as error:
        raise TimeoutError("no response from the MCP server") from error
    if line is None:
        raise EOFError("the MCP server closed stdout")
    return json.loads(line)


def run(
    port: int,
    allow_write: bool,
    token: str = TOKEN,
    bridge: tuple[str, str] | None = None,
    write_scenario: bool = False,
    elicit: str | None = None,
) -> tuple[dict, str]:
    env = dict(os.environ)
    env.update({
        "TSHOCK_REST_URL": f"http://127.0.0.1:{port}",
        "TSHOCK_TOKEN": token,
        "TSHOCK_MCP_ALLOW_WRITE": "true" if allow_write else "false",
        "TSHOCK_MCP_TIMEOUT_SECONDS": "10",
    })
    # Never let the caller's environment change the assertions below.
    for name in ISOLATED_ENV:
        env.pop(name, None)
    if bridge is not None:
        env["TSHOCK_BRIDGE_URL"] = bridge[0]
        env["TSHOCK_BRIDGE_TOKEN"] = bridge[1]
    proc = subprocess.Popen(
        ["dotnet", str(DEFAULT_DLL)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        env=env, text=True, bufsize=1,
    )
    messages: "queue.Queue[str | None]" = queue.Queue()
    threading.Thread(target=_pump, args=(proc.stdout, messages), daemon=True).start()

    def send(message: dict) -> None:
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    def raw_call(call_id: int, name: str, arguments: dict) -> dict:
        send({"jsonrpc": "2.0", "id": call_id, "method": "tools/call",
              "params": {"name": name, "arguments": arguments}})
        while True:
            message = read_message(messages)
            if message.get("method") == "elicitation/create":
                send({"jsonrpc": "2.0", "id": message["id"],
                      "result": {"action": "decline", "content": {"confirm": False}}})
                continue
            if message.get("id") == call_id:
                return message["result"]

    def call(call_id: int, name: str, arguments: dict, elicit_action: str | None = None) -> dict:
        send({"jsonrpc": "2.0", "id": call_id, "method": "tools/call",
              "params": {"name": name, "arguments": arguments}})
        while True:
            message = read_message(messages)
            if message.get("method") == "elicitation/create":
                # Answer the operator prompt the server sent us.
                action = elicit_action or "decline"
                send({
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"action": action, "content": {"confirm": action == "accept"}},
                })
                continue
            if message.get("id") == call_id:
                return json.loads(message["result"]["content"][0]["text"])

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {
              "protocolVersion": "2025-11-25" if elicit else "2025-06-18",
              "capabilities": {"elicitation": {}} if elicit else {},
              "clientInfo": {"name": "e2e", "version": "0.1"},
          }})
    while read_message(messages).get("id") != 1:
        pass
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    results = {
        "status": call(2, "server_status", {}),
        "player": call(3, "player_info", {"player": "Alice"}),
        "kick": call(4, "kick_player", {"player": "Alice", "reason": "e2e"}),
        "bans": call(5, "bans_list", {}),
        "missing_args": raw_call(6, "player_info", {}),
        "wrong_type": raw_call(7, "player_info", {"player": 123}),
        "events": call(8, "events_since", {}),
    }

    if allow_write:
        before_valid = len(seen)
        results["broadcast_max"] = call(27, "broadcast", {"message": "a" * 256})
        results["broadcast_max_requests"] = len(seen) - before_valid

    before_invalid = len(seen)
    results["player_over"] = call(31, "player_info", {"player": "p" * 65})
    results["user_over"] = call(32, "user_info", {"user": "u" * 65})
    results["group_over"] = call(33, "group_info", {"group": "g" * 65})
    if allow_write:
        results["broadcast_over"] = call(28, "broadcast", {"message": "a" * 257})
        results["kick_over_reason"] = call(
            29, "kick_player", {"player": "Alice", "reason": "r" * 201})
        results["kill_over_source"] = call(
            30, "kill_player", {"player": "Alice", "from": "s" * 65})
    results["invalid_requests"] = len(seen) - before_invalid

    if bridge is not None:
        results["events_by_cursor"] = call(9, "events_since", {"cursor": 1})
        results["events_expired"] = call(10, "events_since", {"cursor": 99})
        results["events_max_limit"] = call(11, "events_since", {"limit": 500})
        results["events_bad_cursor"] = raw_call(12, "events_since", {"cursor": -5})
        results["events_bad_limit"] = raw_call(13, "events_since", {"limit": 0})
        results["regions"] = call(14, "regions_list", {})
        results["region"] = call(15, "region_info", {"name": "spawn"})
        results["region_spaced"] = call(16, "region_info", {"name": "mi arena"})
        results["region_missing"] = call(17, "region_info", {"name": "nope"})
        results["region_bad_name"] = raw_call(18, "region_info", {"name": ""})

        if write_scenario:
            results["region_create"] = call(
                19, "region_create",
                {"name": "arena", "x": 300, "y": 300, "width": 20, "height": 20})
            results["region_create_again"] = call(
                20, "region_create",
                {"name": "arena", "x": 300, "y": 300, "width": 20, "height": 20})
            results["region_create_bad_owner"] = call(
                21, "region_create",
                {"name": "otra", "x": 300, "y": 300, "width": 20, "height": 20,
                 "owner": "mallory"})
            results["region_create_owner"] = call(
                22, "region_create",
                {"name": "parcela", "x": 300, "y": 300, "width": 20, "height": 20,
                 "owner": "alice"})
            results["region_delete_unconfirmed"] = call(23, "region_delete", {"name": "arena"})
            results["region_delete"] = call(24, "region_delete", {"name": "arena", "confirm": True})
            results["region_delete_again"] = call(
                25, "region_delete", {"name": "arena", "confirm": True})
        elif elicit:
            results["region_delete_elicit"] = call(
                19, "region_delete", {"name": "parcela"}, elicit_action=elicit)
            results["regions_after"] = call(20, "regions_list", {})
        else:
            results["region_create_disabled"] = call(
                19, "region_create",
                {"name": "arena", "x": 300, "y": 300, "width": 20, "height": 20})
            results["region_delete_disabled"] = call(
                20, "region_delete", {"name": "arena", "confirm": True})

        before_invalid_bridge = len(bridge_seen)
        results["region_over"] = call(34, "region_info", {"name": "r" * 129})
        results["region_control"] = call(35, "region_info", {"name": "spawn\nchild"})
        if write_scenario:
            results["region_owner_over"] = call(
                36, "region_create",
                {"name": "arena", "x": 300, "y": 300, "width": 20, "height": 20,
                 "owner": "o" * 129})
        results["invalid_bridge_requests"] = len(bridge_seen) - before_invalid_bridge

    send({"jsonrpc": "2.0", "id": 26, "method": "tools/list", "params": {}})
    while True:
        message = read_message(messages)
        if message.get("id") == 26:
            results["tools_raw"] = message["result"]["tools"]
            results["tools"] = {tool["name"]: tool for tool in results["tools_raw"]}
            break
    proc.stdin.close()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()
        raise
    return results, proc.stderr.read()


def run_modern_tools_list(port: int) -> tuple[dict, dict[str, dict], list[dict], str]:
    """Negotiates 2026-07-28 without initialize and returns tools/list.

    The modern revision replaces the initialize handshake with server/discover
    and per-request _meta, so this probe proves titles reach a current client
    through that path too, not only through the legacy wire transformation.
    """
    env = dict(os.environ)
    env.update({
        "TSHOCK_REST_URL": f"http://127.0.0.1:{port}",
        "TSHOCK_TOKEN": TOKEN,
        "TSHOCK_MCP_ALLOW_WRITE": "false",
        "TSHOCK_MCP_TIMEOUT_SECONDS": "10",
    })
    for name in ISOLATED_ENV:
        env.pop(name, None)

    proc = subprocess.Popen(
        ["dotnet", str(DEFAULT_DLL)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        env=env, text=True, bufsize=1,
    )
    messages: "queue.Queue[str | None]" = queue.Queue()
    threading.Thread(target=_pump, args=(proc.stdout, messages), daemon=True).start()

    def send(message: dict) -> None:
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    meta = {
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientInfo": {"name": "e2e-modern", "version": "0.1"},
        "io.modelcontextprotocol/clientCapabilities": {},
    }
    send({"jsonrpc": "2.0", "id": 1, "method": "server/discover",
          "params": {"_meta": meta}})
    discover = read_message(messages)
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list",
          "params": {"_meta": meta}})
    while True:
        message = read_message(messages)
        if message.get("id") == 2:
            tools_raw = message["result"]["tools"]
            tools = {tool["name"]: tool for tool in tools_raw}
            break

    proc.stdin.close()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()
        raise
    return discover, tools, tools_raw, proc.stderr.read()


def run_rate_limit(
    port: int,
    allow_write: bool,
    per_minute: str,
    write_per_minute: str,
    calls: list[tuple[str, dict]],
) -> tuple[list[dict], str]:
    """Runs a minimal session with explicit limits and returns each raw result.

    It deliberately does not reuse ``run``: that session makes many calls and
    would exhaust a one-per-minute budget before reaching the assertions.
    """
    env = dict(os.environ)
    for name in ISOLATED_ENV:
        env.pop(name, None)
    env.update({
        "TSHOCK_REST_URL": f"http://127.0.0.1:{port}",
        "TSHOCK_TOKEN": TOKEN,
        "TSHOCK_MCP_ALLOW_WRITE": "true" if allow_write else "false",
        "TSHOCK_MCP_TIMEOUT_SECONDS": "10",
        "TSHOCK_MCP_RATE_LIMIT_PER_MINUTE": per_minute,
        "TSHOCK_MCP_WRITE_RATE_LIMIT_PER_MINUTE": write_per_minute,
    })

    proc = subprocess.Popen(
        ["dotnet", str(DEFAULT_DLL)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        env=env, text=True, bufsize=1,
    )
    messages: "queue.Queue[str | None]" = queue.Queue()
    threading.Thread(target=_pump, args=(proc.stdout, messages), daemon=True).start()

    def send(message: dict) -> None:
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    def raw_call(call_id: int, name: str, arguments: dict) -> dict:
        send({"jsonrpc": "2.0", "id": call_id, "method": "tools/call",
              "params": {"name": name, "arguments": arguments}})
        while True:
            message = read_message(messages)
            if message.get("id") == call_id:
                return message["result"]

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {
              "protocolVersion": "2025-06-18",
              "capabilities": {},
              "clientInfo": {"name": "e2e-rate-limit", "version": "0.1"},
          }})
    while read_message(messages).get("id") != 1:
        pass
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    results = [
        raw_call(index + 2, name, arguments)
        for index, (name, arguments) in enumerate(calls)
    ]

    proc.stdin.close()
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()
        raise
    return results, proc.stderr.read()


def check(condition: bool, message: str) -> None:
    if not condition:
        print(f"FAIL: {message}")
        sys.exit(1)
    print(f"ok: {message}")


def check_catalog(snapshot: dict, tools: list[dict], label: str) -> None:
    """Every scenario must emit the same catalog frozen in the contract snapshot."""
    problems = contract.compare_snapshot(snapshot, tools)
    if problems:
        print(contract.format_diff(snapshot, tools))
        details = "\n".join(f"  {problem}" for problem in problems[:40])
        check(False, f"{label}: tools/list matches the contract snapshot\n{details}")
    check(True, f"{label}: tools/list matches the contract snapshot")


def load_public_error_codes() -> set[str]:
    """Reads the canonical public set from the architecture table."""
    text = (REPO_ROOT / "docs" / "architecture.md").read_text(encoding="utf-8")
    begin = "<!-- BEGIN ERROR CODES -->"
    end = "<!-- END ERROR CODES -->"
    block = text.split(begin, 1)[1].split(end, 1)[0]
    return set(re.findall(r"^\|\s*`([a-z0-9_]+)`", block, flags=re.MULTILINE))


def iter_error_codes(value: object):
    if isinstance(value, dict):
        error = value.get("error")
        if isinstance(error, dict) and isinstance(error.get("code"), str):
            yield error["code"]
        for item in value.values():
            yield from iter_error_codes(item)
    elif isinstance(value, list):
        for item in value:
            yield from iter_error_codes(item)


def check_known_error_codes(results: dict, known: set[str]) -> None:
    """No tool may return a code outside the documented public set."""
    observed = set(iter_error_codes(results))
    unknown = sorted(observed - known)
    check(not unknown, "every error code belongs to the canonical table" +
          (f" (unknown: {unknown})" if unknown else ""))


def main() -> int:
    if not DEFAULT_DLL.exists():
        print(f"Binary not found: {DEFAULT_DLL}\nRun: dotnet build -c Release", file=sys.stderr)
        return 2

    known_codes = load_public_error_codes()
    check("invalid_argument" in known_codes and "bridge_error" in known_codes,
          "the canonical error table is readable")

    try:
        mutations = contract.self_test()
    except AssertionError as error:
        check(False, f"the tools/list contract comparator detects mutations: {error}")
    check(True, f"the tools/list contract comparator detects {mutations} mutation classes")

    try:
        contract_snapshot = contract.load_snapshot()
    except contract.ContractError as error:
        check(False, f"the tools/list contract snapshot is readable: {error}")
    check(bool(contract_snapshot["order"]),
          "the tools/list contract snapshot is present and valid")

    server = HTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        results, stderr = run(port, allow_write=True)
        check_known_error_codes(results, known_codes)
        check_catalog(contract_snapshot, results["tools_raw"], "legacy 2025-06-18")
        check(results["status"]["ok"] and results["status"]["data"]["name"] == "E2E",
              "server_status returns the parsed TShock body")
        check(results["status"]["data"]["serverversion"] == "1.4.4.9",
              "version strings that look like IPv4 addresses are not redacted")
        check(results["player"]["data"]["ip"] == "<redacted>", "player IP is redacted")
        check(results["player"]["data"]["token"] == "<redacted>", "token field is always redacted")
        check(results["kick"]["ok"], "kick_player succeeds when writes are enabled")
        check(any(path == "/v2/players/kick" for path, _ in seen), "kick route was reached")
        check(results["bans"]["data"]["bans"][0]["identifier"] == "<redacted>",
              "ban identifiers that are IPs are redacted")
        check(results["missing_args"].get("isError") is True,
              "missing required arguments are reported as a tool error")
        missing_envelope = json.loads(results["missing_args"]["content"][0]["text"])
        check(missing_envelope["error"]["code"] == "invalid_argument",
              "missing arguments keep the stable invalid_argument envelope")
        wrong_envelope = json.loads(results["wrong_type"]["content"][0]["text"])
        check(results["wrong_type"].get("isError") is True and
              wrong_envelope["error"]["code"] == "invalid_argument",
              "wrongly typed arguments keep the stable invalid_argument envelope")

        check(results["broadcast_max"]["ok"] is True and
              results["broadcast_max_requests"] == 1,
              "a message at the limit reaches TShock exactly once")
        check(results["invalid_requests"] == 0,
              "over-limit arguments never reach TShock")
        for key in ("broadcast_over", "kick_over_reason", "kill_over_source",
                    "player_over", "user_over", "group_over"):
            check(results[key]["error"]["code"] == "invalid_argument",
                  f"{key} keeps the invalid_argument envelope")
        check("a" * 257 not in stderr,
              "the rejected payload never appears in stderr")

        # Rate limiting: a global budget of one allows exactly one read in the
        # session. The denied call returns the stable envelope and never reaches
        # the fake TShock.
        before_rate = len(seen)
        rate_results, rate_stderr = run_rate_limit(
            port, allow_write=False, per_minute="1", write_per_minute="20",
            calls=[("server_status", {}), ("server_status", {})])
        check(rate_results[0].get("isError") is not True,
              "a call inside the global budget succeeds")
        check(rate_results[1].get("isError") is True,
              "a call over the global budget is a tool error")
        rate_envelope = json.loads(rate_results[1]["content"][0]["text"])
        check(rate_envelope["error"]["code"] == "rate_limited",
              "the global limit returns the stable rate_limited code")
        check(0 < rate_envelope["error"].get("details", {}).get("retryAfterMs", 0) <= 60000,
              "rate_limited carries an actionable retryAfterMs")
        check(len(seen) - before_rate == 1,
              "the rate-limited call never reaches TShock")
        check(TOKEN not in rate_stderr,
              "the token never appears in stderr when a call is rate-limited")

        # Writes have their own budget: with one write per minute, the second
        # broadcast is denied even though the global budget is still large.
        before_write = len(seen)
        write_results, write_stderr = run_rate_limit(
            port, allow_write=True, per_minute="60", write_per_minute="1",
            calls=[("broadcast", {"message": "rate limit"}),
                   ("broadcast", {"message": "rate limit"})])
        check(write_results[0].get("isError") is not True,
              "a write inside its budget succeeds")
        check(write_results[1].get("isError") is True,
              "a write over its budget is a tool error")
        write_envelope = json.loads(write_results[1]["content"][0]["text"])
        check(write_envelope["error"]["code"] == "rate_limited",
              "the write limit returns the stable rate_limited code")
        check(len(seen) - before_write == 1,
              "the rate-limited write never reaches TShock")
        check(TOKEN not in write_stderr,
              "the token never appears in stderr when a write is rate-limited")

        check(results["tools"]["server_status"]["annotations"]["readOnlyHint"] is True,
              "read tools are annotated as read-only")
        check(results["tools"]["kick_player"]["annotations"]["destructiveHint"] is True,
              "player-mutating tools are annotated as destructive")
        check(TOKEN not in stderr, "token never appears in stderr")

        # The bridge tool is always registered, even with no bridge configured.
        check("events_since" in results["tools"],
              "events_since is registered without a configured bridge")
        check(results["tools"]["events_since"]["annotations"]["readOnlyHint"] is True,
              "events_since is annotated as read-only")
        check(len(results["tools"]) == 23, "tools/list exposes the 23 documented tools")

        legacy_titles = {
            name: tool.get("title") for name, tool in results["tools"].items()
        }
        check(all(isinstance(title, str) and title.strip()
                  for title in legacy_titles.values()),
              "every tool declares a non-empty title")
        check(all((results["tools"][name].get("annotations") or {}).get("title") == title
                  for name, title in legacy_titles.items()),
              "every title is mirrored in annotations.title")
        check(len(set(legacy_titles.values())) == 23,
              "tool titles are unique")

        discover, modern_tools, modern_tools_raw, modern_stderr = run_modern_tools_list(port)
        check("result" in discover and
              "2026-07-28" in discover["result"].get("supportedVersions", []),
              "server/discover advertises 2026-07-28")
        check_catalog(contract_snapshot, modern_tools_raw, "modern 2026-07-28")
        check(contract.normalize(modern_tools_raw) == contract.normalize(results["tools_raw"]),
              "legacy and modern clients receive the identical catalog")
        modern_titles = {
            name: tool.get("title") for name, tool in modern_tools.items()
        }
        check(len(modern_tools) == 23,
              "a 2026-07-28 client sees the 23 tools")
        check(modern_titles == legacy_titles,
              "legacy and modern clients receive the same titles")
        check(all((modern_tools[name].get("annotations") or {}).get("title") == title
                  for name, title in modern_titles.items()),
              "a 2026-07-28 client receives annotations.title too")
        check(TOKEN not in modern_stderr,
              "token never appears in the modern client's stderr")

        max_lengths = {
            ("broadcast", "message"): 256,
            ("kick_player", "player"): 64,
            ("kick_player", "reason"): 200,
            ("kill_player", "player"): 64,
            ("kill_player", "from"): 64,
            ("mute_player", "player"): 64,
            ("player_info", "player"): 64,
            ("user_info", "user"): 64,
            ("user_info", "type"): 16,
            ("group_info", "group"): 64,
            ("region_info", "name"): 128,
            ("region_create", "name"): 128,
            ("region_create", "owner"): 128,
            ("region_delete", "name"): 128,
        }
        wrong_limits = [
            f"{tool}.{parameter}"
            for (tool, parameter), expected in max_lengths.items()
            if (results["tools"].get(tool, {}).get("inputSchema", {})
                .get("properties", {}).get(parameter, {}).get("maxLength") != expected)
        ]
        check(not wrong_limits,
              "every text argument publishes its maxLength in inputSchema" +
              (f" (wrong: {wrong_limits})" if wrong_limits else ""))

        # Clients must not need the SDK defaults: every tool declares the four hints
        # as real booleans, which is also what the OpenAI directory requires.
        four_hints = ("readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint")
        missing_hints = [
            f"{name}:{hint}"
            for name, tool in results["tools"].items()
            for hint in four_hints
            if not isinstance((tool.get("annotations") or {}).get(hint), bool)
        ]
        check(not missing_hints,
              "every tool declares all four annotation hints" +
              (f" (missing: {missing_hints})" if missing_hints else ""))
        check(results["tools"]["broadcast"]["annotations"]["readOnlyHint"] is False,
              "write tools explicitly declare readOnlyHint false")
        check(results["events"]["error"]["code"] == "bridge_unavailable",
              "events_since reports bridge_unavailable when unconfigured")

        before = len(seen)
        results, _ = run(port, allow_write=False)
        check_known_error_codes(results, known_codes)
        check_catalog(contract_snapshot, results["tools_raw"], "writes disabled")
        check(results["kick"]["ok"] is False and results["kick"]["error"]["code"] == "write_disabled",
              "kick_player is blocked when writes are disabled")
        check(not any(path == "/v2/players/kick" for path, _ in seen[before:]),
              "blocked write never reaches TShock")

        results, _ = run(port, allow_write=False, token="wrong")
        check_known_error_codes(results, known_codes)
        check(results["status"]["ok"] is False and results["status"]["error"]["httpStatus"] == 403,
              "invalid token maps to a 403 rest_error")
        check("invalid token" in results["status"]["error"]["message"],
              "TShock error message is propagated")

        # Bridge acceptance: the real binary talks to a fake plugin over HTTP.
        bridge_server = HTTPServer(("127.0.0.1", 0), BridgeHandler)
        bridge_url = f"http://127.0.0.1:{bridge_server.server_address[1]}"
        threading.Thread(target=bridge_server.serve_forever, daemon=True).start()
        try:
            before = len(bridge_seen)
            results, stderr = run(port, allow_write=False, bridge=(bridge_url, BRIDGE_TOKEN))
            check_known_error_codes(results, known_codes)
            check_catalog(contract_snapshot, results["tools_raw"], "bridge configured")

            check(len(results["events"]["data"]["events"]) == 2,
                  "events_since returns the retained backlog")
            check(results["events"]["data"]["events"][0]["untrusted"] is True,
                  "event payloads keep the untrusted flag")
            check(results["events"]["data"]["cursor"] == 2,
                  "events_since returns the cursor to resume from")
            check(len(results["events_by_cursor"]["data"]["events"]) == 1 and
                  results["events_by_cursor"]["data"]["events"][0]["cursor"] == 2,
                  "events_since treats the cursor as an exclusive lower bound")
            check(results["events_max_limit"]["ok"] is True,
                  "events_since accepts the maximum page size")
            check(results["events_expired"]["error"]["code"] == "cursor_expired" and
                  results["events_expired"]["error"]["details"]["resumeCursor"] == 0,
                  "expired cursors keep the resume cursor in the error details")

            bad_cursor = json.loads(results["events_bad_cursor"]["content"][0]["text"])
            bad_limit = json.loads(results["events_bad_limit"]["content"][0]["text"])
            check(bad_cursor["error"]["code"] == "invalid_argument" and
                  bad_limit["error"]["code"] == "invalid_argument",
                  "invalid cursor and limit keep the invalid_argument envelope")

            check(len(results["regions"]["data"]["regions"]) == 2,
                  "regions_list returns every bridge region")
            check(results["region"]["data"]["owner"] == "mcp-gateway",
                  "region_info returns the full region record")
            check(results["region_spaced"]["data"]["name"] == "mi arena",
                  "region_info escapes names with spaces into one path segment")
            check(results["region_missing"]["error"]["code"] == "region_not_found" and
                  results["region_missing"]["error"]["httpStatus"] == 404,
                  "a missing region maps to region_not_found with 404")
            bad_name = json.loads(results["region_bad_name"]["content"][0]["text"])
            check(bad_name["error"]["code"] == "invalid_argument",
                  "an empty region name keeps the invalid_argument envelope")
            check(results["region_over"]["error"]["code"] == "invalid_argument" and
                  results["region_control"]["error"]["code"] == "invalid_argument",
                  "over-limit and control-character region names keep invalid_argument")
            check(results["invalid_bridge_requests"] == 0,
                  "invalid region arguments never reach the bridge")

            check(results["region_create_disabled"]["error"]["code"] == "write_disabled",
                  "region_create is blocked when writes are disabled")
            check(results["region_delete_disabled"]["error"]["code"] == "write_disabled",
                  "region_delete is blocked when writes are disabled")

            # 4 events reads + regions_list + region_info(spawn) + region_info(mi arena)
            # + region_info(nope). Neither the invalid calls nor the blocked writes
            # may appear here.
            check(len(bridge_seen) - before == 8,
                  "invalid arguments and blocked writes never reach the bridge")
            check(BRIDGE_TOKEN not in stderr, "bridge token never appears in stderr")

            # Write acceptance: the same real binary, now with writes enabled.
            before = len(bridge_seen)
            results, stderr = run(
                port, allow_write=True, bridge=(bridge_url, BRIDGE_TOKEN), write_scenario=True)
            check_known_error_codes(results, known_codes)
            check_catalog(contract_snapshot, results["tools_raw"], "bridge and writes enabled")

            check(results["region_create"]["ok"] is True and
                  results["region_create"]["data"]["owner"] == "mcp-gateway",
                  "region_create creates the region with the policy default owner")
            check(results["region_create_again"]["error"]["code"] == "region_exists",
                  "creating an existing region maps to region_exists")
            check(results["region_create_bad_owner"]["error"]["code"] == "owner_not_allowed",
                  "an owner outside the server policy maps to owner_not_allowed")
            check(results["region_create_owner"]["data"]["owner"] == "alice",
                  "an allowed owner is forwarded to the bridge")
            check(results["region_owner_over"]["error"]["code"] == "invalid_argument",
                  "an over-limit owner keeps the invalid_argument envelope")
            check(results["invalid_bridge_requests"] == 0,
                  "invalid region writes never reach the bridge")
            check(results["region_delete_unconfirmed"]["error"]["code"] == "confirmation_required",
                  "deleting without confirmation is refused when the client cannot ask")
            check(results["region_delete"]["ok"] is True, "region_delete deletes the region")
            check(results["region_delete_again"]["error"]["code"] == "region_not_found",
                  "deleting a missing region maps to region_not_found")

            # 8 read calls + 2 successful creates + 1 rejected create + 1 rejected
            # create owner + 1 delete + 1 delete-again. The unconfirmed delete must
            # never reach the bridge.
            check(len(bridge_seen) - before == 14,
                  "an unconfirmed delete never reaches the bridge")
            check(BRIDGE_TOKEN not in stderr, "bridge token stays out of stderr with writes")

            # Elicitation: the client declares the capability, so the server asks
            # the operator instead of trusting a boolean from the model.
            results, _ = run(
                port, allow_write=True, bridge=(bridge_url, BRIDGE_TOKEN), elicit="decline")
            check_known_error_codes(results, known_codes)
            check_catalog(contract_snapshot, results["tools_raw"], "protocol 2025-11-25")
            check(results["region_delete_elicit"]["error"]["code"] == "confirmation_declined",
                  "a declined elicitation cancels the deletion")
            check(any(region["name"] == "parcela"
                      for region in results["regions_after"]["data"]["regions"]),
                  "a declined deletion leaves the region in place")

            results, stderr = run(
                port, allow_write=True, bridge=(bridge_url, BRIDGE_TOKEN), elicit="accept")
            check_known_error_codes(results, known_codes)
            check(results["region_delete_elicit"]["ok"] is True and
                  results["region_delete_elicit"]["data"]["deleted"] is True,
                  "an accepted elicitation deletes the region")
            check(not any(region["name"] == "parcela"
                          for region in results["regions_after"]["data"]["regions"]),
                  "an accepted deletion removes the region")
            check(BRIDGE_TOKEN not in stderr, "bridge token stays out of stderr with elicitation")

            bridge_server.shutdown()
            bridge_server.server_close()

            results, _ = run(port, allow_write=False, bridge=(bridge_url, BRIDGE_TOKEN))
            check_known_error_codes(results, known_codes)
            check_catalog(contract_snapshot, results["tools_raw"], "bridge stopped")
            check(results["events"]["error"]["code"] == "bridge_unavailable",
                  "a stopped bridge maps to bridge_unavailable")
            check(results["status"]["ok"] is True,
                  "REST tools keep working while the bridge is down")
        finally:
            bridge_server.server_close()

        print("E2E PASSED")
        return 0
    finally:
        server.shutdown()


if __name__ == "__main__":
    raise SystemExit(main())
