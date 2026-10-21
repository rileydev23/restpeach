# Arquitectura de RestPeach

Cliente de APIs local y nativo de git, de la familia Peach (diseño en [DISENO.md](DISENO.md), formato de los
archivos en [FORMATO.md](FORMATO.md)).

Tres proyectos y una regla: **todo lo que no es pantalla vive en `RestPeach.Core`**, que no depende de
Avalonia. La app y el CLI son dos caras del mismo núcleo, así que una colección se envía, se prueba y se
ejecuta igual en los dos.

```
RestPeach (Avalonia, MVVM)          RestPeach.Cli (restpeach-cli)
        │                                   │
        └──────────────┬────────────────────┘
                       ▼
                RestPeach.Core
  Storage ── Workspace (la carpeta .api) · Codec (modelo ↔ JSON/YAML)
  Model ──── ApiRequest · AuthSpec · BodySpec · WorkspaceConfig · FolderConfig · ApiEnvironment
  Http ───── VariableScope · RequestPreparer · HttpEngine · ResponseFormatter
  Testing ── Assertions · JsonPath
  Formats ── Curl · Import (Postman, Insomnia, OpenAPI) · Export (Postman, OpenAPI, bash)
  Text ───── CanonicalJson · Yaml
  Runner ─── envía una o varias peticiones como la app · Loc + Strings (textos en/es)
```

## Núcleo

| Pieza | Responsabilidad |
|---|---|
| `Workspace` | Encuentra la colección (`.api/` o `api-client/`) y la lee y escribe: árbol, peticiones, `config.json`, `folder.json`, entornos y secretos. No guarda nada en caché; quien la usa vuelve a leer cuando el disco cambia. Crea el `.gitignore` y el `.gitattributes` propios de la colección. |
| `Codec` | Modelo ↔ árbol JSON. Omite los valores por defecto y conserva las claves desconocidas en `Extra`. |
| `CanonicalJson` / `Yaml` | Escritura determinista (claves ordenadas, dos espacios, LF): un guardado sin cambios no produce diff. |
| `VariableScope` | Las capas de variables (colección < carpetas < entorno < secretos < sesión) y la expansión de `{{…}}`, con las dinámicas `{{$guid}}`… Informa de las que faltan. |
| `AuthResolver` | Resuelve `inherit` subiendo por las carpetas hasta la colección. |
| `RequestPreparer` | Pura: petición + auth + variables → `PreparedRequest` (método, URL final, headers, body). La usan el envío, la vista previa de cURL (con los secretos enmascarados) y los tests. |
| `HttpEngine` | Envía un `PreparedRequest` con `HttpClient`: un motor por colección abierta, con su propio frasco de cookies en memoria. Mide el tiempo hasta el último byte y corta los bodies de más de 64 MB. |
| `Assertions` | Pruebas declarativas, una por línea, y capturas `set NOMBRE = …`. Sin motor de scripts. |
| `Runner` | `SendAsync` / `RunAsync` / `RunAllAsync`: prepara, envía y evalúa exactamente como la app. Las capturas quedan en `Runtime` y las ven las peticiones siguientes. Devuelve `RunResult` (petición, lo enviado, la respuesta, las pruebas). |
| `Import` / `Export` / `Curl` | Detectan el formato y convierten. Lo importado nunca sobrescribe; lo que no se pudo conservar se informa. |

## App de escritorio

**Patrón:** MVVM con CommunityToolkit.Mvvm, igual que TextPeach y GitPeach.

```
MainWindowViewModel ─┬─ HomeViewModel (proyectos y grupos)
                     └─ ProjectViewModel (una pestaña por proyecto; ICollectionHost)
                          ├─ RequestTab      una petición: edición + respuesta
                          ├─ RunTab          una ejecución de carpeta o colección
                          ├─ EnvironmentTab  variables y secretos de un entorno
                          └─ SettingsTab     config.json
```

| Carpeta | Responsabilidad |
|---|---|
| `Services/` | `ProjectStore` (state.json), `ProjectScanner` (tipo de proyecto, colecciones), `ProjectGrouping` (grupos, puro y testeado), `GitStatus` (rama y cambios, solo lectura), `PeachApps` + `PeachLocator` (carpeta compartida y dónde están GitPeach y TextPeach), `SingleInstance`, `Highlighting` (TextMate + `peach-dark.json`), `L` (la extensión `{l:L clave}`). |
| `ViewModels/` | Estado de pantalla. `ProjectViewModel` implementa `ICollectionHost`: el `Workspace`, el entorno elegido, el `Runtime` de la sesión y el `HttpEngine` que comparten todas sus pestañas. |
| `Views/` | XAML de Avalonia. `RequestView` y `RunView` usan AvaloniaEdit; los `TextDocument` pertenecen a la pestaña y la vista solo los engancha, porque Avalonia recicla la vista entre pestañas del mismo tipo. |
| `Controls/` | `Palette` (colores de la familia) y `Look` (colores de métodos, estados y orígenes de variables). |

