# Notice

KiskEmu is an independent, non-commercial community project for education and interoperability research. It is not
affiliated with, endorsed by or connected to NCSoft Corporation. AION and AION2 are trademarks of NCSoft Corporation;
they are named only to say which client KiskEmu works with.

KiskEmu started as a fork of A2Core (https://github.com/Addiboii/A2Core) and keeps its history and authorship;
contributions merged from other A2Core forks keep theirs too. KiskLauncher renames an older launcher's data folder
`A2CorePrivate` to `KiskPrivate` at its first start, and removes client patch paks under the old name
`z_A2Core_Patch-Windows_P` (now `z_KiskEmu_Patch-Windows_P`).

The code is original work under the AGPL-3.0 (see LICENSE), with third-party open-source code under its own license:
CUE4Parse (Apache-2.0, `third_party/CUE4Parse`) and the launcher's NuGet packages SteamKit2 (LGPL-2.1) and QRCoder
(MIT). The game data the server runs on (`tools/data/*.json`, `data/aion-server-data.tar.gz`,
`research/local/tooling/mappings`) is derived from the game client and is not covered by the AGPL.

The client type mappings (`research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap`) are the community mappings
for Global 2.0.6.0 from kelekelio/aion2-usmaps (https://github.com/kelekelio/aion2-usmaps).

Opcode names in `research/local/tooling/opcodes.json` and several packet layouts were checked against nuriland/a2kit
(https://github.com/nuriland/a2kit, commit fb693ba), MIT License, Copyright (c) 2026 nuri. NOIA2, Aether-Aion2-DPS-meter-Global
and AIon2-Dps-Meter (GPL-3.0) were used only to compare packet layouts; no code was copied from them.

KiskEmu contains no game client and no client archive key. Players use their own copy of the game, obtained through
their own Steam account.

Do not use KiskEmu for commercial purposes: no paid access, donations for perks or item sales.
