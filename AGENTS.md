# AGENTS.md

Reglas para agentes de IA que trabajen en este repositorio. Léalas antes de modificar código o
documentación.

## Idioma

- **Prosa** (documentación, mensajes de chat, artefactos de diseño): español neutro, registro
  formal-neutral. Sin voseo ni regionalismos.
- **Código** (fuentes, comentarios, identificadores, mensajes de commit, nombres de variables,
  claves de configuración, salida de logs): inglés.

## Invariantes

Estas reglas no se negocian. Se aplican a la Fase 0 y a todo lo que se agregue después, incluidas
las herramientas del puente de Fase 1. Romper cualquiera de ellas es un error de revisión:

1. **stdout es solo JSON-RPC.** Nunca escriba en stdout desde código de aplicación, bibliotecas ni
   diagnósticos. Todo log, advertencia y traza va a stderr.
2. **El token nunca se registra.** `TSHOCK_TOKEN` y el token del puente no deben aparecer en logs,
   mensajes de error, excepciones, trazas, el registro de auditoría ni en la salida de las
   herramientas.
3. **La escritura está cerrada por defecto.** Las 6 herramientas de escritura de la Fase 0 y toda
   herramienta de escritura que se agregue después (por ejemplo `region_create` o `region_delete`)
   solo se habilitan con `TSHOCK_MCP_ALLOW_WRITE=true`. La comprobación ocurre en tiempo de
   ejecución, no al arrancar.
4. **Sin `rawcmd` ni ejecución arbitraria.** No agregue herramientas que ejecuten comandos de
   consola, apaguen el servidor, muten usuarios, creen o eliminen prohibiciones sin una allowlist
   explícita y confirmación. En la Fase 0 esas operaciones no existen.
5. **El sobre de salida es estable.** Toda herramienta devuelve una cadena JSON con la forma
   `{"ok":true,"data":...}` o `{"ok":false,"error":{"code":"...","message":"...","httpStatus":...}}`.
   No cambie nombres de campos ni códigos de error existentes; agregue códigos nuevos solo si es
   imprescindible y documéntelos.
6. **Las IP se redactan por defecto** (`<redacted>`) salvo `TSHOCK_MCP_SHOW_IPS=true`. Se redactan
   los campos `ip` y cualquier valor con forma de IP. Los campos `token` y `password` se redactan
   siempre.
7. **La configuración es solo por variables de entorno.** No agregue flags de CLI ni archivos de
   configuración. La validación es estricta y ocurre al arrancar: una URL base con consulta,
   fragmento o credenciales embebidas, o un timeout no numérico, terminan el proceso. No agregue
   valores de reserva silenciosos.
8. **Los argumentos se validan antes de la red.** Un argumento faltante, vacío o de tipo incorrecto
   devuelve `invalid_argument` sin ninguna petición HTTP, tanto en las tools REST como en las del
   puente.

## Comandos

Desde la raíz del repositorio:

```bash
dotnet build -c Release
dotnet run --project src/TShock.MCP.Server
dotnet test
python3 tests/e2e/mcp_stdio_smoke.py   # requiere build previo

# plugin de Fase 1: compilar y desplegar
dotnet build src/TShock.AgentBridge -c Release
cp src/TShock.AgentBridge/bin/Release/net9.0/TShock.AgentBridge.dll \
   "$HOME/.local/share/tshock-mcp/plugins/"
docker restart tshock-test             # no hay recarga de plugins en caliente
```

No ejecute `dotnet run` para probar el protocolo a mano sin un cliente MCP: el proceso espera
JSON-RPC por stdin.

## Estructura

```
src/TShock.MCP.Server/
  Program.cs                     Arranque, host, logging a stderr, registro de herramientas
  Configuration/TShockOptions.cs Lectura y validación de variables de entorno
  TShock/                        Cliente REST, auditoría, redacción y sobre de resultados
  Tools/                         ReadTools.cs y WriteTools.cs
src/TShock.AgentBridge/          Plugin de Fase 1 (net9.0): bridge, dispatcher, regiones y eventos
tests/
  TShock.MCP.Server.Tests/       Suite xUnit del gateway
  TShock.AgentBridge.Tests/      Suite xUnit del puente
  e2e/mcp_stdio_smoke.py         Prueba de extremo a extremo
docs/                            Arquitectura, bridge, herramientas, pruebas, seguridad y roadmap
docker/                          Dockerfile y ejemplo para Claude Desktop
.github/workflows/build.yml      CI de compilación y pruebas
```

## Cómo agregar una herramienta REST

1. Confirme que la ruta existe en la API REST de TShock y que la Fase 0 la permite (sin comandos
   arbitrarios, sin mutaciones de usuarios ni prohibiciones).
2. Elija el archivo correcto: `Tools/ReadTools.cs` si no tiene efectos secundarios;
   `Tools/WriteTools.cs` si muta estado. Toda herramienta de escritura debe comprobar
   `options.AllowWrite` y devolver `write_disabled` cuando esté deshabilitada.
3. Declare el método con `[McpServerTool(Name = "...")]` y una `[Description]` en inglés, clara y
   breve. Use nombres en `snake_case` y argumentos con tipo y descripción.
4. Delegue la llamada HTTP en `ITShockClient` usando la ruta y los parámetros de consulta
   correspondientes. No construya `HttpClient` ad hoc ni concatene URLs con datos sin escapar.
