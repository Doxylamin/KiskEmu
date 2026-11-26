"""Write the launcher's reference for one client game version (launcher/builds/<game version>.json).

A server offers the game versions it is built for (Aion:Native:ClientVersions); each has a reference here,
named by the version the client sends in its greeting: 3527 (2.0.6, Steam depot 3393111 manifest
1699273097574069541) and 3533 (Steam build 26262663, manifest 586322693862358479). Source: a verified copy's
verification.json (research/local/dev-client/verification.json for 3527), or --scan <client folder>, which
hashes a copy itself. The output lists every file's relative path, size and SHA-256 (hashes only, no game data). The launcher checks sizes of all
files at each launch, hashes the "quick" set (AION2.exe and the pak index/signature files) and
offers a full verification.

With --depot-manifest (Steam's depotcache/3393111_<manifest>.manifest, written when the player
runs download_depot or updates), only the files the depot itself contains are listed. A copy that
has been run also holds files the game creates (Aion2/InstallLog.txt, and NCGuard's own
Aion2/Binaries/Win64/NCGuard/bb64.dll); a fresh download does not have them.
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'research/local/dev-client/verification.json'
BUILDS_DIR = ROOT / 'launcher/builds'
EXECUTABLE = 'Aion2/Binaries/Win64/AION2.exe'
# Game version -> its Steam depot manifest (the download coordinates; the version is the build's name).
MANIFESTS = {3527: '1699273097574069541', 3533: '586322693862358479'}
DEFAULT_VERSION = 3527


def coordinates(version):
    if version not in MANIFESTS:
        raise ValueError(f'Unknown game version {version}: add its Steam manifest to MANIFESTS')
    return {'gameVersion': version, 'appId': 3393110, 'depotId': 3393111, 'manifestId': MANIFESTS[version],
            'executable': EXECUTABLE}


def quick(path):
    """Files hashed before every launch: the game binary and the pak index/signature files."""
    lower = path.lower()
    return lower == EXECUTABLE.lower() or (lower.startswith('aion2/content/paks/') and lower.endswith(('.utoc', '.sig')))


def _varint(data, i):
    value = shift = 0
    while True:
        byte = data[i]
        i += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            return value, i


def _fields(data):
    """Protobuf wire fields: (number, wire type, value)."""
    i = 0
    while i < len(data):
        key, i = _varint(data, i)
        number, kind = key >> 3, key & 7
        if kind == 0:
            value, i = _varint(data, i)
        elif kind == 2:
            length, i = _varint(data, i)
            value, i = data[i:i + length], i + length
        elif kind == 1:
            value, i = data[i:i + 8], i + 8
        elif kind == 5:
            value, i = data[i:i + 4], i + 4
        else:
            raise ValueError(f'Unsupported protobuf wire type {kind}')
        yield number, kind, value


MANIFEST_PAYLOAD_MAGIC = 0x71F617D0
MANIFEST_METADATA_MAGIC = 0x1F4812BE
DIRECTORY_FLAG = 0x40


def depot_manifest(data):
    """Steam depot manifest (depotcache): {'depotId', 'manifestId', 'files': {path: size}}; directories skipped."""
    magic, length = struct.unpack_from('<II', data, 0)
    if magic != MANIFEST_PAYLOAD_MAGIC:
        raise ValueError('Not a Steam depot manifest')
    payload = data[8:8 + length]
    meta_magic, meta_length = struct.unpack_from('<II', data, 8 + length)
    if meta_magic != MANIFEST_METADATA_MAGIC:
        raise ValueError('Depot manifest metadata missing')
    meta = {n: v for n, k, v in _fields(data[16 + length:16 + length + meta_length]) if k == 0}
    files = {}
    for number, kind, value in _fields(payload):
        if number != 1 or kind != 2:
            continue
        entry = {n: v for n, _, v in _fields(value) if n in (1, 2, 3)}
        if entry.get(3, 0) & DIRECTORY_FLAG:
            continue
        files[entry[1].decode('utf-8').replace('\\', '/')] = entry.get(2, 0)
    return {'depotId': meta.get(1), 'manifestId': str(meta.get(2)), 'files': files}


def scan(folder):
    """Hash a client copy into the verification.json shape (every file under the folder, '/' paths)."""
    folder = Path(folder)
    files = []
    for path in sorted(p for p in folder.rglob('*') if p.is_file()):
        digest = hashlib.sha256()
        with path.open('rb') as handle:
            for chunk in iter(lambda: handle.read(1 << 22), b''):
                digest.update(chunk)
        files.append({'path': path.relative_to(folder).as_posix(), 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()})
    return {'status': 'verified', 'errors': [], 'file_count': len(files), 'files': files}


def build(verification, depot=None, version=DEFAULT_VERSION):
    BUILD = coordinates(version)
    if verification.get('status') != 'verified' or verification.get('errors'):
        raise ValueError('verification.json is not a clean verified copy')
    files = sorted(({'path': f['path'].replace('\\', '/'), 'size': f['bytes'], 'sha256': f['sha256'].lower()}
                    for f in verification['files']), key=lambda f: f['path'].lower())
    if len(files) != verification['file_count'] or not any(f['path'] == BUILD['executable'] for f in files):
        raise ValueError('File list incomplete')
    if depot is not None:
        if depot['depotId'] != BUILD['depotId'] or depot['manifestId'] != BUILD['manifestId']:
            raise ValueError(f"Depot manifest is {depot['depotId']}/{depot['manifestId']}, not the supported build")
        listed = {path.lower(): size for path, size in depot['files'].items()}
        have = {f['path'].lower(): f for f in files}
        missing = sorted(set(listed) - set(have))
        wrong = sorted(p for p in set(listed) & set(have) if have[p]['size'] != listed[p])
        if missing or wrong:
            raise ValueError(f'Copy differs from the depot: missing {missing[:5]}, sizes {wrong[:5]}')
        files = [f for f in files if f['path'].lower() in listed]
    for f in files:
        f['quick'] = quick(f['path'])
    # One digest over the quick set (path, size, hash), to name the build in logs and support.
    digest = hashlib.sha256()
    for f in files:
        if f['quick']:
            digest.update(f"{f['path'].lower()}\0{f['size']}\0{f['sha256']}\n".encode())
    return {**BUILD, 'fingerprint': digest.hexdigest(), 'fileCount': len(files),
            'totalBytes': sum(f['size'] for f in files), 'files': files}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-version', type=int, default=DEFAULT_VERSION, help='the version the client greets with')
    parser.add_argument('--source', type=Path, default=SOURCE, help='verification.json of a verified copy')
    parser.add_argument('--scan', type=Path, help='hash this client folder instead of reading --source')
    parser.add_argument('--output', type=Path, help='default: launcher/builds/<game version>.json')
    parser.add_argument('--depot-manifest', type=Path, help='Steam depotcache/3393111_<manifest>.manifest')
    args = parser.parse_args(argv)
    depot = depot_manifest(args.depot_manifest.read_bytes()) if args.depot_manifest else None
    verification = scan(args.scan) if args.scan else json.loads(args.source.read_text(encoding='utf-8'))
    result = build(verification, depot, args.game_version)
    args.output = args.output or BUILDS_DIR / f'{args.game_version}.json'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{args.output}: {result['fileCount']} files, {sum(f['quick'] for f in result['files'])} quick, "
          f"fingerprint {result['fingerprint'][:16]}")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
