// Автотест этапа 6 — два запуска сервера на одной копии игры:
//   -coopautotest6a: Centre (A берёт фрагмент) → North, B берёт фрагмент и
//                    выходит из игры, сервер пишет кампанию и выходит;
//   -coopautotest6c: последний игрок уходит с острова, у погибшего в трупе
//                    фрагмент: фрагмент — в тайник, лут острова и тайники
//                    записаны до лобби, лобби и выход их не затирают;
//   -coopautotest6b: кампания продолжается — проверить остров, счёт, вещи
//                    обоих (по ключу установки), тайники; North → Centre.
// Ожидания 6a передаёт 6b файлом CoopTest6.txt в папке сохранений сервера.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public partial class CoopCampaign
{
    private bool _auto6Started;

    private void ServerAutoTest6Tick()
    {
        if ((!AutoTest6a && !AutoTest6b && !AutoTest6c) || _auto6Started || NetworkManager.Instance == null) return;
        if (Transitioning || Spawned().Count() < NetworkManager.Instance.MinimumPlayers) return;
        _auto6Started = true;
        StartCoroutine(AutoTest6a ? Test6a() : AutoTest6c ? Test6c() : Test6b());
    }

    private static string Test6File() { return Path.Combine(C_SaveManager.m_Instance.m_SavePath.Root, "CoopTest6.txt"); }

    private static string V2(Vector3 p) { return p.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " " + p.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

    private IEnumerator Test6a()
    {
        yield return new WaitForSeconds(10f);
        var players = Spawned().OrderBy(r => int.Parse(r.NetworkPlayer.ToString())).ToList();
        if (players.Count < 2) { Check(false, "6a needs two players"); yield break; }
        NetworkPlayer pa = players[0].NetworkPlayer, pb = players[1].NetworkPlayer;
        string keyA = Key(players[0]), keyB = Key(players[1]);
        CoopMod.Log("TEST 6a start on " + Island + ", keys " + keyA + " / " + keyB);
        Check(keyA != keyB && !keyA.StartsWith("name:") && !keyB.StartsWith("name:"), "players have distinct install keys");
        Vector3 pA;
        Check(TakePieceInto(Rec(pa), Vector3.zero, Vector3.zero, out pA), "A took a piece on Centre");
        NetworkManager.Instance.SendPlayerInventoryContent(Rec(pa).Components.PlayerGameObject);
        Boat north = _boats.FirstOrDefault(x => x.Direction == "North");
        if (north == null) { Check(false, "Centre has a North boat"); yield break; }
        Teleport(Rec(pa), north.SpawnPos, north.SpawnRot);
        yield return new WaitForSeconds(3f);
        networkView.RPC("CoopBotDo", pa, "propose");
        yield return StartCoroutine(WaitArrived(1, 400f));
        Check(Island == "North", "travel Centre -> North (" + Island + ")");
        Vector3 pB;
        Check(TakePieceInto(Rec(pb), Vector3.zero, Vector3.zero, out pB), "B took a piece on North");
        NetworkManager.Instance.SendPlayerInventoryContent(Rec(pb).Components.PlayerGameObject);
        Rec(pa).Components.CharacterHealth.SetHealth(63f);
        PiecesReturned = 2;
        ApplyPieceCounts();
        yield return new WaitForSeconds(2f);
        string printB = InvPrint(Rec(pb).Components.Inventory);
        // B выходит из игры: вещи — в снимок, фрагмент не возвращается в тайник
        networkView.RPC("CoopBotDo", pb, "quit");
        yield return StartCoroutine(WaitUntil(() => Record(pb) == null, 30f));
        yield return new WaitForSeconds(3f);
        PlayerSnap sb;
        Check(_travelSnaps.TryGetValue(keyB, out sb) && sb.Print == printB && sb.Items.Count(i => i.Origin != null && i.Origin.Island == "North") == 1,
              "B left: his items (with the North piece) are kept for his return" + (sb != null && sb.Print != printB ? ": " + sb.Print + " vs " + printB : ""));
        var cb = FindFullCache(new Vector2(pB.x, pB.z));
        Check(cb != null && !cb.activeSelf, "B's piece did not go back to its cache on disconnect");
        string printA = InvPrint(Rec(pa).Components.Inventory);
        File.WriteAllLines(Test6File(), new[]
        {
            "keyA " + B64(keyA), "keyB " + B64(keyB), "printA " + B64(printA), "printB " + B64(printB),
            "cacheA " + V2(pA), "cacheB " + V2(pB),
        });
        CoopMod.Log("TEST 6a saving and quitting: A " + printA + " | B " + printB);
        CoopMod.Log("TEST DONE pass=" + _pass + " fail=" + _fail);
        yield return new WaitForSeconds(1f);
        Application.Quit();   // OnApplicationQuit пишет кампанию
    }

    private IEnumerator Test6b()
    {
        yield return new WaitForSeconds(10f);
        var exp = new Dictionary<string, string>();
        try { foreach (var l in File.ReadAllLines(Test6File())) { int i = l.IndexOf(' '); exp[l.Substring(0, i)] = l.Substring(i + 1); } }
        catch (Exception e) { Check(false, "6b expectations file: " + e.Message); yield break; }
        string keyA = UnB64(exp["keyA"]), keyB = UnB64(exp["keyB"]);
        Func<string, Vector2> pos = k => { var p = exp[k].Split(' '); return new Vector2(PF(p[0]), PF(p[1])); };
        CoopMod.Log("TEST 6b start on " + Island);
        Check(Island == "North" && ArrivalFrom == "Centre" && TravelCount == 1, "campaign continues on North after a server restart (" + Island + ", from " + ArrivalFrom + ")");
        Check(PiecesReturned == 2 && NetworkManager.Instance.NumberOfPiecesToWin == 25, "piece count survives the restart (" + PiecesReturned + ")");
        Check(_generated.Contains("Centre") && _generated.Contains("North") && _generated.Count == 2, "Centre and North are loaded from disk, not generated (" + string.Join(",", _generated.ToArray()) + ")");
        var A = Spawned().FirstOrDefault(r => Key(r) == keyA);
        var B = Spawned().FirstOrDefault(r => Key(r) == keyB);
        Check(A != null && B != null, "both players recognised by their install keys");
        if (A == null || B == null) yield break;
        Check(InvPrint(A.Components.Inventory) == UnB64(exp["printA"]), "A has his items after the restart: " + InvPrint(A.Components.Inventory));
        // 63 в 6a; при сытости ≥ 50 игра сама подлечивает (~0.5/с, C_CharacterVitality.RestoreHealth),
        // поэтому «не сброшено в 100», а не ровно 63
        float hA = A.Components.CharacterHealth._currentHealth;
        Check(hA >= 62f && hA < 80f, "A's health survives the restart (63 -> " + hA.ToString("0") + ", natural regen)");
        Check(InvPrint(B.Components.Inventory) == UnB64(exp["printB"]), "B (left before the restart) has his items with the piece: " + InvPrint(B.Components.Inventory));
        var bp = B.Components.Inventory.Items.FirstOrDefault(IsPiece);
        Check(bp != null && _pieceOrigins.ContainsKey(bp.ItemID) && _pieceOrigins[bp.ItemID].Island == "North", "B's piece still knows its North cache");
        var cb = FindFullCache(pos("cacheB"));
        Check(cb != null && !cb.activeSelf, "B's North cache is still empty after the restart");
        CheckArrival("North");
        Boat back = _boats.FirstOrDefault();
        if (back != null)
        {
            Teleport(A, back.SpawnPos, back.SpawnRot);
            yield return new WaitForSeconds(3f);
            networkView.RPC("CoopBotDo", A.NetworkPlayer, "propose");
            yield return StartCoroutine(WaitArrived(2, 400f));
            Check(Island == "Centre" && TravelCount == 2, "travel North -> Centre after the restart (" + Island + ")");
            var ca = FindFullCache(pos("cacheA"));
            Check(ca != null && !ca.activeSelf, "Centre cache A emptied before the restart is still empty");
            Check(NetworkManager.Instance.StonesAreActive && C_TeleportPeiceEater.m_Instance != null, "stone circle works on Centre after the restart");
        }
        CoopMod.Log("TEST DONE pass=" + _pass + " fail=" + _fail);
    }

    private IEnumerator Test6c()
    {
        yield return new WaitForSeconds(10f);
        var players = Spawned().OrderBy(r => int.Parse(r.NetworkPlayer.ToString())).ToList();
        if (players.Count < 2) { Check(false, "6c needs two players"); yield break; }
        NetworkPlayer pa = players[0].NetworkPlayer, pb = players[1].NetworkPlayer;
        string keyB = Key(players[1]);
        CoopMod.Log("TEST 6c start on " + Island);
        Vector3 pA, pB;
        Check(TakePieceInto(Rec(pa), Vector3.zero, Vector3.zero, out pA), "A took a piece");
        NetworkManager.Instance.SendPlayerInventoryContent(Rec(pa).Components.PlayerGameObject);
        Check(TakePieceInto(Rec(pb), Vector3.zero, Vector3.zero, out pB), "B took a piece");
        NetworkManager.Instance.SendPlayerInventoryContent(Rec(pb).Components.PlayerGameObject);
        yield return new WaitForSeconds(2f);
        Rec(pa).Components.CharacterHealth.SetHealth(0f);
        yield return new WaitForSeconds(3f);
        var corpse = _corpses.LastOrDefault(c => c.Owner == pa);
        Check(corpse != null && IsDead(Rec(pa)), "A died, his piece is in the corpse");
        string inv = InvFile(Island, "");
        // 1) уходит B (живой, с фрагментом), 2) уходит A (мёртвый, последний)
        networkView.RPC("CoopBotDo", pb, "quit");
        yield return StartCoroutine(WaitUntil(() => Record(pb) == null, 30f));
        yield return new WaitForSeconds(1f);   // до отката (6 с) — уходит и A
        networkView.RPC("CoopBotDo", pa, "quit");
        yield return StartCoroutine(WaitUntil(() => NM.CoopInLobby, 30f));
        yield return new WaitForSeconds(3f);
        Check(NM.CoopInLobby, "the server went to the lobby after the last player left");
        List<Vector2> em;
        _emptiedCaches.TryGetValue(Island, out em);
        em = em ?? new List<Vector2>();
        Check(!ContainsPos(em, pA), "A's piece from the corpse went back to its cache");
        Check(ContainsPos(em, pB), "B's cache stays empty (B has the piece)");
        PlayerSnap sb;
        Check(_travelSnaps.TryGetValue(keyB, out sb) && sb.Items.Count(i => i.Origin != null) == 1, "B's items with the piece are kept");
        var saved = File.Exists(SaveFile()) ? File.ReadAllLines(SaveFile()) : new string[0];
        Check(saved.Contains("end") && saved.Count(l => l.StartsWith("emptied " + Island + " ")) == em.Count,
              "campaign file written before the lobby (emptied on " + Island + ": " + saved.Count(l => l.StartsWith("emptied " + Island + " ")) + " vs " + em.Count + ")");
        long size1 = File.Exists(inv) ? new FileInfo(inv).Length : -1;
        DateTime t1 = File.Exists(inv) ? File.GetLastWriteTimeUtc(inv) : DateTime.MinValue;
        SaveNow(true, "test: save in the lobby");
        long size2 = File.Exists(inv) ? new FileInfo(inv).Length : -1;
        Check(size1 > 200 && size1 == size2 && File.GetLastWriteTimeUtc(inv) == t1, "a save in the lobby does not overwrite the island loot (" + size1 + " / " + size2 + " bytes)");
        CoopMod.Log("TEST DONE pass=" + _pass + " fail=" + _fail);
    }
}
