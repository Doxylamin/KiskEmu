# KiskEmu Client Editor

Windows desktop tool (`editor/AionClientEditor`) for editing the supported AION2 Global client build (**Steam build 25767555**, manifest `1699273097574069541`) while keeping the shipped paks untouched. It mirrors the launcher stack (.NET 10, WinForms, single-file publish) and reuses the shared mount helper in `tools/AionClientProbe` (same AES + Oodle + usmap path as `tools/WorldProbe`).

## Requirements

- Windows PC with the KiskEmu client installed via the launcher (default Paks folder: `%LOCALAPPDATA%\A2CorePrivate\AION2\Aion2\Content\Paks`).
- Your **archive key JSON** locally (`key_hex` field). Never commit this file.
- Oodle: `oo2core_9_win64.dll` or `oodle-data-shared.dll` (WorldProbe uses `research/local/tooling/oodle-2.9.16/oodle-data-shared.dll`). The open flow lets you pick the DLL; defaults are auto-detected from the repo checkout.
- Usmap: `research/local/tooling/mappings/*.usmap` (default `AION2-5.3.2.0-2.0.6.0.usmap`). Picked on open; last paths are stored in `%LOCALAPPDATA%\\A2CorePrivate\\client-editor-settings.json`.

## Build & publish

```powershell
dotnet build editor/AionClientEditor.Core
dotnet build tests/Aion.ClientEditor.WinFormsBuild/Aion.ClientEditor.WinFormsBuild.csproj
dotnet test tests/Aion.ClientEditor.Tests --filter "FullyQualifiedName~Aion.ClientEditor"
dotnet publish editor/AionClientEditor -c Release -r win-x64 -o out/client-editor -p:DebugType=none
```

## Workflow

