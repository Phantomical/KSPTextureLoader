#if !KSP_RETOOLED
using System;
using UnityEngine;

namespace KSPTextureLoader;

internal static class UnsupportedVersionWarning
{
    const string Message =
        ""
        + "You appear to be running KSP Retooled, but you have loaded a version "
        + "of KSPTextureLoader that was built for KSP 1.12. This should normally "
        + "be managed automatically, but requires recent versions of retooled "
        + "in order to work correctly."
        + "\n\n"
        + "Make sure you are running retooled 1.13.0-rc25 or later in order for "
        + "this version of KSPTextureLoader to load correctly.";

    public static void DisplayWarning()
    {
        try
        {
            Debug.LogError(
                "[KSPTextureLoader] This DLL was built for 1.12.5 but has been loaded on a retooled instance."
            );

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                "KSPTextureLoaderRetooledMismatch",
                "Unsupported Game Version",
                Message,
                "OK",
                true,
                HighLogic.UISkin
            );
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }
}
#endif
