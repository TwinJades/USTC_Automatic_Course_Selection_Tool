from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser(description="Build deterministic Route 2 PNG/ICO assets.")
    parser.add_argument("source", type=Path)
    parser.add_argument("png_output", type=Path)
    parser.add_argument("ico_output", type=Path)
    args = parser.parse_args()

    source = Image.open(args.source).convert("RGBA")
    alpha = source.getchannel("A")

    # The supplied artwork has a few alpha=1..3 pixels on the canvas edge.
    # Remove only those effectively invisible export artifacts, then crop to
    # the remaining alpha bounds. The visible artwork itself is not redrawn.
    cleaned_alpha = alpha.point(lambda value: 0 if value < 4 else value)
    source.putalpha(cleaned_alpha)
    bbox = cleaned_alpha.getbbox()
    if bbox is None:
        raise ValueError("Source icon is fully transparent.")

    cropped = source.crop(bbox)
    canvas_size = 1024
    padding = 64
    available = canvas_size - padding * 2
    scale = min(available / cropped.width, available / cropped.height)
    resized = cropped.resize(
        (round(cropped.width * scale), round(cropped.height * scale)),
        Image.Resampling.LANCZOS,
    )

    canvas = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    position = (
        (canvas_size - resized.width) // 2,
        (canvas_size - resized.height) // 2,
    )
    canvas.alpha_composite(resized, position)

    args.png_output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(args.png_output, format="PNG", optimize=True)
    canvas.save(
        args.ico_output,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    print(f"source_size={source.size[0]}x{source.size[1]}")
    print(f"alpha_crop={bbox}")
    print(f"output_size={canvas_size}x{canvas_size}")
    print(f"content_size={resized.width}x{resized.height}")


if __name__ == "__main__":
    main()
