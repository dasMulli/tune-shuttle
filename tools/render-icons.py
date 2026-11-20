"""Renders the raster icons of the web app from src/Bridge.Web/wwwroot/favicon.svg.

favicon.svg is the source of truth; browsers without SVG favicon support (Safari/iOS home screen, bookmark and tab
fallbacks that request /favicon.ico) use the files written here:

  favicon.ico           16/32/48 px, the rounded tile on transparency
  icon-192.png          rounded tile, transparent corners (web app manifest)
  icon-512.png          rounded tile, transparent corners (web app manifest)
  icon-maskable-512.png full-bleed square (manifest "maskable": the platform cuts its own shape)
  apple-touch-icon.png  180 px full-bleed square (iOS rounds the corners itself, transparency would turn black)

Needs Microsoft Edge (or Chrome) for the SVG rendering and Pillow. Run: python tools/render-icons.py
"""
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
WWWROOT = ROOT / "src" / "Bridge.Web" / "wwwroot"
SVG = (WWWROOT / "favicon.svg").read_text(encoding="utf-8")
SIZE = 512

BROWSERS = [
    os.environ.get("ICON_BROWSER", ""),
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    shutil.which("msedge") or "",
    shutil.which("google-chrome") or "",
    shutil.which("chromium") or "",
]


def browser() -> str:
    for b in BROWSERS:
        if b and pathlib.Path(b).exists():
            return b
    sys.exit("No Edge/Chrome found; set ICON_BROWSER to the browser executable.")


def light_only(svg: str) -> str:
    # Raster icons have no prefers-color-scheme: always the light-scheme accent.
    return re.sub(r"@media \(prefers-color-scheme: dark\) \{.*?\}\s*\}", "", svg, flags=re.S)


def render(svg: str, tmp: pathlib.Path, name: str) -> Image.Image:
    html = tmp / f"{name}.html"
    png = tmp / f"{name}.png"
    sized = svg.replace('viewBox="0 0 32 32"', f'viewBox="0 0 32 32" width="{SIZE}" height="{SIZE}"', 1)
    html.write_text(
        "<!doctype html><html><head><style>html,body{margin:0;background:transparent;overflow:hidden}"
        "svg{display:block}</style></head><body>" + sized + "</body></html>",
        encoding="utf-8",
    )
    subprocess.run(
        [browser(), "--headless", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
         "--default-background-color=00000000", f"--window-size={SIZE},{SIZE}",
         f"--user-data-dir={tmp / 'profile'}", f"--screenshot={png}", html.as_uri()],
        check=True, capture_output=True, timeout=60,
    )
    img = Image.open(png).convert("RGBA")
    return img.crop((0, 0, SIZE, SIZE)) if img.size != (SIZE, SIZE) else img


def main() -> None:
    light = light_only(SVG)
    full_bleed = light.replace('<rect width="32" height="32" rx="9"', '<rect width="32" height="32"', 1)
    if full_bleed == light:
        sys.exit("favicon.svg: tile <rect> not found; update render-icons.py.")
    with tempfile.TemporaryDirectory() as t:
        tmp = pathlib.Path(t)
        rounded = render(light, tmp, "rounded")
        square = render(full_bleed, tmp, "square")

    def scaled(img: Image.Image, px: int) -> Image.Image:
        return img.resize((px, px), Image.LANCZOS)

    rounded.save(WWWROOT / "icon-512.png", optimize=True)
    scaled(rounded, 192).save(WWWROOT / "icon-192.png", optimize=True)
    square.save(WWWROOT / "icon-maskable-512.png", optimize=True)
    scaled(square, 180).convert("RGB").save(WWWROOT / "apple-touch-icon.png", optimize=True)
    scaled(rounded, 48).save(WWWROOT / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)],
                             append_images=[scaled(rounded, 16), scaled(rounded, 32)])
    print("Wrote icons to", WWWROOT)


if __name__ == "__main__":
    main()
