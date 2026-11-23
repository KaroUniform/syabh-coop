// Замер кадров (-coopperf) и сценарий «игроки рядом с роботами»
// (-coopperftest на сервере) — чтобы мерить лаги у роботов, а не гадать.
// Раз в 5 с: средний и худший кадр, сборки мусора, сколько роботов всего и в
// 60 м от игрока. Сценарий: 40 с у лодки, 60 с в 15 м от ближайшего робота
// (игроков не дают убить), 30 с снова вдали.
using System;
using System.Collections;
using System.Linq;
using UnityEngine;

public partial class CoopCampaign
{
    public static readonly bool PerfLog = Environment.GetCommandLineArgs().Contains("-coopperf");
    public static readonly bool PerfTest = Environment.GetCommandLineArgs().Contains("-coopperftest");

    private float _perfT0 = -1f, _perfMax;
    private int _perfFrames, _perfGc0;
    public string PerfPhase = "-";

    private void PerfUpdate()
    {
        if (!PerfLog && !PerfTest) return;
        float now = Time.realtimeSinceStartup;
        if (_perfT0 < 0f) { _perfT0 = now; _perfGc0 = GC.CollectionCount(0); _perfFrames = 0; _perfMax = 0f; return; }
        _perfFrames++;
        _perfMax = Mathf.Max(_perfMax, Time.unscaledDeltaTime);
        if (now - _perfT0 < 5f) return;
        int gc = GC.CollectionCount(0);
        int bots = 0, near = 0;
        float nearest = -1f;
        Vector3 me;
        if (PlayerPos(out me))
        {
            // клиент: удалённые роботы — объекты с C_BotAnimManager; сервер — с C_BotAICore
            var objs = Network.isServer ? (Component[])UnityEngine.Object.FindObjectsOfType<C_BotAICore>() : UnityEngine.Object.FindObjectsOfType<C_BotAnimManager>();
            foreach (var o in objs)
            {
                if (o == null || !o.gameObject.activeInHierarchy) continue;
                bots++;
                DescribeRobot(o.transform.root.gameObject);
                float d = Vector3.Distance(o.transform.position, me);
                if (d < 60f) near++;
                if (nearest < 0f || d < nearest) nearest = d;
            }
        }
        float dt = now - _perfT0;
        CoopMod.Log("perf " + (Network.isServer ? "server" : "client") + " [" + PerfPhase + "]: " + (_perfFrames / dt).ToString("0.0") + " fps, avg " +
                    (dt * 1000f / Mathf.Max(1, _perfFrames)).ToString("0.0") + " ms, worst " + (_perfMax * 1000f).ToString("0") + " ms, gc " + (gc - _perfGc0) +
                    ", heap " + (GC.GetTotalMemory(false) / 1048576) + " MB, robots " + bots + " (" + near + " within 60 m, nearest " + nearest.ToString("0") + " m)");
        _perfT0 = now; _perfGc0 = gc; _perfFrames = 0; _perfMax = 0f;
    }

    // один раз на тип: из чего робот состоит для рендера (тени, skinned-меши,
    // culling аниматора, свет) — отделить графику от кода
    private readonly System.Collections.Generic.HashSet<string> _described = new System.Collections.Generic.HashSet<string>();
    private bool _qualityLogged;
    private void DescribeRobot(GameObject g)
    {
        if (!_qualityLogged)
        {
            _qualityLogged = true;
            CoopMod.Log("perf quality: level " + QualitySettings.GetQualityLevel() + " '" + QualitySettings.names[QualitySettings.GetQualityLevel()] + "', shadowDistance " +
                        QualitySettings.shadowDistance + ", cascades " + QualitySettings.shadowCascades + ", pixelLights " + QualitySettings.pixelLightCount +
                        ", lodBias " + QualitySettings.lodBias + ", vSync " + QualitySettings.vSyncCount + ", screen " + Screen.width + "x" + Screen.height);
        }
        string type = g.name.Replace("(Clone)", "");
        if (_described.Contains(type)) return;
        _described.Add(type);
        var rs = g.GetComponentsInChildren<Renderer>(true);
        var sk = g.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var an = g.GetComponentsInChildren<Animator>(true);
        var li = g.GetComponentsInChildren<Light>(true);
        var ps = g.GetComponentsInChildren<ParticleSystem>(true);
        var au = g.GetComponentsInChildren<AudioSource>(true);
        var mb = g.GetComponentsInChildren<MonoBehaviour>(true);
        int shadows = rs.Count(r => r.castShadows), mats = rs.Sum(r => r.sharedMaterials.Length), bones = sk.Sum(k => k.bones != null ? k.bones.Length : 0);
        int enabledMb = mb.Count(m => m != null && m.enabled && m.gameObject.activeInHierarchy);
        CoopMod.Log("perf robot " + type + ": renderers " + rs.Length + " (shadow casters " + shadows + ", materials " + mats + "), skinned " + sk.Length +
                    " (bones " + bones + ", offscreen update " + sk.Count(k => k.updateWhenOffscreen) + "), animators " + an.Length +
                    " (culling " + string.Join(",", an.Select(x => x.cullingMode.ToString()).Distinct().ToArray()) + "), lights " + li.Length +
                    ", particles " + ps.Length + ", audio " + au.Length + ", scripts " + mb.Length + " (" + enabledMb + " active): " +
                    string.Join(",", mb.Where(m => m != null && m.enabled && m.gameObject.activeInHierarchy).Select(m => m.GetType().Name).Distinct().ToArray()));
    }

