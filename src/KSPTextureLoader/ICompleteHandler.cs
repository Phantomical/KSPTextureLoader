using System.Threading.Tasks;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader;

internal interface ICompleteHandler
{
    bool IsComplete { get; }

    void WaitUntilComplete();
}

internal class AssetBundleCompleteHandler(AssetBundleCreateRequest request) : ICompleteHandler
{
    public bool IsComplete => request.isDone;

    public void WaitUntilComplete()
    {
        request.priority = 100;
        _ = request.assetBundle;
    }
}

internal class AssetBundleRequestCompleteHandler(AssetBundleRequest request) : ICompleteHandler
{
    public bool IsComplete => request.isDone;

    public void WaitUntilComplete()
    {
        request.priority = 100;
        _ = request.asset;
    }
}

internal class JobHandleCompleteHandler(JobHandle handle) : ICompleteHandler
{
    public bool IsComplete => handle.IsCompleted;

    public void WaitUntilComplete()
    {
        try
        {
            handle.Complete();
        }
        catch
        {
            // This will be handled by the coroutine getting the task result.
        }
    }
}

internal class TaskCompleteHandler(Task task) : ICompleteHandler
{
    public bool IsComplete => task.IsCompleted;

    public void WaitUntilComplete()
    {
        TextureLoader.Context.WaitUntilComplete(task);
    }
}
