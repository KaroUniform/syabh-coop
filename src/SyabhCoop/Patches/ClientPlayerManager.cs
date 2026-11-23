// Патч ClientPlayerManager.RequestRespawn (этап 5): игра не проверяла, что
// игрок мёртв, и что просит владелец тела (ClientPlayerManager.cs:557) —
// повторный/чужой запрос сбрасывал инвентарь живому. Теперь сервер отбрасывает
// такие запросы и помечает труп как «уже без подъёма».
#pragma warning disable CS0626
using UnityEngine;

class patch_ClientPlayerManager : ClientPlayerManager
{
    public extern void orig_RequestRespawn(NetworkMessageInfo info);
    public new void RequestRespawn(NetworkMessageInfo info)
    {
        if (Network.isServer && CoopCampaign.Instance != null)
        {
            if (info.sender != networkView.owner)
            {
                CoopMod.Warn("server: respawn request for " + networkView.owner + " from " + info.sender + " ignored");
                return;
            }
            if (!CoopCampaign.Instance.ServerBeforeRespawn(info.sender)) return;
        }
        orig_RequestRespawn(info);
    }
}
