using Unity.Jobs;
using UnityEngine;
using UnityEngine.Networking;

namespace AsyncTextureLoad;

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

internal class UnityWebRequestCompleteHandler(UnityWebRequest request) : ICompleteHandler
{
    public void WaitUntilComplete()
    {
        // There isn't really a good way to block on a UnityWebRequest, mostly
        // because it is something you aren't supposed to do.
        while (!request.isDone) { }
    }
}

internal class JobHandleCompleteHandler(JobHandle handle) : ICompleteHandler
{
    public void WaitUntilComplete() => handle.Complete();
}
