"""
Reader for KOTOR .tpc textures (as exported by Holocron Toolset / KotOR Tool) -> PIL image, top row first.

Header (128 bytes): u32 data size (0 = uncompressed), f32 alpha test, u16 width, u16 height, u8 encoding,
u8 mipmap count, padding. Encoding 1 = grey, 2 = RGB (DXT1 when compressed), 4 = RGBA (DXT5 when compressed).
The first mipmap follows the header; a TXI text block (texture options) may follow the image data.
"""
import io
import struct
from pathlib import Path

from PIL import Image


def _dds(width, height, fourcc: bytes, data: bytes) -> bytes:
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000
    pf = struct.pack("<II4s5I", 32, 0x4, fourcc, 0, 0, 0, 0, 0)
    header = struct.pack("<7I", 124, flags, height, width, len(data), 0, 1) + b"\0" * 44 + pf + struct.pack("<5I", 0x1000, 0, 0, 0, 0)
    return b"DDS " + header + data


def read_tpc(path: Path):
    """-> (PIL RGBA image, txi text)."""
    d = Path(path).read_bytes()
    size, _alpha_test, w, h, enc, _mips = struct.unpack_from("<IfHHBB", d, 0)
    body = d[128:]
    if size:
        fourcc = b"DXT1" if enc == 2 else b"DXT5"
        img = Image.open(io.BytesIO(_dds(w, h, fourcc, body[:size]))).convert("RGBA")
        rest = body[size:]
    else:
        bpp = {1: 1, 2: 3, 4: 4}.get(enc, 4)
        mode = {1: "L", 2: "RGB", 4: "RGBA"}[bpp if bpp != 1 else 1] if False else {1: "L", 3: "RGB", 4: "RGBA"}[bpp]
        img = Image.frombytes(mode, (w, h), body[:w * h * bpp]).convert("RGBA")
        rest = body[w * h * bpp:]
    # the TXI is plain text after all mipmaps; keep only the readable tail
    txi = ""
    tail = rest[-2048:]
    for i in range(len(tail)):
        chunk = tail[i:]
        if chunk and all(32 <= b < 127 or b in (9, 10, 13) for b in chunk) and len(chunk) > 3:
            txi = chunk.decode("ascii")
            break
    return img, txi
