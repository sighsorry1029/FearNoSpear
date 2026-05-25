using UnityEngine;

namespace FearNoSpear;

internal static class SpearLocatorProtocol
{
    private const int ProtocolVersion = 2;

    internal static void WriteHeader(ZPackage package)
    {
        package.Write(ProtocolVersion);
    }

    internal static bool TryReadHeader(ZPackage package, string rpcName)
    {
        int protocol = package.ReadInt();
        if (protocol == ProtocolVersion) return true;

        FearNoSpearPlugin.Log.LogWarning($"Ignoring incompatible {rpcName} payload: protocol={protocol}; expected={ProtocolVersion}. Make sure server and client use the same FearNoSpear build.");
        return false;
    }

    internal static void WriteRecord(ZPackage package, SpearLocationRecord record)
    {
        package.Write(record.Key);
        package.Write(record.ItemKey);
        package.Write(record.Position);
        package.Write(record.Source);
        package.Write(Mathf.Max(0f, Time.time - record.LastUpdated));
    }

    internal static SpearLocationRecord ReadRecord(ZPackage package)
    {
        string key = package.ReadString();
        string itemKey = package.ReadString();
        Vector3 position = package.ReadVector3();
        string source = package.ReadString();
        float ageSeconds = package.ReadSingle();

        return new SpearLocationRecord
        {
            Key = key,
            ItemKey = itemKey,
            Position = position,
            Source = source,
            LastUpdated = Time.time - Mathf.Max(0f, ageSeconds)
        };
    }
}
