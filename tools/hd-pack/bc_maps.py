"""Schema-3 mip preparation and DDS DX10 writing. Codec: MIT ISPCTextureCompressor."""
import math
from pathlib import Path
import struct
from PIL import Image

DEFAULT_POLICY = {'capital': 4096, 'station': 4096, 'small': 2048, 'projectile': 1024}
DXGI = {7: 99, 5: 83, 4: 80, 3: 78}  # sRGB colour, UNORM data


def atlas_class(stem, catalog, policy):
    classes = []
    for row in catalog['models']:
        if stem not in [s.lower() for s in row['stems']]:
            continue
        kind = row['kind']
        # Catalogue exports actual IGC hull capabilities; never infer from names.
        cls = 'station' if kind == 'station' else ('capital' if row.get('capital', False) else
              'small' if kind == 'ship' else 'projectile' if kind in ('missile', 'probe', 'projectile') else 'small')
        classes.append(cls)
    return max(classes or ['small'], key=lambda c: policy[c])


def resize(data, size):
    import numpy as np
    return np.stack([np.asarray(Image.fromarray(data[:, :, i]).resize(size, Image.Resampling.BOX))
                     for i in range(data.shape[2])], axis=2)


def samples(data, name):
    import numpy as np
    result = data.copy()
    if name in ('basecolor', 'emission'):
        rgb = result[:, :, :3]
        result[:, :, :3] = np.where(rgb <= .0031308, rgb * 12.92, 1.055 * np.maximum(rgb, 0) ** (1 / 2.4) - .055)
    if name == 'normal':
        xyz = result[:, :, :3]
        length = np.linalg.norm(xyz, axis=2, keepdims=True)
        xyz = np.divide(xyz, length, out=np.zeros_like(xyz), where=length > 1e-8)
        xyz[:, :, 2] = np.where(length[:, :, 0] > 1e-8, xyz[:, :, 2], 1)
        result[:, :, :3] = xyz * .5 + .5
    return np.uint8(np.clip(result * 255 + .5, 0, 255))


def write_dds(image, path, name, limit, bc):
    import numpy as np
    import ispc_texcomp as codec
    data = np.asarray(image.convert('RGBA'), dtype=np.float32) / 255
    if name in ('basecolor', 'emission'):
        rgb = data[:, :, :3]
        data[:, :, :3] = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
    if name == 'normal':
        data[:, :, :3] = data[:, :, :3] * 2 - 1
    # Never enlarge an authoring source; preserve its aspect ratio.
    ratio = min(1, limit / max(image.size))
    w, h = [2 ** int(math.log2(max(1, int(v * ratio)))) for v in image.size]
    original_size = (w, h)
    if (w, h) != image.size:
        data = resize(data, (w, h))
    levels = int(math.log2(max(w, h))) + 1
    block = 8 if bc == 4 else 16
    header = [124, 0xA1007, h, w, ((w + 3)//4)*((h + 3)//4)*block, 0, levels] + [0]*11
    header += [32, 4, int.from_bytes(b'DX10', 'little'), 0, 0, 0, 0, 0, 0x401008, 0, 0, 0, 0]
    total = 0
    with path.open('wb') as out:
        out.write(b'DDS ' + struct.pack('<31I', *header) + struct.pack('<5I', DXGI[bc], 3, 0, 1, 0))
        for _ in range(levels):
            pixels = samples(data, name)
            # The native codec requires complete 4x4 blocks, including tiny mips.
            padded = np.pad(pixels, ((0, max(0, 4-h)), (0, max(0, 4-w)), (0, 0)), mode='edge')
            channels = 1 if bc == 4 else 2 if bc == 5 else 4
            raw = np.ascontiguousarray(padded[:, :, :channels]).tobytes()
            surface = codec.RGBASurface(raw, max(4, w), max(4, h), max(4, w) * channels)
            if bc == 7:
                encoded = codec.compress_blocks_bc7(surface, codec.BC7EncSettings.from_profile('alpha_fast'))
            else:
                encoded = getattr(codec, 'compress_blocks_bc' + str(bc))(surface)
            size = ((w + 3)//4)*((h + 3)//4)*block
            if len(encoded) != size:
                raise ValueError('BC codec output length mismatch')
            out.write(encoded)
            total += size
            if w == h == 1:
                break
            w, h = max(1, w//2), max(1, h//2)
            data = resize(data, (w, h))
    return {'width': original_size[0], 'height': original_size[1],
            'format': 'BC' + str(bc), 'mipCount': levels, 'residentBytes': total}
