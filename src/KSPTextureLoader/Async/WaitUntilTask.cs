using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader.Async;

internal class WaitUntilTask(Task task) : CustomYieldInstruction
{
    static readonly ProfilerMarker UpdateMarker = new("TextureLoader.PollTasks");

    public override bool keepWaiting
    {
        get
        {
            using var scope = UpdateMarker.Auto();
            TextureLoader.Context?.Update();
            return !task.IsCompleted;
        }
    }
}
