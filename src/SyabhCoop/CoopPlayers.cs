// Кампания: игрок переносит вещи и состояние между островами (этап 2).
//
// Сервер: перед отплытием — снимок каждого заспавненного игрока по имени
// (предметы с позицией и патронами / номером записки, здоровье и бодрость как
// float). Новое тело на следующем острове игра создаёт со стартовым набором;
// через 2 кадра (после Start компонентов, который сбрасывает здоровье и
// бодрость — исследование Kimi п.17) заменяем набор снимком и шлём клиенту
// содержимое инвентаря и ForceSyncHealthVitality.
// Фрагменты: своя таблица «ItemID → остров + позиция тайника» (связь игры
// _telepieceIdToInventoryId живёт только до смены карты), переносится снимком.
// Клиент: слоты быстрого выбора знает только он сам — запоминает при
// CoopTravelBegin, возвращает после спавна. Решения — docs/stages/stage2.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class CoopCampaign
{
    public class Origin
    {
        public string Island;
        public Vector2 Pos;   // x, z тайника
    }

    public class ItemSnap
    {
        public string Name;
        public int Value, Left, Top;
        public Origin Origin;   // только у фрагментов
    }

    public class PlayerSnap
    {
        public List<ItemSnap> Items = new List<ItemSnap>();
        public float Health, Vitality;
        public string Print;    // отпечаток предметов для проверки
        public Assets.Networking.PlayerStats Stats;   // статистика MP за кампанию
    }

    // ---------------- сервер ----------------
    private readonly Dictionary<string, PlayerSnap> _travelSnaps = new Dictionary<string, PlayerSnap>();
    private readonly Dictionary<int, Origin> _pieceOrigins = new Dictionary<int, Origin>();
    private readonly Dictionary<string, PlayerSnap> _restoredSnaps = new Dictionary<string, PlayerSnap>(); // для проверки после прибытия
    private readonly HashSet<string> _restoring = new HashSet<string>();   // тело есть, вещи ещё не восстановлены

    public static bool IsPiece(C_PickupItem it)
    {
        return it != null && it.ItemName != null && it.ItemName.StartsWith("TeleportPeice", StringComparison.Ordinal);
    }

    // как C_SaveManager.InventoryPlayerSave: патроны в обойме / в оружии / номер записки
    public static int ItemValue(C_PickupItem it)
    {
        var a = it.GetComponent<C_AmmoInClip>(); if (a != null) return a.m_CurrentAmmo;
        var g = it.GetComponent<C_AmmoOnGun>(); if (g != null) return g._currentAmmo;
        var r = it.GetComponent<C_Readable>(); if (r != null) return r.m_WhichTextAsset;
        return 0;
    }

    // только то, что сеть игры передаёт клиенту (номер записки — нет)
    private static int NetValue(C_PickupItem it)
    {
        var a = it.GetComponent<C_AmmoInClip>(); if (a != null) return a.m_CurrentAmmo;
        var g = it.GetComponent<C_AmmoOnGun>(); if (g != null) return g._currentAmmo;
        return 0;
    }

    public static string InvPrint(C_Inventory inv)
    {
        if (inv == null) return "-";
        var parts = inv.Items.Where(i => i != null)
            .Select(i => i.ItemName + "@" + i.InventoryPositionLeft + "," + i.InventoryPositionTop + "=" + NetValue(i))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        return parts.Length == 0 ? "(empty)" : string.Join(";", parts);
    }

    private static string SnapPrint(PlayerSnap s, Func<ItemSnap, int> value)
    {
        var parts = s.Items.Select(i => i.Name + "@" + i.Left + "," + i.Top + "=" + value(i))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return parts.Length == 0 ? "(empty)" : string.Join(";", parts);
    }

    public PlayerSnap TakeSnap(NetworkPlayerRecord r)
    {
        var c = r.Components;
        var s = new PlayerSnap
        {
            Health = c.CharacterHealth != null ? c.CharacterHealth._currentHealth : 100f,
            Vitality = c.CharacterVitality != null ? c.CharacterVitality._currentVitality : 0f,
            Stats = r.PlayerStats,   // teardown заменит объект новым (NetworkManager.cs:3697)
        };
        foreach (var it in c.Inventory.Items)
        {
            if (it == null) continue;
            Origin o = null;
            if (IsPiece(it)) o = PieceOrigin(it.ItemID);
            s.Items.Add(new ItemSnap { Name = it.ItemName, Value = ItemValue(it), Left = it.InventoryPositionLeft, Top = it.InventoryPositionTop, Origin = o });
        }
        s.Print = InvPrint(c.Inventory);
        return s;
    }

    // происхождение фрагмента по его текущему ItemID (null — неизвестно)
    private Origin PieceOrigin(int itemId)
    {
        Origin o;
        if (_pieceOrigins.TryGetValue(itemId, out o)) return o;
        CoopMod.Warn("server: piece item " + itemId + " has no known cache");
        return null;
    }

    // перед ServerTearDownForNewMap
    // Снимки не очищаем: снимок отключившегося (ещё не вернувшегося) игрока
    // ждёт его возвращения (ревью Kimi п.3); присутствующим — перезаписываем.
    private void SnapAllForTravel()
    {
        foreach (var r in Spawned())
        {
            var s = TakeSnap(r);
            _travelSnaps[Key(r)] = s;
            CoopMod.Log("server: snapshot " + Name(r) + ": " + s.Items.Count + " items, health " + s.Health.ToString("0.00") +
                        ", vitality " + s.Vitality.ToString("0.00") + ", pieces " + s.Items.Count(i => i.Origin != null));
        }
    }

    // из патча RegisterPlayerObjectsAndCreateRemoteBody (серверное тело создано,
    // игра сейчас выдаст стартовый набор)
    public void ServerBodyCreated(ClientPlayerManager cpm)
    {
        var rec = Record(cpm.networkView.owner);
        if (rec == null) return;
        // счёт и сетка круга — и вошедшему посреди острова (ревью Qwen п.6)
        networkView.RPC("CoopProgress", cpm.networkView.owner, PiecesReturned, PiecesTotal);
        // вошедшему посреди острова — трупы, которые ещё можно поднять (ревью Kimi п.4)
        foreach (var c in _corpses)
        {
            if (!c.Revivable || c.Owner == cpm.networkView.owner) continue;
            int left = Mathf.CeilToInt(ReviveSeconds - (Time.realtimeSinceStartup - c.DiedAt));
            if (left > 0) networkView.RPC("CoopCorpse", cpm.networkView.owner, c.InvId, c.Name, left);
        }
        string key = Key(rec);
        PlayerSnap s;
        if (!_travelSnaps.TryGetValue(key, out s))
        {
            StartCoroutine(ArrivalSnapLater(cpm.networkView.owner));   // этап 5: откат к прибытию
            return;
        }
        _travelSnaps.Remove(key);
        _arrivalSnaps[key] = NoStats(s);
        _restoring.Add(key);
        StartCoroutine(RestoreLater(cpm.networkView.owner, key, s));
    }

    private IEnumerator RestoreLater(NetworkPlayer owner, string key, PlayerSnap s)
    {
        yield return null;
        yield return null;
        var rec = Record(owner);
        if (rec == null || !rec.IsSpawned || rec.Components == null)
        {
            CoopMod.Warn("server: player " + owner + " is gone before restore, keeping the snapshot");
            _travelSnaps[key] = s;
            _restoring.Remove(key);
            yield break;
        }
        try { ApplySnap(rec, s); }
        catch (Exception e) { CoopMod.Warn("server: restore of " + Name(rec) + " failed: " + e); }
        finally { _restoring.Remove(key); }
    }

    public void ApplySnap(NetworkPlayerRecord rec, PlayerSnap s)
    {
        var c = rec.Components;
        var inv = c.Inventory;
        inv.ClearInventory();   // стартовый набор, выданный игрой
        int lost = 0;
        foreach (var i in s.Items)
        {
            C_PickupItem p = C_SaveManager.m_Instance.InventoryFindPickupItem(i.Name, i.Value);
            if (p == null) { lost++; CoopMod.Warn("server: no prefab for item " + i.Name); continue; }
            if (!inv.TryAddItem(p, i.Left, i.Top) && !inv.AddItem(p))
            {
                lost++;
                CoopMod.Warn("server: no room for " + i.Name + " in " + Name(rec) + "'s inventory");
                UnityEngine.Object.Destroy(p.gameObject);
                continue;
            }
            if (i.Origin != null)
            {
                // AddItem мог вернуть другой объект только для оружия-дубля; фрагмент — сам p
                p.ItemID = NetworkStatics.GetNextItemId();
                _pieceOrigins[p.ItemID] = i.Origin;
            }
        }
        if (s.Stats != null) rec.PlayerStats = s.Stats;
        if (c.CharacterHealth != null) c.CharacterHealth.SetHealth(s.Health);
        if (c.CharacterVitality != null) c.CharacterVitality.SetVitality(s.Vitality);
        NetworkManager.Instance.SendPlayerInventoryContent(c.PlayerGameObject);
        rec.ClientPlayerManager.networkView.RPC("ForceSyncHealthVitality", rec.NetworkPlayer,
            c.CharacterHealth._currentHealth, c.CharacterVitality._currentVitality);
        _restoredSnaps[Key(rec)] = s;
        CoopMod.Log("server: restored " + Name(rec) + ": " + inv.Items.Count + " items (lost " + lost + "), health " +
                    c.CharacterHealth._currentHealth.ToString("0.00") + ", vitality " + c.CharacterVitality._currentVitality.ToString("0.00"));
        if (CoopMod.AutoTest)
        {
            Check(lost == 0 && InvPrint(inv) == s.Print, Name(rec) + " items on server after travel match the snapshot" +
                  (InvPrint(inv) == s.Print ? "" : ": before " + s.Print + " | after " + InvPrint(inv)));
        }
    }

    // ---- фрагменты: откуда взят ----

    // из патча EmptyTelepieceCache: фрагмент вынут из тайника
    public void ServerPieceTaken(int telepieceId, Vector3 cachePos)
    {
        _pieceOrigins[telepieceId] = new Origin { Island = Island, Pos = new Vector2(cachePos.x, cachePos.z) };
        CoopMod.Log("server: piece " + telepieceId + " taken from cache at " + V(cachePos) + " on " + Island);
    }

    // из патча ReturnTelepieceToCache (отключение игрока, исчезновение трупа):
    // true — сделали сами, false — пусть сработает игра (связь восстановлена).
    public bool ServerReturnPiece(C_PickupItem piece)
    {
        var map = NM.CoopTelepieceToCache;
        int invId;
        if (map.TryGetValue(piece.ItemID, out invId) && NM.CoopEmptyCaches.ContainsKey(invId) && NM.CoopFullCaches.ContainsKey(invId))
            return false;   // взят на этой карте — штатно
        Origin o;
        _pieceOrigins.TryGetValue(piece.ItemID, out o);
        // Инвариант кампании: пустых тайников столько же, сколько фрагментов на
        // руках, в сундуках и трупах. Возвращаемый фрагмент заполняет свой тайник,
        // а если он неизвестен/уже полон — любой пустой (сначала на этом острове).
        // Фрагмент не уничтожаем, не заполнив тайник (ревью Kimi п.2, 8).
        if (o != null && o.Island != Island)
        {
            List<Vector2> list;
            if (_emptiedCaches.TryGetValue(o.Island, out list) && list.RemoveAll(v => Near(v, o.Pos)) > 0)
            {
                CoopMod.Log("server: piece " + piece.ItemID + " returns to its cache on " + o.Island + " at (" +
                            o.Pos.x.ToString("0.0") + ", " + o.Pos.y.ToString("0.0") + ")");
                UnityEngine.Object.Destroy(piece.gameObject);
                return true;
            }
            CoopMod.Warn("server: piece " + piece.ItemID + ": its cache on " + o.Island + " is not empty, using another one");
        }
        GameObject cache = null;
        if (o != null && o.Island == Island)
        {
            cache = FindFullCache(o.Pos);
            if (cache != null && !EmptyHere(cache)) cache = null;
        }
        if (cache == null)
            foreach (var kv in NM.CoopFullCaches)
                if (kv.Value != null && EmptyHere(kv.Value)) { cache = kv.Value; break; }
        if (cache != null)
        {
            var cinv = cache.GetComponent<C_Inventory>();
            NM.CoopTelepieceToCache[piece.ItemID] = cinv.InventoryID;
            CoopMod.Log("server: piece " + piece.ItemID + " returns to a cache on " + Island + " at " + V(cache.transform.position) +
                        (o == null ? " (origin unknown)" : ""));
            return false;   // дальше — игра (ReturnTelepieceToCache)
        }
        // на этом острове пустых нет — пустой тайник на другом острове
        foreach (var kv in _emptiedCaches)
        {
            if (kv.Key == Island || kv.Value.Count == 0) continue;
            Vector2 p = kv.Value[0];
            kv.Value.RemoveAt(0);
            CoopMod.Log("server: piece " + piece.ItemID + " refills an empty cache on " + kv.Key + " at (" + p.x.ToString("0.0") + ", " + p.y.ToString("0.0") + ")");
            UnityEngine.Object.Destroy(piece.gameObject);
            return true;
        }
        CoopMod.Warn("server: piece " + piece.ItemID + " is extra: no empty cache anywhere, removed");
        UnityEngine.Object.Destroy(piece.gameObject);
        return true;
    }

    // тайник на этой карте опустошён (его можно заполнить игрой)
    private bool EmptyHere(GameObject full)
    {
        if (full.activeSelf) return false;
        var inv = full.GetComponent<C_Inventory>();
        return inv != null && NM.CoopEmptyCaches.ContainsKey(inv.InventoryID) && NM.CoopFullCaches.ContainsKey(inv.InventoryID);
    }

    private GameObject FindFullCache(Vector2 pos)
    {
        var holder = C_ProgressManager.m_Instance != null ? C_ProgressManager.m_Instance.m_pTelePeiceHolder : null;
        for (int i = 0; holder != null && i < holder.childCount; i++)
        {
            var c = holder.GetChild(i);
            if (c.name == "P_TelePeiceHolderFull" && Near(new Vector2(c.position.x, c.position.z), pos)) return c.gameObject;
        }
        return null;
    }

    private static bool Near(Vector2 a, Vector2 b)
    {
        return Mathf.Abs(a.x - b.x) < 0.5f && Mathf.Abs(a.y - b.y) < 0.5f;
    }

    // клиент сообщил, что у него после прибытия (автотест)
    [RPC]
    private void CoopReportPlayer(string island, string print, float health, float vitality, string slotsBefore, string slotsAfter, NetworkMessageInfo info)
    {
        if (!Network.isServer) return;
        var rec = Record(info.sender);
        if (rec == null || rec.Components == null) return;
        string name = Name(rec), key = Key(rec);
        PlayerSnap s;
        if (!_restoredSnaps.TryGetValue(key, out s)) return;   // не после перехода
        _restoredSnaps.Remove(key);
        if (!CoopMod.AutoTest) return;
        var c = rec.Components;
        string serverPrint = InvPrint(c.Inventory);
        Check(print == s.Print, name + " items on client after travel match the snapshot" + (print == s.Print ? "" : ": snapshot " + s.Print + " | client " + CoopMod.Clean(print, 400)));
        float h = c.CharacterHealth._currentHealth, v = c.CharacterVitality._currentVitality;
        float drop = c.CharacterVitality.VitalityLostPerDrop + 0.01f;
        Check(Mathf.Abs(h - s.Health) < 0.5f && Mathf.Abs(v - s.Vitality) <= drop,
              name + " health/vitality after travel kept: server " + h.ToString("0.00") + "/" + v.ToString("0.00") +
              ", snapshot " + s.Health.ToString("0.00") + "/" + s.Vitality.ToString("0.00"));
        Check(Mathf.Abs(health - h) < 1f && Mathf.Abs(vitality - v) <= drop,
              name + " client health/vitality match the server: client " + health.ToString("0.00") + "/" + vitality.ToString("0.00"));
        Check(slotsBefore == slotsAfter, name + " quick slots kept: before " + CoopMod.Clean(slotsBefore, 120) + ", after " + CoopMod.Clean(slotsAfter, 120));
        CoopMod.Log("server: " + name + " report: server print " + serverPrint);
    }

    // ---------------- клиент ----------------
    private string[] _clientSlots;          // слоты перед отплытием

    private void ClientRememberSlots()
    {
        var l = ClientPlayerManager.LocalInstance;
        if (l == null || l.Components == null || l.Components.EquipmentController == null) { _clientSlots = null; return; }
        _clientSlots = (string[])l.Components.EquipmentController.m_SlotItems.Clone();
        CoopMod.Log("client: remembered quick slots " + string.Join(",", _clientSlots));
    }

    // после спавна (ClientSpawnWatch): вернуть слоты как были. Не фильтруем по
    // инвентарю: в слотах бывают не имена предметов (Stone → ThrowStones,
    // C_InventoryItemButton.Equip), а игра и сама держит слот израсходованного предмета.
    private string ClientRestoreSlots(out string before)
    {
        before = "";
        var l = ClientPlayerManager.LocalInstance;
        if (_clientSlots == null || l == null) return "";
        var eq = l.Components.EquipmentController;
        var want = _clientSlots;
        _clientSlots = null;
        for (int i = 0; i < want.Length && i < eq.m_SlotItems.Length; i++) eq.m_SlotItems[i] = want[i];
        before = string.Join(",", want);
        string after = string.Join(",", eq.m_SlotItems);
        CoopMod.Log("client: quick slots restored " + after);
        return after;
    }

    // автотест: назначить первый подходящий предмет в слот (как кнопка в инвентаре)
    private void BotAssignSlot()
    {
        var l = ClientPlayerManager.LocalInstance;
        if (l == null) return;
        var eq = l.Components.EquipmentController;
        // в слотах — имена вида ThrowStones (C_InventoryItemButton.Equip)
        var it = l.Components.Inventory.Items.FirstOrDefault(i => i != null && i.ItemName.StartsWith("Throw", StringComparison.Ordinal) && !eq.m_SlotItems.Contains(i.ItemName))
              ?? l.Components.Inventory.Items.FirstOrDefault(i => i != null && i.IsEquippable && !eq.m_SlotItems.Contains(i.ItemName));
        if (it == null) { CoopMod.Log("client: bot: nothing to put in a slot (" + InvPrint(l.Components.Inventory) + ")"); return; }
        int slot = eq.m_SlotItems.Length - 1;
        eq.AssignNewSlotItem(it.ItemName, slot);
        CoopMod.Log("client: bot: slot " + slot + " = " + it.ItemName + " -> " + string.Join(",", eq.m_SlotItems));
    }
}
