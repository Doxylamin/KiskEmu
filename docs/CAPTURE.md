# Recording the official server

Many values in KiskEmu are still marked OURS (our own choice) or CANDIDATE (inferred, not confirmed). The way to settle
them is to watch what the official server sends while you play, then compare. This guide covers recording your own
session, turning it into a readable message timeline, and sharing what you found without exposing your account.

Recording is passive: a Windows network trace of your own client's traffic. Nothing is injected, the client is not
modified, and nothing is sent to the server or to other players.

## What a recording can and cannot show

- **Server to client (readable).** After the handshake the game server's stream (port 13328) is plaintext apart from
  LZ4 compression, which the tools unpack. A recording settles what the server sends: replies, notices, values and
  their timing.
- **Client to server (not readable).** The client ciphers its own direction after the handshake. Request layouts come
  from our own client traces (the server logs what the client sends to KiskEmu) and from the client's type mappings.

## Setup (Windows)

1. Python 3 (standard library only) and Administrator PowerShell.
2. Microsoft's ETL converter **etl2pcapng 1.11.0** from <https://github.com/microsoft/etl2pcapng/releases/tag/v1.11.0>.
   Put `etl2pcapng.exe` (the x64 build) at `research/local/tooling/etl2pcapng-1.11.0/etl2pcapng.exe`. The capture
   script checks its SHA-256 (`C2D03AAC43EA0F5626FC8D909E6688393FEAF71BD6FACD39AAF8A637382D9043`) and refuses
   another file. `research/local/` is ignored by git, so nothing you record there gets committed.

## Record

Start the game normally through Steam on the official server and enter the world. Then, in Administrator PowerShell in
the repository folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/capture.ps1 -InGame -DurationSeconds 1800 -MaxSizeMb 1024
```

- `-InGame` finds the game server your client is connected to (port 13328) and records both link layers, which a
  Wi-Fi connection needs to see the server's direction. Without it the script records the login server (port 13700);
  restart the game while it runs to capture the opening handshake.
- `-ServerAddress '<ip>,<ip>'` adds servers to the one discovered, for example the login server and other game servers
  a map change may move you to. Quote the list.
- `-DurationSeconds` up to 7200; the trace stops by itself. Allow about 4 MB per minute in `-MaxSizeMb` (a 10-minute
  fight was 36 MB), or the buffer wraps.
- `-MovementCues` plays beeps that tell you when to walk and stop (for movement timing).
- `-PlanOnly` prints what would run without recording or needing elevation.

The trace records only TCP traffic to and from those server addresses. Keep the window open when it ends: Windows
takes a few minutes to finish the trace, then `capture.pcapng` appears in `research/local/capture-13700-<date>-<id>/`.

Between activities (not during a fight), an ordinary PowerShell can timestamp a note into the recording:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tools/capture_mark.ps1 "died to the elite, reviving"
```

The note's time is when you typed it, not when the thing happened, so treat it as a cue.

## Decode

```powershell
python tools/capture_timeline.py research/local/capture-13700-<date>-<id>/capture.pcapng
python tools/capture_timeline.py research/local/capture-13700-<date>-<id>/timeline-13328 --grep 0x3642,0x8d00 --dir s2c --limit 50
```

The first command writes `timeline-13328/` next to the capture:

- `timeline.jsonl`: every message in stream order, with capture time, direction, opcode, name, length and body (hex).
- `counts.json`: opcode counts per direction, and reassembly problems (gaps, retransmissions).
- `large/`: bodies too big to inline.

The second command prints the messages you are interested in. Opcode names come from `tools/data/opcode-names.json`;
the server code that builds each message is in `server/Aion.Protocol/Native/*Packets.cs` (search for the opcode).
Comparing an official body with what KiskEmu sends for the same situation is how a CANDIDATE or OURS value becomes
OBSERVED. `tools/inspect_capture.py` and `tools/summarize_session.py` check framing and summarize a session without
printing bodies.

## Sharing safely

A raw recording is personal data. The server's stream carries your character names, account and character IDs,
your inventory, the names and positions of players near you, and chat.

- **Never share** `capture.pcapng`, the `.etl` trace, or a whole `timeline.jsonl`.
- **Do share** the few messages that answer a question: the `--grep` output for those opcodes, with the situation
  ("revived at the obelisk 60 s after death", "bought 1 potion for 50 kinah").
- Before posting, replace character names and other players' names in the hex with same-length placeholders, and say
  you did. Entity and character IDs are session-specific; replace them too if in doubt.
- In a pull request, cite the recording by date and situation; do not add recordings to the repository.

## What is still wanted

The open capture requests are issues on the project's forge, labelled `capture`. Each one says what to do in game,
what is already known (OBSERVED / CANDIDATE / OURS) and where the code is. When a recording answers one, comment the
findings on the issue and close it with the commit that fixes ours.
