"""Package approved imagegen output without changing the illustration design.

Only high-quality downsampling and PNG/ICO format encoding are performed here.
Transparency is supplied by imagegen, not inferred or masked in this script.
"""

from pathlib import Path
import json

from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
GENERATED = Path(r"C:\Users\tang\.codex\generated_images\01a149e0-ae32-72e2-805d-306f1d90bffc")
JOURNAL = ROOT / "Assets" / "Journal"
JOURNAL.mkdir(parents=True, exist_ok=True)

sources = {
    "harbor-header.png": ("exec-209b47cf-d919-4be5-97ef-2c8b5e2fec84.png", (1536, 640)),
    "paper-texture.png": ("exec-46209fb1-2851-4f6d-8259-3ac1e73a5e20.png", (1024, 1024)),
    "notes-decoration.png": ("exec-3ec782f5-064f-4413-a5a7-75ce9ebb4725.png", (768, 768)),
    "sidebar-decoration.png": ("exec-3ec782f5-064f-4413-a5a7-75ce9ebb4725.png", (768, 768)),
}

for name, (source, size) in sources.items():
    with Image.open(GENERATED / source) as image:
        image.convert("RGB").resize(size, Image.Resampling.LANCZOS).save(JOURNAL / name, optimize=True)

with Image.open(GENERATED / "exec-ac6b3fef-1232-4410-bbfe-bfa9518c0979.png") as image:
    icon = image.convert("RGBA").resize((1024, 1024), Image.Resampling.LANCZOS)
    icon.save(ROOT / "Assets" / "NativeTavern.png", optimize=True)
    icon.save(ROOT / "Assets" / "NativeTavern.ico", format="ICO", sizes=[
        (16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)
    ])

report = {}
for path in [*sorted(JOURNAL.glob("*.png")), ROOT / "Assets" / "NativeTavern.png"]:
    with Image.open(path) as image:
        entry = {"size": list(image.size), "mode": image.mode, "bytes": path.stat().st_size}
        if "A" in image.getbands():
            alpha = image.getchannel("A")
            histogram = alpha.histogram()
            entry["transparent_pixels"] = histogram[0]
            entry["opaque_pixels"] = histogram[255]
            entry["alpha_extrema"] = list(alpha.getextrema())
            entry["corner_alpha"] = [alpha.getpixel(p) for p in [
                (0, 0), (image.width - 1, 0), (0, image.height - 1), (image.width - 1, image.height - 1)
            ]]
        report[str(path.relative_to(ROOT))] = entry

with Image.open(ROOT / "Assets" / "NativeTavern.ico") as image:
    report["Assets\\NativeTavern.ico"] = {"sizes": sorted(map(list, image.ico.sizes()))}

(ROOT / "artifacts" / "JournalTheme" / "asset-validation.json").write_text(
    json.dumps(report, indent=2), encoding="utf-8"
)
print(json.dumps(report, indent=2))
