// Патч C_ChangeIslands: генерация того острова, что выставлен в m_CurrentIsland
// (в игре Update перед генерацией жёстко ставил "Centre", C_ChangeIslands.cs:82).
// Остальное тело Update — без изменений.
#pragma warning disable CS0626
using UnityEngine;

class patch_C_ChangeIslands : C_ChangeIslands
{
    [MonoMod.MonoModIgnore] public extern void UpdateIslandLoad();
    [MonoMod.MonoModIgnore] public extern void UpdateIslandGenSlim();

    // было: C_MenuWindowManager.ForceHideAllWindows() безусловно (C_ChangeIslands.cs:115).
    // На сервере, который только что вышел из лобби, C_UIMenuToggler ещё нет
    // (он в сцене острова) → NullReferenceException в NotifyUiClosed и остров
    // не грузится. Так было при продолжении сохранённой кампании (прогон s6b):
    // в ванили из лобби всегда шла генерация, а не загрузка с диска.
    public new void LoadIsland()
    {
        if (C_UIMenuToggler.m_Instance != null) C_MenuWindowManager.ForceHideAllWindows();
        C_SaveManager.m_Instance.m_SavePath.CurrentIsland = m_CurrentIsland;
        m_IsRegenerating = false;
        CoopMod.Log("islands: reloading the scene to load " + m_CurrentIsland);
        m_Stage = 1;
    }

    private void Update()
    {
        if (m_Stage > 0 && !m_IsRegenerating)
        {
            UpdateIslandLoad();
        }
        else if (m_Stage > 0 && m_IsRegenerating)
        {
            UpdateIslandGenSlim();
        }
        if (m_Stage <= 0)
        {
            if (m_MakeAll == 1)
            {
                CoopMod.Log("worldgen: generating island " + m_CurrentIsland);
                RegenerateIsland();
                m_MakeAll = 6;
            }
            else if (m_MakeAll == 6)
            {
                C_SaveManager.m_Instance.GameProgressSaveData();
                C_SaveManager.m_Instance.GameStatsSaveData();
                m_MakeAll = 0;
            }
        }
    }
}
