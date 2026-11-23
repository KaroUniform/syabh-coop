// Кампания на диске сервера (этап 6): переживает перезапуск сервера и
// отключения игроков. Файл SaveGames/<сервер>/CoopCampaign.txt — строки
// «ключ значения…», строки и ключи игроков — base64 (в них пробелы).
// Острова (мир, лут) игра хранит сама в папке мира; здесь — состояние
// кампании, которого у игры нет. Решения — docs/stages/stage6.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Assets.Phunk.Extensions;

public partial class CoopCampaign
{
    public static readonly bool NewCampaignFlag = Environment.GetCommandLineArgs().Contains("-coopnewcampaign");
    public static readonly bool AutoTest6a = Environment.GetCommandLineArgs().Contains("-coopautotest6a");
    public static readonly bool AutoTest6c = Environment.GetCommandLineArgs().Contains("-coopautotest6c");
    public static readonly bool AutoTest6b = Environment.GetCommandLineArgs().Contains("-coopautotest6b");
    private const int SaveFormat = 1;
    private const float SaveEvery = 30f;
    private float _nextSave;

    private static string SaveFile()
    {
        return Path.Combine(C_SaveManager.m_Instance.m_SavePath.Root, "CoopCampaign.txt");
    }

    private static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
    private static string UnB64(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
    private static string F(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }
    private static float PF(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

    // ---------------- запись ----------------

    // midIsland — посреди острова (периодически, при отключении, при выходе):
    // текущий остров и игроков снимаем сейчас. Иначе (момент отплытия) —
    // всё уже снято: _emptiedCaches и _travelSnaps.
    public void SaveNow(bool midIsland, string why)
    {
        if (!Network.isServer || _templates.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var emptied = _emptiedCaches.ToDictionary(kv => kv.Key, kv => new List<Vector2>(kv.Value));
            var players = new Dictionary<string, PlayerSnap>(_travelSnaps);
            // в лобби мира нет (ResetWorldStateForFreshSession): Inv.xml и тайники
            // не трогаем, иначе пустой мир затрёт лут острова (выход последнего
            // игрока, закрытие сервера в лобби)
            if (midIsland && !Transitioning && !NM.CoopInLobby)
            {
                // лут текущего острова — в его <остров>Inv.xml (копия прибытия — отдельно)
                C_SaveManager.m_Instance.m_SavePath.CurrentIsland = Island;
                C_SaveManager.m_Instance.IslandInventoriesSave();
                emptied[Island] = CurrentEmptied();
                // фрагменты в трупах: трупы после перезапуска не вернутся — считаем,
                // что фрагмент снова в своём (или любом пустом) тайнике
                // (все тела: и игроков, и роботов — ReturnCorpsePieces)
                foreach (RagdollWithInventoryBehaviour rb in UnityEngine.Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)))
                {
                    if (rb == null || rb.Inventory == null) continue;
                    foreach (var it in rb.Inventory.Items.Where(IsPiece))
                    {
                        Origin o;
                        _pieceOrigins.TryGetValue(it.ItemID, out o);
                        if (!RefillSaved(emptied, o)) CoopMod.Warn("server: save: no empty cache for a corpse piece");
                    }
                }
                foreach (var r in Spawned())
                {
                    string k = Key(r);
                    if (IsDead(r)) continue;                 // вещи в трупе; при входе — стартовый набор
                    PlayerSnap a;
                    if (_restoring.Contains(k)) { if (_arrivalSnaps.TryGetValue(k, out a)) players[k] = a; continue; }
                    players[k] = TakeSnap(r);
                }
            }
            var sb = new StringBuilder();
            sb.Append("coopcampaign ").Append(SaveFormat).Append('\n');
            sb.Append("island ").Append(Island).Append('\n');
            if (ArrivalFrom != null) sb.Append("from ").Append(ArrivalFrom).Append('\n');
            sb.Append("travels ").Append(TravelCount).Append('\n');
            sb.Append("rollbacks ").Append(RollbackCount).Append('\n');
            sb.Append("returned ").Append(PiecesReturned).Append('\n');
            float played = (float)(Transitioning ? PlayedBefore : NM.CoopPlayedSeconds);
            sb.Append("played ").Append(F(played)).Append('\n');
            foreach (var kv in _templates) sb.Append("template ").Append(kv.Key).Append(' ').Append(Convert.ToBase64String(kv.Value.AsByteArray())).Append('\n');
            foreach (var g in _generated) sb.Append("generated ").Append(g).Append('\n');
            foreach (var kv in _islandPieceCount) sb.Append("count ").Append(kv.Key).Append(' ').Append(kv.Value).Append('\n');
            foreach (var kv in emptied)
                foreach (var v in kv.Value) sb.Append("emptied ").Append(kv.Key).Append(' ').Append(F(v.x)).Append(' ').Append(F(v.y)).Append('\n');
            foreach (var kv in _playerClass) sb.Append("class ").Append(B64(kv.Key)).Append(' ').Append(kv.Value).Append('\n');
            foreach (var kv in players)
            {
                var s = kv.Value;
                sb.Append("player ").Append(B64(kv.Key)).Append(' ').Append(F(s.Health)).Append(' ').Append(F(s.Vitality)).Append('\n');
                var st = s.Stats;
                if (st != null)
                    sb.Append("stats ").Append(st.Deaths).Append(' ').Append(st.PiecesReturned).Append(' ').Append(st.PiecesDropped).Append(' ')
                      .Append(st.PlayersKilled).Append(' ').Append(st.RobotsKilled).Append(' ').Append(st.ShotsFired).Append(' ')
                      .Append(st.BandagesUsed).Append(' ').Append(st.TeasDrunk).Append('\n');
                foreach (var i in s.Items)
                {
                    sb.Append("item ").Append(B64(i.Name)).Append(' ').Append(i.Value).Append(' ').Append(i.Left).Append(' ').Append(i.Top);
                    if (i.Origin != null) sb.Append(' ').Append(i.Origin.Island).Append(' ').Append(F(i.Origin.Pos.x)).Append(' ').Append(F(i.Origin.Pos.y));
                    sb.Append('\n');
                }
                sb.Append("print ").Append(B64(s.Print)).Append('\n');
            }
            sb.Append("end\n");
            // без окна, когда файла нет совсем (ревью Kimi п.7, Qwen п.15):
            // прошлый — в .bak, новый — на его место; чтение умеет брать .bak
            string f = SaveFile(), tmp = f + ".tmp", bak = f + ".bak";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(f))
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(f, bak);
            }
            File.Move(tmp, f);
            CoopMod.Log("server: campaign saved (" + why + "): " + Island + ", pieces " + PiecesReturned + ", players " + players.Count +
                        ", emptied " + emptied.Sum(kv => kv.Value.Count) + ", " + sw.ElapsedMilliseconds + " ms");
        }
        catch (Exception e) { CoopMod.Warn("server: campaign save failed: " + e); }
    }

    // опустевшие тайники текущего острова сейчас (как SaveLeavingIsland, без изменения состояния)
    private List<Vector2> CurrentEmptied()
    {
        List<Vector2> cur;
        var list = _emptiedCaches.TryGetValue(Island, out cur) ? new List<Vector2>(cur) : new List<Vector2>();
        var full = NM.CoopFullCaches;
        if (full != null)
            foreach (var kv in full)
            {
                if (kv.Value == null) continue;
                Vector3 p = kv.Value.transform.position;
                if (!kv.Value.activeSelf) { if (!ContainsPos(list, p)) list.Add(new Vector2(p.x, p.z)); }
                else list.RemoveAll(v => Mathf.Abs(v.x - p.x) < 0.5f && Mathf.Abs(v.y - p.z) < 0.5f);
            }
        return list;
    }

    // в сохраняемой копии — вернуть фрагмент: свой тайник, иначе любой пустой
    private bool RefillSaved(Dictionary<string, List<Vector2>> emptied, Origin o)
    {
        List<Vector2> l;
        if (o != null && emptied.TryGetValue(o.Island, out l) && l.RemoveAll(v => Near(v, o.Pos)) > 0) return true;
        if (emptied.TryGetValue(Island, out l) && l.Count > 0) { l.RemoveAt(0); return true; }
        foreach (var kv in emptied) if (kv.Value.Count > 0) { kv.Value.RemoveAt(0); return true; }
        return false;
    }

    public void DeleteSave(string why)
    {
        try
        {
            if (File.Exists(SaveFile())) { File.Delete(SaveFile()); CoopMod.Log("server: campaign save deleted (" + why + ")"); }
            if (File.Exists(SaveFile() + ".bak")) File.Delete(SaveFile() + ".bak");
        }
        catch (Exception e) { CoopMod.Warn("server: deleting campaign save: " + e.Message); }
    }

    // ---------------- чтение ----------------

    // true — кампания загружена (продолжаем); основной файл не читается — прошлый (.bak)
    private bool TryLoadCampaign()
    {
        if (TryLoadCampaignFrom(SaveFile())) return true;
        if (!File.Exists(SaveFile() + ".bak")) return false;
        CoopMod.Warn("server: trying the previous campaign save (.bak)");
        return TryLoadCampaignFrom(SaveFile() + ".bak");
    }

    private bool TryLoadCampaignFrom(string f)
    {
        if (!File.Exists(f)) return false;
        try
        {
            var templates = new Dictionary<string, C_IslandTemplateData>();
            var generated = new HashSet<string>();
            var counts = new Dictionary<string, int>();
            var emptied = new Dictionary<string, List<Vector2>>();
            var classes = new Dictionary<string, int>();
            var players = new Dictionary<string, PlayerSnap>();
            string island = null, from = null;
            int travels = 0, rollbacks = 0, returned = 0;
            float played = 0f;
            PlayerSnap cur = null;
            bool end = false;
            foreach (var raw in File.ReadAllLines(f))
            {
                var p = raw.Split(' ');
                if (p.Length == 0 || p[0] == "") continue;
                switch (p[0])
                {
                    case "coopcampaign": if (int.Parse(p[1]) != SaveFormat) throw new Exception("format " + p[1]); break;
                    case "island": island = p[1]; break;
                    case "from": from = p[1]; break;
                    case "travels": travels = int.Parse(p[1]); break;
                    case "rollbacks": rollbacks = int.Parse(p[1]); break;
                    case "returned": returned = int.Parse(p[1]); break;
                    case "played": played = PF(p[1]); break;
                    case "template": templates[p[1]] = Convert.FromBase64String(p[2]).ReadAs<C_IslandTemplateData>(); break;
                    case "generated": generated.Add(p[1]); break;
                    case "count": counts[p[1]] = int.Parse(p[2]); break;
                    case "emptied":
                        List<Vector2> l;
                        if (!emptied.TryGetValue(p[1], out l)) emptied[p[1]] = l = new List<Vector2>();
                        l.Add(new Vector2(PF(p[2]), PF(p[3])));
                        break;
                    case "class": classes[UnB64(p[1])] = int.Parse(p[2]); break;
                    case "player":
                        cur = new PlayerSnap { Health = PF(p[2]), Vitality = PF(p[3]) };
                        players[UnB64(p[1])] = cur;
                        break;
                    case "stats":
                        cur.Stats = new Assets.Networking.PlayerStats
                        {
                            Deaths = int.Parse(p[1]), PiecesReturned = int.Parse(p[2]), PiecesDropped = int.Parse(p[3]), PlayersKilled = int.Parse(p[4]),
                            RobotsKilled = int.Parse(p[5]), ShotsFired = int.Parse(p[6]), BandagesUsed = int.Parse(p[7]), TeasDrunk = int.Parse(p[8]),
                        };
                        break;
                    case "item":
                        var it = new ItemSnap { Name = UnB64(p[1]), Value = int.Parse(p[2]), Left = int.Parse(p[3]), Top = int.Parse(p[4]) };
                        if (p.Length >= 8) it.Origin = new Origin { Island = p[5], Pos = new Vector2(PF(p[6]), PF(p[7])) };
                        cur.Items.Add(it);
                        break;
                    case "print": cur.Print = UnB64(p[1]); break;
                    case "end": end = true; break;
                }
            }
            if (!end || island == null || !IsIsland(island) || Islands.Any(i => !templates.ContainsKey(i)))
                throw new Exception("incomplete file");
            // остров «создан», только если его файлы на диске сервера
            C_SaveManager.m_Instance.EnsureWorldFolder(true);
            foreach (var g in generated.ToList())
                if (!File.Exists(C_SaveManager.m_Instance.m_SavePath.Static + "/" + g + "Template.xml"))
                {
                    CoopMod.Warn("server: island " + g + " is not on disk any more, it will be generated again");
                    generated.Remove(g);
                }
            ResetCampaign(false);
            foreach (var kv in templates) _templates[kv.Key] = kv.Value;
            foreach (var g in generated) _generated.Add(g);
            foreach (var kv in counts) _islandPieceCount[kv.Key] = kv.Value;
            foreach (var kv in emptied) _emptiedCaches[kv.Key] = kv.Value;
            foreach (var kv in classes) if (!_playerClass.ContainsKey(kv.Key)) _playerClass[kv.Key] = kv.Value;
            foreach (var kv in players) _travelSnaps[kv.Key] = kv.Value;
            Island = island; ArrivalFrom = from; TravelCount = travels; RollbackCount = rollbacks;
            PiecesReturned = returned; PlayedBefore = played;
            CoopMod.Log("server: campaign loaded: " + Island + " (from " + (ArrivalFrom ?? "-") + "), travels " + TravelCount + ", pieces " + PiecesReturned +
                        ", generated " + string.Join(",", _generated.ToArray()) + ", emptied " + _emptiedCaches.Sum(kv => kv.Value.Count) +
                        ", players " + string.Join(",", _travelSnaps.Select(kv => kv.Value.Items.Count + " items").ToArray()));
            return true;
        }
        catch (Exception e)
        {
            CoopMod.Warn("server: campaign save " + f + " unreadable, starting a new campaign: " + e.Message);
            try { File.Copy(f, f + ".bad", true); } catch (Exception) { }
            return false;
        }
    }

    // ---------------- когда писать ----------------

    private void ServerSaveTick()
    {
        if (_templates.Count == 0 || Transitioning || NM.CoopInLobby || !Spawned().Any()) return;
        if (Time.realtimeSinceStartup < _nextSave) return;
        _nextSave = Time.realtimeSinceStartup + SaveEvery;
        SaveNow(true, "periodic");
    }

    private void OnApplicationQuit()
    {
        if (Network.isServer) SaveNow(true, "quit");
    }

    // из патча NetworkManager.OnPlayerDisconnected, до игры: вещи — в снимок,
    // фрагменты не возвращаем в тайники (они в снимке, игрок вернётся с ними)
    public void ServerBeforeDisconnect(NetworkPlayer p)
    {
        var rec = Record(p);
        if (rec == null || Transitioning || NM.CoopInLobby) return;
        // его трупы больше не поднять: вернётся он уже с новым NetworkPlayer (ревью Kimi п.4)
        foreach (var c in _corpses.Where(x => x.Owner == p && x.Revivable).ToList()) { c.Revivable = false; CorpseGone(c); }
        bool last = NM.CoopRecords.Count() <= 1;
        try { SnapLeaving(rec); }
        finally
        {
            // последний: игра в этом же кадре уйдёт в лобби и сбросит мир
            // (NetworkManager.cs:3611-3614) — фрагменты из трупов в тайники и
            // запись острова сейчас, пока мир цел (ревью Kimi п.3, Qwen п.2)
            if (last) ServerLastPlayerLeaving();
            else StartCoroutine(SaveNextFrame("disconnect"));
        }
    }

    private void ServerLastPlayerLeaving()
    {
        CoopMod.Log("server: the last player leaves " + Island + " — saving the island before the lobby");
        try
        {
            ReturnCorpsePieces();
            C_SaveManager.m_Instance.m_SavePath.CurrentIsland = Island;
            C_SaveManager.m_Instance.IslandInventoriesSave();
            _emptiedCaches[Island] = CurrentEmptied();
        }
        catch (Exception e) { CoopMod.Warn("server: saving the island on the last leave: " + e); }
        ClearCorpsesBeforeTeardown();   // лобби уничтожит трупы (см. ClearCorpsesBeforeTeardown)
        SaveNow(false, "last player left");
    }

    private void SnapLeaving(NetworkPlayerRecord rec)
    {
        if (!rec.IsSpawned || rec.Components == null || rec.Components.Inventory == null) return;
        string k = Key(rec);
        if (IsDead(rec) || _restoring.Contains(k)) return;   // мёртв: вещи в трупе; восстановление: снимок ещё у нас
        var s = TakeSnap(rec);
        _travelSnaps[k] = s;
        var inv = rec.Components.Inventory;
        int pieces = 0;
        foreach (var it in inv.Items.Where(IsPiece).ToList()) { inv.RemoveItem(it, true); pieces++; }
        CoopMod.Log("server: " + Name(rec) + " left: " + s.Items.Count + " items kept for the return (pieces " + pieces + ")");
    }

    private IEnumerator SaveNextFrame(string why)
    {
        yield return null;
        SaveNow(true, why);
    }

    // ---- копия лута прибытия для отката (Inv.xml теперь перезаписывается посреди острова) ----
    private static string InvFile(string island, string suffix)
    {
        return C_SaveManager.m_Instance.m_SavePath.Dynamic + "/" + island + "Inv" + suffix + ".xml";
    }

    private void BackupArrivalLoot()
    {
        try { File.Copy(InvFile(Island, ""), InvFile(Island, ".arrival"), true); }
        catch (Exception e) { CoopMod.Warn("server: arrival loot copy failed: " + e.Message); }
    }

    private void RestoreArrivalLoot()
    {
        try { if (File.Exists(InvFile(Island, ".arrival"))) File.Copy(InvFile(Island, ".arrival"), InvFile(Island, ""), true); }
        catch (Exception e) { CoopMod.Warn("server: arrival loot restore failed: " + e.Message); }
    }

    // чат "#coop newcampaign": начать заново
    private void ServerNewCampaign(NetworkPlayerRecord by)
    {
        if (Transitioning) { Tell(by.NetworkPlayer, "Wait until the travel is over"); return; }
        if (by.AdminStatus == PlayerAdminStatus.NonAdmin && NM.AllPlayers.Any(r => r.AdminStatus != PlayerAdminStatus.NonAdmin))
        {
            Tell(by.NetworkPlayer, "Only the server admin can start a new campaign");
            return;
        }
        TellAll(Name(by) + " started a new campaign");
        CoopMod.Log("server: NEW CAMPAIGN by " + Name(by));
        foreach (var r in NM.CoopRecords)
        {
            NM.CoopEndTrade(r);
            networkView.RPC("CoopTravelBegin", r.NetworkPlayer, "Centre");
        }
        ClearCorpsesBeforeTeardown();
        NM.CoopTearDown();
        ResetCampaign(true);
        ServerStartCampaign();
    }
}
