using Unity.Jobs;
using UnityEngine;
using UnityEngine.Networking;

namespace KSPTextureLoader;

internal interface ICompleteHandler
{
    bool IsComplete { get; }

    void WaitUntilComplete();
}

internal class AssetBundleCompleteHandler(AssetBundleCreateRequest request) : ICompleteHandler
{
    public bool IsComplete => request.isDone;

    public void WaitUntilComplete() => _ = request.assetBundle;
}

internal class AssetBundleRequestCompleteHandler(AssetBundleRequest request) : ICompleteHandler
{
    public bool IsComplete => request.isDone;

    public void WaitUntilComplete() => _ = request.asset;
}

internal class JobHandleCompleteHandler(JobHandle handle) : ICompleteHandler
{
    public bool IsComplete => handle.IsCompleted;

    public void WaitUntilComplete() => handle.Complete();
}
