# Roadmap y contexto de continuación

Este documento es el punto de entrada para retomar el proyecto en una sesión nueva. Resume lo que
ya existe, las decisiones que no conviene volver a discutir, el plan de las fases siguientes y los
detalles de entorno que no son evidentes desde el código.

- **Repositorio:** https://github.com/luismasuarez/tshock-mcp
- **Estado:** Fase 0 completada y verificada contra un servidor TShock real (TShock 6.2.1.0,
  Terraria 1.4.5.8, cliente en la LAN).
- **Commit de referencia:** `v0.1.0`.
- **Fecha de cierre de la sesión:** 1 de noviembre de 2026.

---

## 1. Estado actual

### Lo que funciona

| Área | Estado |
| --- | --- |
| Transporte MCP | stdio, JSON-RPC por stdin/stdout, logs solo por stderr |
| Herramientas | 18: 12 de lectura y 6 de escritura |
| Backend | API REST de TShock, sin modificar TShock |
| Configuración | Solo variables de entorno, validación estricta al arrancar |
| Seguridad | Escritura desactivada por defecto, token nunca registrado, IP y credenciales redactadas |
| Contrato de salida | Sobre JSON estable `ok` / `error` |
| Pruebas | 109 pruebas xUnit y 17 comprobaciones e2e por stdio |
| Verificación real | Lectura, escritura, errores y redacción probados con un cliente de Terraria en la LAN |
| Privilegio mínimo | Grupo dedicado `mcp-rest` sin permisos de escritura; la escritura tiene dos barreras (flag del gateway y permisos de TShock) |
| Distribución | Dockerfile multi-stage y workflow de CI |

### Lo que no existe todavía

- Transporte HTTP y OAuth 2.1.
- Plugin puente con TShock.
- Regiones, eventos en tiempo real y descubrimiento de comandos.
- Ejecución de comandos arbitrarios, apagado, mutación de usuarios o prohibiciones.
- Publicación en GitHub: el repositorio es local y no tiene remoto configurado.

### Verificación de referencia

```bash
cd tshock-mcp
dotnet build -c Release
dotnet test -c Release --no-build
python3 tests/e2e/mcp_stdio_smoke.py
```

Resultados esperados: 0 advertencias, 109 pruebas superadas y `E2E PASSED` con 17 comprobaciones.

---

## 2. Decisiones tomadas (no re-litigar sin motivo nuevo)

| Decisión | Motivo | Consecuencia |
| --- | --- | --- |
| Repositorio independiente, no un fork | El gateway no modifica TShock; el fork solo se usa para PRs upstream | Ciclo de release propio y CI ligero |
| Gateway externo, no plugin en proceso | Aísla fallos, evita cargar ASP.NET dentro de TShock y desacopla el SDK MCP | Phase 0 no necesita TShock instalado para compilar |
| Licencia GPL-3.0-or-later | El futuro plugin enlaza `TShockAPI`, que es GPL | Un solo repo y una sola licencia |
| stdio primero | Es el camino más corto a un cliente real y no requiere autenticación de red | HTTP/OAuth queda para Fase 2 |
| Escritura cerrada por defecto | Un modelo confundido no debe poder mutar el servidor sin una decisión humana | `TSHOCK_MCP_ALLOW_WRITE=true` explícito |
| Sin `rawcmd` en Fase 0 | Un shell disfrazado anula el modelo de permisos | La ejecución de comandos llega con allowlist en Fase 1 |
| Sobre JSON estable | Todos los hosts renderizan el mismo contrato y es fácil de testear | No cambiar nombres de campos ni códigos existentes |
| Configuración solo por entorno | Evita archivos versionados con secretos y facilita la inyección del host | Se ignoran `appsettings.json` y argumentos CLI |
| Redacción por clave y por valor | `PlayerReadV4` devuelve IP y los bans usan `identifier` para IPs | Se redactan IPs y credenciales; las versiones no se tocan |
| Gateway en .NET 8 | Es el SDK instalado en la máquina | El plugin de Fase 1 necesitará .NET 9 |

### Invariantes

Las reglas completas están en [`AGENTS.md`](../AGENTS.md). Las críticas:

1. stdout solo transporta JSON-RPC.
2. El token nunca aparece en logs, errores, auditoría ni salida de herramientas.
3. Las 6 herramientas de escritura se bloquean en runtime salvo `TSHOCK_MCP_ALLOW_WRITE=true`.
4. Sin `rawcmd`, apagado ni mutación de usuarios/bans en Fase 0.
5. El sobre de salida es estable.
6. IPs redactadas por defecto; `token` y `password` siempre.
7. Configuración solo por variables de entorno.
8. Los argumentos inválidos devuelven `invalid_argument` sin tocar la red.

