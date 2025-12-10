using Unity.Jobs;
using UnityEngine;
using UnityEngine.Networking;

namespace KSPTextureLoader;

internal interface ICompleteHandler
{
    void WaitUntilComplete();
}

internal class AssetBundleCompleteHandler(AssetBundleCreateRequest request) : ICompleteHandler
{
    public void WaitUntilComplete() => _ = request.assetBundle;
}

internal class AssetBundleRequestCompleteHandler(AssetBundleRequest request) : ICompleteHandler
{
    public void WaitUntilComplete() => _ = request.asset;
}

internal class JobHandleCompleteHandler(JobHandle handle) : ICompleteHandler
{
    public void WaitUntilComplete() => handle.Complete();
}
