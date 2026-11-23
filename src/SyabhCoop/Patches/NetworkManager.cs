// Патч NetworkManager.
//  - Awake: вешаем на тот же объект компонент кампании (RPC мода идут через
//    networkView этого объекта, smoke test 2026-11-23).
//  - Этап 1: имя острова через весь поток смены карты (вместо жёсткого
//    "Centre"), загрузка посещённого острова без генерации, спавн у лодки
//    прибытия, защита RPC камней на островах без каменного круга.
// Приватные члены игры объявлены здесь одноимёнными: поля MonoMod сводит с
// оригиналом сам, методы — только с [MonoModIgnore] (иначе объявление без
// тела заменило бы метод игры пустым — проверено декомпиляцией).
#pragma warning disable CS0626, CS0649, CS0169
using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Phunk.Extensions;
using MonoMod;
using SirAPI;
using UnityEngine;

class patch_NetworkManager : NetworkManager
{
    // ---- приватные члены оригинала (ссылки) ----
    private byte[] _currentIslandTemplateData;
    private Dictionary<NetworkPlayer, NetworkPlayerRecord> _recordLookup;
    private Dictionary<int, GameObject> _fullTelepieceCaches;
    private Dictionary<int, int> _telepieceIdToInventoryId;
    private Dictionary<int, GameObject> _emptyTelepieceCaches;
    private bool _isInLobby;
    private int _numberOfPiecesReturned;
    private Dictionary<int, C_birdEmitter> _birdEmitters;
    private int _lastBirdEmitterId;
    private double CurrentGameStartTime;
    private List<C_Inventory> _lootInventories;
    private List<NetworkPlayer> _waitingClients;

    [MonoModIgnore] private extern void StartIslandGenerationOnServer();
    [MonoModIgnore] private extern void ServerTearDownForNewMap(bool includeClients);
    [MonoModIgnore] private extern void SetServerStatus(StatusCode newStatus);
    [MonoModIgnore] private extern void ReplaceFullCacheWithEmptyOne(int inventoryId);
    [MonoModIgnore] private extern void EndTradeWithInventoryId(int inventoryId);
    [MonoModIgnore] private extern void MakeAllPlayersInvulnerable();
    [MonoModIgnore] private extern void SendStatsToAllPlayers();
    [MonoModIgnore] private extern void StartServerLobby();
    [MonoModIgnore] private extern C_Inventory GetInventory(int id);
    [MonoModIgnore] private extern bool RequestTargetInventoryContent(int inventoryId, NetworkMessageInfo info);

    // ---- ревью этапов 1-3 (Kimi п.5, 8, 9, 10) ----

    // Фрагмент — только в каменный круг: ручной перенос в тайник сервер
    // разрешал (клиент запрещал только пустой тайник, InventoryPanelManager.cs:422),
    // а в полном тайнике второй фрагмент пропадал при следующем изъятии.
    private extern void orig_TryMoveInventoryItem(int itemId, int sourceInventoryId, int targetInventoryId, int left, int top, NetworkMessageInfo info);
    private void TryMoveInventoryItem(int itemId, int sourceInventoryId, int targetInventoryId, int left, int top, NetworkMessageInfo info)
    {
        if (sourceInventoryId != targetInventoryId && targetInventoryId > 0)
        {
            var t = GetInventory(targetInventoryId);
            if (t != null && t.name.StartsWith("P_TelePeiceHolder", StringComparison.Ordinal))
            {
                CoopMod.Log("server: move of item " + itemId + " into piece cache " + targetInventoryId + " rejected");
                if (CoopCampaign.Instance != null) CoopCampaign.Instance.CoopTell(info.sender, "Pieces go to the stone circle, not into a cache");
                RequestPlayerInventoryContent(info);   // как при неудачном переносе (NetworkManager.cs:2081-2089)
                RequestTargetInventoryContent(targetInventoryId, info);
                return;
            }
        }
        orig_TryMoveInventoryItem(itemId, sourceInventoryId, targetInventoryId, left, top, info);
    }

