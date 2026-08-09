namespace LanSwitch.Agent.Services;

public sealed class FileSaveLocationPicker
{
    public Task<string?> PickAsync(string initialDirectory, string fileName, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "选择 DeskMesh 文件保存位置",
                    InitialDirectory = initialDirectory,
                    FileName = fileName,
                    CheckPathExists = true,
                    OverwritePrompt = true,
                    RestoreDirectory = true
                };
                var selected = dialog.ShowDialog() == DialogResult.OK
                    ? FileTransferCoordinator.UniqueDestinationForPath(dialog.FileName)
                    : null;
                completion.TrySetResult(selected);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "DeskMesh 保存文件"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Once the native dialog is visible it cannot be safely cancelled from another thread. Keep the
        // server-side operation alive until the user accepts or closes it, even if the browser disconnects.
        return completion.Task;
    }
}
