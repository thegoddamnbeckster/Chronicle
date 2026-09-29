#!/usr/bin/env python3
"""Generates the Inno Setup wizard images (WizardImage*.bmp, WizardSmallImage*.bmp) from
Chronicle's own app icon. Run this again whenever chronicle.ico changes -- these four .bmp
files are checked into installer/ rather than generated at build time, since they're small,
rarely change, and a broken image-generation step shouldn't be able to block a release build.

Root-caused live (2026-09-29): Inno's modern wizard style only shows the icon in a small
header-corner slot without these, which reads as "a dot" at that size in any installer --
these give it a proper, clearly-legible presence down the left side of every wizard page too.

Usage: python generate-wizard-images.py
(Requires Pillow: pip install pillow)
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent
ICON_PATH = ROOT.parent / "src" / "Chronicle.API" / "chronicle.ico"

# Chronicle's own brand purple (#7C3AED) -- matches the web app's login screen and header.
BACKGROUND = (124, 58, 237)


def load_icon_frame(size: int) -> Image.Image:
    """Loads the specific size frame embedded in the .ico (a multi-resolution .ico is really
    several images in one file; Pillow needs telling which one to decode)."""
    im = Image.open(ICON_PATH)
    im.size = (size, size)
    im.load()
    return im.convert("RGBA")


def make_wizard_image(icon: Image.Image, width: int, height: int, icon_target: int, out_path: Path) -> None:
    canvas = Image.new("RGB", (width, height), BACKGROUND)
    resized = icon.resize((icon_target, icon_target), Image.LANCZOS)
    x = (width - icon_target) // 2
    y = (height - icon_target) // 3  # slightly above center -- standard wizard banner composition
    canvas.paste(resized, (x, y), resized)
    canvas.save(out_path)
    print(f"wrote {out_path} ({width}x{height})")


def main() -> None:
    icon = load_icon_frame(256)
    # Classic size + Inno's documented high-DPI 2x variant, for both the large banner (shown
    # down the left of every wizard page) and the small header-corner image.
    make_wizard_image(icon, 164, 314, 100, ROOT / "WizardImage.bmp")
    make_wizard_image(icon, 192, 386, 118, ROOT / "WizardImage2x.bmp")
    make_wizard_image(icon, 55, 58, 40, ROOT / "WizardSmallImage.bmp")
    make_wizard_image(icon, 110, 116, 80, ROOT / "WizardSmallImage2x.bmp")


if __name__ == "__main__":
    main()
