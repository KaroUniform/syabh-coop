// Патч C_RegionBoat: после загрузки острова сообщаем кампании о лодке и её
// направлении (GetDirection: BoatN → "North" и т.д.) — для подсказки у лодки,
// выбора острова назначения и спавна у лодки прибытия. И на сервере, и у клиентов.
#pragma warning disable CS0626

class patch_C_RegionBoat : C_RegionBoat
{
    public extern void orig_EndInit(bool generating);
    public override void EndInit(bool generating)
    {
        orig_EndInit(generating);
        if (generating || CoopCampaign.Instance == null) return;
        try { CoopCampaign.Instance.RegisterBoat(this); }
        catch (System.Exception e) { CoopMod.Warn("boat register failed: " + e.Message); }
    }
}
