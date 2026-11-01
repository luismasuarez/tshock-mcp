# Contribuir

Gracias por el interés en `tshock-mcp`. Es un proyecto comunitario que acepta reportes de errores,
mejoras de documentación, pruebas y cambios de código. Este documento describe cómo proponer un
cambio, cómo preparar el entorno y qué se espera de un pull request.

Antes de empezar, lea [AGENTS.md](AGENTS.md): contiene los invariantes del repositorio y las reglas
que se aplican a cualquier contribución. El estado del proyecto y el alcance de cada fase están en
[docs/roadmap.md](docs/roadmap.md).

## Cómo proponer cambios

- **Cambios de comportamiento, herramientas nuevas o modificaciones al contrato de salida:** abra un
  issue primero. Explique el problema, el caso de uso y el enfoque propuesto. Un cambio de este tipo
  necesita acuerdo antes de escribir código, sobre todo si toca el conjunto de 18 herramientas o el
  sobre estable.
- **Correcciones pequeñas:** un error evidente, una errata en la documentación, una prueba que falta
  o una mejora acotada puede ir directamente a un pull request. Describa el cambio con claridad y
  mantenga el diff pequeño.
- **Problemas de seguridad:** no abra un issue. Siga el procedimiento de [SECURITY.md](SECURITY.md).

Si no está seguro de si un cambio requiere discusión previa, abra el issue. Es más barato acordar el
enfoque que rechazar un pull request extenso.

## Entorno de desarrollo

Requisitos:

- **.NET 8 SDK.** El proyecto fija `net8.0`; no se necesita .NET 9 para el gateway.
- **Python 3**, solo para la prueba de extremo a extremo.
- **Docker**, opcional, para probar contra un TShock real.

Comandos, desde la raíz del repositorio:

```bash
dotnet build -c Release
dotnet test -c Release
python3 tests/e2e/mcp_stdio_smoke.py
```

La prueba e2e requiere una compilación previa: lanza el binario real y levanta un TShock falso en un
puerto efímero, así que no necesita un servidor de juego en ejecución. No invoque `dotnet run` para
probar el protocolo a mano sin un cliente MCP: el proceso espera JSON-RPC por stdin.

Para probar contra un servidor TShock real, con un cliente de Terraria y con OpenCode, siga
[docs/local-testing.md](docs/local-testing.md).

## Reglas de código

### Idioma

- **Código:** fuentes, comentarios, identificadores, nombres de variables, mensajes de commit y salida
  de logs en **inglés**.
- **Documentación y prosa:** español neutro, registro formal-neutral. Sin voseo ni regionalismos.

### Invariantes que no se negocian

Un pull request que rompa cualquiera de estas reglas se devuelve para corrección:

1. **stdout es solo JSON-RPC.** Ningún log, advertencia ni traza puede escribirse en stdout; todo el
   diagnóstico va a stderr.
2. **El token nunca se registra.** `TSHOCK_TOKEN` no debe aparecer en logs, errores, excepciones,
   trazas, el registro de auditoría ni la salida de las herramientas.
3. **La escritura está cerrada por defecto.** Toda herramienta de escritura comprueba
   `TSHOCK_MCP_ALLOW_WRITE` en tiempo de ejecución y devuelve `write_disabled` cuando está
   deshabilitada, sin tráfico de red.
4. **Sin `rawcmd` ni ejecución arbitraria.** No se agregan herramientas que ejecuten comandos de
   consola, apaguen el servidor o muten usuarios y prohibiciones en la Fase 0.
5. **El sobre de salida es estable.** Toda herramienta devuelve una cadena JSON con la forma
   `{"ok":true,"data":...}` o `{"ok":false,"error":{"code":"...","message":"...","httpStatus":...}}`.
   No cambie nombres de campos ni códigos de error existentes.
6. **Las IP se redactan por defecto.** Se redactan los campos `ip` y los valores con forma de IP en
   campos de identidad, salvo `TSHOCK_MCP_SHOW_IPS=true`. `token` y `password` se redactan siempre.
7. **La configuración es solo por variables de entorno.** No agregue flags de CLI, archivos de
   configuración ni valores de reserva silenciosos. La validación estricta ocurre al arrancar.
