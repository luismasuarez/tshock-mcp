# Roadmap y contexto de continuación

Este documento es el punto de entrada para retomar el proyecto en una sesión nueva. Resume lo que
ya existe, las decisiones que no conviene volver a discutir, el plan de las fases siguientes y los
detalles de entorno que no son evidentes desde el código.

- **Repositorio:** https://github.com/luismasuarez/tshock-mcp
- **Estado:** Fase 0 completada y verificada contra un servidor TShock real (TShock 6.2.1.0,
  Terraria 1.4.5.8, cliente en la LAN). Fase 1 en curso: el plugin `TShock.AgentBridge` carga y
  expone regiones y eventos con cursor; `events_since` ya consume el puente desde MCP.
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
| Pruebas | 213 pruebas xUnit (182 del gateway y 31 del puente) y 52 comprobaciones e2e por stdio |
| Verificación real | Lectura, escritura, errores y redacción probados con un cliente de Terraria en la LAN |
| Privilegio mínimo | Grupo dedicado `mcp-rest` sin permisos de escritura; la escritura tiene dos barreras (flag del gateway y permisos de TShock) |
| Distribución | Dockerfile multi-stage y workflow de CI |
| Puente Fase 1 | Plugin `TShock.AgentBridge` cargado en TShock 6.2.1.0; regiones y eventos con cursor verificados por HTTP. `events_since`, `regions_list`, `region_info`, `region_create` y `region_delete` implementados; comandos pendientes |

### Estado por capas de la Fase 1

La capa MCP es la única que cuenta como disponible para el agente. Hoy no hay ninguna tool del
puente, así que regiones y eventos **no** están disponibles desde el cliente MCP.

| Capa | Estado | Verificado por |
| --- | --- | --- |
| Plugin: regiones y eventos | Implementado | HTTP directo y unitarias del buffer |
| `BridgeClient` en el gateway | Implementado | Unitarias con HTTP falso y e2e |
| Tool `events_since` | Implementada | E2E MCP por stdio con puente falso |
| Tools de región de lectura | Implementadas | Unitarias y E2E con puente falso |
| Tools de región de escritura | Implementadas | Unitarias, E2E con confirmación y elicitación |
| Política de propietario | Implementada en el plugin | Unitarias del puente |
| E2E MCP del puente | Existe para `events_since` | `mcp_stdio_smoke.py` con puente falso |
| Verificación real del plugin | Parcial: región, `player.join` y `command` | HTTP directo; faltan chat, muerte, jefe y salida |

El detalle de capas y criterios está en [`testing.md`](testing.md).

### Lo que no existe todavía

- Transporte HTTP y OAuth 2.1.
- Herramientas MCP de región (`regions_*`) y de comandos (`commands_list`, `config_get`,
  `run_command`).
- Descubrimiento y ejecución de comandos con allowlist.
- Notificaciones push o streaming de eventos.
- Ejecución de comandos arbitrarios, apagado, mutación de usuarios o prohibiciones.
- Publicación en GitHub: el repositorio es local y no tiene remoto configurado.

### Verificación de referencia

```bash
cd tshock-mcp
dotnet build -c Release
dotnet test -c Release --no-build
python3 tests/e2e/mcp_stdio_smoke.py
```

Resultados esperados: 0 advertencias, 213 pruebas superadas y `E2E PASSED` con 52 comprobaciones.
La prueba e2e cubre las 18 tools REST de la Fase 0 y las 5 tools del puente contra un puente falso.

---

## 2. Decisiones tomadas (no re-litigar sin motivo nuevo)

