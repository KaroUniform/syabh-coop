// Патч AI_PoacherInvestigate.SoundHeard: игра проверяла «звук от игрока?» через
// r.Components.PlayerGameObject у ВСЕХ записей игроков (AI_PoacherInvestigate.cs:132),
// а у игрока, который ещё грузит остров, Components == null → NullReferenceException.
// Исключение уходит из события C_AudioReceiver.SoundHeard: этот браконьер и
// подписанные после него не реагируют на звук. Чаще всего — сразу после прибытия
// на остров (прогон s5g: 176 раз за миг прибытия). Остальное — как в игре.
#pragma warning disable CS0169, CS0649
using System.Linq;
using UnityEngine;

class patch_AI_PoacherInvestigate : AI_PoacherInvestigate
{
    private C_BotAICore _bot;
    private bool _hasCurrentFocus;
    private Vector3 _currentDestination;
    private float _currentFocusPerceivedVolume;

    private void SoundHeard(object sender, C_AudioReceiver.SoundHeardEventArgs e)
    {
        if (object.ReferenceEquals(e.Source, base.gameObject)) return;
        bool flag = NetworkManager.Instance.AllPlayers.Any(r => r.Components != null && r.Components.PlayerGameObject == e.Source);
        if (flag && Time.time < m_InvestigateCooldown) return;
        for (int i = 0; i < m_IgnoreSoundsFrom.Length; i++)
            if (e.Type == m_IgnoreSoundsFrom[i]) return;
        if (e.PerceivedVolume > _currentFocusPerceivedVolume * AudioInterrupVolumeFactor)
        {
            m_AttentionUntilTime = Time.time + AudioAttentionBaseTime + AudioAttentionVolumeFactor * e.PerceivedVolume;
            _currentFocusPerceivedVolume = e.PerceivedVolume;
            _currentDestination = e.Position;
            _currentDestination = m_pAI_PoacherLeash.ConstrainMoveToLeash(_currentDestination);
            _hasCurrentFocus = true;
            _bot.RequestStateChange(this);
            m_AttentionUntilTime = Time.time + 10f;
            if (flag) m_InvestigateCooldown = Time.time + m_InvestigateCooldownPeriod;
        }
    }
}