### Flujo de una petición

1. El árbol abre un archivo → `RequestTab` carga el `ApiRequest` (copia editable; nada se escribe hasta
   Ctrl+S).
2. Cada cambio vuelve a calcular la URL resuelta, las variables que faltan y la vista previa de cURL con
   `Runner.Prepare` y los secretos como `••••`.
3. **Enviar** usa lo que hay en pantalla (guardado o no): `Runner.SendAsync` → `HttpEngine` → `Assertions`.
4. La respuesta se formatea fuera del hilo de la interfaz (`ResponseFormatter`) y se muestra; las capturas pasan
   al `Runtime` de la sesión y el panel de variables se actualiza.
5. **Guardar** escribe con `Workspace` y marca la escritura como propia (`MarkOwnWrite`) para que el *watcher*
   no la trate como un cambio externo.

### Ejecuciones

`RunTab` toma la lista de peticiones de una carpeta (en el orden del árbol) y consume
`Runner.RunAllAsync`, agregando una `RunRow` por resultado. Cada fila conserva su `RunResult` completo, así
que el detalle de abajo no vuelve a enviar nada: **Pruebas** (o el error), **Body** y **Headers** de la
respuesta, y **Enviado** a partir de `PreparedRequest` (más los headers que agrega el motor, como
`User-Agent`). Los valores secretos del entorno se enmascaran en el detalle; "Copiar como cURL" copia los
reales. El body se formatea en segundo plano y, si se elige otra fila antes de que termine, gana la más
nueva.

### Cambios en el disco

`ProjectViewModel` vigila `.api/` con un `FileSystemWatcher` (con *debounce*): vuelve a leer el árbol, los
entornos y la configuración cuando algo cambia afuera (un `git pull`, otro editor, un agente de IA). Una
petición abierta sin cambios se recarga; una con cambios sin guardar avisa en lugar de pisarlos. Un segundo
*watcher* sobre `.git` solo refresca el estado de git.

### Instancia única y familia Peach

`Program.Main` llama a `SingleInstance.TryBecomePrimary`: la primera ejecución escucha una *named pipe* y las
siguientes (por ejemplo, GitPeach o TextPeach abriendo un proyecto) le pasan sus rutas y terminan.
`PeachApps.Announce` escribe `%APPDATA%\peachapps\RestPeach\location.json` al iniciar; `PeachLocator` lee el de
las otras apps para abrirlas. Los grupos de proyectos usan el mismo modelo que el resto de la familia.

### Idiomas

`Strings` (en `RestPeach.Core`, porque el CLI también habla en/es) tiene cada texto en inglés y español.
`Loc.Init` elige el idioma guardado o el de Windows. En XAML, `{l:L clave}` devuelve `Loc.T(clave)` al
construirse la vista, así que el cambio de idioma se aplica al reiniciar. Un test verifica que cada texto
tenga los dos idiomas con los mismos marcadores.

## CLI

`restpeach-cli` es un `Program.cs` sobre el núcleo: `run` usa el mismo `Runner` (con `--var` como valores de
sesión y `--timeout` sobre la configuración), `import`/`export` los mismos conversores e `init` el mismo
`Workspace.Open(create: true)`. Se publica con *trimming*, sin Avalonia.

## Desarrollo

```
dev.cmd                     compila Debug y ejecuta
dotnet test RestPeach.sln   tests del núcleo (formato, variables, cURL, pruebas, importar/exportar,
                            HTTP real contra un servidor local, Runner) y de la app (textos, grupos, ejecuciones)
build.cmd                   dist\RestPeach.exe y dist\restpeach-cli.exe portables
```

En Debug, `RESTPEACH_DEBUG_ACTIONS="open:Carpeta/Petición.req.json;send;run;pick:0;detail:sent;env;about"`
ejecuta acciones al iniciar (útil para capturas sin simular teclas) y `RESTPEACH_DEMO_DATAFOLDER` cambia la
carpeta de configuración que se muestra.
