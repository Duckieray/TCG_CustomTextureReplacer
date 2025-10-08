from __future__ import annotations

import argparse
import platform
import random
import re
import shutil
import sys
from pathlib import Path

VALID_EXTENSIONS = (".png", ".jpg", ".jpeg")
DEFAULT_TARGET_SIZE = (819, 1024)


def load_card_names_from_dump(dump_path: Path, target_size: tuple[int, int]) -> list[str]:
    """Parse TexturesList.txt and return names that match the target size."""
    card_names: list[str] = []
    size_str = f"{target_size[0]}x{target_size[1]}"

    with dump_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            match = re.match(r"(.+)\s+\((\d+)x(\d+)\)", line.strip())
            if not match:
                continue

            name, width, height = match.groups()
            if f"{width}x{height}" == size_str:
                card_names.append(name)

    return card_names


def random_assign_images(source_folder: Path, output_folder: Path, card_names: list[str]) -> None:
    output_folder.mkdir(parents=True, exist_ok=True)

    all_images = [
        entry for entry in source_folder.iterdir()
        if entry.is_file() and entry.suffix.lower() in VALID_EXTENSIONS
    ]

    if not all_images:
        print("No images found in the source folder.")
        return

    random.shuffle(all_images)

    for index, name in enumerate(card_names):
        source_image = all_images[index % len(all_images)]
        destination = output_folder / f"{name}.png"
        shutil.copy2(source_image, destination)
        print(f"Copied {source_image} -> {destination}")


def detect_default_paths(script_dir: Path) -> tuple[Path, Path]:
    """Return fallback dump and output paths relative to the script directory."""
    candidate_dumps = [
        script_dir / "TexturesList.txt",
        script_dir.parent / "TexturesList.txt",
        script_dir.parent / "BepInEx" / "plugins" / "TexturesList.txt",
    ]

    dump_path = next((path for path in candidate_dumps if path.exists()), candidate_dumps[-1])

    candidate_outputs = [
        script_dir / "CustomTextures",
        script_dir.parent / "CustomTextures",
        script_dir.parent / "BepInEx" / "plugins" / "CustomTextures",
        script_dir,
    ]

    output_path = next((path for path in candidate_outputs if path.exists()), candidate_outputs[0])

    return dump_path, output_path


def parse_size(value: str) -> tuple[int, int]:
    try:
        width_str, height_str = value.lower().split("x", 1)
        width = int(width_str.strip())
        height = int(height_str.strip())
    except (ValueError, AttributeError):
        raise argparse.ArgumentTypeError("Size must be in the form WIDTHxHEIGHT, for example 819x1024.")

    if width <= 0 or height <= 0:
        raise argparse.ArgumentTypeError("Size values must be positive integers.")

    return width, height


def build_argument_parser(script_dir: Path) -> argparse.ArgumentParser:
    default_dump, default_output = detect_default_paths(script_dir)

    parser = argparse.ArgumentParser(
        description="Populate the CustomTextures folder with random images based on TexturesList.txt.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument(
        "source",
        nargs="?",
        help="Folder containing the replacement images (.png/.jpg/.jpeg).",
    )
    parser.add_argument(
        "--dump",
        type=Path,
        default=default_dump,
        help="Path to the TexturesList.txt file.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=default_output,
        help="Folder where renamed textures will be written.",
    )
    parser.add_argument(
        "--size",
        type=parse_size,
        default=DEFAULT_TARGET_SIZE,
        help="Target texture size to match (WIDTHxHEIGHT).",
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Optional random seed for reproducible assignments.",
    )

    return parser


def main(argv: list[str]) -> int:
    script_dir = Path(__file__).resolve().parent
    parser = build_argument_parser(script_dir)

    if not argv:
        parser.print_help()
        return 0

    args = parser.parse_args(argv)

    system_name = platform.system() or "Unknown"
    print(f"Detected platform: {system_name}")

    source_folder = Path(args.source).expanduser().resolve()
    dump_path = args.dump.expanduser().resolve()
    output_folder = args.output.expanduser().resolve()

    if not source_folder.exists() or not source_folder.is_dir():
        print(f"Source folder does not exist or is not a directory: {source_folder}")
        return 1

    if not dump_path.exists():
        print(f"Textures list not found at: {dump_path}")
        return 1

    target_size = args.size

    if args.seed is not None:
        random.seed(args.seed)

    print(f"Reading texture names from: {dump_path}")
    print(f"Writing renamed textures to: {output_folder}")

    card_names = load_card_names_from_dump(dump_path, target_size)
    print(f"Found {len(card_names)} textures matching {target_size[0]}x{target_size[1]}")

    if not card_names:
        print("No matching textures were found. Check the size filter or dump file.")
        return 1

    random_assign_images(source_folder, output_folder, card_names)
    print("Replacement complete.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
