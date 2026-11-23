// Кампания: свой класс у каждого игрока (этап 4).
//
// В MP выбора класса нет: стартовый набор выдаёт сервер по своему
// C_SaveManager.m_PlayerData.m_WhichClass — одному на всех (исследование Kimi
// п.37-44). Здесь: клиент выбирает класс в лобби (плашка вверху экрана,
// кнопки < >) или командой "#coop class <номер>" в чате;
// выбор хранится в файле CoopClass.txt в папке сохранений клиента и
// отправляется серверу; сервер подставляет класс владельца тела в
// GuaranteeDefaultEquipment (патч C_PlayerEquipmentController).
// Класс влияет на стартовый набор: в начале кампании и при респауне после
// смерти (снимок перехода набор не выдаёт).
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class CoopCampaign
{
    // имена из ресурсов игры (sharedassets0.assets), если C_PlayerClassLoadOuts
    // ещё не загружен; порядок — порядок m_ClassLoadOuts
    private static readonly string[] FallbackClassNames =
        { "The Aristocrat", "The Gamekeeper", "The Inventor", "The Officer", "The Cook", "The Artist", "The Old Soak" };

    public static int ClassCount
    {
        get
        {
            var lo = C_PlayerClassLoadOuts.m_Instance;
            return lo != null && lo.m_ClassLoadOuts != null && lo.m_ClassLoadOuts.Length > 0 ? lo.m_ClassLoadOuts.Length : FallbackClassNames.Length;
        }
    }

    public static string ClassName(int i)
    {
        var lo = C_PlayerClassLoadOuts.m_Instance;
        if (lo != null && lo.m_ClassLoadOuts != null && i >= 0 && i < lo.m_ClassLoadOuts.Length && lo.m_ClassLoadOuts[i] != null)
            return lo.m_ClassLoadOuts[i].m_ClassName;
        return i >= 0 && i < FallbackClassNames.Length ? FallbackClassNames[i] : "Class " + (i + 1);
    }

    // ---------------- сервер ----------------
    private readonly Dictionary<string, int> _playerClass = new Dictionary<string, int>();

    [RPC]
    private void CoopSetClass(int cls, NetworkMessageInfo info)
    {
        if (!Network.isServer || !_helloFrom.ContainsKey(info.sender)) return;
        var rec = Record(info.sender);
        if (rec == null) return;
        if (cls < 0 || cls >= ClassCount) { CoopMod.Warn("server: bad class " + cls + " from " + info.sender); return; }
        _playerClass[Key(rec)] = cls;
        CoopMod.Log("server: " + Name(rec) + " plays " + ClassName(cls) + " (" + cls + ")");
    }

    // класс владельца тела (для GuaranteeDefaultEquipment), -1 — не знаем
    public int ServerClassFor(Component bodyPart)
    {
        Transform t = bodyPart.transform;
        ClientPlayerManager cpm = null;
        while (t != null && cpm == null) { cpm = t.GetComponent<ClientPlayerManager>(); t = t.parent; }
        if (cpm == null) return -1;
        var rec = Record(cpm.networkView.owner);
        int c;
        if (rec != null && _playerClass.TryGetValue(Key(rec), out c) && c >= 0 && c < ClassCount) return c;
        return -1;
    }

    private void ServerClassCommand(NetworkPlayerRecord rec, string arg)
    {
        int n;
        if (arg != null && int.TryParse(arg, out n) && n >= 1 && n <= ClassCount)
        {
            _playerClass[Key(rec)] = n - 1;
            Tell(rec.NetworkPlayer, "Your class: " + ClassName(n - 1) + " (starting kit at the campaign start and after a respawn)");
            networkView.RPC("CoopClassChosen", rec.NetworkPlayer, n - 1);
            return;
        }
        int cur;
        _playerClass.TryGetValue(Key(rec), out cur);
        var parts = new List<string>();
        for (int i = 0; i < ClassCount; i++) parts.Add((i + 1) + " " + ClassName(i).Replace("The ", ""));
        Tell(rec.NetworkPlayer, "Class: " + ClassName(cur) + ". Change: #coop class <n>: " + string.Join(", ", parts.ToArray()));
    }

    // ---------------- клиент ----------------
    private int _myClass = -1;

    private string ClassFile()
    {
        return System.IO.Path.Combine(C_SaveManager.m_Instance.m_SavePath.Root, "CoopClass.txt");
    }

    public int MyClass
    {
        get
        {
            if (_myClass < 0)
            {
                _myClass = 0;
                int forced = CoopMod.ArgInt("-coopclass", -1);   // автотест
                if (forced >= 0) _myClass = forced;
                else
                    try
                    {
                        int n;
                        if (System.IO.File.Exists(ClassFile()) && int.TryParse(System.IO.File.ReadAllText(ClassFile()).Trim(), out n)) _myClass = n;
                    }
                    catch (Exception e) { CoopMod.Warn("client: class file: " + e.Message); }
                _myClass = Mathf.Clamp(_myClass, 0, ClassCount - 1);
            }
            return _myClass;
        }
    }

    private void SetMyClass(int c, bool tellServer)
    {
        _myClass = ((c % ClassCount) + ClassCount) % ClassCount;
        try { System.IO.File.WriteAllText(ClassFile(), _myClass.ToString()); }
        catch (Exception e) { CoopMod.Warn("client: class file: " + e.Message); }
        if (C_SaveManager.m_Instance != null) C_SaveManager.m_Instance.m_PlayerData.m_WhichClass = _myClass;  // локальный набор до ответа сервера
        CoopMod.Log("client: class " + ClassName(_myClass));
        if (tellServer && Network.isClient) networkView.RPC("CoopSetClass", RPCMode.Server, _myClass);
    }

    private void ClientSendClass()
    {
        if (C_SaveManager.m_Instance != null) C_SaveManager.m_Instance.m_PlayerData.m_WhichClass = MyClass;
        networkView.RPC("CoopSetClass", RPCMode.Server, MyClass);
    }

    [RPC]
    private void CoopClassChosen(int cls, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        SetMyClass(cls, false);
    }

    private bool InLobby()
    {
        var l = C_MenuLobby.Instance;
        return Network.isClient && l != null && l.m_pHolder != null && l.m_pHolder.activeInHierarchy;
    }

    private GUIStyle _classStyle;

    private void ClassGUI()
    {
        if (!InLobby()) return;
        if (_classStyle == null)
        {
            _classStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _classStyle.normal.textColor = new Color(1f, 0.95f, 0.7f);
        }
        float w = 420f, h = 34f, x = (Screen.width - w) / 2f, y = 6f;
        GUI.Box(new Rect(x, y, w, h), "");
        if (GUI.Button(new Rect(x + 4, y + 4, 34, h - 8), "<")) SetMyClass(MyClass - 1, true);
        if (GUI.Button(new Rect(x + w - 38, y + 4, 34, h - 8), ">")) SetMyClass(MyClass + 1, true);
        GUI.Label(new Rect(x + 40, y, w - 80, h), "Co-op class: " + ClassName(MyClass), _classStyle);
    }
}
