namespace AiGateway.Secrets;

public sealed record CiphertextRecord(string ProviderId, string ProtectorId, int KeyVersion, byte[] Ciphertext);

public interface ICiphertextStore
{
    CiphertextRecord? Find(string providerId);

    void Put(CiphertextRecord record);

    bool Remove(string providerId);
}
