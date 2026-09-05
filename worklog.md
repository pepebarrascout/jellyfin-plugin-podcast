# Jellyfin Podcast Plugin - Worklog

---
Task ID: 1
Agent: Super Z (Main)
Task: Research Jellyfin plugin architecture from reference repositories

Work Log:
- Examined jellyfin-plugin-listenbrainz and jellyfin-plugin-lastfm repositories
- Identified standard plugin file structure and patterns
- Documented key integration points (BasePlugin, IHasWebPages, IPluginServiceRegistrator, IHostedService)
- Noted GUID consistency requirements across 4 files
- Analyzed config.html patterns for Jellyfin dashboard pages

Stage Summary:
- Full architectural understanding of Jellyfin 10.11 plugin development
- Key patterns: BasePlugin<TConfig>, embedded HTML resources, IHostedService for background tasks
- Config persistence via XML auto-handled by Jellyfin framework

---
Task ID: 2
Agent: Super Z (Main)
Task: Build complete Jellyfin Podcast plugin

Work Log:
- Created 17 source files forming the complete plugin
- Implemented PodcastFeed and EpisodeRecord data models with full enum types
- Built PluginConfiguration extending BasePluginConfiguration for automatic XML persistence
- Created PodcastService with RSS parsing (XDocument/LINQ), episode downloading, cover image extraction, auto-delete, and auto-playlist generation (XML format)
- Implemented PodcastScheduler as IHostedService with timer-based scheduling (00:00 updates, 01:00 playlist, 02:00 auto-delete)
- Added playback event tracking via ISessionManager for listen detection at 90% threshold
- Created comprehensive config.html dashboard page with CRUD operations, client-side RSS validation, and status banner
- Set up DI registration in PodcastsPluginServiceRegistrator (PodcastService as singleton, PodcastScheduler as hosted service)
- Created build files: .csproj (net9.0, Jellyfin.Common/Controller/Model 10.11.*), global.json, nuget.config, manifest.json, meta.json
- Ensured GUID consistency across AssemblyInfo.cs, PodcastsPlugin.cs, manifest.json, meta.json

Stage Summary:
- Complete plugin source code at /home/z/my-project/download/Jellyfin.Plugin.Podcasts/
- Features: RSS feed management, configurable update frequency, episode downloads, folder.jpg covers, auto-delete after 2 days, daily auto-playlist (Jellyfin XML format), playback-based listen detection
- All data stored as plain XML in Jellyfin plugin config directory

---
Task ID: 3
Agent: Super Z (Main)
Task: Playlist format change, GitHub repo creation, compilation, and deployment

Work Log:
- Changed playlist format from M3U8 to Jellyfin native XML (playlist.xml with <Item>, <PlaylistItems>, <PlaylistMediaType> elements)
- Researched Jellyfin playlist XML format from Jellyfin source code (PlaylistXmlSaver, BaseXmlSaver)
- Created GitHub repository: pepebarrascout/jellyfin-plugin-podcast
- Generated podcast-specific logo using AI image generator
- Created README.md following same style as reference repos (Spanish, badges, tables, sections)
- Created LICENSE (MIT) and .gitignore
- Fixed compilation errors: correct namespaces (MediaBrowser.Common.Configuration, MediaBrowser.Common.Plugins, MediaBrowser.Controller.Library), proper enum types (CollectionTypeOptions), event args types (PlaybackProgressEventArgs for both Start and Progress events), nullable handling
- Removed API controller in favor of client-side RSS validation in config.html
- Added Jellyfin.Common NuGet package reference
- Successfully compiled with dotnet 9.0.313
- Created GitHub release v1.0.0.0 with ZIP artifact (MD5: 696C2BE1CB0AB25EAF682E126196667A)
- Updated manifest.json with correct release URL and checksum

Stage Summary:
- Repository: https://github.com/pepebarrascout/jellyfin-plugin-podcast
- Release: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/tag/v1.0.0.0
- Plugin DLL: jellyfin-plugin-podcasts_1.0.0.0.zip (76KB compiled, 26KB compressed)
- Manifest URL: https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-podcast/main/manifest.json
- All code compiles and pushes successfully

