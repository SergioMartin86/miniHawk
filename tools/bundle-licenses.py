#!/usr/bin/env python3
"""Writes the licence of a Chimera bundle, from what its parts declare.

A bundle is the frontend, and the frontend alone: Chimera ships no cores, and
each is installed from its own project through File > Core Manager
(docs/core-manager.md). So this states the frontend's terms and says plainly
where the rest come from.

It still reads Cores/ - a bundle assembled with packages in it, which a
developer or a downstream packager may do, must state their terms too. A core
package is somebody else's emulator: several of them (Genesis Plus GX, Snes9x,
the FreeDO-descended parts of Opera) forbid commercial redistribution, and
others (PPSSPP, DOSBox-X) are GPL and require the corresponding source to be
identifiable. Those terms bind the WHOLE distribution, not just the zip they
came in.

Nothing here is written by hand. Each package carries licenses/licenses.json
(put there by miniBox's package-licenses.py), and this reads them, copies every
licence text into licenses/<package>/, and writes LICENSES.md with:

  * what the bundle as a whole may be used for - PROHIBITED beats allowed, so
    one non-commercial core makes the bundle non-commercial;
  * every component, its licence, and the exact commit it was built from;
  * for GPL parts, a link to the corresponding source of that exact commit.

Usage: bundle-licenses.py <bundle dir> [--chimera-root <dir>]
"""

import json
import os
import sys
import zipfile


