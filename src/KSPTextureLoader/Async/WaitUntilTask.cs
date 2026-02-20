using System.Threading.Tasks;
using UnityEngine;

namespace KSPTextureLoader.Async;

internal class WaitUntilTask(Task task) : CustomYieldInstruction
{
    public override bool keepWaiting
    {
        get
        {
            TextureLoader.Context?.Update();
            return !task.IsCompleted;
        }
    }
}