5. Devuelva siempre el sobre mediante `ToolResults`. No devuelva objetos crudos ni texto libre.
6. Actualice `docs/tools.md` (tabla completa) y la tabla resumida del `README.md`.
7. Agregue pruebas en `tests/TShock.MCP.Server.Tests` y amplíe `tests/e2e/mcp_stdio_smoke.py` con
   la llamada MCP real. No compile a mano si otro agente está editando las fuentes; coordine
   primero.

## Cómo agregar una herramienta del puente

El puente de Fase 1 es otra capa: el gateway habla con el plugin por HTTP en loopback, no con la
API REST. Una tool del puente no está terminada hasta que se prueba por MCP.

1. Confirme que la ruta existe en `src/TShock.AgentBridge/BridgeServer.cs` y que está documentada
   en `docs/bridge.md`. Si no existe, primero se agrega y se prueba en el plugin.
2. Agregue el método correspondiente a `BridgeClient` (cliente HTTP del puente; todavía no existe,
   se crea con la primera tool). No construya `HttpClient` ad hoc; use el token del puente, timeout
   propio y límite de tamaño de respuesta. Nunca registre el token ni la URL completa con
   credenciales.
3. Declare la tool en `Tools/BridgeTools.cs` (el archivo se crea con la primera tool) con
   `[McpServerTool]`, nombre en `snake_case` y una `[Description]` en inglés. Separe lectura y
   escritura; las de escritura comprueban `options.AllowWrite` y devuelven `write_disabled`.
4. Valide los argumentos antes de la red y traduzca los errores del puente a códigos estables del
   sobre: `bridge_unavailable`, `bridge_timeout`, `bridge_auth_error`, `bridge_error` y
   `cursor_expired`. Devuelva siempre el sobre mediante `ToolResults`.
5. Para operaciones destructivas, exija confirmación explícita además de `TSHOCK_MCP_ALLOW_WRITE`.
   `region_delete` no es una excepción.
6. Actualice `docs/tools.md`, `docs/bridge.md` y la tabla resumida del `README.md`.
7. Agregue pruebas unitarias en `tests/TShock.MCP.Server.Tests` (con un manejador HTTP falso) y una
   prueba MCP por stdio con un puente falso. Cuando la tool toque TShock, verifique también contra
   el servidor real llamando a la tool, no con `curl`.

## Capas y pruebas: definición de terminado

Una funcionalidad del gateway se valida en capas. La prueba de aceptación es siempre la capa MCP;
las capas inferiores son diagnóstico, no cierre.

| Capa | Qué prueba | Cómo se ejecuta | ¿Es aceptación? |
| --- | --- | --- | --- |
| Unidad | Lógica pura, sin red: validación, envoltorio, redacción, ring buffer. | `dotnet test -c Release` | No |
| Plugin HTTP | Contrato del puente: rutas, cursores, errores, regiones. Requiere el plugin cargado. | Script directo contra `127.0.0.1:7879` | No |
| Gateway MCP | Una tool real por JSON-RPC sobre stdio. Fase 0: contra un TShock falso. Puente: contra un puente falso. | `python3 tests/e2e/mcp_stdio_smoke.py` | Sí |
| Servidor real | Integración completa: TShock, plugin, gateway y cliente MCP. | Arnés MCP con secretos desde archivo | Sí, cuando aplica |

Reglas:

- Una tool nueva sin prueba MCP por stdio se considera incompleta.
- Una prueba con `curl` al puente o a REST no reemplaza la prueba MCP. Sirve para aislar la capa
  que falla.
- La verificación contra el servidor real se hace llamando a la tool MCP, no a un endpoint
  interno.
- Toda prueba que use secretos los lee de archivos con permisos `600` o de variables inyectadas;
  nunca los imprime ni los pasa como argumento literal.
- El detalle de comandos y variables está en [docs/testing.md](docs/testing.md).

## Qué no cambiar en la Fase 0

- El transporte stdio y el hecho de que stdout transporta solo JSON-RPC.
- El conjunto de 18 herramientas y sus rutas. Las tools de Fase 1 son aditivas: no renombran,
  reemplazan ni cambian la semántica de estas 18.
- Los nombres y la semántica de las variables de entorno de la Fase 0.
- El sobre de resultados y los códigos de error documentados.
- La licencia GPL-3.0-or-later.
- `Directory.Build.props`, el `.sln` y los archivos de proyecto, salvo que la tarea lo pida
  explícitamente.

## Documentación

- Actualice la documentación en el mismo cambio que el código. Una herramienta nueva sin entrada en
  `docs/tools.md` se considera incompleta.
- Marque el trabajo futuro como futuro. No documente HTTP, OAuth, herramientas MCP de regiones o
  eventos, descubrimiento de comandos ni `rawcmd` como si existieran. El puente de Fase 1 existe y
  se documenta por separado en `docs/bridge.md`; el gateway todavía no lo consume.
- No declare una funcionalidad como disponible desde MCP hasta que existan la tool y su prueba MCP
  por stdio. «Implementado en el plugin» no es lo mismo que «disponible para el agente».
- Mantenga `docs/roadmap.md` como fuente de verdad del estado por capa. Si un cambio altera qué se
  puede usar desde MCP, actualice ese documento en el mismo cambio.
- Los comandos y criterios de prueba viven en [docs/testing.md](docs/testing.md); no invente un
  procedimiento ad hoc.
- Use URLs de GitHub con marcador de posición (`https://github.com/<your-org>/tshock-mcp`); no
  invente cuentas ni organizaciones reales.
- No deje marcadores `TODO` en los archivos entregados.
