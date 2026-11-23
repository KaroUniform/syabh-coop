// Кампания: смерть, подъём напарника, респаун, откат (этап 5).
//
// Сервер: смерть игры (C_DeathMonitor.OnServerHealthChanged →
// ReplacePlayerWithRagdoll) — патч после неё регистрирует труп: делает его
// «вечным» (до ухода с острова), открывает окно подъёма (ReviveSeconds) и
// рассылает клиентам. Подъём — RPC CoopRevive от живого напарника: тело встаёт
// на месте трупа, вещи из трупа возвращаются, половина здоровья (как
// RequestRespawn игры, но без лодки, сброса инвентаря и бодрости — исследование
// Kimi п.34-38). Окно вышло — сервер велит клиенту погибшего нажать респаун.
// Все погибли — откат острова к состоянию прибытия (Rollback).
// Решения — docs/stages/stage5.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public partial class CoopCampaign
{
    public static readonly float ReviveSeconds = CoopMod.ArgInt("-coopreviveseconds", 60);
    public const float ReviveHold = 3f;
    public const float ReviveDistClient = 2.5f, ReviveDistServer = 6f;

    // ---------------- сервер ----------------
    private class Corpse
    {
        public NetworkPlayer Owner;
        public string Key, Name;
        public GameObject Obj;
        public int InvId;
        public float DiedAt;
        public bool Revivable;
    }
    private readonly List<Corpse> _corpses = new List<Corpse>();
    private bool _rollbackPending;

    private static bool IsDead(NetworkPlayerRecord r)
    {
        var c = r.Components;
        return c != null && ((c.DeathMonitor != null && c.DeathMonitor.m_StartDeath) || (c.CharacterHealth != null && c.CharacterHealth.IsDead));
    }

    // из патча ReplacePlayerWithRagdoll: труп игрока создан
    public void ServerPlayerDied(NetworkPlayerRecord rec, GameObject ragdoll)
    {
        var rb = ragdoll.GetComponent<RagdollWithInventoryBehaviour>();
        if (rb != null) rb.DespawnTimeInSeconds = 1e8f;   // до ухода с острова (корутина ещё не стартовала: Start — со следующего кадра)
        foreach (var old in _corpses.Where(x => x.Key == Key(rec) && x.Revivable)) old.Revivable = false;
        var c = new Corpse
        {
            Owner = rec.NetworkPlayer, Key = Key(rec), Name = Name(rec), Obj = ragdoll,
            InvId = rb != null && rb.Inventory != null ? rb.Inventory.InventoryID : 0,
            DiedAt = Time.realtimeSinceStartup, Revivable = true,
        };
        _corpses.Add(c);
        CoopMod.Log("server: " + c.Name + " died at " + V(ragdoll.transform.position) + ", corpse inventory " + c.InvId +
                    " (" + (rb != null && rb.Inventory != null ? rb.Inventory.Items.Count : -1) + " items), revive window " + ReviveSeconds + " s");
        ClearPending("someone died");
        foreach (var r in NM.CoopRecords)
        {
            if (r.NetworkPlayer == rec.NetworkPlayer) networkView.RPC("CoopYouDied", r.NetworkPlayer, (int)ReviveSeconds);
            else networkView.RPC("CoopCorpse", r.NetworkPlayer, c.InvId, c.Name, (int)ReviveSeconds);
        }
    }

    // каждый кадр на сервере: окна подъёма, «все погибли»
    private float _nextDeathCheck;
    private void ServerDeathUpdate()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextDeathCheck) return;
        _nextDeathCheck = now + 0.5f;
        if (_rollbackPending) return;   // решение об откате принято (ревью Qwen п.13)
        foreach (var c in _corpses)
        {
            if (!c.Revivable || now - c.DiedAt < ReviveSeconds) continue;
            c.Revivable = false;
            CorpseGone(c);
            var rec = Record(c.Owner);
            if (rec != null && IsDead(rec))
            {
                CoopMod.Log("server: revive window for " + c.Name + " is over — respawn at the boat");
                networkView.RPC("CoopForceRespawn", c.Owner);
            }
        }
        if (Transitioning) return;
        var sp = Spawned().ToList();
        if (sp.Count > 0 && sp.All(IsDead) && !AnyoneLoading()) StartCoroutine(Rollback());
    }

    // кто-то с модом ещё грузит остров или получает вещи — он не мёртв, откат
    // не начинаем (ревью Kimi п.1, Qwen п.7)
    private bool AnyoneLoading()
    {
        foreach (var r in NM.CoopRecords)
        {
            if (!_helloFrom.ContainsKey(r.NetworkPlayer)) continue;
            if (!r.IsSpawned || r.Components == null || _restoring.Contains(Key(r))) return true;
        }
        return false;
    }

    private void CorpseGone(Corpse c)
    {
        foreach (var r in NM.CoopRecords) networkView.RPC("CoopCorpseGone", r.NetworkPlayer, c.InvId);
    }

    // из патча RequestRespawn: false — не респаунить (игрок жив)
    public bool ServerBeforeRespawn(NetworkPlayer p)
    {
        var rec = Record(p);
        if (rec == null) return false;
        if (!IsDead(rec)) { CoopMod.Warn("server: respawn request from living " + Name(rec) + " ignored"); return false; }
        if (_rollbackPending)
        {
            CoopMod.Log("server: respawn of " + Name(rec) + " ignored: rollback is coming");
            Tell(p, "Everyone is down — the island restarts in a moment");
            return false;
        }
        foreach (var c in _corpses.Where(x => x.Owner == p && x.Revivable)) { c.Revivable = false; CorpseGone(c); }
        CoopMod.Log("server: " + Name(rec) + " respawns at the boat (corpse stays)");
        networkView.RPC("CoopRespawnOk", p);
        return true;
    }

    [RPC]
    private void CoopRevive(int corpseInvId, NetworkMessageInfo info)
    {
        if (!Network.isServer) return;
        var helper = Record(info.sender);
        var c = _corpses.FirstOrDefault(x => x.InvId == corpseInvId);
        string why = null;
        if (helper == null || !helper.IsSpawned || helper.Components == null || IsDead(helper)) why = "helper is not alive";
        else if (c == null || c.Obj == null) why = "no such corpse";
        else if (!c.Revivable || Time.realtimeSinceStartup - c.DiedAt > ReviveSeconds + 2f) why = "too late";
        else if (Vector3.Distance(helper.Components.Body.position, c.Obj.transform.position) > ReviveDistServer) why = "too far";
        var victim = c != null ? Record(c.Owner) : null;
        if (why == null && (victim == null || !victim.IsSpawned || victim.Components == null || !IsDead(victim))) why = "victim is not dead";
        if (why != null)
        {
            CoopMod.Log("server: revive of corpse " + corpseInvId + " by " + (helper != null ? Name(helper) : "?") + " rejected: " + why);
            if (helper != null) Tell(helper.NetworkPlayer, "Can't revive: " + why);
            return;
        }
        try { ServerRevive(victim, c, helper); }
        catch (Exception e) { CoopMod.Warn("server: revive failed: " + e); }
    }

    private void ServerRevive(NetworkPlayerRecord victim, Corpse corpse, NetworkPlayerRecord helper)
    {
        var comp = victim.Components;
        var cpm = victim.ClientPlayerManager;
        Vector3 pos = RevivePos(corpse.Obj.transform.position, helper.Components.Body.position);
        Vector3 rot = new Vector3(0f, comp.Body.eulerAngles.y, 0f);
        // как RequestRespawn (ClientPlayerManager.cs:556-590), но на месте трупа,
        // со своими вещами и половиной здоровья; сначала жизнь > 0 и без
        // кровотечения, потом сброс смерти (Kimi п.36)
        comp.Body.position = pos;
        comp.Body.eulerAngles = rot;
        cpm.networkView.RPC("SendSpawnPosition", victim.NetworkPlayer, pos, rot);
        comp.BleedMonitor.StopBleeding();
        // Reset снимает _detonated: без него вторая смерть не вызывает Detonate и
        // тело остаётся целью роботов (C_CharacterHealth.cs:51-54, 149-156; ревью Qwen п.3)
        comp.CharacterHealth.Reset();
        comp.CharacterHealth.SetHealth(comp.CharacterHealth.MaximumHealth * 0.5f);
        // умер от голода — без еды умер бы снова (C_CharacterVitality.cs:78-80; Qwen п.4)
        var vit = comp.CharacterVitality;
        if (vit._currentVitality < vit.MaximumVitality * 0.3f) vit._currentVitality = vit.MaximumVitality * 0.3f;
        comp.TrappedMonitor.OnReleased(0f);
        cpm.networkView.RPC("ForceSyncHealthVitality", victim.NetworkPlayer, comp.CharacterHealth._currentHealth, comp.CharacterVitality._currentVitality);
        comp.DeathMonitor.Reset();
        cpm.networkView.RPC("UnDie", victim.NetworkPlayer);
        NetworkManager.Instance.SetPlayerUntrapped(comp.PlayerGameObject);
        comp.Character.Register();
        // вещи: из трупа обратно, удаляем из трупа только поместившиеся (Kimi п.24, 37)
        var rb = corpse.Obj.GetComponent<RagdollWithInventoryBehaviour>();
        int moved = 0, left = 0;
        if (rb != null && rb.Inventory != null)
            foreach (var it in rb.Inventory.Items.ToArray())
            {
                if (it == null) continue;
                if (comp.Inventory.TryAddItem(it, it.InventoryPositionLeft, it.InventoryPositionTop) || comp.Inventory.AddItem(it))
                {
                    rb.Inventory.RemoveItem(it, false);
                    moved++;
                }
                else left++;
            }
        NetworkManager.Instance.SendPlayerInventoryContent(comp.PlayerGameObject);
        victim.RemotePlayerManager.SetEnabledOnServer(pos);
        victim.CurrentlyOpenedInventoryId = 0;
        NetworkManager.Instance.SetTradeState(victim, false);
        victim.NetworkTimeOfLastSpawn = Network.time;
        corpse.Revivable = false;
        CorpseGone(corpse);
        if (left == 0) DestroyCorpse(corpse);
        networkView.RPC("CoopRevived", victim.NetworkPlayer, Name(helper));
        TellAll(Name(helper) + " revived " + corpse.Name);
        CoopMod.Log("server: " + Name(helper) + " revived " + corpse.Name + " at " + V(pos) + ": items back " + moved + ", left in corpse " + left +
                    ", health " + comp.CharacterHealth._currentHealth.ToString("0"));
    }

    private void DestroyCorpse(Corpse c)
    {
        _corpses.Remove(c);
        if (c.Obj == null) return;
        foreach (var r in NM.CoopRecords) if (r.CurrentlyOpenedInventoryId == c.InvId) r.CurrentlyOpenedInventoryId = 0;
        Network.RemoveRPCs(c.Obj.networkView.viewID);
        Network.Destroy(c.Obj);   // OnDestroy трупа снимет регистрацию (RagdollWithInventoryBehaviour.cs:67-85)
    }

    // Место подъёма: у трупа на земле; труп в воде (ниже -10 сервер убивает
    // каждый кадр, C_UnderWaterMonitor.cs:85-87) или земля не найдена рядом —
    // рядом с поднимающим (ревью Kimi п.8, Qwen п.10, 16)
    private static Vector3 RevivePos(Vector3 corpse, Vector3 helper)
    {
        Vector3 g;
        if (TryGround(corpse, out g) && g.y > -1.5f) return g + Vector3.up * 1.0f;
        if (TryGround(helper, out g) && g.y > -1.5f) return g + Vector3.up * 1.0f;
        return helper + Vector3.up * 0.5f;
    }

    private static bool TryGround(Vector3 p, out Vector3 g)
    {
        var ray = new Ray(new Vector3(p.x, 500f, p.z), Vector3.down);
        RaycastHit hit;
        g = p;
        if (!C_WorldTerrain.RaycastTerrain(ray, out hit, 600f) || Mathf.Abs(hit.point.y - p.y) > 4f) return false;
        g = new Vector3(p.x, Mathf.Max(hit.point.y, p.y), p.z);
        return true;
    }

    private static Vector3 GroundAt(Vector3 p)
    {
        var ray = new Ray(new Vector3(p.x, 500f, p.z), Vector3.down);
        RaycastHit hit;
        if (C_WorldTerrain.RaycastTerrain(ray, out hit, 600f) && hit.point.y > p.y - 3f) return new Vector3(p.x, Mathf.Max(hit.point.y, p.y), p.z);
        return p;
    }

    // перед уходом с острова: фрагменты из трупов игроков — в их тайники
    // (и из тел роботов: туда фрагмент может положить игрок; игра возвращает их
    // только при исчезновении тела по таймеру, RagdollWithInventoryBehaviour.cs:88-94)
    private void ReturnCorpsePieces()
    {
        foreach (RagdollWithInventoryBehaviour rb in UnityEngine.Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)))
        {
            if (rb == null || rb.Inventory == null) continue;
            int n = rb.Inventory.Items.Count(IsPiece);
            if (n == 0) continue;
            var c = _corpses.FirstOrDefault(x => x.Obj == rb.gameObject);
            string whose = c != null ? c.Name + "'s corpse" : rb.gameObject.name;
            try { NetworkManager.Instance.ReturnTelepiecesInInventory(rb.Inventory); }
            catch (Exception e) { CoopMod.Warn("server: returning pieces from " + whose + ": " + e); }
            CoopMod.Log("server: " + n + " piece(s) from " + whose + " returned to caches");
        }
    }

    // Перед teardown карты: при выгрузке сцены предметы трупа уничтожаются
    // раньше, чем OnDestroy трупа чистит инвентарь, — NullReferenceException в
    // C_Inventory.ClearInventory, и инвентарь не снимается с учёта (прогон s5b).
    // Чистим сами, пока всё живо.
    // То же у тел роботов: у них тот же RagdollWithInventoryBehaviour с лутом
    // (C_HunterGear.cs:223, AI_RiderRagdoll.cs:64 и др.) — прогон s5h: робот убит
    // на East, при отплытии NullReferenceException, инвентарь тела не снят с учёта
    // (RemoveDynamicInventory после ClearInventory не вызвался). Чистим все тела.
    private void ClearCorpsesBeforeTeardown()
    {
        int n = 0;
        foreach (RagdollWithInventoryBehaviour rb in UnityEngine.Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)))
        {
            if (rb == null || rb.Inventory == null || rb.Inventory.Items.Count == 0) continue;
            try { rb.Inventory.ClearInventory(); n++; }
            catch (Exception e) { CoopMod.Warn("server: clearing a body's inventory: " + e.Message); }
        }
        if (n > 0) CoopMod.Log("server: cleared " + n + " body inventories before the map teardown");
        _corpses.Clear();
    }

    // ---- откат к прибытию ----
    private readonly Dictionary<string, PlayerSnap> _arrivalSnaps = new Dictionary<string, PlayerSnap>();
    private List<Vector2> _arrivalEmptied = new List<Vector2>();
    private int _arrivalReturned;

    // ServerBeforeBootstrap: мир острова загружен, игроков ещё нет
    private void RememberArrivalWorld()
    {
        _arrivalSnaps.Clear();
        List<Vector2> l;
        _arrivalEmptied = _emptiedCaches.TryGetValue(Island, out l) ? new List<Vector2>(l) : new List<Vector2>();
        _arrivalReturned = PiecesReturned;
        C_SaveManager.m_Instance.m_SavePath.CurrentIsland = Island;
        try { C_SaveManager.m_Instance.IslandInventoriesSave(); BackupArrivalLoot(); }   // лут на момент прибытия
        catch (Exception e) { CoopMod.Warn("server: saving arrival loot failed: " + e); }
    }

    private static PlayerSnap NoStats(PlayerSnap s)
    {
        return new PlayerSnap { Items = s.Items, Health = s.Health, Vitality = s.Vitality, Print = s.Print, Stats = null };
    }

    // тело без снимка перехода (начало кампании, поздний вход): снимок прибытия — после набора
    private IEnumerator ArrivalSnapLater(NetworkPlayer owner)
    {
        yield return new WaitForSeconds(1f);
        var rec = Record(owner);
        if (rec == null || !rec.IsSpawned || rec.Components == null) yield break;
        if (IsDead(rec)) { CoopMod.Log("server: " + Name(rec) + " died before the arrival snapshot — none taken"); yield break; }
        string k = Key(rec);
        if (!_arrivalSnaps.ContainsKey(k)) _arrivalSnaps[k] = NoStats(TakeSnap(rec));
    }

    private IEnumerator Rollback()
    {
        _rollbackPending = true;
        CoopMod.Log("server: everyone is dead on " + Island + " — rolling back to the arrival in 6 s");
        TellAll("Everyone is down... back to the moment you arrived on " + Island);
        yield return new WaitForSeconds(6f);
        if (Transitioning || !Spawned().Any() || !Spawned().All(IsDead) || AnyoneLoading()) { _rollbackPending = false; CoopMod.Log("server: rollback cancelled"); yield break; }
        if (!File.Exists(InvFile(Island, ".arrival")))
        {
            // без копии лута прибытия откат был бы частичным (ревью Kimi п.9) —
            // вместо него обычный респаун у лодки, вещи в трупах
            CoopMod.Warn("server: no arrival loot copy for " + Island + " — respawning everyone at the boat instead of a rollback");
            _rollbackPending = false;
            foreach (var c in _corpses.Where(x => x.Revivable).ToList()) { c.Revivable = false; CorpseGone(c); }
            foreach (var r in Spawned().Where(IsDead).ToList()) networkView.RPC("CoopForceRespawn", r.NetworkPlayer);
            yield break;
        }
        Transitioning = true;
        RollbackCount++;
        _emptiedCaches[Island] = new List<Vector2>(_arrivalEmptied);
        PiecesReturned = _arrivalReturned;
        // вещи и здоровье — как при прибытии, статистика (смерти) — текущая:
        // teardown игры её обнуляет (NetworkManager.cs:3696), ApplySnap вернёт.
        // Снимки ушедших с острова игроков не стираем (ревью Kimi п.1, Qwen п.1):
        // у кого есть снимок прибытия — он, у кого нет — его снимок ухода.
        foreach (var kv in _arrivalSnaps)
        {
            var a = kv.Value;
            if (a.Health <= 0f) continue;
            var r = NM.CoopRecords.FirstOrDefault(x => Key(x) == kv.Key);
            PlayerSnap old;
            _travelSnaps.TryGetValue(kv.Key, out old);
            var stats = r != null ? r.PlayerStats : (old != null ? old.Stats : null);
            _travelSnaps[kv.Key] = new PlayerSnap { Items = a.Items, Health = a.Health, Vitality = a.Vitality, Print = a.Print, Stats = stats };
        }
        CoopMod.Log("server: ROLLBACK #" + RollbackCount + " on " + Island + ": " + _travelSnaps.Count + " player snapshots, pieces " + PiecesReturned);
        foreach (var r in NM.CoopRecords)
        {
            NM.CoopEndTrade(r);
            networkView.RPC("CoopTravelBegin", r.NetworkPlayer, Island);
        }
        ClearCorpsesBeforeTeardown();
        NM.CoopTearDown();
        _rollbackPending = false;
        RestoreArrivalLoot();
        SaveNow(false, "rollback");   // согласованное состояние сразу (ревью Kimi п.7)
        NM.CoopEnterIsland(Island, _templates[Island], false);
    }
    public int RollbackCount;

    // ---------------- клиент ----------------
    private class ClientCorpse { public int InvId; public string Name; public float Expires; public Transform T; }
    private readonly List<ClientCorpse> _cCorpses = new List<ClientCorpse>();
    private float _deadUntil = -1f, _reviveHeld, _nextCorpseScan;
    private float _botReviveUntil = -1f;
    private bool _botGiveUp;

    [RPC]
    private void CoopCorpse(int invId, string name, int seconds, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _cCorpses.RemoveAll(x => x.InvId == invId);
        _cCorpses.Add(new ClientCorpse { InvId = invId, Name = CoopMod.Clean(name, 16), Expires = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 1, 600) });
        _nextCorpseScan = 0f;
        Notify(CoopMod.Clean(name, 16) + " is down! Hold [G] at the body to revive (" + seconds + " s)");
    }

    [RPC]
    private void CoopCorpseGone(int invId, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _cCorpses.RemoveAll(x => x.InvId == invId);
    }

    [RPC]
    private void CoopYouDied(int seconds, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _deadUntil = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 1, 600);
        CoopMod.Log("client: I died, revive window " + seconds + " s");
        if (_botGiveUp) StartCoroutine(BotGiveUp());
    }

    [RPC]
    private void CoopForceRespawn(NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        CoopMod.Log("client: revive window over — respawning");
        _deadUntil = -1f;
        ClientRespawn();
    }

    // сервер принял респаун (кнопка Respawn игры или наш ClientRespawn)
    [RPC]
    private void CoopRespawnOk(NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _deadUntil = -1f;
        _reviveHeld = 0f;
        _hint = null;
    }

    private void ClientRespawn()
    {
        var mr = C_MenuRespawn.m_Instance;
        if (mr != null && mr.m_pHolder != null && mr.m_pHolder.activeInHierarchy) { mr.Respawn(); return; }
        // экран смерти ещё не открылся (первые 4 с) — как кнопка Respawn
        if (ClientPlayerManager.LocalInstance != null)
        {
            var dm = ClientPlayerManager.LocalInstance.Components.DeathMonitor;
            if (dm != null) dm.Reset();   // чтобы экран смерти не открылся поверх
            ClientPlayerManager.LocalInstance.SendRespawnRequest();
            C_MenuWindowManager.ForceHideAllWindows();
        }
    }

    [RPC]
    private void CoopRevived(string by, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        CoopMod.Log("TEST client: revived by " + CoopMod.Clean(by, 16));
        _deadUntil = -1f;
        // экран смерти закрыть; RPC UnDie идёт по другому NetworkView и может
        // прийти позже — монитор смерти сбрасываем сами, иначе экран откроется
        // уже после ForceHideAllWindows
        if (ClientPlayerManager.LocalInstance != null && ClientPlayerManager.LocalInstance.Components.DeathMonitor != null)
            ClientPlayerManager.LocalInstance.Components.DeathMonitor.Reset();
        C_MenuWindowManager.ForceHideAllWindows();
        if (C_ScreenFader.m_Instance != null)
        {
            C_ScreenFader.m_Instance.SetColor(new Color(0f, 0f, 0f, 1f));
            C_ScreenFader.m_Instance.StartFade(new Color(0f, 0f, 0f, 0f), 0.5f);
        }
        Notify(CoopMod.Clean(by, 16) + " got you back on your feet!");
    }

    private void ClientDeathUpdate()
    {
        var l = ClientPlayerManager.LocalInstance;
        if (l == null) { _cCorpses.Clear(); return; }
        float now = Time.realtimeSinceStartup;
        _cCorpses.RemoveAll(x => now > x.Expires);
        if (_deadUntil > 0f)
        {
            float left = _deadUntil - now;
            if (left > 0f) _hint = "Your partner can revive you: " + Mathf.CeilToInt(left) + " s.   Respawn = give up";
            else _deadUntil = -1f;
            return;
        }
        if (_cCorpses.Count == 0 || l.Components.DeathMonitor.m_StartDeath) { _reviveHeld = 0f; return; }
        if (now >= _nextCorpseScan)
        {
            _nextCorpseScan = now + 1f;
            var all = UnityEngine.Object.FindObjectsOfType<RagdollWithInventoryBehaviour>();
            foreach (var c in _cCorpses)
                if (c.T == null)
                    foreach (var rb in all)
                        if (rb.Inventory != null && rb.Inventory.InventoryID == c.InvId) { c.T = rb.PhysicsTransform != null ? rb.PhysicsTransform : rb.transform; break; }
        }
        ClientCorpse best = null; float bd = float.MaxValue;
        Vector3 me = l.Components.Body.position;
        foreach (var c in _cCorpses)
        {
            if (c.T == null) continue;
            float d = Vector3.Distance(me, c.T.position);
            if (d < bd) { bd = d; best = c; }
        }
        if (best == null || bd > ReviveDistClient) { _reviveHeld = 0f; return; }
        bool holding = now < _botReviveUntil || (InPlay() && Input.GetKey(KeyCode.G));
        _reviveHeld = holding ? _reviveHeld + Time.deltaTime : 0f;
        _hint = _reviveHeld > 0f
            ? "Reviving " + best.Name + "... " + Mathf.Min(100, Mathf.RoundToInt(_reviveHeld / ReviveHold * 100f)) + "%"
            : "Hold [G] to revive " + best.Name + " (" + Mathf.CeilToInt(best.Expires - now) + " s)";
        if (_reviveHeld >= ReviveHold)
        {
            _reviveHeld = 0f;
            _botReviveUntil = -1f;
            CoopMod.Log("client: revive " + best.Name + " (corpse " + best.InvId + ")");
            networkView.RPC("CoopRevive", RPCMode.Server, best.InvId);
            _cCorpses.Remove(best);
        }
    }

    private IEnumerator BotGiveUp()
    {
        _botGiveUp = false;
        yield return new WaitForSeconds(6f);   // экран смерти открывается через 4 с
        CoopMod.Log("client: bot gives up");
        ClientRespawn();
    }
}
