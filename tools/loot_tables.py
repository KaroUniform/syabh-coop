#!/usr/bin/env python3
"""Таблицы лута C_LootTable из ресурсов игры (Unity 4 и 2018).

  tools/loot_tables.py <sir_Data>            — таблица: тиры min–max и предметы
  tools/loot_tables.py <sir_Data> --cs <файл> — C# с этими таблицами (CoopLootSP.cs)

Нужен UnityPy (pip install UnityPy, лучше в отдельном venv). Поля MonoBehaviour
читаются из сырых байт: в сборках игры нет typetree. Порядок полей —
decomp/*/Assembly-CSharp/C_LootTable.cs: m_InitialItems, затем 4 × (min, max,
массив предметов). Разбор каждой таблицы проверяется: должен дойти ровно до конца.
Исследование и сравнение версий — docs/research/loot-sp-vs-mp.md.
"""
import sys, struct, glob, os
import UnityPy


def load(d):
    keep = ('mainData', 'globalgamemanagers', 'data.unity3d')
    files = [f for f in sorted(glob.glob(os.path.join(d, '*')))
             if os.path.isfile(f) and (f.endswith('.assets') or os.path.basename(f).startswith('level') or os.path.basename(f) in keep)]
    return UnityPy.load(*files)


def resolve(af, fid, pid):
    if fid == 0:
        tgt = af
    else:
        ext = os.path.basename(af.externals[fid - 1].path).lower()
        tgt = next(f for n, f in af.environment.files.items() if n.lower().endswith(ext))
    o = tgt.objects[pid].read(check_read=False)
    try:
        return o.m_GameObject.read().m_Name
    except Exception:
        return o.m_Name


def tables(env):
    out = {}
    for obj in env.objects:
        if obj.type.name != 'MonoBehaviour':
            continue
        try:
            mb = obj.read(check_read=False)
            if mb.m_Script.read().m_ClassName != 'C_LootTable':
                continue
            name = mb.m_GameObject.read().m_Name
        except Exception:
            continue
        af = obj.assets_file
        big = af.header.version >= 14          # PPtr: fileID int32 + pathID int64 (иначе int32)
        ps = 12 if big else 8
        raw = obj.get_raw_data()
        p = ps + 4 + ps                         # m_GameObject, m_Enabled (+выравнивание), m_Script
        n = struct.unpack_from('<i', raw, p)[0]
        p += 4 + n
        p = (p + 3) & ~3                        # m_Name

        def arr():
            nonlocal p
            c = struct.unpack_from('<i', raw, p)[0]
            p += 4
            r = []
            for _ in range(c):
                fid = struct.unpack_from('<i', raw, p)[0]
                pid = struct.unpack_from('<q' if big else '<i', raw, p + 4)[0]
                p += ps
                r.append(resolve(af, fid, pid))
            return r

        init = arr()
        tiers = []
        for _ in range(4):
            mn, mx = struct.unpack_from('<ii', raw, p)
            p += 8
            tiers.append((mn, mx, arr()))
        if p != len(raw):
            raise SystemExit(f'{name}: разбор не дошёл до конца ({p} из {len(raw)} байт)')
        if name in out:
            raise SystemExit(f'{name}: две таблицы с одним именем')
        out[name] = (init, tiers)
    return out


def cs(t, src):
    q = lambda xs: 'new[] { ' + ', '.join(f'"{x}"' for x in xs) + ' }' if xs else 'new string[0]'
    lines = [
        '// СГЕНЕРИРОВАНО tools/loot_tables.py из ' + src + ' — руками не править.',
        '// Таблицы лута одиночной игры (1.4 = 1.5 по числам и предметам); сетевая 1.1.2',
        '// их поменяла (больше полезного, меньше хлама) — docs/research/loot-sp-vs-mp.md.',
        'using System.Collections.Generic;',
        '',
        'public static partial class CoopLoot',
        '{',
        '    private static readonly Dictionary<string, SpTable> SpTables = new Dictionary<string, SpTable>',
        '    {',
    ]
    for name in sorted(t):
        init, tiers = t[name]
        lines.append(f'        {{ "{name}", new SpTable({q(init)},')
        for i, (mn, mx, items) in enumerate(tiers):
            end = ') },' if i == 3 else ','
            lines.append(f'            {mn}, {mx}, {q(items)}{end}')
    lines += ['    };', '}', '']
    return '\n'.join(lines)


if __name__ == '__main__':
    d = sys.argv[1]
    t = tables(load(d))
    if '--cs' in sys.argv:
        f = sys.argv[sys.argv.index('--cs') + 1]
        open(f, 'w', encoding='utf-8').write(cs(t, os.path.relpath(d)))
        print(f'{len(t)} таблиц → {f}')
    else:
        for name in sorted(t):
            init, tiers = t[name]
            print(f'== {name}' + (f'  init {init}' if init else ''))
            for i, (mn, mx, items) in enumerate(tiers):
                print(f'  T{i + 1} {mn}-{mx}: ' + ', '.join(items))
