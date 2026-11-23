// Кампания: команды "#coop" в чате и автотест.
//
// "#coop" / "#coop status" — всем: где кампания.
// Только если сервер запущен с -coopdebug:
//   #coop tp <North|South|East|West>  — к лодке с этим направлением на острове
//   #coop sail <остров>               — переход без лодки и согласия
//   #coop bleed | starve | heal       — себе: кровотечение / голод / снять всё
//   #coop pieces <N>                  — счёт сданных фрагментов (проверить победу)
// Автотест (-coopautotest у сервера): сценарий этапа, итог — строки
// "[SYABH-COOP] TEST PASS|FAIL ..." и "TEST DONE fail=N".
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class CoopCampaign
{
    public void ServerChatCommand(NetworkPlayer sender, string message)
    {
        string[] a = message.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string cmd = a.Length > 1 ? a[1].ToLowerInvariant() : "status";
        string arg = a.Length > 2 ? a[2] : null;
        var rec = Record(sender);
        CoopMod.Log("server: chat command from " + (rec != null ? Name(rec) : sender.ToString()) + ": " + CoopMod.Clean(message, 80));
        if (cmd == "status")
        {
            Tell(sender, "Campaign: island " + Island + ", from " + (ArrivalFrom ?? "-") + ", travels " + TravelCount +
                         ", generated " + string.Join(",", _generated.ToArray()));
            return;
        }
        if (cmd == "class" && rec != null) { ServerClassCommand(rec, arg); return; }
        if (cmd == "newcampaign" && rec != null) { ServerNewCampaign(rec); return; }
        if (!CoopMod.DebugMode) { Tell(sender, "Debug commands need the server started with -coopdebug"); return; }
        if (rec == null || !rec.IsSpawned) return;
        switch (cmd)
        {
            case "tp":
                var b = _boats.FirstOrDefault(x => arg == null || string.Equals(x.Direction, arg, StringComparison.OrdinalIgnoreCase));
                if (b == null) { Tell(sender, "No such boat here"); return; }
                Teleport(rec, b.SpawnPos, b.SpawnRot);
                break;
            case "sail":
                string target = Islands.FirstOrDefault(x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
                if (target == null || target == Island || Transitioning) { Tell(sender, "Can't sail there"); return; }
                ServerTravel(target);
                break;
            case "pieces":
                int n;
                if (arg == null || !int.TryParse(arg, out n) || n < 0 || n >= PiecesTotal) { Tell(sender, "Usage: #coop pieces <0.." + (PiecesTotal - 1) + ">"); return; }
                PiecesReturned = n;
                ApplyPieceCounts();
                TellAll("Pieces returned set to " + n + " of " + PiecesTotal);
                break;
            case "bleed": rec.Components.BleedMonitor.StartBleeding(); break;
            case "starve": rec.Components.CharacterVitality.SetVitality(0f); break;
            case "heal":
                rec.Components.BleedMonitor.StopBleeding();
                rec.Components.CharacterVitality.Reset();
                break;
            default: Tell(sender, "Unknown #coop command"); break;
        }
    }

    public void Teleport(NetworkPlayerRecord rec, Vector3 pos, Vector3 rot)
    {
        rec.Components.Body.position = pos;
        rec.Components.Body.eulerAngles = rot;
        rec.ClientPlayerManager.networkView.RPC("SendSpawnPosition", rec.NetworkPlayer, pos, rot);
        CoopMod.Log("server: teleported " + Name(rec) + " to " + V(pos));
    }

    // ---------- клиент: команды бота от автотеста ----------
    [RPC]
    private void CoopBotDo(string what, NetworkMessageInfo info)
    {
        if (!FromServer(info) || !(CoopMod.AutoTest || CoopMod.Bot)) return;
        CoopMod.Log("client: bot do " + CoopMod.Clean(what));
        if (what == "propose") ClientPropose();
        else if (what == "answer-yes") _botAnswer = true;
        else if (what == "answer-no") _botAnswer = false;
        else if (what == "slot") BotAssignSlot();
        else if (what == "revive") _botReviveUntil = Time.realtimeSinceStartup + 12f;   // «держит G» до 12 с
        else if (what == "sink-bodies") StartCoroutine(BotSinkBodies());
        else if (what == "giveup") _botGiveUp = true;                                 // при следующей смерти — респаун
        else if (what == "quit") { CoopMod.Log("client: bot quits the game"); Application.Quit(); }
        else if (what == "respawn" && ClientPlayerManager.LocalInstance != null) ClientPlayerManager.LocalInstance.SendRespawnRequest();
    }

    // клиент: заметить свой спавн и записать, где оказались
    private ClientPlayerManager _seenLocal;
    private float _spawnedAt = -1f;

    private void ClientSpawnWatch()
    {
        var l = ClientPlayerManager.LocalInstance;
        if (l == null) { _seenLocal = null; _spawnedAt = -1f; return; }
        if (l != _seenLocal) { _seenLocal = l; _spawnedAt = Time.realtimeSinceStartup; ApplyClientProgress(); return; }
        if (_spawnedAt > 0f && Time.realtimeSinceStartup - _spawnedAt > 4f)
        {
            _spawnedAt = -1f;
            float d;
            Vector3 p = l.Components.Body.position;
            Boat b = NearestBoat(p, out d);
            CoopMod.Log("TEST client: spawned on " + ClientIsland + " at " + V(p) + ", nearest boat " +
                        (b != null ? b.Direction + " " + d.ToString("0.0") + " m" : "none") + ", boats " + _boats.Count);
            int n;
            string dg = IdDigest(out n);
            CoopMod.Log("client: island ids digest " + dg + " (" + n + " inventories)");
            networkView.RPC("CoopReportDigest", RPCMode.Server, ClientIsland, n, dg);
            ApplyClientProgress();
            var pd = C_ProgressManager.m_Instance != null ? C_ProgressManager.m_Instance.m_Data : null;
            networkView.RPC("CoopReportHud", RPCMode.Server, C_TelePeicesDisplay.CurrentReturnedCount, C_TelePeicesDisplay.CurrentRemainingCount,
                pd != null ? pd.m_NumReturned : -1, pd != null ? pd.m_MaxNumTelePeices : -1);
            string before, after = ClientRestoreSlots(out before);
            networkView.RPC("CoopReportPlayer", RPCMode.Server, ClientIsland, InvPrint(l.Components.Inventory),
                l.Components.CharacterHealth._currentHealth, l.Components.CharacterVitality._currentVitality, before, after);
        }
    }

    // ---------- сервер: автотест этапа 1 ----------
    private bool _autoStarted;
    private int _pass, _fail;

    private void ServerAutoTestTick()
    {
        if (!CoopMod.AutoTest || _autoStarted || NetworkManager.Instance == null) return;
        if (Transitioning || Spawned().Count() < NetworkManager.Instance.MinimumPlayers) return;
        _autoStarted = true;
        StartCoroutine(AutoTest());
    }

    private void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        CoopMod.Log("TEST " + (ok ? "PASS" : "FAIL") + ": " + what);
    }

    private IEnumerator WaitArrived(int travel, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        while (Time.realtimeSinceStartup < until)
        {
            if (TravelCount == travel && !Transitioning &&
                NM.CoopRecords.Count() > 0 && NM.CoopRecords.All(r => r.IsSpawned && r.Components != null))
                break;
            yield return new WaitForSeconds(1f);
        }
        yield return new WaitForSeconds(8f);
    }

    private IEnumerator AutoTest()
    {
        yield return new WaitForSeconds(10f);
        var players = Spawned().OrderBy(r => int.Parse(r.NetworkPlayer.ToString())).ToList();
        var A = players[0];
        var B = players.Count > 1 ? players[1] : null;
        CoopMod.Log("TEST start: island " + Island + ", players " + players.Count + ", boats " + _boats.Count);
        Check(Island == "Centre" && _boats.Count == 4, "campaign starts on Centre with 4 boats (" + _boats.Count + ")");
        // этап 4: стартовый набор — класса каждого игрока
        foreach (var r in players) CheckKit(r);

        // 1. не у лодки → отказ
        Boat north = _boats.FirstOrDefault(x => x.Direction == "North");
        if (north == null) { Check(false, "Centre has a North boat"); yield break; }
        Teleport(A, north.SpawnPos + north.Facing * 60f + Vector3.up * 30f, north.SpawnRot);
        yield return new WaitForSeconds(3f);
        int before = TravelCount;
        ServerPropose(A.NetworkPlayer);
        Check(_pending == null && TravelCount == before, "proposal away from a boat is rejected");

        // 2. у лодки, но напарник кровоточит → отказ
        Teleport(A, north.SpawnPos, north.SpawnRot);
        yield return new WaitForSeconds(3f);
        if (B != null)
        {
            B.Components.BleedMonitor.StartBleeding();
            ServerPropose(A.NetworkPlayer);
            Check(_pending == null && TravelCount == before, "proposal is rejected while the partner bleeds");
            B.Components.BleedMonitor.StopBleeding();
            B.Components.CharacterHealth.Reset();

            // 3. напарник отказывается
            networkView.RPC("CoopBotDo", B.NetworkPlayer, "answer-no");
            yield return new WaitForSeconds(1f);
            ServerPropose(A.NetworkPlayer);
            Check(_pending != null, "proposal at the boat is pending for the partner");
            yield return new WaitForSeconds(5f);
            Check(_pending == null && TravelCount == before, "partner refusal cancels the proposal");
            networkView.RPC("CoopBotDo", B.NetworkPlayer, "answer-yes");
            yield return new WaitForSeconds(1f);
        }

        // 4. пометить состояние Centre: пустой сундук и опустевший тайник
        var invs = C_SaveManager.m_Instance.m_pIslandInventories;
        int k = invs.FindIndex(i => i != null && i.Items.Count > 0);
        string lootName = k >= 0 ? invs[k].m_InventoryName : null;
        if (k >= 0) invs[k].ClearInventory();
        Vector3 cachePos = Vector3.zero; bool cacheMarked = false;
        var holder = C_ProgressManager.m_Instance.m_pTelePeiceHolder;
        for (int i = 0; holder != null && i < holder.childCount; i++)
        {
            var c = holder.GetChild(i);
            var inv = c.GetComponent<C_Inventory>();
            if (c.name == "P_TelePeiceHolderFull" && c.gameObject.activeSelf && inv != null && inv.Items.Count > 0)
            {
                cachePos = c.position;
                int pieceId = inv.Items[0].ItemID;
                inv.ClearInventory();
                NetworkManager.Instance.EmptyTelepieceCache(inv.InventoryID, pieceId);
                cacheMarked = true;
                break;
            }
        }
        CoopMod.Log("TEST marked on Centre: loot #" + k + " '" + lootName + "' emptied, cache at " + V(cachePos) + " emptied: " + cacheMarked);

        // 4б. этап 2: состояние игроков перед отплытием
        var cA = A.Components;
        cA.CharacterHealth.SetHealth(cA.CharacterHealth.MaximumHealth * 0.55f);
        cA.CharacterVitality.SetVitality(Mathf.Min(cA.CharacterVitality._currentVitality * 0.6f, 45f) + 0.37f);
        var ammo = cA.Inventory.Items.FirstOrDefault(i => i != null && (i.GetComponent<C_AmmoInClip>() != null || i.GetComponent<C_AmmoOnGun>() != null));
        if (ammo != null)
        {
            var ac = ammo.GetComponent<C_AmmoInClip>(); if (ac != null) ac.m_CurrentAmmo = 3;
            var ag = ammo.GetComponent<C_AmmoOnGun>(); if (ag != null) ag._currentAmmo = 1;
        }
        Vector3 piece2Pos, piece3Pos;
        bool p2 = TakePieceInto(A, cachePos, Vector3.zero, out piece2Pos);
        bool p3 = TakePieceInto(A, cachePos, piece2Pos, out piece3Pos);
        Check(p2 && p3, "A took two pieces from caches on Centre (" + V(piece2Pos) + ", " + V(piece3Pos) + ")");
        NetworkManager.Instance.SendPlayerInventoryContent(cA.PlayerGameObject);
        if (B != null)
        {
            B.Components.Inventory.ClearInventory();
            NetworkManager.Instance.SendPlayerInventoryContent(B.Components.PlayerGameObject);
        }
        networkView.RPC("CoopBotDo", A.NetworkPlayer, "slot");
        yield return new WaitForSeconds(3f);
        CoopMod.Log("TEST A before travel: " + InvPrint(cA.Inventory) + ", health " + cA.CharacterHealth._currentHealth.ToString("0.00") +
                    ", vitality " + cA.CharacterVitality._currentVitality.ToString("0.00"));

        // 5. Centre → North (первое посещение), предложение — кодом клиента A
        networkView.RPC("CoopBotDo", A.NetworkPlayer, "propose");
        yield return StartCoroutine(WaitArrived(before + 1, 400f));
        Check(Island == "North" && TravelCount == before + 1, "travel Centre -> North (" + Island + ")");
        CheckArrival("North");

        // 6. North → Centre (повторное посещение: загрузка без генерации)
        Boat back = _boats.FirstOrDefault();
        if (back == null) { Check(false, "North has a boat"); }
        else
        {
            Check(_boats.Count == 1 && back.Direction == "South", "North has one boat, South (" + _boats.Count + ", " + back.Direction + ")");
            A = Spawned().FirstOrDefault(r => r.NetworkPlayer == A.NetworkPlayer) ?? A;
            if (B != null)
            {
                B = Spawned().FirstOrDefault(r => r.NetworkPlayer == B.NetworkPlayer) ?? B;
                Check(B.Components.Inventory.Items.Count == 0, "B arrived with an empty inventory, no starting kit (" + InvPrint(B.Components.Inventory) + ")");
            }
            // фрагмент тайника 2 «возвращается в тайник» на чужом острове (как при отключении)
            var inv2 = A.Components.Inventory;
            var piece2 = inv2.Items.FirstOrDefault(i => IsPiece(i) && _pieceOrigins.ContainsKey(i.ItemID) && Near(_pieceOrigins[i.ItemID].Pos, new Vector2(piece2Pos.x, piece2Pos.z)));
            Check(piece2 != null, "piece from Centre cache 2 travelled with A to North");
            if (piece2 != null)
            {
                bool threw = false;
                try { NetworkManager.Instance.ReturnTelepieceToCache(piece2); inv2.RemoveItem(piece2, false); }
                catch (Exception e) { threw = true; CoopMod.Warn("TEST return piece: " + e); }
                NetworkManager.Instance.SendPlayerInventoryContent(A.Components.PlayerGameObject);
                List<Vector2> cl;
                bool stillMarked = _emptiedCaches.TryGetValue("Centre", out cl) && cl.Any(v => Near(v, new Vector2(piece2Pos.x, piece2Pos.z)));
                Check(!threw && !stillMarked, "returning a Centre piece on North does not throw and unmarks the Centre cache");
            }
            Teleport(A, back.SpawnPos, back.SpawnRot);
            yield return new WaitForSeconds(3f);
            networkView.RPC("CoopBotDo", A.NetworkPlayer, "propose");
            yield return StartCoroutine(WaitArrived(before + 2, 400f));
            Check(Island == "Centre" && TravelCount == before + 2, "travel North -> Centre (" + Island + ")");
            CheckArrival("Centre");
            invs = C_SaveManager.m_Instance.m_pIslandInventories;
            if (k >= 0)
                Check(k < invs.Count && invs[k].m_InventoryName == lootName && invs[k].Items.Count == 0,
                      "emptied loot on Centre is still empty after return ('" + (k < invs.Count ? invs[k].m_InventoryName : "?") + "', items " + (k < invs.Count ? invs[k].Items.Count : -1) + ")");
            if (cacheMarked)
            {
                bool emptied = NM.CoopFullCaches.Values.Any(g => g != null && !g.activeSelf && (g.transform.position - cachePos).sqrMagnitude < 0.25f);
                Check(emptied, "emptied piece cache on Centre is still empty after return");
            }
            var c2 = FindFullCache(new Vector2(piece2Pos.x, piece2Pos.z));
            Check(c2 != null && c2.activeSelf && c2.GetComponent<C_Inventory>().Items.Count > 0,
                  "cache 2 on Centre is full again (its piece was returned on North)");
            // фрагмент тайника 3 вернулся с игроком на родной остров: возврат — в тот же тайник
            A = Spawned().FirstOrDefault(r => r.NetworkPlayer == A.NetworkPlayer) ?? A;
            int had = A.Components.Inventory.Items.Count(IsPiece);
            bool threw3 = false;
            try { NetworkManager.Instance.ReturnTelepiecesInInventory(A.Components.Inventory); }
            catch (Exception e) { threw3 = true; CoopMod.Warn("TEST return piece 3: " + e); }
            NetworkManager.Instance.SendPlayerInventoryContent(A.Components.PlayerGameObject);
            var c3 = FindFullCache(new Vector2(piece3Pos.x, piece3Pos.z));
            Check(!threw3 && had == 1 && c3 != null && c3.activeSelf && c3.GetComponent<C_Inventory>().Items.Count(IsPiece) == 1,
                  "piece 3 carried back to Centre returns to its own cache (pieces with A: " + had + ")");
        }

        // 7. Centre → East (ещё одно первое посещение, другая лодка)
        Boat east = _boats.FirstOrDefault(x => x.Direction == "East");
        if (east != null)
        {
            A = Spawned().FirstOrDefault(r => r.NetworkPlayer == A.NetworkPlayer) ?? A;
            Teleport(A, east.SpawnPos, east.SpawnRot);
            yield return new WaitForSeconds(3f);
            networkView.RPC("CoopBotDo", A.NetworkPlayer, "propose");
            yield return StartCoroutine(WaitArrived(before + 3, 400f));
            Check(Island == "East" && TravelCount == before + 3, "travel Centre -> East (" + Island + ")");
            CheckArrival("East");
        }
        else Check(false, "Centre has an East boat");

        // 8. этап 3: раскладка, общий счёт через переход, сдача в круг, общая победа
        foreach (var isl in new[] { "Centre", "North", "East" })
        {
            int n;
            Check(_islandPieceCount.TryGetValue(isl, out n) && n == PiecesPerIsland, isl + " got " + PiecesPerIsland + " piece caches (" + (_islandPieceCount.ContainsKey(isl) ? _islandPieceCount[isl] : -1) + ")");
        }
        Check(CentreCircleCacheDist >= 0f && CentreCircleCacheDist < 20f, "one Centre cache is by the stone circle (" + CentreCircleCacheDist.ToString("0.0") + " m)");
        Check(PiecesTotal == 25, "campaign threshold is 25 (" + PiecesTotal + ")");
        // лут как в одиночной: все таблицы, по которым заполняли, — одиночные, предупреждений нет
        int used = 0, sp = 0;
        foreach (C_LootTable t in Resources.FindObjectsOfTypeAll(typeof(C_LootTable)))
            if (CoopLoot.WasApplied(t)) { used++; if (CoopLoot.MatchesSinglePlayer(t)) sp++; }
        Check(CoopLoot.Applied >= 10 && CoopLoot.Failed == 0 && used == CoopLoot.Applied && sp == used,
              "loot tables are the single-player ones (" + sp + " of " + used + " used, failed " + CoopLoot.Failed + ")");
        PiecesReturned = 3;
        ApplyPieceCounts();
        // 9. этап 5 на East: смерть, подъём, «сдаться», окно подъёма, фрагмент в трупе
        Vector3 corpsePiecePos = Vector3.zero;
        if (B != null)
        {
            var dt = DeathTest(A.NetworkPlayer, B.NetworkPlayer);
            yield return StartCoroutine(dt);
            corpsePiecePos = _corpsePiecePos;
            yield return StartCoroutine(RobotBodyTest(B.NetworkPlayer, corpsePiecePos));
        }
        Boat home = _boats.FirstOrDefault();
        if (home != null)
        {
            A = Spawned().FirstOrDefault(r => r.NetworkPlayer == A.NetworkPlayer) ?? A;
            Teleport(A, home.SpawnPos, home.SpawnRot);
            yield return new WaitForSeconds(3f);
            networkView.RPC("CoopBotDo", A.NetworkPlayer, "propose");
            yield return StartCoroutine(WaitArrived(before + 4, 400f));
            Check(Island == "Centre" && TravelCount == before + 4, "travel East -> Centre (" + Island + ")");
            Check(PiecesReturned == 3 && NetworkManager.Instance.NumberOfPiecesToWin == 25, "piece count survives travel (" + PiecesReturned + ", to win " + NetworkManager.Instance.NumberOfPiecesToWin + ")");
            Check(NetworkManager.Instance.StonesAreActive && C_TeleportPeiceEater.m_Instance != null, "stones are active on Centre after arrival");
            if (B != null)
            {
                List<Vector2> el;
                bool stillEmpty = _emptiedCaches.TryGetValue("East", out el) && el.Any(v => Near(v, new Vector2(corpsePiecePos.x, corpsePiecePos.z)));
                Check(corpsePiecePos != Vector3.zero && !stillEmpty, "piece left in B's corpse went back to its East cache on departure");
                bool robotStillEmpty = _emptiedCaches.TryGetValue("East", out el) && el.Any(v => Near(v, new Vector2(_robotPiecePos.x, _robotPiecePos.z)));
                Check(_robotPiecePos != Vector3.zero && !robotStillEmpty, "piece left in a robot's body went back to its East cache on departure");
            }
            A = Spawned().FirstOrDefault(r => r.NetworkPlayer == A.NetworkPlayer) ?? A;
            NetworkManager.Instance.LogReturnedPiece(A.NetworkPlayer);
            Check(PiecesReturned == 4 && C_ProgressManager.m_Instance.m_Data.m_NumReturned == 4 && C_ProgressManager.m_Instance.m_Data.m_MaxNumTelePeices == 25,
                  "returning a piece counts for the campaign (" + PiecesReturned + ", circle " + C_ProgressManager.m_Instance.m_Data.m_NumReturned + "/" + C_ProgressManager.m_Instance.m_Data.m_MaxNumTelePeices + ")");
            yield return new WaitForSeconds(2f);
            if (B != null) yield return StartCoroutine(RollbackTest(A.NetworkPlayer, B.NetworkPlayer));
            A = Rec(A.NetworkPlayer);
            PiecesReturned = PiecesTotal - 1;
            ApplyPieceCounts();
            yield return new WaitForSeconds(1f);
            int travels = TravelCount;
            CoopMod.Log("TEST victory: returning the last piece");
            NetworkManager.Instance.LogReturnedPiece(A.NetworkPlayer);
            Check(_templates.Count == 0 && PiecesReturned == 0, "after the victory the next game is a new campaign (templates " + _templates.Count + ")");
            yield return new WaitForSeconds(5f);
        }
        else Check(false, "East has a boat");

        CoopMod.Log("TEST DONE pass=" + _pass + " fail=" + _fail);
    }

    private NetworkPlayerRecord Rec(NetworkPlayer p)
    {
        return Spawned().FirstOrDefault(r => r.NetworkPlayer == p) ?? Record(p);
    }

    private IEnumerator WaitUntil(Func<bool> cond, float timeout)
    {
        float until = Time.realtimeSinceStartup + timeout;
        while (!cond() && Time.realtimeSinceStartup < until) yield return new WaitForSeconds(0.5f);
    }

    private static int Carried(C_Inventory inv) { return inv.Items.Count(i => i != null && i.LayoutHeight > 0); }

    private Vector3 _corpsePiecePos;
    private Vector3 _robotPiecePos;

    // тело робота с фрагментом: при отплытии фрагмент — в тайник, тело чистится
    // до разгрузки сцены (без NullReferenceException в RagdollWithInventoryBehaviour.OnDestroy)
    private IEnumerator RobotBodyTest(NetworkPlayer pb, Vector3 exclude)
    {
        _robotPiecePos = Vector3.zero;
        var B = Rec(pb);
        var bot = UnityEngine.Object.FindObjectsOfType(typeof(C_BotAICore)).Cast<C_BotAICore>()
            .Select(b => b.GetComponentInChildren<C_CharacterHealth>()).Where(h => h != null && h._currentHealth > 0f)
            .OrderBy(h => (h.transform.position - B.Components.Body.position).sqrMagnitude).FirstOrDefault();
        if (bot == null) { Check(false, "a robot to kill on " + Island); yield break; }
        var before = new HashSet<RagdollWithInventoryBehaviour>(UnityEngine.Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)).Cast<RagdollWithInventoryBehaviour>());
        CoopMod.Log("TEST killing robot " + bot.gameObject.name);
        typeof(C_CharacterHealth).GetMethod("AddHealth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(bot, new object[] { -(bot._currentHealth + 1000f), 0, null });
        yield return new WaitForSeconds(3f);
        var body = UnityEngine.Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)).Cast<RagdollWithInventoryBehaviour>()
            .FirstOrDefault(r => !before.Contains(r) && r.Inventory != null);
        Check(body != null, "the robot left a body with an inventory");
        if (body == null) yield break;
        Vector3 pos;
        B = Rec(pb);
        if (!TakePieceInto(B, exclude, Vector3.zero, out pos)) { Check(false, "B took a piece for the robot body"); yield break; }
        var piece = B.Components.Inventory.Items.Where(IsPiece).LastOrDefault();
        B.Components.Inventory.RemoveItem(piece, false);
        bool put = body.Inventory.AddItem(piece);
        if (!put) { B.Components.Inventory.AddItem(piece); Check(false, "the piece fits into the robot body"); yield break; }
        NetworkManager.Instance.SendPlayerInventoryContent(B.Components.PlayerGameObject);
        _robotPiecePos = pos;
        Check(true, "a piece lies in the robot's body (" + body.gameObject.name + ")");
        // тело под землёй: сервер и клиент B поднимают его сами (CoopBodies.cs)
        int lifted = BodiesLifted;
        _bodyReport = null;
        SinkBody(body);
        networkView.RPC("CoopBotDo", pb, "sink-bodies");
        yield return new WaitForSeconds(3f);
        float depth = BodyDepth(body);
        Check(BodiesLifted > lifted && depth <= BodySinkTolerance,
              "server lifted the sunken robot body (depth now " + depth.ToString("0.00") + " m, lifts " + (BodiesLifted - lifted) + ")");
        yield return StartCoroutine(WaitUntil(() => _bodyReport != null, 15f));
        Check(_bodyReport != null && _bodyReport[0] > 0 && _bodyReport[1] == _bodyReport[0],
              "client B lifted the bodies sunk on its side (" + (_bodyReport != null ? _bodyReport[1] + " of " + _bodyReport[0] : "no report") + ")");
    }

    private int[] _bodyReport;

    [RPC]
    private void CoopReportBodies(int sunk, int ok, NetworkMessageInfo info)
    {
        if (!Network.isServer || !CoopMod.AutoTest) return;
        CoopMod.Log("server: client reports bodies sunk " + sunk + ", lifted " + ok);
        _bodyReport = new[] { sunk, ok };
    }

    // клиент-бот: утопить тела рядом, через 3 с доложить, сколько поднято
    private IEnumerator BotSinkBodies()
    {
        var l = ClientPlayerManager.LocalInstance;
        var sunk = new List<RagdollWithInventoryBehaviour>();
        if (l != null)
            foreach (RagdollWithInventoryBehaviour rb in UnityEngine.Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)))
                if (rb != null && Vector3.Distance(rb.transform.position, l.Components.Body.position) < 100f && !float.IsNaN(BodyDepth(rb)))
                {
                    SinkBody(rb);
                    sunk.Add(rb);
                }
        yield return new WaitForSeconds(3f);
        int ok = sunk.Count(rb => rb != null && BodyDepth(rb) <= BodySinkTolerance);
        CoopMod.Log("client: bodies sunk " + sunk.Count + ", above the ground again " + ok);
        networkView.RPC("CoopReportBodies", RPCMode.Server, sunk.Count, ok);
    }

    // этап 5 (docs/stages/stage5.md, п.1-5)
    // смерть как от урона в игре: AddHealth → Detonate → снять с учёта целей
    // (SetHealth(0) этот путь обходит, C_CharacterHealth.cs:115-156)
    private static void KillByDamage(NetworkPlayerRecord r)
    {
        var h = r.Components.CharacterHealth;
        float before = h._currentHealth;
        bool ok = h.Damage(before + 100f, 1, null);
        CoopMod.Log("test: Damage(" + (before + 100f).ToString("0") + ", faction 1) -> " + ok + ", health " + before.ToString("0") + " -> " +
                    h._currentHealth.ToString("0") + ", IncomingDamageFactor " + h.IncomingDamageFactor + ", enabled " + h.enabled);
        if (h._currentHealth > 0f)
            typeof(C_CharacterHealth).GetMethod("AddHealth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(h, new object[] { -(h._currentHealth + 100f), 1, null });
    }

    private IEnumerator DeathTest(NetworkPlayer pa, NetworkPlayer pb)
    {
        var A = Rec(pa); var B = Rec(pb);
        CoopMod.Log("TEST stage 5: death and revive on " + Island);
        Vector3 pPos;
        bool took = TakePieceInto(B, Vector3.zero, Vector3.zero, out pPos);
        NetworkManager.Instance.SendPlayerInventoryContent(B.Components.PlayerGameObject);
        Teleport(B, GroundAt(A.Components.Body.position + new Vector3(3f, 0f, 0f)) + Vector3.up, Vector3.zero);
        yield return new WaitForSeconds(3f);

        // 1. смерть: вещи в трупе, труп «вечный», отплыть нельзя
        B = Rec(pb);
        int carried = Carried(B.Components.Inventory);
        KillByDamage(B);
        yield return new WaitForSeconds(3f);
        var c1 = _corpses.LastOrDefault(c => c.Owner == pb);
        var rb = c1 != null && c1.Obj != null ? c1.Obj.GetComponent<RagdollWithInventoryBehaviour>() : null;
        int inCorpse = rb != null && rb.Inventory != null ? rb.Inventory.Items.Count : -1;
        Check(took && IsDead(Rec(pb)) && c1 != null && c1.Revivable && rb != null && rb.DespawnTimeInSeconds > 1e7f &&
              inCorpse == carried && rb.Inventory.Items.Count(IsPiece) == 1,
              "B died: corpse holds his " + carried + " items with the piece and does not despawn (corpse items " + inCorpse + ")");
        string blk = TravelBlocker();
        Check(blk != null && blk.Contains("dead"), "no sailing while B is dead (" + blk + ")");

        // 2. подъём: A у трупа «держит G»
        if (c1 == null || c1.Obj == null) yield break;
        Vector3 cpos = c1.Obj.transform.position;
        Teleport(Rec(pa), GroundAt(cpos + new Vector3(1f, 0f, 0f)) + Vector3.up, Vector3.zero);
        yield return new WaitForSeconds(2f);
        networkView.RPC("CoopBotDo", pa, "revive");
        yield return StartCoroutine(WaitUntil(() => !IsDead(Rec(pb)), 20f));
        yield return new WaitForSeconds(2f);
        B = Rec(pb);
        var hB = B.Components.CharacterHealth;
        Check(!IsDead(B) && hB._currentHealth >= hB.MaximumHealth * 0.5f - 0.5f && hB._currentHealth <= hB.MaximumHealth * 0.5f + 8f /* сытый лечится сам (C_CharacterVitality.cs:74) */ && Carried(B.Components.Inventory) == carried &&
              B.Components.Inventory.Items.Count(IsPiece) == 1 && c1.Obj == null && !_corpses.Contains(c1) && !B.Components.BleedMonitor.m_IsBleeding,
              "A revived B: half health (" + hB._currentHealth.ToString("0") + "), all " + Carried(B.Components.Inventory) + " items back, corpse gone");
        Check(Vector3.Distance(B.Components.Body.position, cpos) < 4f, "B stands up where he fell (" + Vector3.Distance(B.Components.Body.position, cpos).ToString("0.0") + " m)");
        Check(B.Components.Character.IsRegistered, "revived B is a robot target again");

        // 3. «сдаться»: респаун у лодки с набором класса, труп остаётся с вещами
        networkView.RPC("CoopBotDo", pb, "giveup");
        yield return new WaitForSeconds(1f);
        carried = Carried(B.Components.Inventory);
        KillByDamage(B);
        yield return new WaitForSeconds(2f);
        // после подъёма вторая смерть снимает тело с учёта (иначе роботы целятся в труп; ревью Qwen п.3)
        Check(IsDead(Rec(pb)) && !Rec(pb).Components.Character.IsRegistered, "second death after a revive: the body is no longer a robot target");
        yield return StartCoroutine(WaitUntil(() => !IsDead(Rec(pb)), 25f));
        yield return new WaitForSeconds(3f);
        B = Rec(pb);
        var c2 = _corpses.LastOrDefault(c => c.Owner == pb);
        var rb2 = c2 != null && c2.Obj != null ? c2.Obj.GetComponent<RagdollWithInventoryBehaviour>() : null;
        Check(!IsDead(B) && c2 != null && c2 != c1 && rb2 != null && !c2.Revivable && rb2.Inventory.Items.Count == carried && rb2.Inventory.Items.Count(IsPiece) == 1,
              "B gave up: respawned, corpse stays with his " + carried + " items and the piece, no revive (corpse items " + (rb2 != null ? rb2.Inventory.Items.Count : -1) + ")");
        Boat arr = ArrivalBoat();
        float d = arr == null ? -1f : Vector2.Distance(new Vector2(B.Components.Body.position.x, B.Components.Body.position.z), new Vector2(arr.BoatPos.x, arr.BoatPos.z));
        Check(arr != null && d < 10f, "B respawned at the arrival boat (" + d.ToString("0.0") + " m)");
        CheckKit(B);

        // повторный запрос респауна от живого — игнор
        string pr = InvPrint(B.Components.Inventory);
        networkView.RPC("CoopBotDo", pb, "respawn");
        yield return new WaitForSeconds(3f);
        Check(InvPrint(Rec(pb).Components.Inventory) == pr, "respawn request from a living player is ignored");

        // 4. окно подъёма вышло — респаун сам
        B = Rec(pb);
        B.Components.CharacterHealth.SetHealth(0f);
        yield return new WaitForSeconds(2f);
        Check(IsDead(Rec(pb)), "B is dead again");
        yield return StartCoroutine(WaitUntil(() => !IsDead(Rec(pb)), ReviveSeconds + 20f));
        yield return new WaitForSeconds(2f);
        var c3 = _corpses.LastOrDefault(c => c.Owner == pb);
        Check(!IsDead(Rec(pb)) && c3 != null && c3 != c2 && c3.Obj != null && !c3.Revivable, "revive window (" + ReviveSeconds + " s) over: B respawned at the boat by himself");
        _corpsePiecePos = pPos;
        Check(TravelBlocker() == null, "sailing allowed again (" + TravelBlocker() + ")");
    }

    // этап 5, п.6: погибли все — откат к прибытию
    private IEnumerator RollbackTest(NetworkPlayer pa, NetworkPlayer pb)
    {
        var A = Rec(pa); var B = Rec(pb);
        CoopMod.Log("TEST stage 5: everyone dies on " + Island);
        PlayerSnap snapA;
        _arrivalSnaps.TryGetValue(Key(A), out snapA);
        int deathsA = A.PlayerStats.Deaths, deathsB = B.PlayerStats.Deaths, returned = PiecesReturned - 1;
        Vector3 xPos;
        bool took = TakePieceInto(A, Vector3.zero, Vector3.zero, out xPos);
        NetworkManager.Instance.SendPlayerInventoryContent(A.Components.PlayerGameObject);
        yield return new WaitForSeconds(1f);
        int rb0 = RollbackCount, travels = TravelCount;
        A.Components.CharacterHealth.SetHealth(0f);
        B.Components.CharacterHealth.SetHealth(0f);
        yield return StartCoroutine(WaitUntil(() => RollbackCount > rb0, 30f));
        Check(RollbackCount == rb0 + 1, "everyone dead: rollback started (" + RollbackCount + ")");
        yield return StartCoroutine(WaitArrived(travels, 400f));
        A = Rec(pa); B = Rec(pb);
        var cx = FindFullCache(new Vector2(xPos.x, xPos.z));
        Check(took && cx != null && cx.activeSelf && cx.GetComponent<C_Inventory>().Items.Count(IsPiece) == 1, "after rollback the cache A emptied is full again");
        Check(PiecesReturned == returned && C_ProgressManager.m_Instance.m_Data.m_NumReturned == returned, "after rollback the piece count is back to the arrival (" + PiecesReturned + ", want " + returned + ")");
        Check(snapA != null && InvPrint(A.Components.Inventory) == snapA.Print, "after rollback A has the arrival items");
        Check(!IsDead(A) && !IsDead(B) && A.PlayerStats.Deaths == deathsA + 1 && B.PlayerStats.Deaths == deathsB + 1,
              "after rollback both alive, deaths are kept (A " + A.PlayerStats.Deaths + ", B " + B.PlayerStats.Deaths + ")");
        CheckArrival(Island + " (rollback)");
        Check(TravelCount == travels && Island == "Centre", "rollback is not a travel (" + TravelCount + ", " + Island + ")");
    }

    private void CheckArrival(string island)
    {
        Boat arr = ArrivalBoat();
        foreach (var r in Spawned())
        {
            float d = arr == null ? -1f : Vector2.Distance(new Vector2(r.Components.Body.position.x, r.Components.Body.position.z), new Vector2(arr.BoatPos.x, arr.BoatPos.z));
            Check(arr != null && d < 10f, Name(r) + " on " + island + " spawned at the arrival boat " + (arr != null ? arr.Direction : "?") + " (" + d.ToString("0.0") + " m)");
        }
    }

    // автотест: переложить фрагмент из полного тайника (не в exclude1/2) игроку,
    // как это делает перенос мышью (NetworkManager.cs:2054-2066)
    private bool TakePieceInto(NetworkPlayerRecord rec, Vector3 exclude1, Vector3 exclude2, out Vector3 pos)
    {
        pos = Vector3.zero;
        var holder = C_ProgressManager.m_Instance.m_pTelePeiceHolder;
        for (int i = 0; holder != null && i < holder.childCount; i++)
        {
            var c = holder.GetChild(i);
            var inv = c.GetComponent<C_Inventory>();
            if (c.name != "P_TelePeiceHolderFull" || !c.gameObject.activeSelf || inv == null || inv.Items.Count == 0) continue;
            if ((c.position - exclude1).sqrMagnitude < 0.25f || (c.position - exclude2).sqrMagnitude < 0.25f) continue;
            var item = inv.Items[0];
            if (item.ItemID <= 0) item.ItemID = NetworkStatics.GetNextItemId();
            var pinv = rec.Components.Inventory;
            // полный инвентарь (у Егеря набор большой) — выкинуть самый крупный не-фрагмент, как игрок
            while (!pinv.AddItem(item))
            {
                var big = pinv.Items.Where(x => x != null && x.LayoutHeight > 0 && !IsPiece(x)).OrderByDescending(x => x.LayoutHeight * x.LayoutWidth).FirstOrDefault();
                if (big == null) return false;
                CoopMod.Log("TEST inventory full: dropping " + big.ItemName);
                pinv.RemoveItem(big, true);
            }
            inv.RemoveItem(item, false);
            NetworkManager.Instance.EmptyTelepieceCache(inv.InventoryID, item.ItemID);
            pos = c.position;
            return true;
        }
        return false;
    }

    // этап 4: набор игрока = набор его класса (по именам предметов)
    private void CheckKit(NetworkPlayerRecord r)
    {
        int cls = ServerClassFor(r.Components.Inventory);
        var lo = C_PlayerClassLoadOuts.m_Instance;
        if (lo == null || cls < 0) { Check(false, Name(r) + " class known on the server (" + cls + ")"); return; }
        var want = lo.m_ClassLoadOuts[cls].m_Items.Select(i => i.ItemName).Distinct().OrderBy(x => x).ToArray();
        var have = r.Components.Inventory.Items.Where(i => i != null).Select(i => i.ItemName).Distinct().OrderBy(x => x).ToArray();
        Check(want.SequenceEqual(have), Name(r) + " got the kit of " + ClassName(cls) + " (" + cls + "): " + string.Join(",", have) +
              (want.SequenceEqual(have) ? "" : " — expected " + string.Join(",", want)));
    }
}
