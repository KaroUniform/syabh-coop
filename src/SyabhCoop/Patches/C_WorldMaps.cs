// Патч C_WorldMaps.RegenerateRegionArray: тот же результат, в ~25 раз быстрее
// (CoopRegionMap). Оригинал под CrossOver — 79 с без кадров, клиенты теряли
// соединение с сервером во время генерации острова.
#pragma warning disable CS0626

class patch_C_WorldMaps : C_WorldMaps
{
    public extern void orig_RegenerateRegionArray();
    public new void RegenerateRegionArray()
    {
        var cells = C_WorldGraph.m_Instance.m_Data.m_C_VoronoiCellList;
        int n = cells.Count;
        var cx = new int[n]; var cy = new int[n];
        for (int i = 0; i < n; i++)
        {
            // как в оригинале: (int)(float) координаты центра
            cx[i] = (int)cells[i].m_CentrePoint.X;
            cy[i] = (int)cells[i].m_CentrePoint.Y;
        }
        var t0 = UnityEngine.Time.realtimeSinceStartup;
        float[] fast = CoopRegionMap.Compute(m_Dim, cx, cy);
        CoopMod.Log("worldgen: region map " + m_Dim + "x" + m_Dim + ", " + n + " cells, fast in " +
                    ((UnityEngine.Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms");
        if (CoopMod.CheckRegion)
        {
            orig_RegenerateRegionArray();
            float[] orig = m_Data.m_RegionArray.m_Array;
            int diff = 0;
            for (int i = 0; i < orig.Length && i < fast.Length; i++) if (orig[i] != fast[i]) diff++;
            if (orig.Length != fast.Length) diff = -1;
            CoopMod.Log("TEST " + (diff == 0 ? "PASS" : "FAIL") + " region map equals original (" + diff + " differing cells)");
        }
        System.Array.Copy(fast, m_Data.m_RegionArray.m_Array, fast.Length);
    }
}