    // «Выбросить» уничтожает предмет (NetworkManager.cs:2162) — фрагмент так не терять
    private extern void orig_DiscardItem(int itemId, NetworkMessageInfo info);
    private void DiscardItem(int itemId, NetworkMessageInfo info)
    {
        C_PickupItem it = null;
        NetworkPlayerRecord rec;
        if (_recordLookup.TryGetValue(info.sender, out rec) && rec.Components != null && rec.Components.Inventory != null)
            it = rec.Components.Inventory.Items.FirstOrDefault(i => i != null && i.ItemID == itemId);
        if (it == null && _lootInventories != null)
            foreach (var inv in _lootInventories)
            {
                if (inv == null) continue;
                it = inv.Items.FirstOrDefault(i => i != null && i.ItemID == itemId);
                if (it != null) break;
            }
        if (it != null && it.ItemName.StartsWith("TeleportPeice", StringComparison.Ordinal))
        {
            CoopMod.Log("server: discard of piece " + itemId + " rejected");
            if (CoopCampaign.Instance != null) CoopCampaign.Instance.CoopTell(info.sender, "A piece can't be thrown away");
            RequestPlayerInventoryContent(info);
            return;
        }
        orig_DiscardItem(itemId, info);
    }

    // отключившийся в очереди загрузки — не бутстрапить (KeyNotFoundException)
    private extern void orig_OnPlayerDisconnected(NetworkPlayer player);
    private void OnPlayerDisconnected(NetworkPlayer player)
    {
        if (CoopCampaign.Instance != null)
        {
            try { CoopCampaign.Instance.ServerBeforeDisconnect(player); }
            catch (Exception e) { CoopMod.Warn("server: disconnect snapshot failed: " + e); }
        }
        orig_OnPlayerDisconnected(player);
        if (_waitingClients != null) _waitingClients.Remove(player);
    }

    public void CoopStartLobby() { StartServerLobby(); }

    // ---- Awake ----
    private extern void orig_Awake();

    private void Awake()
    {
        orig_Awake();
        if (gameObject.GetComponent<CoopCampaign>() == null)
            gameObject.AddComponent<CoopCampaign>();
        CoopMod.Log("loaded: mod " + CoopMod.Version + ", game version string '" +
                    WatermarkUpdater.VersionNumber + "', debug=" + CoopMod.DebugMode +
                    ", autotest=" + CoopMod.AutoTest + ", bot=" + CoopMod.Bot);
    }

