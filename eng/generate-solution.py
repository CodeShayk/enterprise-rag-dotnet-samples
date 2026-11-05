"""Regenerates Contoso-Knowledge.slnx from every project under Shared/ and the ChapterNN-*/ folders.

Run it after adding a project, from anywhere:  python sample-code/eng/generate-solution.py
Solution folders mirror the directory layout: Shared, then one folder per chapter.
Adapted from the companion code of the author's previous book, Agentic UI in .NET.
"""
from pathlib import Path

root = Path(__file__).resolve().parent.parent
solution = root / "Contoso-Knowledge.slnx"


def projects_under(folder: Path):
    return sorted(
        p for p in folder.rglob("*.csproj")
        if not any(part in ("bin", "obj") for part in p.relative_to(root).parts)
    )


groups: dict[str, list[Path]] = {}
for top in [root / "Shared", *sorted(root.glob("Chapter*"))]:
    if not top.is_dir():
        continue
    for project in projects_under(top):
        rel = project.relative_to(root)
        folder = "/" + "/".join(rel.parts[:-2]) + "/"   # every directory above the project's own
        groups.setdefault(folder, []).append(rel)

lines = [
    "<Solution>",
    '  <Folder Name="/Solution Items/">',
    '    <File Path="Directory.Build.props" />',
    '    <File Path="Directory.Packages.props" />',
    '    <File Path="global.json" />',
    '    <File Path="README.md" />',
    "  </Folder>",
]

# Declare parent folders explicitly so nested topic folders render under their chapter.
declared = set()
for folder in groups:
    parts = folder.strip("/").split("/")
    for i in range(1, len(parts)):
        parent = "/" + "/".join(parts[:i]) + "/"
        if parent not in groups:
            declared.add(parent)

for folder in sorted(set(groups) | declared, key=lambda f: f.lower()):
    if folder in groups:
        lines.append(f'  <Folder Name="{folder}">')
        for project in groups[folder]:
            lines.append(f'    <Project Path="{project.as_posix()}" />')
        lines.append("  </Folder>")
    else:
        lines.append(f'  <Folder Name="{folder}" />')

lines.append("</Solution>")
solution.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"Wrote {solution.name} with {sum(len(v) for v in groups.values())} projects in {len(groups)} folders.")
