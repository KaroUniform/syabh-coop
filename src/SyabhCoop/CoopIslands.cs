// Кампания: острова и переход между ними (этап 1). Сервер — хозяин состояния.
//
// Поток перехода (сервер), см. docs/stages/stage1.md:
//   согласие обоих → SaveLeavingIsland (лут — IslandInventoriesSave, опустевшие
//   тайники — свой список по позиции) → CoopTravelBegin клиентам → teardown
//   игры (ServerTearDownForNewMap) → CoopEnterIsland: первое посещение —
//   генерация, повторное — загрузка с диска сервера → ServerBeforeBootstrap
//   опустошает тайники → игра отправляет мир клиентам → спавн у лодки прибытия.
// Клиенты всегда генерируют остров из шаблона сами (как в игре); имя острова
// приходит RPC CoopIslandInfo перед шаблоном.
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class CoopCampaign
{
    public static readonly string[] Islands = { "Centre", "North", "South", "East", "West" };

    public static bool IsIsland(string s)
    {
        return Array.IndexOf(Islands, s) >= 0;
    }

    // ---- сервер ----
    private readonly Dictionary<string, C_IslandTemplateData> _templates = new Dictionary<string, C_IslandTemplateData>();
    private readonly HashSet<string> _generated = new HashSet<string>();          // остров есть на диске сервера
    private readonly Dictionary<string, List<Vector2>> _emptiedCaches = new Dictionary<string, List<Vector2>>();
    public string Island = "Centre";            // текущий остров кампании (сервер)
    public string ArrivalFrom;                  // с какого острова приплыли (null — начало кампании)
    public bool Transitioning;                  // идёт переход: предложения не принимаем
    private int _spawnIndex;                    // сдвиг точки спавна для 2-го игрока
    public int TravelCount;                     // для автотеста и журнала
    private bool _newFlagUsed;

    private static patch_NetworkManager NM { get { return (patch_NetworkManager)(object)NetworkManager.Instance; } }

    // Лобби пройдено (или #new): начать или продолжить кампанию.
    public void ServerStartCampaign()
    {
        if (_templates.Count == 0)
        {
            // продолжить сохранённую (этап 6), кроме явной новой и автотестов этапов 1-5
            bool fresh = (NewCampaignFlag && !_newFlagUsed) || CoopMod.AutoTest || AutoTest6a || AutoTest6c;
            _newFlagUsed = true;
            if (fresh || !TryLoadCampaign())
            {
                ResetCampaign(true);
                CreateTemplates();
                CoopMod.Log("server: new campaign");
            }
            else CoopMod.Log("server: continuing the saved campaign on " + Island);
        }
        else
        {
            CoopMod.Log("server: continuing campaign on " + Island);
        }
        Transitioning = true;
        NM.CoopEnterIsland(Island, _templates[Island], !_generated.Contains(Island));
    }

    // Шаблоны всех пяти островов — один раз на кампанию: Centre — сельский
    // (биом 0, как одиночная по умолчанию), остальные — случайный биом.
    private void CreateTemplates()
    {
        _templates.Clear();
        int baseSeed = (int)DateTime.Now.Ticks;
        for (int i = 0; i < Islands.Length; i++)
        {
            var t = new C_IslandTemplateData();
            t.m_BiomeType = i == 0 ? 0 : UnityEngine.Random.Range(0, 5);
            C_MenuGeneration.MakeDefaultRarityValues(t);
            t.m_RandomNumberSeed = baseSeed + 7919 * (i + 1);
            _templates[Islands[i]] = t;
            CoopMod.Log("server: template " + Islands[i] + ": biome " + t.m_BiomeType + ", seed " + t.m_RandomNumberSeed);
        }
    }

    public void ServerGenerationComplete()
    {
        _generated.Add(Island);
        CoopMod.Log("server: generation of " + Island + " complete");
    }

    public void ServerSendIslandInfo(NetworkPlayer client)
    {
        networkView.RPC("CoopIslandInfo", client, Island);
    }

    // Игра сбросила состояние мира (перед загрузкой острова) — и сервер, и клиент.
    public void OnWorldReset()
    {
        _boats.Clear();
        _spawnIndex = 0;
        _pieceOrigins.Clear();   // ItemID новой карты начинаются заново (NetworkStatics.Reset)
        _corpses.Clear();
        _rollbackPending = false;
        _cCorpses.Clear();
        _deadUntil = -1f;
        _reviveHeld = 0f;
        ClearPending("world reset");
    }

    // Сервер: остров загружен, игра сейчас отправит мир ждущим клиентам.
    public void ServerBeforeBootstrap()
    {
        int emptied = 0;
        List<Vector2> list;
        var holder = C_ProgressManager.m_Instance != null ? C_ProgressManager.m_Instance.m_pTelePeiceHolder : null;
        if (holder != null && _emptiedCaches.TryGetValue(Island, out list))
        {
            var full = new List<C_Inventory>();
            for (int i = 0; i < holder.childCount; i++)
            {
                Transform c = holder.GetChild(i);
                if (c.name != "P_TelePeiceHolderFull") continue;
                var inv = c.GetComponent<C_Inventory>();
                if (inv != null && ContainsPos(list, c.position)) full.Add(inv);
            }
            foreach (var inv in full)
            {
                inv.ClearInventory();
                NM.CoopEmptyCache(inv.InventoryID);
                emptied++;
            }
        }
        if (!_islandPieceCount.ContainsKey(Island)) CountIslandPieces();   // первое посещение: все тайники полные
        ApplyPieceCounts();
        RememberArrivalWorld();
        Transitioning = false;
        _serverDigest = IdDigest(out _serverDigestCount);
        CoopMod.Log("server: island ids digest " + _serverDigest + " (" + _serverDigestCount + " inventories)");
        CoopMod.Log("server: island " + Island + " ready (travel #" + TravelCount + ", arrived from " +
                    (ArrivalFrom ?? "-") + "), caches emptied from campaign state: " + emptied +
                    ", boats: " + _boats.Count);
    }

    // Номера инвентарей мира (сундуки, тайники, круг) обязаны совпадать у
    // сервера и клиентов: клиент открывает сундук по номеру. Сверяем отпечаток
    // упорядоченного списка (исследование этапа 1, Kimi п.11).
    private string _serverDigest;
    private int _serverDigestCount;

    public static string IdDigest(out int count)
    {
        uint h = 2166136261;
        count = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var inv in C_SaveManager.m_Instance.m_pIslandInventories)
        {
            if (inv == null) continue;
            sb.Append(inv.InventoryID).Append(':').Append(inv.m_InventoryName).Append('|');
            count++;
        }
        var holder = C_ProgressManager.m_Instance != null ? C_ProgressManager.m_Instance.m_pTelePeiceHolder : null;
        for (int i = 0; holder != null && i < holder.childCount; i++)
        {
            var c = holder.GetChild(i);
            if (c.name != "P_TelePeiceHolderFull") continue;
            var inv = c.GetComponent<C_Inventory>();
            sb.Append("T").Append(inv != null ? inv.InventoryID : -1).Append('@').Append((int)c.position.x).Append(',').Append((int)c.position.z).Append('|');
            count++;
        }
        foreach (char ch in sb.ToString()) { h ^= ch; h *= 16777619; }
        return h.ToString("x8");
    }

    [RPC]
    private void CoopReportDigest(string island, int count, string digest, NetworkMessageInfo info)
    {
        if (!Network.isServer) return;
        var r = Record(info.sender);
        if (r == null || !CoopMod.AutoTest) return;   // только известные игроки и только в автотесте (Kimi п.15, Qwen п.7)
        string who = Name(r);
        bool ok = island == Island && digest == _serverDigest && count == _serverDigestCount;
        CoopMod.Log((ok ? "TEST PASS" : "TEST FAIL") + ": " + who + " inventory ids on " + CoopMod.Clean(island, 16) +
                    (ok ? " match the server" : " DIFFER: client " + CoopMod.Clean(digest, 16) + "/" + count +
                    ", server " + _serverDigest + "/" + _serverDigestCount + " on " + Island));
        if (ok) _pass++; else _fail++;
    }

    private static bool ContainsPos(List<Vector2> list, Vector3 p)
    {
        foreach (var v in list)
            if (Mathf.Abs(v.x - p.x) < 0.5f && Mathf.Abs(v.y - p.z) < 0.5f) return true;
        return false;
    }

    // Сервер: точка спавна у лодки прибытия.
    public bool ServerArrivalSpawn(out Vector3 pos, out Vector3 rot)
    {
        pos = Vector3.zero; rot = Vector3.zero;
        if (ArrivalFrom == null) return false;
        Boat b = ArrivalBoat();
        if (b == null)
        {
            CoopMod.Warn("server: no arrival boat on " + Island + " (from " + ArrivalFrom + "), default spawn");
            return false;
        }
        // игроки — по обе стороны от точки у лодки, в шаге друг от друга
        int i = (_spawnIndex++) % 4;   // респауны тоже идут сюда — не уходить от лодки
        Vector3 side = Vector3.Cross(Vector3.up, b.Facing).normalized;
        float off = (i % 2 == 0 ? 1f : -1f) * (1.2f + 1.2f * (i / 2));
        pos = b.SpawnPos + side * off;
        rot = b.SpawnRot;
        CoopMod.Log("server: spawn #" + i + " at " + b.Direction + " boat " + V(pos));
        return true;
    }

    // На внешнем острове лодка одна (обратно на Centre); на Centre — та, что
    // ведёт на остров, откуда приплыли.
    private Boat ArrivalBoat()
    {
        foreach (var b in _boats)
            if (Island != "Centre" || b.Direction == ArrivalFrom) return b;
        return null;
    }

    // Сервер: переход всех на остров target. Проверки — до вызова.
    private void ServerTravel(string target)
    {
        if (!IsIsland(target) || target == Island) { CoopMod.Warn("server: bad travel target " + target); return; }
        Transitioning = true;
        TravelCount++;
        string from = Island;
        CoopMod.Log("server: TRAVEL #" + TravelCount + " " + from + " -> " + target);
        ReturnCorpsePieces();
        SaveLeavingIsland();
        SnapAllForTravel();
        PlayedBefore = NM.CoopPlayedSeconds;
        foreach (var r in NM.CoopRecords)
        {
            NM.CoopEndTrade(r);
            networkView.RPC("CoopTravelBegin", r.NetworkPlayer, target);
        }
        ClearCorpsesBeforeTeardown();
        NM.CoopTearDown();
        ArrivalFrom = from;
        Island = target;
        SaveNow(false, "travel");
        NM.CoopEnterIsland(target, _templates[target], !_generated.Contains(target));
    }

    // Мировая часть уходящего острова: лут (как в игре, в <остров>Inv.xml) и
    // опустевшие тайники (свой список: файл TeleCaches игры не перезаписываем —
    // ReturnAliveCaches в MP дал бы дубли тайников).
    private void SaveLeavingIsland()
    {
        C_SaveManager.m_Instance.m_SavePath.CurrentIsland = Island;
        try { C_SaveManager.m_Instance.IslandInventoriesSave(); }
        catch (Exception e) { CoopMod.Warn("server: saving island inventories failed: " + e); }
        List<Vector2> list;
        if (!_emptiedCaches.TryGetValue(Island, out list)) _emptiedCaches[Island] = list = new List<Vector2>();
        var full = NM.CoopFullCaches;
        if (full != null)
            foreach (var kv in full)
            {
                if (kv.Value == null) continue;
                Vector3 p = kv.Value.transform.position;
                if (!kv.Value.activeSelf) { if (!ContainsPos(list, p)) list.Add(new Vector2(p.x, p.z)); }
                else list.RemoveAll(v => Mathf.Abs(v.x - p.x) < 0.5f && Mathf.Abs(v.y - p.z) < 0.5f);  // фрагмент вернули
            }
        CoopMod.Log("server: saved " + Island + ": " + C_SaveManager.m_Instance.m_pIslandInventories.Count +
                    " loot inventories, emptied caches " + list.Count);
    }

    // ---- клиент ----
    public string ClientIsland = "Centre";
    private string _clientNextIsland = "Centre";

    [RPC]
    private void CoopIslandInfo(string island, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        if (!IsIsland(island)) { CoopMod.Warn("client: unknown island " + CoopMod.Clean(island)); return; }
        _clientNextIsland = island;
        CoopMod.Log("client: next island " + island);
    }

    // оригинал SendIslandTemplateToClient отработал (сохранил шаблон как Centre,
    // запустил генерацию) — подставить настоящее имя
    public void ClientTemplateArrived()
    {
        ClientIsland = _clientNextIsland;
        C_ChangeIslands.m_Instance.m_CurrentIsland = ClientIsland;
        C_SaveManager.m_Instance.m_SavePath.CurrentIsland = ClientIsland;
        if (ClientIsland != "Centre")
            C_SaveManager.m_Instance.IslandTemplateSaveData(ClientIsland, C_ChangeIslands.m_Instance.m_IslandTemplateData);
        CoopMod.Log("client: template for " + ClientIsland + " received, generating");
    }

    [RPC]
    private void CoopTravelBegin(string target, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        CoopMod.Log("client: travel to " + CoopMod.Clean(target) + " begins");
        _ask = null;
        ClientRememberSlots();
        try
        {
            if (InventoryUiManager.m_Instance != null) InventoryUiManager.m_Instance.HideInventories();
            C_MenuWindowManager.ForceHideAllWindows();
        }
        catch (Exception e) { CoopMod.Warn("client: closing windows: " + e.Message); }
        Notify("Sailing to " + CoopMod.Clean(target) + "...");
    }

    private bool FromServer(NetworkMessageInfo info)
    {
        return Network.isClient && Network.connections.Length > 0 && info.sender == Network.connections[0];
    }

    public static string V(Vector3 v)
    {
        return "(" + v.x.ToString("0.0") + ", " + v.y.ToString("0.0") + ", " + v.z.ToString("0.0") + ")";
    }
}