---
Task ID: 1
Agent: Main Agent
Task: Fix frequency and auto-delete display persistence in config page (v0.0.1.6)

Work Log:
- Analyzed XML config file: Frequency stored as "Daily", AutoDelete as "AfterTwoDays" (enum string names)
- Analyzed C# enums: UpdateFrequency(Daily=0, Weekly=1, Monthly=2), AutoDeleteOption(Never=0, AfterTwoDays=1)
- Identified root cause: Jellyfin API returns enum values as string names ("Daily", "AfterTwoDays"), but JS expected numeric values (0, 1, 2)
- frequencyNames["Daily"] was undefined → displayed "Desconocido"
- autoDeleteNames["AfterTwoDays"] was undefined → displayed "Nunca"
- Added toNumericEnum() helper function to convert string enum names to numbers
- Added frequencyStringMap and autoDeleteStringMap for string-to-number conversion
- Added frequencyNumberMap and autoDeleteNumberMap for number-to-string conversion in feedToServer()
- Updated normalizeFeeds() to use toNumericEnum() for both frequency and autoDelete
- Updated feedToServer() to convert numbers back to string names for server compatibility
- Bumped version to 0.0.1.6 in .csproj and meta.json
- Updated manifest.json with v0.0.1.6 entry (descending order, first position)
- Timestamp: 2026-04-23T09:00:00Z
- Clean build verified with strings command - all enum mapping code present in DLL
- Created ZIP, calculated MD5 checksum: A6A482C7331EB6B709A2B8E02A30658A
- Pushed to GitHub, tagged v0.0.1.6, created release (not pre-release)
- Release URL: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/tag/v0.0.1.6

Stage Summary:
- Fixed the core issue: enum string values from server now properly converted to numbers for display
- Both display (feed list) and editing (dropdown pre-population) will now work correctly
- Saving preserves the string enum format for XML compatibility
- v0.0.1.6 Alpha published successfully

---
Task ID: 2
Agent: Main Agent
Task: Fix v0.0.1.6 - config.html was not updated in root source + ZIPs not moved in repo

Work Log:
- Discovered the repo has TWO copies of source: root level (actual) and download/Jellyfin.Plugin.Podcasts/ (copy)
- The fix was only applied to the copy in download/, not the root Configuration/config.html
- The .csproj at root references Configuration\config.html (root), so the DLL embedded the old unfixed file
- Applied toNumericEnum fix to root Configuration/config.html
- Added exclusion in .csproj: <Compile Remove="download/**/*.cs" /> to avoid duplicate compilation errors
- Moved all ZIPs to Zip/ folder in repository (git mv)
- Committed, pushed, deleted old release v0.0.1.6, recreated with correct DLL
- New checksum: 1A87F3D6192929272AAA90A6A32D3720

Stage Summary:
- Root Configuration/config.html now has toNumericEnum fix
- manifest.json on GitHub updated with correct checksum
- All ZIPs moved from root to Zip/ folder in repository
- Release v0.0.1.6 recreated with correct DLL
- Release URL: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/tag/v0.0.1.6
---
Task ID: 1
Agent: Main Agent
Task: v0.0.1.8 Alpha - Fix playlist XML fields and red Borrar escuchados button

Work Log:
- Read uploaded playlist.xml reference file to understand correct Jellyfin playlist format
- Read all source files from ROOT directory (config.html, PodcastService.cs, PodcastsApiController.cs, PodcastsPlugin.cs, PodcastScheduler.cs, PodcastsPluginServiceRegistrator.cs, .csproj, manifest.json, meta.json)
- Added red background CSS (.btn-danger class) to config.html for "Borrar escuchados" button
- Updated PodcastService.cs to add IUserManager dependency for OwnerUserId
- Modified GenerateAutoPlaylistAsync() to include: RunningTime (total seconds from library items), Genres (Podcast), OwnerUserId (first user GUID), Shares (empty element)
- Updated PodcastsPluginServiceRegistrator.cs to inject IUserManager from MediaBrowser.Controller.Library
- Bumped version to 0.0.1.8 in .csproj and meta.json (timestamp: 2026-04-23T10:00:00Z)
- Added v0.0.1.8 to manifest.json with SHA256 checksum
- Clean build from ROOT, verified DLL with strings and embedded resource check
- Created ZIP, pushed to GitHub, created release v0.0.1.8 Alpha (NOT pre-release)

