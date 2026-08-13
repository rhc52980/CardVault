"""
Regenerates the raster app icons from the same design as client/public/favicon.svg.

The SVG is the source of truth and covers modern browsers on its own, but three
places need bitmaps: favicon.ico for the browsers and OS shortcuts that still ask
for it, apple-touch-icon.png for iOS "Add to Home Screen" (without it iOS uses a
screenshot of the page), and the manifest icons for Android.

Run from the repo root:  python tools/generate-icons.py
Requires Pillow.
"""

from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "client" / "public"

# Same palette as the app: near-black ground, arc blue into gold across the card.
GROUND = (6, 7, 12, 255)
ARC = (109, 139, 255)
GOLD = (242, 182, 50)
BACK_CARD = (58, 69, 104)  # the card behind, dimmed so the front one leads

# Drawn oversized and downsampled, which is the cheapest way to get clean edges
# on the rounded corners and the circle.
SUPERSAMPLE = 8
BASE = 64  # the SVG's coordinate space, so the geometry below matches it 1:1


def draw_icon(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    k = s / BASE  # scale from SVG units to pixels

    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Rounded-square ground
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=14 * k, fill=GROUND)

    # Two overlapping card silhouettes — a stack reads as "collection" where a
    # single card reads as a device. Deliberately no inner detail: an art window
    # and energy dot turn to mush at 16px, and the shapes alone carry it.
    back = [26 * k, 11 * k, 52 * k, 45 * k]
    front = [13 * k, 19 * k, 39 * k, 53 * k]

    d.rounded_rectangle(back, radius=4 * k, fill=BACK_CARD)

    # A ground-coloured pad behind the front card separates the two by a gap in
    # the background rather than by drawing an outline on the card.
    gap = 1.6 * k
    d.rounded_rectangle(
        [front[0] - gap, front[1] - gap, front[2] + gap, front[3] + gap],
        radius=4 * k + gap,
        fill=GROUND,
    )

    gradient = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(gradient)
    for y in range(s):
        t = y / max(s - 1, 1)
        gd.line(
            [(0, y), (s, y)],
            fill=(
                round(ARC[0] + (GOLD[0] - ARC[0]) * t),
                round(ARC[1] + (GOLD[1] - ARC[1]) * t),
                round(ARC[2] + (GOLD[2] - ARC[2]) * t),
                255,
            ),
        )

    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle(front, radius=4 * k, fill=255)
    img.paste(gradient, (0, 0), mask)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    # One high-res render downsampled for each target keeps them consistent.
    master = draw_icon(512)

    master.save(OUT / "icon-512.png")
    draw_icon(192).save(OUT / "icon-192.png")

    # iOS ignores transparency and composites on white, so flatten onto the
    # ground colour rather than letting it pick.
    apple = Image.new("RGB", (180, 180), GROUND[:3])
    touch = draw_icon(180)
    apple.paste(touch, (0, 0), touch)
    apple.save(OUT / "apple-touch-icon.png")

    # Multi-resolution .ico for browsers and OS shortcuts that still request it.
    draw_icon(64).save(OUT / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)])

    for name in ("icon-512.png", "icon-192.png", "apple-touch-icon.png", "favicon.ico"):
        print(f"  wrote {name}  ({(OUT / name).stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
