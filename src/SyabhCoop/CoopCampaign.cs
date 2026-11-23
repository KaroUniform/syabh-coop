// Компонент кампании. Живёт на объекте NetworkManager (и на сервере, и у
// клиентов), все RPC мода — здесь. Разбит на части (partial):
//   CoopCampaign.cs — связь и проверка версии мода (этап 0);
//   CoopIslands.cs  — острова и переход (этап 1);
//   CoopBoats.cs    — лодки, предложение/согласие, подсказка на экране;
//   CoopDebug.cs    — команды "#coop" в чате и автотест.
//
// Правила для всех RPC мода (ревью этапа 0):
//  - первым делом проверить сторону (Network.isServer / isClient);
//  - на клиенте — что отправитель сервер; на сервере — что отправитель
//    известный игрок, а его аргументы допустимы;
//  - строки от другой стороны — в журнал только через CoopMod.Clean;
//  - сетевая группа 31 (объект NetworkManager) игрой не глушится, RPC может
//    прийти до готовности мира — проверять своё состояние, а не только группу.
using System.Collections.Generic;
using UnityEngine;

public partial class CoopCampaign : MonoBehaviour
{
    public static CoopCampaign Instance { get; private set; }

    // Сколько сервер ждёт «привет» от нового игрока, прежде чем отключить его
    // как клиента без мода. Штатная проверка версии (CheckClientVersion) обычно
    // отключает такого раньше; это страховка, если её выключат.
    private const float HelloTimeout = 20f;

    // клиент
    private bool _helloSent;
    private float _connectedAt = -1f;
    // сервер: кто поздоровался, кто ждёт
    private readonly Dictionary<NetworkPlayer, string> _helloFrom = new Dictionary<NetworkPlayer, string>();
    private readonly Dictionary<NetworkPlayer, float> _waitingHello = new Dictionary<NetworkPlayer, float>();
    private float _nextPrune;

    private void Awake()
    {
        Instance = this;
    }

    private void Update()
    {
        if (Network.isServer)
        {
            ServerUpdate();
            ServerBoatUpdate();
            ServerDeathUpdate();
            BodiesUpdate();
            PerfTestTick();
            ServerSaveTick();
            ServerAutoTest6Tick();
            PerfUpdate();
            ServerAutoTestTick();
            return;
        }
        if (Network.isClient)
        {
            if (_connectedAt < 0f) _connectedAt = Time.realtimeSinceStartup;
            // через 3 с после подключения: сервер к этому времени завёл запись
            // игрока и проверил версию (несовпадающих он уже отключил)
            if (!_helloSent && Time.realtimeSinceStartup - _connectedAt > 3f)
            {
                _helloSent = true;
                CoopMod.Log("client: hello -> server");
                networkView.RPC("CoopHello", RPCMode.Server, CoopMod.Version);
                networkView.RPC("CoopIdentity", RPCMode.Server, ClientId());
                ClientSendClass();
            }
            ClientBoatUpdate();
            ClientDeathUpdate();
            BodiesUpdate();
            PerfUpdate();
            ClientSpawnWatch();
            return;
        }
        // не подключены: сбросить клиентское и серверное состояние
        _helloSent = false;
        _connectedAt = -1f;
        if (_helloFrom.Count > 0 || _waitingHello.Count > 0)
        {
            _helloFrom.Clear();
            _waitingHello.Clear();
        }
    }

    private void ServerUpdate()
    {
        float now = Time.realtimeSinceStartup;
        // кто не поздоровался вовремя — клиент без мода: отключаем штатной причиной
        List<NetworkPlayer> late = null;
        foreach (var kv in _waitingHello)
            if (now - kv.Value > HelloTimeout) (late ?? (late = new List<NetworkPlayer>())).Add(kv.Key);
        if (late != null)
            foreach (var p in late)
            {
                _waitingHello.Remove(p);
                CoopMod.Warn("server: player " + p + " sent no mod hello in " + HelloTimeout + " s — disconnecting (no mod?)");
                Kick(p);
            }
        // раз в 5 с — убрать из своих списков тех, кого уже нет среди подключений
        // (игра иногда чистит свой список в ValidatePlayerList без OnPlayerDisconnected)
        if (now >= _nextPrune)
        {
            _nextPrune = now + 5f;
            var alive = new HashSet<NetworkPlayer>(Network.connections);
            Prune(_helloFrom, alive);
            Prune(_waitingHello, alive);
        }
    }

    private static void Prune<T>(Dictionary<NetworkPlayer, T> d, HashSet<NetworkPlayer> alive)
    {
        List<NetworkPlayer> gone = null;
        foreach (var k in d.Keys)
            if (!alive.Contains(k)) (gone ?? (gone = new List<NetworkPlayer>())).Add(k);
        if (gone != null)
            foreach (var k in gone) d.Remove(k);
    }

    // Как NetworkManager.DisconnectPlayer (приватный): сообщить причину и закрыть соединение.
    private void Kick(NetworkPlayer p)
    {
        networkView.RPC("NotifyClientOfDisconnect", p, (int)DisconnectionReason.BadClientVersion);
        Network.CloseConnection(p, true);
    }

    private void OnServerInitialized()
    {
        _helloFrom.Clear();
        _waitingHello.Clear();
        CoopMod.Log("server: initialized, waiting for players");
    }