8. **Los argumentos se validan antes de la red.** Un argumento faltante, vacío o de tipo incorrecto
   devuelve `invalid_argument` sin ninguna petición HTTP.

### Cómo agregar una herramienta

1. Confirme que la ruta existe en la API REST de TShock y que la Fase 0 la permite. Las operaciones
   destructivas o fuera de alcance necesitan discusión previa en un issue.
2. Coloque la herramienta en `Tools/ReadTools.cs` si no tiene efectos secundarios o en
   `Tools/WriteTools.cs` si muta estado.
3. Declare el método con `[McpServerTool(Name = "...")]`, un nombre en `snake_case`, argumentos con
   tipo y descripción, y una `[Description]` breve en inglés.
4. Delegue la llamada HTTP en `ITShockClient`. No construya `HttpClient` ad hoc ni concatene URL con
   datos sin escapar.
5. Devuelva siempre el sobre mediante `ToolResults`.
6. Actualice [docs/tools.md](docs/tools.md) y la tabla resumida de [README.md](README.md). Una
   herramienta sin documentación se considera incompleta.
7. Agregue pruebas en `tests/TShock.MCP.Server.Tests` y, si corresponde, amplíe
   `tests/e2e/mcp_stdio_smoke.py`.

El procedimiento completo está en [AGENTS.md](AGENTS.md#cómo-agregar-una-herramienta).

## Estilo de mensajes de commit

Escriba los mensajes en inglés, en modo imperativo y con un prefijo al estilo de *Conventional
Commits*. Use un prefijo de la lista siguiente y una descripción corta en minúscula, sin punto final:

- `feat:` funcionalidad nueva.
- `fix:` corrección de un error.
- `docs:` documentación.
- `chore:` mantenimiento, dependencias o herramientas.
- `ci:` cambios en el flujo de integración continua.
- `test:` pruebas.

Ejemplos tomados del historial del repositorio:

```
feat: phase 0 MCP gateway for TShock REST API
fix: redact ban identifiers that contain IP addresses
docs: consolidate phase 0 status and real-server findings
ci: update actions to current majors and pin the runner image
```

Un pull request puede contener varios commits; mantenga cada uno coherente y con un mensaje que
describa el cambio real.

## Lista de verificación del pull request

Antes de pedir revisión, confirme que:

- [ ] `dotnet build -c Release` termina sin advertencias nuevas.
- [ ] `dotnet test -c Release` pasa por completo.
- [ ] `python3 tests/e2e/mcp_stdio_smoke.py` informa `E2E PASSED` después de una compilación previa.
- [ ] La documentación afectada está actualizada, incluidos `docs/tools.md` y el `README.md` si cambió
      una herramienta.
- [ ] No hay secretos, tokens, contraseñas, IP reales ni rutas absolutas de su máquina en el diff.
- [ ] No se agregó ninguna herramienta fuera del alcance de la Fase 0 sin discusión previa en un
      issue.
- [ ] El sobre de salida y los códigos de error existentes no cambiaron; cualquier código nuevo está
      documentado.
- [ ] No quedan marcadores pendientes ni comentarios de trabajo sin resolver.

Si alguno de los puntos no se cumple, explique el motivo en la descripción del pull request.

## Licencia

El proyecto se distribuye bajo **GNU General Public License v3.0 o posterior**
(`GPL-3.0-or-later`); el texto completo está en [LICENSE](LICENSE). Al enviar una contribución, usted
acepta que se publique bajo los mismos términos. No se requiere un acuerdo de licencia de
contribuyente (CLA) ni la cesión de derechos de autor: conserva la titularidad de su trabajo y lo
licencia para el proyecto.

No incluya código de terceros con una licencia incompatible. Si necesita una dependencia nueva,
justifique su incorporación en el pull request y revise que su licencia sea compatible con GPL-3.0.

## Proyecto comunitario

`tshock-mcp` es un proyecto comunitario independiente. No está afiliado, patrocinado ni respaldado
por Pryaxis, el equipo de TShock ni el equipo de Terraria, y no habla en nombre de ellos. TShock y
Terraria son marcas de sus respectivos titulares. Las contribuciones deben mantener esa
independencia: no presente el proyecto como oficial ni use nombres o marcas ajenas para promocionarlo.
