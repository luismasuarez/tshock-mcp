#!/usr/bin/env python3
"""End-to-end smoke test: MCP stdio server -> fake TShock REST API.

Build first, then run from anywhere:

    dotnet build -c Release
    python3 tests/e2e/mcp_stdio_smoke.py

The script starts a local fake TShock server, launches the real MCP binary over
stdio, and asserts the full path: JSON-RPC tool call -> REST request -> result
envelope. It uses only the Python standard library.
"""
from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DLL = REPO_ROOT / "src" / "TShock.MCP.Server" / "bin" / "Release" / "net8.0" / "tshock-mcp.dll"
TOKEN = "secret-token"
seen: list[tuple[str, dict[str, list[str]]]] = []


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


def run(port: int, allow_write: bool, token: str = TOKEN) -> tuple[dict, str]:
    env = dict(os.environ)
    env.update({
        "TSHOCK_REST_URL": f"http://127.0.0.1:{port}",
        "TSHOCK_TOKEN": token,
        "TSHOCK_MCP_ALLOW_WRITE": "true" if allow_write else "false",
        "TSHOCK_MCP_TIMEOUT_SECONDS": "10",
    })
    # Never let the caller's environment change the assertions below.
    for name in ("TSHOCK_MCP_SHOW_IPS", "TSHOCK_MCP_AUDIT_LOG", "TSHOCK_MCP_ALLOW_INSECURE"):
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

    def raw_call(call_id: int, name: str, arguments: dict) -> dict:
        send({"jsonrpc": "2.0", "id": call_id, "method": "tools/call",
              "params": {"name": name, "arguments": arguments}})
        while True:
            message = read_message(messages)
            if message.get("id") == call_id:
                return message["result"]

    def call(call_id: int, name: str, arguments: dict) -> dict:
        return json.loads(raw_call(call_id, name, arguments)["content"][0]["text"])

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                     "clientInfo": {"name": "e2e", "version": "0.1"}}})
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
    }

    send({"jsonrpc": "2.0", "id": 8, "method": "tools/list", "params": {}})
    while True:
        message = read_message(messages)
        if message.get("id") == 8:
            results["tools"] = {tool["name"]: tool for tool in message["result"]["tools"]}
            break
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


def main() -> int:
    if not DEFAULT_DLL.exists():
        print(f"Binary not found: {DEFAULT_DLL}\nRun: dotnet build -c Release", file=sys.stderr)
        return 2

    server = HTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        results, stderr = run(port, allow_write=True)
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
        check(results["tools"]["server_status"]["annotations"]["readOnlyHint"] is True,
              "read tools are annotated as read-only")
        check(results["tools"]["kick_player"]["annotations"]["destructiveHint"] is True,
              "player-mutating tools are annotated as destructive")
        check(TOKEN not in stderr, "token never appears in stderr")

        before = len(seen)
        results, _ = run(port, allow_write=False)
        check(results["kick"]["ok"] is False and results["kick"]["error"]["code"] == "write_disabled",
              "kick_player is blocked when writes are disabled")
        check(not any(path == "/v2/players/kick" for path, _ in seen[before:]),
              "blocked write never reaches TShock")

        results, _ = run(port, allow_write=False, token="wrong")
        check(results["status"]["ok"] is False and results["status"]["error"]["httpStatus"] == 403,
              "invalid token maps to a 403 rest_error")
        check("invalid token" in results["status"]["error"]["message"],
              "TShock error message is propagated")

        print("E2E PASSED")
        return 0
    finally:
        server.shutdown()


if __name__ == "__main__":
    raise SystemExit(main())