1. **Open Paks** — choose the client `Content\Paks` folder and your key JSON.
2. **Browse / filter** — tables under `AION2/Content/Data/…` and `L10NString.dat` per locale.
3. **Edit** — grid edits for L10N keys; data tables show row summaries (full struct editing continues in patch JSON + re-save pipeline).
4. **Save to patch** — writes encrypted on-disk bytes into `%LOCALAPPDATA%\A2CorePrivate\client-patches\` (manifest + payloads). Base paks are not modified.
5. **Build patch pak** — writes `z_A2Core_Patch-Windows_P.pak` (+ empty `.sig` placeholder) into the selected Paks folder and mirrors to `%LOCALAPPDATA%\A2CorePrivate\client-patches\deploy\` for the launcher.
6. **Export / import JSON** — share edits as `patch-project.json` (paths + payload file names; binary payloads stay alongside).
7. **Revert patch** — clears the local patch project.

### Tables commonly edited for private servers

| Path | Purpose |
|------|---------|
| `AION2/Content/Data/Table/ServerName.dat` | Display names for world/server selection (grid shows each row's key, its `ID.Value` or `Name`, plus one column per struct field; nested values as JSON in the details pane) |
| `AION2/Content/Data/Table/ServerList.dat` | Server list entries |
| `AION2/Content/Data/Table/RegionList.dat` | Region list at selection |
| `AION2/Content/Data/Table/CountryList.dat` | Country list at selection |
| `AION2/Content/Data/L10N/<locale>/L10NString.dat` | UI strings |

## Integrity checks and loading patches

### KiskLauncher (documented behavior)

| Check | What it verifies | Patch impact |
|-------|------------------|--------------|
| **Sizes** (every launch) | Every file listed in `launcher/supported-build.json` exists with the expected **size** | **Extra** patch files (`z_A2Core_*`) are **not** in the manifest → **allowed** |
| **Quick hash** (when `.utoc`/`.sig`/exe timestamps change) | SHA-256 of `AION2.exe` and every `Aion2/Content/Paks/**/*.utoc` and `.sig` listed in the manifest | Patch pak adds **new** utoc/sig names → quick set unchanged → **allowed** |
| **Full verify** (player-initiated) | SHA-256 of **all** manifest files including `.ucas` | Unchanged base files pass; patch files are outside the manifest |

Modified **base** utoc/ucas/pak bytes **fail** quick/full checks — the editor never writes them.

**Launcher opt-in:** `launcher-settings.json` → `"AllowClientPatchPaks": true` copies `z_A2Core_Patch-Windows_P.*` from `%LOCALAPPDATA%\A2CorePrivate\client-patches\deploy\` into the game Paks folder immediately before start (`ClientPatchSupport` in `Aion.Launcher.Core`).

### Game client (best-effort, not exhaustively reversed here)

- IoStore containers ship with `.sig` sidecars; `tools/extract_iostore.py` notes signatures are **not** verified during extraction.
- NCGuard / executable integrity beyond pak mounts was **not** fully reversed in this repo. Treat in-game rejection of unsigned patch paks as **possible** until validated on a live client (CANDIDATE: the custom NPC pak is the first one to check in game).
- UE mount order loads later `pakchunk*` / `z_*` names after base chunks; a separate patch pak is the intended modding path (same as other UE titles).

## Headless tool (`kisk-client-tool`)

`editor/AionClientEditor.Cli` runs the same core library without the window. It reads the local client, so run it on a PC with
the game installed and keep its output out of the repository (`research/local/` is ignored).

```bash
dotnet run --project editor/AionClientEditor.Cli -- dump-schema --paks "<client>/Aion2/Content/Paks" --key <key.json> --oodle <oodle.dll> --table NpcData --rows 3 --out research/local/schema
```

```bash
dotnet run --project editor/AionClientEditor.Cli -- custom-npcs --paks "<client>/Aion2/Content/Paks" --key <key.json> --oodle <oodle.dll> --definitions custom-npcs.json --out research/local/custom-npcs
```

- `dump-schema --table NpcData|NpcTalk|String|L10NString[:locale]` writes the columns and sample rows (`--rows`, `--id`, `--find`).
- `custom-npcs` builds `z_KiskEmu_CustomNpcs-Windows_P.pak` (and an empty `.sig`) from the staff dashboard's export
  (docs/CUSTOM_NPCS.md). It first re-serializes every table and L10N file it patches unmodified and refuses to go on unless
  the bytes match the client's own. Then it mounts the written pak and reads every custom row and text back. It also
  writes `custom-npcs.lock.json` (SHA-256, files, template ids, talk names).

## Byte formats (OBSERVED in the supported build)

- **Patch pak:** UE pak version 11 (Fnv64BugFix) like the client's own paks, with an encrypted index and an all-zero GUID.
  - Each entry is stored uncompressed, AES-encrypted and padded to 16 bytes; its 53-byte record is repeated in front of the data.
  - The index has a path-hash table (FNV-1a 64 over the lowercase UTF-16 path, seeded) and the full directory index.
  - The mount point is `../../../AION2/`.
- **Tables (`.dat`):**
  - The version is 13, the same number as the encrypted-container magic, so on-disk bytes go to `FAion2DataTableFile`
    as they are; decrypting first would decrypt them twice.
  - The header's id list holds **one hash per column**, not one per row. Rows are keyed by `ID.Value` (a TableItemId struct)
    or by `Name` (for example String.dat).
  - In tables whose id string has a negative length (NpcData, NpcTalk, String), names (ids, NameProperty, EnumProperty)
    are XORed UTF-16. The key `25 00 a8 00 7e 00 91 00` covers the characters only; the UTF-16 terminator stays `00 00`.
    One-character names are ANSI.
  - StrProperty values are plain UTF-8.
  - The top-level row array has no struct tag. Nested struct arrays carry a full one: property name, `StructProperty`,
    element byte size (int64), struct type, a 16-byte zero GUID and a 0 byte.
- **L10N:**
  - An encrypted file (header 2) holds an int32 1 and then plain Unreal FStrings (namespace, then the key/text map),
    followed by one more int32 0.
  - The unencrypted format XORs each string, except buffers of 4 bytes or fewer.
  - `AION2/Content/Data/Table/L10N/<locale>/L10NString.dat` and `AION2/Content/L10N/Text/<locale>/L10NString.dat`
    are identical per locale. Both are patched; the StartUpData copy (pre-login strings) is left alone.
- **Row append:** `DataTableRows.AppendRow` clones a row, sets cells by path (`Desc.Key`, `Tags[1]`) and keeps the header ids.

## Serialization round-trip tests

`tests/Aion.ClientEditor.Tests` covers:

| Format | Round-trip |
|--------|------------|
| L10N plaintext (`version` 1 map) | read → write → read |
| L10N encrypted container (`uint` 2 + LZ4/AES) | encrypt → decrypt |
| Small `.dat` stream container (`uint` 13, ≤4 KiB, XorAES) | encrypt → decrypt |
| `MapHierarchy` binary | read → write → read |
| Patch project JSON | save → load |
| Patch pak (v11, encrypted index) | write → mount with CUE4Parse → read |
| Encrypted L10N payload | write → read |
| Row append and custom NPC rows | synthetic NpcData / NpcTalk / String tables |
| Name encoding (UTF-16 XOR, ANSI one-character names) | byte-exact against the observed layout |

Real tables need a mounted client and usmap: `kisk-client-tool custom-npcs` checks every table it patches byte-exact
before it writes anything (all 19 files of the supported build match).

## Shared probe library

`tools/AionClientProbe/AionClientSession.cs` centralizes:

- Oodle init
- `DefaultFileProvider` + `GAME_Aion2`
- AES key from JSON
- Usmap mappings

`tools/WorldProbe` now references this library instead of inlining the setup.
