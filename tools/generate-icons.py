"""
Generates the app icons and the in-app logo from brand/CardVault.png.

That file is the source of truth. Everything under client/public/ that this
writes is generated — edit the brand artwork and rerun, don't touch the outputs.

Three places need bitmaps: favicon.ico for browsers and OS shortcuts that still
ask for it, apple-touch-icon.png for iOS "Add to Home Screen" (without it iOS
uses a screenshot of the page), and the manifest icons for Android. The in-app
logo is emitted separately at a sane size, because shipping a 1254px 2.4MB
original to fill a 40px slot is silly.

Run from the repo root:  python tools/generate-icons.py
Requires Pillow.
"""

from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "brand" / "CardVault.png"
OUT = ROOT / "client" / "public"

# The Windows shortcut icon. Not part of the web app, which is exactly why it was
# missed the first time the artwork changed: it lives in install/, the installer
# copies it next to the exe, and Install-DesktopIcon.bat points the shortcut at it.
# Generated here so it can never drift from the brand art again.
SHORTCUT_ICON = ROOT / "install" / "cardvault.ico"

# The in-app logo goes into src/, not public/, so Vite fingerprints it into
# /assets/logo-<hash>.png. That matters more than it looks: a file in public/
# keeps its name forever, so a browser that cached the old artwork under
# /logo.png had no reason to ever ask again — which is exactly what happened, and
# left the header showing the previous logo across several releases. A
# content-hashed URL changes when the picture changes, so the problem cannot
# recur.
APP_LOGO = ROOT / "client" / "src" / "assets" / "logo.png"

# The app's near-black ground. iOS ignores transparency and composites on white,
# so anything that must be opaque gets flattened onto this instead.
GROUND = (6, 7, 12)


def load() -> Image.Image:
    img = Image.open(SOURCE).convert("RGBA")

    # Trim any fully transparent border so the artwork fills the icon square
    # rather than sitting inside invisible padding.
    bbox = img.getchannel("A").getbbox()
    if bbox:
        img = img.crop(bbox)

    # Pad back to a square so nothing is distorted when it's resized.
    side = max(img.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(img, ((side - img.width) // 2, (side - img.height) // 2))
    return square


def resized(img: Image.Image, size: int) -> Image.Image:
    return img.resize((size, size), Image.LANCZOS)


def compact(img: Image.Image) -> Image.Image:
    """
    Palette-reduces before saving. The artwork is full-colour, and a 256px PNG of
    it lands around 145KB — heavy for something the header loads on every page
    view. 256 colours cuts that to about a quarter with no visible difference at
    the sizes these are actually displayed. FASTOCTREE is used because it's the
    only method that keeps the alpha channel.
    """
    return img.quantize(colors=256, method=Image.FASTOCTREE)


def flattened(img: Image.Image, size: int) -> Image.Image:
    out = Image.new("RGB", (size, size), GROUND)
    scaled = resized(img, size)
    out.paste(scaled, (0, 0), scaled)
    return out


# The pokéball, for if the favicon ever needs to be legible at 16px more than it
# needs to carry the wordmark. See the note in main().
#
# These are fractions of the artwork, so they only hold for the artwork they were
# measured against — the previous logo had its pokéball as a vault dial off to the
# right, and this crop framed that. Re-measure it if the brand art is replaced
# again, or focal() will confidently crop the wrong part of the picture.
FOCAL_CROP = (0.26, 0.21, 0.74, 0.69)


def focal(img: Image.Image) -> Image.Image:
    """Crops to FOCAL_CROP and re-squares it, without distorting anything."""
    w = img.width
    left, top, right, bottom = FOCAL_CROP
    crop = img.crop((int(left * w), int(top * w), int(right * w), int(bottom * w)))

    side = max(crop.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(crop, ((side - crop.width) // 2, (side - crop.height) // 2))
    return square


def main() -> None:
    if not SOURCE.exists():
        raise SystemExit(f"Source artwork not found: {SOURCE}")

    OUT.mkdir(parents=True, exist_ok=True)
    art = load()

    # Manifest icons keep their transparency so Android can place them itself.
    compact(resized(art, 512)).save(OUT / "icon-512.png", optimize=True)
    compact(resized(art, 192)).save(OUT / "icon-192.png", optimize=True)

    flattened(art, 180).save(OUT / "apple-touch-icon.png", optimize=True)

    # Browser tabs and OS shortcuts.
    #
    # The whole logo is used rather than a crop: the CARDVAULT wordmark is still
    # readable at 32 and 48px, which is what most displays render now. At 16px it
    # is admittedly indistinct — an ICO holds one image scaled to each size, and
    # Pillow silently drops extras passed via append_images, so there's no having
    # both. If tab-strip recognition ever matters more than the wordmark, swap
    # this for focal(art) to use the pokéball dial instead.
    compact(resized(art, 256)).save(
        OUT / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)]
    )

    # The logo used inside the app. 256 covers a 40px header mark even at 3x DPI,
    # and this one is on every page view so it gets the most attention to size.
    APP_LOGO.parent.mkdir(parents=True, exist_ok=True)
    compact(resized(art, 256)).save(APP_LOGO, optimize=True)

    # The Windows shortcut icon. Carries a 256px entry as well as the small sizes,
    # because Explorer's large-icon views and the taskbar at high DPI ask for it —
    # a favicon-sized .ico looks visibly soft there.
    SHORTCUT_ICON.parent.mkdir(parents=True, exist_ok=True)
    compact(resized(art, 256)).save(
        SHORTCUT_ICON, sizes=[(16, 16), (32, 32), (48, 48), (256, 256)]
    )

    for name in ("icon-512.png", "icon-192.png", "apple-touch-icon.png", "favicon.ico"):
        print(f"  wrote {name}  ({(OUT / name).stat().st_size:,} bytes)")

    for path in (APP_LOGO, SHORTCUT_ICON):
        print(f"  wrote {path.relative_to(ROOT)}  ({path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