| Decisión | Motivo | Consecuencia |
| --- | --- | --- |
| Repositorio independiente, no un fork | El gateway no modifica TShock; el fork solo se usa para PRs upstream | Ciclo de release propio y CI ligero |
| Gateway externo, no plugin en proceso | Aísla fallos, evita cargar ASP.NET dentro de TShock y desacopla el SDK MCP | La Fase 0 no necesita TShock instalado para compilar |
| Licencia GPL-3.0-or-later | El plugin de Fase 1 enlaza `TShockAPI`, que es GPL | Un solo repo y una sola licencia |
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
| `RegionService.cs` | Regiones sobre el dispatcher |
| `EventBuffer.cs` | Ring buffer de eventos con cursor monótono |
| `EventService.cs` | Suscripción a los hooks de TShock y publicación de eventos |
| `CommandCatalog.cs` | Lectura de `Commands.ChatCommands` y allowlist |
| `BridgeClient.cs` (gateway) | Cliente HTTP del bridge, con detección y timeout |
| `Tools/BridgeTools.cs` (gateway) | Tools de regiones, eventos y comandos |

### API del bridge

Implementados hoy: `GET /v1/health`, `GET /v1/world`, las cuatro rutas de regiones y
`GET /v1/events`. El resto de la tabla es futuro. El contrato detallado, incluido el cursor, está en
[`bridge.md`](bridge.md).

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

Implementado en el plugin y verificado contra el servidor real para eventos de región, de entrada y
de comando. El gateway lo expone como la herramienta MCP `events_since`, que consume
`GET /v1/events` a través de `BridgeClient`.

- Ring buffer en memoria de 10 000 eventos; el cursor `long` empieza en `1` y es exclusivo.
- `GET /v1/events?cursor=&limit=`: `limit` entre 1 y 500, 100 por defecto. Omitir `cursor` devuelve
  todo el backlog retenido, lo que permite arrancar un cliente nuevo aunque el ring haya dado la
  vuelta.
- La respuesta incluye `events`, `cursor` (último de la página), `head`, `oldest`, `hasMore` y
  `session`.
- Si el cursor es anterior a lo retenido o posterior a `head`, se devuelve `410 cursor_expired` con
  `reason` (`evicted` o `unknown`), `oldest`, `head` y `resumeCursor = oldest - 1`.
- Un cursor de una sesión anterior (reinicio del contenedor) cae en `unknown`; nunca se interpreta
  como «no hay eventos». `session` cambia en cada arranque.
- Tipos iniciales: `player.join`, `player.leave`, `chat`, `command`, `player.death`, `boss.kill`,
  `region.created`, `region.deleted`.
- Todo contenido generado por jugadores se marca con `"untrusted": true`.
- `command` guarda solo el nombre del comando: los argumentos se descartan a propósito porque
  `/login` y `/register` llevan contraseñas.
- No hay notificaciones push en Fase 1; el cliente guarda el cursor y consulta.

El contrato completo, con ejemplos y límites de longitud, está en [`bridge.md`](bridge.md).

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

### Estado de la implementación

Primer corte ya construido y probado contra el servidor real:

| Pieza | Archivo | Estado |
| --- | --- | --- |
| Plugin | `src/TShock.AgentBridge/AgentBridgePlugin.cs` | `[ApiVersion(2, 1)]`, `Order = 10`, arranque defensivo, token desde archivo o variable |
| Servidor HTTP | `src/TShock.AgentBridge/BridgeServer.cs` | `HttpListener` en `0.0.0.0:7879`, bearer con comparación en tiempo constante, `401`/`404`/`500` sin filtrar trazas |
| Dispatcher | `src/TShock.AgentBridge/MainThreadDispatcher.cs` | Cola `BlockingCollection`, timeout de 5 s, pump por `GameUpdate` y por temporizador cuando el servidor está en vacío |
| Regiones | `src/TShock.AgentBridge/RegionService.cs` | Listar, leer, crear y eliminar sobre el hilo del juego |
| Eventos | `src/TShock.AgentBridge/EventBuffer.cs` y `EventService.cs` | Ring buffer de 10 000 eventos, cursor monótono, suscripción a hooks de jugador, chat, comandos, muerte, jefes y regiones |
| Endpoints | — | `GET /v1/health`, `GET /v1/world`, `GET /v1/events`, y regiones: `GET /v1/regions`, `GET /v1/regions/{name}`, `POST /v1/regions`, `DELETE /v1/regions/{name}` |

