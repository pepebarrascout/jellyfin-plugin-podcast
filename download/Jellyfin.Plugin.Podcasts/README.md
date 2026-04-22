# Jellyfin Podcast Plugin
<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-podcast/main/logo.png" height="180"/><br />
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/pepebarrascout/jellyfin-plugin-podcast/total?color=9b59b6&label=descargas"/></a>
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-podcast/issues"><img alt="GitHub Issues" src="https://img.shields.io/github/issues/pepebarrascout/jellyfin-plugin-podcast?color=9b59b6"/></a>
        <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11.x-blue.svg"/></a>
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-podcast"><img alt="RSS" src="https://img.shields.io/badge/RSS-Podcasts-orange?logo=rss&logoColor=white"/></a>
    </p>
</div>

> **Gestiona tus suscripciones a podcasts** desde Jellyfin. Suscríbete a feeds RSS, descarga automáticamente nuevos episodios, elimina contenido escuchado y genera listas de reproducción automáticas diarias.

**Requiere Jellyfin versión `10.11.0` o superior.**

---

## ✨ Características

| Característica | Descripción |
|---|---|
| 🎙️ **Gestión de Feeds RSS** | Agrega, edita y elimina suscripciones a podcasts desde el panel de control |
| 🔄 **Actualización Automática** | Actualiza feeds según frecuencia: diario, semanal (lunes) o mensual (día 1) |
| 📥 **Descarga de Episodios** | Descarga automáticamente nuevos episodios en `podcasts/` dentro de tu biblioteca de música |
| 🖼️ **Portadas Automáticas** | Extrae la imagen de portada del feed y la guarda como `folder.jpg` |
| 🗑️ **Auto-borrado** | Elimina episodios 2 días después de escucharlos (solo el episodio escuchado) |
| 📋 **Lista Automática** | Genera una playlist diaria (XML nativo de Jellyfin) con episodios no escuchados |
| ✅ **Validación de Feeds** | Verifica que el RSS sea válido y contenga audio antes de agregarlo |
| 🔁 **Detección de Escucha** | Detecta automáticamente cuando terminas de escuchar un episodio (≥ 90%) |
| 💾 **Persistencia XML** | Toda la información se guarda en archivos XML de texto plano |

---

## 📋 Clientes Probados

| Cliente | Plataforma | Estado |
|---|---|---|
| 🌐 **Jellyfin Web** | Interfaz web nativa | ✅ Funcional |
| 📱 **Jellyfin para Android** | App oficial de Jellyfin | ✅ Funcional |
| 🖥️ **[Feishin]** | Escritorio (AppImage Linux) | ✅ Funcional |
| 🎵 **[Finamp]** | Android (versión Beta) | ✅ Funcional |

---

## 🚀 Instalación

### Método 1: Desde el Catálogo de Plugins de Jellyfin (vía Manifest) ⭐ Recomendado

1. En tu servidor Jellyfin, navega a **Panel de Control > Plugins > Repositorios**
2. Haz clic en el botón **+** (agregar repositorio)
3. Ingresa los siguientes datos:
   - **Nombre**: `Podcast Plugin`
   - **URL del Manifest**: `https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-podcast/main/manifest.json`
4. Haz clic en **Guardar**
5. Navega a la pestaña **Catálogo**
6. Busca **Podcasts** en la lista de plugins disponibles
7. Haz clic en **Instalar**
8. Reinicia Jellyfin cuando se te solicite

### Método 2: Instalación Manual

1. Descarga la última versión desde [Releases](https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases)
2. Descomprime el archivo ZIP
3. Copia todos los archivos `.dll` a la carpeta de plugins de tu servidor Jellyfin:
   - **Linux**: `~/.config/jellyfin/plugins/`
   - **Windows**: `%LocalAppData%\Jellyfin\plugins\`
   - **macOS**: `~/.local/share/jellyfin/plugins/`
   - **Docker**: Monta un volumen en `/config/plugins` dentro del contenedor
4. Reinicia Jellyfin

---

## ⚙️ Configuración

### Paso 1: Agregar un Podcast
1. Navega a **Panel de Control > Plugins > Podcasts**
2. Haz clic en **Agregar Podcast**
3. Completa los campos:
   - **Nombre del Podcast**: Nombre descriptivo (se usa como nombre de carpeta)
   - **Frecuencia de actualización**: Diario, Semanal (lunes) o Mensual (día 1)
   - **Enlace del Feed RSS**: URL del feed RSS del podcast
4. Haz clic en **Validar Feed** para verificar que el RSS sea accesible
5. Configura las opciones adicionales:
   - **Auto-borrado**: `Nunca` o `2 días después de escucharlo`
   - **Lista automática**: Incluir episodios en la playlist diaria
6. Haz clic en **Guardar**

### Opciones de Configuración

| Opción | Descripción |
|---|---|
| **Nombre del Podcast** | Nombre descriptivo. Se usa como carpeta dentro de `podcasts/` |
| **Frecuencia** | `Diario` (00:00), `Semanal` (lunes 00:00), `Mensual` (día 1, 00:00) |
| **Feed RSS** | URL del feed XML del podcast. Se valida antes de guardar |
| **Auto-borrado** | `Nunca` = mantener siempre, `2 días` = eliminar 2 días después de escuchar el episodio |
| **Lista automática** | Incluir episodios no escuchados en la playlist diaria generada a las 01:00 |

---

## ⏰ Programación de Tareas

El plugin ejecuta las siguientes tareas automáticamente en segundo plano:

| Hora | Tarea | Descripción |
|---|---|---|
| 🕛 **00:00** | Actualización de feeds | Descarga nuevos episodios según la frecuencia configurada |
| 🕐 **01:00** | Playlist automática | Genera `playlist.xml` con episodios no escuchados en orden cronológico |
| 🕑 **02:00** | Auto-borrado | Elimina episodios escuchados hace más de 2 días |

---

## 📁 Estructura de Archivos

```
{Biblioteca de Música}/
└── podcasts/
    ├── Podcast Auto Playlist/       ← Playlist diaria (formato XML de Jellyfin)
    │   └── playlist.xml
    ├── Mi Podcast 1/
    │   ├── folder.jpg               ← Portada extraída del feed RSS
    │   ├── 2026-04-20 - Episodio 1.mp3
    │   └── 2026-04-22 - Episodio 2.mp3
    └── Mi Podcast 2/
        ├── folder.jpg
        └── 2026-04-21 - Entrevista.mp3