    private void OnPlayerConnected(NetworkPlayer player)
    {
        _waitingHello[player] = Time.realtimeSinceStartup;
        CoopMod.Log("server: player " + player + " connected, waiting for mod hello");
    }

    private void OnPlayerDisconnected(NetworkPlayer player)
    {
        _waitingHello.Remove(player);
        StartCoroutine(ForgetKeyLater(player));   // ключ нужен ещё патчу NetworkManager.OnPlayerDisconnected (снимок)
        if (_helloFrom.Remove(player))
            CoopMod.Log("server: player " + player + " left; players with mod: " + _helloFrom.Count);
    }

    private System.Collections.IEnumerator ForgetKeyLater(NetworkPlayer p)
    {
        yield return null;
        _playerKey.Remove(p);
    }

    // автотест/бот: «готов» в лобби, как AutoStart.ReadyUp
    private System.Collections.IEnumerator AutoReady()
    {
        yield return new WaitForSeconds(2f);
        if (!Network.isClient) yield break;
        CoopMod.Log("client: autotest — ready in lobby");
        NetworkManager.Instance.SetClientReadyState(true);
    }

    [RPC]
    private void CoopHello(string clientModVersion, NetworkMessageInfo info)
    {
        if (!Network.isServer) return;
        if (_helloFrom.ContainsKey(info.sender)) return; // один «привет» на подключение
        string v = CoopMod.Clean(clientModVersion, 32);
        if (clientModVersion != CoopMod.Version)
        {
            // своя проверка, не зависит от CheckClientVersion игры
            CoopMod.Warn("server: player " + info.sender + " has mod " + v + ", server has " +
                         CoopMod.Version + " — disconnecting");
            _waitingHello.Remove(info.sender);
            Kick(info.sender);
            return;
        }
        _waitingHello.Remove(info.sender);
        _helloFrom[info.sender] = v;
        CoopMod.Log("server: hello from player " + info.sender + " (" + info.sender.ipAddress +
                    "), mod " + v + "; players with mod: " + _helloFrom.Count);
        networkView.RPC("CoopWelcome", info.sender, CoopMod.Version);
    }

    [RPC]
    private void CoopWelcome(string serverModVersion, NetworkMessageInfo info)
    {
        if (!Network.isClient) return;
        // у клиента единственное соединение — с сервером
        if (Network.connections.Length == 0 || info.sender != Network.connections[0]) return;
        CoopMod.Log("client: welcome from server, mod " + CoopMod.Clean(serverModVersion, 32) +
                    " — connection OK");
        if (CoopMod.AutoTest || CoopMod.Bot) StartCoroutine(AutoReady());
    }

    // ---- кто есть кто (этап 2) ----
    // Игрока кампания узнаёт по постоянному id установки (файл в папке
    // сохранений клиента), а не только по имени: имя в игре по умолчанию у
    // всех "Player" (Hunted.Data/NetworkPlayerData.cs), и у двух игроков оно
    // может совпасть. Имя — для сообщений.
    private readonly Dictionary<NetworkPlayer, string> _playerKey = new Dictionary<NetworkPlayer, string>();

    private static string ClientId()
    {
        string path = null;
        try
        {
            path = System.IO.Path.Combine(C_SaveManager.m_Instance.m_SavePath.Root, "CoopPlayerId.txt");
            if (System.IO.File.Exists(path))
            {
                string id = System.IO.File.ReadAllText(path).Trim();
                if (ValidId(id)) return id;
            }
            string nid = System.Guid.NewGuid().ToString("N");
            System.IO.File.WriteAllText(path, nid);
            CoopMod.Log("client: new player id saved to " + path);
            return nid;
        }
        catch (System.Exception e)
        {
            CoopMod.Warn("client: player id file " + path + ": " + e.Message);
            return "";
        }
    }

    private static bool ValidId(string id)
    {
        if (id == null || id.Length != 32) return false;
        foreach (char c in id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    [RPC]
    private void CoopIdentity(string id, NetworkMessageInfo info)
    {
        if (!Network.isServer || !_helloFrom.ContainsKey(info.sender) || _playerKey.ContainsKey(info.sender)) return;
        if (!ValidId(id)) { CoopMod.Warn("server: player " + info.sender + " sent a bad id, using the name"); return; }
        string key = id;
        // копия той же установки (или скопированная папка сохранений) — различаем
        for (int n = 2; _playerKey.ContainsValue(key); n++) key = id + "-" + n;
        if (key != id)
        {
            // ключ «-2» зависит от порядка входа — после перезапуска вещи могут
            // поменяться местами (ревью Qwen п.6)
            CoopMod.Warn("server: player " + info.sender + " has the same id as another player (copied game folder?)");
            Tell(info.sender, "Your co-op id equals your partner's: delete SaveGames/client/CoopPlayerId.txt and restart the game");
        }
        _playerKey[info.sender] = key;
        CoopMod.Log("server: player " + info.sender + " id " + key.Substring(0, 8) + "…");
    }

    // ключ игрока в состоянии кампании
    public string Key(NetworkPlayerRecord r)
    {
        string k;
        if (_playerKey.TryGetValue(r.NetworkPlayer, out k)) return k;
        return "name:" + Name(r);
    }
}
