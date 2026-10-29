using System.ComponentModel;
using NetworkIsolator.Localization;

namespace NetworkIsolator.Core;

/// <summary>Turns cryptic Win32 errors into readable messages.</summary>
public static class ErrorTranslator
{
    public static string Describe(Exception ex)
    {
        if (ex is IsolationException) return ex.Message;

        if (ex is Win32Exception w)
        {
            return w.NativeErrorCode switch
            {
                2 => Loc.T("Err_Win2"),
                3 => Loc.T("Err_Win3"),
                5 => Loc.T("Err_Win5"),
                32 => Loc.T("Err_Win32"),
                193 => Loc.T("Err_Win193"),
                216 => Loc.T("Err_Win216"),
                267 => Loc.T("Err_Win267"),
                740 => Loc.T("Err_Win740"),
                1223 => Loc.T("Err_Win1223"),
                1260 => Loc.T("Err_Win1260"),
                _ => Loc.T("Err_WinOther", w.Message, w.NativeErrorCode)
            };
        }

        return ex.Message;
    }
}