def read_package_licences(zip_path):
    """The licences a core package declares, and its licence texts."""
    try:
        with zipfile.ZipFile(zip_path) as z:
            names = z.namelist()
            if "licenses/licenses.json" not in names:
                return None, {}
            index = json.loads(z.read("licenses/licenses.json"))
            texts = {
                os.path.basename(n): z.read(n)
                for n in names
                if n.startswith("licenses/") and not n.endswith("licenses.json")
            }
            return index, texts
    except (OSError, zipfile.BadZipFile, ValueError):
        return None, {}


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    bundle = sys.argv[1]
    chimera_root = None
    if "--chimera-root" in sys.argv:
        chimera_root = sys.argv[sys.argv.index("--chimera-root") + 1]

    cores_dir = os.path.join(bundle, "Cores")
    out_dir = os.path.join(bundle, "licenses")
    os.makedirs(out_dir, exist_ok=True)

    packages = []
    undeclared = []
    for name in sorted(os.listdir(cores_dir)) if os.path.isdir(cores_dir) else []:
        if not name.endswith(".chimeraCore"):
            continue
        index, texts = read_package_licences(os.path.join(cores_dir, name))
        pkg = os.path.splitext(name)[0]
        if index is None:
            undeclared.append(name)
            continue
        dest = os.path.join(out_dir, pkg)
        os.makedirs(dest, exist_ok=True)
        for filename, data in texts.items():
            with open(os.path.join(dest, filename), "wb") as f:
                f.write(data)
        packages.append((pkg, index))

    if undeclared:
        # a binary with no stated terms must not be distributed: better to fail
        # the build than to ship it and find out later
        sys.exit("these core packages declare no licences: " + ", ".join(undeclared))

    # the frontend's own terms, copied in beside the cores
    if chimera_root:
        for filename in ("LICENSE", "CREDITS.md"):
            src = os.path.join(chimera_root, filename)
            if os.path.exists(src):
                with open(src, "rb") as f:
                    data = f.read()
                with open(os.path.join(out_dir, f"chimera-{filename}"), "wb") as f:
                    f.write(data)

    non_commercial = [pkg for pkg, index in packages if index.get("commercialUse") == "prohibited"]

    lines = []
    lines.append("# Licences of this Chimera bundle")
    lines.append("")
    lines.append("This file is generated from what each part of the bundle declares about")
    lines.append("itself; the licence texts themselves are under `licenses/`.")
    lines.append("")
    lines.append("## What this bundle may be used for")
    lines.append("")
    if non_commercial:
        lines.append("**This bundle may NOT be redistributed or used commercially.** It contains")
        lines.append("emulator cores whose licences forbid commercial use, and that restriction")
        lines.append("binds the whole distribution, not only those cores:")
        lines.append("")
        for pkg in non_commercial:
            index = dict(packages)[pkg]
            lines.append(f"- **{pkg}** - {index.get('effectiveTerms', '')}")
        lines.append("")
        lines.append("Removing those packages from `Cores/` removes the restriction they impose;")
        lines.append("what is left is stated per package below.")
    elif packages:
        lines.append("Every part of this bundle permits redistribution; see each component's")
        lines.append("licence below for the conditions.")
    else:
        lines.append("This bundle carries **no emulator cores**, so what is here is the")
        lines.append("frontend and its own dependencies, stated below.")
        lines.append("")
        lines.append("**Installing a core changes this.** Cores are separate projects,")
        lines.append("downloaded through File > Core Manager, and each brings its own terms")
        lines.append("with it - several (Genesis Plus GX, Opera, Snes9x) forbid commercial")
        lines.append("use, and that binds whatever they are installed into. Every package")
        lines.append("carries its own `licenses/` and says so before you install it.")
    lines.append("")
    lines.append("## The frontend")
    lines.append("")
    lines.append("Chimera itself is a derivative fork of BizHawk; its terms are in")
    lines.append("`licenses/chimera-LICENSE` and the people behind it in")
    lines.append("`licenses/chimera-CREDITS.md`. The native libraries it links (SDL2, OpenAL")
    lines.append("Soft, Lua, zstd, SQLite, cimgui, luasocket and others) remain their authors'")
    lines.append("under their own licences.")
    lines.append("")
    # A shipped GPL program has to be named and pointed at its source, and it is
    # not one of the cores, so it gets said here rather than nowhere.
    ffmpeg_licence = os.path.join(bundle, "dll", "ffmpeg-LICENSE.txt")
    if os.path.exists(ffmpeg_licence):
        with open(ffmpeg_licence) as f:
            version = f.readline().strip()
        lines.append("## ffmpeg")
        lines.append("")
        lines.append(f"Encoding video runs `{version}`, shipped in `dll/` beside Chimera rather")
        lines.append("than downloaded the first time somebody wants a video. It is a separate")
        lines.append("GPL program: Chimera starts it and writes frames down a pipe, and does not")
        lines.append("link any part of it. The release it came from, the exact source commit it")
        lines.append("was built from and its own licence are in `dll/ffmpeg-LICENSE.txt`.")
        lines.append("")

    lines.append("## The cores")
    lines.append("")
    if not packages:
        lines.append("None in this bundle. A core installed later states its own terms, which")
        lines.append("Chimera shows before installing it and keeps in the package's own")
        lines.append("`licenses/` afterwards.")
        lines.append("")
    for pkg, index in packages:
        lines.append(f"### {pkg}")
        lines.append("")
        lines.append(f"- **Terms of the built core:** {index.get('effectiveTerms', 'unstated')}")
        lines.append(f"- **Commercial use:** {index.get('commercialUse', 'unstated')}")
        if index.get("note"):
            lines.append(f"- {index['note']}")
        lines.append("")
        for component in index.get("components", []):
            name = component.get("name", "")
            licence = component.get("license", "")
            bits = [f"**{name}** - {licence}"]
            if component.get("url"):
                bits.append(f"<{component['url']}>")
            if component.get("commit"):
                bits.append(f"commit `{component['commit'][:12]}`")
            lines.append(f"- {', '.join(bits)}")
            if component.get("sourceUrl"):
                lines.append(f"  - corresponding source: {component['sourceUrl']}")
            if component.get("licenseFile"):
                lines.append(f"  - licence text: `licenses/{pkg}/{component['licenseFile']}`")
        lines.append("")

    lines.append("## Source for the GPL parts")
    lines.append("")
    lines.append("Every GPL-licensed component above links the corresponding source of the")
    lines.append("EXACT commit its binary was built from. Those archives are permanent: they")
    lines.append("are generated by the hosting service from an immutable commit, not from a")
    lines.append("branch that moves. Each package's `build.json` records the same commits")
    lines.append("along with the toolchain and flags that built it.")
    lines.append("")

    with open(os.path.join(bundle, "LICENSES.md"), "w") as f:
        f.write("\n".join(lines))

    print(f"LICENSES.md: {len(packages)} packages, "
          f"{'NON-COMMERCIAL (' + ', '.join(non_commercial) + ')' if non_commercial else 'commercial use permitted'}")


if __name__ == "__main__":
    main()
