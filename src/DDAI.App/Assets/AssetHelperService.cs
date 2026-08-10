namespace DDAI.App.Assets;

public sealed class AssetHelperService(string mailboxRoot, TimeProvider timeProvider)
{
    public void RunOnce()
    {
        _ = new AssetPackNormalizationService(mailboxRoot).ProcessPending();
        _ = new AssetCatalogPublicationAdviceService(mailboxRoot, timeProvider).ProcessPending();
    }
}
