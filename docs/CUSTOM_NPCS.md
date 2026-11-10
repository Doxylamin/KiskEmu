# Custom NPCs

Staff can place NPCs of their own in the worlds. Each one looks like an official NPC, has its own name, and talks in the
client's own lower-third dialog. The player continues each line with the interaction key, and the server can act when
the dialog ends: give items or Kina, set a flag, show a notice, teleport.

The pieces:

| Piece | Where |
|-------|-------|
| Definitions (realm table `game.custom_npcs`, migration 102) | staff dashboard → **NPCs**; `server/Aion.Application/Gameplay/CustomNpcs.cs` |
| Placement and dialogs on each world | `server/Aion.World/CustomNpcWorld.cs`, `GameSession.CustomNpcs.cs` |
| Names and lines in the client | the patch pak `z_KiskEmu_CustomNpcs-Windows_P.pak`, built by `kisk-client-tool custom-npcs` (docs/tools/CLIENT_EDITOR.md) |

## Add a custom NPC

1. Open the staff dashboard → **NPCs** → **New NPC**. You need the Administrator rank to edit; Gamemasters can view.
2. Fill in:
   - **Name**: English is required. Switch **Texts in** to add other languages; an empty text uses the English one.
   - **Code name**: letters and digits, for example `Ferryman`. It names the NPC's rows in the patch pak, so keep it
     once the pak is out.
   - **Looks like**: the official NPC to copy, for its model, level and talk prompt. Only NPCs players talk to can be
     picked, not monsters.
   - **Where it stands**: enter a character who is in the picked world, then **Place where they stand**. Up to 20
     places across any maps.
   - **Dialogs**: one or more. Each has a key (letters and digits) and its lines, one per row.
     - When a player talks to the NPC, the first dialog whose **conditions** hold plays. Conditions: level range, race,
       holds an item, has or lacks a flag.
     - Its **actions** run when the player finishes the last line.
3. Save. Every world places the NPC within a minute, without a restart.

Flags belong to one character and one NPC, and are saved in the world's `game.character_settings` under
`custom_npc_flags`. A dialog that ends with **Set a flag** `met` plus a first dialog with the condition **not flag**
`met` pays its reward once; the next talk plays the following dialog. Flags are saved before items are granted, so a
failed save pays nothing rather than paying twice.

Ids: the NPC gets its own NpcData id from 2999000 to 2999999, above every official one (the client's highest is
2990928). Its dialogs become the NpcTalk rows `KISK_Npc_<code name>_<dialog key>`. In the world it uses entity keys
880000-899999.

## Ship the names and lines (patch pak)

Without the pak, players see the official NPC the custom one looks like, with that NPC's name. Each talk then shows the
next line as a centre-screen notice, "Name: line", in the client's language; actions still run after the last line.

To show the custom name and the real dialog:

1. On the dashboard's **NPCs** page, click **Export texts**. It downloads `custom-npcs.json` with every NPC, disabled
   ones too, so enabling one later needs no new pak.
2. On a PC with the supported client installed, build the pak:

   ```bash
   dotnet run --project editor/AionClientEditor.Cli -- custom-npcs --paks "<client>/Aion2/Content/Paks" --key <archive-key.json> --oodle <oodle-data-shared.dll> --definitions custom-npcs.json --out research/local/custom-npcs
   ```

   The tool works like this:
   - It checks that it can rewrite NpcData, NpcTalk, String and every L10NString file byte for byte before it changes
     anything.
   - It adds the NPC rows, one NpcTalk row per line and the texts in every language (missing languages get English).
   - It writes `z_KiskEmu_CustomNpcs-Windows_P.pak`, an empty `.sig` and `custom-npcs.lock.json` with the pak's SHA-256.
   - It reads every new row and text back out of the written pak.

   The pak holds whole client tables: never commit it, and keep `--out` inside `research/local/` or outside the
   repository.
3. Put the pak and the `.sig` into the client's `Aion2/Content/Paks` folder.
   - Server distribution through KiskLauncher is the next step (CANDIDATE, not built yet).
   - Until it exists, a world only sends the custom template ids when `Aion:CustomNpcs:AssumePatchedClients` is true.
     That is for testing with patched clients only: an unpatched client would get NPC ids it does not know.
4. Rebuild the pak whenever names or lines change or an NPC is added; placements, conditions and actions need no new
   pak.

## In-game check (still open)

The server side is covered by `tests/Aion.World.Tests/CustomNpcTests.cs`. The client side is CANDIDATE until it is tried
in game:

- whether the client loads an unsigned extra pak with appended NpcData, NpcTalk and String rows;
- whether `PlayNpcTalk` with the talk name, sent right after the interaction opens (0xe241), plays the lines as a
  continue-style dialog;
- when the client then sends 0xe242. It may close the interaction at once, within 15 ms, when it has no talk of its own
  to play; the actions would then run while the lines still show.

Test steps:

1. Build the pak from a test NPC.
2. Start a private client copy with it.
3. Set `Aion:CustomNpcs:AssumePatchedClients=true` on a test world.
4. Talk to the NPC: check the name, the lines with continue, and the reward at the end.
