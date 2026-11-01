# Arquitectura

## Visión general

`tshock-mcp` es un proceso local de .NET 8 que actúa como **puerta de enlace** entre un anfitrión
MCP y la API REST de TShock. No se instala dentro del servidor de Terraria, no modifica TShock y no
necesita plugins adicionales para la Fase 0: se comunica con la API REST que TShock ya expone. La
Fase 1 agrega un plugin opcional que se describe en [bridge.md](bridge.md).

```
+------------------------------+        MCP (JSON-RPC sobre stdio)        +------------------------------+
| Anfitrión de IA              |  ----------------------------------->   | tshock-mcp                   |
| Claude Desktop / VS Code /   |                                         |  - Servidor MCP (stdio)      |
| Cursor / cliente MCP         |  <-----------------------------------   |  - 18 herramientas           |
+------------------------------+        stdout: JSON-RPC, stderr: logs   |  - Cliente HTTP de TShock    |
                                                                         |  - Sobre de resultados       |
                                                                         +--------------+---------------+
                                                                                        | HTTP/HTTPS + token
                                                                                        | (query param)
                                                                         +--------------v---------------+
                                                                         | Servidor TShock              |
                                                                         |  - Terraria + TShock         |
                                                                         |  - REST API habilitada       |
                                                                         +------------------------------+
```

## Capas y estado actual

La Fase 0 está completa. La Fase 1 tiene el plugin, pero **no** la capa MCP que lo consume:

```
+------------------+   MCP/stdio   +----------------------+   REST 7878   +-------------+
| Cliente MCP      |<------------->| tshock-mcp           |<------------->| TShock      |
+------------------+               | 18 tools REST        |               +-------------+
                                   |                      |
                                   | (sin BridgeClient)   |
                                   +----------x-----------+
                                              | no implementado
                                   +----------+-----------+
                                   | AgentBridge 7879     |
                                   | regiones + eventos   |
                                   +----------------------+
```

Hoy el plugin solo se consulta con HTTP directo en pruebas. Ninguna tool MCP lo usa, así que el
agente no puede leer eventos ni operar regiones. El corte vertical pendiente es
`tool MCP -> BridgeClient -> HTTP /v1/* -> plugin`; ver [`testing.md`](testing.md).

Componentes internos:

| Componente | Responsabilidad |
| --- | --- |
| `Program.cs` | Construye el host, configura logging a stderr, registra el transporte stdio y descubre las herramientas por ensamblado. |
| `Configuration/TShockOptions.cs` | Lee y valida las variables de entorno; falla al arrancar si la configuración es inválida. |
| `TShock/ITShockClient` | Ejecuta peticiones GET contra la API REST, aplica timeout y traduce fallos a resultados tipados. |
| `TShock/ToolResults` | Serializa el sobre estable `ok` / `error`. |
| `Tools/ReadTools.cs` | 12 herramientas sin efectos secundarios. |
| `Tools/WriteTools.cs` | 6 herramientas de mutación, bloqueadas salvo que se habilite la escritura. |

## Por qué una puerta de enlace externa

Se evaluaron tres alternativas antes de fijar la Fase 0:

1. **Bifurcar (*fork*) TShock.** Obliga a mantener un servidor modificado, a seguir cada versión de
   Terraria y de TShock y a que cada operador instale un binario no oficial. El costo de
   mantenimiento y el riesgo de seguridad recaen sobre el proyecto.
2. **Plugin en proceso dentro de TShock.** Requiere cargar código de terceros en el servidor,
   comparte su dominio de fallos y ata el ciclo de vida del agente al del juego. Un fallo del
   plugin puede tumbar el servidor; una actualización de TShock puede romper el plugin.
3. **Puerta de enlace externa sobre la API REST.** Es la opción elegida: TShock ya expone las rutas
   necesarias con su propio modelo de permisos, el servidor no se modifica y el agente corre en un
   proceso separado con su propio ciclo de vida, límites y configuración.

Ventajas concretas:

- **Aislamiento de fallos.** Si el servidor MCP se cae, Terraria sigue funcionando.
- **Actualizaciones independientes.** El agente se actualiza sin tocar el servidor de juego.
- **Reutilización del modelo de permisos.** TShock sigue siendo la autoridad: el token y su grupo
  determinan qué rutas se permiten, además del filtro propio de escritura.
- **Despliegue simple.** Un binario y variables de entorno; también una imagen de contenedor.
- **Auditoría centralizada.** Todas las llamadas pasan por un único punto donde se puede registrar
  qué se pidió, con qué resultado y cuánto tardó.

## Flujo de una petición

1. El anfitrión MCP arranca `tshock-mcp` como proceso hijo y completa el handshake JSON-RPC por
   stdin/stdout.
2. El modelo invoca una herramienta, por ejemplo `player_info` con `player = "Alice"`.
3. El SDK de MCP deserializa los argumentos y llama al método correspondiente.
4. La herramienta valida el argumento. Si falta, llega vacío o tiene un tipo incorrecto, devuelve
   `invalid_argument` sin tocar la red.