Todo lo anterior es **capa plugin**. La única ruta que el agente puede invocar hoy es
`GET /v1/events`, a través de la tool `events_since`; el resto sigue sin consumidor MCP.

Despliegue usado en la prueba:

```bash
cp src/TShock.AgentBridge/bin/Release/net9.0/TShock.AgentBridge.dll \
   "$HOME/.local/share/tshock-mcp/plugins/"

docker run -d --name tshock-test \
  -p <host-ip>:7777:7777 \
  -p 127.0.0.1:7878:7878 \
  -p 127.0.0.1:7879:7879 \
  -v "$DATA/tshock:/tshock" -v "$DATA/worlds:/worlds" -v "$DATA/plugins:/plugins" \
  -v "$HOME/.config/tshock-mcp/bridge-token:/run/secrets/bridge-token:ro" \
  -e TSHOCK_BRIDGE_TOKEN_FILE=/run/secrets/bridge-token \
  ghcr.io/pryaxis/tshock:latest \
  -world /worlds/Test.wld -port 7777 -maxplayers 8
```

La imagen ya pasa `-additionalplugins /plugins`, así que basta con copiar el DLL. El token del
puente es distinto del token REST y se monta como archivo para que no aparezca en `docker inspect`.
La carga se confirma con `docker logs tshock-test | grep "TShock.AgentBridge"`; no hay recarga en
caliente: un cambio de plugin requiere reiniciar el contenedor.

Mediciones con un jugador conectado, frente al servidor vacío:

| Escenario | Ticks | Latencia de `/v1/world` |
| --- | --- | --- |
| Servidor vacío | ~1/s (pump de respaldo) | ~0,5 s |
| Jugador conectado | ~60/s (hook `GameUpdate`) | ~0,01 s |

Con 20 peticiones concurrentes a `/v1/world` respondieron `200` las 20, sin errores nuevos en el
log. La comprobación de que el pump de respaldo queda inactivo es el propio contador: 304 ticks en
5 segundos no deja sitio a un temporizador de 1 Hz.

### Plan de trabajo

- [x] Instalar .NET 9 SDK en la máquina (TShock 6.2.1 apunta a `net9.0`).
- [x] Crear `src/TShock.AgentBridge/TShock.AgentBridge.csproj` con referencias a TShock 6.2.1 y TSAPI.
- [x] Implementar `AgentBridgePlugin` con arranque y parada idempotentes.
- [x] Implementar `MainThreadDispatcher` y probarlo con un work item que lea `Main.worldName`.
- [x] Implementar `BridgeServer` con `/v1/health` y autenticación bearer.
- [x] Añadir regiones (listar, leer, crear, eliminar).
- [x] Añadir `EventBuffer`, `EventService` y `GET /v1/events` con cursor.
- [x] Añadir la suite xUnit del ring buffer (23 pruebas, `tests/TShock.AgentBridge.Tests`).
- [x] Añadir `BridgeClient` y `BridgeTools` al gateway; empezar por `events_since`.
- [x] Añadir la prueba MCP por stdio del puente (`events_since`) con un puente falso.
- [x] Añadir las tools de región de lectura (`regions_list`, `region_info`).
- [x] Añadir la escritura de regiones, con política de propietario en el plugin y confirmación al borrar.
- [ ] **Siguiente:** añadir `CommandCatalog` y `GET /v1/commands`.
- [ ] Añadir `CommandCatalog` y `/v1/commands`.
- [ ] Probar en un servidor TShock real la recarga, la desconexión del bridge y la concurrencia de
      eventos con jugadores conectados.
- [ ] Verificar por MCP, no por `curl`, los eventos de chat, muerte, jefe y salida.

### Criterios de aceptación

