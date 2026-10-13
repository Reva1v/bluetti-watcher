"""Генератор assets/icon.ico (16/24/32/48/64/256): оранжевая батарея с молнией на тёмном скруглённом квадрате.

Запуск из корня репозитория: python -I assets/make_icon.py  (нужен Pillow). Результат коммитится,
при сборке Python не нужен.
"""

import os
import sys

from PIL import Image, ImageDraw

ORANGE = (0xFF, 0x6F, 0x1A, 255)
DARK = (0x11, 0x11, 0x11, 255)
SIZES = [16, 24, 32, 48, 64, 256]
SS = 8  # суперсэмплинг


def draw(size: int) -> Image.Image:
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = n / 32.0  # сетка 32×32

    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=6 * u, fill=DARK)

    # Корпус батареи (вертикальный) и клемма сверху; на мелких размерах линия не тоньше 1 px.
    stroke = max(2.2 * u, SS * 1.0)
    body = [9 * u, 7.5 * u, 23 * u, 27.5 * u]
    d.rounded_rectangle(body, radius=2.2 * u, outline=ORANGE, width=round(stroke))
    d.rounded_rectangle([13 * u, 4 * u, 19 * u, 7.5 * u + stroke / 2], radius=1 * u, fill=ORANGE)

    # Молния.
    bolt = [
        (17.6, 10.5),
        (12.2, 18.6),
        (15.6, 18.6),
        (14.4, 24.8),
        (19.8, 16.4),
        (16.4, 16.4),
    ]
    d.polygon([(x * u, y * u) for x, y in bolt], fill=ORANGE)

    return img.resize((size, size), Image.LANCZOS)


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "icon.ico")
    images = [draw(s) for s in SIZES]
    images[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    if len(sys.argv) > 1:  # каталог для PNG-превью
        for img, s in zip(images, SIZES):
            img.resize((256, 256), Image.NEAREST).save(os.path.join(sys.argv[1], f"icon{s}.png"))
    print(f"{out}: {os.path.getsize(out)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
