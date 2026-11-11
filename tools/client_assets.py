"""Inspect the v611 game assets without modifying the client files."""
import argparse
import json
import struct
import zlib
from collections import Counter
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUN_WIDTHS = bytes(value & 0x7f for value in range(256))

def archive_index(path):
    size = path.stat().st_size
    with path.open("rb") as source:
        count, = struct.unpack("<I", source.read(4))
        assert 1 <= count <= (size - 4) // 68, path
        directory = source.read(count * 68)
    offsets = [struct.unpack_from("<I", directory, i * 68)[0] for i in range(count)]
    assert offsets == sorted(offsets) and offsets[0] == 4 + 68 * count and offsets[-1] == size, path
    entries = []
    for i, (start, end) in enumerate(zip(offsets, offsets[1:])):
        name = directory[i * 68 + 4:(i + 1) * 68].decode("utf-16-le", "replace").split("\0", 1)[0]
        entries.append({"name": name, "offset": start, "length": end - start})
    return {"bytes": size, "extensions": dict(Counter(Path(e["name"]).suffix for e in entries)), "entries": entries}


def epf_render_rows(block, width, height):
    rows = block.split(b'\0', height)
    if len(rows) != height + 1 or any(sum(row.translate(RUN_WIDTHS)) != width + 1 for row in rows[:-1]):
        raise ValueError('Invalid EPF transparency runs')
    return rows[:-1], rows[-1]


def epf_frames(payload, *, require_render_masks=False):
    count, _, _, _, table_offset = struct.unpack_from('<HHHHI', payload)
    table = 12 + table_offset
    end = table + count * 16
    sentinel = struct.pack('<hhhhII', 0, 0, 0, 0, table_offset, 0)
    if end > len(payload) or payload[end:] not in (b'', sentinel):
        raise ValueError('Invalid EPF frame table')
    for index in range(count):
        top, left, bottom, right, start, end = struct.unpack_from('<hhhhII', payload, table + index * 16)
        if not (0 <= start <= end <= table_offset and left <= right and top <= bottom
                and (right-left)*(bottom-top) == end-start):
            raise ValueError(f'Invalid EPF frame {index}')
        pixels = payload[12+start:12+end]
        if require_render_masks and pixels:
            following = struct.unpack_from('<I', payload, table + (index+1)*16 + 8)[0] if index+1 < count else table_offset
            width = right-left
            rows, _ = epf_render_rows(payload[12+end:12+following], width, bottom-top)
            for y, row in enumerate(rows):
                mask = b''.join(bytes([run >> 7]) * (run & 0x7f) for run in row)
                if mask[:-1] != bytes(map(bool, pixels[y*width:(y+1)*width])):
                    raise ValueError(f'EPF transparency disagrees with pixels in frame {index}, row {y}')
        yield (left, top, right, bottom), pixels


def equipment_layer_assets(layer, word, left_word=0):
    """Resolve the S33 equipment word as the v611 renderer does."""
    if layer in (0, 1):
        # 0045D900 clears bits 8–11 before formatting the hand model.
        model = word & 0xF0FF
        if model == 0:
            return ()
        if layer == 1:
            return (f'LA{model:05X}.EPF',)
        right = f'RA{model:05X}.EPF'
        if not left_word & 0xF0FF and model >> 12 in (2, 4, 8, 11):
            return right, f'LA{model:05X}.EPF'
        return (right,)
    prefixes = {2: 'AR', 3: 'UP', 4: 'DO', 5: 'BT', 6: 'HL'}
    # 004DD588 unpacks the high nibble as the palette for these layers.
    model = word & 0xFFF
    return (f'{prefixes[layer]}{model:05d}.EPF',) if model and layer in prefixes else ()


def metadata_index(path):
    data = path.read_bytes()
    count, index_size = struct.unpack_from("<II", data)
    offset, body = 8, 8 + index_size
    entries = []
    for _ in range(count):
        chars = data[offset]
        name = data[offset + 1:offset + 1 + 2 * chars].decode("utf-16-le")
        offset += 1 + 2 * chars
        size, = struct.unpack_from(">I", data, offset)
        offset += 4
        decoded = zlib.decompress(data[body:body + size])
        rows, = struct.unpack_from(">H", decoded)
        position = 2
        field_counts = Counter()
        for _ in range(rows):
            key_size = decoded[position]
            key_end = position + 1 + key_size
            decoded[position + 1:key_end].decode("cp949")
            fields, = struct.unpack_from(">H", decoded, key_end)
            field_counts[fields] += 1
            position = key_end + 2
            for _ in range(fields):
                field_size, = struct.unpack_from(">H", decoded, position)
                field_end = position + 2 + field_size
                assert field_end <= len(decoded), name
                decoded[position + 2:field_end].decode("cp949")
                position = field_end
        assert position == len(decoded), name
        entries.append({"name": name, "compressedBytes": size, "decodedBytes": len(decoded),
                        "crc32": f"{zlib.crc32(decoded):08x}", "rows": rows, "fieldCounts": dict(field_counts)})
        body += size
    assert offset == 8 + index_size and body == len(data)
    return entries

