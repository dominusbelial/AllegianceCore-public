#!/usr/bin/env python3
"""Regenerate the checked-in macOS icon from allegiance.svg (FFmpeg with librsvg)."""
from pathlib import Path
import struct
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent
chunks = []
with tempfile.TemporaryDirectory(prefix='allegiance-icon-') as temp:
    for size, kind in [(128, b'ic07'), (256, b'ic08'), (512, b'ic09'), (1024, b'ic10')]:
        svg = ET.parse(root / 'allegiance.svg')
        svg.getroot().set('width', str(size))
        svg.getroot().set('height', str(size))
        source = Path(temp) / f'{size}.svg'
        png = source.with_suffix('.png')
        svg.write(source)
        subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-i', str(source),
                        '-frames:v', '1', '-y', str(png)], check=True)
        data = png.read_bytes()
        chunks.append(kind + struct.pack('>I', len(data) + 8) + data)
data = b''.join(chunks)
(root / 'allegiance.icns').write_bytes(b'icns' + struct.pack('>I', len(data) + 8) + data)
