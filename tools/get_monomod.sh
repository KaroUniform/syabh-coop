#!/bin/zsh
# MonoMod и Mono.Cecil для сборки мода (tools/build_mod.sh) — из NuGet, точные версии.
#   tools/get_monomod.sh   → tools/monomod/bin (MonoMod.exe и библиотеки, net452/net40)
#                            и tools/monomod/dl/<пакет>/ (распакованные пакеты)
# Уже скачанные пакеты (tools/monomod/dl/*.nupkg) не качаются заново.
set -eu
root=${0:A:h:h}; m="$root/tools/monomod"
mkdir -p "$m/dl" "$m/bin"
get() {  # имя-папки пакет версия
  local f="$m/dl/$1.nupkg"
  [[ -f $f ]] || curl -fsSL -o "$f" "https://www.nuget.org/api/v2/package/$2/$3"
  rm -rf "$m/dl/$1"; mkdir -p "$m/dl/$1"; unzip -q -o "$f" -d "$m/dl/$1"
}
get monomod MonoMod 22.7.31.1
get monomod.utils MonoMod.Utils 22.7.31.1
get mono.cecil Mono.Cecil 0.11.4
cp "$m/dl/monomod/lib/net452/MonoMod.exe" "$m/dl/monomod.utils/lib/net452/MonoMod.Utils.dll" "$m/bin/"
cp "$m/dl/mono.cecil/lib/net40/"Mono.Cecil{,.Mdb,.Pdb,.Rocks}.dll "$m/bin/"
echo "готово: $m/bin"