| Criterio | Estado |
| --- | --- |
| La suite de Fase 0 sigue en verde sin cambios de contrato. | Hecho: 213 pruebas y 52 checks e2e. |
| El plugin carga en TShock 6.2.1 y se descarga sin dejar hilos ni puertos abiertos. | Hecho para la carga; la descarga en caliente no existe. |
| Crear y eliminar una región desde MCP se refleja en el servidor. | Hecho y verificado contra TShock 6.2.1.0, con elicitation real. |
| `events_since` devuelve eventos ordenados y estables entre reconexiones. | Hecho con puente falso (unitarias y e2e); falta la verificación con jugadores reales. |
| Un bridge caído no impide usar las tools REST. | Hecho y probado en la e2e: `bridge_unavailable` sin afectar a REST. |
| Ninguna mutación se ejecuta fuera del hilo principal. | Hecho en las rutas de región del plugin. |
| La política de propietario la aplica el servidor. | Hecho: `owner_not_allowed` verificado en vivo con la lista vacía. |
| El token del bridge no aparece en logs ni en respuestas. | Verificado en el plugin y en la e2e del gateway. |
| Toda tool del puente tiene prueba MCP por stdio. | Hecho para `events_since`; las tools nuevas lo heredan como requisito. |

### Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Deadlock entre el hilo HTTP y el hilo del juego | Timeout de 5 s y prohibición de esperas síncronas dentro del pump |
| Cambios de API entre versiones de TShock | Fijar 6.2.1, compilar contra NuGet y probar en un servidor real |
| Volumen de eventos | Ring buffer acotado, límite por llamada y `cursor_expired` |
| Escalada por comandos | Allowlist vacía, permisos del actor y confirmación |
| Doble fuente de verdad (REST y bridge) | El bridge manda si está disponible; el gateway documenta y prueba la degradación |

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

- Un cliente remoto completa el handshake y lista el catálogo completo (18 tools REST y las del
  puente disponibles en ese momento).
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
| Retención de eventos | Memoria, JSONL, ambos | Ring buffer en memoria implementado; JSONL opcional queda para después |
| Ejecución de comandos | Todo, allowlist, nada | Allowlist vacía por defecto |
| Nombre y publicación | `tshock-mcp` comunitario u organización oficial | Publicar como comunitario y consultar antes de usar el nombre oficial |
| Migración a .NET 9 | Gateway net8 + plugin net9, o todo net9 | Gateway net8 hasta que el plugin estabilice |
| Confirmaciones destructivas | Elicitation MCP, parámetro `confirm`, ambos | Ambos, con fallback si el host no soporta elicitation |

---

## 8. Cómo retomar en una sesión nueva

### Orden de lectura

1. `AGENTS.md` — invariantes, definición de terminado y reglas para agentes.
2. `README.md` — uso y estado de cara al usuario.
3. `docs/roadmap.md` — este documento: estado por capa, decisiones y plan real.
4. `docs/testing.md` — capas de prueba y qué se considera aceptación.
5. `docs/architecture.md` — diseño de la Fase 0 y capas actuales.
6. `docs/bridge.md` — contrato HTTP del plugin de Fase 1.
7. `docs/security.md` — modelo de amenazas.
8. `docs/local-testing.md` — guía para levantar TShock y probar.

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
  TShock/HttpPayload.cs          Lectura acotada y saneado compartido
  TShock/BridgeClient.cs         Cliente HTTP del puente y códigos de error
  Tools/ReadTools.cs             12 tools de lectura
  Tools/WriteTools.cs            6 tools de escritura
  Tools/BridgeTools.cs           5 tools del puente (eventos, regiones y escritura)
src/TShock.AgentBridge/          Plugin de Fase 1 (net9.0)
  AgentBridgePlugin.cs           Arranque, hooks de ciclo de vida
  BridgeServer.cs                HTTP loopback, auth y rutas
  MainThreadDispatcher.cs        Cola de trabajo del hilo del juego
  RegionService.cs               Regiones
  EventBuffer.cs                 Ring buffer y contrato de cursor
  EventService.cs                Suscripción a hooks y publicación
  RegionOwnerPolicy.cs           Política de propietario configurada por el admin
