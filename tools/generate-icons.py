"""
Generates the app icons and the in-app logo from brand/Pokemon_Card_Vault.png.

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
SOURCE = ROOT / "brand" / "Pokemon_Card_Vault.png"
OUT = ROOT / "client" / "public"

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


# Fractions of the artwork holding the vault opening and the cards inside it.
FOCAL_CROP = (0.28, 0.15, 0.72, 0.62)


def focal(img: Image.Image) -> Image.Image:
    """
    The whole logo turns to mush below about 32px — the wordmark and the fanned
    cards are far too fine. Cropping to the gold vault opening keeps a readable
    silhouette and strong colour at favicon sizes. Multi-resolution icons exist
    precisely so small entries can be a simplified image rather than a shrunken one.
    """
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

    # Browser tabs and OS shortcuts get the cropped vault, which stays readable
    # where the full logo would not.
    resized(focal(art), 256).save(OUT / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)])

    # The logo used inside the app. 256 covers a 40px header mark even at 3x DPI,
    # and this one is on every page view so it gets the most attention to size.
    compact(resized(art, 256)).save(OUT / "logo.png", optimize=True)

    for name in ("icon-512.png", "icon-192.png", "apple-touch-icon.png", "favicon.ico", "logo.png"):
        print(f"  wrote {name}  ({(OUT / name).stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
