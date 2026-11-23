// Тела (игроков и роботов) под землёй — поднять на поверхность.
//
// Ванильная игра этого не проверяет: падение тела считает физика сервера, а
// клиент берёт с сервера только x/z, высоту — своей физикой
// (RagdollPositionSync.UpdatePosition: y = TargetTransform.position.y), так что
// провалиться тело может у каждого своё. Провалившееся под ландшафт тело нельзя
// обыскать (игроки, игра 2026-11-23). Для игрока у игры такая защита есть —
// LocalPlayer_EnsureAboveTerrain: луч только по коллайдеру ландшафта
// (C_WorldTerrain.RaycastTerrain), поэтому полы и крыши домов не мешают. Здесь то
// же для тел, на сервере и на каждом клиенте, раз в секунду.
using System.Collections.Generic;
using UnityEngine;

public partial class CoopCampaign
{
    private const float BodySinkTolerance = 0.3f;   // глубже — считаем провалившимся
    private const float BodyLiftAbove = 0.35f;      // таз тела над поверхностью после подъёма
    private float _nextBodyScan;
    private readonly Dictionary<int, int> _bodyLifts = new Dictionary<int, int>();
    public static int BodiesLifted;

    private void BodiesUpdate()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextBodyScan) return;
        _nextBodyScan = now + 1f;
        if (!C_WorldTerrain.terrainCollider) return;
        foreach (RagdollWithInventoryBehaviour rb in Object.FindObjectsOfType(typeof(RagdollWithInventoryBehaviour)))
            if (rb != null) LiftIfSunk(rb);
    }

    // true — тело было под землёй и поднято
    private bool LiftIfSunk(RagdollWithInventoryBehaviour rb)
    {
        Transform hips = rb.PhysicsTransform ?? rb.transform;
        Vector3 p = hips.position;
        float depth;
        if (!TerrainDepth(p, out depth) || depth <= BodySinkTolerance) return false;

        // поднять так, чтобы и самая глубокая часть (ноги, руки) была над землёй:
        // иначе застрявшие в ландшафте части тянут тело обратно (прогон s5k —
        // тело робота через секунду снова на 1 м под землёй)
        float lift = depth + BodyLiftAbove, d;
        foreach (Collider c in rb.GetComponentsInChildren<Collider>())
        {
            if (c == null || !c.enabled || c.isTrigger) continue;
            Bounds b = c.bounds;
            if (TerrainDepth(new Vector3(b.center.x, b.min.y, b.center.z), out d)) lift = Mathf.Max(lift, d + 0.05f);
        }
        rb.transform.position += Vector3.up * lift;
        // и закрепить, как игра закрепляет замершее тело
        // (RagdollWithInventoryBehaviour.WaitForMovementToStopAndDisablePhysics)
        foreach (Rigidbody r in rb.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!r.isKinematic) { r.velocity = Vector3.zero; r.angularVelocity = Vector3.zero; }
            r.isKinematic = true;
        }
        int id = rb.GetInstanceID(), n;
        _bodyLifts.TryGetValue(id, out n);
        _bodyLifts[id] = ++n;
        BodiesLifted++;
        CoopMod.Log((Network.isServer ? "server" : "client") + ": body " + rb.gameObject.name + " was " + depth.ToString("0.00") +
                    " m under the ground at " + V(p) + " — lifted by " + lift.ToString("0.00") + " m" + (n > 1 ? " (" + n + " times)" : ""));
        return true;
    }

    // насколько точка ниже поверхности ландшафта (минус — выше); false — ландшафта нет
    private static bool TerrainDepth(Vector3 p, out float depth)
    {
        RaycastHit hit;
        depth = 0f;
        if (!C_WorldTerrain.RaycastTerrain(new Ray(new Vector3(p.x, 2000f, p.z), Vector3.down), out hit, 4000f)) return false;
        depth = hit.point.y - p.y;
        return true;
    }

    // глубина таза тела под поверхностью (для автотеста); NaN — нет ландшафта
    private static float BodyDepth(RagdollWithInventoryBehaviour rb)
    {
        float d;
        return TerrainDepth((rb.PhysicsTransform ?? rb.transform).position, out d) ? d : float.NaN;
    }

    // автотест: утопить замершее тело на 2 м (закрепить, как игра через 5 с
    // покоя, — иначе его может вытолкнуть сама физика, прогон s5k)
    private static void SinkBody(RagdollWithInventoryBehaviour rb)
    {
        float d = BodyDepth(rb);
        if (float.IsNaN(d)) return;
        foreach (Rigidbody r in rb.GetComponentsInChildren<Rigidbody>(true)) r.isKinematic = true;
        rb.transform.position += Vector3.down * (d + 2f);
        CoopMod.Log("test: sank body " + rb.gameObject.name + " 2 m under the ground");
    }
}