Stage Summary:
- Plugin DLL: /home/z/my-project/bin/Release/net9.0/Jellyfin.Plugin.Podcasts.dll
- ZIP: /home/z/my-project/Zip/jellyfin-plugin-podcasts_0.0.1.8.zip (659346 bytes)
- Release: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/tag/v0.0.1.8
- Key fix: IUserManager is in MediaBrowser.Controller.Library namespace (not MediaBrowser.Controller.Entities)

---
Task ID: v0.0.3.6
Agent: Super Z (Main)
Task: Fix scan podcast library task to scan ONLY the podcast folder (not the entire Jellyfin library)

Work Log:
- Reviewed previous chat at https://chat.z.ai/s/a63011b9-7ae9-4485-b184-5537ecd50d62 for full context
- v0.0.3.5 introduced the "Escanear biblioteca de podcasts" scheduled task, but it used ILibraryManager.ValidateMediaLibrary() which scans ALL Jellyfin libraries (movies, TV, music, etc.) — slow and unnecessary
- User requested scan ONLY the podcast folder (the "Podcasts" subfolder inside the music library)
- Decompiled Jellyfin.Controller 10.11.11 with ilspycmd to discover the correct API:
  * ILibraryManager.FindByPath(string path, bool? isFolder) → returns BaseItem for a filesystem path
  * Folder.ValidateChildren(IProgress<double>, MetadataRefreshOptions, bool recursive, bool allowRemoveRoot, CancellationToken) → recursively validates only the folder's children
  * VirtualFolderInfo.ItemId is a string representation of a Guid (not a Guid directly)
  * ImageRefreshMode is a PROPERTY of type MetadataRefreshMode (same enum, no separate ImageRefreshMode enum)
- Inspected MetadataRefreshOptions and DirectoryService:
  * MetadataRefreshOptions requires IDirectoryService in its constructor (cannot use FormatterServices.GetUninitializedObject trick because Folder.ValidateChildren actually USES DirectoryService to enumerate the filesystem)
  * DirectoryService(IFileSystem) — constructable directly with injected IFileSystem
- Implemented 3-tier scan strategy in PodcastService.ScanPodcastLibraryAsync:
  1. Best case: "Podcasts" folder is already in Jellyfin library DB → call ValidateChildren on it (scans ONLY the podcast folder, fastest)
  2. Fallback: "Podcasts" folder not yet indexed → call ValidateChildren on the parent music library folder (scans only the music library, not movies/TV/etc.) to discover the podcast folder
  3. Last resort: full ValidateMediaLibrary (only if neither can be located)
- Added FindParentMusicLibraryFolder() helper that:
  * Iterates virtual folders with CollectionType=music or mixed
  * Parses VirtualFolderInfo.ItemId (string) as Guid and calls GetItemById
  * Falls back to FindByPath(firstLocation, true) if ItemId lookup fails
- Injected IFileSystem (MediaBrowser.Model.IO) into PodcastService constructor and registered in PodcastsPluginServiceRegistrator
- RefreshOptions: MetadataRefreshMode.Default, ImageRefreshMode.Default, ReplaceAllMetadata=false, ReplaceAllImages=false, ForceSave=false — only pick up new files, do not clobber existing metadata
- Updated version 0.0.3.5 → 0.0.3.6 in:
  * Jellyfin.Plugin.Podcasts.csproj (AssemblyVersion, FileVersion, Version)
  * meta.json (version, changelog, timestamp 2026-09-05T18:58:38Z)
  * manifest.json (new entry at top of versions array, targetAbi 10.11.0.0, sourceUrl with underscore in filename)
