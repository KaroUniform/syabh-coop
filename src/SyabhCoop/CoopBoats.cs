// Кампания: лодки и согласие на отплытие (этап 1).
//
// Клиент: у лодки (до BoatRadius м) подсказка «[B] предложить отплыть»; B —
// RPC серверу. Напарнику — запрос: B — согласиться, N — отказаться.
// Сервер: проверяет, что предлагающий у лодки, никто не мёртв, не кровоточит и
// не голоден; ждёт согласия всех заспавненных игроков (AskTimeout с), потом
// переход. Отказ/таймаут/ошибка — сообщение всем.
// Клавиши: B и N в игре свободны (занятые: E/Q — наклон, F — взаимодействие,
// Y — показать себя, T — чат, O — торг).
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class CoopCampaign
{
    public const float BoatRadius = 12f;   // «у лодки», по горизонтали
    public const float AskTimeout = 30f;

    public class Boat
    {
        public string Direction;   // GetDirection: North/South/East/West
        public Vector3 BoatPos;
        public Vector3 SpawnPos;
        public Vector3 SpawnRot;
        public Vector3 Facing;     // от лодки к центру острова
    }

    private readonly List<Boat> _boats = new List<Boat>();

    // из патча C_RegionBoat.EndInit — и на сервере, и у клиента
    public void RegisterBoat(C_RegionBoat region)
    {
        string dir = region.GetDirection();
        if (string.IsNullOrEmpty(dir)) return;
        Transform t = region.transform.Find("P_Boat(Clone)");
        Vector3 boat = t != null ? t.position : region.m_Data.m_Centre;
        // как C_RegionBoat.EndInit в MP: 4 м от лодки к центру острова, высота 4
        float half = C_WorldGraph.m_Instance.m_BoundarySize / 2f;
        Vector3 facing = (new Vector3(half, 0f, half) - boat);
        facing.y = 0f;
        facing.Normalize();
        Vector3 spawn = boat + facing * 4f;
        spawn.y = 4f;
        Vector3 rot = Quaternion.LookRotation(facing, Vector3.up).eulerAngles;
        rot.x = 0f; rot.z = 0f;
        _boats.Add(new Boat { Direction = dir, BoatPos = boat, SpawnPos = spawn, SpawnRot = rot, Facing = facing });
        CoopMod.Log((Network.isServer ? "server" : "client") + ": boat " + dir + " at " + V(boat));
    }

    // куда ведёт лодка с текущего острова
    public static string Destination(string island, string boatDirection)
    {
        return island == "Centre" ? boatDirection : "Centre";
    }

    private Boat NearestBoat(Vector3 p, out float dist)
    {
        Boat best = null; dist = float.MaxValue;
        foreach (var b in _boats)
        {
            float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(b.BoatPos.x, b.BoatPos.z));
            if (d < dist) { dist = d; best = b; }
        }
        return best;
    }

    // ================= сервер =================

    private class Pending
    {
        public NetworkPlayer Proposer;
        public string Target;
        public float Expires;
        public HashSet<NetworkPlayer> Accepted = new HashSet<NetworkPlayer>();
    }
    private Pending _pending;

    private IEnumerable<NetworkPlayerRecord> Spawned()
    {
        foreach (var r in NM.CoopRecords)
            if (r.IsSpawned && r.Components != null) yield return r;
    }

    private string Name(NetworkPlayerRecord r)
    {
        return r.PlayerInfo != null && r.PlayerInfo.Name != null ? CoopMod.Clean(r.PlayerInfo.Name, 16) : r.NetworkPlayer.ToString();
    }

    private NetworkPlayerRecord Record(NetworkPlayer p)
    {
        foreach (var r in NM.CoopRecords) if (r.NetworkPlayer == p) return r;
        return null;
    }

    private void Tell(NetworkPlayer p, string msg)
    {
        networkView.RPC("DisplayNotificationOnClient", p, "[#FFFF88]" + msg);
    }

    private void TellAll(string msg)
    {
        CoopMod.Log("server: tell all: " + msg);
        foreach (var r in NM.CoopRecords) Tell(r.NetworkPlayer, msg);
    }

    // Почему нельзя плыть (null — можно). Проверяет всех заспавненных.
    public string TravelBlocker()
    {
        if (Transitioning) return "Travel already in progress";
        // все, кто в игре с модом, должны быть на острове и уже с вещами (ревью Kimi п.4)
        foreach (var r in NM.CoopRecords)
        {
            if (!_helloFrom.ContainsKey(r.NetworkPlayer)) continue;
            if (!r.IsSpawned || r.Components == null) return Name(r) + " is still loading";
            if (_restoring.Contains(Key(r))) return Name(r) + " is still arriving";
        }
        foreach (var r in Spawned())
        {
            var c = r.Components;
            if (IsDead(r)) return Name(r) + " is dead";   // здоровье мёртвого тела восстанавливается (C_CharacterVitality.cs:74) — смотрим флаг смерти
            if (c.BleedMonitor != null && c.BleedMonitor.m_IsBleeding) return Name(r) + " is bleeding";
            if (c.CharacterVitality != null && (int)c.CharacterVitality._currentVitality <= 0) return Name(r) + " is starving";
        }
        return null;
    }

    [RPC]
    private void CoopProposeTravel(NetworkMessageInfo info)
    {
        if (!Network.isServer) return;
        ServerPropose(info.sender);
    }

    public void ServerPropose(NetworkPlayer sender)
    {
        var rec = Record(sender);
        if (rec == null || !rec.IsSpawned || rec.Components == null) return;
        float dist;
        Boat b = NearestBoat(rec.Components.Body.position, out dist);
        if (b == null || dist > BoatRadius + 3f)  // +3 м — запас на рассинхрон позиции
        {
            Tell(sender, "You need to be at a boat to sail");
            CoopMod.Log("server: propose from " + Name(rec) + " rejected: not at a boat (" + (b == null ? "no boats" : dist.ToString("0") + " m") + ")");
            return;
        }
        string target = Destination(Island, b.Direction);
        if (_pending != null)
        {
            if (_pending.Target == target && _pending.Proposer != sender) { ServerAnswer(sender, true); return; }
            Tell(sender, "Another proposal is waiting for an answer");
            return;
        }
        string why = TravelBlocker();
        if (why != null)
        {
            TellAll("Cannot sail to " + target + ": " + why);
            return;
        }
        _pending = new Pending { Proposer = sender, Target = target, Expires = Time.realtimeSinceStartup + AskTimeout };
        _pending.Accepted.Add(sender);
        CoopMod.Log("server: " + Name(rec) + " proposes to sail " + Island + " -> " + target);
        int asked = 0;
        foreach (var r in Spawned())
            if (r.NetworkPlayer != sender)
            {
                networkView.RPC("CoopTravelAsk", r.NetworkPlayer, Name(rec), target, (int)AskTimeout);
                asked++;
            }
        if (asked == 0) { ServerTryDepart(); return; }
        Tell(sender, "Waiting for your partner to agree to sail to " + target + "...");
    }

    [RPC]
    private void CoopTravelAnswer(bool yes, NetworkMessageInfo info)
    {
        if (!Network.isServer) return;
        ServerAnswer(info.sender, yes);
    }

    private void ServerAnswer(NetworkPlayer sender, bool yes)
    {
        if (_pending == null) return;
        var rec = Record(sender);
        if (rec == null || !rec.IsSpawned) return;
        if (!yes)
        {
            TellAll(Name(rec) + " doesn't want to sail to " + _pending.Target);
            ClearPending("refused");
            return;
        }
        _pending.Accepted.Add(sender);
        CoopMod.Log("server: " + Name(rec) + " agrees to sail to " + _pending.Target);
        ServerTryDepart();
    }

    private void ServerTryDepart()
    {
        if (_pending == null) return;
        foreach (var r in Spawned())
            if (!_pending.Accepted.Contains(r.NetworkPlayer)) return;   // ждём остальных
        string target = _pending.Target;
        string why = TravelBlocker();
        ClearPending(null);
        if (why != null) { TellAll("Cannot sail to " + target + ": " + why); return; }
        ServerTravel(target);
    }

    private void ClearPending(string reason)
    {
        if (_pending == null) return;
        if (reason != null) CoopMod.Log("server: proposal to " + _pending.Target + " cancelled: " + reason);
        if (Network.isServer)
            foreach (var r in NM.CoopRecords) networkView.RPC("CoopTravelAskCancel", r.NetworkPlayer);
        _pending = null;
    }

    private void ServerBoatUpdate()
    {
        // ушедший/отключившийся напарник больше не держит предложение (Kimi п.11)
        if (_pending != null) ServerTryDepart();
        if (_pending != null && Time.realtimeSinceStartup > _pending.Expires)
        {
            TellAll("No answer - staying on " + Island);
            ClearPending("timeout");
        }
    }

    // ================= клиент =================

    private class Ask { public string From; public string Target; public float Expires; }
    private Ask _ask;
    private string _hint;          // подсказка на экране (null — нет)

    [RPC]
    private void CoopTravelAsk(string from, string target, int seconds, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _ask = new Ask { From = CoopMod.Clean(from, 16), Target = CoopMod.Clean(target, 16), Expires = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 1, 120) };
        CoopMod.Log("client: " + _ask.From + " asks to sail to " + _ask.Target);
        if (CoopMod.Bot) StartCoroutine(BotAnswer());
    }

    [RPC]
    private void CoopTravelAskCancel(NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        _ask = null;
    }

    private bool InPlay()
    {
        return ClientPlayerManager.LocalInstance != null && C_UIMenuToggler.m_Instance != null &&
               C_UIMenuToggler.m_Instance.CurrentState == C_UIMenuToggler.ControlState.InPlay;
    }

    private void ClientBoatUpdate()
    {
        _hint = null;
        if (ClientPlayerManager.LocalInstance == null) return;
        if (_ask != null)
        {
            float left = _ask.Expires - Time.realtimeSinceStartup;
            if (left <= 0f) { _ask = null; return; }
            _hint = _ask.From + " wants to sail to " + _ask.Target + ":  [B] agree   [N] refuse   (" + Mathf.CeilToInt(left) + ")";
            if (InPlay())
            {
                if (Input.GetKeyDown(KeyCode.B)) ClientAnswer(true);
                else if (Input.GetKeyDown(KeyCode.N)) ClientAnswer(false);
            }
            return;
        }
        float dist;
        Boat b = NearestBoat(ClientPlayerManager.LocalInstance.Components.Body.position, out dist);
        if (b == null || dist > BoatRadius) return;
        string target = Destination(ClientIsland, b.Direction);
        _hint = "[B] propose to sail to " + target;
        if (InPlay() && Input.GetKeyDown(KeyCode.B)) ClientPropose();
    }

    public void ClientPropose()
    {
        CoopMod.Log("client: propose to sail");
        networkView.RPC("CoopProposeTravel", RPCMode.Server);
    }

    public void ClientAnswer(bool yes)
    {
        CoopMod.Log("client: answer " + (yes ? "yes" : "no"));
        _ask = null;
        networkView.RPC("CoopTravelAnswer", RPCMode.Server, yes);
    }

    private System.Collections.IEnumerator BotAnswer()
    {
        yield return new WaitForSeconds(2f);
        if (_ask != null) ClientAnswer(_botAnswer);
    }
    private bool _botAnswer = true;

    private GUIStyle _hintStyle;

    private void OnGUI()
    {
        ClassGUI();
        if (_hint == null || !Network.isClient) return;
        if (_hintStyle == null)
        {
            _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _hintStyle.normal.textColor = new Color(1f, 0.95f, 0.7f);
        }
        var r = new Rect(0, Screen.height * 0.62f, Screen.width, 40);
        var shadow = new Rect(r.x + 2, r.y + 2, r.width, r.height);
        var c = _hintStyle.normal.textColor;
        _hintStyle.normal.textColor = Color.black;
        GUI.Label(shadow, _hint, _hintStyle);
        _hintStyle.normal.textColor = c;
        GUI.Label(r, _hint, _hintStyle);
    }

    public void CoopTell(NetworkPlayer p, string msg) { Tell(p, msg); }

    public void ServerLobbyNextFrame() { StartCoroutine(LobbyNextFrame()); }
    private System.Collections.IEnumerator LobbyNextFrame()
    {
        yield return null;
        CoopMod.Log("server: victory - back to the lobby");
        ClearCorpsesBeforeTeardown();
        NM.CoopStartLobby();
    }

    private void Notify(string msg)
    {
        CoopMod.Log("client: notify: " + msg);
        if (C_Notification.m_Instance != null) C_Notification.m_Instance.ShowNotification(msg, 5f);
    }
}