5. Si es una herramienta de escritura y `TSHOCK_MCP_ALLOW_WRITE` es `false`, devuelve
   `write_disabled` sin tocar la red.
6. `ITShockClient` compone la URL sobre `TSHOCK_REST_URL`, agrega `token` como parámetro de consulta
   y ejecuta un `GET` con el timeout configurado.
7. La respuesta se lee con un límite de tamaño, se parsea como JSON y se redactan las credenciales
   siempre y las IP según la configuración.
8. `ToolResults` envuelve el resultado y lo devuelve como cadena JSON. El SDK lo envía al anfitrión.
9. El registro de auditoría anota operación, ruta, estado HTTP y duración, sin argumentos.

No hay reintentos automáticos: las operaciones de escritura no son idempotentes y reintentar a
ciegas podría duplicar efectos. El modelo decide si vuelve a intentar.

## Mapeo de errores

| Código | Cuándo se produce | `httpStatus` | Acción sugerida |
| --- | --- | --- | --- |
| `write_disabled` | Se invocó una herramienta de escritura con `TSHOCK_MCP_ALLOW_WRITE` desactivado. | ausente | Habilitar la variable y reiniciar el anfitrión. |
| `invalid_argument` | Falta un argumento obligatorio, llega vacío o tiene un tipo incorrecto. No se hizo ninguna petición HTTP. | ausente | Corregir la llamada. |
| `rest_error` | TShock respondió con un estado HTTP de error (401, 403, 404, 500…), o con estado 200 y un campo `error` en el cuerpo. El mensaje es el texto de TShock, saneado. | presente (puede ser `200`) | Revisar token, permisos y disponibilidad del servidor. |
| `network_error` | No se pudo establecer la conexión (DNS, conexión rechazada, TLS). | ausente | Verificar que TShock esté activo y la URL sea correcta. |
| `timeout` | Se superó `TSHOCK_MCP_TIMEOUT_SECONDS`. | ausente | Subir el timeout o revisar la carga del servidor. |
| `cancelled` | El anfitrión canceló la operación. | ausente | Ninguna; es una cancelación deliberada. |
| `invalid_response` | El cuerpo no es JSON válido o no tiene la forma esperada. | presente | Revisar la versión de TShock o un proxy intermedio. |
| `response_too_large` | La respuesta excede el límite interno de lectura. | presente | Evitar rutas que devuelvan volúmenes masivos. |
| `unexpected_error` | Cualquier fallo no clasificado. | ausente | Revisar stderr; puede ser un defecto del servidor MCP. |

Reglas del sobre:

- Éxito: `{"ok":true,"data":<respuesta de TShock>}`. `data` puede ser `null` si TShock responde sin
  cuerpo.
- Error: `{"ok":false,"error":{"code":"...","message":"...","httpStatus":N}}`. `httpStatus` solo
  aparece cuando existe una respuesta HTTP asociada.
- Los mensajes de error se escriben en inglés, son breves y nunca incluyen el token ni cuerpos
  completos de respuesta.

## Decisiones de diseño

- **GET para todo.** La API REST de TShock usa GET incluso para mutaciones. El servidor MCP no
  convierte estas llamadas en POST: refleja el contrato real de TShock.
- **Token por variable de entorno.** Evita archivos de configuración versionados y permite que el
  anfitrión lo inyecte en el entorno del proceso hijo.
- **Un único punto de salida.** Toda respuesta pasa por `ToolResults`, lo que mantiene el contrato
  estable para cualquier anfitrión.
- **Validación al arrancar.** Una URL no absoluta o con consulta, fragmento o credenciales
  embebidas, un token ausente, un timeout no numérico o fuera de `1..300`, o HTTP sin cifrar hacia un
  host remoto impiden el arranque con un mensaje claro en stderr. La configuración nunca se corrige
  ni se completa en silencio.
- **El servidor no interpreta el contenido.** No resume, no filtra por heurísticas y no ejecuta nada
  de lo que devuelve TShock; solo redacta IP cuando corresponde.

## Fuera de alcance en la Fase 0

- Transporte HTTP y OAuth 2.1 (Fase 2).
- Puente con TShock para regiones, eventos y descubrimiento de comandos (Fase 1). Ya existe un
  plugin separado con regiones y eventos; el gateway todavía no lo consume. Su contrato está en
  [bridge.md](bridge.md).
- Ejecución de comandos arbitrarios o `rawcmd`.
- Apagado o reinicio del servidor de Terraria.
- Alta, baja o modificación de usuarios, grupos y prohibiciones.
- WebSockets, notificaciones push o eventos en tiempo real. El puente de Fase 1 expone consulta por
  cursor, no push.
- Persistencia propia: el servidor MCP no guarda estado entre ejecuciones, salvo el registro de
  auditoría si se configura.
- Reintentos, caché de respuestas y agregación de varias llamadas.

Cualquier propuesta que cruce estos límites debe discutirse como cambio de fase, no como parche
dentro de la Fase 0. El plan de las fases siguientes y el contexto para retomar el proyecto están en
[roadmap.md](roadmap.md).