tests/
  TShock.MCP.Server.Tests/       Suite xUnit del gateway (181)
  TShock.AgentBridge.Tests/      Suite xUnit del puente (25)
  e2e/mcp_stdio_smoke.py         Prueba de extremo a extremo
docs/                            Arquitectura, bridge, tools, testing, seguridad, roadmap
.github/pull_request_template.md Checklist de la definición de terminado
docker/                          Imagen y ejemplo para Claude Desktop
```

### Entorno

- **.NET SDK instalado:** 8.0.426 y 9.0.320, ambos en `~/.dotnet`.
- **.NET 9:** requerido por el plugin de Fase 1; el gateway sigue en `net8.0`.
- **Docker:** 29.4.1 disponible.
- **TShock de referencia:** 6.2.1, `net9.0`, REST por defecto en el puerto 7878.
- **MCP C# SDK:** 2.3.0 (`ModelContextProtocol`), compatible con `net8.0`.
- **Spec MCP:** 2026-07-28, sin sesiones de protocolo.

### Primeras tareas sugeridas

1. Añadir las tools de región, con escritura condicionada y confirmación al borrar.
2. Verificar por MCP los eventos de chat, muerte, jefe y salida con un jugador conectado.
3. Añadir `CommandCatalog` y `GET /v1/commands`.
4. Traducir y verificar `config_get` y, por último, `run_command` con allowlist.

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
- Un servidor TShock **sin jugadores no ejecuta el tick del juego**: `Main.time` no avanza y
  `ServerApi.Hooks.GameUpdate` no se dispara. Un `MainThreadDispatcher` que dependa solo de ese hook
  se queda esperando y expira. El puente usa un pump de respaldo por temporizador que actúa solo
  cuando el contador de ticks lleva más de un segundo estático, es decir, cuando no hay estado del
  mundo mutando en paralelo.
- `RegionManager.ListAllRegions(worldid)` **solo selecciona `RegionName`** y devuelve objetos
  `Region` con el resto de campos en cero. Para listar con datos reales hay que leer
  `TShock.Regions.Regions` y filtrar por `WorldID`; `GetRegionByName` sí devuelve el registro
  completo porque trabaja sobre la lista en memoria.
- `RegionManager.DeleteRegion(name)` devuelve `true` aunque no exista la fila; hay que comprobar
  `GetRegionByName` antes para poder responder `404`.
- `RegionManager.DeleteRegion` dispara `RegionHooks.OnRegionDeleted` **aunque no haya fila**, con
  `Region` nulo. El consumidor debe comprobar `args.Region is null` antes de publicar el evento.
- `PlayerHooks.PlayerPostLogin` no es un hook general de entrada: solo se dispara en los flujos de
  cuenta autenticada. Para `player.join` hay que usar `ServerApi.Hooks.NetGreetPlayer`, que corre
  cuando el nombre ya es definitivo.
- TShock registra su manejador de `ServerLeave` con `Order = 0` y vacía `Players[who]` antes que los
  plugins con `Order` mayor. Un manejador tardío no puede leer el nombre desde `TShock.Players`; el
  puente guarda un roster alimentado por `NetGreetPlayer`.
- `PlayerCommandEventArgs.Text` y `CommandText` incluyen los argumentos. No registrarlos: `/login` y
  `/register` llevan contraseñas y `/msg` texto privado. Guardar solo `CommandName`.
- Los hooks `PlayerHooks` y `RegionHooks` son eventos estáticos con delegados anidados; se pueden
  suscribir desde un plugin externo, pero hay que dar de baja cada delegado en `Dispose` para no
  dejar referencias al plugin descargado.
- El `HttpListener` responde `411` a un `POST` sin `Content-Length` antes de llegar al handler; para
  probar el `405` hay que enviar un cuerpo vacío (`--data ''`).
- Un cursor de una sesión anterior no es «sin eventos»: tras reiniciar el plugin los cursores vuelven
  a empezar en `1`. El endpoint responde `410 cursor_expired` con `reason = "unknown"` para que el
  cliente reanude desde `resumeCursor`.

### Continuación: regiones y eventos (1 de noviembre de 2026)

Lo construido en esta sesión:

- `EventBuffer`: ring buffer de 10 000 eventos, cursor `long` exclusivo, expiración por descarte y
  detección de cursores de una sesión anterior.
- `EventService`: suscripción a `NetGreetPlayer`, `ServerLeave`, `PlayerChat`, `PlayerCommand`,
  `GetDataHandlers.KillMe`, `NpcKilled`, `RegionCreated` y `RegionDeleted`; roster de nombres para
  sobrevivir al orden de los manejadores de `ServerLeave`.
- `GET /v1/events?cursor=&limit=` con `200`, `400` (`invalid_cursor`, `invalid_limit`), `401` y
  `410 cursor_expired`.
- Suite `tests/TShock.AgentBridge.Tests` con 23 pruebas del buffer y del truncado; el total sube a
  132 (175 tras el corte de `events_since`).
- `docs/bridge.md` con el contrato completo.

Verificado contra el servidor real (TShock 6.2.1.0):

- `/v1/health` con el bloque `events` (sesión, `head`, `oldest`, `retained`).
- Crear y eliminar una región genera `region.created` y `region.deleted` con cursores 1 y 2,
  `untrusted: true` y el nombre esperado.
- Paginación con `limit=1`, `hasMore` y cursor exclusivo.
- `400` para cursor y límite inválidos, `401` sin bearer, `405` con métodos distintos de `GET`.
- `410` con `reason = "unknown"` para un cursor posterior a `head`.
- Reinicio del contenedor: `session` cambia, `head` vuelve a 0 y un cursor viejo cae en `410`.
- El token del puente no aparece en los logs del contenedor ni en `docker inspect`.

Durante la sesión se conectó un jugador real, lo que permitió verificar además:

- `player.join` se emitió al saludar, con el nombre definitivo.
- `command` registró `/setup` **solo con el nombre del comando**; el código de configuración, que es
  un secreto, no apareció en el evento. Es la comprobación real de que los argumentos se descartan.

Pendiente de verificación con un cliente: `player.leave`, `chat`, `player.death` y `boss.kill`. Los
hooks están registrados y compilan contra TShock 6.2.1.0; faltan las acciones en vivo (hablar, morir,
matar un jefe y desconectarse).

### Continuación: `BridgeClient` y `events_since` (1 de noviembre de 2026)

Primer corte vertical MCP → puente. Lo construido:

- `TShockOptions` lee `TSHOCK_BRIDGE_URL`, `TSHOCK_BRIDGE_TOKEN`, `TSHOCK_BRIDGE_TOKEN_FILE` y
  `TSHOCK_BRIDGE_TIMEOUT_SECONDS`. Sin ninguna de ellas el puente queda deshabilitado y el gateway
  arranca igual; una configuración parcial es un error de arranque, no un valor de reserva.
- `TShock/HttpPayload.cs` centraliza la lectura acotada a 5 MB, el rechazo de claves JSON duplicadas
  y el truncado; `TShockClient` lo reutiliza sin cambios de comportamiento.
- `TShock/BridgeClient.cs` con bearer por petición, sin token en la URL, auditoría JSONL y los
  códigos `bridge_unavailable`, `bridge_timeout`, `bridge_auth_error`, `bridge_error` y
  `cursor_expired`.
- `Tools/BridgeTools.cs` con `events_since`, registrada siempre: sin puente responde
  `bridge_unavailable` en lugar de desaparecer del catálogo.
- `ToolResults.Error` acepta un objeto `details` opcional dentro de `error`. Es aditivo y conserva
  `code`, `message` y `httpStatus`. `cursor_expired` lo usa para devolver `reason`, `oldest`, `head`,
  `resumeCursor` y `session`.
- 43 pruebas nuevas en el gateway (de 109 a 152) y 14 checks nuevos en la e2e (de 17 a 31), incluida
  la degradación: con el puente apagado, `server_status` sigue respondiendo.

Trampas de esta sesión:

- El binder de argumentos del SDK resuelve bien los parámetros opcionales (`long?`, `int?`): la
  validación de rango tiene que vivir en el cuerpo de la tool, no en el binder.
- `HttpClient.Timeout` vuelve a manifestarse como `TaskCanceledException`; sin comprobar
  `cancellationToken.IsCancellationRequested` el agente recibe `bridge_timeout` cuando en realidad
  canceló él.
- Un mensaje de `HttpRequestException` real suele traer la URL completa; el saneado debe reemplazar
  también la autoridad, no solo la ruta.
- Verificar `Content-Length` antes de leer evita descargar un cuerpo enorme solo para rechazarlo.
- Los tests de configuración comparten el entorno del proceso: las variables nuevas tienen que
  entrar en `EnvironmentScope.Names` o se filtran entre pruebas.

### Continuación: lectura de regiones desde MCP (1 de noviembre de 2026)

- `regions_list` y `region_info` sobre `GET /v1/regions` y `GET /v1/regions/{name}`.
- Código nuevo `region_not_found` (404) solo para «la región pedida no existe». Un fallo del puente
  sigue siendo `bridge_error`; la distinción permite al agente ofrecer una corrección en vez de
  reintentar.
- Validación antes de la red: nombre vacío, de más de 128 caracteres, con `/` o con caracteres de
  control devuelve `invalid_argument` y no llega al puente. El nombre se escapa como segmento único.
- 9 pruebas nuevas (161 en el gateway) y 5 checks nuevos (36 en total).
- Bug de contrato encontrado al verificar en vivo: el puente serializaba los registros de región en
  PascalCase (`"Name"`) mientras los eventos iban en camelCase. La e2e con puente falso no lo
  detectaba porque el falso ya usaba camelCase. Corregido con una política de serialización común en
  `BridgeServer`, más 2 pruebas de contrato en el proyecto del puente (25 en total).

Verificado contra el servidor real **por MCP**, con el binario real y los secretos leídos de archivos:

- El catálogo expone 19 tools en ese momento y `events_since` devuelve el backlog real.
- Los ocho tipos de evento quedaron capturados por MCP contra el servidor real: `player.join`,
  `player.leave`, `chat`, `command`, `boss.kill` (King Slime, `netId` 50) y `player.death`
  (`pvp: false`). `region.created` y `region.deleted` ya se habían verificado en la sesión anterior
  por HTTP.
- `player.death` se generó con una muerte real en el juego. `kill_player` por REST devuelve `403`:
  el grupo `mcp-rest` no tiene `tshock.rest.kill`, que es el privilegio mínimo funcionando.
- El token del puente no aparece en `stderr`.

Trampa: un apagado del contenedor **no** emite `player.leave`. TShock no dispara el hook de salida
durante el shutdown, así que tras un reinicio lo primero que se ve es el `player.join` de la
reconexión.

### Continuación: escritura de regiones y confirmación (1 de noviembre de 2026)

- `region_create` y `region_delete` en el gateway, con `write_disabled` en runtime y validación antes
  de la red.
- `BridgeClient` gana `PostAsync`/`DeleteAsync` sobre un único `SendCoreAsync`; un `204` con cuerpo
  vacío es éxito y un `403` ya no se confunde con un fallo de token (solo el `401` lo es).
- **Confirmación por elicitation.** `region_delete` usa `InputRequiredException` con
  `InputResponse`/`ElicitResult`. El SDK la convierte en `elicitation/create` en sesiones con estado y
  en un *input-required result* en el modo sin estado, así que una sola implementación cubre ambos
  protocolos. El filtro de `Program.cs` reenvía `InputRequiredException` en lugar de convertirla en
  `unexpected_error`.
- **Capacidades por petición.** La comprobación es `server.IsMrtrSupported &&
  server.ClientCapabilities?.Elicitation is not null`. Leerlas de `context.JsonRpcRequest.Context`
  no funciona en protocolos anteriores a 2026-07-28, donde viven en la sesión.
- **Política de propietario en el plugin** (`RegionOwnerPolicy`): `TSHOCK_BRIDGE_REGION_OWNER` y
  `TSHOCK_BRIDGE_REGION_OWNERS`. El admin decide a quién se le puede asignar una región; el modelo no.
- Códigos nuevos: `region_exists` (409), `owner_not_allowed` (403), `confirmation_required`,
  `confirmation_declined`.
- 26 pruebas nuevas en el gateway (181) y 6 en el puente (31); la e2e sube a 52 comprobaciones,
  incluida la elicitación aceptada y rechazada de extremo a extremo.
- `InternalsVisibleTo` en el proyecto del gateway para probar decisiones puras, igual que ya hacía el
  proyecto del puente.

Trampas de esta sesión:

- `McpServer` es también el nombre del espacio de nombres raíz del gateway (`TShock.McpServer`): en
  `Tools/` hay que escribir `ModelContextProtocol.Server.McpServer`.
- El cliente declara elicitation en `initialize` para protocolos anteriores a 2026-07-28; pedir la
  capacidad en el `_meta` de la petición es solo para el protocolo nuevo.
- Un `403` del puente es política (`owner_not_allowed`), no autenticación: mapearlo a
  `bridge_auth_error` esconde información que el agente necesita.
- `InputRequiredException` debe reenviarse desde el filtro de errores; si el filtro la captura, el
  humano nunca ve la pregunta.

### Continuación: verificación real de regiones (1 de noviembre de 2026)

Tras desplegar el plugin con la política de propietario y el casing corregido, verificado **por MCP**
con el binario real y los secretos leídos de archivos:

- `regions_list` devuelve camelCase y el mundo vacío después de limpiar.
- `region_create` sin `owner` crea con `mcp-gateway`.
- Repetir el nombre devuelve `region_exists` (409).
- `owner: "mallory"` y `owner: "alice"` devuelven `owner_not_allowed` (403) con la lista vacía: el
  default seguro funciona.
- `region_info` devuelve el registro completo tras crearlo.
- `region_delete` sin confirmación devuelve `confirmation_required`; con `confirm: true` borra.
- Repetir el borrado devuelve `region_not_found`.
- Elicitation real: el cliente declaró la capacidad, el servidor preguntó y el borrado se completó
  solo tras aceptar.
- `events_since` emitió `region.created` y `region.deleted` con sus cursores.
- El token del puente no apareció en `stderr`.

Validación final con el **servidor MCP del host** (OpenCode), no con arneses externos: 23 tools en el
catálogo, `write_disabled` con la escritura apagada, creación con propietario por defecto,
`owner_not_allowed`, `confirmation_required`, borrado con `confirm` y `region_not_found`.

Trampa: OpenCode declara `elicitation` pero el diálogo no llega a la ejecución en modo Code, así que
`region_delete` se queda esperando respuesta. Se añadió `TSHOCK_MCP_ALLOW_ELICITATION` (default
`true`); con `false` la tool exige `confirm: true` y no pregunta. El camino de elicitation sí está
probado de extremo a extremo en la e2e y contra el servidor real con un cliente que responde.

Pendiente de la fase: `CommandCatalog` y `GET /v1/commands`.

### No objetivos

- Ejecutar MCP dentro del proceso de TShock.
- Convertir el gateway en un panel de administración web.
- Depender de un proveedor de IA concreto.
- Añadir `rawcmd` sin allowlist y confirmación.
- Publicar tokens o configuración real en el repositorio.
