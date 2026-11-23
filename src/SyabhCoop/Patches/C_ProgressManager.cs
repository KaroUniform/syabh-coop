// Патч C_ProgressManager: ReDropTeleportParts без каменного круга.
// В игре GameObject.Find("P_TelePeiceDeposit(Clone)") без проверки на null
// (C_ProgressManager.cs:491-492) — на North/South/East/West круга нет.
// Тело — как в игре, кроме проверки.
// Этап 3: DropTeleportParts — раскладка кампании (5 на остров, на Centre 4 + 1
// у круга, как в одиночной 1.4) и без снижения NumberOfPiecesToWin.
#pragma warning disable CS0626
using Assets.Networking;
using MonoMod;
using UnityEngine;

class patch_C_ProgressManager : C_ProgressManager
{
    [MonoModIgnore] private extern void RememberPeicesForThisIsland(int numpeices);
    [MonoModIgnore] private extern bool DropTeleportPeice(Vector3 spawnpos, Vector3 angle, bool isFull);

    // было: C_ProgressManager.cs:495-546 (MP) — NumberOfPiecesOnIsland случайных
    // тайников и снижение порога победы; блок у круга — sp-1.4/C_ProgressManager.cs:478-505
    public new void DropTeleportParts(int seed)
    {
        Random.seed = seed;
        MakeHolder();
        string island = C_ChangeIslands.m_Instance.m_CurrentIsland;
        int want = CoopCampaign.PiecesPerIsland;
        if (island == "Centre")
        {
            want--;
            GameObject dep = GameObject.Find("P_TelePeiceDeposit(Clone)");
            float half = C_WorldGraph.m_Instance.m_BoundarySize / 2f;
            Vector3 p;
            if (dep != null) { p = dep.transform.position; p.x += 9f; p.z += 9f; }
            else p = new Vector3(half + 13f, 50f, half + 13f);
            bool ok = DropTeleportPeice(p, Vector3.zero, isFull: true);
            CoopMod.Log("worldgen: piece cache by the stone circle " + (dep != null ? "at " : "(no circle found, island centre) at ") +
                        CoopCampaign.V(p) + (ok ? "" : " — FAILED, adding one more random cache"));
            if (!ok) want++;
        }
        int count = C_WorldGraph.m_Instance.m_Data.m_C_VoronoiCellList.Count;
        bool[] used = new bool[count];
        for (int j = 0; j < want; j++)
        {
            bool placed = false;
            for (int tries = 0; !placed && tries < 500; tries++)
            {
                int cell = Random.Range(0, count);
                int type = C_WorldGraph.m_Instance.m_Data.m_C_VoronoiCellList[cell].m_Type;
                bool allowed = true;
                for (int k = 0; k < m_BannedFragRegions.Length; k++)
                    if (type == C_RegionManager.m_Instance.FindType(m_BannedFragRegions[k])) { allowed = false; break; }
                if (!allowed || used[cell]) continue;
                C_Point c = C_WorldGraph.m_Instance.m_Data.m_C_VoronoiCellList[cell].m_TrueCentre;
                if (DropTeleportPeice(new Vector3(c.X, 50f, c.Y), Vector3.zero, isFull: true))
                {
                    placed = true;
                    used[cell] = true;
                }
            }
        }
        int num = m_pTelePeiceHolder.childCount;
        CoopMod.Log("worldgen: dropped " + num + " piece caches on " + island);
        RememberPeicesForThisIsland(num);
        m_Data.m_MaxNumTelePeices += num;   // кампания всё равно выставит свой порог после загрузки
    }

    public new void ReDropTeleportParts()
    {
        MakeHolder();
        C_TeleCacheData c_TeleCacheData = C_SaveManager.m_Instance.TeleCachesLoadData();
        if (c_TeleCacheData == null || c_TeleCacheData.m_NumCaches == 0)
        {
            return;
        }
        GameObject gameObject;
        for (int i = 0; i < c_TeleCacheData.m_NumCaches; i++)
        {
            if (c_TeleCacheData.m_WhichPeices[i] > -1)
            {
                gameObject = Object.Instantiate(m_pTeleportCacheFull, c_TeleCacheData.m_Locations[i], Quaternion.identity) as GameObject;
                gameObject.transform.eulerAngles = new Vector3(Random.Range(-20, 20), Random.Range(0, 360), Random.Range(-20, 20));
                gameObject.transform.parent = m_pTelePeiceHolder;
                gameObject.name = "P_TelePeiceHolderFull";
                gameObject.GetComponent<C_InventoryFillerTeleBit>().DoFill(c_TeleCacheData.m_WhichPeices[i]);
                gameObject.GetComponent<C_Inventory>().InventoryID = NetworkStatics.GetNextInventoryId();
            }
            else
            {
                gameObject = Object.Instantiate(m_pTeleportCacheEmpty, c_TeleCacheData.m_Locations[i], Quaternion.identity) as GameObject;
                gameObject.transform.eulerAngles = new Vector3(Random.Range(-20, 20), Random.Range(0, 360), Random.Range(-20, 20));
                gameObject.transform.parent = m_pTelePeiceHolder;
                gameObject.name = "P_TelePeiceHolderEmpty";
            }
        }
        gameObject = GameObject.Find("P_TelePeiceDeposit(Clone)");
        if (gameObject != null)
        {
            gameObject.GetComponent<C_Inventory>().InventoryID = NetworkStatics.GetNextInventoryId();
        }
        CoopMod.Log("island: " + c_TeleCacheData.m_NumCaches + " piece caches, stone circle " + (gameObject != null ? "present" : "absent"));
    }
}
