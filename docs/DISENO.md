# Sistema de diseño de RestPeach

<!-- peach-hijo: DISENO v1.0 -->
> RestPeach comparte su sistema de diseño con las demás apps Peach (GitPeach, TextPeach): mismos colores,
> componentes, atajos y tono, para que pasar de una a otra no cueste nada. Este documento aplica esas reglas a
> RestPeach y agrega lo propio, marcado como **Propio de RestPeach**. Si cambias algo que también afecta a las
> otras apps (un color, un atajo común, un componente), dilo en el *pull request*: se cambia en las tres.

> **Regla de oro:** RestPeach parece un IDE sobrio para **probar APIs**. Fondo gris carbón, texto gris claro, un
> solo azul de acento, y color solo cuando significa algo (método, estado de la respuesta, origen de una
> variable, resultado de una prueba). Nada decorativo.

Índice:

1. [Principios](#1-principios)
2. [Base técnica](#2-base-técnica)
3. [Color](#3-color)
4. [Tipografía](#4-tipografía)
5. [Espaciado, forma y bordes](#5-espaciado-forma-y-bordes)
6. [Anatomía de la ventana](#6-anatomía-de-la-ventana)
7. [Componentes](#7-componentes)
8. [Patrones de interacción](#8-patrones-de-interacción)
9. [Teclado](#9-teclado)
10. [Textos y tono](#10-textos-y-tono)
11. [Iconografía y marca](#11-iconografía-y-marca)
12. [Rendimiento percibido](#12-rendimiento-percibido)
13. [Dónde está cada cosa en el código](#13-dónde-está-cada-cosa-en-el-código)

---

## 1. Principios

1. **Oscuro y denso, como un IDE.** Solo tema oscuro. Filas compactas, fuente base de 13 px, mucha información
   visible a la vez. No hay modo claro.
2. **El color es información.** El 90 % de la pantalla son grises. El azul de acento marca lo seleccionado y la
   acción principal. Los demás colores solo comunican estado o tipo, con el mismo significado que en GitPeach y
   TextPeach ([3.4](#34-colores-semánticos)) más los propios de RestPeach ([3.7](#37-colores-propios-de-restpeach)).
3. **Una acción principal por zona.** Como máximo un botón `accent` por barra: **Nueva petición...** en la
   toolbar, **Enviar** en la petición.
4. **La colección es texto en tu repositorio (Propio de RestPeach).** Nada se escribe hasta que guardas
   (`Ctrl+S`); los archivos son deterministas (un guardado sin cambios no produce diff). Git solo se **lee**: las
   acciones de git quedan para GitPeach.
5. **Lo destructivo nunca es por defecto.** Borrar una petición o carpeta, sobrescribir o descartar piden un
   gesto explícito y confirmación. Importar nunca sobrescribe.
6. **Los secretos no se ven sin pedirlo** ([8.7](#87-secretos)).
7. **Nada se mueve solo.** La ventana se abre donde la dejaste; las listas se reemplazan de una vez; al recargar
   una petición se conserva lo que estabas mirando.
8. **Todo se puede hacer con teclado.** Cada acción frecuente tiene atajo y todos se listan en F1
   ([9](#9-teclado)).
9. **Estado persistente.** Proyectos, pestañas, peticiones abiertas, entorno elegido, ventana y preferencias se
   guardan solos en `state.json`. Las cookies y los valores capturados viven solo en memoria.
10. **Inglés y español en la interfaz, inglés en el código** ([10](#10-textos-y-tono)).
11. **Sin nombres de terceros en la interfaz**, salvo los formatos que se importan o exportan (Postman, Insomnia,
    OpenAPI, cURL), que son el nombre del formato. Lo permitido además: el explorador de archivos del sistema
    (**Carpeta**) y las apps hermanas GitPeach y TextPeach.
12. **Lo que solo se lee nunca parece editable.** Respuestas, lo **Enviado**, mensajes y atajos usan
    `TextBlock`/`SelectableTextBlock` o un editor de solo lectura; `TextBox` es solo para escribir.
13. **Portable y autocontenido.** `RestPeach.exe` y `restpeach-cli.exe` funcionan sin instalar; con `portable.txt`
    al lado, los datos se guardan junto al exe. El instalador de la app (`restpeach-windows-x64-setup.exe`, `install.sh`) es
    opcional: copia ese mismo exe a una carpeta del usuario, lo registra en el buscador del sistema y deja
    `installed.txt` al lado.
14. **Actualizar no interrumpe.** Una versión nueva solo se nota como un botón **↑ x.y.z** junto al **?**; nada
    se descarga sin confirmar en su popup (Actualizar y reiniciar / Ahora no / Omitir esta versión).

---

## 2. Base técnica

| Pieza | Elección | Notas |
|---|---|---|
| UI | **Avalonia 11.3** + `Avalonia.Themes.Fluent` | `RequestedThemeVariant="Dark"`, estilos propios en `Styles.axaml`. |
| Runtime | **.NET 10**, C# `latest`, `Nullable` e `ImplicitUsings` | `InvariantGlobalization=true`. |
| MVVM | **CommunityToolkit.Mvvm 8.4** | `[ObservableProperty]`, `[RelayCommand]`. |
| Bindings | Compilados por defecto | Todo XAML declara `x:DataType`. |
| Render | Software | Se excluye `av_libglesv2.dll` al publicar. |
| Editor | **AvaloniaEdit 11.4** + **AvaloniaEdit.TextMate**, tema `peach-dark.json` | Body, respuesta y lo Enviado. Sin *trimming* en la app. |
| Núcleo | `RestPeach.Core` sin Avalonia | La app y el CLI lo comparten. El CLI se publica con *trimming*. |
| Publicación | `build.cmd` → `RestPeach.exe` y `restpeach-cli.exe` single-file, `win-x64` | `dev.cmd` compila Debug y ejecuta. |
| Estado | `%APPDATA%\peachapps\RestPeach\state.json` o `.\data\` | Escritura atómica. |
| Tests | xUnit | Núcleo (formato, variables, cURL, pruebas, importar/exportar, HTTP real contra un servidor local, Runner) y app. |

---

## 3. Color

Hex sRGB; en XAML en minúsculas; en C# desde `Controls/Palette.cs` (`Palette` para la familia, `Look` para los
significados propios). Nunca inventes un gris nuevo.

### 3.1 Superficies

| Token | Hex | Uso en RestPeach |
|---|---|---|
| (título) | `#111214` | Barra de título, botón Home. |
| `Chrome` | `#18191c` | Barra de pestañas de la ventana, barra de estado. |
| `Code` | `#1b1c1f` | Body, respuesta, lo Enviado, vista de cURL. |
| `Bg` | `#1e1f22` | Fondo de ventana y contenido; franja de pestañas de documentos. |
| `Sidebar` | `#222327` | Árbol de la colección, panel de variables, cURL y sesión. |
| `Panel` | `#26272b` | Toolbar, barra de la petición (método + URL), diálogos, `TextBox`. |
| `Panel2` | `#2d2e33` | Botón normal, *hover* de filas, chips. |
| `Border` | `#3a3c42` | Todos los bordes, separadores y `GridSplitter`. |

Cada franja horizontal cambia de superficie y se separa con un borde de 1 px `#3a3c42`. Nunca blanco ni grises
claros como fondo.

### 3.2 Texto

| Token | Hex | Uso |
|---|---|---|
| `Text` | `#d4d4d4` | Texto normal. |
| `Dim` | `#8a8d93` | Rutas, tiempos, tamaños, *watermarks*, pistas. |
| (gutter) | `#5d6066` | Números de línea. |
| Blanco | `#ffffff` | Solo sobre fondos de color (botón `accent`, insignias de estado, seleccionado). |

### 3.3 Acento y selección

Acento `#4a90d9` (*hover* `#5aa0e9`); seleccionado `#33507a` (+ *hover* `#3a5a88`); *hover* de fila `#2d2e33`.

### 3.4 Colores semánticos

Significado fijo, igual en todas las apps Peach:

| Nombre | Hex | Significado general |
|---|---|---|
| Verde | `#6cc070` | Positivo / identidad: correcto, "N peticiones", agregado. |
| Rojo | `#e0645a` | Error, borrado, variable sin definir, prueba fallida. |
| Ámbar | `#e0a24a` | Atención: favorito ★, modificado, carpetas del árbol. |
| Azul | `#4a90d9` | Información, entorno. |
| Morado | `#9a7ae0` | Secretos. |
| Teal | `#4ac7c0` | Valores de la sesión, renombrado. |
| Rosa | `#d96a9b` | Conflicto de git; palabras clave en código. |
| Oliva | `#c8c84a` | Atributos en código. |

### 3.5 Banners

| Tipo | Fondo | Cuándo |
|---|---|---|
| Información | `#25364d` | "El archivo cambió en el disco mientras tenías cambios sin guardar", con **Recargar desde el disco** / **Conservar los míos**. |

Las variables sin definir no usan banner: se avisan en rojo debajo de la URL.

### 3.6 Serie categórica y sintaxis

`#4a90d9` azul · `#e0a24a` ámbar · `#6cc070` verde · `#d96a9b` rosa · `#9a7ae0` morado · `#4ac7c0` teal ·
`#e0645a` rojo · `#c8c84a` oliva, en ese orden. Resaltado de sintaxis: el tema `peach-dark.json`, igual al de
las otras apps (cadenas `#8fcf94`, números `#e0a24a`, claves `#a8c8e8`, palabras clave `#d96a9b`, comentarios
`#6f737a` en cursiva).

### 3.7 Colores propios de RestPeach

Todo sale de la paleta anterior; aquí se fija **qué significa** cada color (`Look`).

**Métodos HTTP** (categorías, así que toman la serie; DELETE conserva el rojo porque borra):

| Método | Color | Etiqueta |
|---|---|---|
| GET | verde `#6cc070` | `GET` |
| POST | ámbar `#e0a24a` | `POST` |
| PUT | azul `#4a90d9` | `PUT` |
| PATCH | violeta `#9a7ae0` | `PATCH` |
| DELETE | rojo `#e0645a` | `DEL` |
| HEAD, OPTIONS | turquesa `#4ac7c0` | `HEAD`, `OPT` |
| WS (WebSocket) | turquesa `#4ac7c0` | `WS` |

La etiqueta (`TextBlock.method`) va en mayúsculas, monoespaciada, 9 px, negrita, con ancho mínimo fijo (árbol,
pestañas, filas de una ejecución) para que los nombres queden alineados.

**Estados de respuesta:**

| Rango | Texto (listas) | Insignia rellena (cabecera de la respuesta) |
|---|---|---|
| 2xx | verde `#6cc070` | `#2f6f3e` |
| 3xx | azul `#4a90d9` | `#2d5c8a` |
| 4xx | ámbar `#e0a24a` | `#8a6d1f` |
| 5xx y sin respuesta | rojo `#e0645a` | `#8a2f2a` |

Las insignias son pasos oscuros del color semántico con texto blanco.

**Origen de una variable:** colección `Dim` · carpeta ámbar · entorno azul · secreto violeta (valor siempre
`••••`) · sesión (capturado) turquesa.

**Pruebas:** `✓` verde pasó · `✗` rojo falló · `!` rojo no se pudo evaluar o no hubo respuesta · `=` turquesa
captura.

---

## 4. Tipografía

**UI:** `Segoe UI`. **Monoespaciada:** `Cascadia Mono, Consolas, Courier New` (URL, body, respuesta, cURL,
etiquetas de método).

| Tamaño | Uso |
|---|---|
| 9 | Etiqueta de método. |
| 11 | Tiempos y tamaños, badges, pistas bajo un campo, origen de una variable. |
| 12 | Árbol, filas de ejecución, tablas de params/headers, panel lateral. |
| **13** | **Base** de la interfaz y del editor (zoom con `Ctrl +/−`). |
| 14 | Nombre del proyecto en la Home. |
| 16 | Título de un estado vacío. |

SemiBold para títulos de sección y el elemento actual; Bold solo para etiquetas de método y letras de estado;
cursiva solo para notas ("Todavía no hay respuesta").

---

## 5. Espaciado, forma y bordes

Unidad base de 2 px. Toolbar `Padding="8,6"`, botones con `Spacing="4"`, separadores verticales
`Border Width="1" Margin="6,2"`. Barra de estado `Padding="10,3"`. Filas densas `Margin="8,2"`, árbol `+12` px
por nivel. Diálogos `Margin="18"`, botones `MinWidth=84`, el principal primero a la derecha. Botón
`CornerRadius=3`, `Padding="10,4"`; insignias `CornerRadius=3`, `Padding="5,1"`. `GridSplitter` de 4 px. Esquinas
de 3 px en controles y 0 en segmentos y pestañas. Sin sombras ni degradados (salvo el logo).

---

## 6. Anatomía de la ventana

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 🍏 RestPeach  shop-api · C:\ruta                              ─   ☐   ✕      │ título     #111214 (34 px)
├──────────────────────────────────────────────────────────────────────────────┤
│▌Proyectos▐│ shop-api ✕ │ otro ✕ │                                         ? │ pestañas   #18191c
├──────────────────────────────────────────────────────────────────────────────┤
│ Nueva petición... Nueva carpeta... Importar ▾ Exportar ▾ │ ▶ Ejecutar colección Ajustes │ Entorno [Local ▾] Editar + │ TextPeach Carpeta │
├──────────────┬┬───────────────────────────────────────────┬┬─────────────────┤
│ shop-api  6  ││ GET Get post ✕ │ RUN Colección ✕          ││ Variables·Local │
│ [Filtrar…]   ││ [GET ▾] {{BASE_URL}}/posts/1    [Enviar]  ││ BASE_URL entorno│
│ ■ Shop       ││ Params Headers Body Auth Pruebas Notas    ││ cURL            │
│   GET Get…   ││ ─ 200 OK · 22 ms · 1.2 KB ─────────────── ││ Sesión          │
│ #222327      ││ Pruebas Body Headers Cookies              ││ #222327         │
├──────────────┴┴───────────────────────────────────────────┴┴─────────────────┤
│ ⑂ master 2 cambios en .api      1 valor capturado   { } Formatear  Ajuste de línea  13 px │ estado #18191c
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Barra de título propia** (34 px, `#111214`), botones de ventana dibujados; en la barra de tareas el título es
  "proyecto — RestPeach".
- **Barra de pestañas:** botón fijo **Proyectos** (`Button.home`), una pestaña por proyecto y `?` (F1).
- **Toolbar** (`#26272b`): **Nueva petición...** (accent), Nueva carpeta..., Importar ▾, Exportar ▾ ·
  **▶ Ejecutar colección**, Ajustes · selector de **Entorno**, Editar y `+`. A la derecha **TextPeach** (abre el
  proyecto y la petición actual) y **Carpeta** (la carpeta `.api` en el explorador).
- **Cuerpo** en tres columnas: árbol de la colección · documentos (peticiones, ejecuciones, entornos, ajustes) ·
  panel lateral (Variables del entorno, cURL, Sesión).
- **Barra de estado:** rama de git y "n cambios en .api" (o "Sin git"), el último aviso, "n valores capturados",
  `{ } Formatear`, ajuste de línea y tamaño de fuente.

**Colocación de la ventana** (`App.PlaceWindow`): 1200×760 DIP por defecto (máx. 92 % del área de trabajo),
mínimo 800×500, recuerda tamaño, posición, monitor y maximizado; arranca invisible y se muestra ya colocada.

---

## 7. Componentes

Estilos en `src/RestPeach/Styles.axaml`.

### 7.1 Botones

| Clase | Cuándo |
|---|---|
| (ninguna) | Acciones normales. |
| `accent` | Nueva petición..., **Enviar**, el botón principal de un diálogo. Una por zona. |
| `flat` | Acciones dentro de filas y cabeceras (✕, Borrar valores, Copiar, toggles de la barra de estado). |
| `seg` (+ `active`) | Secciones de la petición (Params, Headers, Body, Auth, Pruebas, Notas) y de la respuesta o el detalle de una ejecución (Pruebas, Body, Headers, Cookies / Enviado). |
| `ToggleButton` | Opciones persistentes (`{ } Formatear`, Detener en el primer fallo). |

Cursor de mano en botones y enlaces; flecha en filas, árbol y pestañas. Etiquetas cortas, `...` si abren un
diálogo, `▾` si abren un menú. Glifos sueltos (`✕`, `+`, `▶`) siempre con tooltip.

### 7.2 Campos

`TextBox` `#26272b` con borde `#3a3c42`. La **URL** es monoespaciada; debajo se muestra en `dim` la URL resuelta
con el entorno elegido (secretos ocultos) y, en rojo, las variables que faltan. Pegar un comando cURL en la URL
lo importa. Las tablas clave-valor (params, headers, form, variables) tienen casilla para activar cada fila; los
params activos viven en la URL.

### 7.3 Listas y árbol

Árbol de la colección: carpetas `■` ámbar, peticiones con su etiqueta de método; F2 renombra, Supr borra (con
confirmación); un clic abre. Filas de una ejecución: `✓`/`✗`, método, ruta, estado en su color, tiempo y
"pruebas pasadas / total".

### 7.4 Pestañas de documentos

`ListBox.doctabs` + `ListBox.tabstrip` (barra de 4 px debajo, rueda de lado). Cada pestaña lleva una etiqueta de
su tipo: el método para una petición, `RUN` para una ejecución, `ENV` para un entorno. Prefijo `● ` con cambios
sin guardar. Menú: Cerrar los demás, Cerrar todos.

### 7.5 Respuesta

Cabecera: insignia rellena del estado (`200 OK`), tiempo y tamaño en `dim`, y a la derecha las acciones (Copiar
como cURL…). Secciones con `Button.seg`. Body con formato y colores (JSON, XML), imágenes como imagen, búsqueda con
`Ctrl+F`. Sin respuesta: "Todavía no hay respuesta" en cursiva `dim` con la pista "Envía la petición con
Ctrl+Enter".

### 7.6 Ejecuciones (Propio de RestPeach)

- Cabecera: "Ejecución: toda la colección" (o la carpeta) y el resumen en **una sola línea**
  (`6 peticiones · 5 correctas · 1 fallida · 916 ms`), verde si todo pasó, rojo si no; sin tarjetas.
  **Detener en el primer fallo** y **▶ Ejecutar**.
- Al elegir una fila, abajo su detalle con **Pruebas · Body · Headers · Enviado**; la sección elegida se mantiene
  al cambiar de fila para comparar. Al terminar con fallos se selecciona el primero. Doble clic o Enter abre la
  petición.
- **Enviado** es de solo lectura y muestra la verdad: URL final, todos los headers (también los que agrega el
  motor, marcados "se agrega al enviar") y el body expandido. Los secretos se enmascaran.
- Abajo, "Lo mismo en CI:" con el comando `restpeach-cli run …` y **Copiar**.

### 7.7 Panel lateral

**Variables · <entorno>**: cada variable con su valor y su origen en color
([3.7](#37-colores-propios-de-restpeach)), de mayor a menor prioridad. **cURL**: la petición abierta como comando,
secretos ocultos. **Sesión**: cookies y valores capturados, con **Borrar cookies** / **Borrar valores**.

### 7.8 Diálogos, menús, estados vacíos

Diálogos construidos en código (`Views/Dialogs.cs`), fondo `#26272b`; `Message` nunca usa un `TextBox`. "Acerca
de" (F1): logo, versión, atajos, **Idioma** y carpeta de configuración. Menús con verbos cortos y lo destructivo
al final. Estados vacíos centrados: "No hay peticiones abiertas" + qué hacer; un proyecto sin `.api` explica que
la colección es una carpeta de texto en el repositorio y ofrece **Crear colección (.api)**.

### 7.9 Git de solo lectura

Solo `git --no-optional-locks status`. Archivos del árbol en el color de su estado (`M U A R D !`), la rama y "n
cambios en .api" en la barra de estado; un clic en la rama abre el repositorio en **GitPeach** (o el diálogo que
explica qué es y ofrece **Ir al repositorio**). Sin git: "Sin git" `dim` con el motivo.

### 7.10 Barras de desplazamiento

Una sola para todo: 4 px, sin flechas, no crece, `AllowAutoHide=False`.

---

## 8. Patrones de interacción

### 8.1 Home de proyectos

Igual que en GitPeach y TextPeach: Agregar proyecto... (accent), Escanear carpeta..., Nuevo grupo..., filtro,
Expandir/Contraer, ★ Favoritos; grupos plegables con "Abrir todos"; arrastrar carpetas; Supr quita de la lista sin
tocar el disco. Dato de contexto en verde: "N peticiones" (o "sin colección todavía" atenuado).

### 8.2 Pestañas persistentes

Proyectos, sus documentos abiertos y el activo se restauran al iniciar. Cerrar con cambios sin guardar pregunta.

### 8.3 Ocupado y avisos

Mientras se envía, **Enviar** pasa a **Cancelar**; una ejecución se puede **Detener**. Los resultados van al
aviso de la barra de estado ("Copiado como cURL (con los valores reales)").

### 8.4 Vigilancia del disco

Un *watcher* sobre `.api/` con *debounce* recarga árbol, entornos y ajustes cuando algo cambia afuera (`git
pull`, otro editor, un agente de IA). Una petición abierta sin cambios se recarga; con cambios sin guardar
aparece el banner azul. Lo que RestPeach guarda se marca como propio y no dispara el aviso.

### 8.5 Guardar

`Ctrl+S` guarda la petición, `Ctrl+Shift+S` todo. **Enviar** usa lo que hay en pantalla aunque no esté guardado.
Los archivos se escriben deterministas (claves ordenadas, dos espacios, LF).

### 8.6 Importar y exportar

Importar escribe en una carpeta nueva, nunca sobrescribe, y lista lo que no se pudo conservar. Exportar genera
Postman, OpenAPI o un script de bash con `curl`.

### 8.7 Secretos

Un secreto nunca se muestra en claro sin una acción explícita: el panel de variables, la vista de cURL y el
detalle de una ejecución usan `••••`; el editor de entornos los muestra con **Mostrar valores**. Copiar como
cURL copia los valores reales y lo dice en el aviso.

### 8.8 Integración con GitPeach y TextPeach

- **TextPeach** abre el proyecto y la petición actual; un clic en la rama abre **GitPeach**. `PeachLocator` las
  busca por su `location.json`, junto al exe, carpeta hermana, `%LOCALAPPDATA%\Programs`, `%ProgramFiles%` y
  `PATH`.
- Instancia única: abrir una carpeta desde otra app la entrega a la ventana de RestPeach ya abierta.
- `%APPDATA%\peachapps\RestPeach\` guarda `state.json` y `location.json` (este último en cada inicio de una
  compilación Release). `state.json` también guarda la búsqueda de versiones (`CheckUpdates`, `LastUpdateCheck`,
  `LatestVersion`, `SkippedVersion`): como mucho una consulta a GitHub al día.

---

## 9. Teclado

Los atajos comunes a todas las apps Peach más los de RestPeach; todos en XAML (`KeyBindings`) y en
`ViewModels/Shortcuts.cs` (F1).

| Atajo | Acción |
|---|---|
| `Ctrl+P` | Ir a petición (en un proyecto) / abrir proyecto |
| `Ctrl+Shift+O` | Abrir proyecto (búsqueda rápida) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Pestaña siguiente / anterior |
| `Ctrl+1 … Ctrl+9` | Ir a la pestaña N |
| `Ctrl+W` / `Ctrl+F4` | Cerrar el documento (o el proyecto) |
| `F1` | Acerca de, atajos e idioma |
| `F5` | Volver a leer la colección, los entornos y git |
| `Ctrl+Enter` | Enviar la petición / ejecutar de nuevo; en un WebSocket, conectar y luego enviar el mensaje |
| `Ctrl+S` / `Ctrl+Shift+S` | Guardar / guardar todo |
| `Ctrl+L` | Ir a la URL |
| `Ctrl+N` / `Ctrl+Shift+N` | Nueva petición / nueva carpeta |
| `Ctrl+D` | Duplicar la petición |
| `Ctrl+Shift+C` | Copiar como cURL |
| `Ctrl+F` · `F3` / `Shift+F3` | Buscar en el body o la respuesta |
| `Ctrl+Shift+E` | Filtrar el árbol |
| `F2` / `Supr` | Renombrar / eliminar (en el árbol) |
| `Ctrl+Shift+R` | Ejecutar la colección |
| `Ctrl+PageDown` / `PageUp` | Documento siguiente / anterior |
| `Ctrl +` / `Ctrl −` / `Ctrl+0` | Zoom del texto |
| `Alt+Z` | Ajuste de línea |

---

## 10. Textos y tono

- **Inglés y español.** Todos los textos están en `RestPeach.Core/Strings.cs` (en el núcleo, porque el CLI
  también habla los dos idiomas). En XAML `{l:L clave}`, en C# `Loc.T`/`Loc.F`/`Loc.Plural`. Un test verifica
  que cada texto tenga los dos idiomas con los mismos marcadores.
- El idioma se elige en "Acerca de" o es el de Windows; se aplica al reiniciar.
- No se traducen: nombres de apps, atajos, métodos y términos de HTTP (header, body, query, cookie), formatos.
- Inglés claro, *sentence case*. Español neutro con tuteo, mayúscula solo al inicio, sin exclamaciones ni emojis.
- Pistas que enseñan sin estorbar, en `dim` debajo del campo ("Content-Type se agrega según el tipo de body
  salvo que lo pongas aquí").
- Errores: qué pasó + el dato concreto. Números con separador de miles, tiempos en ms o s, tamaños en B/KB/MB.

---

## 11. Iconografía y marca

- Glifos Unicode: `★` favorito, `✕` cerrar, `●` sin guardar, `■` carpeta, `▶` ejecutar, `▾` menú, `⋯` más,
  `?` ayuda, `✓ ✗ ! =` resultados de pruebas, `••••` secreto.
- **Logo:** fruta verde azulada con hoja y flechas `⇄` blancas (petición y respuesta). Cada app Peach tiene su
  fruta y color para distinguirse en la barra de tareas. Va en `app.ico`, `icon64.png` y `logo.png`; en la barra
  de título, "Acerca de" y los estados vacíos, **nunca** en la barra de pestañas.
- El nombre es **RestPeach**, una palabra. Un fork debe usar otro nombre y logo.

---

## 12. Rendimiento percibido

- Envío, formato de la respuesta, importación y lectura de la colección fuera del hilo de la interfaz; si se
  elige otra fila o petición antes de que termine el formato, gana la más nueva.
- Límites explícitos: body de la respuesta 64 MB (se corta y se marca), texto mostrado 16 MB, tiempo límite por
  defecto 30 s (configurable en Ajustes).
- "Cargando…" o la pista correspondiente en vez de un panel vacío.

---

## 13. Dónde está cada cosa en el código

| Qué | Dónde |
|---|---|
| Estilos y clases | `src/RestPeach/Styles.axaml` |
| Colores (familia y significados propios) | `Controls/Palette.cs` (`Palette`, `Look`) |
| Tema de sintaxis | `Assets/peach-dark.json` + `Services/Highlighting.cs` |
| Textos | `src/RestPeach.Core/Strings.cs` |
| Atajos de F1 | `ViewModels/Shortcuts.cs` |
| Diálogos | `Views/Dialogs.cs` |
| Formato de los archivos de la colección | [FORMATO.md](FORMATO.md) |

Si agregas un patrón de interfaz nuevo, documéntalo aquí.