{Datos de Jellyfin}/plugins/podcasts/
└── episode-data.xml                 ← Tracking de episodios (XML de texto plano)
```

---

## 🔧 Solución de Problemas

### El plugin no aparece en el panel de control
- Verifica que el archivo `.dll` esté en la carpeta correcta de plugins
- Asegúrate de reiniciar Jellyfin después de copiar los archivos
- Revisa los logs de Jellyfin para errores de carga del plugin

### Los episodios no se descargan
- Verifica que la URL del feed RSS sea accesible desde el servidor de Jellyfin
- Revisa los logs para errores de conexión o timeout
- Asegúrate de que Jellyfin tenga acceso a internet

### La playlist automática está vacía
- Verifica que al menos un podcast tenga la opción "Lista automática" activada
- Los episodios ya escuchados se excluyen automáticamente de la playlist
- La playlist se genera a las 01:00, espera a ese horario o reinicia Jellyfin

### Los episodios no se borran automáticamente
- Verifica que el podcast tenga configurado "2 días después de escucharlo"
- El auto-borrado se ejecuta a las 02:00
- Un episodio se considera "escuchado" al reproducir al menos el 90% de su duración

---

## 🛠️ Compilación

### Requisitos Previos
- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Pasos para Compilar

```bash
# Clonar el repositorio
git clone https://github.com/pepebarrascout/jellyfin-plugin-podcast.git
cd jellyfin-plugin-podcast

# Compilar en modo Release
dotnet publish Jellyfin.Plugin.Podcasts/Jellyfin.Plugin.Podcasts.csproj -c Release

# Los archivos compilados estarán en:
# Jellyfin.Plugin.Podcasts/bin/Release/net9.0/publish/
```

Los archivos `.dll` resultantes se copian a la carpeta de plugins de Jellyfin.

---

## 🏗️ Arquitectura

| Archivo | Responsabilidad |
|---|---|
| `PodcastsPlugin.cs` | Entry point del plugin. Configuración y página web del dashboard |
| `PodcastsPluginServiceRegistrator.cs` | Registro de servicios en el contenedor DI de Jellyfin |
| `PodcastService.cs` | Lógica central: RSS, descargas, portadas, auto-borrado, playlist |
| `PodcastScheduler.cs` | Servicio en segundo plano con timer para tareas programadas |
| `PodcastsApiController.cs` | API para validación de feeds RSS desde el dashboard |
| `Configuration/PluginConfiguration.cs` | Modelo de configuración (persistencia XML automática) |
| `Configuration/config.html` | Página de configuración del dashboard de Jellyfin |
| `Model/PodcastFeed.cs` | Modelo de datos de una suscripción a podcast |
| `Model/EpisodeRecord.cs` | Modelo de tracking de episodios descargados |

---

## 💬 Soporte

- **Issues**: [GitHub Issues](https://github.com/pepebarrascout/jellyfin-plugin-podcast/issues)
- **Jellyfin**: [Foro de Jellyfin](https://forum.jellyfin.org/)
- **Matrix**: [#jellyfin en Matrix](https://matrix.to/#/#jellyfin:matrix.org)

---

## ⚠️ Disclaimer

Este plugin es un proyecto independiente y no está afiliado, respaldado ni patrocinado por Jellyfin. Jellyfin es una marca registrada de [The Jellyfin Project](https://jellyfin.org/).

---

## 📄 Licencia

Este proyecto está bajo la licencia [MIT](LICENSE).

[Feishin]: https://github.com/jeffvli/feishin
[Finamp]: https://github.com/Finamp/Finamp
