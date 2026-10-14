namespace AiGateway.Secrets;

// Write-only by design: any type can resolve this from DI, so a read method would hand the plaintext to the whole process.
public interface IRealKeyVault
{
    void Save(string providerId, string realKey);

    bool Delete(string providerId);

    bool Exists(string providerId);
}
