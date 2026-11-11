#if WINDOWS
using System.Drawing;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

public sealed class StaticFramePresentationTests
{
    [Fact]
    public async Task OddWidthStaticFramePreservesPixelsOnRepaintAndReload()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var view = new FolioView { Size = new Size(101, 53) };
                using var bitmap = new Bitmap(view.Width, view.Height);
                view.RenderFailed += (_, error) => throw new InvalidOperationException("Static paint failed.", error.Exception);
                view.LoadHtml("<body style='margin:0;background:#e02010'></body>");
                CheckFrame(Color.FromArgb(224, 32, 16));
                CheckFrame(Color.FromArgb(224, 32, 16));
                view.LoadHtml("<body style='margin:0;background:#1020e0'></body>");
                CheckFrame(Color.FromArgb(16, 32, 224));
                completion.SetResult();

                void CheckFrame(Color expected)
                {
                    view.DrawToBitmap(bitmap, view.ClientRectangle);
                    Assert.Equal(expected.ToArgb(), bitmap.GetPixel(20, 20).ToArgb());
                    Assert.Equal(expected.ToArgb(), bitmap.GetPixel(100, 20).ToArgb());
                }
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
#endif