---

## 3. Fase 1 — Plugin puente `TShock.AgentBridge`

### Objetivo

Cubrir lo que la API REST no expone: regiones, eventos del servidor y descubrimiento de comandos.
El gateway sigue siendo el único proceso que habla MCP; el plugin solo expone un puente local
estructurado.

### Arquitectura

```
┌──────────────────┐   MCP/stdio   ┌─────────────────────┐   HTTP loopback   ┌──────────────────────┐
│ Claude Desktop   │◄─────────────►│  TShock.MCP.Server  │◄─────────────────►│  AgentBridge 7879    │
│ VS Code/Cursor   │               │  (net8.0)           │                   │  (plugin net9.0)     │
└──────────────────┘               │                     │                   └──────────┬───────────┘
                                   │  fallback REST      │                              │
                                   └──────────┬──────────┘                   ┌──────────▼───────────┐
                                              │                              │  TShock 6.2.1         │
                                              └─────────────────────────────►│  REST 7878            │
                                                                             └──────────────────────┘
```

Regla de degradación: si el bridge no responde en el arranque, el gateway sigue funcionando con
REST y las herramientas de puente se marcan como no disponibles.

### Componentes

| Componente | Responsabilidad |
| --- | --- |
| `TShock.AgentBridge/AgentBridgePlugin.cs` | `TerrariaPlugin`: arranque, parada y recarga |
| `BridgeServer.cs` | HTTP loopback con token propio y endpoints versionados |
| `MainThreadDispatcher.cs` | Cola `Channel<WorkItem>` drenada en `ServerApi.Hooks.GameUpdate` |
| `EventBuffer.cs` | Ring buffer de eventos con cursor monótono |
| `CommandCatalog.cs` | Lectura de `Commands.ChatCommands` y allowlist |
| `BridgeClient.cs` (gateway) | Cliente HTTP del bridge, con detección y timeout |
| `Tools/BridgeTools.cs` (gateway) | Tools de regiones, eventos y comandos |

### API del bridge (propuesta)

| Método | Ruta | Uso |
| --- | --- | --- |
| GET | `/v1/health` | Versión, uptime, mundo y tick |
| GET | `/v1/regions` | Lista de regiones |
| GET | `/v1/regions/{name}` | Detalle de una región |
| POST | `/v1/regions` | Crear región |
| DELETE | `/v1/regions/{name}` | Eliminar región |
| GET | `/v1/events?cursor=&limit=` | Eventos desde un cursor |
| GET | `/v1/commands` | Comandos con nombres, alias, permisos y ayuda |
| GET | `/v1/config` | Configuración redactada |
| POST | `/v1/commands/execute` | Comando permitido, con actor y auditoría |

Autenticación del bridge: `Authorization: Bearer <bridge-token>`, un secreto distinto del token REST
de TShock. Bind exclusivo a `127.0.0.1`.

### Nuevas tools MCP

| Tool | Origen | Notas |
| --- | --- | --- |
| `regions_list` | Bridge | Solo lectura |
| `region_info` | Bridge | Solo lectura |
| `region_create` | Bridge | Escritura, requiere flag |
| `region_delete` | Bridge | Escritura destructiva, requiere flag y confirmación |
| `events_since` | Bridge | Cursor explícito, sin sesiones MCP |
| `commands_list` | Bridge | Descubrimiento con permisos |
| `config_get` | Bridge | Redactado |
| `run_command` | Bridge | Opt-in, allowlist vacía por defecto |

### Dispatcher de hilo principal

Toda mutación del mundo debe ejecutarse en el hilo del juego. El patrón:

```csharp
public sealed class MainThreadDispatcher
{
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>();

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
            return Task.CompletedTask;
        });
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public void Pump()
    {
        while (_queue.Reader.TryRead(out var work))
        {
            work();
        }
    }
}
```

`Pump()` se registra en `ServerApi.Hooks.GameUpdate`. Si se agota el timeout, la tool devuelve
`bridge_busy` y no se reintenta a ciegas.

### Eventos y cursor

- Ring buffer en memoria de 10 000 eventos.
- Cursor `long` monótono; el cliente guarda el último valor y pide `events_since(cursor)`.
- Tipos iniciales: `player.join`, `player.leave`, `chat`, `command`, `player.death`, `boss.kill`,
  `region.created`, `region.deleted`.
