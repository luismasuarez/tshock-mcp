## Qué cambia

<!-- Resumen breve del cambio y del motivo. -->

## Capa afectada

- [ ] Gateway MCP: tool REST.
- [ ] Plugin `TShock.AgentBridge`.
- [ ] `BridgeClient` o tools del puente.
- [ ] Documentación, CI o infraestructura.

## Verificación

- [ ] `dotnet build -c Release` sin advertencias nuevas.
- [ ] `dotnet test -c Release` pasa por completo.
- [ ] `python3 tests/e2e/mcp_stdio_smoke.py` informa `E2E PASSED`.
- [ ] Si agrega o cambia una tool, hay una prueba MCP por stdio que la invoca.
- [ ] Si toca TShock y existe una tool, se verificó contra el servidor real llamando a la tool, no
      solo con `curl`.
- [ ] Si solo toca el plugin, se probó por HTTP y se indicó que no es la aceptación MCP.

## Contrato y seguridad

- [ ] El sobre `ok` / `error` no cambió; los códigos nuevos están documentados.
- [ ] Los argumentos se validan antes de la red.
- [ ] Las tools de escritura comprueban `TSHOCK_MCP_ALLOW_WRITE`.
- [ ] Las operaciones destructivas exigen confirmación.
- [ ] No hay tokens, contraseñas, IP reales ni rutas absolutas en el diff.
- [ ] El token del puente no se registra ni se devuelve.

## Documentación

- [ ] `docs/tools.md` actualizado si cambió el catálogo de tools.
- [ ] `docs/roadmap.md` actualizado si cambió el estado por capa.
- [ ] `README.md` actualizado si cambió lo que el usuario puede hacer.
- [ ] `docs/bridge.md` actualizado si cambió el contrato del plugin.
- [ ] `docs/testing.md` actualizado si cambió la forma de probar.

Consulte [AGENTS.md](../AGENTS.md) y [docs/testing.md](../docs/testing.md) antes de pedir revisión.
