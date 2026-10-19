using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace OpenMessages.Windows;

public static class ClipboardHelper
{
    /// <summary>
    /// Copies <paramref name="text"/>. With <paramref name="sensitive"/>, asks Windows to keep it out of
    /// clipboard history and cloud clipboard sync.
    /// </summary>
    public static void Copy(string text, bool sensitive)
    {
        var data = new DataObject();
        data.SetText(text);
        if (sensitive)
        {
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        }
        // Another app can hold the clipboard open briefly; retry instead of failing.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
