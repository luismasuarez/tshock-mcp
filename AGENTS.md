# AGENTS.md

Reglas para agentes de IA que trabajen en este repositorio. Léalas antes de modificar código o
documentación.

## Idioma

- **Prosa** (documentación, mensajes de chat, artefactos de diseño): español neutro, registro
  formal-neutral. Sin voseo ni regionalismos.
- **Código** (fuentes, comentarios, identificadores, mensajes de commit, nombres de variables,
  claves de configuración, salida de logs): inglés.

## Invariantes

Estas reglas no se negocian en la Fase 0. Romper cualquiera de ellas es un error de revisión:

1. **stdout es solo JSON-RPC.** Nunca escriba en stdout desde código de aplicación, bibliotecas ni
   diagnósticos. Todo log, advertencia y traza va a stderr.
2. **El token nunca se registra.** `TSHOCK_TOKEN` no debe aparecer en logs, mensajes de error,
   excepciones, trazas, el registro de auditoría ni en la salida de las herramientas.
3. **La escritura está cerrada por defecto.** Las 6 herramientas de escritura solo se habilitan con
   `TSHOCK_MCP_ALLOW_WRITE=true` y la comprobación ocurre en tiempo de ejecución, no al arrancar.
4. **Sin `rawcmd` ni ejecución arbitraria.** No agregue herramientas que ejecuten comandos de
   consola, apaguen el servidor, muten usuarios, creen o eliminen prohibiciones en la Fase 0.
5. **El sobre de salida es estable.** Toda herramienta devuelve una cadena JSON con la forma
   `{"ok":true,"data":...}` o `{"ok":false,"error":{"code":"...","message":"...","httpStatus":...}}`.
   No cambie nombres de campos ni códigos de error existentes; agregue códigos nuevos solo si es
   imprescindible y documéntelos.
6. **Las IP se redactan por defecto** (`<redacted>`) salvo `TSHOCK_MCP_SHOW_IPS=true`. Se redactan
   los campos `ip` y cualquier valor con forma de IP. Los campos `token` y `password` se redactan
   siempre.
7. **La configuración es solo por variables de entorno.** No agregue flags de CLI ni archivos de
   configuración en la Fase 0. La validación es estricta y ocurre al arrancar: una URL base con
   consulta, fragmento o credenciales embebidas, o un timeout no numérico, terminan el proceso. No
   agregue valores de reserva silenciosos.
8. **Los argumentos se validan antes de la red.** Un argumento faltante, vacío o de tipo incorrecto
   devuelve `invalid_argument` sin ninguna petición HTTP.

## Comandos

Desde la raíz del repositorio:

```bash
dotnet build -c Release
dotnet run --project src/TShock.MCP.Server
dotnet test
python3 tests/e2e/mcp_stdio_smoke.py   # requiere build previo
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
tests/                           Proyecto de pruebas unitarias y e2e (mcp_stdio_smoke.py)
docs/                            Arquitectura, herramientas, seguridad y roadmap
docker/                          Dockerfile y ejemplo para Claude Desktop
.github/workflows/build.yml      CI de compilación y pruebas
```

## Cómo agregar una herramienta

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
7. Agregue pruebas en `tests/TShock.MCP.Server.Tests` y, si la ruta lo amerita, amplíe
   `tests/e2e/mcp_stdio_smoke.py`. No compile a mano si otro agente está editando las fuentes;
   coordine primero.

## Qué no cambiar en la Fase 0

- El transporte stdio y el hecho de que stdout transporta solo JSON-RPC.
- El conjunto de 18 herramientas y sus rutas.
- Los nombres y la semántica de las variables de entorno.
- El sobre de resultados y los códigos de error documentados.
- La licencia GPL-3.0-or-later.
- `Directory.Build.props`, el `.sln` y los archivos de proyecto, salvo que la tarea lo pida
  explícitamente.

## Documentación

- Actualice la documentación en el mismo cambio que el código. Una herramienta nueva sin entrada en
  `docs/tools.md` se considera incompleta.
- Marque el trabajo futuro como futuro. No documente HTTP, OAuth, puente de plugins, eventos,
  regiones ni `rawcmd` como si existieran.
- Use URLs de GitHub con marcador de posición (`https://github.com/<your-org>/tshock-mcp`); no
  invente cuentas ni organizaciones reales.
- No deje marcadores `TODO` en los archivos entregados.
