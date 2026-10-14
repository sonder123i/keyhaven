using AiGateway.Secrets;

namespace AiGateway.Security;

internal sealed class RealKeyVault(ICiphertextStore store) : IRealKeyVault
{
    public void Save(string providerId, string realKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(realKey);

        var keyVersion = (store.Find(providerId)?.KeyVersion ?? 0) + 1;
        store.Put(new CiphertextRecord(providerId, SecretProtector.ProtectorId, keyVersion, SecretProtector.Protect(providerId, realKey)));
    }

    public bool Delete(string providerId) => store.Remove(providerId);

    public bool Exists(string providerId) => store.Find(providerId) is not null;
}
