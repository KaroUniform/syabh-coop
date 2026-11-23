// Патч C_InventoryFiller.DoFill: перед заполнением контейнера на сервере — таблица
// лута одиночной игры (CoopLoot). Само заполнение — как в игре (C_InventoryFiller.cs:25-32).
#pragma warning disable CS0626

class patch_C_InventoryFiller : C_InventoryFiller
{
    public extern void orig_DoFill();
    public new void DoFill()
    {
        if (UnityEngine.Network.isServer)
        {
            try { CoopLoot.Apply(m_pLootTable); }
            catch (System.Exception e) { CoopMod.Warn("loot: " + e.Message); }
        }
        orig_DoFill();
    }
}