- Todo contenido generado por jugadores se marca con `"untrusted": true`.
- Si el cursor es más antiguo que el buffer, se devuelve `cursor_expired` con el cursor mínimo
  disponible. No hay notificaciones push en Fase 1.

### Descubrimiento de comandos

`CommandCatalog` lee `Commands.ChatCommands` y expone:

```json
{
  "name": "kick",
  "aliases": ["k"],
  "help": "Kicks a player from the server.",
  "permissions": ["tshock.admin.kick"],
  "allowServer": true
}
```

El gateway genera `command_<name>` solo para comandos incluidos en una allowlist explícita. Los
comandos con esquema curado a mano tienen prioridad. La allowlist por defecto está vacía.

### Seguridad de la Fase 1

- Bridge solo en loopback y con token propio; nunca expuesto a Internet.
- Allowlist de comandos vacía por defecto; `run_command` requiere habilitación explícita.
- Regiones y comandos validan permisos del actor configurado, no solo el token del bridge.
- Auditoría JSONL de cada mutación: operación, actor, parámetros redactados, resultado y duración.
- Confirmación humana para `region_delete` y cualquier comando destructivo.
- Límite de eventos por llamada y de tamaño de respuesta.

### Plan de trabajo

- [ ] Instalar .NET 9 SDK en la máquina (TShock 6.2.1 apunta a `net9.0`).
- [ ] Crear `src/TShock.AgentBridge/TShock.AgentBridge.csproj` con referencias a TShock 6.2.1 y TSAPI.
- [ ] Implementar `AgentBridgePlugin` con arranque y parada idempotentes.
- [ ] Implementar `MainThreadDispatcher` y probarlo con un work item que lea `Main.worldName`.
- [ ] Implementar `BridgeServer` con `/v1/health` y autenticación bearer.
- [ ] Añadir regiones (listar, leer, crear, eliminar).
- [ ] Añadir `EventBuffer` y `events_since` con cursor.
- [ ] Añadir `CommandCatalog` y `/v1/commands`.
- [ ] Añadir `BridgeClient` y `BridgeTools` al gateway, con degradación a REST.
- [ ] Extender la suite xUnit y la prueba e2e con un bridge falso.
- [ ] Probar en un servidor TShock real: recarga, reinicio, desconexión del bridge y concurrencia.

### Criterios de aceptación

- La suite de Fase 0 sigue en verde sin cambios de contrato.
- El plugin carga en TShock 6.2.1 y se descarga sin dejar hilos ni puertos abiertos.
- Crear y eliminar una región desde MCP se refleja en el servidor.
- `events_since` devuelve eventos ordenados y estables entre reconexiones.
- Un bridge caído no impide usar las tools REST.
- Ninguna mutación se ejecuta fuera del hilo principal.
- El token del bridge no aparece en logs ni en respuestas.

### Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Deadlock entre el hilo HTTP y el hilo del juego | Timeout de 5 s y prohibición de esperas síncronas dentro del pump |
| Cambios de API entre versiones de TShock | Fijar 6.2.1, compilar contra NuGet y probar en un servidor real |
| Volumen de eventos | Ring buffer acotado, límite por llamada y `cursor_expired` |
| Escalada por comandos | Allowlist vacía, permisos del actor y confirmación |
| Doble fuente de verdad (REST y bridge) | El bridge manda cuando está disponible; el gateway documenta la degradación |

---

## 4. Fase 2 — HTTP y OAuth 2.1

### Objetivo

Permitir despliegues remotos y varios clientes sin compartir un proceso hijo por sesión.

### Alcance

- Streamable HTTP en `/mcp`, modo sin estado por defecto.
- TLS terminado en un proxy inverso; el gateway escucha en loopback.
- OAuth 2.1: authorization server, resource metadata, validación de audiencia y `Authorization:
  Bearer` en cada request. Nunca tokens en query string.
- Scopes por cliente: `read`, `players`, `world`, `admin`.
- Rate limiting y auditoría correlacionada por cliente.
- `docker compose` con TShock, gateway y proxy TLS.
- Documentación de despliegue y rotación de secretos.

### Nota de protocolo

El spec MCP **2026-07-28** eliminó las sesiones a nivel de protocolo y el header
`Mcp-Session-Id`. No construir estado por conexión: usar cursores y handles explícitos, como en
`events_since`.

### Criterios de aceptación