@lru_cache(maxsize=32768)
def validate_render_block(payload, width, height):
    lines = payload.split(b"\0")
    if len(lines) != height + 1 or lines[-1]:
        return False
    return all(sum(line.translate(RUN_WIDTHS)) == width for line in lines[:-1])

def map_index(path):
    data = path.read_bytes()
    width, height = data[2:4]
    assert width and height, path
    layer_bytes = 4 * width * height * 5
    position = 4 + layer_bytes
    trailer = len(data) - position
    columns, rows = (width + 26) // 27, (height + 1) // 2
    chunks = columns * rows
    floors = []
    for floor in range(4):
        flags = data[position:position + chunks]
        position += chunks
        offsets = struct.unpack_from(f"<{chunks + 1}I", data, position)
        position += 4 * (chunks + 1)
        assert offsets[0] == 0 and tuple(sorted(offsets)) == offsets, path
        for i, (start, end) in enumerate(zip(offsets, offsets[1:])):
            block_width = 24 * min(27, width - 27 * (i % columns)) + 1
            block_height = 24 * min(2, height - 2 * (i // columns))
            payload = data[position + start:position + end]
            assert validate_render_block(payload, block_width, block_height), (path, floor, i)
        floors.append({"markedChunks": sum(bool(f) for f in flags), "payloadBytes": offsets[-1],
                       "verifiedRenderBlocks": chunks})
        position += offsets[-1]
    assert position == len(data), path
    return {"leadingBytes": list(data[:2]), "width": width, "height": height,
            "baseLayerBytes": layer_bytes, "trailingBytes": trailer,
            "chunkColumns": columns, "chunkRows": rows, "chunkFloors": floors}

def map_tile(path, x, y, floor):
    with path.open("rb") as source:
        tile_set, palette, width, height = struct.unpack("<BBBB", source.read(4))
        if not (0 <= x < width and 0 <= y < height and 0 <= floor < 4):
            raise ValueError(f"Tile outside {width}x{height} map or floor outside 0–3")
        offset = 4 + 5 * ((floor * height + y) * width + x)
        source.seek(offset)
        ground, static, flags = struct.unpack("<HHB", source.read(5))
    return {"map": path.stem, "width": width, "height": height,
            "tileSet": tile_set, "palette": palette, "x": x, "y": y, "floor": floor,
            "offset": offset, "groundFrame": ground, "staticObject": static,
            "flags": f"0x{flags:02X}", "renderBlock": [x // 27, y // 2]}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tile", nargs=4, type=int, metavar=("MAP", "X", "Y", "FLOOR"),
                        help="inspect one tile by its map ID and zero-based coordinates")
    args = parser.parse_args()
    catalog = json.loads((ROOT / "evidence/packet-catalog.json").read_text(encoding="utf-8"))
    client = Path(catalog["client"]).parent
    if args.tile:
        map_id, x, y, floor = args.tile
        if not 0 <= map_id <= 65535:
            parser.error("Map ID outside 0–65535")
        base_id = map_id % 10000 if map_id < 60000 else map_id
        try:
            tile = map_tile(client / "Maps" / f"{base_id:05d}.map", x, y, floor)
        except (OSError, ValueError, struct.error) as exc:
            parser.error(str(exc))
        print(json.dumps(tile, indent=2))
        return
    archives = {path.name: archive_index(path) for path in sorted((client / "Data").glob("*.dat"))}
    maps = {path.stem: map_index(path) for path in sorted((client / "Maps").glob("*.map"))}
    minimaps = {p.stem: p.stat().st_size for p in sorted((client / "MiniMap").glob("*.mmp"))}
    metadata = metadata_index(client / "Profiles/Meta.dat")
    evidence = {"client": str(client), "archives": archives, "maps": maps, "minimaps": minimaps, "metadata": metadata}
    (ROOT / "evidence/client-assets.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print("Archives", len(archives), "resources", sum(len(a["entries"]) for a in archives.values()),
          "maps", len(maps), "minimaps", len(minimaps), "metadata", len(metadata),
          "rows", sum(e["rows"] for e in metadata),
          "render blocks", sum(f["verifiedRenderBlocks"] for m in maps.values() for f in m["chunkFloors"]))

if __name__ == "__main__":
    main()