    private bool PlayerPos(out Vector3 p)
    {
        p = Vector3.zero;
        if (Network.isServer)
        {
            var r = Spawned().FirstOrDefault();
            if (r == null) return false;
            p = r.Components.Body.position;
            return true;
        }
        var l = ClientPlayerManager.LocalInstance;
        if (l == null) return false;
        p = l.Components.Body.position;
        return true;
    }

    [RPC]
    private void CoopPerfPhase(string phase, NetworkMessageInfo info)
    {
        if (!FromServer(info)) return;
        PerfPhase = CoopMod.Clean(phase, 16);
        _perfT0 = -1f;   // новый отрезок замера
    }

    private void SetPhase(string ph)
    {
        PerfPhase = ph; _perfT0 = -1f;
        CoopMod.Log("perf phase " + ph);
        foreach (var r in NM.CoopRecords) networkView.RPC("CoopPerfPhase", r.NetworkPlayer, ph);
    }

    private bool _perfStarted;
    private void PerfTestTick()
    {
        if (!PerfTest || _perfStarted || NetworkManager.Instance == null) return;
        if (Transitioning || Spawned().Count() < NetworkManager.Instance.MinimumPlayers) return;
        _perfStarted = true;
        StartCoroutine(PerfScenario());
    }

    private IEnumerator PerfScenario()
    {
        yield return new WaitForSeconds(15f);
        SetPhase("far");
        yield return new WaitForSeconds(40f);
        var players = Spawned().ToList();
        // ближайший к игроку робот-охотник (у отряда — несколько), иначе любой
        Vector3 me = players[0].Components.Body.position;
        var bots = UnityEngine.Object.FindObjectsOfType<C_BotAICore>().Where(b => b != null && b.gameObject.activeInHierarchy).ToList();
        var target = bots.Where(b => b.name.Contains("Hunter")).OrderBy(b => (b.transform.position - me).sqrMagnitude).FirstOrDefault()
                     ?? bots.OrderBy(b => (b.transform.position - me).sqrMagnitude).FirstOrDefault();
        CoopMod.Log("perf: " + bots.Count + " robots on the island: " +
                    string.Join(", ", bots.GroupBy(b => b.name.Replace("(Clone)", "")).Select(g => g.Key + " x" + g.Count()).ToArray()));
        if (target == null) { CoopMod.Warn("perf: no robots"); yield break; }
        Vector3 tp = target.transform.position;
        CoopMod.Log("perf: going to " + target.name + " at " + V(tp));
        SetPhase("robots");
        float until = Time.realtimeSinceStartup + 60f;
        int i = 0;
        foreach (var r in players)
        {
            Vector3 p = GroundAt(tp + new Vector3(15f + 2f * i++, 0f, 0f)) + Vector3.up;
            Teleport(r, p, new Vector3(0f, 270f, 0f));
        }
        while (Time.realtimeSinceStartup < until)
        {
            foreach (var r in Spawned())   // не дать убить: здоровье и кровь — каждую секунду
            {
                var c = r.Components;
                if (c.BleedMonitor.m_IsBleeding) c.BleedMonitor.StopBleeding();
                if (c.CharacterHealth._currentHealth < c.CharacterHealth.MaximumHealth * 0.8f) c.CharacterHealth.SetHealth(c.CharacterHealth.MaximumHealth);
            }
            yield return new WaitForSeconds(0.5f);
        }
        SetPhase("back");
        Boat b0 = _boats.FirstOrDefault();
        foreach (var r in Spawned()) if (b0 != null) Teleport(r, b0.SpawnPos, b0.SpawnRot);
        yield return new WaitForSeconds(30f);
        CoopMod.Log("perf DONE");
    }
}
