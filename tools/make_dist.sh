#!/bin/zsh
# Архив для установки: dist/syabh-coop-<версия>.zip
#   tools/make_dist.sh   — сам собирает мод (build_mod.sh), чтобы архив никогда
#   не получил старую сборку с новой версией (ревью этапа 0).
# Кода игры в архиве нет (его можно выкладывать): только код мода
# (Assembly-CSharp.SyabhCoop.mm.dll), MonoMod + Mono.Cecil (MIT) и normalize.exe.
# install.command собирает из них мод у игрока, из его оригинала, под .NET
# CrossOver, и сверяет результат с mod.sha256 (сборка байт-в-байт та же).
set -eu
root=${0:A:h:h}
"$root/tools/build_mod.sh" >/dev/null
ver=$(sed -n 's/.*const string Version = "\([^"]*\)".*/\1/p' "$root/src/SyabhCoop/CoopMod.cs")
[[ $ver == [0-9]*.[0-9]*.[0-9]* ]] || { echo "не нашла версию в CoopMod.cs: '$ver'" >&2; exit 1; }
# строковые литералы .NET лежат в UTF-16 — ищем «+coop-» в этой кодировке
python3 -I -c 'import sys; sys.exit(0 if "+coop-".encode("utf-16-le") in open(sys.argv[1],"rb").read() else 1)' \
  "$root/build/out/Assembly-CSharp.dll" || { echo "в собранной DLL нет суффикса версии" >&2; exit 1; }
name="syabh-coop-$ver"; d="$root/dist/$name"
rm -rf "$d" "$root/dist/$name.zip"; mkdir -p "$d"
cp "$root/build/work/Assembly-CSharp.SyabhCoop.mm.dll" "$d/"
mkdir "$d/patcher"
cp "$root/tools/monomod/bin/"{MonoMod.exe,MonoMod.Utils.dll,Mono.Cecil.dll,Mono.Cecil.Mdb.dll,Mono.Cecil.Pdb.dll,Mono.Cecil.Rocks.dll} "$d/patcher/"
cp "$root/build/tools/normalize.exe" "$d/patcher/"
cmp -s "$root/tools/monomod/bin/Mono.Cecil.dll" "$root/build/tools/Mono.Cecil.dll" || { echo "normalize.exe собран с другим Mono.Cecil" >&2; exit 1; }
print "$(shasum -a 256 "$root/build/out/Assembly-CSharp.dll" | cut -d' ' -f1)  Assembly-CSharp.dll (mod $ver)" > "$d/mod.sha256"
cp "$root/tools/dist/install.command" "$root/tools/dist/restore.command" "$root/tools/dist/server.command" "$root/tools/dist/README.txt" "$root/tools/dist/THIRD-PARTY-NOTICES.txt" "$d/"
cp "$root/tools/base.sha256" "$d/"
chmod +x "$d"/*.command
print -n "$ver" > "$d/VERSION.txt"
(cd "$root/dist" && zip -qr "$name.zip" "$name")
echo "готово: dist/$name.zip ($(wc -c <"$root/dist/$name.zip" | tr -d ' ') байт)"