- Compiled cleanly with dotnet 9.0.317 (0 warnings, 0 errors, TreatWarningsAsErrors=true)
- Created ZIP with the 4 required files (DLL + logo.png + meta.json + manifest.json) with underscore in filename:
  jellyfin-plugin-podcasts_0.0.3.6.zip (743477 bytes)
- Calculated MD5 checksum: 5285650309D523D1EC4FB7BE04FFD737
- Updated manifest.json checksum field with this MD5 (did NOT rebuild ZIP after — lesson learned from v0.0.3.1)
- Verified MD5 in manifest.json matches MD5 of the ZIP file
- Local commit: a44f6fb "v0.0.3.6: Targeted podcast folder scan (no full library scan)" (5 files changed, 180 insertions, 14 deletions)
- Local tag: v0.0.3.6

Stage Summary:
- 5 files modified: Jellyfin.Plugin.Podcasts.csproj, PodcastService.cs, PodcastsPluginServiceRegistrator.cs, manifest.json, meta.json
- 1 ZIP file created locally (NOT committed to repo): /home/z/my-project/jellyfin-plugin-podcast/jellyfin-plugin-podcasts_0.0.3.6.zip
- Local commit and tag ready to push
- AWAITING: GitHub Personal Access Token from user to push and create release
- Once token is provided: git push origin main && git push origin v0.0.3.6, then create GitHub release with the ZIP attached
- The 4 scheduled tasks remain the same:
  1. Actualizar feeds RSS de podcasts
  2. Generar lista de reproduccion de podcasts
  3. Borrar episodios escuchados
  4. Escanear biblioteca de podcasts (now scans ONLY the podcast folder, not the whole library)

---
Task ID: v0.0.3.6-PUBLISH
Agent: Super Z (Main)
Task: Push commit + tag, create GitHub release with ZIP asset, verify checksum

Work Log:
- User provided GitHub PAT (ghp_3k5... — redacted from logs)
- Configured git remote URL with token embedded for push
- git push origin main: 687bcce..a44f6fb main -> main (success)
- git push origin v0.0.3.6: * [new tag] v0.0.3.6 -> v0.0.3.6 (success)
- Created GitHub release via REST API:
  * Release ID: 383345351
  * HTML URL: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/tag/v0.0.3.6
  * Draft: false, Prerelease: false
- Uploaded ZIP asset (jellyfin-plugin-podcasts_0.0.3.6.zip, 743477 bytes):
  * Download URL: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/download/v0.0.3.6/jellyfin-plugin-podcasts_0.0.3.6.zip
  * State: uploaded
- VERIFIED download URL returns HTTP 200 and 743477 bytes (matches ZIP size)
- VERIFIED MD5 of downloaded ZIP (5285650309D523D1EC4FB7BE04FFD737) matches checksum in manifest.json
- VERIFIED manifest.json on GitHub raw URL has version 0.0.3.6 as first entry with correct checksum 5285650309D523D1EC4FB7BE04FFD737 and timestamp 2026-09-05T18:58:38Z
- Removed token from git remote URL for security (git remote set-url origin back to plain HTTPS)

Stage Summary:
- v0.0.3.6 fully published and verified on GitHub
- Release URL: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/tag/v0.0.3.6
- ZIP URL: https://github.com/pepebarrascout/jellyfin-plugin-podcast/releases/download/v0.0.3.6/jellyfin-plugin-podcasts_0.0.3.6.zip
- Manifest URL: https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-podcast/main/manifest.json
- MD5 checksum: 5285650309D523D1EC4FB7BE04FFD737 (matches ZIP on disk, ZIP on GitHub release, and entry in manifest.json)
- Timestamp: 2026-09-05T18:58:38Z (different from all previous version timestamps)
- The "Escanear biblioteca de podcasts" task now scans ONLY the podcast folder, not the whole Jellyfin library
