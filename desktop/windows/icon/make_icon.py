"""The Windows app icon, AppIcon.ico: the macOS icon's tile, circle and line (desktop/macos/icon/make_icon.py, imported, so
the two never drift apart), cropped to the tile's edges without Apple's grid margin and shadow, the way Windows draws icons
and the way the web's favicon is cut. Each size is rendered on its own, so the line keeps its minimum width at 16 px.

Usage: .venv\\Scripts\\python.exe desktop\\windows\\icon\\make_icon.py   -> AppIcon.ico next to this file, which the exe
carries (Verticals.csproj). Renders through Chrome (Playwright's chrome channel); packs the .ico with the standard library."""
import importlib.util
import pathlib
import struct

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('mac_icon', HERE.parents[1] / 'macos' / 'icon' / 'make_icon.py')
mac = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mac)

# What Explorer, the taskbar, Alt-Tab and Start ask for, at 100 % to 200 % scale.
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def svg(px, look='AppIcon'):
    off = (mac.CANVAS - mac.TILE) / 2
    tile, mark = mac.mascot(look, mac.TILE / px)
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="{off:g} {off:g} {mac.TILE} {mac.TILE}" width="{px}" height="{px}">
  <path d="{tile}" fill="{mac.LOOKS[look]['tile']}"/>
  {mark}
</svg>"""


def dib(px, rgba):
    """A 32-bit bitmap entry: what every reader of .ico understands (Windows Forms' Icon class reads no PNG below 256)."""
    header = struct.pack('<IiiHHIIiiII', 40, px, px * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = [rgba[y * px * 4:(y + 1) * px * 4] for y in range(px)]
    pixels = b''.join(bytes(b for i in range(0, len(row), 4) for b in (row[i + 2], row[i + 1], row[i], row[i + 3]))
                      for row in reversed(rows))  # bottom-up, BGRA
    mask = b'\0' * (((px + 31) // 32) * 4 * px)  # the alpha channel decides; the AND mask stays clear
    return header + pixels + mask


def ico(images):
    """An .ico with bitmaps up to 128 px and a PNG at 256, the way Windows' own icons are made."""
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b'', b''
    for px, image in images:
        side = 0 if px >= 256 else px
        entries += struct.pack('<BBBBHHII', side, side, 0, 0, 1, 32, len(image), offset + len(data))
        data += image
    return header + entries + data


RASTER = """async ([source, px]) => {
  const image = new Image();
  image.src = 'data:image/svg+xml;base64,' + btoa(source);
  await image.decode();
  const canvas = Object.assign(document.createElement('canvas'), {width: px, height: px});
  const context = canvas.getContext('2d');
  context.drawImage(image, 0, 0, px, px);
  return Array.from(context.getImageData(0, 0, px, px).data);
}"""


def main():
    from playwright.sync_api import sync_playwright
    images = []
    with sync_playwright() as pw:
        browser = pw.chromium.launch(channel='chrome')  # the installed Chrome, as the macOS script uses
        page = browser.new_page(device_scale_factor=1)
        for px in SIZES:
            if px >= 256:
                page.set_viewport_size({'width': px, 'height': px})
                page.set_content(f'<html><body style="margin:0;background:transparent">{svg(px)}</body></html>')
                images.append((px, page.locator('svg').screenshot(omit_background=True)))
            else:
                images.append((px, dib(px, bytes(page.evaluate(RASTER, [svg(px), px])))))
        browser.close()
    (HERE / 'AppIcon.ico').write_bytes(ico(images))
    print(HERE / 'AppIcon.ico')


if __name__ == '__main__':
    main()
