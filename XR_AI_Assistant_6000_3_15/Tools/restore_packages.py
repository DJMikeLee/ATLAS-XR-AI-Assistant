"""Restore the project's third-party packages and Unity importer settings."""
import argparse
from pathlib import Path
import shutil
import subprocess

PROJECT = Path(__file__).resolve().parent.parent
IMPORTERS = PROJECT / "Tools" / "NuGetImportSettings"
PACKAGES = PROJECT / "Assets" / "Packages"

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", default="nugetforunity", help="Path to the NuGetForUnity CLI 4.5.0")
    args = parser.parse_args()
    subprocess.run([args.cli, "restore", str(PROJECT)], check=True)
    for template in sorted(IMPORTERS.rglob("*.meta.template")):
        relative = template.relative_to(IMPORTERS).with_suffix("")
        destination = PACKAGES / relative
        if not destination.with_suffix("").is_file():
            raise SystemExit(f"Expected restored library is missing: {relative.with_suffix('')}")
        shutil.copyfile(template, destination)
    print("Restored packages and applied the Unity importer settings.")

if __name__ == "__main__":
    main()