- Un cliente remoto completa el handshake y lista las 18 tools.
- Un token inválido o de audiencia incorrecta recibe 401.
- Un scope insuficiente recibe un error de autorización, no un 500.
- El token no aparece en logs, auditoría ni URLs.
- La suite de Fase 0 y Fase 1 sigue en verde.

---

## 5. Fase 3 — Asistente en el chat del juego (opcional)

No es necesaria para el objetivo principal y no debe mezclarse con la administración.

- Comando `/ai <pregunta>` con cola, rate limit y coste por jugador.
- Motor: *sampling* de MCP si el host lo soporta, o API de proveedor configurada por el operador.
- Respuestas de solo lectura por defecto; ninguna acción administrativa desde el chat de un jugador.
- Moderación de entrada y salida; el chat sigue siendo contenido no confiable.
- Apagado por configuración y por permisos.

---

## 6. Contribuciones upstream a TShock

El core no debe depender del SDK MCP. Sí conviene aportar primitivas genéricas que beneficien a
cualquier integración.

| Propuesta | Valor | Riesgo | Prioridad |
| --- | --- | --- | --- |
| `GET /v4/commands` con permisos y ayuda | Alto: permite generar tools sin escribir una por una | Bajo | Alta |
| `GET /v4/events` con cursor o SSE | Alto: eventos sin plugin | Medio | Alta |
| Errores estructurados `{code,message,hint}` | Medio: permite actuar al agente | Bajo | Media |
| Scopes REST más gruesos (`read`, `players`, `world`, `admin`) | Medio: menos configuración | Medio, posible ruptura | Media |
| Rotación y expiración de tokens + hook de auditoría | Alto para operadores | Medio | Media |
| `GET /v4/server/info` estable | Bajo, pero útil | Bajo | Baja |
| Rutas REST para regiones | Medio: elimina la necesidad del bridge para regiones | Medio | Baja |
| `docs/mcp.md` con quickstart y matriz de permisos | Bajo | Ninguno | Baja |

Estrategia: un fork de TShock solo para ramas de PR (`feat/rest-v4-commands`,
`feat/events-sse`). Nada de MCP dentro del core.

---

## 7. Decisiones abiertas

| Pregunta | Opciones | Recomendación |
| --- | --- | --- |
| Transporte del bridge | HTTP loopback, Unix socket, named pipe | HTTP loopback con bearer; simple y multiplataforma |
| Secreto del bridge | Token propio o reutilizar el token REST | Token propio, con permisos separados |
| Retención de eventos | Memoria, JSONL, ambos | Ring buffer en memoria + JSONL opcional |
| Ejecución de comandos | Todo, allowlist, nada | Allowlist vacía por defecto |
| Nombre y publicación | `tshock-mcp` comunitario u organización oficial | Publicar como comunitario y consultar antes de usar el nombre oficial |
| Migración a .NET 9 | Gateway net8 + plugin net9, o todo net9 | Gateway net8 hasta que el plugin estabilice |
| Confirmaciones destructivas | Elicitation MCP, parámetro `confirm`, ambos | Ambos, con fallback si el host no soporta elicitation |

---

## 8. Cómo retomar en una sesión nueva

### Orden de lectura

1. `AGENTS.md` — invariantes y reglas del repositorio.
2. `README.md` — uso y estado.
3. `docs/architecture.md` — diseño de la Fase 0.
4. `docs/security.md` — modelo de amenazas.
5. `docs/local-testing.md` — guía para levantar TShock y probar en OpenCode.
6. Este documento — plan y contexto.

### Comandos

```bash
cd tshock-mcp
git log --oneline -3
dotnet build -c Release
dotnet test -c Release --no-build
python3 tests/e2e/mcp_stdio_smoke.py
```

Para levantar TShock y registrar el MCP en OpenCode, sigue
[`local-testing.md`](local-testing.md).

### Mapa de archivos

```
src/TShock.MCP.Server/
  Program.cs                     Host, filtro de invocación, transporte stdio
  Configuration/TShockOptions.cs Variables de entorno y validación
  TShock/TShockClient.cs         Cliente REST, errores, auditoría
  TShock/ResponseRedactor.cs     Redacción de IP y credenciales
  TShock/AuditLog.cs             JSONL de auditoría
  TShock/ToolResults.cs          Sobre estable
  Tools/ReadTools.cs             12 tools de lectura
  Tools/WriteTools.cs            6 tools de escritura
tests/
  TShock.MCP.Server.Tests/       Suite xUnit
  e2e/mcp_stdio_smoke.py         Prueba de extremo a extremo
docs/                            Arquitectura, tools, seguridad, roadmap
docker/                          Imagen y ejemplo para Claude Desktop
```

