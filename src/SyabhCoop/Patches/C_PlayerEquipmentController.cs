// Патч C_PlayerEquipmentController.GuaranteeDefaultEquipment (этап 4): на
// сервере стартовый набор — класса владельца тела, а не общий
// C_SaveManager.m_PlayerData.m_WhichClass сервера (C_PlayerEquipmentController.cs:91).
#pragma warning disable CS0626

class patch_C_PlayerEquipmentController : C_PlayerEquipmentController
{
    public extern void orig_GuaranteeDefaultEquipment();
    public new void GuaranteeDefaultEquipment()
    {
        int cls = -1;
        if (UnityEngine.Network.isServer && CoopCampaign.Instance != null && PlayerInventory != null && PlayerInventory.Items.Count == 0)
        {
            try { cls = CoopCampaign.Instance.ServerClassFor(this); }
            catch (System.Exception e) { CoopMod.Warn("server: class lookup failed: " + e.Message); }
        }
        if (cls < 0) { orig_GuaranteeDefaultEquipment(); return; }
        var pd = C_SaveManager.m_Instance.m_PlayerData;
        int old = pd.m_WhichClass;
        pd.m_WhichClass = cls;
        try { orig_GuaranteeDefaultEquipment(); }
        finally { pd.m_WhichClass = old; }
    }
}
