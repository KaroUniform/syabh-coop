// Лут как в одиночной игре (решение игроков 2026-11-23: «как в одиночной в
// точности»). Сетевая 1.1.2 поменяла таблицы C_LootTable (данные, не код);
// контейнеры и код заполнения у версий одни и те же — разбор
// docs/research/loot-sp-vs-mp.md. Перед заполнением контейнера (патч
// C_InventoryFiller.DoFill, только сервер) его таблица получает числа и списки
// предметов одиночной игры (CoopLootSP.cs). Предметы — те же префабы сетевой
// версии, ищем их по имени среди всех таблиц (73 имени, каждое — один префаб).
// Не нашли хоть один предмет — таблицу не трогаем (лучше сетевая, чем наполовину).
using System.Collections.Generic;
using UnityEngine;

public static partial class CoopLoot
{
    private class SpTable
    {
        public readonly string[] Init;
        public readonly int[] Min = new int[4], Max = new int[4];
        public readonly string[][] Items = new string[4][];
        public SpTable(string[] init, int min1, int max1, string[] t1, int min2, int max2, string[] t2,
                       int min3, int max3, string[] t3, int min4, int max4, string[] t4)
        {
            Init = init;
            Min[0] = min1; Max[0] = max1; Items[0] = t1;
            Min[1] = min2; Max[1] = max2; Items[1] = t2;
            Min[2] = min3; Max[2] = max3; Items[2] = t3;
            Min[3] = min4; Max[3] = max4; Items[3] = t4;
        }
    }

    private static readonly HashSet<int> _done = new HashSet<int>();
    private static Dictionary<string, C_PickupItem> _prefabs;
    public static int Applied, Failed;

    // таблица → значения одиночной игры (один раз на таблицу за запуск сервера)
    public static void Apply(C_LootTable t)
    {
        if (t == null || !_done.Add(t.GetInstanceID())) return;
        SpTable sp;
        if (!SpTables.TryGetValue(t.gameObject.name, out sp))
        {
            CoopMod.Warn("loot: table " + t.gameObject.name + " is unknown — left as in multiplayer");
            Failed++;
            return;
        }
        var init = Resolve(sp.Init);
        var tiers = new C_PickupItem[4][];
        for (int i = 0; i < 4; i++) tiers[i] = Resolve(sp.Items[i]);
        if (init == null || tiers[0] == null || tiers[1] == null || tiers[2] == null || tiers[3] == null)
        {
            CoopMod.Warn("loot: table " + t.gameObject.name + " left as in multiplayer (an item was not found)");
            Failed++;
            return;
        }
        t.m_InitialItems = init;
        t.m_Tier1MinPicked = sp.Min[0]; t.m_Tier1MaxPicked = sp.Max[0]; t.m_Tier1Items = tiers[0];
        t.m_Tier2MinPicked = sp.Min[1]; t.m_Tier2MaxPicked = sp.Max[1]; t.m_Tier2Items = tiers[1];
        t.m_Tier3MinPicked = sp.Min[2]; t.m_Tier3MaxPicked = sp.Max[2]; t.m_Tier3Items = tiers[2];
        t.m_Tier4MinPicked = sp.Min[3]; t.m_Tier4MaxPicked = sp.Max[3]; t.m_Tier4Items = tiers[3];
        Applied++;
        CoopMod.Log("loot: " + t.gameObject.name + " uses the single-player table");
    }

    public static bool WasApplied(C_LootTable t) { return t != null && _done.Contains(t.GetInstanceID()); }

    // проверка для автотеста: таблица сейчас совпадает с одиночной?
    public static bool MatchesSinglePlayer(C_LootTable t)
    {
        SpTable sp;
        if (t == null || !SpTables.TryGetValue(t.gameObject.name, out sp)) return false;
        if (!Same(t.m_InitialItems, sp.Init)) return false;
        return t.m_Tier1MinPicked == sp.Min[0] && t.m_Tier1MaxPicked == sp.Max[0] && Same(t.m_Tier1Items, sp.Items[0])
            && t.m_Tier2MinPicked == sp.Min[1] && t.m_Tier2MaxPicked == sp.Max[1] && Same(t.m_Tier2Items, sp.Items[1])
            && t.m_Tier3MinPicked == sp.Min[2] && t.m_Tier3MaxPicked == sp.Max[2] && Same(t.m_Tier3Items, sp.Items[2])
            && t.m_Tier4MinPicked == sp.Min[3] && t.m_Tier4MaxPicked == sp.Max[3] && Same(t.m_Tier4Items, sp.Items[3]);
    }

    private static bool Same(C_PickupItem[] a, string[] names)
    {
        if (a == null || a.Length != names.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] == null || a[i].gameObject.name != names[i]) return false;
        return true;
    }

    // имена → префабы предметов; null — какого-то нет
    private static C_PickupItem[] Resolve(string[] names)
    {
        var r = new C_PickupItem[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            r[i] = Prefab(names[i]);
            if (r[i] == null) { CoopMod.Warn("loot: item " + names[i] + " not found"); return null; }
        }
        return r;
    }

    private static C_PickupItem Prefab(string name)
    {
        C_PickupItem p;
        if (_prefabs != null && _prefabs.TryGetValue(name, out p) && p != null) return p;
        // сетевые таблицы ещё не тронуты целиком — их списки и есть каталог префабов
        _prefabs = new Dictionary<string, C_PickupItem>();
        foreach (C_LootTable t in Resources.FindObjectsOfTypeAll(typeof(C_LootTable)))
            foreach (var arr in new[] { t.m_InitialItems, t.m_Tier1Items, t.m_Tier2Items, t.m_Tier3Items, t.m_Tier4Items })
                if (arr != null)
                    foreach (var it in arr)
                        if (it != null && !_prefabs.ContainsKey(it.gameObject.name)) _prefabs[it.gameObject.name] = it;
        return _prefabs.TryGetValue(name, out p) ? p : null;
    }
}
