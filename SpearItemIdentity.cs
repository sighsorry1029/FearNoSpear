namespace FearNoSpear;

internal static class SpearItemIdentity
{
    internal static string BuildDropRecordKey(ItemDrop drop)
    {
        ZNetView? nview = drop.GetComponent<ZNetView>();
        string? zdoKey = TryGetZdoKey(nview);
        if (zdoKey != null && zdoKey.Length > 0) return BuildDropRecordKey(zdoKey);

        return $"drop-local:{drop.GetInstanceID()}";
    }

    internal static string BuildWorldDropRecordKey(ZDOID zdoId)
    {
        return BuildDropRecordKey(zdoId.ToString());
    }

    internal static string? TryGetZdoKey(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid()) return null;

        ZDO zdo = nview.GetZDO();
        if (zdo == null || !zdo.IsValid()) return null;

        return zdo.m_uid.ToString();
    }

    private static string BuildDropRecordKey(string zdoKey)
    {
        return $"drop:{zdoKey}";
    }

}
