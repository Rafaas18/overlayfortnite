# Overlay Cofres (Fortnite)

Contador de cofres abiertos que se muestra encima del juego. Lee los replays que Fortnite
guarda después de cada partida, cuenta los cofres que se abrieron y va sumando el total.

No toca el proceso del juego ni su memoria: solo lee archivos `.replay` de tu disco y dibuja
una ventana normal de Windows siempre visible.

## Requisitos

- Windows 10 u 11.
- En Fortnite: **Ajustes → Juego → Repeticiones → Grabar repeticiones: Sí**. Sin eso no hay archivos que leer.
- En Fortnite: modo de pantalla **Pantalla completa en ventana**. En pantalla completa exclusiva
  ninguna ventana puede dibujarse encima del juego.

## Cómo obtener el programa

**Opción A, ya compilado.** En GitHub, pestaña *Actions* → última ejecución de *Build* →
artefacto `OverlayCofres-windows`. Trae `OverlayCofres.exe` (el overlay) y `ChestCounter.Cli.exe`
(diagnóstico). No hace falta instalar nada más.

**Opción B, compilarlo.** Con el [SDK de .NET 10](https://dotnet.microsoft.com/download):

```bash
dotnet run --project src/ChestCounter.Overlay --configuration Release
```

## Uso

Al abrirlo lee todos los replays de `%LOCALAPPDATA%\FortniteGame\Saved\Demos` y después revisa
la carpeta cada pocos segundos. Fortnite cifra el replay y solo guarda la clave cuando sales de la
partida, así que los cofres de una partida aparecen **al volver a la sala**, no durante el juego.

- **Arrastrar** con el botón izquierdo: mover el overlay.
- **Clic derecho**: opciones.
  - *Ajustar total…*: escribe cuántos cofres lleváis ya (por ejemplo 40000) y sigue sumando desde ahí.
  - *Qué cofres contar*: ver la sección siguiente.
  - *Patrón de cofres…*: qué tipos de contenedor cuentan como cofre.
  - *Ver partidas…*: tabla con cada partida y los tipos de contenedor encontrados.
  - *Bloquear*: los clics atraviesan el overlay y llegan al juego.
- **Ctrl+Alt+L**: bloquear o desbloquear. También desde el icono junto al reloj.

El historial se guarda en `%APPDATA%\OverlayCofres\history.json`, por eso el total se mantiene
aunque Fortnite borre los replays antiguos. Los ajustes están en `settings.json` en la misma carpeta.

## Qué cuenta exactamente

El replay solo contiene lo que el servidor le envió a **tu** cliente: lo que pasa cerca de ti.
Para cada contenedor el overlay distingue:

| Dato | Significado |
| --- | --- |
| Abiertos a la vista | Pasó de cerrado a abierto mientras lo tenías cerca. Lo abriste tú, alguien de tu squad a tu lado, o un rival cercano. |
| Del squad | De los anteriores, los que el replay atribuye a un jugador de tu equipo. Solo existe si el replay incluye quién abrió cada cofre. |
| Ya abiertos | Estaba abierto la primera vez que lo viste. No se sabe quién ni cuándo. |

Modos de conteo:

- **Todos los que se ven abiertos** (por defecto): *abiertos a la vista* + *ya abiertos*. Es un máximo.
- **Junto a mi squad** / **Junto a mí** (experimentales): solo cofres cuyo botín sale a menos de 6 m de un
  jugador de tu equipo en el momento en que el cofre aparece abierto. En los replays de prueba cuentan de
  menos (0 a 6 por partida), porque el aviso de «cofre abierto» y el botín casi nunca llegan a la vez.
- **Abiertos a la vista**.

Por qué el modo por defecto es el más amplio: el juego solo empieza a enviar un cofre cuando cambia,
así que la mayoría de los cofres que abres llegan al replay directamente como «ya abiertos», igual que
los que abrió otro antes de que llegaras. En replays reales de prueba, una partida de 15–20 minutos da
entre 40 y 80 cofres «ya abiertos» y solo de 0 a 4 «a la vista».

Límites que conviene conocer:

- El total por defecto es un **máximo**: incluye cofres abiertos por rivales en las zonas por las que pasas.
  Los otros modos se quedan cortos. Con los datos del replay analizados hasta ahora no se puede separar con
  exactitud «lo abrió mi squad» de «lo abrió otro».
- Los cofres que abre un compañero lejos de ti no están en tu replay.
- Se considera cofre todo contenedor cuyo nombre interno contiene `Chest`. Si la temporada usa otro
  nombre para algún cofre especial, aparece en *Ver partidas…* y se añade al patrón, por ejemplo `Chest|Treasure`.

## Diagnóstico

`ChestCounter.Cli.exe` imprime lo que el lector ve en cada replay: versión del juego, equipo,
y por cada tipo de contenedor cuántos se abrieron.

```bash
ChestCounter.Cli.exe
```

```bash
ChestCounter.Cli.exe "C:\ruta\a\partida.replay" --pattern "Chest|Treasure"
```

## Estructura

- `src/ChestCounter.ReplayReader`: lectura del replay (sobre [FortniteReplayReader](https://github.com/Shiqan/FortniteReplayDecompressor)), reglas de conteo, historial.
- `src/ChestCounter.Overlay`: la ventana (WPF).
- `src/ChestCounter.Cli`: herramienta de diagnóstico.
- `tests/ChestCounter.Tests`: pruebas de las reglas de conteo y del escaneo de la carpeta.
