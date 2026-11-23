// Кампания: общий счёт фрагментов и общая победа (этап 3).
//
// Счёт «сдано N из порога» живёт здесь и переживает смену острова: игра
// обнуляет _numberOfPiecesReturned в ResetWorldStateForFreshSession и снижает
// NumberOfPiecesToWin в DropTeleportParts — после загрузки каждого острова мы
// выставляем оба поля заново, а также m_Data.m_NumReturned/m_MaxNumTelePeices
// (сетка круга и сложность ИИ — исследование Kimi п.20-21) у сервера и клиентов.
// Порог: 5 тайников × 5 островов; если генерация острова поставила меньше —
// сумма реально созданных (иначе кампания непроходима).
// Победа — общая: финал у всех клиентов без имени победителя, статистика за всю
// кампанию (PlayerStats переносятся снимком игрока, время — с начала кампании).
// Решения — docs/stages/stage3.md.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class CoopCampaign
{
    public const int PiecesPerIsland = 5;

    // ---- сервер ----
    public int PiecesReturned;
    private readonly Dictionary<string, int> _islandPieceCount = new Dictionary<string, int>();
    public double PlayedBefore;        // секунды игры на прошлых островах
    public float CentreCircleCacheDist = -1f;

    public int PiecesTotal
    {
        get
        {
            int t = 0;
            foreach (var i in Islands)
            {
                int n;
                t += _islandPieceCount.TryGetValue(i, out n) ? n : PiecesPerIsland;
            }
            return t;
        }
    }

    // сервер: генерация острова закончилась — сколько тайников реально создано
    private void CountIslandPieces()
    {
        var holder = C_ProgressManager.m_Instance != null ? C_ProgressManager.m_Instance.m_pTelePeiceHolder : null;
        int n = 0;
        float best = -1f;
        var dep = GameObject.Find("P_TelePeiceDeposit(Clone)");
        for (int i = 0; holder != null && i < holder.childCount; i++)
        {
            var c = holder.GetChild(i);
            if (c.name != "P_TelePeiceHolderFull") continue;
            n++;
            if (dep != null)
            {
                float d = Vector2.Distance(new Vector2(c.position.x, c.position.z), new Vector2(dep.transform.position.x, dep.transform.position.z));
                if (best < 0f || d < best) best = d;
            }
        }
        _islandPieceCount[Island] = n;
        if (Island == "Centre") CentreCircleCacheDist = best;
        CoopMod.Log("server: " + Island + " has " + n + " piece caches" + (dep != null ? ", nearest to the stone circle " + best.ToString("0.0") + " m" : "") +
                    "; campaign threshold " + PiecesTotal);
        if (n < PiecesPerIsland) CoopMod.Warn("server: only " + n + " caches on " + Island + " — threshold lowered to " + PiecesTotal);
    }

    // сервер: выставить счёт в поля игры и разослать клиентам
    public void ApplyPieceCounts()
    {
        int total = PiecesTotal;
        NM.CoopSetPieceCounts(PiecesReturned, total);
        if (C_ProgressManager.m_Instance != null)
        {
            C_ProgressManager.m_Instance.m_Data.m_NumReturned = PiecesReturned;
            C_ProgressManager.m_Instance.m_Data.m_MaxNumTelePeices = total;
        }
        foreach (var r in NM.CoopRecords)
            networkView.RPC("CoopProgress", r.NetworkPlayer, PiecesReturned, total);
    }

    // сервер, из патча LogReturnedPiece: фрагмент сдан в круг
    public int ServerPieceReturned()
    {
        PiecesReturned = Mathf.Min(PiecesReturned + 1, PiecesTotal);
        ApplyPieceCounts();
        CoopMod.Log("server: piece returned to the stones: " + PiecesReturned + " of " + PiecesTotal);
        return PiecesReturned;
    }

    // сервер, из патча LogReturnedPiece: последний фрагмент сдан
    public void ServerVictory()
    {
        CoopMod.Log("server: campaign won after " + TravelCount + " travels, " + PiecesReturned + " pieces");
        ResetCampaign(true);
    }

    // новая кампания при следующем выходе из лобби
    private void ResetCampaign(bool deleteSave)
    {
        if (deleteSave) DeleteSave("new campaign");
        _arrivalSnaps.Clear();
        _restoring.Clear();
        _corpses.Clear();
        RollbackCount = 0;
        _templates.Clear();
        _generated.Clear();
        _emptiedCaches.Clear();
        _islandPieceCount.Clear();
        _travelSnaps.Clear();
        _restoredSnaps.Clear();
        _pieceOrigins.Clear();
        PiecesReturned = 0;
        PlayedBefore = 0;
        TravelCount = 0;
        Island = "Centre";
        ArrivalFrom = null;
        CentreCircleCacheDist = -1f;
    }

    // ---- клиент ----
    private int _cReturned = -1, _cTotal = -1;

    [RPC]
    private void CoopProgress(int returned, int total, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _cReturned = Mathf.Clamp(returned, 0, 1000);
        _cTotal = Mathf.Clamp(total, 0, 1000);
        ApplyClientProgress();
    }

    // и после каждой загрузки острова: LoadIsland перечитывает Progress.xml
    private void ApplyClientProgress()
    {
        if (_cTotal < 0) return;
        if (C_ProgressManager.m_Instance != null)
        {
            C_ProgressManager.m_Instance.m_Data.m_NumReturned = _cReturned;
            C_ProgressManager.m_Instance.m_Data.m_MaxNumTelePeices = _cTotal;
        }
        C_TelePeicesDisplay.SetCounts(_cReturned, Mathf.Max(_cTotal - _cReturned, 0));
    }

    // финал без имени победителя (из патча DisplayEndSequence)
    public void ClientSharedEnding()
    {
        CoopMod.Log("TEST client: end sequence on " + ClientIsland);
        _hint = null; _ask = null;
        if (C_TeleportPeiceEater.m_Instance != null && C_ProgressManager.m_Instance != null)
            C_ProgressManager.m_Instance.StartEndSequence();
        else
            CoopMod.Warn("client: no stone circle here, ending without the final scene");
        if (C_Notification.m_Instance != null)
            C_Notification.m_Instance.ShowNotification("All pieces returned - you escaped together!", 10f);
    }

    [RPC]
    private void CoopReportHud(int returned, int remaining, int dataReturned, int dataMax, NetworkMessageInfo info)
    {
        if (!Network.isServer || !CoopMod.AutoTest) return;
        var rec = Record(info.sender);
        if (rec == null) return;
        Check(returned == PiecesReturned && remaining == PiecesTotal - PiecesReturned && dataReturned == PiecesReturned && dataMax == PiecesTotal,
              Name(rec) + " piece counts on client: HUD " + returned + "/" + remaining + ", circle " + dataReturned + "/" + dataMax +
              " (server " + PiecesReturned + " of " + PiecesTotal + ")");
    }
}