    // ---- этап 5: труп игрока ----
    // _playerRagdolls — словарь с приватным типом значения, берём ключи отражением
    private static readonly System.Reflection.FieldInfo PlayerRagdollsField =
        typeof(NetworkManager).GetField("_playerRagdolls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private List<GameObject> CoopPlayerRagdolls()
    {
        var l = new List<GameObject>();
        var d = PlayerRagdollsField != null ? PlayerRagdollsField.GetValue(this) as System.Collections.IDictionary : null;
        if (d != null) foreach (var k in d.Keys) { var g = k as GameObject; if (g != null) l.Add(g); }
        return l;
    }

    public extern void orig_ReplacePlayerWithRagdoll(NetworkPlayerRecord playerRecord);
    public new void ReplacePlayerWithRagdoll(NetworkPlayerRecord playerRecord)
    {
        var before = new HashSet<GameObject>(CoopPlayerRagdolls());
        orig_ReplacePlayerWithRagdoll(playerRecord);
        if (CoopCampaign.Instance == null) return;
        try
        {
            foreach (var g in CoopPlayerRagdolls())
                if (!before.Contains(g)) CoopCampaign.Instance.ServerPlayerDied(playerRecord, g);
        }
        catch (Exception e) { CoopMod.Warn("server: corpse registration failed: " + e); }
    }

    // ---- доступ для CoopCampaign ----
    public IEnumerable<NetworkPlayerRecord> CoopRecords { get { return _recordLookup != null ? _recordLookup.Values : (IEnumerable<NetworkPlayerRecord>)new NetworkPlayerRecord[0]; } }
    public bool CoopInLobby { get { return _isInLobby; } }
    public Dictionary<int, GameObject> CoopFullCaches { get { return _fullTelepieceCaches; } }
    public Dictionary<int, int> CoopTelepieceToCache { get { return _telepieceIdToInventoryId; } }
    public Dictionary<int, GameObject> CoopEmptyCaches { get { return _emptyTelepieceCaches; } }

    // teardown игры + то, что он не убирает (ревью исследования этапа 1, Qwen п.25-27):
    // буферизованные Network.Instantiate старых тел и открытые инвентари/торг
    public void CoopTearDown()
    {
        ServerTearDownForNewMap(true);
        foreach (var r in _recordLookup.Values)
        {
            Network.RemoveRPCs(r.NetworkPlayer);
            r.CurrentlyOpenedInventoryId = 0;
            r.IsOpenForTrade = false;
        }
        if (_waitingClients != null) _waitingClients.Clear();   // очередь старой карты
    }

    // Только если инвентарь игрока открыт кем-то (торг): EndTradeWithInventoryId
    // безусловно шлёт владельцу EndActiveTradeOnClient, а без открытого торга у
    // клиента это NullReferenceException (C_PlayerInventoryController.EndActiveTrade,
    // прогон s2a).
    public void CoopEndTrade(NetworkPlayerRecord r)
    {
        bool trading = false;
        foreach (var o in _recordLookup.Values)
            // торг — только если владелец открыт для обмена: после респауна игра
            // ставит CurrentlyOpenedInventoryId = -1 (ClientPlayerManager.cs:576), а
            // это совпадает с номером инвентаря игрока -1 (прогон s5b, NRE у клиента)
            if (o != r && r.IsOpenForTrade && r.PlayerInventoryId != 0 && o.CurrentlyOpenedInventoryId == r.PlayerInventoryId) trading = true;
        if (trading)
        {
            try { EndTradeWithInventoryId(r.PlayerInventoryId); }
            catch (Exception e) { CoopMod.Warn("end trade: " + e.Message); }
        }
        r.CurrentlyOpenedInventoryId = 0;
    }

    public void CoopEmptyCache(int inventoryId) { ReplaceFullCacheWithEmptyOne(inventoryId); }

    // Сервер: войти на остров кампании. generate — первое посещение (генерация
    // из шаблона, как StartIslandGenerationOnServer); иначе — загрузка того,
    // что сервер сгенерировал и сохранил раньше (Static_Content + <остров>Inv.xml).
    // Клиенты в обоих случаях получают шаблон и генерируют остров сами.
    public void CoopEnterIsland(string island, C_IslandTemplateData template, bool generate)
    {
        C_ChangeIslands.m_Instance.m_CurrentIsland = island;
        C_SaveManager.m_Instance.m_SavePath.CurrentIsland = island;
        // пути Static/Dynamic: в игре их выставлял CreateRandomIslandTemplate
        // (без этого генерация пишет меши в "Y:\Centre\..." — проверено)
        C_SaveManager.m_Instance.EnsureWorldFolder(true);
        C_SaveManager.m_Instance.IslandTemplateSaveData(island, template);
        C_ChangeIslands.m_Instance.m_IslandTemplateData = template;
        _currentIslandTemplateData = template.AsByteArray();
        _isInLobby = false;
        if (generate)
        {
            CoopMod.Log("server: island " + island + " — first visit, generating");
            StartIslandGenerationOnServer();
            return;
        }
        CoopMod.Log("server: island " + island + " — revisit, loading from disk without generation");
        SetServerStatus(StatusCode.LoadingWorld);
        ResetWorldStateForFreshSession();
        foreach (NetworkPlayer p in new List<NetworkPlayer>(_recordLookup.Keys))
            StartClientLoadingIsland(p);
        C_ChangeIslands.m_Instance.LoadIsland();
    }

    // ---- поток смены карты ----

    // Лобби пройдено / #new: кампания вместо случайного острова.
    private void CreateAndGenerateRandomIslandOnServer()
    {
        CoopCampaign.Instance.ServerStartCampaign();
    }

    // было: m_CurrentIsland = "Centre" (NetworkManager.cs:3191)
    private void GenerationCompleteOnServer(object sender, EventArgs e)
    {
        SetServerStatus(StatusCode.LoadingWorld);
        CoopCampaign.Instance.ServerGenerationComplete();
        ResetBirdEmitters();
        C_ChangeIslands.m_Instance.LoadIsland();
    }

    // Номера птичьих эмиттеров (RPC TriggerBirdEmitter) выдаются подряд всем,
    // кто зарегистрировался после ResetWorldStateForFreshSession, — и в сцене
    // генерации, и в загруженной. Сервер при повторном посещении острова не
    // генерирует, клиент генерирует всегда — номера разъезжались, у клиента
    // NullReferenceException в C_birdEmitter.EmitBirds (прогон s3a, 219 раз).
    // Сброс перед загрузкой у обеих сторон: номера — только по загруженной сцене.
    private void ResetBirdEmitters()
    {
        _birdEmitters = new Dictionary<int, C_birdEmitter>();
        _lastBirdEmitterId = 0;
    }

    private void TriggerBirdEmitter(int birdEmitterId)
    {
        C_birdEmitter e;
        if (_birdEmitters != null && _birdEmitters.TryGetValue(birdEmitterId, out e) && e != null) e.EmitBirds();
    }

    // было: m_CurrentIsland = "Centre" (NetworkManager.cs:1029)
    private void GenerationCompleteOnClient(object sender, EventArgs e)
    {
        CoopMod.Log("client: generation complete, loading island " + C_ChangeIslands.m_Instance.m_CurrentIsland);
        ResetBirdEmitters();
        C_ChangeIslands.m_Instance.LoadIsland();
    }

    // клиент: оригинал сохраняет шаблон как "Centre" и запускает генерацию
    // (сама генерация — в следующем Update); подставляем имя острова из
    // CoopIslandInfo, которое сервер шлёт перед шаблоном.
    private extern void orig_SendIslandTemplateToClient(byte[] templateData, int numPieces);
    private void SendIslandTemplateToClient(byte[] templateData, int numPieces)
    {
        orig_SendIslandTemplateToClient(templateData, numPieces);
        CoopCampaign.Instance.ClientTemplateArrived();
    }

    // сервер: перед шаблоном — имя острова (и при позднем входе тоже)
    private extern void orig_StartClientLoadingIsland(NetworkPlayer client);
    private void StartClientLoadingIsland(NetworkPlayer client)
    {
        CoopCampaign.Instance.ServerSendIslandInfo(client);
        orig_StartClientLoadingIsland(client);
    }

    private extern void orig_ResetWorldStateForFreshSession();
    private void ResetWorldStateForFreshSession()
    {
        orig_ResetWorldStateForFreshSession();
        // очередь RPC для ещё не готовых клиентов ссылается на объекты прошлой
        // карты — игра её не чистит (тип очереди приватный, поэтому через reflection)
        try
        {
            var f = typeof(NetworkManager).GetField("_rpcQueues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var q = f != null ? f.GetValue(this) as System.Collections.IDictionary : null;
            if (q != null) q.Clear();
        }
        catch (Exception e) { CoopMod.Warn("reset rpc queues: " + e.Message); }
        if (CoopCampaign.Instance != null) CoopCampaign.Instance.OnWorldReset();
    }

    // сервер: остров загружен, перед отправкой мира клиентам — применить
    // состояние кампании (опустевшие тайники и т.п.)
    private extern void orig_BootstrapWaitingClients();
    private void BootstrapWaitingClients()
    {
        try { CoopCampaign.Instance.ServerBeforeBootstrap(); }
        catch (Exception e) { CoopMod.Warn("server: before-bootstrap failed: " + e); }
        orig_BootstrapWaitingClients();
    }

    // спавн: у лодки прибытия (если кампания знает её), иначе как в игре
    private extern void orig_MoveToSafeSpawnPos(Transform target);
    private void MoveToSafeSpawnPos(Transform target)
    {
        Vector3 pos, rot;
        if (CoopCampaign.Instance != null && CoopCampaign.Instance.ServerArrivalSpawn(out pos, out rot))
        {
            target.position = pos;
            target.eulerAngles = rot;
            return;
        }
        orig_MoveToSafeSpawnPos(target);
    }

    // камни всегда активны (решение игроков); на островах без круга таймер
    // всё равно бессмыслен
    private extern void orig_ConfigureGamePlayForNewMap();
    private void ConfigureGamePlayForNewMap()
    {
        EnableStonesActivationTimer = false;
        orig_ConfigureGamePlayForNewMap();
    }

    // Камни «активны» и там, где круга нет: игра сверяет StonesAreActive с
    // C_TeleportPeiceEater.m_Instance без проверки на null при открытии ЛЮБОГО
    // инвентаря (NetworkManager.cs:1809, C_PlayerInventoryController.cs:174).
    public extern bool orig_get_StonesAreActive();
    public new bool StonesAreActive
    {
        get { return orig_get_StonesAreActive() || C_TeleportPeiceEater.m_Instance == null; }
    }

    // клиент: RPC камней обращаются к C_TeleportPeiceEater.m_Instance без
    // проверки — на острове без каменного круга его нет
    private extern void orig_SetStonesActiveOnClient(bool isActive, bool displayMessage);
    private void SetStonesActiveOnClient(bool isActive, bool displayMessage)
    {
        if (C_TeleportPeiceEater.m_Instance == null) return;
        orig_SetStonesActiveOnClient(isActive, displayMessage);
    }

    private extern void orig_ShowStonesActivationWarningOnClient(int secondsToActivation);
    private void ShowStonesActivationWarningOnClient(int secondsToActivation)
    {
        if (C_TeleportPeiceEater.m_Instance == null) return;
        orig_ShowStonesActivationWarningOnClient(secondsToActivation);
    }

    // чат: команды мода "#coop ..." (остальное — как в игре)
    private extern void orig_ProcessMessageFromClient(string message, NetworkMessageInfo info);
    private void ProcessMessageFromClient(string message, NetworkMessageInfo info)
    {
        if (message != null && message.StartsWith("#coop", StringComparison.OrdinalIgnoreCase)
            && CoopCampaign.Instance != null && Network.isServer)
        {
            CoopCampaign.Instance.ServerChatCommand(info.sender, message);
            return;
        }
        orig_ProcessMessageFromClient(message, info);
    }

    // ---- этап 2: игрок и фрагменты ----

    // серверное тело создано; игра следом выдаст стартовый набор — кампания
    // через 2 кадра заменит его снимком игрока (если он приплыл)
    public extern void orig_RegisterPlayerObjectsAndCreateRemoteBody(ClientPlayerManager client);
    public new void RegisterPlayerObjectsAndCreateRemoteBody(ClientPlayerManager client)
    {
        orig_RegisterPlayerObjectsAndCreateRemoteBody(client);
        if (CoopCampaign.Instance == null) return;
        try { CoopCampaign.Instance.ServerBodyCreated(client); }
        catch (Exception e) { CoopMod.Warn("server: body created hook failed: " + e); }
    }

    // фрагмент вынут из тайника: запомнить остров и место тайника
    public extern void orig_EmptyTelepieceCache(int inventoryId, int telepieceId);
    public new void EmptyTelepieceCache(int inventoryId, int telepieceId)
    {
        orig_EmptyTelepieceCache(inventoryId, telepieceId);
        GameObject cache;
        if (CoopCampaign.Instance != null && _fullTelepieceCaches.TryGetValue(inventoryId, out cache) && cache != null)
            CoopCampaign.Instance.ServerPieceTaken(telepieceId, cache.transform.position);
    }

    // возврат фрагмента в тайник: игра знает тайник только по ItemID этой карты
    // (KeyNotFoundException для фрагмента с другого острова) — кампания решает сама
    public extern void orig_ReturnTelepieceToCache(C_PickupItem piece);
    public new void ReturnTelepieceToCache(C_PickupItem piece)
    {
        if (CoopCampaign.Instance != null)
        {
            bool done;
            try { done = CoopCampaign.Instance.ServerReturnPiece(piece); }
            catch (Exception e) { CoopMod.Warn("server: return piece failed: " + e); done = false; }
            if (done) return;
        }
        orig_ReturnTelepieceToCache(piece);
    }

    // ---- этап 3: общий счёт фрагментов и общая победа ----

    public void CoopSetPieceCounts(int returned, int total)
    {
        _numberOfPiecesReturned = returned;
        NumberOfPiecesToWin = total;
        RpcToSpawnedPlayers(networkView, "UpdatePieceCountOnClient", returned, Math.Max(total - returned, 0));
    }

    public double CoopPlayedSeconds { get { return Network.time - CurrentGameStartTime; } }

    // было: NetworkManager.cs:2646-2660 — счёт острова, победа одного игрока
    public new void LogReturnedPiece(NetworkPlayer returningPlayer)
    {
        var camp = CoopCampaign.Instance;
        if (camp == null) { CoopMod.Warn("server: no campaign, piece not counted"); return; }
        string text = _recordLookup[returningPlayer].PlayerInfo.Name;
        Statistics.LogPieceReturned(returningPlayer);
        int returned = camp.ServerPieceReturned();   // и HUD всем
        int num = NumberOfPiecesToWin - returned;
        if (num <= 0)
        {
            camp.ServerVictory();
            MakeAllPlayersInvulnerable();
            networkView.RPC("DisplayEndSequence", RPCMode.Others, "");   // "" — общая победа
            SendStatsToAllPlayers();
            // лобби — со следующего кадра: TryMoveInventoryItem после нас ещё
            // обращается к телам игроков (NetworkManager.cs:2058-2059), а teardown
            // лобби их уже убрал бы (NullReferenceException, ревью Kimi п.9)
            camp.ServerLobbyNextFrame();
            return;
        }
        SendNotificationToClients(NotificationType.PieceReturnedToStones, text, returned, num);
    }

    // клиент: финал без «X has escaped!» для общей победы
    private extern void orig_DisplayEndSequence(string winnerName);
    private void DisplayEndSequence(string winnerName)
    {
        if (!string.IsNullOrEmpty(winnerName) || CoopCampaign.Instance == null) { orig_DisplayEndSequence(winnerName); return; }
        CoopCampaign.Instance.ClientSharedEnding();
    }

    // время игры в статистике — с начала кампании, а не острова
    private extern void orig_IslandLoadedOnServer(object sender, EventArgs e);
    private void IslandLoadedOnServer(object sender, EventArgs e)
    {
        orig_IslandLoadedOnServer(sender, e);
        if (CoopCampaign.Instance != null) CurrentGameStartTime = Network.time - CoopCampaign.Instance.PlayedBefore;
    }
}