### Entorno

- **.NET SDK instalado:** 8.0.426. Solo runtime 8.0.32.
- **.NET 9:** necesario para compilar el plugin de Fase 1; instalarlo antes de empezar.
- **Docker:** 29.4.1 disponible.
- **TShock de referencia:** 6.2.1, `net9.0`, REST por defecto en el puerto 7878.
- **MCP C# SDK:** 2.3.0 (`ModelContextProtocol`), compatible con `net8.0`.
- **Spec MCP:** 2026-07-28, sin sesiones de protocolo.

### Primeras tareas sugeridas

1. Instalar .NET 9 y crear el proyecto del plugin.
2. Implementar `/v1/health` y el dispatcher; validar que no hay deadlocks.
3. Añadir regiones y una prueba de integración con bridge falso.
4. Añadir eventos con cursor y `events_since`.
5. Añadir descubrimiento de comandos y allowlist.

---

## 9. Registro de la sesión

### Lo que se construyó

- Repositorio independiente con 39 archivos y commit inicial.
- Gateway MCP con 18 tools y contrato de salida estable.
- Suite de 109 pruebas y prueba e2e de 17 comprobaciones.
- Documentación, Dockerfile y workflow de CI.
- Revisión independiente con hallazgos corregidos.

### Hallazgos de la revisión ya corregidos

| Hallazgo | Corrección |
| --- | --- |
| Argumentos ausentes o mal tipados rompían el sobre | Filtro `AddCallToolFilter` que devuelve `invalid_argument` |
| IPs de bans en `identifier` sin redactar | Redacción por valor en campos de identidad |
| Token podía aparecer en el `ReasonPhrase` HTTP | Saneado y truncado en todos los errores |
| Timeout no numérico se ignoraba | `ConfigurationException` al arrancar |
| URL base aceptaba query, fragmento y credenciales | Rechazo explícito al arrancar |
| `1.4.4.9` se redactaba por parecer IPv4 | Redacción por valor solo en campos de identidad |
| Comentarios obsoletos y Docker sin el flag inseguro | Documentación corregida |

### Trampas conocidas (no repetir)

- `IPAddress.TryParse` acepta cadenas de versión como `1.4.4.9`. No redactar por valor en cualquier
  campo; limitarlo a `ip`, `identifier`, `address`, `host`, `hostname`, `remote`, `endpoint` y
  `lastip`.
- El binder de argumentos lanza antes del cuerpo del método; sin filtro no hay sobre estable.
- TShock solo lee el token de la query string. Nunca registrar URLs completas.
- `HttpClient.Timeout` se manifiesta como `TaskCanceledException`; distinguir cancelación del
  llamador de timeout.
- `dotnet run` puede escribir salida de compilación en stdout; los hosts deben usar el binario ya
  compilado.
- El spec MCP actual no tiene sesiones; no diseñar estado por conexión.
- `PlayerReadV4` devuelve `ip` sin comprobar permisos; la redacción del gateway es obligatoria.
- Los bans usan `identifier`, no `ip`, para el objetivo de la prohibición.
- `/v2/server/status` es pública en TShock 6.2.1.0: responde `200` sin token. El rechazo `403` se
  prueba en rutas protegidas como `/world/read`.
- Pasar el token con `-rest-token` rompe `reload_config`: el reload vuelve a parsear la línea de
  comandos y `RESTStartupTokens.Add` explota por clave duplicada, devolviendo `500` con el token en
  claro dentro del mensaje. Solución: `ApplicationRestTokens` en `config.json` y arrancar sin ese
  flag; así el token tampoco aparece en `docker inspect`.
- `mute_player` fija el estado en vez de alternarlo, y el gateway no expone `unmute` en Fase 0.
- `kick_player` puede devolver `200` y aun así dejar al cliente congelado y la sesión activa como
  fantasma; reiniciar el contenedor libera el cupo.
- El código de `/setup` se restablece al reiniciar el contenedor.
- TShock escribe la IP real del jugador en los logs del contenedor; la redacción del gateway no
  cubre esa salida.

### No objetivos

- Ejecutar MCP dentro del proceso de TShock.
- Convertir el gateway en un panel de administración web.
- Depender de un proveedor de IA concreto.
- Añadir `rawcmd` sin allowlist y confirmación.
- Publicar tokens o configuración real en el repositorio.
