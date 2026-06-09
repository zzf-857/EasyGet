from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
PNG_PATH = ASSETS / "app.png"
ICO_PATH = ASSETS / "app.ico"
SVG_PATH = ASSETS / "app-icon-source.svg"

SIZE = 256
SCALE = 4
CANVAS = SIZE * SCALE


def hex_to_rgba(value: str, alpha: int = 255) -> tuple[int, int, int, int]:
    value = value.lstrip("#")
    return (
        int(value[0:2], 16),
        int(value[2:4], 16),
        int(value[4:6], 16),
        alpha,
    )


def blend(
    a: tuple[int, int, int, int],
    b: tuple[int, int, int, int],
    t: float,
) -> tuple[int, int, int, int]:
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(4))


def scaled_box(box: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    return tuple(v * SCALE for v in box)


def rounded_rect_mask(size: tuple[int, int], radius: int) -> Image.Image:
    mask = Image.new("L", size, 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=255)
    return mask


def linear_gradient(
    size: tuple[int, int],
    start: str,
    end: str,
    alpha: int = 255,
    diagonal: bool = True,
) -> Image.Image:
    image = Image.new("RGBA", size)
    pixels = image.load()
    a = hex_to_rgba(start, alpha)
    b = hex_to_rgba(end, alpha)
    denominator = (size[0] + size[1] - 2) if diagonal else max(size[1] - 1, 1)

    for y in range(size[1]):
        for x in range(size[0]):
            t = (x + y) / denominator if diagonal else y / denominator
            pixels[x, y] = blend(a, b, t)

    return image


def paste_shadow(
    base: Image.Image,
    mask: Image.Image,
    offset: tuple[int, int],
    blur: int,
    color: tuple[int, int, int, int],
) -> None:
    shadow = Image.new("RGBA", base.size, (0, 0, 0, 0))
    shadow_layer = Image.new("RGBA", mask.size, color)
    shadow.paste(shadow_layer, (offset[0], offset[1]), mask)
    shadow = shadow.filter(ImageFilter.GaussianBlur(blur))
    base.alpha_composite(shadow)


def paste_rounded_gradient(
    base: Image.Image,
    box: tuple[int, int, int, int],
    radius: int,
    start: str,
    end: str,
    outline: str | None = None,
    outline_width: int = 1,
    alpha: int = 255,
) -> None:
    box = scaled_box(box)
    width = box[2] - box[0]
    height = box[3] - box[1]
    mask = rounded_rect_mask((width, height), radius * SCALE)
    gradient = linear_gradient((width, height), start, end, alpha)
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    layer.paste(gradient, box[:2], mask)
    base.alpha_composite(layer)

    if outline:
        draw = ImageDraw.Draw(base)
        draw.rounded_rectangle(
            box,
            radius=radius * SCALE,
            outline=hex_to_rgba(outline, 170),
            width=outline_width * SCALE,
        )


def draw_icon() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    app_box = scaled_box((14, 14, 242, 242))
    app_size = (app_box[2] - app_box[0], app_box[3] - app_box[1])
    app_mask = rounded_rect_mask(app_size, 54 * SCALE)
    paste_shadow(
        image,
        app_mask,
        (app_box[0], app_box[1] + 12 * SCALE),
        18 * SCALE,
        (0, 0, 0, 78),
    )

    app_gradient = linear_gradient(app_size, "#1d2235", "#0d111d")
    app_layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    app_layer.paste(app_gradient, app_box[:2], app_mask)
    image.alpha_composite(app_layer)

    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        app_box,
        radius=54 * SCALE,
        outline=hex_to_rgba("#35405c", 190),
        width=2 * SCALE,
    )

    plate_box = (45, 62, 211, 165)
    plate_scaled = scaled_box(plate_box)
    plate_mask = rounded_rect_mask(
        (plate_scaled[2] - plate_scaled[0], plate_scaled[3] - plate_scaled[1]),
        28 * SCALE,
    )
    paste_shadow(
        image,
        plate_mask,
        (plate_scaled[0], plate_scaled[1] + 8 * SCALE),
        12 * SCALE,
        (0, 0, 0, 86),
    )
    paste_rounded_gradient(
        image,
        plate_box,
        28,
        "#222b49",
        "#111827",
        "#475477",
        outline_width=1,
    )

    draw = ImageDraw.Draw(image, "RGBA")
    draw.polygon(
        [(85 * SCALE, 95 * SCALE), (85 * SCALE, 133 * SCALE), (119 * SCALE, 114 * SCALE)],
        fill=hex_to_rgba("#7f96c6", 74),
    )
    draw.rounded_rectangle(
        scaled_box((67, 82, 189, 91)),
        radius=5 * SCALE,
        fill=hex_to_rgba("#ffffff", 18),
    )

    arrow_mask = Image.new("L", image.size, 0)
    arrow_draw = ImageDraw.Draw(arrow_mask)
    arrow_points = [
        (113 * SCALE, 76 * SCALE),
        (143 * SCALE, 76 * SCALE),
        (143 * SCALE, 122 * SCALE),
        (166 * SCALE, 122 * SCALE),
        (128 * SCALE, 166 * SCALE),
        (90 * SCALE, 122 * SCALE),
        (113 * SCALE, 122 * SCALE),
    ]
    arrow_draw.polygon(arrow_points, fill=255)
    paste_shadow(image, arrow_mask, (0, 6 * SCALE), 8 * SCALE, (0, 0, 0, 95))

    arrow_gradient = linear_gradient((CANVAS, CANVAS), "#9cc1ff", "#4f8cff", diagonal=False)
    arrow_layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    arrow_layer.paste(arrow_gradient, (0, 0), arrow_mask)
    image.alpha_composite(arrow_layer)

    draw = ImageDraw.Draw(image, "RGBA")
    draw.line(
        [(121 * SCALE, 84 * SCALE), (121 * SCALE, 126 * SCALE)],
        fill=hex_to_rgba("#d7e5ff", 76),
        width=3 * SCALE,
    )

    tray_box = scaled_box((74, 178, 182, 194))
    tray_mask = rounded_rect_mask(
        (tray_box[2] - tray_box[0], tray_box[3] - tray_box[1]),
        8 * SCALE,
    )
    paste_shadow(image, tray_mask, (tray_box[0], tray_box[1] + 5 * SCALE), 8 * SCALE, (0, 0, 0, 74))
    tray_gradient = linear_gradient(
        (tray_box[2] - tray_box[0], tray_box[3] - tray_box[1]),
        "#3be3d2",
        "#8be8a8",
        diagonal=True,
    )
    tray_layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    tray_layer.paste(tray_gradient, tray_box[:2], tray_mask)
    image.alpha_composite(tray_layer)

    draw = ImageDraw.Draw(image, "RGBA")
    draw.rounded_rectangle(
        scaled_box((170, 68, 188, 86)),
        radius=9 * SCALE,
        fill=hex_to_rgba("#3be3d2", 210),
    )
    draw.rounded_rectangle(
        scaled_box((183, 90, 197, 104)),
        radius=7 * SCALE,
        fill=hex_to_rgba("#ffb86b", 210),
    )

    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def write_svg() -> None:
    SVG_PATH.write_text(
        """<svg width="256" height="256" viewBox="0 0 256 256" fill="none" xmlns="http://www.w3.org/2000/svg">
  <!-- EasyGet App Icon. Custom mark informed by online download icon research from Microsoft Fluent UI System Icons and Tabler Icons; not a direct copy. -->
  <defs>
    <linearGradient id="bg" x1="22" y1="18" x2="232" y2="242" gradientUnits="userSpaceOnUse">
      <stop stop-color="#1D2235"/>
      <stop offset="1" stop-color="#0D111D"/>
    </linearGradient>
    <linearGradient id="plate" x1="45" y1="62" x2="211" y2="165" gradientUnits="userSpaceOnUse">
      <stop stop-color="#222B49"/>
      <stop offset="1" stop-color="#111827"/>
    </linearGradient>
    <linearGradient id="download-arrow" x1="128" y1="76" x2="128" y2="166" gradientUnits="userSpaceOnUse">
      <stop stop-color="#9CC1FF"/>
      <stop offset="1" stop-color="#4F8CFF"/>
    </linearGradient>
    <linearGradient id="tray" x1="74" y1="178" x2="182" y2="194" gradientUnits="userSpaceOnUse">
      <stop stop-color="#3BE3D2"/>
      <stop offset="1" stop-color="#8BE8A8"/>
    </linearGradient>
    <filter id="soft-shadow" x="-20%" y="-20%" width="140%" height="150%" color-interpolation-filters="sRGB">
      <feDropShadow dx="0" dy="10" stdDeviation="10" flood-color="#000000" flood-opacity="0.35"/>
    </filter>
  </defs>
  <rect x="14" y="14" width="228" height="228" rx="54" fill="url(#bg)" stroke="#35405C" stroke-width="2"/>
  <g filter="url(#soft-shadow)">
    <rect x="45" y="62" width="166" height="103" rx="28" fill="url(#plate)" stroke="#475477"/>
    <path d="M85 95V133L119 114L85 95Z" fill="#7F96C6" opacity="0.29"/>
    <rect x="67" y="82" width="122" height="9" rx="5" fill="white" opacity="0.07"/>
  </g>
  <path d="M113 76H143V122H166L128 166L90 122H113V76Z" fill="url(#download-arrow)"/>
  <path d="M121 84V126" stroke="#D7E5FF" stroke-width="3" stroke-linecap="round" opacity="0.3"/>
  <rect x="74" y="178" width="108" height="16" rx="8" fill="url(#tray)"/>
  <rect x="170" y="68" width="18" height="18" rx="9" fill="#3BE3D2" opacity="0.82"/>
  <rect x="183" y="90" width="14" height="14" rx="7" fill="#FFB86B" opacity="0.82"/>
</svg>
""",
        encoding="utf-8",
    )


def main() -> None:
    ASSETS.mkdir(exist_ok=True)
    write_svg()

    png = draw_icon()
    png.save(PNG_PATH, optimize=False)

    icon_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    png.save(ICO_PATH, sizes=icon_sizes)


if __name__ == "__main__":
    main()
